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
/// Integration tests for EHLO/HELO with a bracketed IPv6 literal, e.g. "EHLO [IPv6:fe80::1]" as sent
/// by Thunderbird — upstream issue #18 (https://github.com/zabszk/CSharp-SMTP-Server/issues/18).
/// Before the fix the command parser split at the first ':' inside the brackets, so the command was
/// misread and the server answered 503 "EHLO/HELO first", breaking the whole session.
/// </summary>
public sealed class EhloBracketedIpv6Tests
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

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

    private static async Task<(Session Session, SMTPServer Server)> NewServerAsync()
    {
        var port = AllocatePort();

        var opts = new ServerOptions(validateSPF: false, validateDMARC: false, dnsServerEndpoint: null)
        {
            ServerName = "test.local"
        };

        var server = new SMTPServer(
            new[] { new ListeningParameters(IPAddress.Loopback, new[] { port }, null) },
            opts, new NoopDelivery());
        server.Start();

        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port).WaitAsync(ReadTimeout);
        var stream = tcp.GetStream();
        var session = new Session(tcp, new StreamReader(stream, Encoding.UTF8, leaveOpen: true), stream);
        await session.ReadLine(); // 220 greeting
        return (session, server);
    }

    // ─── tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ehlo_WithBracketedIPv6_IsAccepted()
    {
        var (session, server) = await NewServerAsync();
        using (session)
        using (server)
        {
            // Thunderbird-style EHLO with a bracketed IPv6 literal. Before the fix this was
            // misparsed at the first ':' inside the brackets and answered 503 "EHLO/HELO first".
            await session.Send("EHLO [IPv6:fe80::e909:5b3d:357b:6234]");

            var final = await session.ReadResponse();
            Assert.StartsWith("250", final);

            // The session must be usable afterwards.
            await session.Send("NOOP");
            Assert.StartsWith("250", await session.ReadResponse());

            await session.Send("QUIT");
            Assert.StartsWith("221", await session.ReadResponse());
        }
    }

    [Fact]
    public async Task Ehlo_WithPlainHostname_StillWorks()
    {
        var (session, server) = await NewServerAsync();
        using (session)
        using (server)
        {
            await session.Send("EHLO test.client");
            Assert.StartsWith("250", await session.ReadResponse());
        }
    }

    [Fact]
    public async Task MailFrom_WithBracketedIpv6Literal_StillParses()
    {
        var (session, server) = await NewServerAsync();
        using (session)
        using (server)
        {
            await session.Send("EHLO test.client");
            Assert.StartsWith("250", await session.ReadResponse());

            // Regression guard: the bracket-aware colon scan must not break "MAIL FROM:" parsing,
            // including senders that use a bracketed IP literal in the address.
            await session.Send("MAIL FROM:<root@[IPv6:fe80::1]>");
            var response = await session.ReadResponse();

            // 250 (accepted) or 554/550 from filters would both prove correct command parsing;
            // a 503 "EHLO/HELO first" or 502 would mean the parser regressed.
            Assert.False(response.StartsWith("503") || response.StartsWith("502"),
                "MAIL FROM was misparsed: " + response);
        }
    }
}
