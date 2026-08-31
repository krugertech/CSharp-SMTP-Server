using System.Reflection;
using CSharp_SMTP_Server.Protocol.DMARC;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Phase 1 (§3.5): unit tests for DmarcValidator.GetOrganizationalDomain — the public-suffix walk used
/// to align DMARC header/envelope domains. The suffix list is served from a local HTTP server so no
/// internet access is needed (DmarcValidator's constructor downloads it and blocks).
///
/// Note: the loaded flag + HashSet are process-wide statics; this class is the only Phase 1 test that
/// touches them, which keeps xunit's cross-class parallelism safe.
/// </summary>
public sealed class DmarcOrganizationalDomainTests : IClassFixture<SuffixListFixture>
{
    private readonly SuffixListFixture _fixture;

    public DmarcOrganizationalDomainTests(SuffixListFixture fixture) => _fixture = fixture;

    [Fact]
    public void TwoLabelSuffix_OrganizationalDomainIsLastTwoLabels()
    {
        Assert.Equal("example.com", DmarcValidator.GetOrganizationalDomain("mail.example.com"));
    }

    [Fact]
    public void MultiLevelPublicSuffix_WalksUpUntilNonSuffix()
    {
        // "co.uk" is in the served list, so the org domain of a.b.co.uk is b.co.uk.
        Assert.Equal("b.co.uk", DmarcValidator.GetOrganizationalDomain("a.b.co.uk"));
    }

    [Fact]
    public void DeepSubdomain_OnlyWalksWhileSuffix()
    {
        // "d.co.uk" is not a suffix, so the walk stops there.
        Assert.Equal("d.co.uk", DmarcValidator.GetOrganizationalDomain("a.b.c.d.co.uk"));
    }

    [Theory]
    [InlineData("x.y")]   // two labels — returned unchanged
    [InlineData("com")]   // single label — returned unchanged
    public void ShortDomains_AreReturnedUnchanged(string domain)
    {
        Assert.Equal(domain, DmarcValidator.GetOrganizationalDomain(domain));
    }

    [Fact]
    public async Task ForceRefreshList_SwitchesTheActiveSuffixSet()
    {
        // With a list that lacks "co.uk", the walk cannot stop at co.uk — result degrades to it.
        using var reduced = new LocalHttpServer("com\nnet\norg\nuk\n");

        try
        {
            await _fixture.Validator.ForceRefreshList(reduced.Url);
            Assert.Equal("co.uk", DmarcValidator.GetOrganizationalDomain("a.b.co.uk"));
        }
        finally
        {
            // restore the canonical list for any later test in this class (methods run sequentially)
            await _fixture.Validator.ForceRefreshList(_fixture.CanonicalUrl);
        }

        Assert.Equal("b.co.uk", DmarcValidator.GetOrganizationalDomain("a.b.co.uk"));
    }

    [Fact]
    public void GetOrganizationalDomain_ThrowsWhenSuffixListNotLoaded()
    {
        // White-box: flip the private static loaded flag, expect the documented exception, restore.
        var field = typeof(DmarcValidator).GetField("_publicSuffixesLoaded", BindingFlags.NonPublic | BindingFlags.Static)!;
        var original = (bool)field.GetValue(null)!;

        try
        {
            field.SetValue(null, false);
            var ex = Assert.Throws<Exception>(() => DmarcValidator.GetOrganizationalDomain("a.b.c"));
            Assert.Equal("Suffix list is not loaded, because DMARC Validator was never initialized.", ex.Message);
        }
        finally
        {
            field.SetValue(null, original);
        }
    }
}

/// <summary>
/// Serves the canonical suffix list over loopback HTTP and constructs a real DmarcValidator
/// (via an SMTPServer with DMARC enabled), which downloads the list in its constructor.
/// </summary>
public sealed class SuffixListFixture : IDisposable
{
    private readonly LocalHttpServer _http;

    public const string CanonicalList = """
        // === BEGIN ICANN DOMAINS ===

        com
        net
        org
        uk
        co.uk
        """;

    public string CanonicalUrl => _http.Url;
    public DmarcValidator Validator { get; }

    public SuffixListFixture()
    {
        _http = new LocalHttpServer(CanonicalList);

        var options = new ServerOptions(false, true) { PublicSuffixList = _http.Url };
        using var server = new SMTPServer(null, options, NoopDelivery.Instance);
        Validator = server.DmarcValidator!; // constructor blocks until the list is downloaded
    }

    public void Dispose() => _http.Dispose();
}
