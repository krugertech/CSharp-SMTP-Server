using System.Net;
using CSharp_SMTP_Server.Protocol;
using CSharp_SMTP_Server.Protocol.Responses;

namespace CSharp_SMTP_Server.Tests;

/// <summary>MAIL FROM wire matrix, sender filter, and SPF-disabled behavior. See TESTING.md.</summary>
public sealed class MailFromTests
{
    private static async Task<(SmtpSession S, SMTPServer Server, RecordingDelivery Delivery)> ConnectAsync(
        ConfigurableFilter? filter = null)
    {
        var port = TestPorts.Allocate();
        var delivery = new RecordingDelivery();
        var server = TestServers.Build(port, delivery: delivery, filter: filter);
        server.Start();

        var s = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await s.ReadLineAsync());
        await s.Send("EHLO test.client");
        await s.ReadResponseAsync();
        return (s, server, delivery);
    }

    [Theory]
    [InlineData("MAIL FROM:<a@b.com>", "250 2.0.0")]
    [InlineData("MAIL FROM: <a@b.com>", "250 2.0.0")] // space after the colon is tolerated
    [InlineData("MAIL FROM:a@b.com", "501 5.5.2")]     // no angle brackets
    [InlineData("MAIL FROM:<a@b>", "501 5.5.2")]       // domain without a dot
    [InlineData("MAIL FROM:<a@@b.c>", "501 5.5.2")]    // two @ signs
    [InlineData("MAIL FROM:<a.b@c>", "501 5.5.2")]     // last dot before the @
    // The null reverse-path is required to be accepted by RFC 5321 §4.5.5 — it is the sender of
    // every DSN/bounce. See Load/Office365RelayTests.NullSender_IsAccepted_AndDelivered.
    [InlineData("MAIL FROM:<>", "250 2.0.0")]
    [InlineData("MAIL FROM:<> SIZE=1234", "250 2.0.0")] // null path with ESMTP parameters
    public async Task MailFrom_WireMatrix(string command, string expected)
    {
        var (s, server, _) = await ConnectAsync();
        using (server)
        await using (s)
        {
            await s.Send(command);
            Assert.Equal(expected, await s.ReadLineAsync());
        }
    }

    [Fact]
    public async Task MailFrom_DisplayNameForm_Accepted_AndFromIsTheAddress()
    {
        var (s, server, delivery) = await ConnectAsync();
        using (server)
        await using (s)
        {
            await s.Send("MAIL FROM:\"J\" <j@e.c>");
            Assert.Equal("250 2.0.0", await s.ReadLineAsync());

            await s.Send("RCPT TO:<c@d.e>");
            Assert.StartsWith("250", await s.ReadLineAsync());
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send("Subject: t");
            await s.Send(".");
            Assert.StartsWith("250", await s.ReadLineAsync());

            // The display name is stripped — the envelope From is the bare address.
            Assert.Equal("j@e.c", delivery.Delivered[0].From);
        }
    }

    [Theory]
    [InlineData(SmtpResultType.PermanentFail, null, "554 5.7.1 Delivery not authorized (MAIL FROM address not allowed), message refused")]
    [InlineData(SmtpResultType.TemporaryFail, null, "554 4.7.1 Delivery not authorized (MAIL FROM address not allowed), message refused")]
    [InlineData(SmtpResultType.PermanentFail, "nope", "554 5.7.1 nope")]
    public async Task IsAllowedSender_Rejection_MapsToWire(SmtpResultType type, string? customMessage, string expected)
    {
        var filter = new ConfigurableFilter
        {
            Sender = new SmtpResult(type, customMessage)
        };

        var (s, server, _) = await ConnectAsync(filter);
        using (server)
        await using (s)
        {
            await s.Send("MAIL FROM:<a@b.com>");
            Assert.Equal(expected, await s.ReadLineAsync());
        }
    }

    [Fact]
    public async Task Filter_ReceivesCorrectArgs_WhenUnauthenticated()
    {
        var filter = new ConfigurableFilter();
        var (s, server, _) = await ConnectAsync(filter);
        using (server)
        await using (s)
        {
            await s.Send("MAIL FROM:<a@b.com>");
            Assert.Equal("250 2.0.0", await s.ReadLineAsync());

            Assert.Equal("a@b.com", filter.LastSender);
            Assert.IsType<IPEndPoint>(filter.LastSenderEp);
            Assert.True(((IPEndPoint)filter.LastSenderEp!).Address.Equals(IPAddress.Loopback));
            Assert.Null(filter.LastSenderUsername); // unauthenticated session
        }
    }

    [Fact]
    public async Task SpfDisabled_SetsCheckDisabled_OnTransaction()
    {
        var filter = new ConfigurableFilter();
        var (s, server, delivery) = await ConnectAsync(filter);
        using (server)
        await using (s)
        {
            await s.Send("MAIL FROM:<a@b.com>");
            Assert.Equal("250 2.0.0", await s.ReadLineAsync());

            // With SPF disabled the second filter hook still runs, with CheckDisabled as the result.
            Assert.Equal(ValidationResult.CheckDisabled, filter.LastSpfResult);

            await s.Send("RCPT TO:<c@d.e>");
            Assert.StartsWith("250", await s.ReadLineAsync());
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");
            Assert.StartsWith("250", await s.ReadLineAsync());

            Assert.Equal(ValidationResult.CheckDisabled, delivery.Delivered[0].SPFValidationResult);
        }
    }
}
