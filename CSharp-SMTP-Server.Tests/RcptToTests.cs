using CSharp_SMTP_Server.Protocol.Responses;

namespace CSharp_SMTP_Server.Tests;

/// <summary>RCPT TO sequencing, UserExistsCodes wire mapping, limits, and CanDeliver. See TESTING.md.</summary>
public sealed class RcptToTests
{
    private static async Task<(SmtpSession S, SMTPServer Server, RecordingDelivery Delivery)> ConnectWithMailFromAsync(
        ConfigurableFilter? filter = null, ServerOptions? options = null)
    {
        var port = TestPorts.Allocate();
        var delivery = new RecordingDelivery();
        var server = TestServers.Build(port, options ?? TestServers.DefaultOptions(), delivery: delivery, filter: filter);
        server.Start();

        var s = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await s.ReadLineAsync());
        await s.Send("EHLO test.client");
        await s.ReadResponseAsync();
        await s.Send("MAIL FROM:<a@b.com>");
        Assert.Equal("250 2.0.0", await s.ReadLineAsync());
        return (s, server, delivery);
    }

    [Fact]
    public async Task RcptTo_BeforeMailFrom_Returns503()
    {
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port);
        server.Start();

        await using var s = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await s.ReadLineAsync());
        await s.Send("EHLO test.client");
        await s.ReadResponseAsync();

        await s.Send("RCPT TO:<a@b.com>");
        Assert.Equal("503 5.5.1 MAIL FROM first.", await s.ReadLineAsync());
    }

    [Fact]
    public async Task RcptTo_InvalidAddress_ReturnsBare501_NoEnhancedStatus()
    {
        // Pin Q5 (KNOWN_ISSUES.md): unlike every other error response, the RCPT syntax error is a
        // bare 501 with table text but NO enhanced status code.
        var (s, server, _) = await ConnectWithMailFromAsync();
        using (server)
        await using (s)
        {
            await s.Send("RCPT TO:a@b.com"); // no angle brackets → ProcessAddress returns null
            Assert.Equal("501 Syntax error in parameters or arguments", await s.ReadLineAsync());
        }
    }

    [Theory]
    [InlineData(UserExistsCodes.DestinationAddressValid, "250 2.1.5")]
    [InlineData(UserExistsCodes.BadDestinationMailboxAddress, "550 5.1.1 Requested action not taken: Bad destination mailbox address")]
    [InlineData(UserExistsCodes.BadDestinationSystemAddress, "550 5.1.2 Requested action not taken: Bad destination system address")]
    [InlineData(UserExistsCodes.DestinationMailboxAddressAmbiguous, "550 5.1.4 Requested action not taken: Destination mailbox address ambiguous")]
    [InlineData(UserExistsCodes.DestinationAddressHasMovedAndNoForwardingAddress, "550 5.1.6 Requested action not taken: Destination mailbox has moved, No forwarding address")]
    [InlineData(UserExistsCodes.BadSendersSystemAddress, "550 5.1.8 Requested action not taken: Bad sender's mailbox address syntax")]
    public async Task DoesUserExist_MapsToExactWireResponse(UserExistsCodes code, string expected)
    {
        var delivery = new RecordingDelivery { NextUserExistsCode = code };
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port, delivery: delivery);
        server.Start();

        await using var s = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await s.ReadLineAsync());
        await s.Send("EHLO test.client");
        await s.ReadResponseAsync();
        await s.Send("MAIL FROM:<a@b.com>");
        Assert.Equal("250 2.0.0", await s.ReadLineAsync());

        await s.Send("RCPT TO:<x@y.z>");
        Assert.Equal(expected, await s.ReadLineAsync());
    }

    [Fact]
    public async Task RecipientsLimit_Boundary_ExactlyAtLimitAllowed_OneOverRejected()
    {
        var options = TestServers.DefaultOptions();
        options.RecipientsLimit = 2;

        var (s, server, delivery) = await ConnectWithMailFromAsync(options: options);
        using (server)
        await using (s)
        {
            await s.Send("RCPT TO:<r1@x.y>");
            Assert.Equal("250 2.1.5", await s.ReadLineAsync());

            await s.Send("RCPT TO:<r2@x.y>"); // exactly at the limit → still accepted
            Assert.Equal("250 2.1.5", await s.ReadLineAsync());

            await s.Send("RCPT TO:<r3@x.y>");
            Assert.Equal("550 5.5.3 Too many recipients", await s.ReadLineAsync());

            // Only the first two made it into the transaction.
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");
            Assert.StartsWith("250", await s.ReadLineAsync());

            Assert.Equal(new[] { "r1@x.y", "r2@x.y" }, delivery.Delivered[0].DeliverTo.ToArray());
        }
    }

    [Theory]
    [InlineData(SmtpResultType.PermanentFail, null, "550 5.7.1 Delivery to this recipients is not allowed, message refused")]
    [InlineData(SmtpResultType.TemporaryFail, null, "550 4.7.1 Delivery to this recipients is not allowed, message refused")]
    [InlineData(SmtpResultType.PermanentFail, "nope", "550 5.7.1 nope")]
    public async Task CanDeliver_Rejection_MapsToWire(SmtpResultType type, string? customMessage, string expected)
    {
        var filter = new ConfigurableFilter { Deliver = new SmtpResult(type, customMessage) };

        var (s, server, _) = await ConnectWithMailFromAsync(filter: filter);
        using (server)
        await using (s)
        {
            await s.Send("RCPT TO:<x@y.z>");
            Assert.Equal(expected, await s.ReadLineAsync());
        }
    }

    [Fact]
    public async Task MultipleRcpts_AccumulateInOrder()
    {
        var (s, server, delivery) = await ConnectWithMailFromAsync();
        using (server)
        await using (s)
        {
            foreach (var rcpt in new[] { "a@x.y", "b@x.y", "c@x.y" })
            {
                await s.Send($"RCPT TO:<{rcpt}>");
                Assert.Equal("250 2.1.5", await s.ReadLineAsync());
            }

            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");
            Assert.StartsWith("250", await s.ReadLineAsync());

            Assert.Equal(new[] { "a@x.y", "b@x.y", "c@x.y" }, delivery.Delivered[0].DeliverTo.ToArray());
        }
    }
}
