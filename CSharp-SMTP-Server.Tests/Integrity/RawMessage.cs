using System.Security.Cryptography;
using System.Text;

namespace CSharp_SMTP_Server.Tests.Integrity;

/// <summary>
/// Byte-exact SMTP transmission and verification: the primary chain-of-custody oracle.
/// </summary>
/// <remarks>
/// <para>
/// The integrity claim this server makes is that the octets a sender put in DATA are the octets
/// handed to <see cref="Interfaces.IMailDelivery"/>. Testing that requires comparing bytes to bytes,
/// with no string round trip anywhere in the harness — the moment expected or actual passes through
/// a .NET string, every byte that is not valid UTF-8 becomes U+FFFD and the comparison starts
/// agreeing with a corruption instead of catching it.
/// </para>
/// <para>
/// This is why the harness cannot use <see cref="SmtpSession.Send"/> for message content:
/// <c>Send</c> encodes a string as UTF-8 and appends CRLF, so it can neither express a Latin-1 body
/// nor preserve a line the test means to send byte-for-byte. <see cref="SmtpSession.SendRaw"/>
/// transmits bytes but applies no SMTP transparency, so a body line beginning with '.' would be read
/// by the server as the terminating dot. <see cref="SendDataAsync"/> closes that gap: it dot-stuffs
/// at byte-defined line starts and appends the terminator itself, so what arrives is exactly the
/// caller's bytes with only the transport framing the RFC requires.
/// </para>
/// <para>
/// The expected value is always the byte array the test composed, never a re-serialization of it.
/// A test that re-serializes to build its expectation is testing the serializer.
/// </para>
/// </remarks>
internal static class RawMessage
{
    /// <summary>Marker header carrying a per-send unique id, used to anchor the delivered payload.</summary>
    internal const string IdHeader = "X-Integrity-Id";

    private static readonly byte[] Crlf = { (byte)'\r', (byte)'\n' };

    /// <summary>
    /// Sends <paramref name="message"/> as DATA content, applying RFC 5321 §4.5.2 transparency and
    /// the terminating dot, then reads the server's response to the terminator.
    /// </summary>
    /// <remarks>
    /// The caller's bytes are treated as an opaque octet stream: lines are split on LF at byte level
    /// and a leading '.' is doubled, which is the only transformation a conforming client applies.
    /// Nothing is decoded, so a body in any charset — or none — survives the harness unchanged.
    /// </remarks>
    /// <param name="session">An open session already past DATA's 354 response.</param>
    /// <param name="message">The exact message octets to transmit, headers included.</param>
    /// <returns>The server's response line to the terminating dot.</returns>
    internal static async Task<string?> SendDataAsync(SmtpSession session, byte[] message)
    {
        await session.SendRaw(Stuff(message));
        return await session.ReadLineAsync();
    }

    /// <summary>
    /// Applies SMTP transparency to a message and appends the end-of-data terminator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Split on LF rather than on CRLF so that a test deliberately sending bare-LF input still has
    /// each of its lines stuffed correctly; the CR, where present, stays attached to the end of the
    /// line and is transmitted untouched.
    /// </para>
    /// <para>
    /// The terminator is preceded by a CRLF only when the message does not already end with one, so
    /// a caller that ends its message without a trailing terminator gets exactly the wire form it
    /// asked for rather than a silently repaired one.
    /// </para>
    /// </remarks>
    internal static byte[] Stuff(byte[] message)
    {
        var output = new MemoryStream();
        var lineStart = 0;

        for (var i = 0; i <= message.Length; i++)
        {
            var atEnd = i == message.Length;

            if (!atEnd && message[i] != (byte)'\n') continue;
            if (atEnd && lineStart >= message.Length) break;

            // A line that begins with '.' gets a second one; the receiver strips it back off.
            if (message[lineStart] == (byte)'.')
                output.WriteByte((byte)'.');

            var length = (atEnd ? message.Length : i + 1) - lineStart;
            output.Write(message, lineStart, length);
            lineStart = i + 1;
        }

        var body = output.ToArray();

        var result = new MemoryStream();
        result.Write(body, 0, body.Length);

        // Only add a terminating CRLF when the message did not supply one.
        if (body.Length < 2 || body[^2] != (byte)'\r' || body[^1] != (byte)'\n')
            result.Write(Crlf, 0, Crlf.Length);

        result.WriteByte((byte)'.');
        result.Write(Crlf, 0, Crlf.Length);

        return result.ToArray();
    }

    /// <summary>
    /// Recovers the client-sent octets from a delivered message by locating the id header line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Anchoring on the exact line <c>X-Integrity-Id: &lt;id&gt;CRLF</c> rather than on the
    /// header/body boundary is deliberate and load-bearing. The server prepends <c>Received:</c> and
    /// <c>Authentication-Results:</c> into the SAME header block as the client's own headers, so
    /// splitting at the first blank line would hash only the MIME body and would not notice
    /// corruption of <c>Subject</c>, <c>From</c>, <c>To</c> or a <c>DKIM-Signature</c>. Anchoring on
    /// the id line keeps every client header inside the compared range.
    /// </para>
    /// <para>
    /// The match is line-anchored — the id header must start a line — so a message whose body merely
    /// mentions the header name cannot shift the anchor.
    /// </para>
    /// </remarks>
    /// <param name="delivered">The full delivered message octets, server headers included.</param>
    /// <param name="id">The id stamped on this send.</param>
    /// <returns>The octets from the id header line to the end of the message.</returns>
    internal static byte[] ExtractFromId(byte[] delivered, string id)
    {
        var anchor = Encoding.ASCII.GetBytes($"{IdHeader}: {id}\r\n");
        var index = IndexOfLineAnchored(delivered, anchor);

        if (index < 0)
            throw new InvalidOperationException(
                $"delivered message does not contain the anchor line '{IdHeader}: {id}'");

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
    /// Asserts two byte arrays are identical, reporting the first differing offset in a readable form.
    /// </summary>
    /// <remarks>
    /// A bare <c>Assert.Equal</c> on two multi-kilobyte arrays reports only that they differ, which
    /// for an integrity failure is the least useful thing it could say — the offset and the bytes on
    /// each side are what identify the transformation responsible.
    /// </remarks>
    internal static void AssertBytesEqual(byte[] expected, byte[] actual, string what)
    {
        if (expected.Length == actual.Length && expected.AsSpan().SequenceEqual(actual))
            return;

        var limit = Math.Min(expected.Length, actual.Length);
        var offset = 0;

        while (offset < limit && expected[offset] == actual[offset])
            offset++;

        var detail = offset == limit
            ? $"identical for the first {limit} bytes, then lengths differ"
            : $"first difference at byte {offset}: expected 0x{expected[offset]:X2}, got 0x{actual[offset]:X2}";

        Assert.Fail(
            $"{what}: delivered octets differ from what was sent.\n" +
            $"  expected {expected.Length} bytes, got {actual.Length} bytes\n" +
            $"  {detail}\n" +
            $"  expected around it: {Describe(expected, offset)}\n" +
            $"  actual around it:   {Describe(actual, offset)}");
    }

    /// <summary>Renders the bytes surrounding an offset as escaped ASCII, for a failure message.</summary>
    private static string Describe(byte[] data, int offset)
    {
        var start = Math.Max(0, offset - 16);
        var end = Math.Min(data.Length, offset + 16);
        var sb = new StringBuilder();

        for (var i = start; i < end; i++)
        {
            var b = data[i];

            sb.Append(b switch
            {
                (byte)'\r' => "\\r",
                (byte)'\n' => "\\n",
                (byte)'\t' => "\\t",
                >= 0x20 and < 0x7F => ((char)b).ToString(),
                _ => $"\\x{b:X2}"
            });
        }

        return sb.ToString();
    }

    /// <summary>SHA-256 of the given octets, as lowercase hex.</summary>
    internal static string Hash(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
