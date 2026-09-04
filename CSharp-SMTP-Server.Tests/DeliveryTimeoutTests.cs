using CSharp_SMTP_Server.Interfaces;
using CSharp_SMTP_Server.Networking;
using CSharp_SMTP_Server.Protocol.Responses;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Integration tests for <see cref="ServerOptions.DeliveryTimeout"/>. See
/// plan-graceful-shutdown-delivery-cancel.md for the design rationale — in particular, why the
/// deadline must be authoritative over whatever the handler returns rather than inferred from a
/// thrown <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class DeliveryTimeoutTests
{
    private static async Task<(SmtpSession S, SMTPServer Server, ushort Port)> ConnectReadyAsync(
        IMailDelivery delivery, ServerOptions options, ILogger? logger = null)
    {
        var port = TestPorts.Allocate();
        var server = new SMTPServer(
            new[] { new ListeningParameters(System.Net.IPAddress.Loopback, new[] { port }, null) },
            options, delivery, logger);
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

    private static ServerOptions OptionsWithTimeout(TimeSpan timeout) =>
        new(false, false, null) { ServerName = "test.local", DeliveryTimeout = timeout };

    // ─── test delivery implementations ──────────────────────────────────────────

    /// <summary>Waits on its cancellation token; reports whether the token was observed cancelled.</summary>
    private sealed class CancellationAwareDelivery : IMailDelivery
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task;
        public bool TokenWasCancelled { get; private set; }

        public async Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction t, CancellationToken ct = default)
        {
            _entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                TokenWasCancelled = true;
                throw;
            }

            return SmtpDeliveryResult.Ok();
        }

        public Task<UserExistsCodes> DoesUserExist(string email) =>
            Task.FromResult(UserExistsCodes.DestinationAddressValid);
    }

    /// <summary>Catches cancellation and returns Ok anyway — the section-3 regression case.</summary>
    private sealed class CatchesCancellationAndReturnsOkDelivery : IMailDelivery
    {
        public async Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction t, CancellationToken ct = default)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                // Swallow it and report success anyway — the deadline must still win.
            }

            return SmtpDeliveryResult.Ok();
        }

        public Task<UserExistsCodes> DoesUserExist(string email) =>
            Task.FromResult(UserExistsCodes.DestinationAddressValid);
    }

    /// <summary>Catches cancellation and returns TemporaryFailure — deadline must still classify as timeout.</summary>
    private sealed class CatchesCancellationAndReturnsTempFailDelivery : IMailDelivery
    {
        public async Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction t, CancellationToken ct = default)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                // fall through
            }

            return SmtpDeliveryResult.TemporaryFailure("busy");
        }

        public Task<UserExistsCodes> DoesUserExist(string email) =>
            Task.FromResult(UserExistsCodes.DestinationAddressValid);
    }

    /// <summary>
    /// Registers a cancellation callback that completes its own TaskCompletionSource synchronously
    /// inside the callback and returns Ok from the resumed await — pins the callback-ordering race:
    /// classification must not be gated behind Register(), because the handler's continuation can run
    /// before ours.
    /// </summary>
    private sealed class SynchronousCallbackRaceDelivery : IMailDelivery
    {
        public async Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction t, CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => tcs.TrySetResult());
            await tcs.Task;
            return SmtpDeliveryResult.Ok();
        }

        public Task<UserExistsCodes> DoesUserExist(string email) =>
            Task.FromResult(UserExistsCodes.DestinationAddressValid);
    }

    /// <summary>
    /// Registers a cancellation callback that throws. A supported IMailDelivery pattern in principle
    /// (cleanup logic in a Register callback is a legitimate use), but a buggy or unlucky one can throw
    /// — and the deadline's own cancellation must not let that escape onto an unguarded timer thread and
    /// crash the whole server process; it must be confined to failing this one delivery.
    /// </summary>
    private sealed class ThrowingRegisteredCallbackDelivery : IMailDelivery
    {
        public async Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction t, CancellationToken ct = default)
        {
            using var registration = ct.Register(() => throw new InvalidOperationException("registered callback deliberately throws"));
            await Task.Delay(Timeout.Infinite, ct);
            return SmtpDeliveryResult.Ok(); // unreachable; Task.Delay throws OperationCanceledException first
        }

        public Task<UserExistsCodes> DoesUserExist(string email) =>
            Task.FromResult(UserExistsCodes.DestinationAddressValid);
    }

    /// <summary>Ignores its token entirely; completes only when released.</summary>
    private sealed class IgnoresCancellationDelivery : IMailDelivery
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task;
        public void Release() => _gate.TrySetResult();

        public async Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction t, CancellationToken ct = default)
        {
            _entered.TrySetResult();
            await _gate.Task; // never observes ct
            return SmtpDeliveryResult.Ok();
        }

        public Task<UserExistsCodes> DoesUserExist(string email) =>
            Task.FromResult(UserExistsCodes.DestinationAddressValid);
    }

    private sealed class FastOkDelivery : IMailDelivery
    {
        public Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction t, CancellationToken ct = default) =>
            Task.FromResult(SmtpDeliveryResult.Ok());

        public Task<UserExistsCodes> DoesUserExist(string email) =>
            Task.FromResult(UserExistsCodes.DestinationAddressValid);
    }

    /// <summary>
    /// Ignores its token and completes shortly AFTER the configured deadline via a plain Task.Delay —
    /// the exact shape that, against a Timer-flag classification (an earlier version of this code
    /// used one), let the deadline lose its own race and produce a false 250 in roughly 1 in 8 trials
    /// under ordinary scheduling, confirmed by direct measurement.
    /// </summary>
    private sealed class CompletesJustAfterDeadlineDelivery : IMailDelivery
    {
        private readonly TimeSpan _completeAfter;
        public CompletesJustAfterDeadlineDelivery(TimeSpan completeAfter) => _completeAfter = completeAfter;

        public async Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction t, CancellationToken ct = default)
        {
            await Task.Delay(_completeAfter); // ignores ct entirely
            return SmtpDeliveryResult.Ok();
        }

        public Task<UserExistsCodes> DoesUserExist(string email) =>
            Task.FromResult(UserExistsCodes.DestinationAddressValid);
    }

    /// <summary>
    /// Throws synchronously — before returning any Task at all — rather than via a faulted or
    /// canceled Task. Since SMTPServer.DeliverMessage is a direct, non-async pass-through to
    /// EmailReceivedAsync, this throws out of the call expression itself
    /// (<c>processor.Server.DeliverMessage(...)</c>) rather than out of an awaited Task.
    /// </summary>
    private sealed class SynchronousThrowDelivery : IMailDelivery
    {
        public Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction t, CancellationToken ct = default) =>
            throw new OperationCanceledException("synchronous throw before returning a Task");

        public Task<UserExistsCodes> DoesUserExist(string email) =>
            Task.FromResult(UserExistsCodes.DestinationAddressValid);
    }

    // ─── tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SlowHandler_ShortDeadline_Returns451_AndTokenObservedCancelled()
    {
        var delivery = new CancellationAwareDelivery();
        var (s, server, _) = await ConnectReadyAsync(delivery, OptionsWithTimeout(TimeSpan.FromMilliseconds(200)));
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");

            Assert.Equal("451 4.4.7 Requested action aborted: delivery timeout exceeded", await s.ReadLineAsync());
            await delivery.Entered;
            Assert.True(delivery.TokenWasCancelled);
        }
    }

    [Fact]
    public async Task HandlerCatchesCancellation_ReturnsOkAfterDeadline_Still451NotOk()
    {
        // The regression test for section 3: without settling classification from monotonic elapsed
        // time, this would incorrectly send 250 because the handler's own returned Ok would win.
        var delivery = new CatchesCancellationAndReturnsOkDelivery();
        var (s, server, _) = await ConnectReadyAsync(delivery, OptionsWithTimeout(TimeSpan.FromMilliseconds(200)));
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");

            Assert.Equal("451 4.4.7 Requested action aborted: delivery timeout exceeded", await s.ReadLineAsync());
        }
    }

    [Fact]
    public async Task SynchronousCancellationCallback_ResumesAwaitWithOk_Still451()
    {
        // Pins the callback-ordering race: a synchronous cancellation callback that resumes the await
        // ahead of our own classification must not let Ok slip through.
        var delivery = new SynchronousCallbackRaceDelivery();
        var (s, server, _) = await ConnectReadyAsync(delivery, OptionsWithTimeout(TimeSpan.FromMilliseconds(200)));
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");

            Assert.Equal("451 4.4.7 Requested action aborted: delivery timeout exceeded", await s.ReadLineAsync());
        }
    }

    [Fact]
    public async Task HandlerReturnsTemporaryFailure_AfterDeadline_Still451WithTimeoutStatus()
    {
        var delivery = new CatchesCancellationAndReturnsTempFailDelivery();
        var (s, server, _) = await ConnectReadyAsync(delivery, OptionsWithTimeout(TimeSpan.FromMilliseconds(200)));
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");

            // Deadline classification wins over the handler's own (also-451) result: the enhanced
            // status is the timeout's 4.4.7, not the handler's 4.3.0.
            Assert.Equal("451 4.4.7 Requested action aborted: delivery timeout exceeded", await s.ReadLineAsync());
        }
    }

    [Fact]
    public async Task HandlerCompletesInsideDeadline_ReturnsItsOwnResult()
    {
        var delivery = new FastOkDelivery();
        var (s, server, _) = await ConnectReadyAsync(delivery, OptionsWithTimeout(TimeSpan.FromSeconds(30)));
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");

            Assert.StartsWith("250", await s.ReadLineAsync());

            // No linked-source leak: the session remains fully usable for a second transaction.
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");
            Assert.StartsWith("250", await s.ReadLineAsync());
        }
    }

    /// <summary>
    /// Regression test for the WhenAny-based race (replacing an earlier, unsound timestamp-comparison
    /// design): a handler backed directly by a <see cref="TaskCompletionSource{TResult}"/> created with
    /// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> — deliberately the shape that
    /// broke the previous design, since it dispatches every continuation on its task to the thread pool
    /// rather than running any of them inline — completed with <c>Ok</c> strictly after the deadline.
    /// Classification must still answer 451 4.4.7, proving the decision no longer depends on which
    /// continuation happens to run first.
    /// </summary>
    private sealed class AsyncContinuationDelivery : IMailDelivery
    {
        private readonly TaskCompletionSource<SmtpDeliveryResult> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteWithOk() => _tcs.TrySetResult(SmtpDeliveryResult.Ok());

        public Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction t, CancellationToken ct = default) =>
            _tcs.Task;

        public Task<UserExistsCodes> DoesUserExist(string email) =>
            Task.FromResult(UserExistsCodes.DestinationAddressValid);
    }

    [Fact]
    public async Task HandlerBackedByAsyncContinuationTcs_CompletesAfterDeadline_Still451NotOk()
    {
        var deadline = TimeSpan.FromMilliseconds(150);
        var delivery = new AsyncContinuationDelivery();
        var (s, server, _) = await ConnectReadyAsync(delivery, OptionsWithTimeout(deadline));
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");

            // Complete strictly after the deadline, from a background thread — never inline with
            // anything TransactionCommands is doing, so this cannot accidentally win by running on
            // the same thread that would otherwise service the WhenAny continuation.
            _ = Task.Run(async () =>
            {
                await Task.Delay(deadline + TimeSpan.FromMilliseconds(150));
                delivery.CompleteWithOk();
            });

            Assert.Equal("451 4.4.7 Requested action aborted: delivery timeout exceeded",
                await s.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public async Task HandlerBackedByAsyncContinuationTcs_CompletesInsideDeadline_ReturnsOk()
    {
        // Companion to the above: the same RunContinuationsAsynchronously shape must not itself cause
        // false timeouts when the handler genuinely finishes in time.
        var deadline = TimeSpan.FromSeconds(30);
        var delivery = new AsyncContinuationDelivery();
        var (s, server, _) = await ConnectReadyAsync(delivery, OptionsWithTimeout(deadline));
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");

            _ = Task.Run(delivery.CompleteWithOk);

            Assert.StartsWith("250", await s.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public async Task ThrowingCancellationCallback_OnDeadlineExpiry_DoesNotCrashServer_Answers451()
    {
        // Regression test: the deadline's own cancellation must never let a handler's misbehaving
        // Token.Register callback escape onto an unguarded timer thread. Direct experiment confirmed
        // that routing this through CancellationTokenSource's own built-in timer (an earlier version
        // of this code did exactly that) crashes the whole process — an unhandled AggregateException
        // from CancellationTokenSource.ExecuteCallbackHandlers on a ThreadPool timer thread, entirely
        // outside any try/catch in TransactionCommands. If this test's process is still alive to report
        // a result at all (rather than the test host crashing), the fix is holding.
        var deadline = TimeSpan.FromMilliseconds(150);
        var delivery = new ThrowingRegisteredCallbackDelivery();
        var (s, server, port) = await ConnectReadyAsync(delivery, OptionsWithTimeout(deadline));
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");

            Assert.Equal("451 4.4.7 Requested action aborted: delivery timeout exceeded",
                await s.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));

            // The server process (and this connection's server instance) survived the throw and is
            // still usable for a new client — the confining fix works, not merely "didn't crash yet".
            await using var s2 = await SmtpSession.ConnectAsync(port);
            Assert.StartsWith("220 ", await s2.ReadLineAsync());
        }
    }

    [Theory]
    // 20 repeated trials: the Timer-flag classification this replaced was measured, by direct probe
    // against this exact pattern (handler completing via Task.Delay shortly after the deadline, under
    // ordinary — not even adversarial — scheduling), to produce a false 250 in 27/200 trials (~13.5%).
    // With that failure rate, 20 independent trials fail to catch it only ~4.6% of the time; this many
    // repeats within one xUnit run gives the regression a real chance to resurface if reintroduced.
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    [InlineData(10)] [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)]
    [InlineData(15)] [InlineData(16)] [InlineData(17)] [InlineData(18)] [InlineData(19)]
    public async Task DeadlineNeverLosesItsOwnRace_HandlerCompletingJustAfterDeadline_Always451(int trial)
    {
        var deadline = TimeSpan.FromMilliseconds(30);
        var delivery = new CompletesJustAfterDeadlineDelivery(deadline + TimeSpan.FromMilliseconds(5));
        var (s, server, _) = await ConnectReadyAsync(delivery, OptionsWithTimeout(deadline));
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");

            var response = await s.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(response == "451 4.4.7 Requested action aborted: delivery timeout exceeded",
                $"trial {trial}: expected 451 4.4.7, got '{response}'");
        }
    }

    [Fact]
    public async Task HandlerThrowsSynchronously_WithDeliveryTimeoutEnabled_Still451NotHang()
    {
        // Regression test: DeliverMessage is invoked inside the same try that awaits deliveryTask, not
        // before it. A handler that throws OperationCanceledException synchronously — out of the call
        // expression itself, never producing a Task to await — must still be classified as a delivery
        // exception and answered 451 4.3.0, not silently propagate past the outer
        // catch (Exception ex) when (!(ex is OperationCanceledException)) and leave the client hanging.
        // Exercised specifically with DeliveryTimeout enabled, since that is the code path where
        // deliveryTask is assigned inside the WhenAny-racing branch.
        var logger = new RecordingLogger();
        var delivery = new SynchronousThrowDelivery();
        var (s, server, _) = await ConnectReadyAsync(delivery, OptionsWithTimeout(TimeSpan.FromSeconds(30)), logger);
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");

            Assert.Equal("451 4.3.0 Requested action aborted: local error in processing",
                await s.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains(logger.Errors, e => e.Contains("[DATA] Delivery handler threw before SMTP ACK:"));

            // The session survives a synchronous throw and remains usable for the next message.
            await StartTransactionAsync(s);
        }
    }

    [Fact]
    public async Task DeliveryTimeoutZero_SlowHandler_CompletesWith250()
    {
        // Pins the opt-in default: DeliveryTimeout == Zero must behave exactly as before — no linked
        // source, no deadline, the handler is simply awaited.
        var delivery = new IgnoresCancellationDelivery();
        var (s, server, _) = await ConnectReadyAsync(delivery, OptionsWithTimeout(TimeSpan.Zero));
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");

            await delivery.Entered.WaitAsync(TimeSpan.FromSeconds(5));

            // No response yet — the handler has not been released.
            var responseTask = s.ReadLineAsync();
            var raced = await Task.WhenAny(responseTask, Task.Delay(300));
            Assert.NotSame(responseTask, raced);

            delivery.Release();
            Assert.StartsWith("250", await responseTask.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task DeliveryTimeoutZero_ConnectionCancelled_TodaysBehaviorExactly()
    {
        // Guards the compatibility claim in section 2: with the option off, a thrown exception (which
        // is what an observed connection-teardown looks like to the handler) still maps to 451 4.3.0,
        // unchanged from before this feature existed.
        var logger = new RecordingLogger();
        var delivery = new RecordingDelivery
        {
            HandlerOverride = (tx, ct) => throw new OperationCanceledException("connection torn down")
        };
        var (s, server, _) = await ConnectReadyAsync(delivery, OptionsWithTimeout(TimeSpan.Zero), logger);
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");

            Assert.Equal("451 4.3.0 Requested action aborted: local error in processing", await s.ReadLineAsync());
            Assert.Contains(logger.Errors, e => e.Contains("[DATA] Delivery handler threw before SMTP ACK:"));
        }
    }

    [Fact]
    public async Task RemoteDisconnect_DuringSlowDelivery_NoUnhandledException()
    {
        // The receive loop is parked inside the delivery await, so a remote RST is not independently
        // observed (see AckGatingAdditionsTests.ClientDisconnect_MidDelivery_TokenDoesNotFire). This
        // test only asserts the server stays healthy — it deliberately does not assert whether a
        // write was attempted, since the deadline's 451 is best-effort against a peer that may
        // already be gone.
        var delivery = new CancellationAwareDelivery();
        var (s, server, port) = await ConnectReadyAsync(delivery, OptionsWithTimeout(TimeSpan.FromMilliseconds(200)));
        using (server)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");

            await delivery.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            s.Abort();

            // Give the deadline time to fire and the best-effort write to fail harmlessly.
            await Task.Delay(1000);

            // Server is still healthy for a new client.
            await using var s2 = await SmtpSession.ConnectAsync(port);
            Assert.StartsWith("220 ", await s2.ReadLineAsync());
        }
    }

    [Fact]
    public async Task LocalTeardown_DuringDelivery_NoWriteAttempted()
    {
        // Only locally-observed teardown (ConnectionToken cancelled) takes the no-write branch: here
        // the server itself is disposed while a delivery is in flight and short-deadline timeout is
        // also armed, so both classifications are in play — ConnectionToken must win.
        var delivery = new CancellationAwareDelivery();
        var port = TestPorts.Allocate();
        var server = new SMTPServer(
            new[] { new ListeningParameters(System.Net.IPAddress.Loopback, new[] { port }, null) },
            OptionsWithTimeout(TimeSpan.FromSeconds(30)), delivery);
        server.Start();

        await using var s = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await s.ReadLineAsync());
        await s.Send("EHLO test.client");
        await s.ReadResponseAsync();
        await StartTransactionAsync(s);
        await s.Send("DATA");
        Assert.StartsWith("354", await s.ReadLineAsync());
        await s.Send(".");

        await delivery.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        // Local teardown: dispose the server while delivery is still in flight.
        server.Dispose();

        // No response should arrive — the no-write branch. A short bounded wait proves absence
        // rather than asserting on a hang.
        var responseTask = s.ReadLineAsync();
        var raced = await Task.WhenAny(responseTask, Task.Delay(1000));
        if (raced == responseTask)
        {
            // If a line does arrive it must not be a 451 timeout write; either EOF (null) or an
            // exception from the torn-down socket is acceptable, but a written response is not.
            var line = await responseTask;
            Assert.Null(line);
        }
    }

    [Fact]
    public async Task HandlerIgnoresCancellation_SessionNotBoundedByDeadline_DocumentedBehavior()
    {
        // Documents the known limitation: a handler that never observes its token is still awaited to
        // completion, because delivery.Body must stay alive until it returns. The deadline bounds
        // whether the message is accepted, not how long the session lasts.
        var delivery = new IgnoresCancellationDelivery();
        var (s, server, _) = await ConnectReadyAsync(delivery, OptionsWithTimeout(TimeSpan.FromMilliseconds(200)));
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");

            await delivery.Entered.WaitAsync(TimeSpan.FromSeconds(5));

            // The 200 ms deadline has long since passed, but nothing is written yet — the handler is
            // still running and is not abandoned.
            await Task.Delay(500);
            var responseTask = s.ReadLineAsync();
            var raced = await Task.WhenAny(responseTask, Task.Delay(300));
            Assert.NotSame(responseTask, raced);

            // Once released, classification runs and reports the timeout, proving the deadline was
            // recorded even though the handler never noticed.
            delivery.Release();
            Assert.Equal("451 4.4.7 Requested action aborted: delivery timeout exceeded",
                await responseTask.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void NegativeDeliveryTimeout_IsNotRejectedByOptionsItself()
    {
        // ServerOptions is a plain field, matching its existing style (see MessageCharactersLimit,
        // RecipientsLimit) — validation happens at the point of use, not at assignment.
        var options = new ServerOptions(false, false, null) { DeliveryTimeout = TimeSpan.FromSeconds(-1) };
        Assert.Equal(TimeSpan.FromSeconds(-1), options.DeliveryTimeout);
    }

    [Fact]
    public async Task NegativeDeliveryTimeout_RejectedAtPointOfUse_451NotHang()
    {
        // Rejected at the point of use, before the handler is ever invoked. The client sent a
        // complete DATA transaction and is owed a final response — a 451, not a silent hang — and
        // the transferred message body must not leak (see BodyDisposed on delivery below; the body's
        // temp file is asserted indirectly via the fixture's disposal, and directly by
        // OversizedDeliveryTimeout_RejectedAtPointOfUse_BodyDisposed).
        var logger = new RecordingLogger();
        var delivery = new FastOkDelivery();
        var (s, server, _) = await ConnectReadyAsync(delivery, new ServerOptions(false, false, null)
        {
            ServerName = "test.local",
            DeliveryTimeout = TimeSpan.FromSeconds(-1)
        }, logger);
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");

            Assert.Equal("451 4.3.0 Requested action aborted: local error in processing", await s.ReadLineAsync());
            Assert.Contains(logger.Errors, e => e.Contains(nameof(ServerOptions.DeliveryTimeout)));

            // The session survives a rejected configuration and remains usable for the next message —
            // proof the failure path did not leave the connection or its resources in a bad state.
            await StartTransactionAsync(s);
        }
    }

    [Fact]
    public async Task OversizedDeliveryTimeout_RejectedAtPointOfUse_BodyDisposed()
    {
        // CancelAfter throws ArgumentOutOfRangeException for a TimeSpan whose millisecond conversion
        // overflows its internal uint (at/beyond ~49.7 days, confirmed by probing the actual runtime
        // rather than assumed) — a configuration mistake that is easy to make with e.g.
        // TimeSpan.FromDays(365). At the point this throws, delivery.Body is already the sole owner
        // of the spilled message (Clone() transferred it from processor.Transaction), so the
        // regression this guards is a leaked temp file plus a client left waiting forever.
        var logger = new RecordingLogger();
        var delivery = new FastOkDelivery();
        var (s, server, _) = await ConnectReadyAsync(delivery, new ServerOptions(false, false, null)
        {
            ServerName = "test.local",
            DeliveryTimeout = TimeSpan.FromMilliseconds(uint.MaxValue)
        }, logger);
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");

            Assert.Equal("451 4.3.0 Requested action aborted: local error in processing", await s.ReadLineAsync());
            Assert.Contains(logger.Errors, e => e.Contains("Delivery setup failed before dispatch"));

            // No leaked resource left the connection unusable.
            await StartTransactionAsync(s);
        }
    }

}
