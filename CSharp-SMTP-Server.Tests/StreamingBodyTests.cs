using System.Net;
using System.Net.Sockets;
using System.Text;
using CSharp_SMTP_Server.Networking;
using CSharp_SMTP_Server.Protocol.Responses;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Tests for the streaming DATA path: the byte-backed <see cref="MessageBody"/>, the
/// <see cref="MailTransaction.GetBodyStream"/> API, temp-file lifetime, and the bounded line reader.
/// </summary>
/// <remarks>
/// These cover the change that took a 150 MB message from ~1.9 GB of peak working set to ~110 MB.
/// The memory result itself is measured by the heavy-tier load test
/// (<c>Office365RelayTests.LargeMessage_150MB_IsAcceptedIntact</c>); what is asserted here are the
/// invariants that make it safe — content is unchanged, temp files do not leak, and an unterminated
/// line cannot grow without bound.
/// </remarks>
public class StreamingBodyTests
{
    // ── MessageBody ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MessageBody_BelowThreshold_StaysInMemory()
    {
        using var body = new MessageBody();
        body.WriteLine("a short line");

        Assert.False(body.IsSpilled);
        Assert.Equal("a short line\r\n", body.ReadAsString());
    }

    [Fact]
    public void MessageBody_PastThreshold_SpillsToFile_AndKeepsContentIntact()
    {
        using var body = new MessageBody();

        // 1 KB lines up past the 4 MB spill threshold, each tagged with its index so a
        // mis-ordered or dropped chunk at the memory→file handover is visible rather than silent.
        const int lines = 6 * 1024;
        var filler = new string('x', 1000);

        for (var i = 0; i < lines; i++)
            body.WriteLine($"{i:D8}{filler}");

        Assert.True(body.IsSpilled);

        using var stream = body.OpenRead();
        using var reader = new StreamReader(stream);

        for (var i = 0; i < lines; i++)
            Assert.Equal($"{i:D8}{filler}", reader.ReadLine());

        Assert.Null(reader.ReadLine());
    }

    [Fact]
    public void MessageBody_SpillFile_IsDeletedOnDispose()
    {
        var before = SpillFiles();

        var body = new MessageBody();
        var filler = new string('y', 1000);

        for (var i = 0; i < 6 * 1024; i++)
            body.WriteLine(filler);

        Assert.True(body.IsSpilled);
        Assert.Single(SpillFiles().Except(before));

        body.Dispose();

        // The file is gone, not merely closed: a relay that leaked one temp file per large message
        // would fill the pod's disk over a day of journaling.
        Assert.Empty(SpillFiles().Except(before));
    }

    [Fact]
    public void MessageBody_PrependedHeaders_ReadBackOutermostFirst()
    {
        using var body = new MessageBody("Subject: s\r\n\r\nbody\r\n");

        body.PrependHeader("Received", "first");
        body.PrependHeader("Authentication-Results", "second");

        // Each PrependHeader puts its header nearer the top, matching the string-prepend order the
        // Received:/Authentication-Results: sequence depended on before the rework.
        Assert.Equal(
            "Authentication-Results: second\r\nReceived: first\r\nSubject: s\r\n\r\nbody\r\n",
            body.ReadAsString());
    }

    [Fact]
    public void MessageBody_MultiByteUtf8_SurvivesTheSpillBoundary()
    {
        using var body = new MessageBody();

        // Multi-byte characters straddling the memory→file handover: a byte-level bug here would
        // corrupt exactly the Polish/Greek/CJK content the corpus carries.
        const string text = "zażółć gęślą jaźń — Ελληνικά — 日本語 — 😀";

        // ~76 UTF-8 bytes per line, so this clears the 4 MB spill threshold comfortably.
        const int lines = 80_000;

        for (var i = 0; i < lines; i++)
            body.WriteLine(text);

        Assert.True(body.IsSpilled);

        using var reader = new StreamReader(body.OpenRead());

        for (var i = 0; i < lines; i++)
            Assert.Equal(text, reader.ReadLine());

        Assert.Null(reader.ReadLine());
    }

    [Fact]
    public void MessageBody_Dispose_LeavesAnInMemoryBodyReadable()
    {
        // Deliberate asymmetry: disposing releases a temp file but does not invalidate an in-memory
        // body. Retaining a transaction past the delivery call and reading it later is a reasonable
        // thing for a handler to do — and what this suite's own RecordingDelivery does — so ordinary
        // mail must keep working exactly as it did.
        var body = new MessageBody("Subject: s\r\n\r\nbody\r\n");

        Assert.False(body.IsSpilled);
        body.Dispose();

        Assert.Equal("Subject: s\r\n\r\nbody\r\n", body.ReadAsString());
    }

    // ── GetBodyStream over a real transaction ─────────────────────────────────────────────────

    [Fact]
    public async Task GetBodyStream_YieldsTheSameContentAsRawBody()
    {
        var delivery = new RecordingDelivery();
        string? streamed = null;

        delivery.HandlerOverride = (transaction, _) =>
        {
            using var stream = transaction.GetBodyStream();
            using var reader = new StreamReader(stream);
            streamed = reader.ReadToEnd();

            // Read inside the handler, where the body is guaranteed alive, and compare against the
            // compatibility property on the same transaction.
            Assert.Equal(transaction.RawBody, streamed);
            return Task.FromResult(SmtpDeliveryResult.Ok());
        };

        var (s, server, _) = await ConnectReadyAsync(delivery);
        using (server)
        await using (s)
        {
            await SendSimpleMessageAsync(s, "hello from the stream");
            Assert.StartsWith("250", await s.ReadLineAsync());
        }

        Assert.NotNull(streamed);
        Assert.Contains("hello from the stream", streamed);
        Assert.StartsWith("Received: from 127.0.0.1 by test.local with SMTP; ", streamed);
    }

    [Fact]
    public async Task BodyLength_MatchesTheStreamedByteCount()
    {
        var delivery = new RecordingDelivery();
        long reported = -1;
        long actual = -1;

        delivery.HandlerOverride = (transaction, _) =>
        {
            reported = transaction.BodyLength;

            using var stream = transaction.GetBodyStream();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            actual = memory.Length;

            return Task.FromResult(SmtpDeliveryResult.Ok());
        };

        var (s, server, _) = await ConnectReadyAsync(delivery);
        using (server)
        await using (s)
        {
            await SendSimpleMessageAsync(s, "measured");
            Assert.StartsWith("250", await s.ReadLineAsync());
        }

        Assert.True(reported > 0);
        Assert.Equal(actual, reported);
    }

    [Fact]
    public async Task LargeMessage_SpillsAndReleasesItsTempFile_AfterDelivery()
    {
        var before = SpillFiles();

        var delivery = new RecordingDelivery();
        var spilledDuringDelivery = false;
        long streamedBytes = 0;

        delivery.HandlerOverride = (transaction, _) =>
        {
            spilledDuringDelivery = SpillFiles().Except(before).Any();

            using var stream = transaction.GetBodyStream();
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                streamedBytes += read;

            return Task.FromResult(SmtpDeliveryResult.Ok());
        };

        var (s, server, _) = await ConnectReadyAsync(delivery);
        using (server)
        await using (s)
        {
            await s.Send("MAIL FROM:<a@b.com>");
            Assert.StartsWith("250", await s.ReadLineAsync());
            await s.Send("RCPT TO:<c@d.e>");
            Assert.StartsWith("250", await s.ReadLineAsync());
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());

            await s.Send("Subject: large");
            await s.Send("");

            // Past the 4 MB spill threshold.
            var filler = new string('z', 1000);
            for (var i = 0; i < 6 * 1024; i++)
                await s.Send(filler);

            await s.Send(".");
            Assert.StartsWith("250", await s.ReadLineAsync());
        }

        Assert.True(spilledDuringDelivery, "a 6 MB message should have spilled to a temp file");
        Assert.True(streamedBytes > 6 * 1024 * 1000, $"only {streamedBytes} bytes streamed");

        // Released once the handler returned — the property that keeps a sustained journaling load
        // from accumulating temp files.
        Assert.Empty(SpillFiles().Except(before));
    }

    [Fact]
    public async Task OversizedMessage_ReleasesItsTempFile_OnThe552Path()
    {
        var before = SpillFiles();

        var options = TestServers.DefaultOptions();
        options.MessageCharactersLimit = 5 * 1024 * 1024; // above the spill threshold, below what we send

        var delivery = new RecordingDelivery();
        var (s, server, _) = await ConnectReadyAsync(delivery, options);
        using (server)
        await using (s)
        {
            await s.Send("MAIL FROM:<a@b.com>");
            Assert.StartsWith("250", await s.ReadLineAsync());
            await s.Send("RCPT TO:<c@d.e>");
            Assert.StartsWith("250", await s.ReadLineAsync());
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());

            var filler = new string('q', 1000);
            for (var i = 0; i < 7 * 1024; i++)
                await s.Send(filler);

            await s.Send(".");
            Assert.Equal("552 5.4.3 Message size exceeds the administrative limit.", await s.ReadLineAsync());

            // A rejected message must not leave its spill file behind: an attacker repeating this
            // would otherwise fill the disk with data the server already decided to refuse.
            Assert.True(await WaitForAsync(() => !SpillFiles().Except(before).Any()),
                "the rejected message's temp file was not released: " +
                string.Join(", ", SpillFiles().Except(before)));
        }
    }

    [Fact]
    public async Task AbandonedTransaction_ReleasesItsTempFile_OnDisconnect()
    {
        var before = SpillFiles();

        var delivery = new RecordingDelivery();
        var (s, server, _) = await ConnectReadyAsync(delivery);

        using (server)
        {
            await s.Send("MAIL FROM:<a@b.com>");
            Assert.StartsWith("250", await s.ReadLineAsync());
            await s.Send("RCPT TO:<c@d.e>");
            Assert.StartsWith("250", await s.ReadLineAsync());
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());

            var filler = new string('w', 1000);
            for (var i = 0; i < 6 * 1024; i++)
                await s.Send(filler);

            // Wait for the spill file to actually appear before abandoning the connection: without
            // this the test can race ahead of the server's receive loop and assert on a transaction
            // that never grew large enough to have a file at all, passing vacuously.
            var spilled = await WaitForAsync(() => SpillFiles().Except(before).Any());
            Assert.True(spilled, "a 6 MB message should have spilled to a temp file");

            // Drop the connection mid-DATA, without ever sending the terminating dot — the shape a
            // client crash or a network partition takes.
            await s.DisposeAsync();

            // Teardown is asynchronous; poll rather than assume it has already happened.
            var released = await WaitForAsync(() => !SpillFiles().Except(before).Any());

            Assert.True(released,
                "the abandoned transaction's temp file was not released: " +
                string.Join(", ", SpillFiles().Except(before)));
        }
    }

    // ── BoundedLineReader ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BoundedLineReader_ReadsOrdinaryLines()
    {
        var reader = ReaderOver("one\r\ntwo\r\n\r\nfour\r\n");

        Assert.Equal("one", await reader.ReadLineAsync());
        Assert.Equal("two", await reader.ReadLineAsync());
        Assert.Equal(string.Empty, await reader.ReadLineAsync());
        Assert.Equal("four", await reader.ReadLineAsync());
        Assert.Null(await reader.ReadLineAsync());
    }

    [Fact]
    public async Task BoundedLineReader_AcceptsBareLf_AndAnUnterminatedTail()
    {
        var reader = ReaderOver("one\ntwo\r\nthree");

        Assert.Equal("one", await reader.ReadLineAsync());
        Assert.Equal("two", await reader.ReadLineAsync());
        Assert.Equal("three", await reader.ReadLineAsync()); // no terminator, still a line
        Assert.Null(await reader.ReadLineAsync());
    }

    [Fact]
    public async Task BoundedLineReader_DecodesMultiByteUtf8AcrossBufferBoundaries()
    {
        // 8 KB read buffer, so these lines straddle it repeatedly; decoding each read independently
        // rather than with a stateful Decoder would corrupt the characters that land on the seam.
        var text = string.Concat(Enumerable.Repeat("zażółć gęślą jaźń 日本語 😀 ", 200));
        var reader = ReaderOver(text + "\r\n" + text + "\r\n");

        Assert.Equal(text, await reader.ReadLineAsync());
        Assert.Equal(text, await reader.ReadLineAsync());
        Assert.Null(await reader.ReadLineAsync());
    }

    [Fact]
    public async Task BoundedLineReader_TruncatesAnOverlongLine_AndResumesAtTheNext()
    {
        var overlong = new string('a', BoundedLineReader.MaxLineLength + 50_000);
        var reader = ReaderOver(overlong + "\r\nnext line\r\n");

        var read = await reader.ReadLineAsync();

        Assert.Equal(BoundedLineReader.MaxLineLength, read!.Length);
        Assert.True(reader.LastLineTruncated);

        // Framing survives truncation: the tail was discarded, not re-read as a fresh line.
        Assert.Equal("next line", await reader.ReadLineAsync());
        Assert.False(reader.LastLineTruncated);
    }

    [Fact]
    public async Task BoundedLineReader_UnterminatedFlood_DoesNotGrowWithoutBound()
    {
        // The hole this reader closes: StreamReader.ReadLineAsync materializes a whole line before
        // returning, so a client that streams bytes and never sends CRLF grew its buffer without
        // limit — unauthenticated, on an internet-facing listener. MessageCharactersLimit does not
        // help, because it is applied per line, after the line exists.
        const int megabytes = 64;

        var stream = new RepeatingStream((byte)'a', (long)megabytes * 1024 * 1024);
        var reader = new BoundedLineReader(stream);

        var before = GC.GetTotalMemory(true);
        var read = await reader.ReadLineAsync();
        var consumed = GC.GetTotalMemory(false) - before;

        Assert.Equal(BoundedLineReader.MaxLineLength, read!.Length);
        Assert.True(reader.LastLineTruncated);

        // 64 MB arrived; the cap is 1 MB. Allow generous slack for the StringBuilder's chunks and the
        // final string, but far below the size of the flood itself.
        Assert.True(consumed < 8 * 1024 * 1024,
            $"{megabytes} MB of unterminated input retained {consumed / 1024 / 1024} MB");
    }

    [Fact]
    public async Task UnterminatedFlood_OverTheWire_DoesNotExhaustMemory()
    {
        // The same hole, end to end through the server's own receive path rather than the reader in
        // isolation: connect, send megabytes inside DATA with no CRLF at all, and confirm the process
        // does not balloon.
        var delivery = new RecordingDelivery();
        var (s, server, _) = await ConnectReadyAsync(delivery);

        using (server)
        {
            await s.Send("MAIL FROM:<a@b.com>");
            Assert.StartsWith("250", await s.ReadLineAsync());
            await s.Send("RCPT TO:<c@d.e>");
            Assert.StartsWith("250", await s.ReadLineAsync());
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());

            var before = GC.GetTotalMemory(true);

            // 32 MB in 1 MB writes, with no CRLF anywhere — the server never sees a complete line.
            var chunk = Encoding.ASCII.GetBytes(new string('a', 1024 * 1024));
            for (var i = 0; i < 32; i++)
                await s.SendRaw(chunk);

            // Give the receive loop time to consume what was written before measuring.
            await Task.Delay(500);

            var consumed = GC.GetTotalMemory(false) - before;

            // 32 MB of CRLF-free data. Without the bound this grows with everything sent.
            Assert.True(consumed < 16 * 1024 * 1024,
                $"32 MB of unterminated DATA retained {consumed / 1024 / 1024} MB");

            await s.DisposeAsync();
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static BoundedLineReader ReaderOver(string text) =>
        new(new MemoryStream(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// The spill files currently in the temp directory.
    /// </summary>
    /// <remarks>
    /// Compared as a SET against a baseline rather than as a count. A count is fooled by any unrelated
    /// file appearing or vanishing between the two reads, and reports "leaked" without saying which
    /// file — the set difference names it, which is what makes a failure here diagnosable.
    /// </remarks>
    private static string[] SpillFiles() =>
        Directory.GetFiles(Path.GetTempPath(), "csharp-smtp-*.eml");

    /// <summary>
    /// Polls <paramref name="condition"/> until it holds or the timeout elapses.
    /// </summary>
    /// <remarks>
    /// Both spilling and teardown happen on the server's own threads, so neither is observable the
    /// instant the client's call returns. Polling keeps the test honest about that without the fixed
    /// sleep that would make it slow on a fast machine and flaky on a loaded one.
    /// </remarks>
    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        for (var waited = 0; waited < timeoutMs; waited += 25)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }

        return condition();
    }

    private static async Task<(SmtpSession Session, SMTPServer Server, RecordingDelivery Delivery)>
        ConnectReadyAsync(RecordingDelivery delivery, ServerOptions? options = null)
    {
        var port = TestPorts.Allocate();
        var server = new SMTPServer(
            new[] { new ListeningParameters(IPAddress.Loopback, new[] { port }, null) },
            options ?? TestServers.DefaultOptions(), delivery);
        server.Start();

        var session = await SmtpSession.ConnectAsync(port, timeout: TimeSpan.FromMinutes(2));
        Assert.StartsWith("220", await session.ReadLineAsync());
        await session.Send("EHLO test");
        await session.ReadResponseAsync();

        return (session, server, delivery);
    }

    private static async Task SendSimpleMessageAsync(SmtpSession s, string body)
    {
        await s.Send("MAIL FROM:<a@b.com>");
        Assert.StartsWith("250", await s.ReadLineAsync());
        await s.Send("RCPT TO:<c@d.e>");
        Assert.StartsWith("250", await s.ReadLineAsync());
        await s.Send("DATA");
        Assert.StartsWith("354", await s.ReadLineAsync());
        await s.Send("Subject: test");
        await s.Send("From: a@b.com");
        await s.Send("To: c@d.e");
        await s.Send("");
        await s.Send(body);
        await s.Send(".");
    }

    /// <summary>
    /// A read-only stream that yields the same byte a fixed number of times and never a terminator.
    /// </summary>
    /// <remarks>
    /// Models the unterminated-flood client without allocating the flood: the point of the test is
    /// that the READER does not retain it, which a real byte[] of the same size would mask.
    /// </remarks>
    private sealed class RepeatingStream : Stream
    {
        private readonly byte _value;
        private long _remaining;

        internal RepeatingStream(byte value, long count)
        {
            _value = value;
            _remaining = count;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _remaining;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;

            var take = (int)Math.Min(count, _remaining);
            for (var i = 0; i < take; i++)
                buffer[offset + i] = _value;

            _remaining -= take;
            return take;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
