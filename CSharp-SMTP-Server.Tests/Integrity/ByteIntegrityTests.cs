using System.Text;

namespace CSharp_SMTP_Server.Tests.Integrity;

/// <summary>
/// Byte-exact chain-of-custody tests: the octets accepted in DATA are the octets delivered.
/// </summary>
/// <remarks>
/// <para>
/// This is the primary integrity oracle. Every test here composes an exact byte array, transmits it
/// with only the transport framing RFC 5321 §4.5.2 requires, and compares the delivered octets
/// against the array it composed — no string round trip on either side, so nothing in the harness
/// can launder a corruption into agreement.
/// </para>
/// <para>
/// The contract asserted is narrow and deliberate:
/// </para>
/// <list type="number">
/// <item><description>SMTP transparency is reversed: a doubled leading dot is stored as one.</description></item>
/// <item><description>Every other octet is stored exactly as it arrived, in any charset or none.</description></item>
/// <item><description>Server headers are PREPENDED, so the client's message is a contiguous suffix.</description></item>
/// <item><description>Conforming CRLF line endings are preserved exactly, on every platform.</description></item>
/// </list>
/// <para>
/// Point 4 is the one the load corpus could not see. <c>MessageCorpus.Canonicalize</c> maps CRLF to
/// LF before hashing, on a rationale that is now stale — it cites <c>StringBuilder.AppendLine</c>
/// emitting <c>Environment.NewLine</c>, but the DATA path has since moved to
/// <c>MessageBody.WriteLine</c>, which writes an explicit CRLF. Those load assertions therefore
/// normalize away a difference the server no longer produces, and a CRLF regression would not fail
/// them. These tests compare the raw octets instead.
/// </para>
/// </remarks>
[Trait("Category", "Integrity")]
public sealed class ByteIntegrityTests
{
    /// <summary>
    /// Runs one message through a real listener and returns the delivered octets from the id anchor on.
    /// </summary>
    /// <remarks>
    /// The id header is stamped by the caller inside <paramref name="message"/> so that it is part of
    /// the signed/compared range rather than something the harness adds afterwards.
    /// </remarks>
    private static async Task<byte[]> RoundTripAsync(byte[] message, string id)
    {
        var delivery = new ByteRecordingDelivery();
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port, delivery: delivery);
        server.Start();

        await using (var session = await SmtpSession.ConnectAsync(port))
        {
            Assert.StartsWith("220 ", await session.ReadLineAsync());
            await session.Send("EHLO integrity.client");
            await session.ReadResponseAsync();

            await session.Send("MAIL FROM:<sender@example.com>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("RCPT TO:<archive@example.org>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("DATA");
            Assert.StartsWith("354", await session.ReadLineAsync());

            var response = await RawMessage.SendDataAsync(session, message);
            Assert.StartsWith("250", response);
        }

        return RawMessage.ExtractFromId(delivery.Single(), id);
    }

    /// <summary>Builds a message with the id anchor as its first header, from exact octets.</summary>
    private static byte[] Compose(string id, params byte[][] parts)
    {
        var output = new MemoryStream();
        var header = Encoding.ASCII.GetBytes(
            $"{RawMessage.IdHeader}: {id}\r\nSubject: integrity {id}\r\n" +
            "From: Sender <sender@example.com>\r\nTo: Archive <archive@example.org>\r\n\r\n");

        output.Write(header, 0, header.Length);

        foreach (var part in parts)
            output.Write(part, 0, part.Length);

        return output.ToArray();
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    // ── the baseline contract ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// An ordinary message survives byte-for-byte, and the client's octets are a contiguous suffix of
    /// what is delivered.
    /// </summary>
    /// <remarks>
    /// The suffix property is what makes every other test in this file meaningful: it establishes
    /// that the server only ever prepends, so anchoring on the id line recovers the client's message
    /// exactly rather than approximately.
    /// </remarks>
    [Fact]
    public async Task OrdinaryMessage_DeliveredByteExact()
    {
        const string id = "baseline";
        var sent = Compose(id, Ascii("Line one.\r\nLine two.\r\nLine three.\r\n"));

        var delivered = await RoundTripAsync(sent, id);

        RawMessage.AssertBytesEqual(sent, delivered, "ordinary message");
    }

    /// <summary>
    /// CRLF line endings are preserved as CRLF, on every platform.
    /// </summary>
    /// <remarks>
    /// The regression guard for the defect <c>MessageBody.WriteLine</c> was written to fix: the old
    /// <c>StringBuilder.AppendLine</c> path emitted <c>Environment.NewLine</c>, so the stored message
    /// had bare-LF endings on Linux — wrong for SMTP, and a silent difference between what the same
    /// server produced on Windows and in a container. Asserted on raw octets because that is the only
    /// way to see it; the load corpus normalizes CRLF to LF before hashing and cannot.
    /// </remarks>
    [Fact]
    public async Task CrlfLineEndings_ArePreservedExactly()
    {
        const string id = "crlf";
        var sent = Compose(id, Ascii("alpha\r\nbeta\r\ngamma\r\n"));

        var delivered = await RoundTripAsync(sent, id);

        RawMessage.AssertBytesEqual(sent, delivered, "CRLF preservation");
        Assert.DoesNotContain(LoneLfIndices(delivered), i => i > 0 && delivered[i - 1] != (byte)'\r');
    }

    /// <summary>Indices of every LF byte in a buffer.</summary>
    private static IEnumerable<int> LoneLfIndices(byte[] data)
    {
        for (var i = 0; i < data.Length; i++)
            if (data[i] == (byte)'\n')
                yield return i;
    }

    // ── the fixtures the review identified as uncovered ───────────────────────────────────────

    /// <summary>
    /// A folded header is delivered with its folding intact — continuation lines and all.
    /// </summary>
    /// <remarks>
    /// RFC 5322 §2.2.3 folding is semantically insignificant whitespace, which is exactly why it is
    /// worth pinning: anything that "helpfully" unfolds or re-folds a header changes the octets
    /// without changing the meaning, and a signature over those headers would no longer verify.
    /// Exchange emits long folded headers routinely, so this is a real shape for a journaling relay.
    /// </remarks>
    [Fact]
    public async Task FoldedHeaders_ArePreservedWithTheirFolding()
    {
        const string id = "folded";

        // Folded with both SP and HTAB continuations, since either is legal and they are distinct bytes.
        var sent = Ascii(
            $"{RawMessage.IdHeader}: {id}\r\n" +
            "Subject: a subject that is folded\r\n across two lines\r\n" +
            "X-Long-Header: first\r\n\tsecond-tab-folded\r\n   third-space-folded\r\n" +
            "From: Sender <sender@example.com>\r\nTo: Archive <archive@example.org>\r\n" +
            "\r\nbody\r\n");

        var delivered = await RoundTripAsync(sent, id);

        RawMessage.AssertBytesEqual(sent, delivered, "folded headers");
    }

    /// <summary>
    /// Trailing space and horizontal tab at end of line survive delivery.
    /// </summary>
    /// <remarks>
    /// The most easily lost bytes in the whole message: trailing whitespace is invisible, and any
    /// trim anywhere in the path removes it silently. DKIM's <c>relaxed</c> body canonicalization
    /// strips it before hashing, so a DKIM test cannot detect its loss — this is precisely the class
    /// of corruption only a raw-byte comparison catches.
    /// </remarks>
    [Fact]
    public async Task TrailingWhitespace_IsPreserved()
    {
        const string id = "trailing-ws";
        var sent = Compose(id, Ascii("trailing space   \r\ntrailing tab\t\t\r\nmixed \t \r\nclean\r\n"));

        var delivered = await RoundTripAsync(sent, id);

        RawMessage.AssertBytesEqual(sent, delivered, "trailing whitespace");
    }

    /// <summary>
    /// Multiple blank lines at the end of the body are preserved, not collapsed or trimmed.
    /// </summary>
    /// <remarks>
    /// Both DKIM body canonicalizations — <c>simple</c> included — reduce trailing empty lines to a
    /// single CRLF before hashing, so a signature verifies whether or not they survived. Only a raw
    /// comparison distinguishes "we kept the message" from "we kept enough of it to still verify".
    /// </remarks>
    [Fact]
    public async Task TerminalBlankLines_ArePreserved()
    {
        const string id = "blank-tail";
        var sent = Compose(id, Ascii("content\r\n\r\n\r\n\r\n"));

        var delivered = await RoundTripAsync(sent, id);

        RawMessage.AssertBytesEqual(sent, delivered, "terminal blank lines");
    }

    /// <summary>
    /// NUL and other control bytes inside the body are stored verbatim.
    /// </summary>
    /// <remarks>
    /// NUL is the classic string-boundary bug: a path that treats the body as a C string, or that
    /// round-trips through a length-prefixed conversion incorrectly, truncates here. It is not legal
    /// in a conforming message body, which is exactly why a journaling relay must not silently alter
    /// it — the archive should record what arrived.
    /// </remarks>
    [Fact]
    public async Task NulAndControlBytes_AreStoredVerbatim()
    {
        const string id = "nul";
        var body = new byte[] { (byte)'a', 0x00, (byte)'b', 0x01, 0x02, 0x1F, (byte)'c', (byte)'\r', (byte)'\n' };
        var sent = Compose(id, body);

        var delivered = await RoundTripAsync(sent, id);

        RawMessage.AssertBytesEqual(sent, delivered, "NUL and control bytes");
    }

    /// <summary>
    /// 8-bit and invalid-UTF-8 body octets survive unchanged.
    /// </summary>
    /// <remarks>
    /// Complements <c>DataAndMessageTests.InvalidUtf8BodyBytes_AreStoredByteExact</c> by asserting it
    /// over a full message through the raw harness rather than over a single line. 0x80 and 0xFF are
    /// invalid UTF-8 in any position; 0xE9 is 'é' in Latin-1 but a truncated sequence in UTF-8. The
    /// old path decoded each line to UTF-16 and re-encoded it, turning every one of these into the
    /// three bytes EF BF BD.
    /// </remarks>
    [Fact]
    public async Task EightBitAndInvalidUtf8_SurviveUnchanged()
    {
        const string id = "eight-bit";
        var body = new byte[]
        {
            (byte)'L', (byte)'a', (byte)'t', (byte)'i', (byte)'n', (byte)':', (byte)' ',
            0xE9, 0xE8, 0xFC, (byte)'\r', (byte)'\n',
            (byte)'R', (byte)'a', (byte)'w', (byte)':', (byte)' ',
            0x80, 0xFF, 0xFE, (byte)'\r', (byte)'\n'
        };

        var sent = Compose(id, body);

        var delivered = await RoundTripAsync(sent, id);

        RawMessage.AssertBytesEqual(sent, delivered, "8-bit body octets");
    }

    /// <summary>
    /// A body line beginning with a dot arrives as the sender composed it, with transparency reversed.
    /// </summary>
    /// <remarks>
    /// The harness stuffs at byte level, so this asserts the server's unstuffing against a client
    /// that framed the message correctly — the RFC 5321 §4.5.2 round trip end to end.
    /// </remarks>
    [Fact]
    public async Task LeadingDotLines_RoundTripThroughTransparency()
    {
        const string id = "dot-lines";
        var sent = Compose(id, Ascii(".leading dot\r\n..double dot\r\nnormal\r\n. dot space\r\n"));

        var delivered = await RoundTripAsync(sent, id);

        RawMessage.AssertBytesEqual(sent, delivered, "leading-dot transparency");
    }

    /// <summary>
    /// A body that crosses the 4 MB spill boundary is delivered byte-exact.
    /// </summary>
    /// <remarks>
    /// <see cref="MessageBody.SpillThresholdBytes"/> is where the store moves from a
    /// <c>MemoryStream</c> to a temp file, copying what it had accumulated. That copy is a seam, and
    /// a seam in a byte path is where truncation and off-by-one live. Sized just over the threshold
    /// rather than far past it: the point is to cross the boundary, not to move volume, so the cost
    /// to the default test run stays small.
    /// </remarks>
    [Fact]
    public async Task BodyCrossingTheSpillBoundary_IsDeliveredByteExact()
    {
        const string id = "spill";
        var line = Ascii("spill boundary payload line with enough length to be worth repeating\r\n");
        var body = new MemoryStream();

        // Just past 4 MB, so the crossing happens mid-body with content on both sides of it.
        while (body.Length < (4 * 1024 * 1024) + (64 * 1024))
            body.Write(line, 0, line.Length);

        var sent = Compose(id, body.ToArray());

        var delivered = await RoundTripAsync(sent, id);

        RawMessage.AssertBytesEqual(sent, delivered, "spill-boundary body");
    }

    // ── negative control ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The oracle detects a single flipped byte.
    /// </summary>
    /// <remarks>
    /// Without this, every passing test above is consistent with a comparison that always succeeds.
    /// The mutation is applied to the expected copy after the round trip, so it proves the assertion
    /// fails on a one-byte difference rather than proving anything about the server.
    /// </remarks>
    [Fact]
    public async Task Oracle_DetectsASingleFlippedByte()
    {
        const string id = "control";
        var sent = Compose(id, Ascii("the payload that must not change\r\n"));

        var delivered = await RoundTripAsync(sent, id);

        RawMessage.AssertBytesEqual(sent, delivered, "unmutated");

        var mutated = (byte[])delivered.Clone();
        mutated[^3] ^= 0x01;

        Assert.Throws<Xunit.Sdk.FailException>(
            () => RawMessage.AssertBytesEqual(sent, mutated, "mutated"));
    }
}
