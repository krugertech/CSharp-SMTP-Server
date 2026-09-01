using System.Net;
using CSharp_SMTP_Server.Networking;

namespace CSharp_SMTP_Server.Tests.Compatibility.ExchangeOnPrem;

/// <summary>
/// TLS behaviour required by an on-premises Exchange send connector configured for opportunistic or
/// forced TLS.
/// </summary>
/// <remarks>
/// <para>
/// Exchange send connectors negotiate STARTTLS by default when the receiving server advertises it,
/// and can be configured to require it. The two behaviours that matter to a connector are that the
/// advertisement disappears once TLS is established (so the connector does not attempt a nested
/// handshake) and that session state is discarded across the upgrade.
/// </para>
/// <para>
/// The state-reset requirement is a security property, not a cosmetic one: anything accepted before
/// the handshake arrived in cleartext and may have been injected by an active attacker. Carrying it
/// past the upgrade would let that attacker prepend an envelope to a session the connector believes
/// is private.
/// </para>
/// </remarks>
[Trait(PlatformContract.Name, PlatformContract.ExchangeOnPrem)]
public sealed class ExchangeTlsTests
{
    private const string ExchangeEhloName = "EXCH01.corp.example.com";

    /// <summary>Starts a server with a self-signed certificate on a plaintext port offering STARTTLS.</summary>
    private static (SMTPServer Server, ushort Port) StartWithCertificate(Interfaces.IMailDelivery delivery)
    {
        var port = TestPorts.Allocate();
        var server = TestServers.Build(port, delivery: delivery, certificate: TlsTestCerts.Create());
        server.Start();
        return (server, port);
    }

    /// <summary>
    /// STARTTLS is advertised before the upgrade and gone after it, and a second STARTTLS is refused.
    /// </summary>
    /// <remarks>
    /// Source: <see cref="PlatformContract.Provenance.StartTlsReset"/>. RFC 3207 §4.2 requires the
    /// server to stop advertising STARTTLS once the connection is secure. A connector that saw it
    /// again could attempt a nested handshake, which fails in a way that looks like a certificate
    /// problem and sends operators hunting in the wrong place.
    /// </remarks>
    [Fact]
    public async Task StartTls_IsNotReadvertised_AndASecondAttemptIsRefused()
    {
        var delivery = new RecordingDelivery();
        var (server, port) = StartWithCertificate(delivery);
        try
        {
            await using var session = await SmtpSession.ConnectAsync(port);
            Assert.StartsWith("220", await session.ReadLineAsync());

            await session.Send($"EHLO {ExchangeEhloName}");
            var beforeTls = await session.ReadResponseAsync();
            Assert.Contains(beforeTls, l => l.Contains("STARTTLS", StringComparison.Ordinal));

            await session.Send("STARTTLS");
            Assert.StartsWith("220", await session.ReadLineAsync());
            await session.UpgradeTlsAsync();

            await session.Send($"EHLO {ExchangeEhloName}");
            var afterTls = await session.ReadResponseAsync();
            Assert.DoesNotContain(afterTls, l => l.Contains("STARTTLS", StringComparison.Ordinal));

            // 8BITMIME and SIZE are still offered — only the TLS upgrade itself is withdrawn.
            Assert.Contains(afterTls, l => l.Contains("8BITMIME", StringComparison.Ordinal));

            await session.Send("STARTTLS");
            var second = await session.ReadLineAsync();
            Assert.NotNull(second);
            Assert.StartsWith("5", second);
        }
        finally
        {
            server.Dispose();
        }
    }

    /// <summary>
    /// An envelope accepted before STARTTLS is discarded by the upgrade.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Source: <see cref="PlatformContract.Provenance.StartTlsReset"/>. RFC 3207 §4.2 requires the
    /// server to discard all knowledge obtained from the client, including any prior EHLO, and to
    /// behave as if the connection had just opened.
    /// </para>
    /// <para>
    /// The test asserts this by sending a pre-TLS <c>MAIL FROM</c> from an attacker-controlled
    /// address, upgrading, and then running a complete transaction from a different sender. If the
    /// pre-TLS envelope survived, the delivered message would carry the cleartext sender — the exact
    /// injection this rule exists to prevent.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task StartTls_DiscardsEnvelopeAcceptedBeforeTheUpgrade()
    {
        var delivery = new RecordingDelivery();
        var (server, port) = StartWithCertificate(delivery);
        try
        {
            await using var session = await SmtpSession.ConnectAsync(port);
            Assert.StartsWith("220", await session.ReadLineAsync());

            await session.Send($"EHLO {ExchangeEhloName}");
            await session.ReadResponseAsync();

            await session.Send("MAIL FROM:<injected@attacker.example.com>");
            Assert.StartsWith("250", await session.ReadLineAsync());

            await session.Send("STARTTLS");
            Assert.StartsWith("220", await session.ReadLineAsync());
            await session.UpgradeTlsAsync();

            await session.Send($"EHLO {ExchangeEhloName}");
            await session.ReadResponseAsync();

            await session.Send("MAIL FROM:<legitimate@corp.example.com>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("RCPT TO:<recipient@test.local>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("DATA");
            Assert.StartsWith("354", await session.ReadLineAsync());
            await session.Send("Subject: post-tls");
            await session.Send("");
            await session.Send("body");
            await session.Send(".");
            Assert.StartsWith("250", await session.ReadLineAsync());
        }
        finally
        {
            server.Dispose();
        }

        var delivered = Assert.Single(delivery.Delivered);
        Assert.Equal("legitimate@corp.example.com", delivered.From);
        Assert.DoesNotContain("attacker", delivered.From, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A full transaction completes over the upgraded connection and is delivered intact.
    /// </summary>
    /// <remarks>
    /// The negative tests above would all pass against a server that upgraded to TLS and then failed
    /// to deliver anything at all. This is the positive control that rules that out.
    /// </remarks>
    [Fact]
    public async Task TransactionOverStartTls_IsDeliveredIntact()
    {
        var delivery = new RecordingDelivery();
        var (server, port) = StartWithCertificate(delivery);
        try
        {
            await using var session = await SmtpSession.ConnectAsync(port);
            Assert.StartsWith("220", await session.ReadLineAsync());

            await session.Send($"EHLO {ExchangeEhloName}");
            await session.ReadResponseAsync();
            await session.Send("STARTTLS");
            Assert.StartsWith("220", await session.ReadLineAsync());
            await session.UpgradeTlsAsync();

            await session.Send($"EHLO {ExchangeEhloName}");
            await session.ReadResponseAsync();

            await session.Send("MAIL FROM:<sender@corp.example.com>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("RCPT TO:<recipient@test.local>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("DATA");
            Assert.StartsWith("354", await session.ReadLineAsync());
            await session.Send("X-MS-Exchange-Organization-AuthAs: Internal");
            await session.Send("Subject: over tls");
            await session.Send("");
            await session.Send("encrypted body");
            await session.Send(".");
            Assert.StartsWith("250", await session.ReadLineAsync());
        }
        finally
        {
            server.Dispose();
        }

        var delivered = Assert.Single(delivery.Delivered);
        Assert.Equal("sender@corp.example.com", delivered.From);
        Assert.Contains("encrypted body", delivered.RawBody, StringComparison.Ordinal);
        Assert.Contains("X-MS-Exchange-Organization-AuthAs: Internal", delivered.RawBody,
            StringComparison.Ordinal);
    }
}
