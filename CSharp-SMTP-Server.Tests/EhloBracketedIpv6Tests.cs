using System;
using System.Net;
using CSharp_SMTP_Server;
using CSharp_SMTP_Server.Networking;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Integration tests for EHLO/HELO with a bracketed IPv6 literal, e.g. "EHLO [IPv6:fe80::1]" as sent
/// by Thunderbird — upstream issue #18 (https://github.com/zabszk/CSharp-SMTP-Server/issues/18).
/// Before the fix the command parser split at the first ':' inside the brackets, so the command was
/// misread and the server answered 503 "EHLO/HELO first", breaking the whole session.
/// </summary>
public sealed class EhloBracketedIpv6Tests
{
    // ─── helpers ────────────────────────────────────────────────────────────────

    private static async Task<(SmtpSession Session, SMTPServer Server)> NewServerAsync()
    {
        var port = TestPorts.Allocate();

        var opts = new ServerOptions(validateSPF: false, validateDMARC: false, dnsServerEndpoint: null)
        {
            ServerName = "test.local"
        };

        var server = new SMTPServer(
            new[] { new ListeningParameters(IPAddress.Loopback, new[] { port }, null) },
            opts, NoopDelivery.Instance);
        server.Start();

        var session = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await session.ReadLineAsync()); // 220 greeting
        return (session, server);
    }

    // ─── tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ehlo_WithBracketedIPv6_IsAccepted()
    {
        var (session, server) = await NewServerAsync();
        using (server)
        await using (session)
        {
            // Thunderbird-style EHLO with a bracketed IPv6 literal. Before the fix this was
            // misparsed at the first ':' inside the brackets and answered 503 "EHLO/HELO first".
            await session.Send("EHLO [IPv6:fe80::e909:5b3d:357b:6234]");

            var lines = await session.ReadResponseAsync();
            Assert.StartsWith("250", lines[^1]);

            // The session must be usable afterwards.
            await session.Send("NOOP");
            lines = await session.ReadResponseAsync();
            Assert.StartsWith("250", lines[^1]);

            await session.Send("QUIT");
            lines = await session.ReadResponseAsync();
            Assert.StartsWith("221", lines[^1]);
        }
    }

    [Fact]
    public async Task Ehlo_WithPlainHostname_StillWorks()
    {
        var (session, server) = await NewServerAsync();
        using (server)
        await using (session)
        {
            await session.Send("EHLO test.client");
            var lines = await session.ReadResponseAsync();
            Assert.StartsWith("250", lines[^1]);
        }
    }

    [Fact]
    public async Task MailFrom_WithBracketedIpv6Literal_StillParses()
    {
        var (session, server) = await NewServerAsync();
        using (server)
        await using (session)
        {
            await session.Send("EHLO test.client");
            var lines = await session.ReadResponseAsync();
            Assert.StartsWith("250", lines[^1]);

            // Regression guard: the bracket-aware colon scan must not break "MAIL FROM:" parsing,
            // including senders that use a bracketed IP literal in the address.
            await session.Send("MAIL FROM:<root@[IPv6:fe80::1]>");
            var response = (await session.ReadResponseAsync())[^1];

            // 250 (accepted) or 554/550 from filters would both prove correct command parsing;
            // a 503 "EHLO/HELO first" or 502 would mean the parser regressed.
            Assert.False(response.StartsWith("503") || response.StartsWith("502"),
                "MAIL FROM was misparsed: " + response);
        }
    }
}
