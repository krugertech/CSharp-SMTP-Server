using System.Net;
using CSharp_SMTP_Server.Interfaces;
using CSharp_SMTP_Server.Protocol;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Phase 4 glue (TEST_PLAN.md §6/§7): SPF and DMARC validation wired through a full SMTP session —
/// the stub DNS server is pointed at via ServerOptions.DnsServerEndpoint, exactly like production.
/// Verifies where in the protocol each check fires: SPF at MAIL FROM (554 5.7.23 on Fail), DMARC at
/// DATA (554 5.7.1 on Fail), and the Authentication-Results headers added to delivered messages.
///
/// Note: SMTPServer constructs a DmarcValidator whenever DnsServerEndpoint is set (even with
/// ValidateDMARC=false) and its constructor downloads the Public Suffix List — so every test here
/// serves the list from loopback HTTP to stay offline.
/// </summary>
public sealed class SpfDmarcIntegrationTests
{
    private static async Task<(SmtpSession S, SMTPServer Server, RecordingDelivery Delivery)> ConnectReadyAsync(
        DnsStub stub, string suffixListUrl, bool validateSpf, bool validateDmarc)
    {
        var options = new ServerOptions(validateSpf, validateDmarc, new IPEndPoint(IPAddress.Loopback, (ushort)stub.Port))
        {
            ServerName = "test.local",
            PublicSuffixList = suffixListUrl
        };

        var port = TestPorts.Allocate();
        var delivery = new RecordingDelivery();
        var server = TestServers.Build(port, options, delivery: delivery);
        server.Start();

        var s = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await s.ReadLineAsync());
        await s.Send("EHLO test.client");
        await s.ReadResponseAsync();
        return (s, server, delivery);
    }

    #region SPF at MAIL FROM (§6)

    [Fact]
    public async Task MailFrom_SpfFail_RejectedWith554_5723_NoDelivery()
    {
        using var stub = new DnsStub();
        using var http = new LocalHttpServer(SuffixListFixture.CanonicalList);
        stub.AddTxt("bad.spf.test", "v=spf1 -all"); // fails for every client IP

        var (s, server, delivery) = await ConnectReadyAsync(stub, http.Url, validateSpf: true, validateDmarc: false);
        using (server)
        await using (s)
        {
            await s.Send("MAIL FROM:<a@bad.spf.test>");
            Assert.Equal("554 5.7.23 Delivery not authorized by SPF, message refused", await s.ReadLineAsync());

            Assert.Empty(delivery.Delivered); // rejected before RCPT/DATA — nothing can be delivered
        }
    }

    [Fact]
    public async Task MailFrom_SpfPass_Delivers_WithSpfResultAndArHeader()
    {
        using var stub = new DnsStub();
        using var http = new LocalHttpServer(SuffixListFixture.CanonicalList);
        // The test client connects from 127.0.0.1 — authorize exactly that address.
        stub.AddTxt("good.spf.test", "v=spf1 ip4:127.0.0.1 -all");

        var (s, server, delivery) = await ConnectReadyAsync(stub, http.Url, validateSpf: true, validateDmarc: false);
        using (server)
        await using (s)
        {
            await s.Send("MAIL FROM:<a@good.spf.test>");
            Assert.Equal("250 2.0.0", await s.ReadLineAsync()); // Q7: two-arg call → no table text
            await s.Send("RCPT TO:<r@example.com>");
            Assert.Equal("250 2.1.5", await s.ReadLineAsync());
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send("Subject: spf pass");
            await s.Send("");
            await s.Send("body");
            await s.Send(".");
            Assert.Equal("250 2.0.0 OK", await s.ReadLineAsync());

            var tx = delivery.Delivered.Single();
            Assert.Equal(ValidationResult.Pass, tx.SPFValidationResult);
            // SPF ran (not CheckDisabled/UserAuthenticated) → Authentication-Results header added.
            Assert.Contains("Authentication-Results: test.local; spf=pass smtp.mailfrom=good.spf.test", tx.RawBody);
        }
    }

    #endregion

    #region DMARC at DATA (§7)

    [Fact]
    public async Task Data_DmarcFail_RejectedWith554_571_NoDelivery()
    {
        using var stub = new DnsStub();
        using var http = new LocalHttpServer(SuffixListFixture.CanonicalList);
        // Header domain (in the display name, per B1) is header.com; envelope is example.com → unaligned.
        stub.AddTxt("_dmarc.header.com", "v=DMARC1; p=reject");

        var (s, server, delivery) = await ConnectReadyAsync(stub, http.Url, validateSpf: false, validateDmarc: true);
        using (server)
        await using (s)
        {
            await s.Send("MAIL FROM:<env@example.com>");
            Assert.Equal("250 2.0.0", await s.ReadLineAsync());
            await s.Send("RCPT TO:<r@example.com>");
            Assert.Equal("250 2.1.5", await s.ReadLineAsync());
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());

            // Display name carries the header domain that DMARC validates (B1).
            await s.Send("From: \"<a@header.com>\" <env@example.com>");
            await s.Send("To: r@example.com");
            await s.Send("");
            await s.Send("body");
            await s.Send(".");

            Assert.Equal("554 5.7.1 Delivery not authorized by DMARC, message refused", await s.ReadLineAsync());
            Assert.Empty(delivery.Delivered); // rejected at DATA — handler never runs
        }
    }

    [Fact]
    public async Task Data_DmarcPass_Delivers_WithDmarcArHeader()
    {
        using var stub = new DnsStub();
        using var http = new LocalHttpServer(SuffixListFixture.CanonicalList);
        // Aligned: header domain == envelope domain (example.com), even though p=reject.
        stub.AddTxt("_dmarc.example.com", "v=DMARC1; p=reject");

        var (s, server, delivery) = await ConnectReadyAsync(stub, http.Url, validateSpf: false, validateDmarc: true);
        using (server)
        await using (s)
        {
            await s.Send("MAIL FROM:<env@example.com>");
            Assert.Equal("250 2.0.0", await s.ReadLineAsync());
            await s.Send("RCPT TO:<r@example.com>");
            Assert.Equal("250 2.1.5", await s.ReadLineAsync());
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());

            await s.Send("From: \"<a@example.com>\" <env@example.com>");
            await s.Send("To: r@example.com");
            await s.Send("");
            await s.Send("body");
            await s.Send(".");

            Assert.Equal("250 2.0.0 OK", await s.ReadLineAsync());

            var tx = delivery.Delivered.Single();
            // B3 note: the delivered clone always shows DMARCValidationResult=None (Clone drops it) —
            // assert on the wire-visible Authentication-Results header instead, which is added to the
            // original transaction before cloning and survives in RawBody.
            Assert.Contains("Authentication-Results: test.local; dmarc=pass header.from=example.com", tx.RawBody);
        }
    }

    #endregion
}
