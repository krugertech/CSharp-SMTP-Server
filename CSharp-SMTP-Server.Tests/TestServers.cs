using System.Net;
using System.Security.Cryptography.X509Certificates;
using CSharp_SMTP_Server;
using CSharp_SMTP_Server.Interfaces;
using CSharp_SMTP_Server.Networking;

namespace CSharp_SMTP_Server.Tests;

/// <summary>Shared SMTPServer factory for integration tests. See TESTING.md.</summary>
public static class TestServers
{
    /// <summary>Default options used by most protocol tests: no SPF/DMARC, plaintext auth allowed.</summary>
    public static ServerOptions DefaultOptions(string serverName = "test.local") =>
        new(false, false, null) { ServerName = serverName };

    /// <summary>
    /// Builds (but does not start) a server with one regular loopback port. Pass <c>null</c> for any
    /// fake to leave it unset; the delivery handler defaults to <see cref="NoopDelivery.Instance"/>.
    /// </summary>
    public static SMTPServer Build(ushort? port = null, ServerOptions? options = null,
        IMailDelivery? delivery = null, IAuthLogin? auth = null, IMailFilter? filter = null,
        X509Certificate2? certificate = null, ILogger? logger = null)
    {
        var p = port ?? TestPorts.Allocate();
        var server = new SMTPServer(
            new[] { new ListeningParameters(IPAddress.Loopback, new[] { p }, null) },
            options ?? DefaultOptions(), delivery ?? NoopDelivery.Instance, logger,
            certificate: certificate);

        if (auth != null) server.SetAuthLogin(auth);
        if (filter != null) server.SetFilter(filter);
        return server;
    }

    /// <summary>Builds (but does not start) a server with one implicit-TLS loopback port.</summary>
    public static SMTPServer BuildTls(ushort? port = null, ServerOptions? options = null,
        IMailDelivery? delivery = null, IAuthLogin? auth = null, X509Certificate2? certificate = null,
        ILogger? logger = null, IMailFilter? filter = null)
    {
        var p = port ?? TestPorts.Allocate();
        var server = new SMTPServer(
            new[] { new ListeningParameters(IPAddress.Loopback, null, new[] { p }) },
            options ?? DefaultOptions(), delivery ?? NoopDelivery.Instance, logger,
            certificate: certificate);

        if (auth != null) server.SetAuthLogin(auth);
        if (filter != null) server.SetFilter(filter);
        return server;
    }
}
