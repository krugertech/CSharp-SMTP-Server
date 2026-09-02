using System.Net;
using System.Net.Sockets;
using CSharp_SMTP_Server.Interfaces;
using CSharp_SMTP_Server.Networking;
using CSharp_SMTP_Server.Protocol;
using CSharp_SMTP_Server.Protocol.SPF;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Address-family handling where the code genuinely branches on IPv4 vs IPv6. See TESTING.md.
///
/// Most of the protocol surface is family-agnostic — the command state machine, BoundedLineReader,
/// DATA, AUTH and TLS all operate on the byte stream and never observe the socket family — so this
/// file deliberately covers only the paths that inspect an <see cref="IPAddress"/>:
///
/// - the IPv4-mapped-to-IPv6 unmapping applied to the connecting address before it is written into
///   the Received header (TransactionCommands, DATA terminator branch), and
/// - the SPF "ptr" mechanism for an IPv6 client, which resolves through ip6.arpa nibble names.
///
/// Both were previously reachable only in production. The existing dual-mode tests in
/// <see cref="LifecycleAndRobustnessTests"/> establish a v4-mapped connection but assert only the
/// greeting, so they never reach the header builder; the existing SPF ptr test in
/// <see cref="SpfValidatorTests"/> uses an IPv4 client, so the stub's ip6.arpa decoding was itself
/// unverified.
/// </summary>
public sealed class Ipv6AddressHandlingTests
{
    #region Received header: v4-mapped address is unmapped

    /// <summary>
    /// RFC 5321 §4.4: the Received header records the client that actually connected. On a dual-mode
    /// socket an IPv4 client arrives as the v4-mapped address ::ffff:a.b.c.d, so the header builder
    /// unmaps it before formatting. Without that step a plain IPv4 sender is archived as
    /// "from ::ffff:127.0.0.1" — a faithful-archive defect for a journaling relay, and one no test
    /// caught: every other Received assertion connects over an IPv4 listener, where the unmapping is
    /// a no-op.
    /// </summary>
    [Fact]
    public async Task ReceivedHeader_Ipv4MappedClientOnDualModeSocket_RecordsUnmappedIpv4()
    {
        var port = TestPorts.Allocate();
        var delivery = new RecordingDelivery();
        using var server = new SMTPServer(
            new[] { new ListeningParameters(IPAddress.IPv6Any, new[] { port }, null, dualMode: true) },
            TestServers.DefaultOptions(), delivery);
        server.Start();

        // Connecting over IPv4 to a dual-mode IPv6 socket is what produces the mapped RemoteEndPoint.
        await using var s = await SmtpSession.ConnectAsync(port, IPAddress.Loopback);
        Assert.StartsWith("220 ", await s.ReadLineAsync());

        await SendMessageAsync(s);

        var raw = Assert.Single(delivery.Delivered).RawBody;
        Assert.StartsWith("Received: from 127.0.0.1 by test.local with SMTP; ", raw);
        Assert.DoesNotContain("::ffff:", raw);
    }

    /// <summary>
    /// The counterpart: a genuine IPv6 client is not v4-mapped, so its address must reach the header
    /// unchanged rather than being mangled by the unmapping branch.
    /// </summary>
    /// <remarks>
    /// The expected value pins the CURRENT format, which is not RFC-conformant: RFC 5321 §4.4 requires
    /// the From-domain's address-literal form from §4.1.3, so this should be <c>[IPv6:::1]</c>, and the
    /// IPv4 cases should be bracketed too. The server emits a bare <c>IPAddress.ToString()</c>. That is
    /// a separate output-formatting defect filed in KNOWN_ISSUES.md; what this test is for is the
    /// unmapping decision, which is orthogonal to how the result is serialized. Both Received tests
    /// here, and DataAndMessageTests.NormalBody_DeliveredWithReceivedHeader, change together when the
    /// formatting is fixed.
    /// </remarks>
    [Fact]
    public async Task ReceivedHeader_NativeIpv6Client_RecordsIpv6LiteralVerbatim()
    {
        var port = TestPorts.Allocate();
        var delivery = new RecordingDelivery();
        using var server = new SMTPServer(
            new[] { new ListeningParameters(IPAddress.IPv6Any, new[] { port }, null, dualMode: true) },
            TestServers.DefaultOptions(), delivery);
        server.Start();

        await using var s = await SmtpSession.ConnectAsync(port, IPAddress.IPv6Loopback);
        Assert.StartsWith("220 ", await s.ReadLineAsync());

        await SendMessageAsync(s);

        var raw = Assert.Single(delivery.Delivered).RawBody;
        Assert.StartsWith("Received: from ::1 by test.local with SMTP; ", raw);
    }

    /// <summary>
    /// The filter callbacks and the delivered transaction see the RAW endpoint, un-normalized: on a
    /// dual-mode listener an IPv4 client reaches <see cref="Interfaces.IMailFilter"/>,
    /// <see cref="Interfaces.IAuthLogin"/> and <see cref="MailTransaction.RemoteEndPoint"/> as
    /// ::ffff:a.b.c.d, while SPF and the Received header both unmap it first.
    /// </summary>
    /// <remarks>
    /// CHARACTERIZATION TEST — it pins current behavior that is considered WRONG, and asserting it is
    /// not an endorsement. Read the failure of this test as "the defect was fixed", not "a regression
    /// occurred"; when normalization lands, invert every assertion here and delete this note.
    ///
    /// The behavior matters because it is an authorization inconsistency, not a cosmetic one: an
    /// operator's IPv4 CIDR allowlist silently stops matching when a listener is switched to dual-mode,
    /// so a rule written to admit a sender no longer admits it, and a family-specific deny rule no
    /// longer denies. The direction of the risk depends on how a given filter is written, which is
    /// precisely why the ambiguity is worth removing.
    ///
    /// It is pinned rather than fixed because normalizing at capture in ClientProcessor changes the
    /// address every existing IMailFilter and IAuthLogin implementation observes — an API-visible
    /// behavior change that belongs with a documented release note, not a silent test-driven edit. The
    /// value of the test in the meantime is that the inconsistency is now visible and enumerated:
    /// SPF and the Received header unmap, these five consumers do not. See KNOWN_ISSUES.md.
    /// </remarks>
    [Fact]
    public async Task FilterAuthAndTransaction_Ipv4MappedClient_SeeUnnormalizedMappedAddress()
    {
        var port = TestPorts.Allocate();
        var delivery = new RecordingDelivery();
        var filter = new ConfigurableFilter();
        var auth = new EndpointRecordingAuth();

        // Plaintext AUTH so the endpoint reaches IAuthLogin without a TLS handshake; the address the
        // callback receives is what is under test, not the transport.
        var options = TestServers.DefaultOptions();
        options.RequireEncryptionForAuth = false;

        var server = new SMTPServer(
            new[] { new ListeningParameters(IPAddress.IPv6Any, new[] { port }, null, dualMode: true) },
            options, delivery);
        server.SetFilter(filter);
        server.SetAuthLogin(auth);
        server.Start();

        using (server)
        {
            await using var s = await SmtpSession.ConnectAsync(port, IPAddress.Loopback);
            Assert.StartsWith("220 ", await s.ReadLineAsync());

            await s.Send("EHLO test.client");
            Assert.StartsWith("250", (await s.ReadResponseAsync())[^1]);

            // AUTH PLAIN, base64 of "\0user\0pass" — the endpoint reaches IAuthLogin either way.
            await s.Send("AUTH PLAIN AHVzZXIAcGFzcw==");
            Assert.StartsWith("235", await s.ReadLineAsync());

            await SendTransactionAsync(s);
        }

        // Every one of these is a policy or authentication consumer, and each sees the mapped form
        // while SPF and the Received header (asserted above) see the unmapped one.
        AssertMapped(filter.LastConnectionEp, "IMailFilter.IsConnectionAllowed");
        AssertMapped(filter.LastSenderEp, "IMailFilter.IsAllowedSender");
        AssertMapped(filter.LastDeliverEp, "IMailFilter.CanDeliver");
        AssertMapped(auth.LastEndpoint, "IAuthLogin.AuthPlain");
        AssertMapped(Assert.Single(delivery.Delivered).RemoteEndPoint, "MailTransaction.RemoteEndPoint");
    }

    private static void AssertMapped(EndPoint? ep, string consumer)
    {
        var address = Assert.IsType<IPEndPoint>(ep).Address;
        Assert.True(address.IsIPv4MappedToIPv6, $"{consumer} saw {address}, expected a v4-mapped address");
    }

    /// <summary>Accepts any AUTH PLAIN and records the endpoint it was handed.</summary>
    private sealed class EndpointRecordingAuth : IAuthLogin
    {
        public EndPoint? LastEndpoint;

        public Task<bool> AuthPlain(string authorizationIdentity, string authenticationIdentity,
            string password, EndPoint? remoteEndPoint, bool secureConnection)
        {
            LastEndpoint = remoteEndPoint;
            return Task.FromResult(true);
        }

        public Task<bool> AuthLogin(string login, string password, EndPoint? remoteEndPoint,
            bool secureConnection)
        {
            LastEndpoint = remoteEndPoint;
            return Task.FromResult(true);
        }
    }

    /// <summary>Drives one minimal accepted transaction so the Received header is built.</summary>
    private static async Task SendMessageAsync(SmtpSession s)
    {
        await s.Send("EHLO test.client");
        Assert.StartsWith("250", (await s.ReadResponseAsync())[^1]);
        await SendTransactionAsync(s);
    }

    /// <summary>The transaction itself, for callers that have already greeted (and perhaps authenticated).</summary>
    private static async Task SendTransactionAsync(SmtpSession s)
    {
        await s.Send("MAIL FROM:<a@b.com>");
        Assert.StartsWith("250", await s.ReadLineAsync());

        await s.Send("RCPT TO:<c@d.e>");
        Assert.StartsWith("250", await s.ReadLineAsync());

        await s.Send("DATA");
        Assert.StartsWith("354", await s.ReadLineAsync());

        await s.Send("Subject: hi");
        await s.Send("From: a@b.com");
        await s.Send("To: c@d.e");
        await s.Send("");
        await s.Send("hello world");
        await s.Send(".");
        Assert.StartsWith("250", await s.ReadLineAsync());
    }

    #endregion

    #region SPF ptr mechanism, both families

    private const string Domain = "spf6.test";

    // Documentation ranges (RFC 3849 / RFC 5737) — never routable, safe for tests.
    private static readonly IPAddress ClientV6 = IPAddress.Parse("2001:db8::7");
    private static readonly IPAddress ClientV4 = IPAddress.Parse("203.0.113.7");

    private static SpfValidator ValidatorFor(DnsStub stub) =>
        new(new IPEndPoint(IPAddress.Loopback, (ushort)stub.Port));

    /// <summary>
    /// RFC 7208 §5.5: "ptr" reverse-resolves the client, keeps names under the validated domain, and
    /// then confirms the name forward-resolves back to the client.
    /// </summary>
    /// <remarks>
    /// These are the suite's first positive ptr assertions, and they were written for IPv6 but apply
    /// to both families, because the mechanism turned out to be dead for both: the DnsStub reverse-name
    /// parser rejected every well-formed in-addr.arpa and ip6.arpa name, so AddPtr could never be
    /// answered. The pre-existing ptr test asserts Fail, which a non-matching mechanism also reaches
    /// through the terminal "-all", so it passed without the PTR lookup ever succeeding. Asserting Pass
    /// is what makes the mechanism's success path load-bearing: only a genuine match produces it.
    ///
    /// The IPv6 case additionally covers the two family-dependent steps — an ip6.arpa nibble reverse
    /// name, and AAAA rather than A for the forward confirmation.
    /// </remarks>
    [Fact]
    public async Task SpfPtr_Ipv6Client_ForwardConfirmedName_Passes()
    {
        using var stub = new DnsStub();
        stub.AddTxt(Domain, "v=spf1 ptr -all");
        stub.AddPtr(ClientV6, Domain);
        stub.AddAAAA(Domain, ClientV6); // forward confirmation

        Assert.Equal(ValidationResult.Pass, await ValidatorFor(stub).CheckHost(ClientV6, Domain));

        // The reverse query must actually have gone out as an ip6.arpa nibble name — otherwise a stub
        // that answered PTR for any name would satisfy the assertion above.
        Assert.Contains(stub.Queries, q => q.QType == 12 && q.Name.EndsWith(".ip6.arpa"));
    }

    /// <summary>IPv4 counterpart, covering the in-addr.arpa reverse name and the A forward lookup.</summary>
    [Fact]
    public async Task SpfPtr_Ipv4Client_ForwardConfirmedName_Passes()
    {
        using var stub = new DnsStub();
        stub.AddTxt(Domain, "v=spf1 ptr -all");
        stub.AddPtr(ClientV4, Domain);
        stub.AddA(Domain, ClientV4);

        Assert.Equal(ValidationResult.Pass, await ValidatorFor(stub).CheckHost(ClientV4, Domain));
        Assert.Contains(stub.Queries, q => q.QType == 12 && q.Name.EndsWith(".in-addr.arpa"));
    }

    /// <summary>
    /// The forward-confirmation half must actually be enforced: a PTR name under the domain whose AAAA
    /// does not include the client is not a match, so evaluation falls through to "-all". If the AAAA
    /// lookup were skipped, or matched against the wrong family, this would wrongly pass.
    /// </summary>
    [Fact]
    public async Task SpfPtr_Ipv6Client_ForwardLookupMismatch_Fails()
    {
        using var stub = new DnsStub();
        stub.AddTxt(Domain, "v=spf1 ptr -all");
        stub.AddPtr(ClientV6, Domain);
        stub.AddAAAA(Domain, IPAddress.Parse("2001:db8::99")); // resolves, but not to the client

        Assert.Equal(ValidationResult.Fail, await ValidatorFor(stub).CheckHost(ClientV6, Domain));
    }

    /// <summary>
    /// Fixture self-test: a PTR registered for an address must be answered for a reverse query on that
    /// address, across both families and the digit patterns that broke the parser.
    /// </summary>
    /// <remarks>
    /// The all-hex-digit case is the one that matters most: <c>DnsStub</c> previously parsed ip6.arpa
    /// nibble pairs with a decimal <c>byte.TryParse</c>, so any address containing a-f failed outright
    /// while digit-only pairs decoded to the wrong value. A round-trip over the whole fixture is what
    /// makes that class of defect visible — the PTR path is only as trustworthy as the stub behind it.
    /// </remarks>
    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("1.2.3.4")]
    [InlineData("255.255.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("2001:db8::7")]
    [InlineData("2001:db8:abcd:ef01:2345:6789:abcd:ef01")] // every hex digit a-f
    [InlineData("::1")]
    [InlineData("fe80::1")]
    public async Task DnsStub_ReverseNameRoundTrip_AnswersPtrForBothFamilies(string address)
    {
        var ip = IPAddress.Parse(address);
        using var stub = new DnsStub();
        stub.AddTxt(Domain, "v=spf1 ptr -all");
        stub.AddPtr(ip, Domain);

        if (ip.AddressFamily == AddressFamily.InterNetwork) stub.AddA(Domain, ip);
        else stub.AddAAAA(Domain, ip);

        Assert.Equal(ValidationResult.Pass, await ValidatorFor(stub).CheckHost(ip, Domain));
    }

    #endregion

    #region SPF a/mx dual-CIDR (RFC 7208 §5.3) — pins a known defect

    /// <summary>
    /// RFC 7208 §5.3 lets "a" and "mx" carry an IPv4 length, an IPv6 length after a double slash, or
    /// both: <c>a/24</c>, <c>a//64</c>, <c>a/24//64</c>. The parser takes everything after the FIRST
    /// slash as one decimal mask, so "a//64" yields "/64" and "a/24//64" yields "24//64" — neither
    /// parses, the length is dropped, and IPv6 silently falls back to /128.
    ///
    /// The error direction rejects mail that should be accepted: a sender inside the published AAAA
    /// record's /64, which the domain deliberately authorized, gets 554 at MAIL FROM. That is the same
    /// direction as the `exists:` defect in KNOWN_ISSUES.md.
    ///
    /// These tests pin the CURRENT WRONG behavior so the defect is tracked and the fix is visible as a
    /// deliberate change. When dual-CIDR parsing is implemented, both expectations become Pass.
    /// </summary>
    [Theory]
    [InlineData("v=spf1 a//64 -all")]
    [InlineData("v=spf1 a/24//64 -all")]
    public async Task SpfDualCidr_Ipv6PrefixIsIgnored_WronglyFails(string record)
    {
        using var stub = new DnsStub();
        stub.AddTxt(Domain, record);
        stub.AddAAAA(Domain, IPAddress.Parse("2001:db8::1")); // same /64 as ClientV6, different host

        // RFC-correct expectation is Pass; this asserts the defect until dual-CIDR parsing lands.
        Assert.Equal(ValidationResult.Fail, await ValidatorFor(stub).CheckHost(ClientV6, Domain));
    }

    /// <summary>
    /// The <c>mx</c> counterpart. It shares the CIDR parsing but not the control flow — <c>mx</c>
    /// resolves MX records first and then calls the address match once per exchange host — so the
    /// <c>a</c> cases above cannot stand in for it. Without this, a partial fix could restore <c>a</c>
    /// while leaving IPv6 senders authorized through <c>mx</c> still rejected.
    /// </summary>
    [Theory]
    [InlineData("v=spf1 mx//64 -all")]
    [InlineData("v=spf1 mx/24//64 -all")]
    public async Task SpfDualCidr_MxIpv6PrefixIsIgnored_WronglyFails(string record)
    {
        const string exchange = "mail." + Domain;

        using var stub = new DnsStub();
        stub.AddTxt(Domain, record);
        stub.AddMx(Domain, (10, exchange));
        stub.AddAAAA(exchange, IPAddress.Parse("2001:db8::1")); // same /64 as ClientV6, different host

        Assert.Equal(ValidationResult.Fail, await ValidatorFor(stub).CheckHost(ClientV6, Domain));

        // Guard against a false negative: the MX lookup and the exchange's AAAA lookup must both have
        // happened, so Fail reflects the CIDR defect rather than a fixture that answered nothing.
        Assert.Contains(stub.Queries, q => q.QType == 15 && q.Name == Domain);
        Assert.Contains(stub.Queries, q => q.QType == 28 && q.Name == exchange);
    }

    /// <summary>
    /// Control for the mx case: an exact AAAA match still passes, so the mechanism itself works and
    /// only the dual-CIDR prefix is being dropped.
    /// </summary>
    [Fact]
    public async Task SpfMx_ExactIpv6Match_Passes()
    {
        const string exchange = "mail." + Domain;

        using var stub = new DnsStub();
        stub.AddTxt(Domain, "v=spf1 mx -all");
        stub.AddMx(Domain, (10, exchange));
        stub.AddAAAA(exchange, ClientV6);

        Assert.Equal(ValidationResult.Pass, await ValidatorFor(stub).CheckHost(ClientV6, Domain));
    }

    /// <summary>
    /// Control for the above: an explicit <c>ip6:</c> prefix of the same width matches, proving the
    /// CIDR comparison itself is sound and the defect is confined to parsing the dual-CIDR syntax.
    /// </summary>
    [Fact]
    public async Task SpfIp6Cidr_SamePrefix_Passes()
    {
        using var stub = new DnsStub();
        stub.AddTxt(Domain, "v=spf1 ip6:2001:db8::/64 -all");

        Assert.Equal(ValidationResult.Pass, await ValidatorFor(stub).CheckHost(ClientV6, Domain));
    }

    #endregion
}
