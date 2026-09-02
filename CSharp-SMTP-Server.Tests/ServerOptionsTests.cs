using System.Net;
using System.Security.Authentication;
using CSharp_SMTP_Server.Protocol.Dns;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Pure unit tests for ServerOptions — field defaults and the resolver-mode invariants around
/// ValidateSPF / ValidateDMARC.
/// </summary>
/// <remarks>
/// These previously pinned two behaviours that are now gone. The first was the silent Cloudflare
/// fallback: constructing with validation on and no endpoint substituted 1.1.1.1:53, sending every
/// SPF and DMARC lookup — and with it the sending domains of all inbound mail — to a third-party
/// operator the deployment never chose. The second was a contradiction between the constructor and
/// the property setters: the constructor wrote the backing fields directly and invented an endpoint,
/// while the setters threw for that same state, and because the endpoint was readonly an instance
/// built with validation off could never enable it afterwards. Identical configuration therefore
/// succeeded or failed based only on the order it was applied in. Both are replaced by
/// <see cref="DnsResolverMode"/>, which makes "which resolver" an explicit choice.
/// </remarks>
public sealed class ServerOptionsTests
{
    private static readonly IPEndPoint Quad9 = new(IPAddress.Parse("9.9.9.9"), 53);

    [Fact]
    public void Defaults_AreDocumentedValues()
    {
        var o = new ServerOptions(false, false, DnsResolverMode.Disabled, null);

        Assert.Equal("CSharp SMTP Server", o.ServerName);
        Assert.True(o.RequireEncryptionForAuth);
        Assert.Equal(SslProtocols.Tls12, o.Protocols);
        Assert.Equal(10485760u, o.MessageCharactersLimit);
        Assert.Equal(50u, o.RecipientsLimit);
        Assert.Contains("public_suffix_list.dat", o.PublicSuffixList);
    }

    #region Resolver mode selection

    [Fact]
    public void Ctor_NoEndpoint_UsesSystemResolvers_NotAPublicOne()
    {
        // The replacement for the Cloudflare fallback: no endpoint now means "the machine's own name
        // servers", so an unconfigured deployment resolves through infrastructure it already trusts
        // rather than silently through a third party.
        var o = new ServerOptions(true, true);

        Assert.Equal(DnsResolverMode.System, o.ResolverMode);
        Assert.Empty(o.DnsServerEndpoints);
    }

    [Fact]
    public void Ctor_ExplicitEndpoint_IsPreservedAndSelectsExplicitMode()
    {
        var o = new ServerOptions(true, true, Quad9);

        Assert.Equal(DnsResolverMode.Explicit, o.ResolverMode);
        Assert.Equal(new[] {Quad9}, o.DnsServerEndpoints);
        Assert.True(o.ValidateSPF);
        Assert.True(o.ValidateDMARC);
    }

    [Fact]
    public void Ctor_ExplicitMode_AcceptsMultipleEndpoints()
    {
        var second = new IPEndPoint(IPAddress.Parse("149.112.112.112"), 53);
        var o = new ServerOptions(true, true, DnsResolverMode.Explicit, new[] {Quad9, second});

        Assert.Equal(new[] {Quad9, second}, o.DnsServerEndpoints);
    }

    [Fact]
    public void Ctor_ExplicitMode_WithoutEndpoints_Throws()
    {
        // "Explicit" that resolves to nothing would have to fall back to something, which is the
        // behaviour being removed.
        Assert.Throws<ArgumentException>(() => new ServerOptions(true, true, DnsResolverMode.Explicit, null));
        Assert.Throws<ArgumentException>(() => new ServerOptions(true, true, DnsResolverMode.Explicit, Array.Empty<IPEndPoint>()));
    }

    [Theory]
    [InlineData(DnsResolverMode.System)]
    [InlineData(DnsResolverMode.Disabled)]
    public void Ctor_NonExplicitMode_WithEndpoints_Throws(DnsResolverMode mode)
    {
        // Supplying endpoints that would be ignored is a configuration error, not a no-op: the caller
        // believes they have pinned a resolver.
        Assert.Throws<ArgumentException>(() => new ServerOptions(false, false, mode, new[] {Quad9}));
    }

    [Fact]
    public void Ctor_DisabledMode_WithValidationRequested_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ServerOptions(true, false, DnsResolverMode.Disabled, null));
        Assert.Throws<ArgumentException>(() => new ServerOptions(false, true, DnsResolverMode.Disabled, null));
    }

    [Fact]
    public void Ctor_DisabledMode_WithoutValidation_IsAllowed()
    {
        var o = new ServerOptions(false, false, DnsResolverMode.Disabled, null);

        Assert.Equal(DnsResolverMode.Disabled, o.ResolverMode);
        Assert.False(o.ValidateSPF);
        Assert.False(o.ValidateDMARC);
    }

    #endregion

    #region Constructor and setters agree

    /// <summary>
    /// The contradiction this replaces: an instance constructed with validation disabled could never
    /// enable it later, because the constructor invented an endpoint the setters then demanded and the
    /// field was readonly. With a resolver always selected, order of application no longer decides
    /// whether the same configuration is legal.
    /// </summary>
    [Fact]
    public void ValidationCanBeEnabledAfterConstruction_WhenAResolverExists()
    {
        var systemMode = new ServerOptions(false, false);
        systemMode.ValidateSPF = true;
        systemMode.ValidateDMARC = true;
        Assert.True(systemMode.ValidateSPF);
        Assert.True(systemMode.ValidateDMARC);

        var explicitMode = new ServerOptions(false, false, Quad9);
        explicitMode.ValidateSPF = true;
        Assert.True(explicitMode.ValidateSPF);
    }

    [Fact]
    public void ValidationCannotBeEnabled_WhenTheResolverIsDisabled()
    {
        var o = new ServerOptions(false, false, DnsResolverMode.Disabled, null);

        Assert.Throws<InvalidOperationException>(() => o.ValidateSPF = true);
        Assert.Throws<InvalidOperationException>(() => o.ValidateDMARC = true);

        Assert.False(o.ValidateSPF); // unchanged after a failed set
        Assert.False(o.ValidateDMARC);
    }

    [Fact]
    public void ValidationCanAlwaysBeDisabled()
    {
        var withResolver = new ServerOptions(true, true, Quad9);
        withResolver.ValidateSPF = false;
        withResolver.ValidateDMARC = false;
        Assert.False(withResolver.ValidateSPF);
        Assert.False(withResolver.ValidateDMARC);

        var withoutResolver = new ServerOptions(false, false, DnsResolverMode.Disabled, null);
        withoutResolver.ValidateSPF = false; // already false — must not throw either
        Assert.False(withoutResolver.ValidateSPF);
    }

    #endregion
}
