using System.Net;
using CSharp_SMTP_Server.Protocol;
using CSharp_SMTP_Server.Protocol.DMARC;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// DmarcValidator.ValidateTransaction coverage against the local UDP DnsStub; see TESTING.md.
/// with the Public Suffix List served from loopback HTTP — no internet. A real SMTPServer is used so
/// the validator's constructor downloads the list exactly like production.
///
/// Each case supplies an ordinary From header and an envelope domain, and alignment follows from the
/// relationship between them. Before B1 was fixed these tests had to smuggle the header domain through
/// a quoted display name (<c>"&lt;a@h.com&gt;" &lt;env@example.com&gt;</c>) because GetFrom returned the
/// display name and ProcessAddress needed angle brackets — which meant none of them exercised the path
/// a normal message actually takes. They now use real addresses.
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
    /// Builds a transaction from an ordinary From header line and an envelope (MAIL FROM) domain.
    /// DMARC validates the header-From domain and checks it against the envelope domain for alignment.
    /// </summary>
    private static MailTransaction Tx(string fromLine, string envelopeDomain) =>
        new($"env@{envelopeDomain}", envelopeDomain, ValidationResult.CheckDisabled)
        {
            RawBody = $"From: {fromLine}\r\nTo: r@example.com\r\nSubject: t\r\n\r\nbody"
        };

    #region No validation possible (§7: missing/unparseable From, no records)

    [Fact]
    public async Task NormalFrom_QueriesDmarcRecord_AndEnforcesPolicy()
    {
        // B1 end-to-end (fixed), inverted from the pin it replaces. That pin asserted DMARC was inert
        // for ordinary mail: None, with ZERO DNS queries, because GetFrom returned the display name
        // ("" for "sender@example.com") and validation bailed before ever resolving _dmarc. A normal
        // message must now drive a real lookup and a real policy decision.
        using var env = new Env();
        env.Stub.AddTxt("_dmarc.example.com", "v=DMARC1; p=reject");

        // Aligned: header-From domain == envelope domain → Pass, and the record WAS fetched.
        Assert.Equal(ValidationResult.Pass,
            await env.Validator.ValidateTransaction(Tx("sender@example.com", "example.com")));
        Assert.True(env.Stub.QueryCount > 0, "no _dmarc lookup was attempted — DMARC is still inert");

        // Unaligned against the same p=reject record → Fail, which is what TransactionCommands turns
        // into a 554 at DATA. Unreachable before the fix.
        Assert.Equal(ValidationResult.Fail,
            await env.Validator.ValidateTransaction(Tx("sender@example.com", "attacker.org")));
    }

    [Fact]
    public async Task NoFromHeader_ReturnsNone()
    {
        using var env = new Env();
        var tx = Tx("sender@example.com", "example.com");
        tx.RawBody = "To: r@example.com\r\nSubject: t\r\n\r\nbody"; // no From header at all

        Assert.Equal(ValidationResult.None, await env.Validator.ValidateTransaction(tx));
    }

    [Theory]
    [InlineData("John")]                 // a bare word: no address at all
    [InlineData("John <not-an-address>")] // display name plus an unusable address
    [InlineData("user@localhost")]        // no dot in the domain → no organizational domain
    public async Task UnparseableFromDomain_ReturnsNone(string fromLine)
    {
        // A From header with no usable domain cannot be validated. Note this is now a genuinely
        // unparseable header: before B1 was fixed, ordinary well-formed addresses landed here too.
        using var env = new Env();

        Assert.Equal(ValidationResult.None, await env.Validator.ValidateTransaction(Tx(fromLine, "example.com")));
    }

    [Fact]
    public async Task NoDmarcRecordOnDomainOrOrgDomain_ReturnsNone()
    {
        using var env = new Env(); // stub answers NOERROR/empty for every _dmarc name

        Assert.Equal(ValidationResult.None, await env.Validator.ValidateTransaction(Tx("a@header.com", "example.com")));
    }

    [Fact]
    public async Task TxtNotStartingWithVdmarc1_IsIgnored_FallsBackToOrgDomain()
    {
        // §7: non-DMARC TXT on _dmarc.<domain> is ignored; validation falls back to the org domain's
        // record, whose policy then applies (unaligned here → p=reject → Fail).
        using var env = new Env();
        env.Stub.AddTxt("_dmarc.sub.h.com", "v=spf1 -all");          // bogus — must be ignored
        env.Stub.AddTxt("_dmarc.h.com", "v=DMARC1; p=reject");       // org record

        Assert.Equal(ValidationResult.Fail, await env.Validator.ValidateTransaction(Tx("a@sub.h.com", "other.org")));
    }

    [Fact]
    public async Task TwoDmarcRecordsInOneResponse_TreatedAsNone()
    {
        // §7: two v=DMARC1; records for one name → treated as if no record existed.
        using var env = new Env();
        env.Stub.AddTxt("_dmarc.h.com", "v=DMARC1; p=reject", "v=DMARC1; p=none");

        Assert.Equal(ValidationResult.None, await env.Validator.ValidateTransaction(Tx("a@h.com", "other.org")));
    }

    #endregion

    #region Alignment (§7: strict vs relaxed)

    [Fact]
    public async Task AlignedEnvelopeAndHeader_PassRegardlessOfPolicy()
    {
        using var env = new Env();
        env.Stub.AddTxt("_dmarc.example.com", "v=DMARC1; p=reject"); // would Fail if unaligned

        Assert.Equal(ValidationResult.Pass, await env.Validator.ValidateTransaction(Tx("a@example.com", "example.com")));
    }

    [Fact]
    public async Task RelaxedAlignment_SameOrgDomain_Pass()
    {
        // Header domain sub.header.com ≠ envelope header.com (strict fails), but both org domains are
        // header.com → aligned under the default aspf=r, even with p=reject.
        using var env = new Env();
        env.Stub.AddTxt("_dmarc.sub.header.com", "v=DMARC1; p=reject");

        Assert.Equal(ValidationResult.Pass, await env.Validator.ValidateTransaction(Tx("a@sub.header.com", "header.com")));
    }

    [Fact]
    public async Task StrictAlignment_SubdomainMismatch_NotAligned()
    {
        // aspf=s: sub.header.com vs header.com is not aligned → policy applies (p=reject → Fail).
        using var env = new Env();
        env.Stub.AddTxt("_dmarc.sub.header.com", "v=DMARC1; aspf=s; p=reject");

        Assert.Equal(ValidationResult.Fail, await env.Validator.ValidateTransaction(Tx("a@sub.header.com", "header.com")));
    }

    #endregion

    #region Policy mapping when unaligned (§7: p / sp)

    [Fact]
    public async Task Unaligned_PReject_ReturnsFail()
    {
        using var env = new Env();
        env.Stub.AddTxt("_dmarc.h.com", "v=DMARC1; p=reject");

        Assert.Equal(ValidationResult.Fail, await env.Validator.ValidateTransaction(Tx("a@h.com", "other.org")));
    }

    [Fact]
    public async Task Unaligned_PQuarantine_ReturnsSoftfail()
    {
        using var env = new Env();
        env.Stub.AddTxt("_dmarc.h.com", "v=DMARC1; p=quarantine");

        Assert.Equal(ValidationResult.Softfail, await env.Validator.ValidateTransaction(Tx("a@h.com", "other.org")));
    }

    [Theory]
    [InlineData("v=DMARC1;")]          // no policy tag at all
    [InlineData("v=DMARC1; p=none")]   // explicit none
    public async Task Unaligned_NoPolicy_ReturnsNone(string record)
    {
        using var env = new Env();
        env.Stub.AddTxt("_dmarc.h.com", record);

        Assert.Equal(ValidationResult.None, await env.Validator.ValidateTransaction(Tx("a@h.com", "other.org")));
    }

    [Fact]
    public async Task SubdomainPolicy_SpAppliesWhenSubdomainHasNoOwnRecord()
    {
        // Header from sub.h.com (no own _dmarc) → the org record's sp= applies instead of p=.
        using var env = new Env();
        env.Stub.AddTxt("_dmarc.h.com", "v=DMARC1; p=none; sp=reject");

        Assert.Equal(ValidationResult.Fail, await env.Validator.ValidateTransaction(Tx("a@sub.h.com", "other.org")));
    }

    [Fact]
    public async Task OwnRecordTakesPrecedenceOverOrgSp()
    {
        // The subdomain HAS its own record → isSubdomain=false → p= (quarantine) applies, not the org's sp=.
        using var env = new Env();
        env.Stub.AddTxt("_dmarc.sub.h.com", "v=DMARC1; p=quarantine");
        env.Stub.AddTxt("_dmarc.h.com", "v=DMARC1; p=none; sp=reject");

        Assert.Equal(ValidationResult.Softfail, await env.Validator.ValidateTransaction(Tx("a@sub.h.com", "other.org")));
    }

    #endregion
}
