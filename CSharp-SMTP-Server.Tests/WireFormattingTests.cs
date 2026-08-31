using System.Net;
using System.Net.Sockets;
using CSharp_SMTP_Server.Networking;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Phase 1 (§3.8): direct tests of the wire-output helpers (ClientProcessor.WriteCode / SMTPCodes) —
/// response line formatting and, most importantly, the CR/LF sanitization that prevents a delivery
/// handler from injecting spurious SMTP response lines (response splitting).
///
/// A real ClientProcessor is constructed on a loopback socket pair; its async greeting is drained
/// first so every assertion reads exactly one known line.
/// </summary>
public sealed class WireFormattingTests
{
    [Fact]
    public async Task WriteCode_CodeOnly_SendsTableText()
    {
        await using var h = await ProcessorHarness.CreateAsync();

        await h.Processor.WriteCode(503);

        Assert.Equal("503 Bad sequence of commands", await h.ReadLineAsync());
    }

    [Fact]
    public async Task WriteCode_WithEnhancedStatus_AppendsDefaultTableText()
    {
        await using var h = await ProcessorHarness.CreateAsync();

        // (ushort) cast: an int literal would bind to the WriteCode(int, string) sanitizer overload instead.
        await h.Processor.WriteCode((ushort)250, "2.1.5");

        Assert.Equal("250 2.1.5 OK", await h.ReadLineAsync());
    }

    [Fact]
    public async Task WriteCode_WithCustomText_SendsItVerbatim()
    {
        await using var h = await ProcessorHarness.CreateAsync();

        await h.Processor.WriteCode(451, "4.3.0", "custom text here");

        Assert.Equal("451 4.3.0 custom text here", await h.ReadLineAsync());
    }

    [Fact]
    public async Task WriteCode_IntMessage_ReplacesLineFeedWithSpace()
    {
        await using var h = await ProcessorHarness.CreateAsync();

        await h.Processor.WriteCode(451, "line1\nline2");

        Assert.Equal("451 line1 line2", await h.ReadLineAsync());
    }

    [Fact]
    public async Task WriteCode_IntMessage_ReplacesCarriageReturnSequencesWithSpaces()
    {
        await using var h = await ProcessorHarness.CreateAsync();

        await h.Processor.WriteCode(550, "a\r\nb\rc");

        // Each CR and LF character is replaced individually — a CRLF pair becomes two spaces.
        Assert.Equal("550 a  b c", await h.ReadLineAsync());
    }

    [Fact]
    public async Task WriteCode_IntMessage_CleanTextPassesThroughUnchanged()
    {
        await using var h = await ProcessorHarness.CreateAsync();

        await h.Processor.WriteCode(451, "plain message");

        Assert.Equal("451 plain message", await h.ReadLineAsync());
    }

    /// <summary>
    /// Builds a live ClientProcessor on an ephemeral loopback socket pair. The Listener is never
    /// started — it only carries the SMTPServer reference the processor needs.
    /// </summary>
    private sealed class ProcessorHarness : IAsyncDisposable
    {
        public ClientProcessor Processor { get; }
        public StreamReader Reader { get; }

        private readonly TcpClient _client;
        private readonly TcpListener _tcpListener;
        private readonly SMTPServer _server;

        private ProcessorHarness()
        {
            // SPF/DMARC disabled → no DNS client, no network I/O at construction.
            _server = new SMTPServer(null, new ServerOptions(false, false), NoopDelivery.Instance);
            var listener = new Listener(IPAddress.Loopback, 0, _server, false, false);

            _tcpListener = new TcpListener(IPAddress.Loopback, 0);
            _tcpListener.Start();
            _client = new TcpClient();
            _client.Connect((IPEndPoint)_tcpListener.Server.LocalEndPoint);
            var serverSide = _tcpListener.AcceptTcpClient();

            Processor = new ClientProcessor(serverSide, listener, false); // kicks off async Greet()
            Reader = new StreamReader(_client.GetStream());
        }

        public static async Task<ProcessorHarness> CreateAsync()
        {
            var h = new ProcessorHarness();
            var greeting = await h.ReadLineAsync();
            Assert.True(greeting != null && greeting.StartsWith("220 "), $"expected the 220 greeting to be drained first, got: {greeting}");
            return h;
        }

        public ValueTask<string?> ReadLineAsync(int timeoutMs = 10_000) =>
            Reader.ReadLineAsync(new CancellationTokenSource(timeoutMs).Token);

        public ValueTask DisposeAsync()
        {
            // Do NOT call Processor.Dispose() here: its Receive loop is fire-and-forget async void,
            // and disposing the reader while the loop sits between iterations throws an unhandled
            // ObjectDisposedException from the loop condition (outside its try/catch) → process crash.
            // Instead, reset our side of the socket with RST so the receive loop exits through its
            // own IOException catch path — exactly like a real client disconnect.
            _client.LingerState = new LingerOption(true, 0);
            _client.Dispose();
            _tcpListener.Stop();
            _server.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
