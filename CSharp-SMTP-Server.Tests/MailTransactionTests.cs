using System.Net;
using CSharp_SMTP_Server.Networking;
using CSharp_SMTP_Server.Protocol;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Phase 1 (§3.3): unit tests for MailTransaction — MimeKit-backed parsing, AddHeader and Clone semantics.
/// Includes pins for confirmed upstream bugs B1–B4 (see TEST_PLAN.md §2); those tests document current
/// behavior on purpose and must be updated together with any fix.
/// </summary>
public sealed class MailTransactionTests
{
    private const string SimpleMessage =
        "From: sender@example.com\r\n" +
        "To: rcpt@example.com\r\n" +
        "Subject: Hello World\r\n" +
        "\r\n" +
        "body text";

    private static MailTransaction Tx(string rawBody, string from = "a@b.c", ValidationResult spf = ValidationResult.CheckDisabled) =>
        new(from, from, spf) { RawBody = rawBody };

    // ─── ParsedMessage / Subject ──────────────────────────────────────────────

    [Fact]
    public void ParsedMessage_IsParsedOnce_AndCached()
    {
        var t = Tx(SimpleMessage);

        var first = t.ParsedMessage;
        var second = t.ParsedMessage;

        Assert.Same(first, second);
    }

    [Fact]
    public void Subject_PlainHeader()
    {
        Assert.Equal("Hello World", Tx(SimpleMessage).Subject);
    }

    [Fact]
    public void Subject_EncodedWord_IsDecoded()
    {
        var raw = "From: a@b.c\r\nSubject: =?UTF-8?B?SGVsbG8gV29ybGQ=?=\r\n\r\nx";

        Assert.Equal("Hello World", Tx(raw).Subject);
    }

    // ─── GetFrom / GetTo / GetCc / GetBcc (bug B1 pins) ──────────────────────

    [Fact]
    public void GetFrom_PlainAddress_ReturnsEmptyDisplayName_BugB1()
    {
        // BUG B1: returns MimeKit's display name, not the address. For a plain "sender@example.com"
        // header the display name is empty — so DMARC validation (which needs "<…>" form) can never
        // see the real sender domain for ordinary mail. Pin until fixed.
        Assert.Equal(string.Empty, Tx(SimpleMessage).GetFrom);
    }

    [Fact]
    public void GetFrom_DisplayName_ReturnsDisplayNameOnly_BugB1()
    {
        var raw = "From: John Doe <john@example.com>\r\nSubject: t\r\n\r\nx";

        Assert.Equal("John Doe", Tx(raw).GetFrom); // not "john@example.com"
    }

    [Fact]
    public void GetFrom_MissingHeader_ReturnsNull()
    {
        var raw = "Subject: x\r\n\r\nbody";

        Assert.Null(Tx(raw).GetFrom);
    }

    [Fact]
    public void GetTo_ReturnsDisplayNames_NotAddresses_BugB1()
    {
        var raw = "From: a@b.c\r\nTo: rcpt@example.com, R2 <r2@e.c>\r\nSubject: t\r\n\r\nx";

        Assert.Equal(new[] { string.Empty, "R2" }, Tx(raw).GetTo().ToArray()); // first entry is the empty display name
    }

    [Fact]
    public void GetCc_GetBcc_MissingHeaders_YieldEmpty()
    {
        var t = Tx(SimpleMessage);

        Assert.Empty(t.GetCc());
        Assert.Empty(t.GetBcc());
    }

    // ─── GetMessageBody ───────────────────────────────────────────────────────

    [Fact]
    public void GetMessageBody_TextPlain()
    {
        Assert.Equal("body text", Tx(SimpleMessage).GetMessageBody());
    }

    [Fact]
    public void GetMessageBody_Multipart_PrefersTextPart()
    {
        var raw =
            "From: sender@example.com\r\n" +
            "To: rcpt@example.com\r\n" +
            "Subject: multi\r\n" +
            "Content-Type: multipart/alternative; boundary=\"bb\"\r\n" +
            "\r\n" +
            "--bb\r\n" +
            "Content-Type: text/plain\r\n" +
            "\r\n" +
            "hello plain\r\n" +
            "--bb\r\n" +
            "Content-Type: text/html\r\n" +
            "\r\n" +
            "<p>hello html</p>\r\n" +
            "--bb--\r\n";

        Assert.Equal("hello plain", Tx(raw).GetMessageBody());
    }

    [Fact]
    public void GetMessageBody_HtmlOnly_FallsBackToHtml()
    {
        var raw = "From: a@b.c\r\nSubject: h\r\nContent-Type: text/html\r\n\r\n<b>x</b>";

        Assert.Equal("<b>x</b>", Tx(raw).GetMessageBody());
    }

    // ─── AddHeader (bug B2 pin) ──────────────────────────────────────────────

    [Fact]
    public void AddHeader_BeforeFirstParse_DuplicatesInParsedMessage_BugB2()
    {
        // BUG B2: AddHeader prepends to RawBody AND explicitly adds to ParsedMessage. When called before
        // the first parse, the explicit add happens on a message that was just parsed from the already-
        // modified RawBody — so ParsedMessage ends up with two copies while RawBody has one.
        var t = Tx(SimpleMessage);

        t.AddHeader("Received", "from 1.2.3.4 by test; now");

        Assert.Equal(1, CountOccurrences(t.RawBody, "Received:"));
        Assert.Equal(2, t.ParsedMessage.Headers.Count(h => h.Field == "Received"));
    }

    [Fact]
    public void AddHeader_AfterParse_AddsExactlyOneCopy()
    {
        var t = Tx(SimpleMessage);
        _ = t.ParsedMessage; // force the parse first

        t.AddHeader("X-Test", "value");

        Assert.Equal(1, CountOccurrences(t.RawBody, "X-Test:"));
        Assert.Single(t.ParsedMessage.Headers, h => h.Field == "X-Test");
    }

    [Fact]
    public void AddHeader_PrependsToRawBody()
    {
        var t = Tx(SimpleMessage);

        t.AddHeader("Received", "from 1.2.3.4 by test; now");

        Assert.StartsWith("Received: from 1.2.3.4 by test; now\r\n", t.RawBody);
    }

    // ─── Clone (bug B3/B4 pins) ──────────────────────────────────────────────

    [Fact]
    public void Clone_PreservesMetadata()
    {
        var t = Tx(SimpleMessage, from: "s@e.c", spf: ValidationResult.Softfail);
        t.AuthenticatedUser = "user";
        t.Encryption = ConnectionEncryption.StartTls;
        t.RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 12345);
        t.DeliverTo.Add("x@y.z");

        var c = (MailTransaction)t.Clone();

        Assert.Equal(t.From, c.From);
        Assert.Equal(t.FromDomain, c.FromDomain);
        Assert.Equal(t.RawBody, c.RawBody);
        Assert.Equal(ValidationResult.Softfail, c.SPFValidationResult);
        Assert.Equal("user", c.AuthenticatedUser);
        Assert.Equal(ConnectionEncryption.StartTls, c.Encryption);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 12345), c.RemoteEndPoint);
        Assert.Equal(t.DeliverTo, c.DeliverTo);
    }

    [Fact]
    public void Clone_SharedDeliverToListInstance_BugB4()
    {
        // BUG B4: Clone copies the DeliverTo list by reference — clone and original share one instance.
        var t = Tx(SimpleMessage);

        var c = (MailTransaction)t.Clone();

        Assert.Same(t.DeliverTo, c.DeliverTo);
    }

    [Fact]
    public void Clone_DropsDmarcValidationResult_BugB3()
    {
        // BUG B3: Clone does not copy DMARCValidationResult — the transaction handed to the delivery
        // handler always shows None, even when DMARC validation ran and produced a real result.
        var t = Tx(SimpleMessage);
        t.DMARCValidationResult = ValidationResult.Pass;

        var c = (MailTransaction)t.Clone();

        Assert.Equal(ValidationResult.None, c.DMARCValidationResult);
    }

    [Fact]
    public void Clone_SharesParsedMessageInstance()
    {
        // Documented behavior: the clone reuses the original's parsed MimeMessage.
        var t = Tx(SimpleMessage);

        var c = (MailTransaction)t.Clone();

        Assert.Same(t.ParsedMessage, c.ParsedMessage);
    }

    [Fact]
    public void Clone_ImplementsICloneable()
    {
        object clone = Tx(SimpleMessage).Clone();

        Assert.IsType<MailTransaction>(clone);
    }

    private static int CountOccurrences(string haystack, string needle) =>
        haystack.Split(needle, StringSplitOptions.None).Length - 1;
}
