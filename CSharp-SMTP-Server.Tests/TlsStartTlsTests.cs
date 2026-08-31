using System.Net;
using System.Net.Sockets;
using System.Text;
using CSharp_SMTP_Server.Interfaces;
using CSharp_SMTP_Server.Networking;
using Xunit;

namespace CSharp_SMTP_Server.Tests;

/// <summary>TEST_PLAN.md §4.8 — TLS / STARTTLS behavior over loopback (Phase 3).</summary>
public class TlsStartTlsTests
{
    private static string B64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    /// <summary>Standard PLAIN payload: empty authzid \0 authcid \0 password.</summary>
    private const string PlainInline = "AHVzZXIAcGFzcw=="; // B64("\0user\0pass")

    private sealed class TestAuth : IAuthLogin
    {
        public Task<bool> AuthPlain(string authorizationIdentity, string authenticationIdentity, string password, EndPoint? remoteEndPoint, bool secureConnection) =>
            Task.FromResult(authenticationIdentity == "user" && password == "pass");

        public Task<bool> AuthLogin(string login, string password, EndPoint? remoteEndPoint, bool secureConnection) =>
            Task.FromResult(login == "user" && password == "pass");
    }

    // ── implicit TLS port ────────────────────────────────────────────────────────

    [Fact]
    public async Task ImplicitTls_GreetingOverTls_FullFlow_EncryptionRecorded()
    {
        var cert = TlsTestCerts.Create();
        var delivery = new RecordingDelivery();
        var port = TestPorts.Allocate();
        using var server = TestServers.BuildTls(port, certificate: cert, delivery: delivery);
        server.Start();

        await using var s = await SmtpSession.ConnectAsync(port);
        await s.UpgradeTlsAsync(); // implicit TLS: handshake first, greeting arrives over TLS

        Assert.Equal("220 test.local ESMTP", await s.ReadLineAsync());

        // EHLO while already secure: no STARTTLS line (nothing left to upgrade to), no AUTH configured
        await s.Send("EHLO client.test");
        var ehlo = await s.ReadResponseAsync();
        Assert.Equal(new[] { "250-test.local at your service", "250 8BITMIME" }, ehlo);

        // full transaction over TLS
        await s.Send("MAIL FROM:<sender@test.local>");
        Assert.Equal("250 2.0.0", await s.ReadLineAsync());
        await s.Send("RCPT TO:<rcpt@test.local>");
        Assert.Equal("250 2.1.5", await s.ReadLineAsync());
        await s.Send("DATA");
        Assert.Equal("354 Start mail input; end with <CRLF>.<CRLF>", await s.ReadLineAsync());
        // This server sends no per-line ACKs during DATA (RFC 5321 allows it) — send all body
        // lines back-to-back and read the single response after the terminator.
        await s.Send("Subject: implicit tls");
        await s.Send(".");
        Assert.Equal("250 2.0.0 OK", await s.ReadLineAsync());

        server.Dispose();
        var t = Assert.Single(delivery.Delivered);
        Assert.Equal(ConnectionEncryption.Tls, t.Encryption);
    }

    // ── STARTTLS on a regular port ───────────────────────────────────────────────

    [Fact]
    public async Task StartTls_Advertised_Upgrade_SecondStartTlsRejected_EncryptionRecorded()
    {
        var cert = TlsTestCerts.Create();
        var delivery = new RecordingDelivery();
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port, certificate: cert, delivery: delivery);
        server.Start();

        await using var s = await SmtpSession.ConnectAsync(port);
        Assert.Equal("220 test.local ESMTP", await s.ReadLineAsync());

        // advertised while unencrypted and a certificate is configured
        await s.Send("EHLO client.test");
        var ehlo = await s.ReadResponseAsync();
        Assert.Contains("250-STARTTLS", ehlo);

        await s.Send("STARTTLS");
        Assert.Equal("220 2.0.0 Ready for TLS", await s.ReadLineAsync());
        await s.UpgradeTlsAsync();

        // after the upgrade STARTTLS disappears from EHLO and commands keep working
        await s.Send("EHLO client.test");
        var ehlo2 = await s.ReadResponseAsync();
        Assert.DoesNotContain(ehlo2, l => l.Contains("STARTTLS"));

        // second STARTTLS on an already-encrypted connection → 503 (Q7: two-arg call, no table text)
        await s.Send("STARTTLS");
        Assert.Equal("503 5.5.1", await s.ReadLineAsync());

        // full transaction over the upgraded session
        await s.Send("MAIL FROM:<sender@test.local>");
        Assert.Equal("250 2.0.0", await s.ReadLineAsync());
        await s.Send("RCPT TO:<rcpt@test.local>");
        Assert.Equal("250 2.1.5", await s.ReadLineAsync());
        await s.Send("DATA");
        Assert.Equal("354 Start mail input; end with <CRLF>.<CRLF>", await s.ReadLineAsync());
        // No per-line ACKs during DATA (see ImplicitTls_GreetingOverTls_FullFlow_EncryptionRecorded).
        await s.Send("Subject: starttls");
        await s.Send(".");
        Assert.Equal("250 2.0.0 OK", await s.ReadLineAsync());

        server.Dispose();
        var t = Assert.Single(delivery.Delivered);
        Assert.Equal(ConnectionEncryption.StartTls, t.Encryption);
    }

    [Fact]
    public async Task StartTls_NoCertificate_Returns502_ConnectionSurvives()
    {
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port); // no certificate configured
        server.Start();

        await using var s = await SmtpSession.ConnectAsync(port);
        Assert.Equal("220 test.local ESMTP", await s.ReadLineAsync());

        // not advertised without a certificate…
        await s.Send("EHLO client.test");
        var ehlo = await s.ReadResponseAsync();
        Assert.DoesNotContain(ehlo, l => l.Contains("STARTTLS"));

        // …and the command itself is rejected — but the connection stays alive in plaintext
        await s.Send("STARTTLS");
        Assert.Equal("502 5.5.1", await s.ReadLineAsync());

        await s.Send("NOOP");
        Assert.Equal("250 2.0.0", await s.ReadLineAsync());
    }

    [Fact]
    public async Task StartTls_BeforeEhlo_Accepted_Q4Pin()
    {
        // Q4: the STARTTLS handler has no _protocolVersion check, so it is accepted as the very first
        // command today (RFC 5321 §4.9.6 does not require EHLO before STARTTLS). Pinned — do not
        // "fix" without review.
        var cert = TlsTestCerts.Create();
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port, certificate: cert);
        server.Start();

        await using var s = await SmtpSession.ConnectAsync(port);
        Assert.Equal("220 test.local ESMTP", await s.ReadLineAsync());

        await s.Send("STARTTLS"); // before EHLO — accepted today
        Assert.Equal("220 2.0.0 Ready for TLS", await s.ReadLineAsync());
        await s.UpgradeTlsAsync();

        await s.Send("EHLO client.test");
        var ehlo = await s.ReadResponseAsync();
        Assert.StartsWith("250-test.local at your service", ehlo[0]);
    }

    // ── configuration edge cases ─────────────────────────────────────────────────

    [Fact]
    public async Task TlsPort_NoCertificate_FallsBackToPlaintext_Pinned()
    {
        // Secure = secure && Certificate != null — a TLS listener constructed without a certificate
        // silently serves plaintext. Pinned (surprising; arguably it should refuse to start).
        var port = TestPorts.Allocate();
        using var server = TestServers.BuildTls(port); // no certificate
        server.Start();

        await using var s = await SmtpSession.ConnectAsync(port); // plain TCP, no handshake at all
        Assert.Equal("220 test.local ESMTP", await s.ReadLineAsync());

        await s.Send("EHLO client.test");
        var ehlo = await s.ReadResponseAsync();
        Assert.StartsWith("250-test.local at your service", ehlo[0]); // plaintext EHLO works
    }

    [Fact]
    public async Task SetTlsCertificate_AfterConstruction_AdvertisedOnNewConnections()
    {
        var cert = TlsTestCerts.Create();
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port); // no certificate at construction time
        server.Start();

        await using (var s1 = await SmtpSession.ConnectAsync(port))
        {
            Assert.Equal("220 test.local ESMTP", await s1.ReadLineAsync());
            await s1.Send("EHLO client.test");
            var ehlo1 = await s1.ReadResponseAsync();
            Assert.DoesNotContain(ehlo1, l => l.Contains("STARTTLS"));
        }

        // dynamic lookup: EHLO checks Server.Certificate per request
        server.SetTLSCertificate(cert);

        await using (var s2 = await SmtpSession.ConnectAsync(port))
        {
            Assert.Equal("220 test.local ESMTP", await s2.ReadLineAsync());
            await s2.Send("EHLO client.test");
            var ehlo2 = await s2.ReadResponseAsync();
            Assert.Contains("250-STARTTLS", ehlo2);
        }
    }

    [Fact]
    public async Task RequireEncryptionForAuth_538BeforeUpgrade_SuccessAfter()
    {
        var cert = TlsTestCerts.Create();
        var port = TestPorts.Allocate();
        var options = TestServers.DefaultOptions();
        options.RequireEncryptionForAuth = true;
        using var server = TestServers.Build(port, options: options, auth: new TestAuth(), certificate: cert);
        server.Start();

        await using var s = await SmtpSession.ConnectAsync(port);
        Assert.Equal("220 test.local ESMTP", await s.ReadLineAsync());

        // pinned: AUTH is advertised even before the upgrade; enforcement happens at AUTH time
        await s.Send("EHLO client.test");
        var ehlo = await s.ReadResponseAsync();
        Assert.Contains("250-AUTH LOGIN PLAIN", ehlo);

        await s.Send($"AUTH PLAIN {PlainInline}");
        Assert.Equal("538 5.7.11", await s.ReadLineAsync()); // Q7: two-arg call, no table text

        await s.Send("STARTTLS");
        Assert.Equal("220 2.0.0 Ready for TLS", await s.ReadLineAsync());
        await s.UpgradeTlsAsync();

        await s.Send($"AUTH PLAIN {PlainInline}");
        Assert.Equal("235 2.7.0 Authentication Succeeded", await s.ReadLineAsync());
    }

    // ── failed handshakes must not take the server down ──────────────────────────

    [Fact]
    public async Task StartTls_ClientRejectsCert_ServerSurvives()
    {
        var cert = TlsTestCerts.Create();
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port, certificate: cert);
        server.Start();

        await using (var s = await SmtpSession.ConnectAsync(port))
        {
            Assert.Equal("220 test.local ESMTP", await s.ReadLineAsync());
            await s.Send("STARTTLS");
            Assert.Equal("220 2.0.0 Ready for TLS", await s.ReadLineAsync());

            // client rejects the server certificate → handshake fails on both sides; the failure is
            // caught by the receive loop (logged, connection dropped) — no crash
            var ex = await Record.ExceptionAsync(() => s.UpgradeTlsAsync(acceptCertificate: false));
            Assert.NotNull(ex);
        }

        // server still alive: a new client gets greeted and STARTTLS is advertised again
        await using var s2 = await SmtpSession.ConnectAsync(port);
        Assert.Equal("220 test.local ESMTP", await s2.ReadLineAsync());
        await s2.Send("EHLO client.test");
        var ehlo = await s2.ReadResponseAsync();
        Assert.Contains("250-STARTTLS", ehlo);
    }

    [Fact]
    public async Task ImplicitTls_SilentDisconnect_ServerSurvives()
    {
        // Regression guard (B5): the implicit-TLS handshake runs in async void Init(); a client that
        // drops the connection before/without handshaking used to throw an unhandled exception and
        // crash the whole process. Now it is logged and only this connection is dropped.
        var cert = TlsTestCerts.Create();
        var logger = new RecordingLogger();
        var port = TestPorts.Allocate();
        using var server = TestServers.BuildTls(port, certificate: cert, logger: logger);
        server.Start();

        // client connects and immediately closes without any TLS handshake
        using (var c = new TcpClient())
            await c.ConnectAsync(IPAddress.Loopback, port);

        // wait (bounded) until the handshake failure is logged — pre-fix this never happens because
        // the process dies instead
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (logger.Errors.Count == 0 && sw.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(50);

        Assert.Contains(logger.Errors, e => e.StartsWith("[Client TLS handshake]"));

        // server still alive: a real client can complete the handshake and get greeted over TLS
        await using var s = await SmtpSession.ConnectAsync(port);
        await s.UpgradeTlsAsync();
        Assert.Equal("220 test.local ESMTP", await s.ReadLineAsync());
    }

    [Fact]
    public async Task ImplicitTls_ClientRejectsCert_ServerSurvives()
    {
        var cert = TlsTestCerts.Create();
        var port = TestPorts.Allocate();
        using var server = TestServers.BuildTls(port, certificate: cert);
        server.Start();

        await using (var s = await SmtpSession.ConnectAsync(port))
        {
            // client rejects the server certificate during the implicit-TLS handshake
            var ex = await Record.ExceptionAsync(() => s.UpgradeTlsAsync(acceptCertificate: false));
            Assert.NotNull(ex); // client-side AuthenticationException
        }

        // server still alive (pre-fix: process crash) — a new client completes the handshake
        await using var s2 = await SmtpSession.ConnectAsync(port);
        await s2.UpgradeTlsAsync();
        Assert.Equal("220 test.local ESMTP", await s2.ReadLineAsync());
    }

    [Fact]
    public async Task ImplicitTls_PlaintextProbe_ServerSurvives()
    {
        var cert = TlsTestCerts.Create();
        var port = TestPorts.Allocate();
        using var server = TestServers.BuildTls(port, certificate: cert);
        server.Start();

        // scanner-style probe: send plaintext SMTP to the TLS port and walk away
        await using (var s = await SmtpSession.ConnectAsync(port))
            await s.Send("EHLO probe");

        // server still alive (pre-fix: process crash) — a new client completes the handshake
        await using var s2 = await SmtpSession.ConnectAsync(port);
        await s2.UpgradeTlsAsync();
        Assert.Equal("220 test.local ESMTP", await s2.ReadLineAsync());
    }

    [Fact]
    public async Task ThrowingFilter_PlaintextConnection_OnlyThatConnectionDropped_R6()
    {
        // R6: same async void crash class as B5, but reachable from ordinary consumer code rather than
        // a hostile scanner. Init() awaits Greet(), which awaits IMailFilter.IsConnectionAllowed; a
        // filter that throws (a database timeout is enough) propagated out of async void and killed the
        // process. Now it is logged and only this connection is dropped.
        var filter = new ConfigurableFilter { ConnectionThrows = new InvalidOperationException("filter backend down") };
        var logger = new RecordingLogger();
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port, filter: filter, logger: logger);
        server.Start();

        await using (var s = await SmtpSession.ConnectAsync(port))
        {
            // No greeting arrives — the connection is dropped instead.
            string? line;
            try { line = await s.ReadLineAsync(); }
            catch (IOException) { line = null; }
            Assert.Null(line);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (logger.Errors.Count == 0 && sw.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(50);

        Assert.Contains(logger.Errors, e => e.StartsWith("[Client init]") && e.Contains("filter backend down"));

        // The process survived and the next client is greeted normally.
        filter.ConnectionThrows = null;
        await using var s2 = await SmtpSession.ConnectAsync(port);
        Assert.Equal("220 test.local ESMTP", await s2.ReadLineAsync());
    }

    [Fact]
    public async Task ThrowingFilter_ImplicitTlsConnection_OnlyThatConnectionDropped_R6()
    {
        // The TLS half of R6, which the plaintext test above does NOT cover: on a secure connection the
        // greeting (and with it the filter call) happens in Receive(), not Init() — and Receive() is
        // also started fire-and-forget. Guarding only Init() would leave the process crashable here.
        var filter = new ConfigurableFilter { ConnectionThrows = new InvalidOperationException("filter backend down") };
        var cert = TlsTestCerts.Create();
        var logger = new RecordingLogger();
        var port = TestPorts.Allocate();
        using var server = TestServers.BuildTls(port, certificate: cert, logger: logger, filter: filter);
        server.Start();

        await using (var s = await SmtpSession.ConnectAsync(port))
        {
            await s.UpgradeTlsAsync(); // handshake succeeds; the filter throws while greeting
            string? line;
            try { line = await s.ReadLineAsync(); }
            catch (IOException) { line = null; }
            Assert.Null(line);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (logger.Errors.Count == 0 && sw.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(50);

        Assert.Contains(logger.Errors, e => e.StartsWith("[Client greeting]") && e.Contains("filter backend down"));

        // The process survived and the next TLS client is greeted normally.
        filter.ConnectionThrows = null;
        await using var s2 = await SmtpSession.ConnectAsync(port);
        await s2.UpgradeTlsAsync();
        Assert.Equal("220 test.local ESMTP", await s2.ReadLineAsync());
    }
}
