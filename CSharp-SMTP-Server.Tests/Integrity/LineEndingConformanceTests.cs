using System.Text;

namespace CSharp_SMTP_Server.Tests.Integrity;

/// <summary>
/// Line-ending conformance: a bare LF is refused, closing the SMTP-smuggling class.
/// </summary>
/// <remarks>
/// <para><b>What the RFC requires.</b> RFC 5321 §2.3.8 permits CR and LF only together, as a line
/// terminator. §4.1.1.4 is considerably stronger, and addresses this exact implementation choice:</para>
/// <para>
/// <i>"The custom of accepting lines ending only in &lt;LF&gt;, as a concession to non-conforming
/// behavior on the part of some UNIX systems, has proven to cause more interoperability problems
/// than it solves, and SMTP server systems MUST NOT do this, even in the name of improved
/// robustness. In particular, the sequence &lt;LF&gt;.&lt;LF&gt; (bare line feeds, without carriage
/// returns) MUST NOT be treated as equivalent to &lt;CRLF&gt;.&lt;CRLF&gt; as the end of mail data
/// indication."</i>
/// </para>
/// <para>
/// <b>Why it matters.</b> That paragraph acquired a name in December 2023: SMTP smuggling. When two
/// hops disagree about what ends a message, an attacker submits one message that the first hop reads
/// as one and the second reads as two. The smuggled second message inherits the first one's
/// connection — its authentication, its SPF result, its DMARC pass. For a journaling relay the
/// consequence is worse than ordinary spoofing: the forged message is archived as an authenticated
/// record.
/// </para>
/// <para>
/// <b>Why rejecting costs nothing here.</b> Exchange Online refuses to send bare-LF mail at all,
/// returning <c>SMTPSEND.BareLinefeedsAreIllegal</c>; Microsoft used to strip bare LFs for
/// compatibility with older servers and deliberately stopped, precisely because stripping them
/// invalidates DKIM signatures. So an Office 365 sender cannot produce a message this rule refuses,
/// and the permissive alternative would have been strictly worse for this deployment: silently
/// rewriting bare LF to CRLF changes the octets the sender signed and destroys the origin proof that
/// <see cref="DkimSurvivalTests"/> exists to establish.
/// </para>
/// <para>
/// These were characterization tests before the fix, asserting the defect so it stayed visible. They
/// now assert the contract.
/// </para>
/// </remarks>
[Trait("Category", "Integrity")]
public sealed class LineEndingConformanceTests
{
    /// <summary>Opens a session and advances it to the DATA state, ready for message octets.</summary>
    private static async Task<SmtpSession> OpenDataAsync(ushort port)
    {
        var session = await SmtpSession.ConnectAsync(port);

        Assert.StartsWith("220 ", await session.ReadLineAsync());
        await session.Send("EHLO conformance.client");
        await session.ReadResponseAsync();

        await session.Send("MAIL FROM:<sender@example.com>");
        Assert.StartsWith("250", await session.ReadLineAsync());
        await session.Send("RCPT TO:<archive@example.org>");
        Assert.StartsWith("250", await session.ReadLineAsync());
        await session.Send("DATA");
        Assert.StartsWith("354", await session.ReadLineAsync());

        return session;
    }

    /// <summary>
    /// A body sent with bare-LF line endings is refused, and nothing is delivered.
    /// </summary>
    /// <remarks>
    /// The server previously accepted this and rewrote the endings to CRLF, so the archive held a
    /// repaired message rather than the transmitted one. Refusing keeps the invariant every other
    /// test in this directory depends on: what is delivered is what arrived.
    /// </remarks>
    [Fact]
    public async Task BareLfBody_IsRefused()
    {
        var delivery = new ByteRecordingDelivery();
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port, delivery: delivery);
        server.Start();

        await using (var session = await OpenDataAsync(port))
        {
            var message = Encoding.ASCII.GetBytes(
                $"{RawMessage.IdHeader}: bare-lf\nSubject: bare lf\n" +
                "From: Sender <sender@example.com>\nTo: Archive <archive@example.org>\n" +
                "\nfirst line\nsecond line\n");

            await session.SendRaw(message);
            await session.SendRaw(Encoding.ASCII.GetBytes(".\r\n"));

            var response = await session.ReadLineAsync();

            Assert.StartsWith("554", response);
            Assert.Contains("bare linefeed", response, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(delivery.Delivered);
    }

    /// <summary>
    /// A bare-LF dot does not end DATA: capture continues, and the message is refused at the
    /// conforming terminator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The SMTP-smuggling case RFC 5321 §4.1.1.4 names, asserted at the level where it is actually
    /// closed. Refusing the message is not enough on its own — what makes the attack work is
    /// <em>leaving DATA capture</em>, because everything the client sent after the bare-LF dot is then
    /// parsed as commands on an already-authenticated connection. Staying in capture keeps those
    /// octets inert: body bytes are stored, never executed.
    /// </para>
    /// <para>
    /// So no reply follows the bare-LF dot; the server is still reading the message. The refusal
    /// arrives at the CRLF-framed terminator, and the content after the bare-LF dot is body text of
    /// the refused message rather than a message of its own. See
    /// <see cref="PipelinedTransactionAfterBareLfDot_IsNotDelivered"/> for the attack itself.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task BareLfDot_DoesNotEndData_MessageRefusedAtConformingTerminator()
    {
        var delivery = new ByteRecordingDelivery();
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port, delivery: delivery);
        server.Start();

        await using (var session = await OpenDataAsync(port))
        {
            // Headers and the visible body are correctly CRLF-framed. Only the terminator is bare-LF,
            // which is the shape a smuggling attempt takes: conforming to the hop that must accept it,
            // ambiguous to the hop that must forward it.
            await session.SendRaw(Encoding.ASCII.GetBytes(
                $"{RawMessage.IdHeader}: smuggle\r\nSubject: smuggling probe\r\n" +
                "From: Sender <sender@example.com>\r\nTo: Archive <archive@example.org>\r\n" +
                "\r\nvisible body\r\n" +
                "\n.\n" +
                "SMUGGLED-CONTENT-MUST-NOT-BE-DELIVERED\r\n"));

            // The bare-LF dot did not terminate anything, so a conforming terminator is still needed.
            await session.SendRaw(Encoding.ASCII.GetBytes(".\r\n"));

            Assert.StartsWith("554", await session.ReadLineAsync());
        }

        Assert.Empty(delivery.Delivered);
    }

    /// <summary>
    /// A bare LF anywhere in the message refuses it, even when every other line is CRLF-framed.
    /// </summary>
    /// <remarks>
    /// The flag is latched for the whole message rather than checked only at the terminator, so a
    /// single non-conforming line mid-body is enough. Without this, a message could carry bare-LF
    /// content and still be accepted as long as it ended correctly.
    /// </remarks>
    [Fact]
    public async Task SingleBareLfMidBody_RefusesTheWholeMessage()
    {
        var delivery = new ByteRecordingDelivery();
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port, delivery: delivery);
        server.Start();

        await using (var session = await OpenDataAsync(port))
        {
            var message = Encoding.ASCII.GetBytes(
                $"{RawMessage.IdHeader}: mid-body\r\nSubject: one bad line\r\n" +
                "From: Sender <sender@example.com>\r\nTo: Archive <archive@example.org>\r\n" +
                "\r\nproper line\r\nbad line ends here\nproper again\r\n");

            await session.SendRaw(message);
            await session.SendRaw(Encoding.ASCII.GetBytes(".\r\n"));

            Assert.StartsWith("554", await session.ReadLineAsync());
        }

        Assert.Empty(delivery.Delivered);
    }

    /// <summary>
    /// A fully CRLF-framed message is unaffected, and a stuffed dot inside it is still body content.
    /// </summary>
    /// <remarks>
    /// The regression guard on the fix: it must refuse bare LF without refusing conforming mail, and
    /// without disturbing terminator detection. A rule that rejected too broadly would fail here.
    /// </remarks>
    [Fact]
    public async Task ConformingCrlfMessage_IsAccepted_WithStuffedDotIntact()
    {
        const string id = "stuffed-dot";
        var delivery = new ByteRecordingDelivery();
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port, delivery: delivery);
        server.Start();

        await using (var session = await OpenDataAsync(port))
        {
            var message = Encoding.ASCII.GetBytes(
                $"{RawMessage.IdHeader}: {id}\r\nSubject: stuffed dot\r\n" +
                "From: Sender <sender@example.com>\r\nTo: Archive <archive@example.org>\r\n" +
                "\r\nbefore\r\n.\r\nafter\r\n");

            // Stuffing turns the lone "." line into ".." on the wire; it must arrive as "." again.
            await session.SendRaw(RawMessage.Stuff(message));

            Assert.StartsWith("250", await session.ReadLineAsync());
        }

        var delivered = RawMessage.ExtractFromId(delivery.Single(), id);

        Assert.Contains("before\r\n.\r\nafter", Encoding.ASCII.GetString(delivered));
    }

    /// <summary>
    /// LF-separated command lines are refused and the connection is dropped, not executed in sequence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The command-path counterpart to the DATA rule, closing the same parser differential. A
    /// CRLF-strict gateway or policy layer in front of this server treats
    /// <c>"MAIL FROM:&lt;a&gt;\nRCPT TO:&lt;b&gt;\nDATA\n"</c> as a single malformed line; a server
    /// that frames on any LF would execute three commands. Whatever such a front end inspected and
    /// allowed would not be what this server ran.
    /// </para>
    /// <para>
    /// The connection is dropped rather than the line merely refused: bytes already buffered behind it
    /// were framed by the same client and must not execute either.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task LfSeparatedCommands_AreRefused_AndConnectionDropped()
    {
        var delivery = new ByteRecordingDelivery();
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port, delivery: delivery);
        server.Start();

        await using (var session = await SmtpSession.ConnectAsync(port))
        {
            Assert.StartsWith("220 ", await session.ReadLineAsync());
            await session.Send("EHLO conformance.client");
            await session.ReadResponseAsync();

            // A whole transaction framed with bare LFs, in one write.
            await session.SendRaw(Encoding.ASCII.GetBytes(
                "MAIL FROM:<attacker@evil.example>\n" +
                "RCPT TO:<victim@example.org>\n" +
                "DATA\n"));

            Assert.StartsWith("500", await session.ReadLineAsync());

            // The connection is closed, so nothing buffered behind the bad line ran.
            Assert.Null(await session.ReadLineAsync());
        }

        Assert.Empty(delivery.Delivered);
    }

    /// <summary>
    /// A complete transaction pipelined after <c>&lt;LF&gt;.&lt;LF&gt;</c> must not be delivered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The actual smuggling attack, and the test that matters most in this file. Refusing the first
    /// message is not sufficient on its own: if the bare-LF dot is allowed to end DATA capture, every
    /// octet the attacker sent after it is parsed as SMTP commands on a connection the upstream hop
    /// already authenticated. A syntactically valid <c>MAIL FROM</c>/<c>RCPT TO</c>/<c>DATA</c>
    /// sequence there becomes a second, injected message that inherits this connection's trust.
    /// </para>
    /// <para>
    /// An earlier version of this suite sent only an invalid trailing line after the bare-LF dot,
    /// which the command parser rejected — so it passed against a server that was still exploitable.
    /// This sends a complete transaction, which is what an attacker would send.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task PipelinedTransactionAfterBareLfDot_IsNotDelivered()
    {
        var delivery = new ByteRecordingDelivery();
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port, delivery: delivery);
        server.Start();

        await using (var session = await OpenDataAsync(port))
        {
            // One write: a visible message, a bare-LF dot, then a complete second transaction. If the
            // bare-LF dot ends DATA, everything after it is read as commands.
            await session.SendRaw(Encoding.ASCII.GetBytes(
                "Subject: visible\r\n\r\nvisible body\r\n" +
                "\n.\n" +
                "MAIL FROM:<attacker@evil.example>\r\n" +
                "RCPT TO:<victim@example.org>\r\n" +
                "DATA\r\n" +
                $"{RawMessage.IdHeader}: smuggled\r\nSubject: SMUGGLED\r\n" +
                "From: Spoofed <ceo@example.com>\r\nTo: Victim <victim@example.org>\r\n" +
                "\r\nSMUGGLED-BODY-MUST-NOT-BE-DELIVERED\r\n" +
                ".\r\n"));

            // Drain whatever the server chooses to say; the assertion is about deliveries, not codes.
            try
            {
                for (var i = 0; i < 8; i++)
                {
                    var line = await session.ReadLineAsync();
                    if (line == null) break;
                }
            }
            catch (TimeoutException)
            {
                // No further output is a perfectly good outcome here.
            }
        }

        var smuggled = delivery.Delivered
            .Select(m => Encoding.ASCII.GetString(m))
            .Where(m => m.Contains("SMUGGLED-BODY-MUST-NOT-BE-DELIVERED"))
            .ToArray();

        Assert.True(smuggled.Length == 0,
            $"a message pipelined after <LF>.<LF> was delivered — SMTP smuggling is not closed. " +
            $"{delivery.Delivered.Count} message(s) reached delivery.");
    }

    /// <summary>
    /// A refused bare-LF message does not poison the next transaction on the same connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bare-LF flag is latched for a whole message and cleared only when DATA is re-entered. That
    /// is correct because DATA is the sole entry point into capture, but it is worth proving rather
    /// than reasoning about: a latch that outlived its transaction would refuse every subsequent
    /// message on a connection that once saw a bare LF, turning one non-conforming message into a
    /// dead session.
    /// </para>
    /// <para>
    /// Also confirms the session stays synchronized across the refusal — the client can begin a new
    /// transaction immediately, without an intervening RSET.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AfterBareLfRefusal_TheNextMessageOnTheSameConnection_IsAccepted()
    {
        const string id = "recovered";
        var delivery = new ByteRecordingDelivery();
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port, delivery: delivery);
        server.Start();

        await using (var session = await OpenDataAsync(port))
        {
            await session.SendRaw(Encoding.ASCII.GetBytes(
                "Subject: refused\r\n\r\nbare\nlf\r\n"));
            await session.SendRaw(Encoding.ASCII.GetBytes(".\r\n"));

            Assert.StartsWith("554", await session.ReadLineAsync());

            // A fresh transaction on the same connection, with no RSET in between.
            await session.Send("MAIL FROM:<sender@example.com>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("RCPT TO:<archive@example.org>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("DATA");
            Assert.StartsWith("354", await session.ReadLineAsync());

            var message = Encoding.ASCII.GetBytes(
                $"{RawMessage.IdHeader}: {id}\r\nSubject: accepted\r\n" +
                "From: Sender <sender@example.com>\r\nTo: Archive <archive@example.org>\r\n" +
                "\r\nall CRLF here\r\n");

            Assert.StartsWith("250", await RawMessage.SendDataAsync(session, message));
        }

        // Exactly one message was delivered: the second. The refused one never reached delivery.
        var delivered = RawMessage.ExtractFromId(delivery.Single(), id);

        Assert.Contains("all CRLF here", Encoding.ASCII.GetString(delivered));
    }

    /// <summary>
    /// An RSET between a refused message and the next one is likewise clean.
    /// </summary>
    /// <remarks>
    /// The other route back to a usable session. RSET discards the transaction without passing
    /// through DATA, so this covers the path where the flag is not reset by re-entering capture.
    /// </remarks>
    [Fact]
    public async Task AfterBareLfRefusal_RsetThenNewMessage_IsAccepted()
    {
        const string id = "after-rset";
        var delivery = new ByteRecordingDelivery();
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port, delivery: delivery);
        server.Start();

        await using (var session = await OpenDataAsync(port))
        {
            await session.SendRaw(Encoding.ASCII.GetBytes("Subject: refused\r\n\r\nbare\nlf\r\n"));
            await session.SendRaw(Encoding.ASCII.GetBytes(".\r\n"));
            Assert.StartsWith("554", await session.ReadLineAsync());

            await session.Send("RSET");
            Assert.StartsWith("250", await session.ReadLineAsync());

            await session.Send("MAIL FROM:<sender@example.com>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("RCPT TO:<archive@example.org>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("DATA");
            Assert.StartsWith("354", await session.ReadLineAsync());

            var message = Encoding.ASCII.GetBytes(
                $"{RawMessage.IdHeader}: {id}\r\nSubject: accepted\r\n" +
                "From: Sender <sender@example.com>\r\nTo: Archive <archive@example.org>\r\n" +
                "\r\nclean body\r\n");

            Assert.StartsWith("250", await RawMessage.SendDataAsync(session, message));
        }

        Assert.Contains("clean body",
            Encoding.ASCII.GetString(RawMessage.ExtractFromId(delivery.Single(), id)));
    }
}
