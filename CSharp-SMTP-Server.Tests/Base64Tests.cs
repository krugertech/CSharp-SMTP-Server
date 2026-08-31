using CSharp_SMTP_Server.Misc;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Phase 1 (§3.6): unit tests for the internal Base64 helper used by AUTH LOGIN/PLAIN parsing.
/// Invalid input must yield null (the protocol layer maps that to a 535 response), never throw.
/// </summary>
public sealed class Base64Tests
{
    [Fact]
    public void Encode_KnownVector()
    {
        Assert.Equal("dXNlcg==", Base64.Base64Encode("user"));
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("héllo wörld ✓")] // UTF-8 multibyte round-trip
    [InlineData("a@b.c:secret!")]
    public void RoundTrip_ReturnsOriginal(string input)
    {
        Assert.Equal(input, Base64.Base64Decode(Base64.Base64Encode(input)));
    }

    [Fact]
    public void EmptyString_RoundTrips()
    {
        Assert.Equal(string.Empty, Base64.Base64Encode(string.Empty));
        Assert.Equal(string.Empty, Base64.Base64Decode(string.Empty));
    }

    [Fact]
    public void Decode_MissingPadding_ReturnsNull_DocumentCurrentBehavior()
    {
        // Modern .NET's Convert.FromBase64String is strict about padding — "dXNlcg" (no '=') throws
        // FormatException, which the helper maps to null → a 535 on the wire. Pin it so a future
        // lenient-decode change is a conscious decision.
        Assert.Null(Base64.Base64Decode("dXNlcg"));
    }

    [Fact]
    public void Decode_EmbeddedWhitespace_IsIgnored()
    {
        // Convert.FromBase64String ignores whitespace anywhere in the input — pin it, since AUTH
        // lines arriving over a real socket may carry stray spaces/newlines.
        Assert.Equal("user", Base64.Base64Decode("dXNl\ncg=="));
        Assert.Equal("user", Base64.Base64Decode(" dXNlc g== "));
    }

    [Theory]
    [InlineData("!!!")]          // invalid characters
    [InlineData("abc")]          // length not a multiple of 4
    [InlineData("dXNl=cg=")]     // padding in the middle
    public void Decode_InvalidInput_ReturnsNull(string input)
    {
        Assert.Null(Base64.Base64Decode(input));
    }
}
