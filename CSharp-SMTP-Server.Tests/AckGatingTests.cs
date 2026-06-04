using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
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

    private static ushort AllocatePort()
    {
        var tmp = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            tmp.Start();
            return (ushort)((IPEndPoint)tmp.LocalEndpoint).Port;
        }
        finally
        {
            tmp.Stop();
        }
    }

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

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Opens a raw SMTP connection, drives EHLO → MAIL FROM → RCPT TO → DATA → body → "."
    /// and returns the full response line that the server sends after ".".
    /// Every read is bounded by <see cref="ReadTimeout"/> so a non-responsive server cannot
    /// cause the test runner to hang indefinitely.
    /// </summary>
    private static async Task<string> SendMailAndGetDataResponseAsync(ushort port)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port).WaitAsync(ReadTimeout);

        using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

        async Task<string> ReadLine()
        {
            var line = await reader.ReadLineAsync().WaitAsync(ReadTimeout);
            return line ?? throw new InvalidOperationException("Server closed the connection unexpectedly.");
        }

        async Task Send(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
            await stream.WriteAsync(bytes).AsTask().WaitAsync(ReadTimeout);
        }

        await ReadLine();                                   // 220 greeting
        await Send("EHLO test.client");
        while (true) { var l = await ReadLine(); if (l.StartsWith("250 ")) break; } // multi-line EHLO
        await Send("MAIL FROM: <sender@example.com>");
        await ReadLine();                                   // 250 2.0.0
        await Send("RCPT TO: <rcpt@example.com>");
        await ReadLine();                                   // 250 2.1.5
        await Send("DATA");
        await ReadLine();                                   // 354

        // minimal RFC 5322 body
        await Send("Subject: test");
        await Send("From: sender@example.com");
        await Send("To: rcpt@example.com");
        await Send("");
        await Send("Hello");
        await Send(".");                                    // end of data

        return await ReadLine();                            // ← the ACK we are testing
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
        var port = AllocatePort();
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
        var port = AllocatePort();
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
        var port = AllocatePort();
        using var server = BuildServer(delivery, port);

        var response = await SendMailAndGetDataResponseAsync(port);

        Assert.StartsWith("451", response);
    }

    /// <summary>Test 4: Delivery handler throws → 451 response.</summary>
    [Fact]
    public async Task Data_Returns451_WhenHandlerThrows()
    {
        var delivery = new ThrowingDelivery();
        var port = AllocatePort();
        using var server = BuildServer(delivery, port);

        var response = await SendMailAndGetDataResponseAsync(port);

        Assert.StartsWith("451", response);
    }

    /// <summary>Test 5 (standalone): Delivery handler called exactly once per DATA transaction.</summary>
    [Fact]
    public async Task DeliveryHandler_CalledExactlyOnce_PerTransaction()
    {
        var delivery = new CountingDelivery();
        var port = AllocatePort();
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
        var port = AllocatePort();
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
