namespace CSharp_SMTP_Server.Tests;

/// <summary>Command sequencing and miscellaneous commands (NOOP/HELP/QUIT/RSET/VRFY). See TESTING.md.</summary>
public sealed class CommandSequencingTests
{
    private static async Task<SmtpSession> ConnectGreetedAsync(ushort port)
    {
        var s = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await s.ReadLineAsync());
        return s;
    }

    [Theory]
    [InlineData("AUTH PLAIN dXNlcg==")]
    [InlineData("RSET")]
    [InlineData("MAIL FROM:<a@b.com>")]
    [InlineData("RCPT TO:<a@b.com>")]
    [InlineData("DATA")]
    [InlineData("VRFY user")]
    public async Task ExtensionCommands_BeforeEhlo_Return503(string command)
    {
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port);
        server.Start();

        await using var s = await ConnectGreetedAsync(port);
        await s.Send(command);

        Assert.Equal("503 5.5.1 EHLO/HELO first.", await s.ReadLineAsync());
    }

    [Fact]
    public async Task UnknownCommand_AfterEhlo_Returns502()
    {
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port);
        server.Start();

        await using var s = await ConnectGreetedAsync(port);
        await s.Send("EHLO test.client");
        await s.ReadResponseAsync();

        // Note: two-arg WriteCode(code, enhanced) call sites bind to the (int, string) sanitizer
        // overload (no implicit int→ushort conversion), so no table text is appended — see Q7 in
        // KNOWN_ISSUES.md R1/Q7. The wire line is just "code enhanced".
        await s.Send("FOOBAR");
        Assert.Equal("502 5.5.1", await s.ReadLineAsync());
    }

    [Fact]
    public async Task UnknownCommand_BeforeEhlo_Returns503()
    {
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port);
        server.Start();

        await using var s = await ConnectGreetedAsync(port);
        await s.Send("FOOBAR");

        Assert.Equal("503 5.5.1 EHLO/HELO first.", await s.ReadLineAsync());
    }

    [Fact]
    public async Task Noop_Returns250Ok()
    {
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port);
        server.Start();

        await using var s = await ConnectGreetedAsync(port);
        await s.Send("EHLO test.client");
        await s.ReadResponseAsync();

        // Two-arg WriteCode → no table text (Q7): just "250 2.0.0".
        await s.Send("NOOP");
        Assert.Equal("250 2.0.0", await s.ReadLineAsync());
    }

    [Fact]
    public async Task Help_Returns214_NoHelpMessage()
    {
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port);
        server.Start();

        await using var s = await ConnectGreetedAsync(port);
        await s.Send("EHLO test.client");
        await s.ReadResponseAsync();

        // Two-arg WriteCode → the "There is no help for you" table text is NOT emitted (Q7).
        await s.Send("HELP");
        Assert.Equal("214 2.0.0", await s.ReadLineAsync());
    }

    [Fact]
    public async Task Quit_Returns221_AndClosesConnection()
    {
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port);
        server.Start();

        await using var s = await ConnectGreetedAsync(port);
        await s.Send("EHLO test.client");
        await s.ReadResponseAsync();

        // Two-arg WriteCode → no table text (Q7).
        await s.Send("QUIT");
        Assert.Equal("221 2.0.0", await s.ReadLineAsync());

        // The server closes the connection after QUIT — no further data ever arrives.
        Assert.Null(await s.ReadLineAsync());
    }

    [Fact]
    public async Task Rset_MidTransaction_ClearsState()
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

        await s.Send("RSET");
        Assert.Equal("250 2.1.5 Flushed", await s.ReadLineAsync());

        // Transaction cleared → DATA is out of sequence again.
        await s.Send("DATA");
        Assert.Equal("503 5.5.1 RCPT TO first.", await s.ReadLineAsync());
    }

    [Fact]
    public async Task Vrfy_AfterEhlo_Returns252_WithPermanentEnhancedStatus()
    {
        // Pin Q2 (KNOWN_ISSUES.md): success-class code 252 paired with permanent-failure enhanced
        // status 5.5.1 — inconsistent per RFC 3463, but this is the current wire behavior.
        // (The "Cannot VRFY user…" table text is not emitted because of Q7.)
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port);
        server.Start();

        await using var s = await ConnectGreetedAsync(port);
        await s.Send("EHLO test.client");
        await s.ReadResponseAsync();

        await s.Send("VRFY user");
        Assert.Equal("252 5.5.1", await s.ReadLineAsync());
    }
}
