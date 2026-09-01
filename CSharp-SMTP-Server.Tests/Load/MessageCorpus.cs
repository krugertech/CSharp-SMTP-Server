using System.Security.Cryptography;
using System.Text;

namespace CSharp_SMTP_Server.Tests.Load;

/// <summary>
/// The sample messages sent by the load tests, hashed as exact octets.
/// </summary>
/// <remarks>
/// <para>
/// The server prepends headers — <c>Received:</c> always, carrying a <c>DateTime.UtcNow</c> that
/// differs on every message, and <c>Authentication-Results:</c> when validation is enabled — so the
/// delivered message is never byte-identical to what was sent. Everything from the client's own
/// first header onward is, and that is what these hashes cover:
/// <see cref="ExtractPayloadBytes"/> anchors on the id header line and hashes from there to the end.
/// </para>
/// <para>
/// <b>This was previously a hash of a canonicalized string, and that was too weak.</b> The old
/// <c>Canonicalize</c> mapped CRLF to LF and trimmed trailing newlines before hashing, justified by
/// the DATA path using <c>StringBuilder.AppendLine</c> and so emitting <see cref="Environment.NewLine"/>
/// — bare LF on Linux. That justification is stale: the DATA path now writes through
/// <c>MessageBody.WriteLine</c>, which appends an explicit CRLF on every platform. Normalizing line
/// endings before hashing therefore erased a difference the server no longer produces, which meant a
/// CRLF regression under load could not fail these tests. The hash is now over raw bytes, so it can.
/// </para>
/// <para>
/// The old anchor was equally loose. It searched for the substring <c>"Subject:"</c>, which is not
/// line-anchored and discarded every byte before it — including the <see cref="IdHeader"/> line the
/// sender stamped. Anchoring on the exact id header line instead keeps every client header inside
/// the hashed range, so corruption of <c>Subject</c>, <c>From</c> or <c>To</c> is now caught rather
/// than skipped over.
/// </para>
/// <para>
/// No payload line begins with '.'. The server unstuffs correctly (historical Q1, fixed), but a
/// leading-dot line has a wire form that differs from its stored form, and these samples are hashed
/// as composed rather than as framed — so keeping them dot-free means the corpus needs no knowledge
/// of transparency encoding. Unstuffing has dedicated coverage: <c>DotStuffing_IsUnstuffed_Q1Fixed</c>
/// here, <c>DotStuffing_IsUnstuffed_BodyLinesStoredAsComposed</c> in <c>DataAndMessageTests</c>, and
/// <c>LeadingDotLines_RoundTripThroughTransparency</c> in the byte-integrity suite.
/// </para>
/// </remarks>
internal static class MessageCorpus
{
    /// <summary>Marker header carrying the per-send unique id, used to pair sends with deliveries.</summary>
    internal const string IdHeader = "X-Load-Id";

    /// <summary>A single sample message: a fixed payload, hashed per-send as exact octets.</summary>
    /// <remarks>
    /// The digest depends on the send id, because the id header is part of the transmitted message
    /// and therefore part of the hashed range — so it is computed by <see cref="ExpectedSha256"/> per
    /// send rather than stored once on the sample.
    /// </remarks>
    internal sealed record Sample(string Name, string Payload)
    {
        /// <summary>Payload size in UTF-8 bytes as sent — the basis for byte-throughput reporting.</summary>
        internal int Bytes { get; } = Encoding.UTF8.GetByteCount(Payload);

        /// <summary>
        /// The exact octets this sample produces on the wire for a given send id, headers included.
        /// </summary>
        /// <remarks>
        /// Must match what <c>LoadDriver.SendOneAsync</c> transmits, byte for byte: the id header
        /// line, the payload, and the CRLF that terminates the payload's final line. The driver sends
        /// the payload line by line and each <c>SmtpSession.Send</c> appends a CRLF, so the final
        /// line gets one even though <see cref="Trim"/> stripped it from the stored payload.
        /// </remarks>
        internal byte[] ExpectedBytes(string id) =>
            Encoding.UTF8.GetBytes($"{IdHeader}: {id}\r\n{Payload}\r\n");

        /// <summary>SHA-256 of <see cref="ExpectedBytes"/>, in lowercase hex.</summary>
        internal string ExpectedSha256(string id) => Hash(ExpectedBytes(id));
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

    /// <summary>SHA-256 of the given octets, as lowercase hex.</summary>
    internal static string Hash(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>
    /// Recovers the client-sent octets from a delivered message by locating the id header line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Anchored on the exact line <c>X-Load-Id: &lt;id&gt;CRLF</c>. The server prepends its headers
    /// into the same header block as the client's, so there is no structural boundary that separates
    /// them — splitting at the first blank line would hash only the body and would miss corruption of
    /// <c>Subject</c>, <c>From</c> or <c>To</c> entirely. The id line is the first thing the sender
    /// transmits, so anchoring there keeps every client header inside the hashed range.
    /// </para>
    /// <para>
    /// Returns null when the anchor is absent, which the caller reports as an unidentified delivery
    /// rather than as a corruption.
    /// </para>
    /// </remarks>
    internal static byte[]? ExtractPayloadBytes(byte[] delivered, string id)
    {
        var anchor = Encoding.ASCII.GetBytes($"{IdHeader}: {id}\r\n");
        var index = IndexOfLineAnchored(delivered, anchor);

        if (index < 0) return null;

        var result = new byte[delivered.Length - index];
        Buffer.BlockCopy(delivered, index, result, 0, result.Length);

        return result;
    }

    /// <summary>Finds <paramref name="needle"/> where it begins a line (start of message, or after LF).</summary>
    private static int IndexOfLineAnchored(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (i > 0 && haystack[i - 1] != (byte)'\n') continue;

            var match = true;

            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] == needle[j]) continue;
                match = false;
                break;
            }

            if (match) return i;
        }

        return -1;
    }

    /// <summary>
    /// Reads the <see cref="IdHeader"/> value the sender stamped on a message, or null if absent.
    /// </summary>
    /// <remarks>
    /// Decodes only enough of the message to find the header line. The id is ASCII by construction,
    /// so decoding it is safe; the payload it identifies is never decoded.
    /// </remarks>
    internal static string? ExtractId(byte[] delivered)
    {
        var prefixLength = Math.Min(delivered.Length, 4096);
        var prefix = Encoding.ASCII.GetString(delivered, 0, prefixLength);

        foreach (var line in prefix.Split('\n'))
        {
            if (line.StartsWith(IdHeader + ":", StringComparison.OrdinalIgnoreCase))
                return line[(IdHeader.Length + 1)..].Trim();
        }

        return null;
    }
}
