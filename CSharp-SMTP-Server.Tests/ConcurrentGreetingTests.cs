using System.Net;
using CSharp_SMTP_Server;
using CSharp_SMTP_Server.Networking;

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
        var port = TestPorts.Allocate();
        _server = new SMTPServer(
            new[] { new ListeningParameters(IPAddress.Loopback, new[] { port }, null) },
            new ServerOptions(false, false),
            NoopDelivery.Instance);
        _server.Start();

        await using var c1 = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await c1.ReadLineAsync()); // client 1 greeted, then stays idle

        await using var c2 = await SmtpSession.ConnectAsync(port);

        // Without the fix this read would block forever (the accept thread is parked inside client 1's
        // processor) — the shared helper's 10 s timeout turns that hang into a test failure.
        Assert.StartsWith("220 ", await c2.ReadLineAsync());
    }

    public void Dispose() => _server?.Dispose();
}
