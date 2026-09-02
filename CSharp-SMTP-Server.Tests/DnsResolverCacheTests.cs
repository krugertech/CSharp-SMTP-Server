using System.Net;
using CSharp_SMTP_Server.Protocol;
using CSharp_SMTP_Server.Protocol.Dns;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Proves the resolver's response cache is real, by counting what reaches the wire.
/// </summary>
/// <remarks>
/// This is the point of replacing the previous DNS client, which had no cache of any kind. Every SPF
/// evaluation issues TXT, MX and A/AAAA queries and recurses through <c>include:</c> and
/// <c>redirect=</c> up to RFC 7208's limit of ten DNS-consuming terms, with a DMARC TXT lookup on
/// top, so sustained traffic from a handful of customer domains re-resolved the same provider
/// include-chain on every single message.
///
/// A cache is only demonstrated by showing the second lookup did NOT reach the wire, which is what
/// <see cref="DnsStub.QueryCount"/> is used for here.
/// </remarks>
public sealed class DnsResolverCacheTests
{
    private static IDnsResolver ResolverFor(DnsStub stub) =>
        SMTPServer.CreateResolver(DnsResolverMode.Explicit, new[] {new IPEndPoint(IPAddress.Loopback, (ushort)stub.Port)});

    [Fact]
    public async Task RepeatedLookupWithinTtl_IssuesNoSecondWireQuery()
    {
        using var stub = new DnsStub();
        stub.AddTxt("cached.test", "v=spf1 ip4:127.0.0.1 -all");

        var resolver = ResolverFor(stub);

        var first = await resolver.QueryAsync("cached.test", DnsRecordType.Txt);
        Assert.Equal(DnsQueryStatus.Success, first.Status);
        Assert.Equal("v=spf1 ip4:127.0.0.1 -all", Assert.Single(first.Records).Text);

        var afterFirst = stub.QueryCount;
        Assert.True(afterFirst > 0, "the first lookup never reached the stub");

        // Same question, still inside the record's 60-second TTL.
        for (var i = 0; i < 5; i++)
        {
            var again = await resolver.QueryAsync("cached.test", DnsRecordType.Txt);
            Assert.Equal("v=spf1 ip4:127.0.0.1 -all", Assert.Single(again.Records).Text);
        }

        Assert.Equal(afterFirst, stub.QueryCount);
    }

    [Fact]
    public async Task DifferentNamesAndTypes_AreCachedSeparately()
    {
        // A cache keyed too loosely would answer one question with another's records.
        using var stub = new DnsStub();
        stub.AddTxt("a.test", "v=spf1 -all");
        stub.AddTxt("b.test", "v=spf1 +all");
        stub.AddA("a.test", IPAddress.Parse("198.51.100.9"));

        var resolver = ResolverFor(stub);

        Assert.Equal("v=spf1 -all", Assert.Single((await resolver.QueryAsync("a.test", DnsRecordType.Txt)).Records).Text);
        Assert.Equal("v=spf1 +all", Assert.Single((await resolver.QueryAsync("b.test", DnsRecordType.Txt)).Records).Text);
        Assert.Equal(IPAddress.Parse("198.51.100.9"), Assert.Single((await resolver.QueryAsync("a.test", DnsRecordType.A)).Records).Address);

        var afterFirstRound = stub.QueryCount;

        Assert.Equal("v=spf1 -all", Assert.Single((await resolver.QueryAsync("a.test", DnsRecordType.Txt)).Records).Text);
        Assert.Equal("v=spf1 +all", Assert.Single((await resolver.QueryAsync("b.test", DnsRecordType.Txt)).Records).Text);
        Assert.Equal(IPAddress.Parse("198.51.100.9"), Assert.Single((await resolver.QueryAsync("a.test", DnsRecordType.A)).Records).Address);

        Assert.Equal(afterFirstRound, stub.QueryCount);
    }

    [Fact]
    public async Task TransientFailures_AreNotCached_AndRecoverOnRetry()
    {
        // The one thing that must NOT be cached. SPF reports Temperror for a failed lookup and DMARC
        // defers the message on it (451 4.7.1), so a cached SERVFAIL would keep deferring a retrying
        // sender after resolution had already recovered.
        using var stub = new DnsStub();
        stub.AddTxt("flaky.test", "v=spf1 ip4:127.0.0.1 -all");
        stub.SetServFail("flaky.test");

        var resolver = ResolverFor(stub);

        Assert.Equal(DnsQueryStatus.Failure, (await resolver.QueryAsync("flaky.test", DnsRecordType.Txt)).Status);

        var afterFailure = stub.QueryCount;

        // A second attempt while still broken must re-query rather than be served a cached failure.
        Assert.Equal(DnsQueryStatus.Failure, (await resolver.QueryAsync("flaky.test", DnsRecordType.Txt)).Status);
        Assert.True(stub.QueryCount > afterFailure, "the failed lookup was cached — a resolver outage would stick");

        stub.ClearServFail("flaky.test");

        var recovered = await resolver.QueryAsync("flaky.test", DnsRecordType.Txt);
        Assert.Equal(DnsQueryStatus.Success, recovered.Status);
        Assert.Equal("v=spf1 ip4:127.0.0.1 -all", Assert.Single(recovered.Records).Text);
    }

    [Fact]
    public async Task NonExistentName_IsReportedAsNameError_NotFailure()
    {
        // RFC 7208 §4.3 turns on this distinction: NXDOMAIN is a definitive "no record" answer, while a
        // failure says nothing about the domain. Collapsing them made a resolver outage look like
        // "publishes no policy" for every domain at once.
        using var stub = new DnsStub();
        stub.SetNxDomain("missing.test");

        var result = await ResolverFor(stub).QueryAsync("missing.test", DnsRecordType.Txt);

        Assert.Equal(DnsQueryStatus.NameError, result.Status);
        Assert.Empty(result.Records);
    }

    [Fact]
    public async Task SpfEvaluation_ReusesCachedRecordsAcrossMessages()
    {
        // The end-to-end shape of the win: the same sender domain checked repeatedly, as a customer
        // sending a run of messages would, resolves its include-chain once rather than per message.
        using var stub = new DnsStub();
        stub.AddTxt("sender.test", "v=spf1 include:provider.test -all");
        stub.AddTxt("provider.test", "v=spf1 ip4:127.0.0.1 -all");

        var validator = new Protocol.SPF.SpfValidator(ResolverFor(stub));

        Assert.Equal(ValidationResult.Pass, await validator.CheckHost(IPAddress.Loopback, "sender.test"));

        var afterFirstMessage = stub.QueryCount;
        Assert.True(afterFirstMessage >= 2, "expected the include chain to be resolved on the first pass");

        for (var i = 0; i < 10; i++)
            Assert.Equal(ValidationResult.Pass, await validator.CheckHost(IPAddress.Loopback, "sender.test"));

        Assert.Equal(afterFirstMessage, stub.QueryCount);
    }
}
