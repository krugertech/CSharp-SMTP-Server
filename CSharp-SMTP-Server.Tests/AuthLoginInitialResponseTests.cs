using System;
using System.Net;
using System.Text;
using CSharp_SMTP_Server;
using CSharp_SMTP_Server.Interfaces;
using CSharp_SMTP_Server.Networking;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Integration tests for AUTH LOGIN with an initial response (base64-encoded username), as sent by
/// IIS SMTP relay — upstream PR #17 / RFC 4954. Before the fix the inline username was ignored and
/// the password sent in reply to the prompt was misread as a second username, so authentication
/// always failed against such clients.
/// </summary>
public sealed class AuthLoginInitialResponseTests
{
    // base64("user") / base64("pass")
    private const string B64User = "dXNlcg==";
    private const string B64Pass = "cGFzcw==";

    // ─── helpers ────────────────────────────────────────────────────────────────

    private sealed class TestAuth : IAuthLogin
    {
        public Task<bool> AuthPlain(string authorizationIdentity, string authenticationIdentity,
            string password, EndPoint remoteEndPoint, bool secureConnection) =>
            Task.FromResult(authenticationIdentity == "user" && password == "pass");

        public Task<bool> AuthLogin(string login, string password,
            EndPoint remoteEndPoint, bool secureConnection) =>
            Task.FromResult(login == "user" && password == "pass");
    }

    private static async Task<(SmtpSession Session, SMTPServer Server)> NewServerWithAuthAsync()
    {
        var port = TestPorts.Allocate();

        var opts = new ServerOptions(validateSPF: false, validateDMARC: false, dnsServerEndpoint: null)
        {
            ServerName = "test.local",
            RequireEncryptionForAuth = false
        };

        var server = new SMTPServer(
            new[] { new ListeningParameters(IPAddress.Loopback, new[] { port }, null) },
            opts, NoopDelivery.Instance);
        server.SetAuthLogin(new TestAuth());
        server.Start();

        var session = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await session.ReadLineAsync()); // 220 greeting
        return (session, server);
    }

    private static async Task EhloAsync(SmtpSession s)
    {
        await s.Send("EHLO test.client");
        var lines = await s.ReadResponseAsync();
        if (!lines[^1].StartsWith("250")) throw new InvalidOperationException("EHLO failed: " + lines[^1]);
    }

    // ─── tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AuthLogin_Bare_TwoStepFlow_StillWorks()
    {
        var (session, server) = await NewServerWithAuthAsync();
        using (server)
        await using (session)
        {
            await EhloAsync(session);

            // Standard RFC 4954 flow without initial response must be unchanged.
            await session.Send("AUTH LOGIN");
            Assert.StartsWith("334", await session.ReadLineAsync()); // username prompt

            await session.Send(B64User);
            Assert.StartsWith("334", await session.ReadLineAsync()); // password prompt

            await session.Send(B64Pass);
            var lines = await session.ReadResponseAsync();
            Assert.StartsWith("235", lines[^1]);
        }
    }

    [Fact]
    public async Task AuthLogin_WithInlineUsername_GoesStraightToPasswordPrompt()
    {
        var (session, server) = await NewServerWithAuthAsync();
        using (server)
        await using (session)
        {
            await EhloAsync(session);

            // IIS SMTP relay style: username is sent as the initial response, then only the password.
            await session.Send($"AUTH LOGIN {B64User}");
            Assert.StartsWith("334", await session.ReadLineAsync()); // must be the PASSWORD prompt now

            await session.Send(B64Pass);
            var lines = await session.ReadResponseAsync();
            Assert.StartsWith("235", lines[^1]);
        }
    }

    [Fact]
    public async Task AuthLogin_WithInlineUsername_WrongPassword_Returns535()
    {
        var (session, server) = await NewServerWithAuthAsync();
        using (server)
        await using (session)
        {
            await EhloAsync(session);

            await session.Send($"AUTH LOGIN {B64User}");
            Assert.StartsWith("334", await session.ReadLineAsync());

            await session.Send(Convert.ToBase64String(Encoding.UTF8.GetBytes("wrong")));
            var lines = await session.ReadResponseAsync();
            Assert.StartsWith("535", lines[^1]);
        }
    }

    [Fact]
    public async Task AuthLogin_WithInvalidBase64InitialResponse_FallsBackToUsernamePrompt()
    {
        var (session, server) = await NewServerWithAuthAsync();
        using (server)
        await using (session)
        {
            await EhloAsync(session);

            // Not valid base64 → treated as if no initial response was given.
            await session.Send("AUTH LOGIN !!!not-base64!!!");
            Assert.StartsWith("334", await session.ReadLineAsync()); // username prompt

            await session.Send(B64User);
            Assert.StartsWith("334", await session.ReadLineAsync()); // password prompt

            await session.Send(B64Pass);
            var lines = await session.ReadResponseAsync();
            Assert.StartsWith("235", lines[^1]);
        }
    }
}
