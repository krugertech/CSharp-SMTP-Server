using System.Net;
using CSharp_SMTP_Server.Interfaces;
using CSharp_SMTP_Server.Protocol;
using CSharp_SMTP_Server.Protocol.Responses;

namespace CSharp_SMTP_Server.Tests;

/// <summary>TEST_PLAN.md §4.1 — greeting format and the IsConnectionAllowed filter.</summary>
public sealed class GreetingAndFilterTests
{
    private sealed class RecordingFilter : IMailFilter
    {
        public SmtpResult ConnectionResult = new(SmtpResultType.Success);
        public EndPoint? SeenEndpoint;

        public Task<SmtpResult> IsConnectionAllowed(EndPoint? ep)
        {
            SeenEndpoint = ep;
            return Task.FromResult(ConnectionResult);
        }

        public Task<SmtpResult> IsAllowedSender(string source, EndPoint? ep, string? username) =>
            Task.FromResult(new SmtpResult(SmtpResultType.Success));

        public Task<SmtpResult> IsAllowedSenderSpfVerified(string source, EndPoint? ep, string? username, ValidationResult validationResult) =>
            Task.FromResult(new SmtpResult(SmtpResultType.Success));

        public Task<SmtpResult> CanDeliver(string source, string destination, bool authenticated, string? username, EndPoint? ep) =>
            Task.FromResult(new SmtpResult(SmtpResultType.Success));

        public Task<SmtpResult> CanProcessTransaction(MailTransaction transaction) =>
            Task.FromResult(new SmtpResult(SmtpResultType.Success));
    }

    [Fact]
    public async Task Greeting_IsExactly_220_DefaultServerName_ESMTP()
    {
        var port = TestPorts.Allocate();
        using var server = new SMTPServer(
            new[] { new Networking.ListeningParameters(IPAddress.Loopback, new[] { port }, null) },
            new ServerOptions(false, false), NoopDelivery.Instance);
        server.Start();

        await using var s = await SmtpSession.ConnectAsync(port);
        Assert.Equal("220 CSharp SMTP Server ESMTP", await s.ReadLineAsync());
    }

    [Fact]
    public async Task Greeting_UsesConfiguredServerName()
    {
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port, TestServers.DefaultOptions("MyMail"));
        server.Start();

        await using var s = await SmtpSession.ConnectAsync(port);
        Assert.Equal("220 MyMail ESMTP", await s.ReadLineAsync());
    }

    [Theory]
    [InlineData(SmtpResultType.PermanentFail, "5.7.1")]
    [InlineData(SmtpResultType.TemporaryFail, "4.7.1")]
    public async Task Filter_RejectsConnection_BeforeAnyGreeting(SmtpResultType type, string enhanced)
    {
        var filter = new RecordingFilter { ConnectionResult = new SmtpResult(type) };
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port, filter: filter);
        server.Start();

        await using var s = await SmtpSession.ConnectAsync(port);

        // The rejection is the very first line — no 220 ever arrives.
        Assert.Equal($"550 {enhanced} Delivery not authorized, connection refused", await s.ReadLineAsync());

        // And the server closes the connection immediately afterwards.
        Assert.Null(await s.ReadLineAsync());
    }

    [Fact]
    public async Task Filter_CustomFailMessage_ReplacesDefaultText()
    {
        var filter = new RecordingFilter { ConnectionResult = new SmtpResult(SmtpResultType.PermanentFail, "nope") };
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port, filter: filter);
        server.Start();

        await using var s = await SmtpSession.ConnectAsync(port);
        Assert.Equal("550 5.7.1 nope", await s.ReadLineAsync());
    }

    [Fact]
    public async Task Filter_WhitespaceOnlyFailMessage_FallsBackToDefault()
    {
        var filter = new RecordingFilter { ConnectionResult = new SmtpResult(SmtpResultType.TemporaryFail, "   ") };
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port, filter: filter);
        server.Start();

        await using var s = await SmtpSession.ConnectAsync(port);
        Assert.Equal("550 4.7.1 Delivery not authorized, connection refused", await s.ReadLineAsync());
    }

    [Fact]
    public async Task Filter_ReceivesRemoteEndpoint()
    {
        var filter = new RecordingFilter(); // Success by default
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port, filter: filter);
        server.Start();

        await using var s = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await s.ReadLineAsync());

        Assert.IsType<IPEndPoint>(filter.SeenEndpoint);
        var ipe = (IPEndPoint)filter.SeenEndpoint!;
        Assert.True(ipe.Address.Equals(IPAddress.Loopback));
        Assert.InRange(ipe.Port, 1, ushort.MaxValue);
    }
}
