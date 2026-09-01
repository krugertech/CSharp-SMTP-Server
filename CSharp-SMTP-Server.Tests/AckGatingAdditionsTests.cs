using CSharp_SMTP_Server.Interfaces;
using CSharp_SMTP_Server.Protocol;
using CSharp_SMTP_Server.Protocol.Responses;

namespace CSharp_SMTP_Server.Tests;

/// <summary>ACK-gating additions beyond the original AckGatingTests. See TESTING.md.</summary>
public sealed class AckGatingAdditionsTests
{
    private static async Task<(SmtpSession S, SMTPServer Server, ushort Port)> ConnectReadyAsync(
        IMailDelivery delivery, ILogger? logger = null)
    {
        var port = TestPorts.Allocate();
        var server = new SMTPServer(
            new[] { new Networking.ListeningParameters(System.Net.IPAddress.Loopback, new[] { port }, null) },
            TestServers.DefaultOptions(), delivery, logger);
        server.Start();

        var s = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await s.ReadLineAsync());
        await s.Send("EHLO test.client");
        await s.ReadResponseAsync();
        return (s, server, port);
    }

    private static async Task StartTransactionAsync(SmtpSession s)
    {
        await s.Send("MAIL FROM:<a@b.com>");
        Assert.Equal("250 2.0.0", await s.ReadLineAsync());
        await s.Send("RCPT TO:<c@d.e>");
        Assert.Equal("250 2.1.5", await s.ReadLineAsync());
    }

    [Fact]
    public async Task ClientDisconnect_MidDelivery_TokenDoesNotFire_CurrentBehavior()
    {
        // Pin of current behavior (contradicts the original plan expectation!): while delivery is in
        // flight, nothing polls the client socket — the receive loop is parked inside DeliverMessage.
        // A client RST therefore does NOT fire the handler's CancellationToken; it only becomes
        // observable after the handler returns, when the response write fails and the connection is
        // disposed (which cancels the token). If delivery cancellation ever matters, this needs a fix.
        var delivery = new GatedDelivery();
        var (s, server, port) = await ConnectReadyAsync(delivery);
        using (server)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");

            // The handler is now gated inside DeliverMessage.
            await delivery.Entered.WaitAsync(TimeSpan.FromSeconds(5));

            // Abrupt client disconnect (RST)…
            s.Abort();
            await Task.Delay(2000);

            // …the token has NOT fired while the handler was blocked.
            Assert.False(delivery.TokenFired, "handler CancellationToken fired on client disconnect — behavior changed");

            // Let the handler finish; the 250 write fails against the dead connection (handled).
            delivery.Release();
            await Task.Delay(500);

            // The server stays healthy for the next client.
            await using var s2 = await SmtpSession.ConnectAsync(port);
            Assert.StartsWith("220 ", await s2.ReadLineAsync());
        }
    }

    [Fact]
    public async Task DeliveryClone_CarriesDmarcResult_AndHandlerMutationIsSafe()
    {
        // B3 end-to-end (fixed): with DMARC disabled the processor-side transaction carries
        // CheckDisabled, and Clone() now preserves it — the delivered clone reports the real result
        // rather than a misleading None. (The direct unit-level test lives in MailTransactionTests.)
        var delivery = new RecordingDelivery();
        delivery.HandlerOverride = async (tx, ct) =>
        {
            // The delivered clone carries the processor-side result: CheckDisabled, not None.
            Assert.Equal(ValidationResult.CheckDisabled, tx.DMARCValidationResult);

            // B4 end-to-end (fixed): the clone owns its own DeliverTo list, so a handler mutating it
            // cannot reach the processor-side transaction. Each delivery must see exactly the
            // recipients of its own transaction, unpolluted by the previous handler's mutation.
            Assert.Equal(new[] { "c@d.e" }, tx.DeliverTo);

            tx.DeliverTo.Add("mutated@x.y");
            return SmtpDeliveryResult.Ok();
        };

        var (s, server, _) = await ConnectReadyAsync(delivery);
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");
            Assert.StartsWith("250", await s.ReadLineAsync());

            // The session is still fully usable after the handler mutated its clone.
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");
            Assert.StartsWith("250", await s.ReadLineAsync());

            Assert.Equal(2, delivery.Delivered.Count);

            // Both clones kept the handler's mutation on their own copy, and neither leaked into the
            // other — two distinct list instances, each with its own transaction's recipient.
            Assert.NotSame(delivery.Delivered[0].DeliverTo, delivery.Delivered[1].DeliverTo);
            Assert.All(delivery.Delivered, d => Assert.Equal(new[] { "c@d.e", "mutated@x.y" }, d.DeliverTo));
        }
    }

    [Fact]
    public async Task HandlerThrows_OperationCanceledException_Still451_AndLogged()
    {
        var logger = new RecordingLogger();
        var delivery = new RecordingDelivery();
        delivery.HandlerOverride = (tx, ct) => throw new OperationCanceledException("client went away");

        var (s, server, _) = await ConnectReadyAsync(delivery, logger);
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");

            // OperationCanceledException is not special-cased — same 451 as any other exception.
            Assert.Equal("451 4.3.0 Requested action aborted: local error in processing", await s.ReadLineAsync());

            var logged = logger.Errors.FirstOrDefault(e => e.Contains("[DATA] Delivery handler threw before SMTP ACK:"));
            Assert.NotNull(logged);
            Assert.Contains("System.OperationCanceledException", logged!);
            Assert.Contains("client went away", logged);
        }
    }

    [Theory]
    [InlineData(552, "5.4.3", "too big")]
    [InlineData(550, "5.1.1", "unroutable")]
    public async Task CustomStatusReachesWireVerbatim(int code, string enhanced, string message)
    {
        var delivery = new RecordingDelivery();
        delivery.HandlerOverride = (tx, ct) => Task.FromResult(SmtpDeliveryResult.Status(code, enhanced, message));

        var (s, server, _) = await ConnectReadyAsync(delivery);
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");

            Assert.Equal($"{code} {enhanced} {message}", await s.ReadLineAsync());
        }
    }

    [Fact]
    public async Task ConcurrentSessions_NoCrossTalk_HandlersRunInParallel()
    {
        var delivery = new RecordingDelivery();
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int inside = 0;

        // Both handlers must be inside at the same time before either may return — deterministic
        // proof of parallel execution (no cross-talk through shared server state).
        delivery.HandlerOverride = async (tx, ct) =>
        {
            if (Interlocked.Increment(ref inside) == 2) bothEntered.TrySetResult();
            await bothEntered.Task;
            return SmtpDeliveryResult.Ok();
        };

        var port = TestPorts.Allocate();
        using var server = new SMTPServer(
            new[] { new Networking.ListeningParameters(System.Net.IPAddress.Loopback, new[] { port }, null) },
            TestServers.DefaultOptions(), delivery);
        server.Start();

        async Task<string> DeliverAsync(string from, string rcpt, string bodyMarker)
        {
            await using var s = await SmtpSession.ConnectAsync(port);
            Assert.StartsWith("220 ", await s.ReadLineAsync());
            await s.Send("EHLO test.client");
            await s.ReadResponseAsync();

            await s.Send($"MAIL FROM:<{from}>");
            Assert.Equal("250 2.0.0", await s.ReadLineAsync());
            await s.Send($"RCPT TO:<{rcpt}>");
            Assert.Equal("250 2.1.5", await s.ReadLineAsync());
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send($"Subject: {bodyMarker}");
            await s.Send(".");
            return (await s.ReadLineAsync()) ?? "";
        }

        var results = await Task.WhenAll(
            DeliverAsync("one@x.y", "a@y.z", "client-one"),
            DeliverAsync("two@x.y", "b@y.z", "client-two"));
        var (r1, r2) = (results[0], results[1]);

        Assert.StartsWith("250", r1);
        Assert.StartsWith("250", r2);

        // Each delivered transaction carries exactly its own client's data.
        var txOne = delivery.Delivered.Single(tx => tx.From == "one@x.y");
        var txTwo = delivery.Delivered.Single(tx => tx.From == "two@x.y");
        Assert.Equal(new[] { "a@y.z" }, txOne.DeliverTo.ToArray());
        Assert.Contains("client-one", txOne.RawBody);
        Assert.DoesNotContain("client-two", txOne.RawBody);
        Assert.Equal(new[] { "b@y.z" }, txTwo.DeliverTo.ToArray());
        Assert.Contains("client-two", txTwo.RawBody);
    }

    private sealed class GatedDelivery : IMailDelivery
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _gate = new(0, 1);
        private int _tokenFiredFlag;

        public Task Entered => _entered.Task;
        public void Release() => _gate.Release();

        /// <summary>Live view: true once the server's connection token has fired (readable while gated).</summary>
        public bool TokenFired => Volatile.Read(ref _tokenFiredFlag) == 1;

        public async Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction t, CancellationToken ct = default)
        {
            _entered.TrySetResult();

            // Records whether the server's connection token ever fires while we are blocked.
            var delay = Task.Delay(Timeout.Infinite, ct);
            delay.ContinueWith(_ => Interlocked.Exchange(ref _tokenFiredFlag, 1));

            await _gate.WaitAsync();
            return SmtpDeliveryResult.Ok();
        }

        public Task<UserExistsCodes> DoesUserExist(string email) =>
            Task.FromResult(UserExistsCodes.DestinationAddressValid);
    }
}
