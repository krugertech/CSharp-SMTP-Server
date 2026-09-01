using System.Net;
using System.Text;
using CSharp_SMTP_Server.Interfaces;
using CSharp_SMTP_Server.Protocol;

namespace CSharp_SMTP_Server.Tests;

/// <summary>AUTH PLAIN / AUTH LOGIN protocol behavior beyond the initial-response tests. See TESTING.md.</summary>
public sealed class AuthProtocolTests
{
    // base64("user") / base64("pass")
    private const string B64User = "dXNlcg==";
    private const string B64Pass = "cGFzcw==";

    private static async Task<(SmtpSession S, SMTPServer Server)> ConnectEhloAsync(
        IAuthLogin? auth = null, bool requireEncryptionForAuth = false)
    {
        var options = TestServers.DefaultOptions();
        options.RequireEncryptionForAuth = requireEncryptionForAuth;

        var port = TestPorts.Allocate();
        var server = TestServers.Build(port, options: options, auth: auth);
        server.Start();

        var s = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await s.ReadLineAsync());
        await s.Send("EHLO test.client");
        await s.ReadResponseAsync();
        return (s, server);
    }

    [Fact]
    public async Task NoAuthConfigured_AuthReturns502_AndEhloOmitsAuthLine()
    {
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port); // no SetAuthLogin
        server.Start();

        await using var s = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await s.ReadLineAsync());
        await s.Send("EHLO test.client");
        var ehloLines = (await s.ReadResponseAsync()).ToArray();

        // No AUTH advertisement without an IAuthLogin.
        Assert.DoesNotContain(ehloLines, l => l.Contains("AUTH"));

        await s.Send("AUTH PLAIN " + B64User);
        // Two-arg WriteCode → no table text (Q7).
        Assert.Equal("502 5.5.1", await s.ReadLineAsync());
    }

    [Fact]
    public async Task RequireEncryptionForAuth_Plaintext_Returns538()
    {
        var (s, server) = await ConnectEhloAsync(auth: new StaticAuth(), requireEncryptionForAuth: true);
        using (server)
        await using (s)
        {
            // The STARTTLS-upgrade half of this scenario is Phase 3 (§4.8).
            await s.Send("AUTH LOGIN");
            Assert.Equal("538 5.7.11", await s.ReadLineAsync()); // Q7: no table text
        }
    }

    [Theory]
    [InlineData("AUTH CRAM-MD5 dXNlcg==")]
    [InlineData("AUTH")]
    public async Task UnknownOrBareAuth_Returns501(string command)
    {
        var (s, server) = await ConnectEhloAsync(auth: new StaticAuth());
        using (server)
        await using (s)
        {
            await s.Send(command);
            Assert.Equal("501 5.7.4 Unrecognized Authentication Method", await s.ReadLineAsync());
        }
    }

    [Fact]
    public async Task PlainInline_CorrectCredentials_Succeeds()
    {
        var (s, server) = await ConnectEhloAsync(auth: new StaticAuth());
        using (server)
        await using (s)
        {
            // Standard PLAIN payload: empty authzid \0 authcid \0 password.
            await s.Send("AUTH PLAIN " + B64("\0user\0pass"));
            Assert.Equal("235 2.7.0 Authentication Succeeded", await s.ReadLineAsync());
        }
    }

    [Theory]
    [InlineData("!!!not-base64!!!")]                       // malformed base64
    [InlineData("dXNlcgBwYXNz")]                           // b64("user\0pass") — two NUL-separated parts (missing authzid)
    [InlineData("AHUAcAB4")]                               // b64("\0u\0p\0x") — four parts
    public async Task PlainInline_MalformedPayload_Returns535(string payload)
    {
        var (s, server) = await ConnectEhloAsync(auth: new StaticAuth());
        using (server)
        await using (s)
        {
            await s.Send("AUTH PLAIN " + payload);
            Assert.Equal("535 5.7.8 Authentication credentials invalid", await s.ReadLineAsync());
        }
    }

    [Fact]
    public async Task PlainInline_WrongPassword_Returns535()
    {
        var (s, server) = await ConnectEhloAsync(auth: new StaticAuth());
        using (server)
        await using (s)
        {
            await s.Send("AUTH PLAIN " + B64("\0user\0wrong"));
            Assert.Equal("535 5.7.8 Authentication credentials invalid", await s.ReadLineAsync());
        }
    }

    [Fact]
    public async Task PlainInline_AuthPlainReceivesAllIdentities()
    {
        var auth = new RecordingAuth();
        var (s, server) = await ConnectEhloAsync(auth: auth);
        using (server)
        await using (s)
        {
            // Non-empty authzid: "authzid\0authcid\0password" (no leading NUL — that belongs to the
            // standard empty-authzid form). A leading NUL here would make four split parts → 535.
            await s.Send("AUTH PLAIN " + B64("other\0user\0pass"));
            Assert.Equal("235 2.7.0 Authentication Succeeded", await s.ReadLineAsync());

            Assert.Equal("other", auth.LastAuthzid);
            Assert.Equal("user", auth.LastAuthcid);
            Assert.Equal("pass", auth.LastPassword);
            Assert.IsType<IPEndPoint>(auth.LastEndpoint);
            Assert.False(auth.LastSecure); // plaintext connection
        }
    }

    [Fact]
    public async Task PlainInteractive_PromptThenCredentials()
    {
        var (s, server) = await ConnectEhloAsync(auth: new StaticAuth());
        using (server)
        await using (s)
        {
            // Bare AUTH PLAIN → bare 334 prompt, then the payload on the next line.
            await s.Send("AUTH PLAIN");
            Assert.Equal("334", await s.ReadLineAsync());

            await s.Send(B64("\0user\0pass"));
            Assert.Equal("235 2.7.0 Authentication Succeeded", await s.ReadLineAsync());
        }
    }

    [Fact]
    public async Task PlainInteractive_WrongCredentials_Returns535()
    {
        var (s, server) = await ConnectEhloAsync(auth: new StaticAuth());
        using (server)
        await using (s)
        {
            await s.Send("AUTH PLAIN");
            Assert.Equal("334", await s.ReadLineAsync());

            await s.Send(B64("\0user\0wrong"));
            Assert.Equal("535 5.7.8 Authentication credentials invalid", await s.ReadLineAsync());
        }
    }

    [Fact]
    public async Task Login_WrongUsername_Returns535AfterPassword()
    {
        var (s, server) = await ConnectEhloAsync(auth: new StaticAuth());
        using (server)
        await using (s)
        {
            await s.Send("AUTH LOGIN");
            Assert.Equal("334 VXNlcm5hbWU6", await s.ReadLineAsync());

            // Step 1 does not validate the username — it only decodes and stores it.
            await s.Send(B64("wronguser"));
            Assert.Equal("334 UGFzc3dvcmQ6", await s.ReadLineAsync());

            await s.Send(B64Pass);
            Assert.Equal("535 5.7.8 Authentication credentials invalid", await s.ReadLineAsync());
        }
    }

    [Fact]
    public async Task Login_InvalidBase64Username_StillPromptsPassword_AndFails()
    {
        var (s, server) = await ConnectEhloAsync(auth: new StaticAuth());
        using (server)
        await using (s)
        {
            await s.Send("AUTH LOGIN");
            Assert.Equal("334 VXNlcm5hbWU6", await s.ReadLineAsync());

            // Undecodable username → TempUsername stays null, but the password prompt still comes.
            await s.Send("!!!not-base64!!!");
            Assert.Equal("334 UGFzc3dvcmQ6", await s.ReadLineAsync());

            await s.Send(B64Pass);
            // Fails because TempUsername is null (pin current behavior).
            Assert.Equal("535 5.7.8 Authentication credentials invalid", await s.ReadLineAsync());
        }
    }

    [Fact]
    public async Task Login_InvalidBase64Password_Returns535()
    {
        var (s, server) = await ConnectEhloAsync(auth: new StaticAuth());
        using (server)
        await using (s)
        {
            await s.Send("AUTH LOGIN");
            Assert.Equal("334 VXNlcm5hbWU6", await s.ReadLineAsync());
            await s.Send(B64User);
            Assert.Equal("334 UGFzc3dvcmQ6", await s.ReadLineAsync());

            await s.Send("!!!not-base64!!!");
            Assert.Equal("535 5.7.8 Authentication credentials invalid", await s.ReadLineAsync());
        }
    }

    [Fact]
    public async Task Login_EmptyDecodedPassword_Returns535()
    {
        var (s, server) = await ConnectEhloAsync(auth: new StaticAuth());
        using (server)
        await using (s)
        {
            await s.Send("AUTH LOGIN");
            Assert.Equal("334 VXNlcm5hbWU6", await s.ReadLineAsync());
            await s.Send(B64User);
            Assert.Equal("334 UGFzc3dvcmQ6", await s.ReadLineAsync());

            // An empty line decodes to "" (not null) — the fake rejects it → 535. Documented behavior.
            await s.Send("");
            Assert.Equal("535 5.7.8 Authentication credentials invalid", await s.ReadLineAsync());
        }
    }

    [Fact]
    public async Task Login_Success_SetsUsername_ForFilterAndDelivery()
    {
        var filter = new ConfigurableFilter();
        var options = TestServers.DefaultOptions();
        options.RequireEncryptionForAuth = false;

        var port = TestPorts.Allocate();
        var delivery = new RecordingDelivery();
        using var server = TestServers.Build(port, options: options, delivery: delivery, auth: new StaticAuth(), filter: filter);
        server.Start();

        await using var s = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await s.ReadLineAsync());
        await s.Send("EHLO test.client");
        await s.ReadResponseAsync();

        await s.Send("AUTH LOGIN");
        Assert.Equal("334 VXNlcm5hbWU6", await s.ReadLineAsync());
        await s.Send(B64User);
        Assert.Equal("334 UGFzc3dvcmQ6", await s.ReadLineAsync());
        await s.Send(B64Pass);
        Assert.Equal("235 2.7.0 Authentication Succeeded", await s.ReadLineAsync());

        // The authenticated flag + username reach the CanDeliver filter hook…
        await s.Send("MAIL FROM:<a@b.com>");
        Assert.Equal("250 2.0.0", await s.ReadLineAsync());
        await s.Send("RCPT TO:<c@d.e>");
        Assert.Equal("250 2.1.5", await s.ReadLineAsync());

        // …and the delivered transaction carries AuthenticatedUser.
        await s.Send("DATA");
        Assert.StartsWith("354", await s.ReadLineAsync());
        await s.Send(".");
        Assert.StartsWith("250", await s.ReadLineAsync());

        Assert.True(filter.LastDeliverAuthenticated);
        Assert.Equal("user", filter.LastDeliverUsername);
        Assert.Equal("user", delivery.Delivered[0].AuthenticatedUser);
    }

    [Fact]
    public async Task AuthState_SurvivesReEhlo()
    {
        // Document current behavior: EHLO resets the in-flight transaction but NOT the authenticated
        // username — a message delivered after a re-EHLO is still treated as authenticated.
        var options = TestServers.DefaultOptions();
        options.RequireEncryptionForAuth = false;

        var port = TestPorts.Allocate();
        var delivery = new RecordingDelivery();
        using var server = TestServers.Build(port, options: options, delivery: delivery, auth: new StaticAuth());
        server.Start();

        await using var s = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await s.ReadLineAsync());
        await s.Send("EHLO test.client");
        await s.ReadResponseAsync();

        await s.Send("AUTH PLAIN " + B64("\0user\0pass"));
        Assert.Equal("235 2.7.0 Authentication Succeeded", await s.ReadLineAsync());

        // Re-EHLO mid-session…
        await s.Send("EHLO test.client");
        await s.ReadResponseAsync();

        // …then deliver: the username is still in effect.
        await s.Send("MAIL FROM:<a@b.com>");
        Assert.Equal("250 2.0.0", await s.ReadLineAsync());
        await s.Send("RCPT TO:<c@d.e>");
        Assert.Equal("250 2.1.5", await s.ReadLineAsync());
        await s.Send("DATA");
        Assert.StartsWith("354", await s.ReadLineAsync());
        await s.Send(".");
        Assert.StartsWith("250", await s.ReadLineAsync());

        var tx = delivery.Delivered[0];
        Assert.Equal("user", tx.AuthenticatedUser);
        // Authenticated senders skip SPF entirely — the transaction records UserAuthenticated.
        Assert.Equal(ValidationResult.UserAuthenticated, tx.SPFValidationResult);
    }

    [Fact]
    public async Task AuthLogin_BecomesNullMidAuth_Returns454()
    {
        var options = TestServers.DefaultOptions();
        options.RequireEncryptionForAuth = false;

        var auth = new StaticAuth();
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port, options: options, auth: auth);
        server.Start();

        await using var s = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await s.ReadLineAsync());
        await s.Send("EHLO test.client");
        await s.ReadResponseAsync();

        await s.Send("AUTH LOGIN");
        Assert.Equal("334 VXNlcm5hbWU6", await s.ReadLineAsync());

        // The auth interface is removed while the exchange is in flight…
        server.SetAuthLogin(null);

        await s.Send(B64User);
        Assert.Equal("334 UGFzc3dvcmQ6", await s.ReadLineAsync());
        await s.Send(B64Pass);
        Assert.Equal("454 4.7.0 Temporary authentication failure", await s.ReadLineAsync());
    }

    private static string B64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private sealed class StaticAuth : IAuthLogin
    {
        public Task<bool> AuthPlain(string authorizationIdentity, string authenticationIdentity,
            string password, EndPoint remoteEndPoint, bool secureConnection) =>
            Task.FromResult(authenticationIdentity == "user" && password == "pass");

        public Task<bool> AuthLogin(string login, string password,
            EndPoint remoteEndPoint, bool secureConnection) =>
            Task.FromResult(login == "user" && password == "pass");
    }

    private sealed class RecordingAuth : IAuthLogin
    {
        public string? LastAuthzid;
        public string? LastAuthcid;
        public string? LastPassword;
        public EndPoint? LastEndpoint;
        public bool? LastSecure;

        public Task<bool> AuthPlain(string authorizationIdentity, string authenticationIdentity,
            string password, EndPoint remoteEndPoint, bool secureConnection)
        {
            LastAuthzid = authorizationIdentity;
            LastAuthcid = authenticationIdentity;
            LastPassword = password;
            LastEndpoint = remoteEndPoint;
            LastSecure = secureConnection;
            return Task.FromResult(true);
        }

        public Task<bool> AuthLogin(string login, string password,
            EndPoint remoteEndPoint, bool secureConnection) => Task.FromResult(false);
    }
}
