using System.Net;
using CSharp_SMTP_Server.Interfaces;
using CSharp_SMTP_Server.Protocol;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// SPF and DMARC validation wired through a full SMTP session; see TESTING.md.
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
        DnsStub stub, string suffixListUrl, bool validateSpf, bool validateDmarc, string helo = "test.client")
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
        await s.Send($"EHLO {helo}");
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

    /// <summary>
    /// The null reverse-path is accepted with SPF and DMARC <b>enabled</b>, and the SPF check runs
    /// against the HELO identity per RFC 7208 §2.4.
    /// </summary>
    /// <remarks>
    /// The null path has no envelope domain, so §2.4 substitutes <c>postmaster@&lt;HELO domain&gt;</c>
    /// as the MAIL FROM identity. The harness's EHLO domain (<c>test.client</c>) publishes no SPF
    /// record, so the result is <see cref="ValidationResult.None"/> — a real check that found no
    /// policy, not the <see cref="ValidationResult.CheckDisabled"/> that the old skip produced.
    ///
    /// The reported identity is <c>smtp.helo=</c>, not <c>smtp.mailfrom=</c>: RFC 8601 §2.7.2 names
    /// the identity actually checked, and claiming to have checked an envelope sender that does not
    /// exist would be false. An empty <c>smtp.mailfrom=</c> must never be emitted.
    ///
    /// This matters beyond the journaling deployment, where both checks are off: the library ships to
    /// consumers who leave them on, and an empty FromDomain must not fault the DNS lookup or the
    /// header-generation path.
    /// </remarks>
    [Fact]
    public async Task NullSender_WithSpfAndDmarcEnabled_ChecksHeloIdentity()
    {
        using var stub = new DnsStub();
        using var http = new LocalHttpServer(SuffixListFixture.CanonicalList);

        var (s, server, delivery) = await ConnectReadyAsync(stub, http.Url, validateSpf: true, validateDmarc: true);
        using (server)
        await using (s)
        {
            await s.Send("MAIL FROM:<>");
            Assert.Equal("250 2.0.0", await s.ReadLineAsync());
            await s.Send("RCPT TO:<r@example.com>");
            Assert.Equal("250 2.1.5", await s.ReadLineAsync());
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send("Subject: delivery status notification");
            await s.Send("");
            await s.Send("body");
            await s.Send(".");
            Assert.Equal("250 2.0.0 OK", await s.ReadLineAsync());

            var tx = delivery.Delivered.Single();
            Assert.Equal(string.Empty, tx.From);
            Assert.Equal(string.Empty, tx.FromDomain);
            Assert.Equal("test.client", tx.HeloDomain);

            // The HELO identity was checked and no record was found.
            Assert.Equal(ValidationResult.None, tx.SPFValidationResult);
            Assert.Contains("spf=none smtp.helo=test.client", tx.RawBody);
            Assert.DoesNotContain("smtp.mailfrom=", tx.RawBody);
        }
    }

    /// <summary>
    /// A null sender from a HELO domain publishing <c>-all</c> is refused with 554 5.7.23.
    /// </summary>
    /// <remarks>
    /// The substantive effect of RFC 7208 §2.4: before the HELO identity was retained, a null-path
    /// sender was not SPF-checked at all, so a domain publishing <c>-all</c> was not enforced against
    /// one. This is a new rejection path and only reachable with <c>ValidateSPF</c> on.
    /// </remarks>
    [Fact]
    public async Task NullSender_HeloDomainFailsSpf_IsRefused()
    {
        using var stub = new DnsStub();
        using var http = new LocalHttpServer(SuffixListFixture.CanonicalList);
        // The connection comes from loopback, which this record does not authorize.
        stub.AddTxt("bad.helo.test", "v=spf1 ip4:198.51.100.7 -all");

        var (s, server, delivery) = await ConnectReadyAsync(
            stub, http.Url, validateSpf: true, validateDmarc: false, helo: "bad.helo.test");

        using (server)
        await using (s)
        {
            await s.Send("MAIL FROM:<>");
            Assert.Equal("554 5.7.23 Delivery not authorized by SPF, message refused", await s.ReadLineAsync());
            Assert.Empty(delivery.Delivered);
        }
    }

    /// <summary>
    /// A null sender whose HELO is an address literal has no checkable identity and is still accepted.
    /// </summary>
    /// <remarks>
    /// RFC 5321 §4.1.3 permits an address literal in place of a domain, and it cannot carry an SPF
    /// record. Refusing on "no identity" would destroy bounces from any MTA that greets with one, so
    /// the check is skipped exactly as it was before the §2.4 work — the gap narrows for clients that
    /// present a domain, and does not become a new rejection for clients that cannot.
    /// </remarks>
    [Fact]
    public async Task NullSender_HeloIsAddressLiteral_IsAcceptedUnchecked()
    {
        using var stub = new DnsStub();
        using var http = new LocalHttpServer(SuffixListFixture.CanonicalList);

        var (s, server, delivery) = await ConnectReadyAsync(
            stub, http.Url, validateSpf: true, validateDmarc: false, helo: "[192.0.2.1]");

        using (server)
        await using (s)
        {
            await s.Send("MAIL FROM:<>");
            Assert.Equal("250 2.0.0", await s.ReadLineAsync());
            await s.Send("RCPT TO:<r@example.com>");
            Assert.Equal("250 2.1.5", await s.ReadLineAsync());
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send("Subject: bounce");
            await s.Send("");
            await s.Send("body");
            await s.Send(".");
            Assert.Equal("250 2.0.0 OK", await s.ReadLineAsync());

            var tx = delivery.Delivered.Single();
            Assert.Null(tx.HeloDomain);
            Assert.Equal(ValidationResult.CheckDisabled, tx.SPFValidationResult);
        }
    }

    /// <summary>
    /// A null-reverse-path DSN carrying a real, DMARC-aligned From header under <c>p=reject</c> must
    /// be delivered, not refused with 554.
    /// </summary>
    /// <remarks>
    /// This is the shape a real bounce takes: <c>MAIL FROM:&lt;&gt;</c> with
    /// <c>From: postmaster@example.com</c>. Because the null path leaves <c>FromDomain</c> empty,
    /// DMARC alignment compares "example.com" against "" and, under relaxed alignment, compares the
    /// organizational domains — also "" — so nothing aligns and a <c>p=reject</c> policy yields Fail.
    /// Under the journaling constraint that permanent rejection destroys a compliance record, this is
    /// the failure mode that matters most.
    /// </remarks>
    [Fact]
    public async Task NullSender_WithAlignedFromHeader_UnderDmarcReject_IsDelivered()
    {
        using var stub = new DnsStub();
        using var http = new LocalHttpServer(SuffixListFixture.CanonicalList);
        stub.AddTxt("_dmarc.example.com", "v=DMARC1; p=reject");

        var (s, server, delivery) = await ConnectReadyAsync(stub, http.Url, validateSpf: false, validateDmarc: true);
        using (server)
        await using (s)
        {
            await s.Send("MAIL FROM:<>");
            Assert.Equal("250 2.0.0", await s.ReadLineAsync());
            await s.Send("RCPT TO:<r@example.com>");
            Assert.Equal("250 2.1.5", await s.ReadLineAsync());
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send("From: postmaster@example.com");
            await s.Send("Subject: Undeliverable: quarterly report");
            await s.Send("");
            await s.Send("Your message could not be delivered.");
            await s.Send(".");

            // A bounce from the very domain that published p=reject must not be destroyed.
            Assert.Equal("250 2.0.0 OK", await s.ReadLineAsync());
            Assert.Single(delivery.Delivered);
        }
    }

    /// <summary>
    /// A null reverse-path carrying a spoofed From under <c>p=reject</c> is refused with 554.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RFC 7489 §3.1.2 names the HELO identity as the one DMARC aligns "when required to 'fake' an
    /// otherwise null reverse-path", and RFC 7208 §2.4 has SPF authenticate that same name as
    /// <c>postmaster@&lt;HELO domain&gt;</c> — so the two specifications agree on which identity is
    /// under test, and this closes the spoofing gap that was previously pinned as a known limitation.
    /// </para>
    /// <para>
    /// Alignment is gated on an SPF <b>Pass</b>: a HELO domain is attacker-controlled text until SPF
    /// says the connecting IP may use it, and without an authenticated identity the DMARC answer is
    /// None rather than Fail. That gate is what keeps legitimate bounces alive — see
    /// <see cref="NullSender_WithAlignedFromHeader_UnderDmarcReject_IsDelivered"/>, where SPF is off
    /// and the message is delivered rather than destroyed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task NullSender_WithSpoofedFrom_UnderDmarcReject_IsRefused()
    {
        using var stub = new DnsStub();
        using var http = new LocalHttpServer(SuffixListFixture.CanonicalList);
        stub.AddTxt("_dmarc.victim.example", "v=DMARC1; p=reject");
        // The attacker's own HELO domain passes SPF — it authorizes the connecting IP. That is the
        // case that matters: SPF authenticates "attacker.example", which then fails to ALIGN with the
        // spoofed From domain "victim.example", and p=reject applies.
        stub.AddTxt("attacker.example", "v=spf1 ip4:127.0.0.1 -all");

        var (s, server, delivery) = await ConnectReadyAsync(
            stub, http.Url, validateSpf: true, validateDmarc: true, helo: "attacker.example");

        using (server)
        await using (s)
        {
            await s.Send("MAIL FROM:<>");
            Assert.Equal("250 2.0.0", await s.ReadLineAsync());
            await s.Send("RCPT TO:<r@example.com>");
            Assert.Equal("250 2.1.5", await s.ReadLineAsync());
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send("From: ceo@victim.example");
            await s.Send("Subject: wire transfer");
            await s.Send("");
            await s.Send("Please action immediately.");
            await s.Send(".");

            Assert.Equal("554 5.7.1 Delivery not authorized by DMARC, message refused", await s.ReadLineAsync());
            Assert.Empty(delivery.Delivered);
        }
    }

    /// <summary>
    /// A client cannot earn an SPF <c>Pass</c> under one HELO name and then re-greet as another to
    /// have DMARC align the second one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bypass this rules out: SPF authenticates the HELO domain at <c>MAIL FROM</c>, while DMARC
    /// aligns at the terminating dot — two different moments. If the identity could change in between,
    /// an attacker would pass SPF as <c>attacker.example</c> and then align as <c>victim.example</c>.
    /// </para>
    /// <para>
    /// Two things prevent it, and this pins both: <c>EHLO</c> calls <c>DiscardTransaction()</c> before
    /// updating the retained domain, so re-greeting destroys the in-flight transaction outright; and
    /// <c>MailTransaction.HeloDomain</c> is captured by value at <c>MAIL FROM</c>, so the identity SPF
    /// authenticated is the identity DMARC aligns.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task NullSender_ReEhloAfterMailFrom_CannotSwapTheAlignedIdentity()
    {
        using var stub = new DnsStub();
        using var http = new LocalHttpServer(SuffixListFixture.CanonicalList);
        stub.AddTxt("_dmarc.victim.example", "v=DMARC1; p=reject");
        stub.AddTxt("attacker.example", "v=spf1 ip4:127.0.0.1 -all");

        var (s, server, delivery) = await ConnectReadyAsync(
            stub, http.Url, validateSpf: true, validateDmarc: true, helo: "attacker.example");

        using (server)
        await using (s)
        {
            // Establish a transaction whose HELO identity passes SPF.
            await s.Send("MAIL FROM:<>");
            Assert.Equal("250 2.0.0", await s.ReadLineAsync());

            // Re-greet as the victim domain, attempting to swap the identity DMARC will align.
            await s.Send("EHLO victim.example");
            await s.ReadResponseAsync();

            // The transaction was discarded by the re-greet, so DATA cannot follow.
            await s.Send("DATA");
            Assert.StartsWith("503", await s.ReadLineAsync());

            Assert.Empty(delivery.Delivered);
        }
    }

    /// <summary>
    /// A null sender whose HELO identity SPF actually authenticates, and which aligns with the From
    /// header, passes DMARC.
    /// </summary>
    /// <remarks>
    /// The positive half of the rule above: alignment is evaluated against the HELO domain (RFC 7489
    /// §3.1.2's "fake" identity for an otherwise null reverse-path), so a bounce genuinely emitted by
    /// the domain it claims is authenticated rather than merely tolerated.
    /// </remarks>
    [Fact]
    public async Task NullSender_HeloAlignedAndSpfPass_UnderDmarcReject_IsDelivered()
    {
        using var stub = new DnsStub();
        using var http = new LocalHttpServer(SuffixListFixture.CanonicalList);
        stub.AddTxt("_dmarc.example.com", "v=DMARC1; p=reject");
        stub.AddTxt("mail.example.com", "v=spf1 ip4:127.0.0.1 -all");

        var (s, server, delivery) = await ConnectReadyAsync(
            stub, http.Url, validateSpf: true, validateDmarc: true, helo: "mail.example.com");

        using (server)
        await using (s)
        {
            await s.Send("MAIL FROM:<>");
            Assert.Equal("250 2.0.0", await s.ReadLineAsync());
            await s.Send("RCPT TO:<r@example.com>");
            Assert.Equal("250 2.1.5", await s.ReadLineAsync());
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());
            await s.Send("From: postmaster@example.com");
            await s.Send("Subject: Undeliverable: quarterly report");
            await s.Send("");
            await s.Send("Your message could not be delivered.");
            await s.Send(".");

            Assert.Equal("250 2.0.0 OK", await s.ReadLineAsync());
            var tx = Assert.Single(delivery.Delivered);
            Assert.Equal(ValidationResult.Pass, tx.DMARCValidationResult);
        }
    }

    #endregion

    #region DMARC at DATA (§7)

    [Fact]
    public async Task Data_DmarcFail_RejectedWith554_571_NoDelivery()
    {
        using var stub = new DnsStub();
        using var http = new LocalHttpServer(SuffixListFixture.CanonicalList);
        // Header-From domain is header.com, envelope is example.com → unaligned, and header.com
        // publishes p=reject. Before B1 was fixed this scenario needed the header domain smuggled
        // through a quoted display name; an ordinary From header now reaches the 554 gate.
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

            await s.Send("From: a@header.com");
            await s.Send("To: r@example.com");
            await s.Send("");
            await s.Send("body");
            await s.Send(".");

            Assert.Equal("554 5.7.1 Delivery not authorized by DMARC, message refused", await s.ReadLineAsync());
            Assert.Empty(delivery.Delivered); // rejected at DATA — handler never runs
        }
    }

    [Fact]
    public async Task Data_MultiMailboxGroupFrom_RejectedWith554_NoDelivery()
    {
        // A single group address is ONE From entry but several mailboxes. The gate used to count
        // .From, so this passed as "one From header" while DMARC authenticated only the first member —
        // letting an attacker pair their own (authenticating) domain with a victim's in the same
        // header. The gate counts .Mailboxes now, so the message is refused before any policy runs.
        using var stub = new DnsStub();
        using var http = new LocalHttpServer(SuffixListFixture.CanonicalList);
        stub.AddTxt("_dmarc.evil.com", "v=DMARC1; p=none"); // attacker's own domain would pass alignment

        var (s, server, delivery) = await ConnectReadyAsync(stub, http.Url, validateSpf: false, validateDmarc: true);
        using (server)
        await using (s)
        {
            await s.Send("MAIL FROM:<env@evil.com>");
            Assert.Equal("250 2.0.0", await s.ReadLineAsync());
            await s.Send("RCPT TO:<r@example.com>");
            Assert.Equal("250 2.1.5", await s.ReadLineAsync());
            await s.Send("DATA");
            Assert.StartsWith("354", await s.ReadLineAsync());

            await s.Send("From: Team: env@evil.com, victim@bank.com;");
            await s.Send("To: r@example.com");
            await s.Send("");
            await s.Send("body");
            await s.Send(".");

            Assert.Equal("554 5.7.1 Message must not contain more than one From header, message refused",
                await s.ReadLineAsync());
            Assert.Empty(delivery.Delivered);
        }
    }

    [Fact]
    public async Task Data_SingleMailboxGroupFrom_IsAccepted()
    {
        // The gate must reject multiple identities, not groups as such: one mailbox inside a group is
        // still a single identity and validates normally.
        using var stub = new DnsStub();
        using var http = new LocalHttpServer(SuffixListFixture.CanonicalList);
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

            await s.Send("From: Team: a@example.com;"); // aligned with the envelope → Pass
            await s.Send("To: r@example.com");
            await s.Send("");
            await s.Send("body");
            await s.Send(".");

            Assert.Equal("250 2.0.0 OK", await s.ReadLineAsync());
            Assert.Equal(ValidationResult.Pass, delivery.Delivered.Single().DMARCValidationResult);
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

            await s.Send("From: a@example.com");
            await s.Send("To: r@example.com");
            await s.Send("");
            await s.Send("body");
            await s.Send(".");

            Assert.Equal("250 2.0.0 OK", await s.ReadLineAsync());

            var tx = delivery.Delivered.Single();
            Assert.Contains("Authentication-Results: test.local; dmarc=pass header.from=example.com", tx.RawBody);

            // B3 (fixed): the delivered clone now carries the real result too, not just the header.
            Assert.Equal(ValidationResult.Pass, tx.DMARCValidationResult);
        }
    }

    #endregion
}
