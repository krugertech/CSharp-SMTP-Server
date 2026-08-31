using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using CSharp_SMTP_Server;
using CSharp_SMTP_Server.Interfaces;
using CSharp_SMTP_Server.Networking;
using CSharp_SMTP_Server.Protocol.Responses;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Integration tests for AUTH LOGIN with an initial response (base64-encoded username), as sent by
/// IIS SMTP relay — upstream PR #17 / RFC 4954. Before the fix the inline username was ignored and
/// the password sent in reply to the prompt was misread as a second username, so authentication
/// always failed against such clients.
/// </summary>
public sealed class AuthLoginInitialResponseTests
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    // base64("user") / base64("pass")
    private const string B64User = "dXNlcg==";
    private const string B64Pass = "cGFzcw==";

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

    private sealed class TestAuth : IAuthLogin
    {
        public Task<bool> AuthPlain(string authorizationIdentity, string authenticationIdentity,
            string password, EndPoint remoteEndPoint, bool secureConnection) =>
            Task.FromResult(authenticationIdentity == "user" && password == "pass");

        public Task<bool> AuthLogin(string login, string password,
            EndPoint remoteEndPoint, bool secureConnection) =>
            Task.FromResult(login == "user" && password == "pass");
    }

    private sealed class NoopDelivery : IMailDelivery
    {
        public Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction t, CancellationToken ct = default) =>
            Task.FromResult(SmtpDeliveryResult.Ok());

        public Task<UserExistsCodes> DoesUserExist(string email) =>
            Task.FromResult(UserExistsCodes.DestinationAddressValid);
    }

    private sealed class Session : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly StreamReader _reader;
        private readonly Stream _stream;

        internal Session(TcpClient tcp, StreamReader reader, Stream stream)
        {
            _tcp = tcp;
            _reader = reader;
            _stream = stream;
        }

        public void Dispose()
        {
            try { _reader.Dispose(); } catch { /* already closed */ }
            _tcp.Close();
        }

        public async Task<string> ReadLine()
        {
            var line = await _reader.ReadLineAsync().WaitAsync(ReadTimeout);
            return line ?? throw new InvalidOperationException("Server closed the connection unexpectedly.");
        }

        public async Task Send(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
            await _stream.WriteAsync(bytes).AsTask().WaitAsync(ReadTimeout);
        }

        /// <summary>Reads a (possibly multi-line) response and returns the final line.</summary>
        public async Task<string> ReadResponse()
        {
            string? last = null;
            while (true)
            {
                last = await ReadLine();
                // Final line of a multi-line reply has "- " replaced by "  " after the code.
                if (last.Length < 4 || last[3] != '-') break;
            }

            return last!;
        }
    }

    private static async Task<(Session Session, SMTPServer Server)> NewServerWithAuthAsync()
    {
        var port = AllocatePort();

        var opts = new ServerOptions(validateSPF: false, validateDMARC: false, dnsServerEndpoint: null)
        {
            ServerName = "test.local",
            RequireEncryptionForAuth = false
        };

        var server = new SMTPServer(
            new[] { new ListeningParameters(IPAddress.Loopback, new[] { port }, null) },
            opts, new NoopDelivery());
        server.SetAuthLogin(new TestAuth());
        server.Start();

        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port).WaitAsync(ReadTimeout);
        var stream = tcp.GetStream();
        var session = new Session(tcp, new StreamReader(stream, Encoding.UTF8, leaveOpen: true), stream);
        await session.ReadLine(); // 220 greeting
        return (session, server);
    }

    private static async Task EhloAsync(Session s)
    {
        await s.Send("EHLO test.client");
        var final = await s.ReadResponse();
        if (!final.StartsWith("250")) throw new InvalidOperationException("EHLO failed: " + final);
    }

    // ─── tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AuthLogin_Bare_TwoStepFlow_StillWorks()
    {
        var (session, server) = await NewServerWithAuthAsync();
        using (session)
        using (server)
        {
            await EhloAsync(session);

            // Standard RFC 4954 flow without initial response must be unchanged.
            await session.Send("AUTH LOGIN");
            Assert.StartsWith("334", await session.ReadLine()); // username prompt

            await session.Send(B64User);
            Assert.StartsWith("334", await session.ReadLine()); // password prompt

            await session.Send(B64Pass);
            Assert.StartsWith("235", await session.ReadResponse());
        }
    }

    [Fact]
    public async Task AuthLogin_WithInlineUsername_GoesStraightToPasswordPrompt()
    {
        var (session, server) = await NewServerWithAuthAsync();
        using (session)
        using (server)
        {
            await EhloAsync(session);

            // IIS SMTP relay style: username is sent as the initial response, then only the password.
            await session.Send($"AUTH LOGIN {B64User}");
            Assert.StartsWith("334", await session.ReadLine()); // must be the PASSWORD prompt now

            await session.Send(B64Pass);
            Assert.StartsWith("235", await session.ReadResponse());
        }
    }

    [Fact]
    public async Task AuthLogin_WithInlineUsername_WrongPassword_Returns535()
    {
        var (session, server) = await NewServerWithAuthAsync();
        using (session)
        using (server)
        {
            await EhloAsync(session);

            await session.Send($"AUTH LOGIN {B64User}");
            Assert.StartsWith("334", await session.ReadLine());

            await session.Send(Convert.ToBase64String(Encoding.UTF8.GetBytes("wrong")));
            Assert.StartsWith("535", await session.ReadResponse());
        }
    }

    [Fact]
    public async Task AuthLogin_WithInvalidBase64InitialResponse_FallsBackToUsernamePrompt()
    {
        var (session, server) = await NewServerWithAuthAsync();
        using (session)
        using (server)
        {
            await EhloAsync(session);

            // Not valid base64 → treated as if no initial response was given.
            await session.Send("AUTH LOGIN !!!not-base64!!!");
            Assert.StartsWith("334", await session.ReadLine()); // username prompt

            await session.Send(B64User);
            Assert.StartsWith("334", await session.ReadLine()); // password prompt

            await session.Send(B64Pass);
            Assert.StartsWith("235", await session.ReadResponse());
        }
    }
}
