using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CSharp_SMTP_Server;
using CSharp_SMTP_Server.Interfaces;
using CSharp_SMTP_Server.Networking;
using CSharp_SMTP_Server.Protocol.Responses;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Integration tests that prove ACK-gating: the server must not send 250 OK for DATA until the
/// delivery handler has returned, and must map delivery failures/exceptions to the correct SMTP codes.
///
/// Each test spins up a real SMTPServer on a loopback port, speaks raw SMTP over TCP, and inspects
/// the response line that follows the terminating "." of the DATA command.
/// </summary>
public sealed class AckGatingTests : IDisposable
{
    // ─── helpers ────────────────────────────────────────────────────────────────

    private static SMTPServer BuildServer(IMailDelivery delivery, ushort port)
    {
        var opts = new ServerOptions(validateSPF: false, validateDMARC: false, dnsServerEndpoint: null);
        opts.ServerName = "test.local";

        var parameters = new[]
        {
            new ListeningParameters(IPAddress.Loopback, new[] { port }, null)
        };

        var server = new SMTPServer(parameters, opts, delivery);
        server.Start();
        return server;
    }

    /// <summary>
    /// Opens a raw SMTP connection (shared <see cref="SmtpSession"/> helper), drives
    /// EHLO → MAIL FROM → RCPT TO → DATA → body → "." and returns the full response line that
    /// the server sends after ".". Every read is bounded by a 10 s timeout so a non-responsive
    /// server cannot cause the test runner to hang indefinitely.
    /// </summary>
    private static async Task<string> SendMailAndGetDataResponseAsync(ushort port)
    {
        await using var s = await SmtpSession.ConnectAsync(port);

        Assert.StartsWith("220 ", await s.ReadLineAsync());  // greeting
        await s.Send("EHLO test.client");
        await s.ReadResponseAsync();                         // multi-line 250
        await s.Send("MAIL FROM: <sender@example.com>");
        await s.ReadLineAsync();                             // 250 2.0.0
        await s.Send("RCPT TO: <rcpt@example.com>");
        await s.ReadLineAsync();                             // 250 2.1.5
        await s.Send("DATA");
        Assert.StartsWith("354", await s.ReadLineAsync());

        // minimal RFC 5322 body
        await s.Send("Subject: test");
        await s.Send("From: sender@example.com");
        await s.Send("To: rcpt@example.com");
        await s.Send("");
        await s.Send("Hello");
        await s.Send(".");                                  // end of data

        return (await s.ReadLineAsync())                     // ← the ACK we are testing
               ?? throw new InvalidOperationException("Server closed the connection unexpectedly.");
    }

    public void Dispose() { }

    // ─── test delivery implementations ──────────────────────────────────────────

    private sealed class OkDelivery : IMailDelivery
    {
        public int CallCount;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Resolves when the handler has been entered (but not yet returned).</summary>
        public Task Started => _started.Task;

        public Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction t, CancellationToken ct = default)
        {
            Interlocked.Increment(ref CallCount);
            _started.TrySetResult();
            return Task.FromResult(SmtpDeliveryResult.Ok("Accepted"));
        }

        public Task<UserExistsCodes> DoesUserExist(string email) =>
            Task.FromResult(UserExistsCodes.DestinationAddressValid);
    }

    private sealed class SlowDelivery : IMailDelivery
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate    = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        /// <summary>Allows the handler to return 250 OK.</summary>
        public void Release() => _gate.TrySetResult();

        public async Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction t, CancellationToken ct = default)
        {
            _entered.TrySetResult();
            await _gate.Task;                               // pause here until Release() is called
            return SmtpDeliveryResult.Ok();
        }

        public Task<UserExistsCodes> DoesUserExist(string email) =>
            Task.FromResult(UserExistsCodes.DestinationAddressValid);
    }

    private sealed class TempFailDelivery : IMailDelivery
    {
        public Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction t, CancellationToken ct = default) =>
            Task.FromResult(SmtpDeliveryResult.TemporaryFailure("Busy, try again"));

        public Task<UserExistsCodes> DoesUserExist(string email) =>
            Task.FromResult(UserExistsCodes.DestinationAddressValid);
    }

    private sealed class ThrowingDelivery : IMailDelivery
    {
        public Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction t, CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated crash");

        public Task<UserExistsCodes> DoesUserExist(string email) =>
            Task.FromResult(UserExistsCodes.DestinationAddressValid);
    }

    private sealed class CountingDelivery : IMailDelivery
    {
        public int CallCount;

        public Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction t, CancellationToken ct = default)
        {
            Interlocked.Increment(ref CallCount);
            return Task.FromResult(SmtpDeliveryResult.Ok());
        }

        public Task<UserExistsCodes> DoesUserExist(string email) =>
            Task.FromResult(UserExistsCodes.DestinationAddressValid);
    }

    // ─── tests ──────────────────────────────────────────────────────────────────

    /// <summary>Test 1 + 5: DATA returns 250 only after delivery handler completes; handler called exactly once.</summary>
    [Fact]
    public async Task Data_Returns250_AfterDeliveryHandlerCompletes()
    {
        var delivery = new OkDelivery();
        var port = TestPorts.Allocate();
        using var server = BuildServer(delivery, port);

        var response = await SendMailAndGetDataResponseAsync(port);

        Assert.StartsWith("250", response);
        Assert.Equal(1, delivery.CallCount);
    }

    /// <summary>Test 2: 250 is NOT sent while the handler is still running.</summary>
    [Fact]
    public async Task Data_DoesNotReturn250_WhileHandlerIsRunning()
    {
        var delivery = new SlowDelivery();
        var port = TestPorts.Allocate();
        using var server = BuildServer(delivery, port);

        // Start the full SMTP session in the background.
        var responseTask = SendMailAndGetDataResponseAsync(port);

        // Wait until the delivery handler has been entered.
        await delivery.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        // Give 200 ms — if the server sent 250 before Release() it will arrive now.
        var completed = await Task.WhenAny(responseTask, Task.Delay(200));

        // The response must NOT have arrived yet.
        Assert.NotSame(responseTask, completed);

        // Now let the handler finish and verify 250 does arrive.
        delivery.Release();
        var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.StartsWith("250", response);
    }

    /// <summary>Test 3: Temporary delivery failure → 451 response.</summary>
    [Fact]
    public async Task Data_Returns451_OnTemporaryDeliveryFailure()
    {
        var delivery = new TempFailDelivery();
        var port = TestPorts.Allocate();
        using var server = BuildServer(delivery, port);

        var response = await SendMailAndGetDataResponseAsync(port);

        Assert.StartsWith("451", response);
    }

    /// <summary>Test 4: Delivery handler throws → 451 response.</summary>
    [Fact]
    public async Task Data_Returns451_WhenHandlerThrows()
    {
        var delivery = new ThrowingDelivery();
        var port = TestPorts.Allocate();
        using var server = BuildServer(delivery, port);

        var response = await SendMailAndGetDataResponseAsync(port);

        Assert.StartsWith("451", response);
    }

    /// <summary>Test 5 (standalone): Delivery handler called exactly once per DATA transaction.</summary>
    [Fact]
    public async Task DeliveryHandler_CalledExactlyOnce_PerTransaction()
    {
        var delivery = new CountingDelivery();
        var port = TestPorts.Allocate();
        using var server = BuildServer(delivery, port);

        await SendMailAndGetDataResponseAsync(port);

        Assert.Equal(1, delivery.CallCount);
    }

    /// <summary>Test 6: No fire-and-forget delivery — response arrives only after handler returns.</summary>
    [Fact]
    public async Task Data_NoFireAndForget_ResponseArrivesAfterHandler()
    {
        // Uses SlowDelivery gate; response must not arrive before Release().
        var delivery = new SlowDelivery();
        var port = TestPorts.Allocate();
        using var server = BuildServer(delivery, port);

        var responseTask = SendMailAndGetDataResponseAsync(port);
        await delivery.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        // 300 ms window — if fire-and-forget, 250 would arrive immediately.
        var raced = await Task.WhenAny(responseTask, Task.Delay(300));
        Assert.True(!ReferenceEquals(responseTask, raced), "250 arrived before delivery handler completed — fire-and-forget detected");

        delivery.Release();
        var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.StartsWith("250", response);
    }
}
