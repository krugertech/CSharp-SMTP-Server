using System.Security.Cryptography;
using System.Text;

namespace CSharp_SMTP_Server.Tests.Load;

/// <summary>
/// The sample messages sent by the load tests, each with a SHA-256 digest of its canonical payload.
/// </summary>
/// <remarks>
/// <para>
/// Integrity checking here is deliberately NOT a hash of the bytes on the wire, because the server
/// legitimately rewrites every message before delivery:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>TransactionCommands.ProcessData</c> prepends a <c>Received:</c> header containing
/// <c>DateTime.UtcNow</c>, so the delivered body differs on every single message even for identical
/// input. <c>Authentication-Results</c> may be prepended too.
/// </description></item>
/// <item><description>
/// The DATA capture uses <c>StringBuilder.AppendLine</c>, which emits <see cref="Environment.NewLine"/>
/// — CRLF on Windows, LF on Linux. A byte-exact comparison would pass locally and fail on Linux CI for
/// reasons unrelated to the server's correctness.
/// </description></item>
/// </list>
/// <para>
/// So the contract verified is: <em>the payload the client sent survives transport unaltered, modulo
/// server-prepended headers and line-ending normalization</em>. <see cref="Canonicalize"/> defines
/// that normalization and <see cref="ExtractPayload"/> strips the prepended headers.
/// </para>
/// <para>
/// No payload line begins with '.'. The server now unstuffs correctly (historical Q1, fixed), but a
/// leading-dot line still has a wire form that differs from its stored form, and these samples are
/// hashed as written rather than as framed — so keeping them dot-free means a corpus hash needs no
/// knowledge of transparency encoding. Unstuffing has its own dedicated coverage; see
/// <c>DotStuffing_IsUnstuffed_Q1Fixed</c> here and <c>DotStuffing_IsUnstuffed_BodyLinesStoredAsComposed</c>
/// in <c>DataAndMessageTests</c>.
/// </para>
/// </remarks>
internal static class MessageCorpus
{
    /// <summary>Marker header carrying the per-send unique id, used to pair sends with deliveries.</summary>
    internal const string IdHeader = "X-Load-Id";

    /// <summary>A single sample message: a fixed payload plus the digest of its canonical form.</summary>
    internal sealed record Sample(string Name, string Payload)
    {
        /// <summary>SHA-256 of the canonicalized payload, in lowercase hex.</summary>
        internal string Sha256 { get; } = Hash(Canonicalize(Payload));

        /// <summary>Payload size in UTF-8 bytes as sent — the basis for byte-throughput reporting.</summary>
        internal int Bytes { get; } = Encoding.UTF8.GetByteCount(Payload);
    }

    /// <summary>
    /// Three messages sized to represent real mail with attachments: 100 KB, 200 KB and 1000 KB.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sizes are payload bytes as sent, so a run's byte volume is predictable: one message from each
    /// sample is ~1.3 MB. All three sit well under the 10 MB default
    /// <see cref="ServerOptions.MessageCharactersLimit"/>, so none is rejected for size — the limit
    /// counts characters excluding CRLF, and the largest sample is ~10% of it.
    /// </para>
    /// <para>
    /// The 1000 KB sample is the one that matters most structurally: it spans a large number of socket
    /// reads and forces the DATA accumulation path to grow its <c>StringBuilder</c> repeatedly, which
    /// is where truncation, buffer-boundary and reordering bugs would surface.
    /// </para>
    /// <para>
    /// The <c>unicode-200kb</c> sample carries genuine multi-byte UTF-8 (Polish, Greek, CJK, emoji),
    /// so its byte count deliberately exceeds its character count — that is what exercises the UTF-8
    /// decode path rather than merely claiming to.
    /// </para>
    /// </remarks>
    internal static readonly IReadOnlyList<Sample> Samples = new[]
    {
        new Sample("ascii-100kb", BuildAsciiBody("ascii-100kb", 100 * 1024)),
        new Sample("unicode-200kb", BuildUnicodeBody("unicode-200kb", 200 * 1024)),
        new Sample("ascii-1000kb", BuildAsciiBody("ascii-1000kb", 1000 * 1024)),
    };

    /// <summary>Standard corpus headers, ending with the blank line that separates them from the body.</summary>
    private static StringBuilder StartMessage(string name, string charsetNote)
    {
        var sb = new StringBuilder();
        sb.Append($"Subject: Load sample: {name}\r\n");
        sb.Append("From: Load Sender <load@example.com>\r\n");
        sb.Append("To: Load Recipient <sink@example.org>\r\n");
        sb.Append($"Content-Type: text/plain; charset={charsetNote}\r\n");
        sb.Append("\r\n");
        return sb;
    }

    /// <summary>
    /// Builds an ASCII body of approximately <paramref name="targetBytes"/>, with a distinct
    /// counter on every line so truncation or line reordering cannot produce a hash collision.
    /// </summary>
    private static string BuildAsciiBody(string name, int targetBytes)
    {
        var sb = StartMessage(name, "us-ascii");

        for (var i = 0; sb.Length < targetBytes; i++)
            sb.Append($"Line {i:D6}: the quick brown fox jumps over the lazy dog 0123456789 abcdefghijklmnopqrstuvwxyz\r\n");

        return Trim(sb, targetBytes);
    }

    /// <summary>
    /// Builds a body of approximately <paramref name="targetBytes"/> containing real multi-byte
    /// UTF-8, so the size is measured in bytes rather than characters.
    /// </summary>
    private static string BuildUnicodeBody(string name, int targetBytes)
    {
        var sb = StartMessage(name, "utf-8");

        // Each line mixes 2-byte (Polish/Greek), 3-byte (CJK) and 4-byte (emoji) sequences with ASCII,
        // so every multi-byte width crosses the reader's buffer boundaries somewhere in a 200 KB body.
        for (var i = 0; Encoding.UTF8.GetByteCount(sb.ToString()) < targetBytes; i++)
            sb.Append($"Wiersz {i:D6}: zażółć gęślą jaźń · αβγδε ζητο · 東西南北 一二三四 · 📧🚀✅ · ASCII tail\r\n");

        return TrimUtf8(sb, targetBytes);
    }

    /// <summary>
    /// Trims to whole lines at or below <paramref name="targetBytes"/> (ASCII: 1 char == 1 byte),
    /// dropping the final CRLF so the payload carries no trailing blank line.
    /// </summary>
    private static string Trim(StringBuilder sb, int targetBytes)
    {
        var text = sb.ToString();
        var cut = text.LastIndexOf("\r\n", Math.Min(targetBytes, text.Length - 1), StringComparison.Ordinal);
        return cut > 0 ? text[..cut] : text;
    }

    /// <summary>Byte-aware counterpart to <see cref="Trim"/> for bodies containing multi-byte UTF-8.</summary>
    private static string TrimUtf8(StringBuilder sb, int targetBytes)
    {
        var text = sb.ToString();

        // Walk back whole lines until the encoded size fits — cutting on a line boundary also
        // guarantees the cut never lands inside a multi-byte sequence.
        var cut = text.Length;
        while (cut > 0)
        {
            cut = text.LastIndexOf("\r\n", cut - 1, StringComparison.Ordinal);
            if (cut <= 0) break;
            if (Encoding.UTF8.GetByteCount(text[..cut]) <= targetBytes) return text[..cut];
        }

        return text;
    }

    /// <summary>
    /// Normalizes a message for hashing: CRLF/CR to LF, and strip trailing blank lines.
    /// </summary>
    /// <remarks>
    /// Line-ending normalization is required because the server re-joins captured lines with
    /// <see cref="Environment.NewLine"/>. Trailing-blank-line trimming absorbs the empty final line
    /// that <c>AppendLine</c> leaves after the last body line. Neither weakens the check meaningfully:
    /// any content change, reordering, truncation or cross-talk still alters the digest.
    /// </remarks>
    internal static string Canonicalize(string message) =>
        message.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');

    /// <summary>SHA-256 of a string's UTF-8 bytes, as lowercase hex.</summary>
    internal static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>
    /// Recovers the client-sent payload from a delivered <c>RawBody</c> by dropping the headers the
    /// server prepended, then canonicalizing.
    /// </summary>
    /// <remarks>
    /// The server prepends whole headers (<c>Received</c>, optionally <c>Authentication-Results</c>)
    /// to the front of the raw body, so the original payload starts at the first line that begins one
    /// of the corpus's own headers. Anchoring on <c>Subject:</c> is exact for this corpus: every
    /// sample starts with it, and no server-prepended header does.
    /// </remarks>
    internal static string ExtractPayload(string rawBody)
    {
        var normalized = Canonicalize(rawBody);
        var index = normalized.IndexOf("Subject:", StringComparison.Ordinal);
        return index < 0 ? normalized : normalized[index..];
    }

    /// <summary>
    /// Reads the <see cref="IdHeader"/> value the sender stamped on a message, or null if absent.
    /// </summary>
    internal static string? ExtractId(string rawBody)
    {
        foreach (var line in Canonicalize(rawBody).Split('\n'))
        {
            if (line.StartsWith(IdHeader + ":", StringComparison.OrdinalIgnoreCase))
                return line[(IdHeader.Length + 1)..].Trim();
        }

        return null;
    }
}
