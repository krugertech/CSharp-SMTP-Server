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

    #region Quoted local-parts (item 5b)

    /// <summary>
    /// RFC 5321 §4.1.2 permits a quoted-string local-part, inside which '&lt;', '&gt;' and '@' are
    /// ordinary characters.
    /// </summary>
    /// <remarks>
    /// These were all a permanent 501 before: the parser took the first '&gt;' as the path terminator
    /// and required exactly one '@' anywhere in the address, so a quoted local-part containing either
    /// was split at the wrong place and failed validation. Shared by RCPT TO, so it could lose a
    /// recipient as well as a sender.
    /// </remarks>
    [Theory]
    [InlineData("<\"a>b\"@example.com>", "\"a>b\"@example.com", "example.com")]
    [InlineData("<\"a<b\"@example.com>", "\"a<b\"@example.com", "example.com")]
    [InlineData("<\"a@b\"@example.com>", "\"a@b\"@example.com", "example.com")]
    [InlineData("<\"a b\"@example.com>", "\"a b\"@example.com", "example.com")]
    // A quoted-pair escaping a quote: the local-part is a"b, written "a\"b".
    [InlineData("<\"a\\\"b\"@example.com>", "\"a\\\"b\"@example.com", "example.com")]
    // Quoted local-part behind the ':' left by command parsing, as MAIL FROM actually delivers it.
    [InlineData(":<\"a>b\"@example.com>", "\"a>b\"@example.com", "example.com")]
    public void ProcessAddress_QuotedLocalPart_IsAccepted(string input, string expectedAddress, string expectedDomain)
    {
        var address = TransactionCommands.ProcessAddress(input, out var domain);

        Assert.Equal(expectedAddress, address);
        Assert.Equal(expectedDomain, domain);
    }

    /// <summary>
    /// A quoted local-part does not become a way to smuggle a second path past the anchored parser.
    /// </summary>
    /// <remarks>
    /// Making the path scanner quote-aware is exactly the kind of change that can reopen the
    /// parser/policy differential the anchoring exists to close: if a '&gt;' inside quotes is ignored,
    /// an attacker who can open a quote controls where the parser thinks the path ends. These pin
    /// that it cannot — an unterminated quote yields no path at all rather than falling back to the
    /// first '&gt;'.
    /// </remarks>
    [Theory]
    // Unterminated quoted-string: refused rather than guessed at.
    [InlineData("<\"a@example.com>")]
    [InlineData("<\"unterminated")]
    // A quote cannot swallow the terminator and re-present a later bare path as the address.
    [InlineData("<\"a> <ceo@victim.example>")]
    // More than one unquoted '@' is still ambiguous and refused.
    [InlineData("<\"a\"@b@example.com>")]
    // A quoted-string in the DOMAIN is not a local-part construct and must not pass.
    [InlineData("<a@\"b.c\">")]
    public void ProcessAddress_QuotedLocalPartAbuse_IsRefused(string input)
    {
        var address = TransactionCommands.ProcessAddress(input, out var domain);

        Assert.Null(address);
        Assert.Null(domain);
    }

    /// <summary>
    /// The null-reverse-path smuggling defences survive the quote-aware scanner.
    /// </summary>
    /// <remarks>
    /// Each of these previously read as <c>MAIL FROM:&lt;&gt;</c> with a real address left in the
    /// command — the caller saw an empty sender and skipped SPF while an address sat there. They are
    /// re-asserted here because <c>FindPathEnd</c> is on that exact path.
    /// </remarks>
    [Theory]
    [InlineData(":<> BODY=8BITMIME AUTH=<>", true)]      // legitimate O365 form: really is a null path
    [InlineData(":<> AUTH=<> <ceo@victim.example>", false)]
    [InlineData(":<><ceo@victim.example>", false)]
    [InlineData(":ceo@victim.example <>", false)]
    [InlineData(":AUTH=<> <ceo@victim.example>", false)]
    public void IsNullReversePath_SmugglingDefences_Hold(string input, bool expected) =>
        Assert.Equal(expected, TransactionCommands.IsNullReversePath(input));

    #endregion
}
