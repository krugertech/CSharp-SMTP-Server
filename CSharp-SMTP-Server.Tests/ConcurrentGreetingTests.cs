using System.Net;
using System.Net.Sockets;
using CSharp_SMTP_Server.Interfaces;
using CSharp_SMTP_Server.Networking;
using CSharp_SMTP_Server.Protocol.Responses;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Regression test for a pre-existing upstream bug: the ClientProcessor constructor used to block.
/// When the greeting write completed synchronously (typical on Windows), Init() ran inline in the ctor
/// and parked inside Receive()'s EndOfStream check until that client sent data or disconnected — which
/// consumed the Listener's accept thread, so a second concurrent client never received its 220 greeting.
/// The fix runs Init on the thread pool; both clients must be greeted while the first stays idle.
/// </summary>
public sealed class ConcurrentGreetingTests : IDisposable
{
    private SMTPServer? _server;

    [Fact]
    public async Task TwoConcurrentClients_BothReceiveGreeting_FirstStaysIdle()
    {
        var port = AllocatePort();
        _server = new SMTPServer(
            new[] { new ListeningParameters(IPAddress.Loopback, new ushort[] { port }, null) },
            new ServerOptions(false, false),
            new NoopDelivery());
        _server.Start();

        using var c1 = new TcpClient();
        await c1.ConnectAsync(IPAddress.Loopback, port);
        var r1 = new StreamReader(c1.GetStream(), leaveOpen: true);
        Assert.StartsWith("220 ", await ReadLineWithTimeout(r1)); // client 1 greeted, then stays idle

        using var c2 = new TcpClient();
        await c2.ConnectAsync(IPAddress.Loopback, port);
        var r2 = new StreamReader(c2.GetStream(), leaveOpen: true);

        // Without the fix this read blocks forever (the accept thread is parked inside client 1's processor).
        Assert.StartsWith("220 ", await ReadLineWithTimeout(r2));
    }

    private static async Task<string?> ReadLineWithTimeout(StreamReader reader, int timeoutMs = 15_000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            return await reader.ReadLineAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("timed out waiting for the SMTP greeting");
        }
    }

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

    public void Dispose() => _server?.Dispose();

    private sealed class NoopDelivery : IMailDelivery
    {
        public Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction transaction, CancellationToken cancellationToken = default) =>
            Task.FromResult(SmtpDeliveryResult.Ok());

        public Task<UserExistsCodes> DoesUserExist(string emailAddress) =>
            Task.FromResult(UserExistsCodes.DestinationAddressValid);
    }
}
