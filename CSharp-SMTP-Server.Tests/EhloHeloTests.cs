using System.Net;
using CSharp_SMTP_Server.Interfaces;

namespace CSharp_SMTP_Server.Tests;

/// <summary>TEST_PLAN.md §4.2 — EHLO/HELO responses, transaction reset, command-parsing edge cases.</summary>
public sealed class EhloHeloTests
{
    private static async Task<SmtpSession> ConnectGreetedAsync(ushort port)
    {
        var s = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await s.ReadLineAsync());
        return s;
    }

    [Fact]
    public async Task Ehlo_NoAuthNoCert_AdvertisesOnly8BitMime()
    {
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port); // no auth, no certificate
        server.Start();

        await using var s = await ConnectGreetedAsync(port);
        await s.Send("EHLO test.client");

        Assert.Equal(new[] { "250-test.local at your service", "250 8BITMIME" },
            (await s.ReadResponseAsync()).ToArray());
    }

    [Fact]
    public async Task Ehlo_WithAuth_AddsAuthLine()
    {
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port, auth: new StaticAuth());
        server.Start();

        await using var s = await ConnectGreetedAsync(port);
        await s.Send("EHLO test.client");

        Assert.Equal(new[] { "250-test.local at your service", "250-AUTH LOGIN PLAIN", "250 8BITMIME" },
            (await s.ReadResponseAsync()).ToArray());
    }

    [Fact]
    public async Task Helo_SingleLine_NoExtensions()
    {
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port);
        server.Start();

        await using var s = await ConnectGreetedAsync(port);
        await s.Send("HELO test.client");

        Assert.Equal("250 test.local at your service", await s.ReadLineAsync());
    }

    [Fact]
    public async Task Ehlo_ResetsInFlightTransaction()
    {
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port);
        server.Start();

        await using var s = await ConnectGreetedAsync(port);
        await s.Send("EHLO test.client");
        await s.ReadResponseAsync();

        await s.Send("MAIL FROM:<a@b.com>");
        Assert.StartsWith("250", await s.ReadLineAsync());
        await s.Send("RCPT TO:<c@d.e>");
        Assert.StartsWith("250", await s.ReadLineAsync());

        // A new EHLO wipes the in-flight transaction…
        await s.Send("EHLO test.client");
        await s.ReadResponseAsync();

        // …so DATA is rejected as out of sequence.
        await s.Send("DATA");
        Assert.Equal("503 5.5.1 RCPT TO first.", await s.ReadLineAsync());
    }

    [Fact]
    public async Task Commands_AreCaseInsensitive()
    {
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port);
        server.Start();

        await using var s = await ConnectGreetedAsync(port);

        await s.Send("ehlo test.client");
        Assert.StartsWith("250", (await s.ReadResponseAsync())[^1]);

        await s.Send("hElO test.client");
        Assert.StartsWith("250", (await s.ReadResponseAsync())[^1]);

        // Two-arg WriteCode → no table text (Q7): just "250 2.0.0".
        await s.Send("mail from:<a@b.com>");
        Assert.Equal("250 2.0.0", await s.ReadLineAsync());
    }

    [Fact]
    public async Task EmptyLine_Returns503BeforeEhlo_502After()
    {
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port);
        server.Start();

        await using var s = await ConnectGreetedAsync(port);

        // Before EHLO an empty line hits the default case with protocol version 0.
        await s.Send("");
        Assert.Equal("503 5.5.1 EHLO/HELO first.", await s.ReadLineAsync());

        // After EHLO the same line is simply an unrecognized command (pin current behavior;
        // no table text because of Q7).
        await s.Send("EHLO test.client");
        await s.ReadResponseAsync();
        await s.Send("");
        Assert.Equal("502 5.5.1", await s.ReadLineAsync());
    }

    [Fact]
    public async Task DoubleSpaceInCommand_TreatedAsUnknown()
    {
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port);
        server.Start();

        await using var s = await ConnectGreetedAsync(port);
        await s.Send("EHLO test.client");
        await s.ReadResponseAsync();

        // "MAIL  FROM" (double space) does not match the "MAIL FROM" case — pin current behavior
        // (no table text because of Q7).
        await s.Send("MAIL  FROM:<a@b.com>");
        Assert.Equal("502 5.5.1", await s.ReadLineAsync());
    }

    [Fact]
    public async Task TrailingWhitespace_IsTolerated()
    {
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port);
        server.Start();

        await using var s = await ConnectGreetedAsync(port);
        await s.Send("EHLO test.client   ");

        Assert.Equal("250 8BITMIME", (await s.ReadResponseAsync())[^1]);
    }

    [Fact]
    public async Task Helo_WithBracketedIpv6Literal_Works()
    {
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port);
        server.Start();

        await using var s = await ConnectGreetedAsync(port);
        await s.Send("HELO [IPv6:fe80::1]");

        Assert.Equal("250 test.local at your service", await s.ReadLineAsync());

        // Session stays usable.
        await s.Send("NOOP");
        Assert.StartsWith("250", await s.ReadLineAsync());
    }

    private sealed class StaticAuth : IAuthLogin
    {
        public Task<bool> AuthPlain(string authorizationIdentity, string authenticationIdentity,
            string password, EndPoint remoteEndPoint, bool secureConnection) => Task.FromResult(false);

        public Task<bool> AuthLogin(string login, string password,
            EndPoint remoteEndPoint, bool secureConnection) => Task.FromResult(false);
    }
}
