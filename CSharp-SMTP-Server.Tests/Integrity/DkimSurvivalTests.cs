// The DKIM suite requires MimeKit's crypto path, which is unusable on net7.0: MimeKit 4.17 has no
// net7.0 build, so a net7.0 consumer resolves its netstandard2.1 build, whose Ed25519DigestSigner
// is incompatible with the BouncyCastle 2.6.2 MimeKit itself depends on, and DkimSigner throws
// TypeLoadException before signing anything. The shipped library never touches that code path — it
// only calls MimeMessage.Load — so this constrains the test fixture, not the server.
#if NET8_0_OR_GREATER
using System.Text;
using MimeKit;
using MimeKit.Cryptography;

namespace CSharp_SMTP_Server.Tests.Integrity;

/// <summary>
/// Origin authentication: a DKIM-signed message still verifies after passing through this server.
/// </summary>
/// <remarks>
/// <para>
/// <b>What these tests are for.</b> A DKIM signature is the only evidence this relay carries that a
/// message genuinely came from the customer it claims to. That matters in two directions:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Provenance of the archive.</b> For a journaled record to be worth anything in a dispute, it
/// has to be attributable. SPF authenticates the connecting IP, which for a relay is frequently just
/// the customer's own relay or whatever sits in front of it; DKIM authenticates the message, and
/// survives forwarding. If a signature that arrived valid still verifies after delivery, the archive
/// holds proof of origin rather than an assertion of it.
/// </description></item>
/// <item><description>
/// <b>Detecting impersonation of a customer.</b> Someone relaying through this server while claiming
/// to be a customer cannot produce a signature that verifies against that customer's published
/// public key. A surviving signature is therefore a positive check, not merely an absence of
/// tampering.
/// </description></item>
/// </list>
/// <para>
/// <b>What a passing signature does and does not say.</b> It is a statement about origin, not about
/// byte preservation, and the two are independent rather than ranked. DKIM cannot stand in for
/// <see cref="ByteIntegrityTests"/> because its coverage is deliberately lossy:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Both body canonicalizations are many-to-one. <c>relaxed</c> strips trailing whitespace and
/// collapses runs of it; <c>simple</c> — despite being the strict one — still reduces trailing empty
/// lines to a single CRLF. A message can lose bytes and still verify.
/// </description></item>
/// <item><description>
/// Only headers named in the signature's <c>h=</c> tag are covered. Anything else can be altered,
/// removed or added without affecting the result — asserted directly by
/// <see cref="AlteredUnsignedHeader_StillVerifies_ShowingTheLimitOfThisOracle"/>.
/// </description></item>
/// <item><description>
/// The signer, the parser and the verifier here are all MimeKit, and the server parses with MimeKit
/// too. A defect in a shared component can cancel itself out across a sign/verify round trip in a
/// way it would not against an independent verifier.
/// </description></item>
/// </list>
/// <para>
/// So the two suites answer different questions — <em>who sent this</em> here, <em>did we alter
/// it</em> next door — and both are needed. Byte preservation is also what makes this suite possible
/// at all: any rewriting of the transmitted octets invalidates the signature and destroys the origin
/// proof, which is why bare-LF normalization was removed in favour of refusing such messages (see
/// <see cref="LineEndingConformanceTests"/>).
/// </para>
/// <para>
/// <b>Not covered here: re-verifying later.</b> These tests verify at delivery time, with the key in
/// memory. An archive that must re-verify a message months afterwards needs the signing key as it
/// was at receipt — DKIM selectors are rotated and retired, so a later DNS lookup may return a
/// different key or none. Capturing the public key or the verification result at receipt is a
/// deployment concern this suite does not address, and does not prove.
/// </para>
/// </remarks>
[Trait("Category", "Integrity")]
public sealed class DkimSurvivalTests
{
    private static readonly string[] SignedHeaders = { "From", "To", "Subject", "Date" };

    /// <summary>
    /// Builds a message, signs it, and returns the exact signed octets.
    /// </summary>
    /// <remarks>
    /// The message is serialized ONCE, after signing, and those bytes are both what is transmitted
    /// and what the expectation is built from. Serializing a second time to compare against would
    /// test MimeKit's serializer rather than the server.
    /// </remarks>
    private static byte[] SignedMessage(string id, MimeEntity body,
        DkimCanonicalizationAlgorithm headerAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
        DkimCanonicalizationAlgorithm bodyAlgorithm = DkimCanonicalizationAlgorithm.Relaxed)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        message.To.Add(new MailboxAddress("Archive", "archive@example.org"));
        message.Subject = $"dkim survival {id}";
        message.Date = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        message.Headers.Add(RawMessage.IdHeader, id);
        message.Body = body;

        DkimTestKey.Signer(headerAlgorithm, bodyAlgorithm).Sign(message, SignedHeaders);

        using var stream = new MemoryStream();

        // DOS newlines: SMTP is a CRLF protocol, and writing LF here would mean the harness, not the
        // server, was the thing that produced non-conforming line endings.
        message.WriteTo(new FormatOptions { NewLineFormat = NewLineFormat.Dos }, stream);

        return stream.ToArray();
    }

    /// <summary>Sends signed octets through a real listener and returns the delivered message.</summary>
    private static async Task<byte[]> RoundTripAsync(byte[] signed)
    {
        var delivery = new ByteRecordingDelivery();
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port, delivery: delivery);
        server.Start();

        await using (var session = await SmtpSession.ConnectAsync(port))
        {
            Assert.StartsWith("220 ", await session.ReadLineAsync());
            await session.Send("EHLO dkim.client");
            await session.ReadResponseAsync();

            await session.Send("MAIL FROM:<sender@example.com>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("RCPT TO:<archive@example.org>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("DATA");
            Assert.StartsWith("354", await session.ReadLineAsync());

            Assert.StartsWith("250", await RawMessage.SendDataAsync(session, signed));
        }

        return delivery.Single();
    }

    /// <summary>Verifies the first DKIM signature on a delivered message.</summary>
    private static async Task<bool> VerifyAsync(byte[] delivered)
    {
        using var stream = new MemoryStream(delivered);
        var message = await MimeMessage.LoadAsync(stream);
        var index = message.Headers.IndexOf(HeaderId.DkimSignature);

        Assert.True(index >= 0, "delivered message carries no DKIM-Signature header");

        return await DkimTestKey.Verifier().VerifyAsync(message, message.Headers[index]);
    }

    private static TextPart Text(string content) => new("plain") { Text = content };

    // ── the survival contract ─────────────────────────────────────────────────────────────────

    /// <summary>A signed message verifies after passing through the server.</summary>
    [Fact]
    public async Task SignedMessage_StillVerifies_AfterDelivery()
    {
        var signed = SignedMessage("baseline", Text("A short signed body.\r\nSecond line.\r\n"));

        Assert.True(await VerifyAsync(await RoundTripAsync(signed)));
    }

    /// <summary>
    /// The server's prepended headers do not break the signature.
    /// </summary>
    /// <remarks>
    /// <c>Received:</c> and <c>Authentication-Results:</c> are added above the signed headers, which
    /// is what a relay is supposed to do — a verifier ignores headers not named in <c>h=</c>. The
    /// test asserts the prepend actually happened, so it cannot pass vacuously on a server that
    /// added nothing.
    /// </remarks>
    [Fact]
    public async Task PrependedReceivedHeader_DoesNotBreakTheSignature()
    {
        var signed = SignedMessage("prepend", Text("Body under a prepended header.\r\n"));
        var delivered = await RoundTripAsync(signed);

        Assert.Contains("Received:", Encoding.ASCII.GetString(delivered, 0, Math.Min(512, delivered.Length)));
        Assert.True(await VerifyAsync(delivered));
    }

    /// <summary>
    /// Both canonicalization pairs survive delivery.
    /// </summary>
    /// <remarks>
    /// <c>simple</c> body canonicalization is the stricter of the two — it normalizes only trailing
    /// empty lines, where <c>relaxed</c> also strips and collapses whitespace — so it is the more
    /// sensitive of the two to anything the server does to the body. Neither is byte-exact, which is
    /// why these tests do not carry the integrity claim.
    /// </remarks>
    [Theory]
    [InlineData(DkimCanonicalizationAlgorithm.Relaxed, DkimCanonicalizationAlgorithm.Relaxed)]
    [InlineData(DkimCanonicalizationAlgorithm.Relaxed, DkimCanonicalizationAlgorithm.Simple)]
    [InlineData(DkimCanonicalizationAlgorithm.Simple, DkimCanonicalizationAlgorithm.Simple)]
    public async Task Canonicalizations_SurviveDelivery(
        DkimCanonicalizationAlgorithm header, DkimCanonicalizationAlgorithm body)
    {
        var signed = SignedMessage($"canon-{header}-{body}",
            Text("Canonicalization body.\r\nWith two lines.\r\n"), header, body);

        Assert.True(await VerifyAsync(await RoundTripAsync(signed)));
    }

    /// <summary>
    /// A signed body containing leading-dot lines survives SMTP transparency.
    /// </summary>
    /// <remarks>
    /// The case that ties DKIM to a real defect in this codebase's history: before dot-unstuffing was
    /// fixed, a stuffed line was stored with its transport dot intact, so the archived body differed
    /// from the signed one and the signature failed. This is that regression expressed as the
    /// consequence a consumer would actually observe.
    /// </remarks>
    [Fact]
    public async Task SignedBodyWithLeadingDots_StillVerifies()
    {
        var signed = SignedMessage("dots",
            Text(".dot at line start\r\n..two dots\r\nplain line\r\n"));

        Assert.True(await VerifyAsync(await RoundTripAsync(signed)));
    }

    /// <summary>
    /// A signed body carrying non-UTF-8 octets survives.
    /// </summary>
    /// <remarks>
    /// Latin-1 content signed as such: the old transcoding DATA path turned every byte that was not
    /// valid UTF-8 into U+FFFD, which changed the body hash and invalidated the signature. Uses an
    /// explicit charset so the bytes on the wire really are Latin-1 rather than UTF-8 that happens to
    /// look like it.
    /// </remarks>
    [Fact]
    public async Task SignedLatin1Body_StillVerifies()
    {
        var body = new TextPart("plain");
        body.SetText(Encoding.Latin1, "Café, naïve, Grüße — Latin-1 bytes.\r\n");
        body.ContentTransferEncoding = ContentEncoding.Binary;

        Assert.True(await VerifyAsync(await RoundTripAsync(SignedMessage("latin1", body))));
    }

    // ── negative controls ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verification fails when a body byte is altered after signing.
    /// </summary>
    /// <remarks>
    /// Establishes that the verifier can fail at all. Without it, every assertion above is equally
    /// consistent with a verifier that returns true unconditionally — the single most important test
    /// in this file.
    /// </remarks>
    [Fact]
    public async Task AlteredBodyByte_FailsVerification()
    {
        var signed = SignedMessage("tamper-body", Text("The body that will be altered.\r\n"));
        var delivered = await RoundTripAsync(signed);

        Assert.True(await VerifyAsync(delivered), "the unaltered message should verify first");

        var text = Encoding.ASCII.GetString(delivered).Replace("will be altered", "was not altered!");

        Assert.False(await VerifyAsync(Encoding.ASCII.GetBytes(text)));
    }

    /// <summary>
    /// Verification fails when a signed header is altered after signing.
    /// </summary>
    /// <remarks>
    /// <c>Subject</c> is in <c>h=</c>, so changing it must break the signature. Complements the body
    /// control: body-hash and header-hash are separate mechanisms in DKIM and a test suite that only
    /// mutates the body leaves the header half unproven.
    /// </remarks>
    [Fact]
    public async Task AlteredSignedHeader_FailsVerification()
    {
        var signed = SignedMessage("tamper-header", Text("Body stays the same.\r\n"));
        var delivered = await RoundTripAsync(signed);

        Assert.True(await VerifyAsync(delivered), "the unaltered message should verify first");

        var text = Encoding.ASCII.GetString(delivered)
            .Replace("Subject: dkim survival tamper-header", "Subject: something else entirely");

        Assert.False(await VerifyAsync(Encoding.ASCII.GetBytes(text)));
    }

    /// <summary>
    /// An unsigned header can be altered freely without affecting verification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The test that makes the limits of this suite concrete rather than merely documented. It
    /// asserts a true fact about DKIM — coverage stops at <c>h=</c> — and in doing so demonstrates
    /// why a passing signature authenticates the signed identity rather than vouching for every byte
    /// of the message carrying it.
    /// </para>
    /// <para>
    /// This is exactly the gap <see cref="ByteIntegrityTests"/> closes, and the reason both suites
    /// exist rather than one standing in for the other.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AlteredUnsignedHeader_StillVerifies_ShowingTheLimitOfThisOracle()
    {
        var signed = SignedMessage("unsigned-header", Text("Body stays the same.\r\n"));
        var delivered = await RoundTripAsync(signed);

        // X-Integrity-Id is deliberately absent from SignedHeaders, so DKIM says nothing about it.
        var text = Encoding.ASCII.GetString(delivered)
            .Replace($"{RawMessage.IdHeader}: unsigned-header", $"{RawMessage.IdHeader}: rewritten");

        Assert.Contains("rewritten", text);
        Assert.True(await VerifyAsync(Encoding.ASCII.GetBytes(text)));
    }
}
#endif
