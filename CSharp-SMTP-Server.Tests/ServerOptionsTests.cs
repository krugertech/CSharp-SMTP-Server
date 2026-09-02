using System.Net;
using System.Security.Authentication;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Phase 1 (§3.2): pure unit tests for ServerOptions — field defaults and the DNS-endpoint validation
/// rules around ValidateSPF / ValidateDMARC.
/// </summary>
public sealed class ServerOptionsTests
{
    [Fact]
    public void Defaults_AreDocumentedValues()
    {
        var o = new ServerOptions(false, false);

        Assert.Equal("CSharp SMTP Server", o.ServerName);
        Assert.True(o.RequireEncryptionForAuth);
        Assert.Equal(SslProtocols.Tls12, o.Protocols);
        Assert.Equal(10485760u, o.MessageCharactersLimit);
        Assert.Equal(50u, o.RecipientsLimit);
        Assert.Contains("public_suffix_list.dat", o.PublicSuffixList);
    }

    [Fact]
    public void Ctor_SpfRequested_NoEndpoint_DefaultsToCloudflare()
    {
        var o = new ServerOptions(true, false, null);

        Assert.True(o.ValidateSPF);
        Assert.False(o.ValidateDMARC);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53), o.DnsServerEndpoint);
    }

    [Fact]
    public void Ctor_DmarcRequested_NoEndpoint_DefaultsToCloudflare()
    {
        var o = new ServerOptions(false, true, null);

        Assert.False(o.ValidateSPF);
        Assert.True(o.ValidateDMARC);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53), o.DnsServerEndpoint);
    }

    [Fact]
    public void Ctor_NoValidationRequested_EndpointStaysNull()
    {
        var o = new ServerOptions(false, false, null);

        Assert.Null(o.DnsServerEndpoint);
    }

    [Fact]
    public void Ctor_ExplicitEndpoint_IsPreserved()
    {
        var endpoint = new IPEndPoint(IPAddress.Parse("9.9.9.9"), 53);
        var o = new ServerOptions(true, true, endpoint);

        Assert.Equal(endpoint, o.DnsServerEndpoint);
        Assert.True(o.ValidateSPF);
        Assert.True(o.ValidateDMARC);
    }

    [Fact]
    public void Ctor_FallbackApplied_IsFlaggedAsDefault()
    {
        Assert.True(new ServerOptions(true, false, null).DnsServerEndpointIsDefault);
        Assert.True(new ServerOptions(false, true, null).DnsServerEndpointIsDefault);
        Assert.True(new ServerOptions(true, true, null).DnsServerEndpointIsDefault);
    }

    [Fact]
    public void Ctor_ExplicitEndpoint_IsNotFlaggedAsDefault()
    {
        var o = new ServerOptions(true, true, new IPEndPoint(IPAddress.Parse("9.9.9.9"), 53));

        Assert.False(o.DnsServerEndpointIsDefault);
    }

    [Fact]
    public void Ctor_NoValidation_NoFallback_IsNotFlaggedAsDefault()
    {
        var o = new ServerOptions(false, false, null);

        Assert.Null(o.DnsServerEndpoint);
        Assert.False(o.DnsServerEndpointIsDefault);
    }

    [Fact]
    public void ValidateSPF_SetterTrue_WithoutEndpoint_Throws()
    {
        var o = new ServerOptions(false, false);

        var ex = Assert.Throws<Exception>(() => o.ValidateSPF = true);

        Assert.Equal("SPF validation can't be enabled if DNS endpoint is not defined!", ex.Message);
        Assert.False(o.ValidateSPF); // unchanged after failed set
    }

    [Fact]
    public void ValidateDMARC_SetterTrue_WithoutEndpoint_Throws()
    {
        var o = new ServerOptions(false, false);

        var ex = Assert.Throws<Exception>(() => o.ValidateDMARC = true);

        Assert.Equal("DMARC validation can't be enabled if DNS endpoint is not defined!", ex.Message);
        Assert.False(o.ValidateDMARC);
    }

    [Fact]
    public void ValidateSPF_SetterTrue_WithEndpoint_Succeeds()
    {
        var o = new ServerOptions(false, false, new IPEndPoint(IPAddress.Parse("9.9.9.9"), 53));

        o.ValidateSPF = true;

        Assert.True(o.ValidateSPF);
    }

    [Fact]
    public void ValidateSPF_SetterFalse_AlwaysAllowed()
    {
        var withEndpoint = new ServerOptions(true, false, new IPEndPoint(IPAddress.Parse("9.9.9.9"), 53));
        withEndpoint.ValidateSPF = false;
        Assert.False(withEndpoint.ValidateSPF);

        var withoutEndpoint = new ServerOptions(false, false);
        withoutEndpoint.ValidateDMARC = false; // already false — must not throw either
        Assert.False(withoutEndpoint.ValidateDMARC);
    }
}
