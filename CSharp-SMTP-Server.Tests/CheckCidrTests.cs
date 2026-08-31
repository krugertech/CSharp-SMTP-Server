using System.Net;
using CSharp_SMTP_Server.Protocol.SPF;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Phase 1 (§3.4): full matrix for SpfValidator.CheckCIDR — the pure static subnet-matching helper
/// used by every SPF ip4/ip6/a/mx mechanism evaluation.
/// </summary>
public sealed class CheckCidrTests
{
    [Theory]
    // mask 0: any two addresses of the same family match
    [InlineData("10.0.0.1", "203.0.113.9", 0, true)]
    [InlineData("::1", "ffff::1", 0, true)]
    // mask 0 does NOT cross address families (family check happens first)
    [InlineData("10.0.0.1", "::1", 0, false)]
    // exact match at maximum masks
    [InlineData("10.0.0.1", "10.0.0.1", 32, true)]
    [InlineData("::1", "::1", 128, true)]
    // one-bit difference at maximum mask
    [InlineData("10.0.0.1", "10.0.0.2", 32, false)]
    [InlineData("::1", "::2", 128, false)]
    // byte-aligned masks
    [InlineData("10.0.0.1", "10.0.0.254", 24, true)]
    [InlineData("10.0.1.1", "10.0.0.1", 24, false)]
    // mid-byte masks (top bit of the boundary byte)
    [InlineData("192.168.1.1", "192.168.1.100", 25, true)]   // /25: last octet 0x01 vs 0x64 — top bit both clear
    [InlineData("192.168.1.1", "192.168.1.128", 25, false)]  // /25: last octet 0x01 vs 0x80 — top bits differ
    [InlineData("10.64.0.1", "10.64.127.254", 17, true)]     // /17 fixes both first octets + top bit of the third: 0x00 vs 0x7F — clear
    [InlineData("10.64.0.1", "10.64.128.1", 17, false)]      // /17: third octet 0x00 vs 0x80 — top bits differ
    // oversized mask is clamped to the address width (behaves like /32)
    [InlineData("10.0.0.1", "10.0.0.1", 33, true)]
    [InlineData("10.0.0.1", "10.0.0.2", 33, false)]
    public void CheckCIDR_MatchesExpected(string a, string b, int mask, bool expected)
    {
        Assert.Equal(expected, SpfValidator.CheckCIDR(IPAddress.Parse(a), IPAddress.Parse(b), mask));
    }

    [Fact]
    public void CheckCIDR_NegativeMask_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            SpfValidator.CheckCIDR(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.2"), -1));

        // The library uses the message-only ArgumentException ctor, so "mask" lands in Message,
        // not ParamName — pin that (harmless today, but a refactor to the two-arg ctor is visible).
        Assert.Equal("mask", ex.Message);
        Assert.Null(ex.ParamName);
    }
}
