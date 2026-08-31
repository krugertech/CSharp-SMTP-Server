using CSharp_SMTP_Server.Protocol.Commands;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Phase 1 (§3.7): the full address-validation matrix for TransactionCommands.ProcessAddress —
/// the gate behind both MAIL FROM and RCPT TO (501 responses). Includes pins for surprising edge
/// cases that current code accepts on purpose or by accident.
/// </summary>
public sealed class ProcessAddressTests
{
    [Theory]
    // null / empty / no angle brackets
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    [InlineData("a@b.com", null, null)]
    // empty or whitespace-only inside brackets
    [InlineData("<>", null, null)]
    [InlineData("<   >", null, null)]
    // domain without a dot / dot before the @
    [InlineData("<a@b>", null, null)]
    [InlineData("<a.b@c>", null, null)]
    // multiple @ signs
    [InlineData("<a@@b.c>", null, null)]
    // valid forms
    [InlineData("<ab@c.d.e>", "ab@c.d.e", "c.d.e")]
    [InlineData(":<a@b.c>", "a@b.c", "b.c")]                       // leading colon from command parsing
    [InlineData("\"John Doe\" <john@example.com>", "john@example.com", "example.com")] // display name
    [InlineData("<user@sub.domain.example>", "user@sub.domain.example", "sub.domain.example")]
    public void ProcessAddress_ValidatesPerMatrix(string? input, string? expectedAddress, string? expectedDomain)
    {
        var address = TransactionCommands.ProcessAddress(input, out var domain);

        Assert.Equal(expectedAddress, address);
        Assert.Equal(expectedDomain, domain);
    }

    [Fact]
    public void ProcessAddress_EmptyLocalPart_IsAccepted_PinCurrentBehavior()
    {
        // "<@b.c>" passes today: only the single-@ and dot-in-domain rules are enforced. Pin it so a
        // future tightening of local-part validation is a conscious, test-updating decision.
        var address = TransactionCommands.ProcessAddress("<@b.c>", out var domain);

        Assert.Equal("@b.c", address);
        Assert.Equal("b.c", domain);
    }

    [Fact]
    public void ProcessAddress_DotOnlyDomain_IsAccepted_PinCurrentBehavior()
    {
        // "<a@.c>" passes today: the domain ".c" contains a dot, which is all that is checked.
        var address = TransactionCommands.ProcessAddress("<a@.c>", out var domain);

        Assert.Equal("a@.c", address);
        Assert.Equal(".c", domain);
    }

    [Fact]
    public void ProcessAddress_MultipleBracketPairs_FirstPairWins()
    {
        var address = TransactionCommands.ProcessAddress("<a@b.c> <x@y.z>", out var domain);

        Assert.Equal("a@b.c", address);
        Assert.Equal("b.c", domain);
    }
}
