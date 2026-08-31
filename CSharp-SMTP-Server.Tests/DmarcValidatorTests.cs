using System.Net;
using CSharp_SMTP_Server.Protocol;
using CSharp_SMTP_Server.Protocol.DMARC;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Phase 4 (TEST_PLAN.md §7): DmarcValidator.ValidateTransaction against a local UDP DNS stub (§1.5)
/// with the Public Suffix List served from loopback HTTP — no internet. A real SMTPServer is used so
/// the validator's constructor downloads the list exactly like production.
///
/// Header-From domains are driven through the display name: GetFrom returns MimeKit's *display name*
/// (bug B1), and ProcessAddress takes the first &lt;…&gt; pair it finds — so a quoted display name that
/// itself contains an address is what actually gets validated. This both exercises the full validation
/// path AND documents B1: DMARC validates the display name, not the real From address.
/// </summary>
public sealed class DmarcValidatorTests
{
    /// <summary>Suffix list + DNS stub + a live DmarcValidator (list downloaded in its constructor).</summary>
    private sealed class Env : IDisposable
    {
        public DnsStub Stub { get; } = new();
        public DmarcValidator Validator { get; }

        public Env()
        {
            var http = new LocalHttpServer(SuffixListFixture.CanonicalList);
            var options = new ServerOptions(false, true, new IPEndPoint(IPAddress.Loopback, (ushort)Stub.Port))
            {
                PublicSuffixList = http.Url
            };

            using var server = new SMTPServer(null, options, NoopDelivery.Instance);
            Validator = server.DmarcValidator!; // constructor blocks until the list is downloaded
        }

        public void Dispose() => Stub.Dispose();
    }

    /// <summary>
    /// Builds a transaction: envelope (MAIL FROM) domain + one From header line in the raw body.
    /// The display name carries the *header* domain that DMARC will validate (see class remarks).
    /// </summary>
    private static MailTransaction Tx(string fromLine, string envelopeDomain) =>
        new($"env@{envelopeDomain}", envelopeDomain, ValidationResult.CheckDisabled)
        {
            RawBody = $"From: {fromLine}\r\nTo: r@example.com\r\nSubject: t\r\n\r\nbody"
        };

    #region No validation possible (§7: missing/unparseable From, no records)

    [Fact]
    public async Task NormalFrom_DmarcIsInert_PinB1()
    {
        // B1 end-to-end pin: a perfectly normal message (SPF-aligned envelope and header domain) must
        // yield None today — GetFrom returns the display name, which is "" for "sender@example.com",
        // so ProcessAddress finds no <…> pair and validation never even queries DNS. DMARC is
        // effectively inert until B1 is fixed; this test fails if that ever changes silently.
        using var env = new Env();
        env.Stub.AddTxt("_dmarc.example.com", "v=DMARC1; p=reject");

        var result = await env.Validator.ValidateTransaction(Tx("sender@example.com", "example.com"));

        Assert.Equal(ValidationResult.None, result);
        Assert.Equal(0, env.Stub.QueryCount); // no _dmarc lookup was ever attempted
    }

    [Fact]
    public async Task NoFromHeader_ReturnsNone()
    {
        using var env = new Env();
        var tx = Tx("sender@example.com", "example.com");
        tx.RawBody = "To: r@example.com\r\nSubject: t\r\n\r\nbody"; // no From header at all

        Assert.Equal(ValidationResult.None, await env.Validator.ValidateTransaction(tx));
    }

    [Fact]
    public async Task UnparseableFromDomain_ReturnsNone()
    {
        using var env = new Env();
        // "From: John" parses as a mailbox with an empty display name → ProcessAddress finds no <…>.
        Assert.Equal(ValidationResult.None, await env.Validator.ValidateTransaction(Tx("John", "example.com")));
    }

    [Fact]
    public async Task NoDmarcRecordOnDomainOrOrgDomain_ReturnsNone()
    {
        using var env = new Env(); // stub answers NOERROR/empty for every _dmarc name

        Assert.Equal(ValidationResult.None, await env.Validator.ValidateTransaction(Tx("\"<a@header.com>\" <env@example.com>", "example.com")));
    }

    [Fact]
    public async Task TxtNotStartingWithVdmarc1_IsIgnored_FallsBackToOrgDomain()
    {
        // §7: non-DMARC TXT on _dmarc.<domain> is ignored; validation falls back to the org domain's
        // record, whose policy then applies (unaligned here → p=reject → Fail).
        using var env = new Env();
        env.Stub.AddTxt("_dmarc.sub.h.com", "v=spf1 -all");          // bogus — must be ignored
        env.Stub.AddTxt("_dmarc.h.com", "v=DMARC1; p=reject");       // org record

        Assert.Equal(ValidationResult.Fail, await env.Validator.ValidateTransaction(Tx("\"<a@sub.h.com>\" <env@example.com>", "other.org")));
    }

    [Fact]
    public async Task TwoDmarcRecordsInOneResponse_TreatedAsNone()
    {
        // §7: two v=DMARC1; records for one name → treated as if no record existed.
        using var env = new Env();
        env.Stub.AddTxt("_dmarc.h.com", "v=DMARC1; p=reject", "v=DMARC1; p=none");

        Assert.Equal(ValidationResult.None, await env.Validator.ValidateTransaction(Tx("\"<a@h.com>\" <env@example.com>", "other.org")));
    }

    #endregion

    #region Alignment (§7: strict vs relaxed)

    [Fact]
    public async Task AlignedEnvelopeAndHeader_PassRegardlessOfPolicy()
    {
        using var env = new Env();
        env.Stub.AddTxt("_dmarc.example.com", "v=DMARC1; p=reject"); // would Fail if unaligned

        Assert.Equal(ValidationResult.Pass, await env.Validator.ValidateTransaction(Tx("\"<a@example.com>\" <env@example.com>", "example.com")));
    }

    [Fact]
    public async Task RelaxedAlignment_SameOrgDomain_Pass()
    {
        // Header domain sub.header.com ≠ envelope header.com (strict fails), but both org domains are
        // header.com → aligned under the default aspf=r, even with p=reject.
        using var env = new Env();
        env.Stub.AddTxt("_dmarc.sub.header.com", "v=DMARC1; p=reject");

        Assert.Equal(ValidationResult.Pass, await env.Validator.ValidateTransaction(Tx("\"<a@sub.header.com>\" <env@header.com>", "header.com")));
    }

    [Fact]
    public async Task StrictAlignment_SubdomainMismatch_NotAligned()
    {
        // aspf=s: sub.header.com vs header.com is not aligned → policy applies (p=reject → Fail).
        using var env = new Env();
        env.Stub.AddTxt("_dmarc.sub.header.com", "v=DMARC1; aspf=s; p=reject");

        Assert.Equal(ValidationResult.Fail, await env.Validator.ValidateTransaction(Tx("\"<a@sub.header.com>\" <env@header.com>", "header.com")));
    }

    #endregion

    #region Policy mapping when unaligned (§7: p / sp)

    [Fact]
    public async Task Unaligned_PReject_ReturnsFail()
    {
        using var env = new Env();
        env.Stub.AddTxt("_dmarc.h.com", "v=DMARC1; p=reject");

        Assert.Equal(ValidationResult.Fail, await env.Validator.ValidateTransaction(Tx("\"<a@h.com>\" <env@example.com>", "other.org")));
    }

    [Fact]
    public async Task Unaligned_PQuarantine_ReturnsSoftfail()
    {
        using var env = new Env();
        env.Stub.AddTxt("_dmarc.h.com", "v=DMARC1; p=quarantine");

        Assert.Equal(ValidationResult.Softfail, await env.Validator.ValidateTransaction(Tx("\"<a@h.com>\" <env@example.com>", "other.org")));
    }

    [Theory]
    [InlineData("v=DMARC1;")]          // no policy tag at all
    [InlineData("v=DMARC1; p=none")]   // explicit none
    public async Task Unaligned_NoPolicy_ReturnsNone(string record)
    {
        using var env = new Env();
        env.Stub.AddTxt("_dmarc.h.com", record);

        Assert.Equal(ValidationResult.None, await env.Validator.ValidateTransaction(Tx("\"<a@h.com>\" <env@example.com>", "other.org")));
    }

    [Fact]
    public async Task SubdomainPolicy_SpAppliesWhenSubdomainHasNoOwnRecord()
    {
        // Header from sub.h.com (no own _dmarc) → the org record's sp= applies instead of p=.
        using var env = new Env();
        env.Stub.AddTxt("_dmarc.h.com", "v=DMARC1; p=none; sp=reject");

        Assert.Equal(ValidationResult.Fail, await env.Validator.ValidateTransaction(Tx("\"<a@sub.h.com>\" <env@example.com>", "other.org")));
    }

    [Fact]
    public async Task OwnRecordTakesPrecedenceOverOrgSp()
    {
        // The subdomain HAS its own record → isSubdomain=false → p= (quarantine) applies, not the org's sp=.
        using var env = new Env();
        env.Stub.AddTxt("_dmarc.sub.h.com", "v=DMARC1; p=quarantine");
        env.Stub.AddTxt("_dmarc.h.com", "v=DMARC1; p=none; sp=reject");

        Assert.Equal(ValidationResult.Softfail, await env.Validator.ValidateTransaction(Tx("\"<a@sub.h.com>\" <env@example.com>", "other.org")));
    }

    #endregion
}
