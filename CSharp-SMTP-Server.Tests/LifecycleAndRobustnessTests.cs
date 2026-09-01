using System.Reflection;
using System.Net;
using System.Net.Sockets;
using CSharp_SMTP_Server.Interfaces;
using CSharp_SMTP_Server.Networking;
using CSharp_SMTP_Server.Protocol.Responses;

namespace CSharp_SMTP_Server.Tests;

/// <summary>Lifecycle, listener, malformed-input, disconnect, and stress coverage. See TESTING.md.</summary>
public sealed class LifecycleAndRobustnessTests
{
    [Fact]
    public void Ctor_NullParameters_StartsWithZeroListeners()
    {
        using var server = new SMTPServer(null, TestServers.DefaultOptions(), NoopDelivery.Instance);
        server.Start(); // no listeners — must not throw
    }

    [Fact]
    public async Task AddListener_AfterStart_BeginsAcceptingImmediately()
    {
        using var server = new SMTPServer(null, TestServers.DefaultOptions(), NoopDelivery.Instance);
        server.Start();

        var port = TestPorts.Allocate();
        server.AddListener(IPAddress.Loopback, port, tls: false); // _started flag path → starts at once

        await using var s = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await s.ReadLineAsync());
    }

    [Fact]
    public void Ctor_NullEntriesAndNullPortArrays_SkippedWithoutThrowing()
    {
        var parameters = new ListeningParameters?[]
        {
            null!, // null entry — skipped by the ctor loop
            new ListeningParameters(IPAddress.Loopback, null, null) // both port arrays null → no listeners
        };

        using var server = new SMTPServer(parameters, TestServers.DefaultOptions(), NoopDelivery.Instance);
        server.Start();
    }

    [Fact]
    public async Task MultipleListeners_EachAcceptsIndependently()
    {
        var portA = TestPorts.Allocate();
        var portB = TestPorts.Allocate();

        using var server = new SMTPServer(
            new[]
            {
                new ListeningParameters(IPAddress.Loopback, new[] { portA }, null),
                new ListeningParameters(IPAddress.Loopback, new[] { portB }, null)
            },
            TestServers.DefaultOptions(), NoopDelivery.Instance);
        server.Start();

        foreach (var port in new[] { portA, portB })
        {
            await using var s = await SmtpSession.ConnectAsync(port);
            Assert.StartsWith("220 ", await s.ReadLineAsync());
            await s.Send("NOOP");
            Assert.StartsWith("250", await s.ReadLineAsync());
        }
    }

    [Fact]
    public async Task Dispose_StopsListener_AndKillsOpenConnections()
    {
        var port = TestPorts.Allocate();
        var server = new SMTPServer(
            new[] { new ListeningParameters(IPAddress.Loopback, new[] { port }, null) },
            TestServers.DefaultOptions(), NoopDelivery.Instance);
        server.Start();

        await using var s = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await s.ReadLineAsync()); // connection established…

        server.Dispose(); // …then the server goes away (open connections get RST via linger option)

        // The open connection is dead: a read yields EOF or a reset error.
        string? line;
        try { line = await s.ReadLineAsync(); }
        catch (IOException) { line = null; } // platform may surface the RST as an exception instead
        Assert.Null(line);

        // And new connections are refused.
        var client = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ConnectionAcceptedDuringShutdown_IsRefusedAndDisposed_R11()
    {
        // R11: there is a window between AcceptTcpClient() returning and AddProcessor() registering
        // the connection. If Dispose() snapshots the processor list inside that window, the connection
        // used to be added afterwards — into a list nobody reads again — and was never disposed. It
        // kept serving a client after shutdown, while SMTPServer.Dispose() went on to dispose the TLS
        // certificate it may still have been using.
        //
        // This pins the invariant that closes the window: once the listener is disposed, registration
        // is refused rather than silently succeeding, and the accept loop disposes what it turns away.
        //
        // A racing version of this test (N connections hammered against a concurrent Dispose()) was
        // written and rejected: it took ~8 minutes and still PASSED against the unfixed library, because
        // the accept/registration window is far too narrow to land in by chance from outside the
        // process. Asserting the invariant directly is both deterministic and fast; the honest tradeoff
        // is that it verifies the guard rather than reproducing the original race end-to-end.
        var port = TestPorts.Allocate();
        using var server = new SMTPServer(null, TestServers.DefaultOptions(), NoopDelivery.Instance);
        var listener = new Listener(IPAddress.Loopback, port, server, false, false);
        listener.Start();

        // The listener is live and registering normally.
        await using (var s = await SmtpSession.ConnectAsync(port))
            Assert.StartsWith("220 ", await s.ReadLineAsync());

        // Open a real connection, then dispose the listener while we hold the accepted client. This is
        // the state the accept loop is in inside the window: it has a TcpClient and is about to
        // register a processor for it.
        var sink = new TcpListener(IPAddress.Loopback, TestPorts.Allocate());
        sink.Start();
        using var late = new TcpClient();
        await late.ConnectAsync((IPEndPoint)sink.LocalEndpoint);
        using var accepted = await sink.AcceptTcpClientAsync();

        listener.Dispose();

        // Registration must now be refused. Pre-fix, AddProcessor returned void and added it
        // unconditionally — into a list already snapshotted, so it was never disposed.
        var processor = new ClientProcessor(accepted, listener, false);
        Assert.False(listener.AddProcessor(processor),
            "AddProcessor accepted a registration after Dispose() — the R11 window is open again");

        processor.Dispose(true, true); // what the accept loop now does with a refused processor
        sink.Stop();
    }

    [Fact]
    public async Task Dispose_JoinsAcceptThread_BeforeReturning_R7()
    {
        // R7: Dispose() used to return without confirming the accept thread had exited, so the thread
        // could still be inside AcceptTcpClient while SMTPServer.Dispose() went on to dispose the TLS
        // certificate. It now waits (bounded) for the loop to signal that it left.
        var port = TestPorts.Allocate();
        using var server = new SMTPServer(null, TestServers.DefaultOptions(), NoopDelivery.Instance);
        var listener = new Listener(IPAddress.Loopback, port, server, false, false);
        listener.Start();

        await using (var s = await SmtpSession.ConnectAsync(port))
            Assert.StartsWith("220 ", await s.ReadLineAsync()); // thread is live and accepting

        var thread = (Thread)typeof(Listener)
            .GetField("_listenerThread", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(listener)!;
        Assert.True(thread.IsAlive);

        listener.Dispose();

        // The guarantee is synchronous: no polling, no sleep. If Dispose() returned early this fails.
        Assert.False(thread.IsAlive, "Dispose() returned while the accept thread was still running (R7)");
    }

    [Fact]
    public void Dispose_IsIdempotent_R7()
    {
        // Dispose() now owns a CancellationTokenSource and a ManualResetEventSlim, so a second call
        // would throw ObjectDisposedException where the old bool-flag version was harmless. Both
        // SMTPServer.Dispose() and a caller's own `using` can reach this, so it must stay idempotent.
        var port = TestPorts.Allocate();
        using var server = new SMTPServer(null, TestServers.DefaultOptions(), NoopDelivery.Instance);
        var listener = new Listener(IPAddress.Loopback, port, server, false, false);
        listener.Start();

        listener.Dispose();
        listener.Dispose(); // must not throw
        listener.Dispose();
    }

    [Fact]
    public void Dispose_NeverStartedListener_ReturnsImmediately_R7()
    {
        // A listener whose thread was never started never signals the accept loop's exit event. Dispose()
        // must not sit on its 5 s timeout waiting for a thread that will never run — this is the path
        // taken by every server that is constructed and disposed without Start() (and by the
        // port-already-in-use path below).
        var port = TestPorts.Allocate();
        using var server = new SMTPServer(null, TestServers.DefaultOptions(), NoopDelivery.Instance);
        var listener = new Listener(IPAddress.Loopback, port, server, false, false); // never Start()ed

        var sw = System.Diagnostics.Stopwatch.StartNew();
        listener.Dispose();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"Dispose() on an unstarted listener blocked for {sw.Elapsed.TotalSeconds:F1}s — it waited on a thread that never ran");
    }

    [Fact]
    public async Task PortAlreadyInUse_StartDoesNotThrow_OthersKeepWorking()
    {
        var blockedPort = TestPorts.Allocate();
        var blocker = new TcpListener(IPAddress.Loopback, blockedPort);
        blocker.Start(); // occupy the port

        var freePort = TestPorts.Allocate();
        var logger = new RecordingLogger();

        var server = new SMTPServer(
            new[]
            {
                new ListeningParameters(IPAddress.Loopback, new[] { blockedPort }, null),
                new ListeningParameters(IPAddress.Loopback, new[] { freePort }, null)
            },
            TestServers.DefaultOptions(), NoopDelivery.Instance, logger);

        server.Start(); // must not throw — the failing listener logs and dies on its own thread

        // The failed listener reported an error…
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !logger.Errors.Any(e => e.StartsWith("[Listening]")))
            await Task.Delay(50);
        Assert.Contains(logger.Errors, e => e.StartsWith("[Listening]"));

        // …and the other listener works fine.
        try
        {
            using (server)
            {
                await using var s = await SmtpSession.ConnectAsync(freePort);
                Assert.StartsWith("220 ", await s.ReadLineAsync());
            }
        }
        finally
        {
            blocker.Stop(); // release the occupied port
        }
    }

    [Fact]
    public async Task DualMode_Ipv4Address_IsIgnored()
    {
        // dualMode only applies to IPv6 sockets (Listener ctor). With an IPv4 address it must be a
        // no-op: the listener starts normally and serves IPv4; IPv6 connections are simply refused.
        var port = TestPorts.Allocate();
        using var server = new SMTPServer(
            new[] { new ListeningParameters(IPAddress.Any, new[] { port }, null, dualMode: true) },
            TestServers.DefaultOptions(), NoopDelivery.Instance);
        server.Start();

        await using var s4 = await SmtpSession.ConnectAsync(port, IPAddress.Loopback);
        Assert.StartsWith("220 ", await s4.ReadLineAsync());

        var client6 = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
            await client6.ConnectAsync(IPAddress.IPv6Loopback, port).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DualMode_Ipv6Any_AcceptsBothFamilies()
    {
        var port = TestPorts.Allocate();
        using var server = new SMTPServer(
            new[] { new ListeningParameters(IPAddress.IPv6Any, new[] { port }, null, dualMode: true) },
            TestServers.DefaultOptions(), NoopDelivery.Instance);
        server.Start();

        await using var s6 = await SmtpSession.ConnectAsync(port, IPAddress.IPv6Loopback);
        Assert.StartsWith("220 ", await s6.ReadLineAsync());

        // v4-mapped connection on the dual-mode socket.
        await using var s4 = await SmtpSession.ConnectAsync(port, IPAddress.Loopback);
        Assert.StartsWith("220 ", await s4.ReadLineAsync());
    }

    [Fact]
    public async Task GarbageInput_BinaryBytes_ServerSurvives_ServesNextClient()
    {
        var port = TestPorts.Allocate();
        var logger = new RecordingLogger();
        using var server = new SMTPServer(
            new[] { new ListeningParameters(IPAddress.Loopback, new[] { port }, null) },
            TestServers.DefaultOptions(), NoopDelivery.Instance, logger);
        server.Start();

        // Client A: raw binary garbage (NULs, invalid UTF-8, CRLF-separated junk lines).
        await using var s = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await s.ReadLineAsync());

        var garbage = new byte[512];
        for (var i = 0; i < garbage.Length; i++)
            garbage[i] = (byte)(i % 251); // includes NULs and invalid UTF-8 sequences
        await s.SendRaw(garbage);

        // The server must respond to the junk lines it can form rather than hanging or crashing.
        // 500 is included because this garbage contains 0x0A bytes: those are bare-LF terminators,
        // which the server refuses outright (RFC 5321 §2.3.8) before the line is ever dispatched as a
        // command, so a byte pattern like this now draws the line-ending refusal rather than an
        // unknown-command or bad-sequence reply. Which of the three arrives is not the point of this
        // test — that the server answers, survives, and keeps serving is.
        var sawResponse = false;
        for (var i = 0; i < 20 && !sawResponse; i++)
        {
            var line = await s.ReadLineAsync();
            if (line == null) break;
            sawResponse = line.StartsWith("503") || line.StartsWith("502") || line.StartsWith("500");
        }
        Assert.True(sawResponse, "server did not respond to garbage input");

        // …and no receive-loop exception was logged.
        s.Abort();
        Assert.DoesNotContain(logger.Errors, e => e.Contains("[Client receive loop]"));

        // Client B: a normal delivery still works.
        await using var s2 = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await s2.ReadLineAsync());
        await s2.Send("EHLO test.client");
        await s2.ReadResponseAsync();
        await s2.Send("NOOP");
        Assert.StartsWith("250", await s2.ReadLineAsync());
    }

    [Fact]
    public async Task GarbageInput_1MBLine_ServerRespondsAndServesNextClient()
    {
        var port = TestPorts.Allocate();
        using var server = new SMTPServer(
            new[] { new ListeningParameters(IPAddress.Loopback, new[] { port }, null) },
            TestServers.DefaultOptions(), NoopDelivery.Instance);
        server.Start();

        await using var s = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await s.ReadLineAsync());

        // A single ~1 MB line — the reader must buffer it without crashing. (The trailing CRLF is
        // what terminates the line for ReadLineAsync.)
        var bigLine = new byte[1024 * 1024 + 2];
        for (var i = 0; i < 1024 * 1024; i++) bigLine[i] = (byte)'A';
        bigLine[^2] = 0x0D; // CR
        bigLine[^1] = 0x0A; // LF
        await s.SendRaw(bigLine);

        // Before EHLO, the giant "command" hits the default case → bad sequence.
        Assert.Equal("503 5.5.1 EHLO/HELO first.", await s.ReadLineAsync());

        s.Abort();

        await using var s2 = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await s2.ReadLineAsync());
    }

    [Theory]
    [InlineData(0)] // after the greeting
    [InlineData(1)] // right after sending EHLO (before reading its response)
    [InlineData(2)] // mid-DATA (body started, no terminating dot)
    [InlineData(3)] // during gated delivery (handler blocked)
    public async Task AbruptDisconnect_AtEachPhase_NoUnhandledExceptions_ListenerKeepsAccepting(int phase)
    {
        var port = TestPorts.Allocate();
        var logger = new RecordingLogger();

        IMailDelivery delivery;
        GatedForAbort? gated = null;
        if (phase == 3)
        {
            gated = new GatedForAbort();
            delivery = gated;
        }
        else delivery = NoopDelivery.Instance;

        using var server = new SMTPServer(
            new[] { new ListeningParameters(IPAddress.Loopback, new[] { port }, null) },
            TestServers.DefaultOptions(), delivery, logger);
        server.Start();

        await using var s = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await s.ReadLineAsync());

        switch (phase)
        {
            case 1:
                await s.Send("EHLO test.client"); // don't read the response — RST mid-server-write
                break;
            case 2:
                await s.Send("EHLO test.client");
                await s.ReadResponseAsync();
                await s.Send("MAIL FROM:<a@b.com>");
                Assert.StartsWith("250", await s.ReadLineAsync());
                await s.Send("RCPT TO:<c@d.e>");
                Assert.StartsWith("250", await s.ReadLineAsync());
                await s.Send("DATA");
                Assert.StartsWith("354", await s.ReadLineAsync());
                await s.Send("partial body line"); // no terminating dot
                break;
            case 3:
                await s.Send("EHLO test.client");
                await s.ReadResponseAsync();
                await s.Send("MAIL FROM:<a@b.com>");
                Assert.StartsWith("250", await s.ReadLineAsync());
                await s.Send("RCPT TO:<c@d.e>");
                Assert.StartsWith("250", await s.ReadLineAsync());
                await s.Send("DATA");
                Assert.StartsWith("354", await s.ReadLineAsync());
                await s.Send("."); // handler is now gated inside DeliverMessage
                await gated!.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                break;
        }

        s.Abort(); // RST — simulate a client crash
        if (gated != null) gated.Release();
        await Task.Delay(300); // let the server notice and clean up

        // No unhandled receive-loop exceptions were logged…
        Assert.DoesNotContain(logger.Errors, e => e.Contains("[Client receive loop]"));

        // …and the listener keeps accepting new clients.
        await using var s2 = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await s2.ReadLineAsync());
    }

    [Trait("Stress", "concurrency")]
    [Fact]
    public async Task ConcurrencyStress_ParallelSessions_AllDeliveriesSucceed()
    {
        // Regression guard for the Listener.ClientProcessors thread-safety fix (Networking/Listener.cs):
        // 10 parallel clients each open/close 5 sessions, so Add/Remove on the processor list race
        // with each other and with Dispose(). Before the lock+snapshot fix this failed deterministically
        // with NullReferenceException in Listener.Dispose() (corrupted List<> internals).
        var port = TestPorts.Allocate();
        using var server = new SMTPServer(
            new[] { new ListeningParameters(IPAddress.Loopback, new[] { port }, null) },
            TestServers.DefaultOptions(), NoopDelivery.Instance);
        server.Start();

        const int clients = 10;
        const int sessionsPerClient = 5;

        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        async Task RunClientAsync(int id)
        {
            for (var i = 0; i < sessionsPerClient; i++)
            {
                try
                {
                    await using var s = await SmtpSession.ConnectAsync(port);
                    Assert.StartsWith("220 ", await s.ReadLineAsync());
                    await s.Send("EHLO test.client");
                    await s.ReadResponseAsync();
                    await s.Send($"MAIL FROM:<c{id}@x.y>");
                    Assert.Equal("250 2.0.0", await s.ReadLineAsync());
                    await s.Send("RCPT TO:<r@y.z>");
                    Assert.Equal("250 2.1.5", await s.ReadLineAsync());
                    await s.Send("DATA");
                    Assert.StartsWith("354", await s.ReadLineAsync());
                    await s.Send($"m{i}");
                    await s.Send(".");
                    Assert.StartsWith("250", await s.ReadLineAsync());
                }
                catch (Exception e)
                {
                    failures.Add($"client {id}, session {i}: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, clients).Select(id => RunClientAsync(id)));

        Assert.True(failures.IsEmpty, "some sessions failed:\n" + string.Join("\n", failures));
    }

    private sealed class GatedForAbort : IMailDelivery
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _gate = new(0, 1);

        public Task Entered => _entered.Task;
        public void Release() => _gate.Release();

        public async Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction t, CancellationToken ct = default)
        {
            _entered.TrySetResult();
            await _gate.WaitAsync();
            return SmtpDeliveryResult.Ok();
        }

        public Task<UserExistsCodes> DoesUserExist(string email) =>
            Task.FromResult(UserExistsCodes.DestinationAddressValid);
    }
}
