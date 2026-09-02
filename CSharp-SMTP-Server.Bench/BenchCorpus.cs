using System.Text;

namespace CSharp_SMTP_Server.Bench;

/// <summary>
/// Pre-framed message payloads. Every sample is converted to its exact on-the-wire byte form once,
/// at startup, so the timed region contains no string splitting, encoding, or allocation.
/// </summary>
/// <remarks>
/// <para>
/// This is the first correction over the in-test harness. That one holds each sample as a
/// <see cref="string"/> and, inside the measured loop, splits it on CRLF and issues one flushed
/// <c>StreamWriter</c> write per line — thousands of syscall-bearing round trips per message. That
/// cost is client-side, but it lands inside the same wall clock the server is judged by, and it
/// scales with core count, so it contaminates any scaling measurement.
/// </para>
/// <para>
/// Here the DATA block — id header, body, terminating dot — is one contiguous <see cref="byte"/>
/// array written with a single socket write.
/// </para>
/// </remarks>
internal static class BenchCorpus
{
    internal const string IdHeader = "X-Load-Id";

    /// <summary>A message whose wire bytes are precomputed per id.</summary>
    internal sealed class Sample
    {
        internal string Name { get; }

        /// <summary>Body as sent, excluding the id header line and the terminating dot.</summary>
        private readonly byte[] _body;

        /// <summary>Payload size in bytes, matching what the in-test harness reports.</summary>
        internal int Bytes => _body.Length;

        internal Sample(string name, string payload)
        {
            Name = name;
            _body = Encoding.UTF8.GetBytes(payload + "\r\n");

            // Pre-framing is only safe if no body line begins with '.', which would require
            // dot-stuffing. The generated corpus never does; assert rather than assume, because a
            // silent stuffing error would corrupt every message and read as a server defect.
            if (StartsAnyLineWithDot(_body))
                throw new InvalidOperationException($"sample '{name}' contains a line starting with '.', which requires dot-stuffing");
        }

        /// <summary>
        /// Builds the complete DATA payload for one message id: id header, body, terminating
        /// <c>.</c> line. One array, one write.
        /// </summary>
        internal byte[] WireBytes(string id)
        {
            var header = Encoding.ASCII.GetBytes($"{IdHeader}: {id}\r\n");
            var terminator = "."u8.ToArray();

            var buffer = new byte[header.Length + _body.Length + terminator.Length + 2];
            var offset = 0;

            header.CopyTo(buffer, offset);
            offset += header.Length;
            _body.CopyTo(buffer, offset);
            offset += _body.Length;
            terminator.CopyTo(buffer, offset);
            offset += terminator.Length;
            buffer[offset++] = (byte)'\r';
            buffer[offset] = (byte)'\n';

            return buffer;
        }

        private static bool StartsAnyLineWithDot(byte[] body)
        {
            if (body.Length > 0 && body[0] == (byte)'.') return true;

            for (var i = 0; i + 2 < body.Length; i++)
                if (body[i] == (byte)'\r' && body[i + 1] == (byte)'\n' && body[i + 2] == (byte)'.')
                    return true;

            return false;
        }
    }

    /// <summary>
    /// The same three sizes the in-test corpus uses, so byte volumes stay comparable across
    /// harnesses. Unicode content is retained: it is what exercises the UTF-8 decode path.
    /// </summary>
    internal static readonly IReadOnlyList<Sample> Samples = new[]
    {
        new Sample("ascii-100kb", BuildAsciiBody("ascii-100kb", 100 * 1024)),
        new Sample("unicode-200kb", BuildUnicodeBody("unicode-200kb", 200 * 1024)),
        new Sample("ascii-1000kb", BuildAsciiBody("ascii-1000kb", 1000 * 1024)),
    };

    private static StringBuilder StartMessage(string name, string charsetNote)
    {
        var sb = new StringBuilder();
        sb.Append($"Subject: Bench sample: {name}\r\n");
        sb.Append("From: Bench Sender <load@example.com>\r\n");
        sb.Append("To: Bench Recipient <sink@example.org>\r\n");
        sb.Append($"Content-Type: text/plain; charset={charsetNote}\r\n");
        sb.Append("\r\n");
        return sb;
    }

    private static string BuildAsciiBody(string name, int targetBytes)
    {
        var sb = StartMessage(name, "us-ascii");
        for (var i = 0; sb.Length < targetBytes; i++)
            sb.Append($"line {i:D6} {name} the quick brown fox jumps over the lazy dog 0123456789\r\n");
        return Trim(sb, targetBytes);
    }

    private static string BuildUnicodeBody(string name, int targetBytes)
    {
        var sb = StartMessage(name, "utf-8");
        for (var i = 0; Encoding.UTF8.GetByteCount(sb.ToString()) < targetBytes; i++)
            sb.Append($"linia {i:D6} {name} zażółć gęślą jaźń · Ελληνικά · 日本語テキスト · 🚀🔥\r\n");
        return TrimUtf8(sb, targetBytes);
    }

    private static string Trim(StringBuilder sb, int targetBytes)
    {
        var text = sb.ToString();
        var cut = text.LastIndexOf("\r\n", Math.Min(targetBytes, text.Length - 1), StringComparison.Ordinal);
        return cut <= 0 ? text : text[..cut];
    }

    private static string TrimUtf8(StringBuilder sb, int targetBytes)
    {
        var text = sb.ToString();
        var cut = text.Length;
        while (cut > 0)
        {
            cut = text.LastIndexOf("\r\n", cut - 1, StringComparison.Ordinal);
            if (cut <= 0) return text;
            if (Encoding.UTF8.GetByteCount(text[..cut]) <= targetBytes) return text[..cut];
        }
        return text;
    }
}
