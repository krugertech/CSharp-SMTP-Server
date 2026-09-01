using System.Net;
using CSharp_SMTP_Server.Interfaces;
using CSharp_SMTP_Server.Networking;

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
    public async Task Ehlo_NoAuthNoCert_Advertises8BitMimeAndSize()
    {
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port); // no auth, no certificate
        server.Start();

        await using var s = await ConnectGreetedAsync(port);
        await s.Send("EHLO test.client");

        Assert.Equal(
            new[] { "250-test.local at your service", "250-8BITMIME", "250 SIZE 10485760" },
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

        Assert.Equal(
            new[]
            {
                "250-test.local at your service", "250-AUTH LOGIN PLAIN", "250-8BITMIME",
                "250 SIZE 10485760"
            },
            (await s.ReadResponseAsync()).ToArray());
    }

    /// <summary>
    /// The advertised RFC 1870 SIZE must be a value the server will never reject, and must not
    /// needlessly understate capacity.
    /// </summary>
    /// <remarks>
    /// The character limit is already a safe understatement of the octet count: ProcessData counts
    /// each line's characters after CRLF is stripped, so octets = sum(line bytes) + 2 * lines is
    /// always >= the counted characters. CRLF, UTF-8 multibyte and dot-stuffing all push octets up
    /// relative to characters, never down, so the limit passes through unchanged.
    ///
    /// In particular a small finite limit must NOT round down to 0, which RFC 1870 §6 reads as "no
    /// fixed maximum" — that would advertise unlimited on a server that refuses at two characters.
    /// </remarks>
    [Theory]
    [InlineData(10485760u, 10485760u)]
    [InlineData(200u * 1024 * 1024, 200u * 1024 * 1024)]
    [InlineData(1u, 1u)]  // must not become 0 ("no fixed maximum")
    [InlineData(0u, 0u)]  // genuinely unlimited
    public void AdvertisedSizeLimit_NeverOverstates_AndPreservesFiniteLimits(uint limit, uint expected)
        => Assert.Equal(expected, ClientProcessor.AdvertisedSizeLimit(limit));

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

        Assert.Equal("250 SIZE 10485760", (await s.ReadResponseAsync())[^1]);
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

    /// <summary>
    /// The EHLO/HELO argument is retained as a checkable domain, or as null when it is not one.
    /// </summary>
    /// <remarks>
    /// Retained for the RFC 7208 §2.4 SPF check, which is the identity for a null reverse-path. Only a
    /// plausible DNS name is kept: an address literal (RFC 5321 §4.1.3) and a bare label cannot carry
    /// an SPF record, and storing null for those makes "no checkable identity" explicit at the point
    /// of capture rather than something each consumer re-derives.
    /// </remarks>
    [Theory]
    [InlineData("mail.example.com", "mail.example.com")]
    [InlineData("  mail.example.com  ", "mail.example.com")]   // surrounding whitespace
    [InlineData("mail.example.com.", "mail.example.com")]      // absolute form, trailing dot stripped
    [InlineData("MAIL.Example.COM", "MAIL.Example.COM")]       // case preserved; comparisons are ordinal-ignore-case
    [InlineData("a-b.example.com", "a-b.example.com")]         // hyphens are legal in a label
    // Not checkable identities:
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("[192.0.2.1]", null)]                          // address literal
    [InlineData("[IPv6:fe80::1]", null)]                       // IPv6 literal
    [InlineData("localhost", null)]                            // bare label: no dot, no SPF record
    [InlineData("mail example com", null)]                     // spaces are not DNS syntax
    [InlineData("mail.example.com extra", null)]
    [InlineData("mail_host.example.com", null)]                // underscore is not a hostname character
    public void ParseHeloDomain_KeepsOnlyCheckableDomains(string? argument, string? expected) =>
        Assert.Equal(expected, ClientProcessor.ParseHeloDomain(argument));

    /// <summary>
    /// A second EHLO replaces the retained identity rather than leaving the first in place.
    /// </summary>
    /// <remarks>
    /// EHLO resets the session (RFC 5321 §4.1.4), so a client that re-greets with a different name
    /// must be checked against the new one — otherwise a client could present a domain that passes
    /// SPF, then re-greet as something else and keep the first result.
    /// </remarks>
    [Fact]
    public async Task Ehlo_Reissued_ReplacesRetainedHeloDomain()
    {
        var port = TestPorts.Allocate();
        var delivery = new RecordingDelivery();
        using var server = TestServers.Build(port, delivery: delivery);
        server.Start();

        await using var s = await ConnectGreetedAsync(port);
        await s.Send("EHLO first.example.com");
        await s.ReadResponseAsync();
        await s.Send("EHLO second.example.com");
        await s.ReadResponseAsync();

        await s.Send("MAIL FROM:<a@b.com>");
        Assert.StartsWith("250", await s.ReadLineAsync());
        await s.Send("RCPT TO:<r@example.com>");
        Assert.StartsWith("250", await s.ReadLineAsync());
        await s.Send("DATA");
        Assert.StartsWith("354", await s.ReadLineAsync());
        await s.Send("Subject: t");
        await s.Send("");
        await s.Send("body");
        await s.Send(".");
        Assert.StartsWith("250", await s.ReadLineAsync());

        Assert.Equal("second.example.com", delivery.Delivered.Single().HeloDomain);
    }

    private sealed class StaticAuth : IAuthLogin
    {
        public Task<bool> AuthPlain(string authorizationIdentity, string authenticationIdentity,
            string password, EndPoint remoteEndPoint, bool secureConnection) => Task.FromResult(false);

        public Task<bool> AuthLogin(string login, string password,
            EndPoint remoteEndPoint, bool secureConnection) => Task.FromResult(false);
    }
}
