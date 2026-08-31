using System.Net;
using CSharp_SMTP_Server.Interfaces;
using CSharp_SMTP_Server.Networking;
using CSharp_SMTP_Server.Protocol.Responses;

namespace CSharp_SMTP_Server.Tests;

/// <summary>TEST_PLAN.md §4.6 — DATA, message processing, size limits, Received header, dot-stuffing.</summary>
public sealed class DataAndMessageTests
{
    private static async Task<(SmtpSession S, SMTPServer Server, RecordingDelivery Delivery)> ConnectReadyAsync(
        ConfigurableFilter? filter = null, ServerOptions? options = null, IAuthLogin? auth = null)
    {
        var port = TestPorts.Allocate();
        var delivery = new RecordingDelivery();
        var server = TestServers.Build(port, options ?? TestServers.DefaultOptions(), delivery: delivery, auth: auth, filter: filter);
        server.Start();

        var s = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await s.ReadLineAsync());
        await s.Send("EHLO test.client");
        await s.ReadResponseAsync();
        return (s, server, delivery);
    }

    /// <summary>MAIL FROM + one RCPT TO, asserting 250 at each step.</summary>
    private static async Task StartTransactionAsync(SmtpSession s)
    {
        await s.Send("MAIL FROM:<a@b.com>");
        Assert.Equal("250 2.0.0", await s.ReadLineAsync());
        await s.Send("RCPT TO:<c@d.e>");
        Assert.Equal("250 2.1.5", await s.ReadLineAsync());
    }

    [Fact]
    public async Task Data_BeforeRcptTo_Returns503()
    {
        var (s, server, _) = await ConnectReadyAsync();
        using (server)
        await using (s)
        {
            await s.Send("MAIL FROM:<a@b.com>");
            Assert.Equal("250 2.0.0", await s.ReadLineAsync());

            await s.Send("DATA");
            Assert.Equal("503 5.5.1 RCPT TO first.", await s.ReadLineAsync());
        }
    }

    [Fact]
    public async Task NormalBody_DeliveredWithReceivedHeader()
    {
        var (s, server, delivery) = await ConnectReadyAsync();
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            // Single-arg WriteCode(354) → full table text (contrast with the two-arg Q7 cases).
            Assert.Equal("354 Start mail input; end with <CRLF>.<CRLF>", await s.ReadLineAsync());

            await s.Send("Subject: hi");
            await s.Send("From: a@b.com");
            await s.Send("To: c@d.e");
            await s.Send("");
            await s.Send("hello world");
            await s.Send(".");
            Assert.StartsWith("250", await s.ReadLineAsync());

            var raw = delivery.Delivered[0].RawBody;
            // Unauthenticated sender → "from <ip> by <server>" part present (Q3 baseline).
            Assert.StartsWith("Received: from 127.0.0.1 by test.local with SMTP; ", raw);
            Assert.Contains("+0000 (UTC)", raw);
            Assert.Contains("hello world", raw);
        }
    }

    [Fact]
    public async Task SizeLimit_Boundary_ExactlyAtLimitAccepted_OneOverRejected()
    {
        const int limit = 100; // characters, CRLF excluded (Q6)
        var options = TestServers.DefaultOptions();
        options.MessageCharactersLimit = limit;

        // Case 1: body exactly at the limit → accepted.
        {
            var (s, server, delivery) = await ConnectReadyAsync(options: options);
            using (server)
            await using (s)
            {
                await StartTransactionAsync(s);
                await s.Send("DATA");
                Assert.StartsWith("354", await s.ReadLineAsync());
                await s.Send(new string('a', limit)); // counter == limit → line kept
                await s.Send(".");
                Assert.StartsWith("250", await s.ReadLineAsync());
                Assert.Contains(new string('a', limit), delivery.Delivered[0].RawBody);
            }
        }

        // Case 2: one character over → 552 and the transaction is reset.
        {
            var (s, server, _) = await ConnectReadyAsync(options: options);
            using (server)
            await using (s)
            {
                await StartTransactionAsync(s);
                await s.Send("DATA");
                Assert.StartsWith("354", await s.ReadLineAsync());
                await s.Send(new string('a', limit + 1)); // over the limit → line dropped from RawBody
                await s.Send(".");
                Assert.Equal("552 5.4.3 Message size exceeds the administrative limit.", await s.ReadLineAsync());

                // Transaction was reset: DATA without MAIL FROM is out of sequence again.
                await s.Send("DATA");
                Assert.Equal("503 5.5.1 RCPT TO first.", await s.ReadLineAsync());
            }
        }
    }

    [Fact]
    public async Task SizeLimit_Zero_MeansNoLimit()
    {
        var options = TestServers.DefaultOptions();
        options.MessageCharactersLimit = 0;

        var (s, server, delivery) = await ConnectReadyAsync(options: options);
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(new string('a', 10_000)); // limit disabled → any size accepted
            await s.Send(".");
            Assert.StartsWith("250", await s.ReadLineAsync());
            Assert.Contains(new string('a', 10_000), delivery.Delivered[0].RawBody);
        }
    }

    [Fact]
    public async Task AuthenticatedReceivedHeader_OmitsFromPart()
    {
        // Pin Q3 (TEST_PLAN.md §2): for authenticated users the Received header has no "from <ip>"
        // part — just "by <server> with SMTP; …".
        var options = TestServers.DefaultOptions();
        options.RequireEncryptionForAuth = false; // allow AUTH over plaintext for this test

        var auth = new StaticAuth();
        var (s, server, delivery) = await ConnectReadyAsync(options: options, auth: auth);
        using (server)
        await using (s)
        {
            await s.Send("AUTH PLAIN " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("\0user\0pass")));
            Assert.StartsWith("235", await s.ReadLineAsync());

            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");
            Assert.StartsWith("250", await s.ReadLineAsync());

            var raw = delivery.Delivered[0].RawBody;
            Assert.StartsWith("Received: by test.local with SMTP; ", raw); // no "from …" part
            Assert.DoesNotContain("Received: from", raw);
            Assert.Equal("user", delivery.Delivered[0].AuthenticatedUser);
        }
    }

    [Fact]
    public async Task DotStuffing_IsNotSupported_BodyLinesStoredVerbatim()
    {
        // Pin Q1 (TEST_PLAN.md §2): no RFC 5321 dot-unstuffing — a body line starting with ".." is
        // stored as-is.
        var (s, server, delivery) = await ConnectReadyAsync();
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send("..foo"); // would be "un-stuffed" to ".foo" by a conforming server
            await s.Send(".");
            Assert.StartsWith("250", await s.ReadLineAsync());

            Assert.Contains("..foo\r\n", delivery.Delivered[0].RawBody);
        }
    }

    [Fact]
    public async Task LoneDotMidBody_TerminatesCapture()
    {
        var (s, server, delivery) = await ConnectReadyAsync();
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());

            await s.Send("a");
            await s.Send("."); // terminates the message here…
            Assert.StartsWith("250", await s.ReadLineAsync()); // …and delivery happens immediately

            // Everything after the terminating dot is parsed as commands again: "b" is unknown.
            await s.Send("b");
            Assert.Equal("502 5.5.1", await s.ReadLineAsync());

            Assert.Contains("\r\na\r\n", delivery.Delivered[0].RawBody);
        }
    }

    [Theory]
    [InlineData(SmtpResultType.PermanentFail, null, "554 5.7.1 Delivery not authorized, message refused")]
    [InlineData(SmtpResultType.TemporaryFail, null, "554 4.7.1 Delivery not authorized, message refused")]
    [InlineData(SmtpResultType.PermanentFail, "nope", "554 5.7.1 nope")]
    public async Task CanProcessTransaction_Rejection_MapsToWire_AndResetsTransaction(
        SmtpResultType type, string? customMessage, string expected)
    {
        var filter = new ConfigurableFilter { ProcessTransaction = new SmtpResult(type, customMessage) };

        var (s, server, _) = await ConnectReadyAsync(filter: filter);
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");
            Assert.Equal(expected, await s.ReadLineAsync());

            // Transaction reset → DATA without MAIL FROM is out of sequence again.
            await s.Send("DATA");
            Assert.Equal("503 5.5.1 RCPT TO first.", await s.ReadLineAsync());
        }
    }

    [Fact]
    public async Task NewTransaction_CanStart_AfterSuccessfulDelivery()
    {
        var (s, server, delivery) = await ConnectReadyAsync();
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");
            Assert.StartsWith("250", await s.ReadLineAsync());

            // The session is reusable for a fresh transaction.
            await s.Send("MAIL FROM:<a@b.com>");
            Assert.Equal("250 2.0.0", await s.ReadLineAsync());
        }
    }

    [Fact]
    public async Task DeliveredMetadata_PlaintextUnauthenticated()
    {
        var (s, server, delivery) = await ConnectReadyAsync();
        using (server)
        await using (s)
        {
            await StartTransactionAsync(s);
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send(".");
            Assert.StartsWith("250", await s.ReadLineAsync());

            var tx = delivery.Delivered[0];
            Assert.Equal("a@b.com", tx.From);
            Assert.Equal(new[] { "c@d.e" }, tx.DeliverTo.ToArray());
            Assert.IsType<IPEndPoint>(tx.RemoteEndPoint);
            Assert.True(((IPEndPoint)tx.RemoteEndPoint!).Address.Equals(IPAddress.Loopback));
            Assert.Equal(ConnectionEncryption.Plaintext, tx.Encryption);
            Assert.Null(tx.AuthenticatedUser);
        }
    }

    private sealed class StaticAuth : IAuthLogin
    {
        public Task<bool> AuthPlain(string authorizationIdentity, string authenticationIdentity,
            string password, EndPoint remoteEndPoint, bool secureConnection) =>
            Task.FromResult(authenticationIdentity == "user" && password == "pass");

        public Task<bool> AuthLogin(string login, string password,
            EndPoint remoteEndPoint, bool secureConnection) =>
            Task.FromResult(login == "user" && password == "pass");
    }
}
