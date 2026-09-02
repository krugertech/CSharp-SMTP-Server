using System.Net;
using CSharp_SMTP_Server.Protocol;
using CSharp_SMTP_Server.Protocol.SPF;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// SpfValidator.CheckHost against the local UDP DnsStub; no internet. See TESTING.md.
/// The validator is constructed directly with the stub endpoint (no SMTP server needed).
///
/// Deviations formerly pinned here, now fixed and asserted the RFC-correct way:
/// - Q11: the previous DNS client dropped TXT responses whose RDATA held multiple character-strings,
///   making split real-world SPF records invisible. DnsClient.NET concatenates them (RFC 7208 §3.3).
/// - Q12: top-level NXDOMAIN yielded Temperror where RFC 7208 §4.3 says "none" (Q12a), and a redirect
///   to a nonexistent domain inherited that instead of permerror (Q12c, RFC §6.1). A failed `a`/`mx`
///   lookup returning the mechanism's qualifier instead of temperror (Q12b, RFC §5) was fixed earlier.
/// - Q13: `redirect=` is evaluated in positional order and short-circuits later mechanisms; RFC 7208
///   §6.1/§4.7 only consults the redirect after ALL mechanisms have failed to match.
/// </summary>
public sealed class SpfValidatorTests
{
    private const string Domain = "spf.test";

    // TEST-NET-3 / documentation ranges — never routable, safe for tests.
    private static readonly IPAddress ClientV4 = IPAddress.Parse("203.0.113.7");
    private static readonly IPAddress ClientV6 = IPAddress.Parse("2001:db8::7");

    /// <summary>Fresh stub + validator per test; registers the given SPF record for Domain.</summary>
    private static async Task<ValidationResult> Check(DnsStub stub, string spfRecord, IPAddress client)
    {
        stub.AddTxt(Domain, spfRecord);
        return await ValidatorFor(stub).CheckHost(client, Domain);
    }

    /// <summary>Validator for tests that configure the stub's tables directly (no Check helper).</summary>
    private static SpfValidator ValidatorFor(DnsStub stub) =>
        new(new IPEndPoint(IPAddress.Loopback, (ushort)stub.Port));

    #region Record lookup (§6: no TXT / query error / version / duplicates)

    [Fact]
    public async Task TxtQueryServFail_ReturnsTemperror()
    {
        using var stub = new DnsStub();
        stub.SetServFail(Domain);

        Assert.Equal(ValidationResult.Temperror, await Check(stub, "v=spf1 -all", ClientV4));
    }

    [Fact]
    public async Task TxtQueryNxDomain_ReturnsNone()
    {
        // RFC 7208 §4.3: NXDOMAIN on the initial lookup yields "none" — the domain does not exist, so
        // it publishes no SPF record. This previously pinned deviation Q12a, where every non-NoError
        // RCODE became Temperror.
        //
        // Fixing it became necessary once DMARC started deferring on Temperror (451 4.7.1, RFC 7489
        // §6.6.3): under the old mapping, mail from any non-existent domain would have been retried
        // indefinitely rather than treated as the unauthenticated mail it is.
        using var stub = new DnsStub();
        stub.SetNxDomain(Domain);

        Assert.Equal(ValidationResult.None, await Check(stub, "v=spf1 -all", ClientV4));
    }

    [Fact]
    public async Task NoTxtRecord_ReturnsNone()
    {
        using var stub = new DnsStub();
        // Domain exists (has an A record) but no TXT at all → NOERROR with empty answer.
        stub.AddA(Domain, IPAddress.Parse("198.51.100.1"));

        Assert.Equal(ValidationResult.None, await ValidatorFor(stub).CheckHost(ClientV4, Domain));
    }

    [Fact]
    public async Task TxtWithoutVSpf1Version_ReturnsNone()
    {
        using var stub = new DnsStub();

        Assert.Equal(ValidationResult.None, await Check(stub, "v=dkim1; k=xyz", ClientV4));
    }

    [Fact]
    public async Task TwoSpfRecordsInOneResponse_ReturnsPermerror()
    {
        using var stub = new DnsStub();
        // Two TXT RRs for the same name, both SPF records → RFC 7208 §4.5 permerror.
        stub.AddTxt(Domain, "v=spf1 -all", "v=spf1 +all");

        Assert.Equal(ValidationResult.Permerror, await ValidatorFor(stub).CheckHost(ClientV4, Domain));
    }

    [Fact]
    public async Task MultiStringTxtRecord_IsConcatenated_AndEvaluated()
    {
        // RFC 7208 §3.3: a TXT record split across several character-strings is evaluated as their
        // concatenation with no separator, which is how any real SPF record longer than 255 bytes is
        // published — notably the provider include-chains this relay sees constantly.
        //
        // This pinned deviation Q11: the previous DNS client parsed only single-string TXT RDATA and
        // silently dropped the whole record, so such a domain looked as though it published no SPF at
        // all and validation returned None. Combined with the DMARC fix, that mattered more than it
        // used to — "no SPF record" now means DMARC cannot authenticate the sender.
        using var stub = new DnsStub();
        // One TXT RR whose RDATA contains two character-strings: "v=spf1 " + "-all".
        stub.AddRawTxt(Domain, [.. TxtRdata("v=spf1 "), .. TxtRdata("-all")]);

        Assert.Equal(ValidationResult.Fail, await ValidatorFor(stub).CheckHost(ClientV4, Domain));
    }

    private static byte[] TxtRdata(string s) => [.. new byte[] { (byte)s.Length }, .. System.Text.Encoding.ASCII.GetBytes(s)];

    #endregion

    #region Qualifiers & terminal mechanisms (§6: -all / ~all / ?all / +all / all)

    [Theory]
    [InlineData("v=spf1 -all", ValidationResult.Fail)]
    [InlineData("v=spf1 ~all", ValidationResult.Softfail)]
    [InlineData("v=spf1 ?all", ValidationResult.Neutral)]
    [InlineData("v=spf1 +all", ValidationResult.Pass)]
    [InlineData("v=spf1 all", ValidationResult.Pass)] // bare "all" defaults to the "+" qualifier
    public async Task TerminalAllQualifier_ReturnsQualifiers(string record, ValidationResult expected)
    {
        using var stub = new DnsStub();
        Assert.Equal(expected, await Check(stub, record, ClientV4));
    }

    [Fact]
    public async Task NoMechanismMatches_NoTerminal_ReturnsNeutral()
    {
        // RFC 7208 §4.7: no match and no redirect → neutral (as if "?all" were last).
        using var stub = new DnsStub();

        Assert.Equal(ValidationResult.Neutral, await Check(stub, "v=spf1 ip4:198.51.100.1", ClientV4));
    }

    #endregion

    #region ip4 / ip6 mechanisms (§6: exact + CIDR match & mismatch, family mismatch)

    [Fact]
    public async Task Ip4ExactMatch_ReturnsPass()
    {
        using var stub = new DnsStub();
        Assert.Equal(ValidationResult.Pass, await Check(stub, "v=spf1 ip4:203.0.113.7 -all", ClientV4));
    }

    [Fact]
    public async Task Ip4Mismatch_FallsThroughToTerminal()
    {
        using var stub = new DnsStub();
        Assert.Equal(ValidationResult.Fail, await Check(stub, "v=spf1 ip4:198.51.100.1 -all", ClientV4));
    }

    [Fact]
    public async Task Ip4CidrInSubnet_ReturnsPass()
    {
        using var stub = new DnsStub();
        Assert.Equal(ValidationResult.Pass, await Check(stub, "v=spf1 ip4:203.0.113.0/24 -all", ClientV4));
    }

    [Fact]
    public async Task Ip4CidrOutOfSubnet_FallsThroughToTerminal()
    {
        using var stub = new DnsStub();
        Assert.Equal(ValidationResult.Fail, await Check(stub, "v=spf1 ip4:203.0.114.0/24 -all", ClientV4));
    }

    [Fact]
    public async Task Ip6ExactMatch_ReturnsPass()
    {
        using var stub = new DnsStub();
        Assert.Equal(ValidationResult.Pass, await Check(stub, "v=spf1 ip6:2001:db8::7 -all", ClientV6));
    }

    [Fact]
    public async Task Ip6CidrInSubnet_ReturnsPass()
    {
        using var stub = new DnsStub();
        Assert.Equal(ValidationResult.Pass, await Check(stub, "v=spf1 ip6:2001:db8::/32 -all", ClientV6));
    }

    [Fact]
    public async Task Ip6CidrOutOfSubnet_FallsThroughToTerminal()
    {
        using var stub = new DnsStub();
        Assert.Equal(ValidationResult.Fail, await Check(stub, "v=spf1 ip6:2001:db9::/32 -all", ClientV6));
    }

    [Fact]
    public async Task Ip4Mechanism_WithIpv6Client_FallsThroughToFinalResult()
    {
        // Family mismatch: the "ip4" case guard requires an IPv4 client, so the mechanism is skipped.
        using var stub = new DnsStub();

        Assert.Equal(ValidationResult.Neutral, await Check(stub, "v=spf1 ip4:203.0.113.7", ClientV6));
    }

    [Fact]
    public async Task Ipv4MappedToIpv6Client_IsUnmappedBeforeMatching()
    {
        // RFC 7208 §5: IPv4-mapped addresses are compared as their IPv4 form.
        using var stub = new DnsStub();

        Assert.Equal(ValidationResult.Pass, await Check(stub, "v=spf1 ip4:203.0.113.7 -all", IPAddress.Parse("::ffff:203.0.113.7")));
    }

    #endregion

    #region a / mx mechanisms (§6: A/AAAA + MX lookups via stub)

    [Fact]
    public async Task AMechanism_ARecordMatches_ReturnsPass()
    {
        using var stub = new DnsStub();
        stub.AddA(Domain, ClientV4);

        Assert.Equal(ValidationResult.Pass, await Check(stub, "v=spf1 a -all", ClientV4));
    }

    [Fact]
    public async Task AMechanism_NoMatch_ContinuesToTerminal()
    {
        using var stub = new DnsStub();
        stub.AddA(Domain, IPAddress.Parse("198.51.100.99"));

        Assert.Equal(ValidationResult.Fail, await Check(stub, "v=spf1 a -all", ClientV4));
    }

    [Fact]
    public async Task AMechanism_DnsFailure_ReturnsTemperror()
    {
        // Q12(b) (fixed): RFC 7208 §5 — a DNS error during an address lookup stops evaluation with
        // temperror. Previously any non-None CheckAddressMatch result counted as a match and returned
        // the mechanism's own qualifier, so SPF failed *open*: a bare "a" (implicit "+") returned Pass
        // during a resolver outage, turning SPF from a control into an authorizer.
        using var stub = new DnsStub();
        stub.SetServFail("failsrv.test");

        Assert.Equal(ValidationResult.Temperror, await Check(stub, "v=spf1 -a:failsrv.test -all", ClientV4)); // was Fail (the qualifier)
        Assert.Equal(ValidationResult.Temperror, await Check(stub, "v=spf1 a:failsrv.test", ClientV4));       // was Pass — the fail-open
    }

    [Fact]
    public async Task MxMechanism_AddressLookupDnsFailure_ReturnsTemperror()
    {
        // Q12(b), the mx half: the MX query itself succeeds, but resolving the MX host's address
        // SERVFAILs. That inner failure must surface as temperror rather than counting as a match.
        using var stub = new DnsStub();
        stub.AddMx(Domain, (10, "failsrv.test"));
        stub.SetServFail("failsrv.test");

        Assert.Equal(ValidationResult.Temperror, await Check(stub, "v=spf1 mx", ClientV4));
    }

    // ─── NXDOMAIN is a definitive no-match, NOT a transient failure ──────────
    //
    // RFC 7208 §5: a nonexistent name means the mechanism does not match and evaluation continues to
    // the next one. Treating it as temperror short-circuits a terminal "-all", and since SMTP rejects
    // only on Fail, that ACCEPTS mail that should be rejected. The Q12(b) fix originally collapsed
    // NXDOMAIN into temperror along with SERVFAIL; these pin the two apart.

    [Fact]
    public async Task AMechanism_Nxdomain_DoesNotMatch_AndReachesTerminalAll()
    {
        using var stub = new DnsStub();
        stub.SetNxDomain("missing.test");

        // "a:missing.test" must not match, so evaluation reaches "-all" → Fail.
        Assert.Equal(ValidationResult.Fail, await Check(stub, "v=spf1 a:missing.test -all", ClientV4));
    }

    [Fact]
    public async Task AMechanism_Nxdomain_ContinuesToLaterMatchingMechanism()
    {
        // The nonexistent name is skipped rather than aborting evaluation, so a later mechanism that
        // does match still decides the result.
        using var stub = new DnsStub();
        stub.SetNxDomain("missing.test");
        stub.AddA("real.test", ClientV4);

        Assert.Equal(ValidationResult.Pass, await Check(stub, "v=spf1 a:missing.test a:real.test -all", ClientV4));
    }

    [Fact]
    public async Task MxMechanism_NxdomainOnMxQuery_DoesNotMatch_AndReachesTerminalAll()
    {
        using var stub = new DnsStub();
        stub.SetNxDomain("missing.test");

        Assert.Equal(ValidationResult.Fail, await Check(stub, "v=spf1 mx:missing.test -all", ClientV4));
    }

    [Fact]
    public async Task MxMechanism_NxdomainOnMxHostAddress_DoesNotMatch_AndReachesTerminalAll()
    {
        // The MX query succeeds; the MX host's own A lookup is NXDOMAIN. Still a no-match, not a
        // temperror — this is the inner CheckAddressMatch path rather than the outer MX query.
        using var stub = new DnsStub();
        stub.AddMx(Domain, (10, "missing.test"));
        stub.SetNxDomain("missing.test");

        Assert.Equal(ValidationResult.Fail, await Check(stub, "v=spf1 mx -all", ClientV4));
    }

    [Fact]
    public async Task MxMechanism_MxChainMatches_ReturnsPass()
    {
        using var stub = new DnsStub();
        stub.AddMx(Domain, (10, "mail.spf.test"));
        stub.AddA("mail.spf.test", ClientV4);

        Assert.Equal(ValidationResult.Pass, await Check(stub, "v=spf1 mx -all", ClientV4));
    }

    [Fact]
    public async Task MxMechanism_NoMatch_ContinuesToTerminal()
    {
        using var stub = new DnsStub();
        stub.AddMx(Domain, (10, "mail.spf.test"), (20, "backup.spf.test"));
        stub.AddA("mail.spf.test", IPAddress.Parse("198.51.100.99"));
        stub.AddA("backup.spf.test", IPAddress.Parse("198.51.100.98"));

        Assert.Equal(ValidationResult.Fail, await Check(stub, "v=spf1 mx -all", ClientV4));
    }

    #endregion

    #region include (§6: qualifier propagation; RFC 7208 §5.2 result table)

    [Fact]
    public async Task Include_IncludedDomainPasses_ReturnsMechanismQualifier()
    {
        // RFC 7208 §5.2: recursive "pass" → the include mechanism matches, i.e. its qualifier applies.
        using var stub = new DnsStub();
        stub.AddTxt("inc.spf.test", "v=spf1 ip4:203.0.113.7 -all");

        Assert.Equal(ValidationResult.Pass, await Check(stub, "v=spf1 include:inc.spf.test -all", ClientV4));
        Assert.Equal(ValidationResult.Softfail, await Check(stub, "v=spf1 ~include:inc.spf.test -all", ClientV4));
    }

    [Fact]
    public async Task Include_IncludedDomainHasNoSpfRecord_ReturnsPermerror()
    {
        // RFC 7208 §5.2 table: recursive "none" → permerror. (inc.spf.test answers NOERROR/empty.)
        using var stub = new DnsStub();

        Assert.Equal(ValidationResult.Permerror, await Check(stub, "v=spf1 include:inc.spf.test -all", ClientV4));
    }

    [Fact]
    public async Task Include_IncludedDomainServFail_ReturnsTemperror()
    {
        // RFC 7208 §5.2 table: recursive temperror → temperror.
        using var stub = new DnsStub();
        stub.SetServFail("inc.spf.test");

        Assert.Equal(ValidationResult.Temperror, await Check(stub, "v=spf1 include:inc.spf.test -all", ClientV4));
    }

    [Fact]
    public async Task Include_IncludedDomainFails_IsNotMatch_EvaluationResumes()
    {
        // RFC 7208 §5.2 table: recursive fail/softfail/neutral → "not match" — the parent record keeps
        // evaluating its remaining mechanisms (the included "-all" does NOT terminate the check).
        using var stub = new DnsStub();
        stub.AddTxt("fail.spf.test", "v=spf1 -all");

        Assert.Equal(ValidationResult.Pass, await Check(stub, "v=spf1 include:fail.spf.test ip4:203.0.113.7", ClientV4));
    }

    #endregion

    #region redirect (§6: result propagation; RFC 7208 §6.1)

    [Fact]
    public async Task Redirect_PropagatesTargetResult()
    {
        using var stub = new DnsStub();
        stub.AddTxt("t.spf.test", "v=spf1 -all");
        Assert.Equal(ValidationResult.Fail, await Check(stub, "v=spf1 redirect:t.spf.test", ClientV4));

        stub.AddTxt("t.spf.test", "v=spf1 ip4:203.0.113.7 -all");
        Assert.Equal(ValidationResult.Pass, await Check(stub, "v=spf1 redirect:t.spf.test", ClientV4));
    }

    [Fact]
    public async Task Redirect_TargetHasNoSpfRecord_ReturnsPermerror()
    {
        // RFC 7208 §6.1: no SPF record at the target → permerror rather than none.
        using var stub = new DnsStub();
        // t.spf.test answers NOERROR/empty (no TXT).

        Assert.Equal(ValidationResult.Permerror, await Check(stub, "v=spf1 redirect:t.spf.test", ClientV4));
    }

    [Fact]
    public async Task Redirect_NonexistentTarget_ReturnsPermerror()
    {
        // RFC 7208 §4.3+§6.1: NXDOMAIN at the target is "none" in check_host, which redirect maps to
        // permerror — the same answer as a target that exists but publishes no record (above).
        //
        // This pinned deviation Q12c, which existed only as a consequence of Q12a: the inner CheckHost
        // returned Temperror for NXDOMAIN and redirect passed it straight through. With §4.3 fixed the
        // inner result is None, and the existing §6.1 mapping turns it into permerror unaided.
        using var stub = new DnsStub();
        stub.SetNxDomain("t.spf.test");

        Assert.Equal(ValidationResult.Permerror, await Check(stub, "v=spf1 redirect:t.spf.test", ClientV4));
    }

    [Fact]
    public async Task Redirect_IsIgnoredWhenAllMechanismPresent()
    {
        // RFC 7208 §5.1: any redirect MUST be ignored when an "all" mechanism is in the record,
        // regardless of ordering.
        using var stub = new DnsStub();
        stub.AddTxt("t.spf.test", "v=spf1 ip4:203.0.113.7 -all");

        Assert.Equal(ValidationResult.Fail, await Check(stub, "v=spf1 redirect:t.spf.test -all", ClientV4));
    }

    [Fact]
    public async Task Redirect_EvaluatedPositionally_ShortCircuitsLaterMechanisms_PinQ13()
    {
        // RFC 7208 §6.1/§4.7: the redirect is only consulted after ALL mechanisms have failed to match,
        // so "v=spf1 redirect:x ip4:<client>" must pass via ip4. The validator evaluates terms in order
        // and returns as soon as it hits the redirect — pin current behavior (deviation Q13).
        using var stub = new DnsStub();
        stub.AddTxt("t.spf.test", "v=spf1 -all");

        Assert.Equal(ValidationResult.Fail, await Check(stub, "v=spf1 redirect:t.spf.test ip4:203.0.113.7", ClientV4));
    }

    #endregion

    #region Request limit (§6: >10 DNS lookups → permerror)

    [Fact]
    public async Task MoreThanTenLookups_ReturnsPermerrorNotTerminal()
    {
        // RFC 7208 §4.6.4: more than 10 lookup-causing terms → permerror. Twelve non-matching "a"
        // mechanisms (each one A query) must yield Permerror, not the terminal "-all"'s Fail.
        using var stub = new DnsStub();
        for (var i = 1; i <= 12; i++)
            stub.AddA($"a{i}.many.spf.test", IPAddress.Parse("198.51.100.1")); // never matches ClientV4

        var record = "v=spf1 " + string.Join(' ', Enumerable.Range(1, 12).Select(i => $"a:a{i}.many.spf.test")) + " -all";
        Assert.Equal(ValidationResult.Permerror, await Check(stub, record, ClientV4));
    }

    [Fact]
    public async Task MoreThanTenLookups_AfterPtrWasUsed_ReturnsFailNotPermerror()
    {
        // §6: the limit yields Permerror — or Fail when a PTR mechanism was used. The ptr lookup and
        // its address check consume two of the counted requests; ten non-matching "a" mechanisms then
        // push the counter past 10 while ptrWasUsed=true → Fail (not the terminal's result either way,
        // but distinct from Permerror).
        using var stub = new DnsStub();
        stub.AddPtr(ClientV4, Domain); // PTR name equals the validated domain…
        stub.AddA(Domain, IPAddress.Parse("198.51.100.1")); // …but its A record never matches
        for (var i = 1; i <= 10; i++)
            stub.AddA($"a{i}.many.spf.test", IPAddress.Parse("198.51.100.2"));

        var record = "v=spf1 ptr " + string.Join(' ', Enumerable.Range(1, 10).Select(i => $"a:a{i}.many.spf.test")) + " -all";
        Assert.Equal(ValidationResult.Fail, await Check(stub, record, ClientV4));
    }

    #endregion
}
