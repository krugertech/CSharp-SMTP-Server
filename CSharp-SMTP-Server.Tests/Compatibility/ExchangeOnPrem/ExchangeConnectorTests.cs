using System.Net;
using System.Text;
using CSharp_SMTP_Server.Networking;
using CSharp_SMTP_Server.Protocol.Responses;

namespace CSharp_SMTP_Server.Tests.Compatibility.ExchangeOnPrem;

/// <summary>
/// Compatibility contract for on-premises Exchange Server send connectors delivering into this
/// server (Exchange 2016/2019, internal relay).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this suite asserts, and what it deliberately does not.</b> Every test here drives our own
/// loopback listener. None of them proves that a real Exchange organisation interoperates — see
/// <see cref="PlatformContract"/> for why that distinction is load-bearing. What they pin is the half
/// of the contract we control: the commands an Exchange connector may send that we do <i>not</i>
/// advertise must be refused in a way that leaves the session usable, and the commands we do
/// advertise must behave exactly as advertised.
/// </para>
/// <para>
/// <b>Why refusal-without-desync is the central theme.</b> This server advertises a deliberately
/// narrow extension set: <c>8BITMIME</c>, <c>SIZE</c>, and conditionally <c>AUTH</c> and
/// <c>STARTTLS</c>. It offers neither <c>CHUNKING</c> nor <c>PIPELINING</c> nor <c>XEXCH50</c>. A
/// conforming client will therefore never use them. But connectors are misconfigured, intermediaries
/// rewrite traffic, and legacy Exchange deployments carry proprietary habits — and the dangerous
/// failure is not the refusal itself, it is a refusal that leaves the command stream misaligned. A
/// desynchronised session can attach one message's body to another message's envelope, which
/// misdelivers mail rather than merely dropping it.
/// </para>
/// <para>
/// These distinctions differ from the Office 365 suite, which encodes a <i>journaling</i> deployment
/// where refusing a message destroys a compliance record. On-premises internal relay has no such
/// asymmetry: normal limits apply, and a 5xx is the sending organisation's problem to see and fix.
/// </para>
/// </remarks>
[Trait(PlatformContract.Name, PlatformContract.ExchangeOnPrem)]
public sealed class ExchangeConnectorTests
{
    /// <summary>
    /// A representative Exchange send-connector EHLO name. Exchange identifies itself by its
    /// fully-qualified server name (or the connector's configured FQDN), not by a public MX name.
    /// </summary>
    private const string ExchangeEhloName = "EXCH01.corp.example.com";

    /// <summary>Starts a default-profile server on a free loopback port.</summary>
    private static (SMTPServer Server, ushort Port) Start(
        Interfaces.IMailDelivery? delivery = null, Interfaces.IMailFilter? filter = null)
    {
        var port = TestPorts.Allocate();
        var server = TestServers.Build(port, delivery: delivery, filter: filter);
        server.Start();
        return (server, port);
    }

    /// <summary>Connects, reads the greeting, sends EHLO and returns the session with the EHLO lines.</summary>
    private static async Task<(SmtpSession Session, IReadOnlyList<string> Ehlo)> OpenAsync(ushort port)
    {
        var session = await SmtpSession.ConnectAsync(port);
        var greeting = await session.ReadLineAsync();
        Assert.StartsWith("220", greeting);
        await session.Send($"EHLO {ExchangeEhloName}");
        return (session, await session.ReadResponseAsync());
    }

    /// <summary>Runs one complete, ordinary transaction and asserts it is accepted.</summary>
    /// <remarks>
    /// Used after every hostile or unsupported command to prove the session is still usable. A test
    /// that only asserts the error code would pass even if the command stream had been left
    /// misaligned, which is the failure that actually misdelivers mail.
    /// </remarks>
    private static async Task AssertTransactionSucceedsAsync(SmtpSession session, string subject)
    {
        await session.Send("MAIL FROM:<sender@corp.example.com>");
        Assert.StartsWith("250", await session.ReadLineAsync());

        await session.Send("RCPT TO:<recipient@test.local>");
        Assert.StartsWith("250", await session.ReadLineAsync());

        await session.Send("DATA");
        Assert.StartsWith("354", await session.ReadLineAsync());

        await session.Send($"Subject: {subject}");
        await session.Send("");
        await session.Send("body");
        await session.Send(".");
        Assert.StartsWith("250", await session.ReadLineAsync());
    }

    // ── proprietary and unadvertised commands ─────────────────────────────────────────────────

    /// <summary>
    /// <c>XEXCH50</c> is refused with 502 and does not desynchronise the session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Source: <see cref="PlatformContract.Provenance.XExch50"/>. Exchange uses this proprietary
    /// command to carry MAPI properties between Exchange servers, and offers it only to a peer that
    /// advertised it. We never advertise it, so this test pins <i>our</i> refusal rather than claiming
    /// Exchange will send it.
    /// </para>
    /// <para>
    /// The critical assertion is the second half. <c>XEXCH50</c> is followed by a binary blob whose
    /// length is announced in the command line, so a server that answered "ready" would then read
    /// that blob as commands. Refusing with 502 and reading nothing further is correct precisely
    /// because it keeps the next line a command.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task XExch50_IsRefused_AndSessionStaysUsable()
    {
        var delivery = new RecordingDelivery();
        var (server, port) = Start(delivery);
        try
        {
            var (session, _) = await OpenAsync(port);
            await using var _s = session;

            await session.Send("XEXCH50 1024 2");
            Assert.StartsWith("502", await session.ReadLineAsync());

            await AssertTransactionSucceedsAsync(session, "after-xexch50");
        }
        finally
        {
            server.Dispose();
        }

        Assert.Single(delivery.Delivered);
    }

    /// <summary>
    /// EHLO does not advertise <c>CHUNKING</c>, and a <c>BDAT</c> issued anyway is refused with 502
    /// without consuming the chunk that would have followed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Source: <see cref="PlatformContract.Provenance.Chunking"/>. RFC 3030 §2 forbids a client from
    /// issuing <c>BDAT</c> unless the server advertised <c>CHUNKING</c>, and Exchange honours that by
    /// falling back to <c>DATA</c>. This test asserts both halves of our side: the absence of the
    /// advertisement, and safe refusal if a connector sends it regardless.
    /// </para>
    /// <para>
    /// Asserting the absence matters as much as the refusal. If someone later adds a <c>CHUNKING</c>
    /// advertisement without implementing <c>BDAT</c>, every Exchange connector would immediately
    /// switch to it and every message would fail — this test fails first.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Bdat_IsNotAdvertised_AndIsRefusedWithoutConsumingTheChunk()
    {
        var delivery = new RecordingDelivery();
        var (server, port) = Start(delivery);
        try
        {
            var (session, ehlo) = await OpenAsync(port);
            await using var _s = session;

            Assert.DoesNotContain(ehlo, l => l.Contains("CHUNKING", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(ehlo, l => l.Contains("BINARYMIME", StringComparison.OrdinalIgnoreCase));

            await session.Send("BDAT 10 LAST");
            Assert.StartsWith("502", await session.ReadLineAsync());

            // The chunk was never consumed, so the stream is still command-aligned.
            await AssertTransactionSucceedsAsync(session, "after-bdat");
        }
        finally
        {
            server.Dispose();
        }

        Assert.Single(delivery.Delivered);
    }

    /// <summary>
    /// The EHLO response advertises exactly the extension set this server implements.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the anchor for every "we do not support X, so refusal is correct" claim in this suite
    /// and in <see cref="PlatformContract"/>. Advertising an extension is a promise; the refusal tests
    /// above are only correct while the promise is absent.
    /// </para>
    /// <para>
    /// <c>ENHANCEDSTATUSCODES</c> is deliberately included in the not-advertised list even though the
    /// server does emit enhanced status codes in its replies. Emitting them unbidden is harmless;
    /// advertising them is a commitment to emit them on <i>every</i> reply, which is not verified.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Ehlo_AdvertisesOnlyTheImplementedExtensionSet()
    {
        var (server, port) = Start();
        try
        {
            var (session, ehlo) = await OpenAsync(port);
            await using var _s = session;

            Assert.Contains(ehlo, l => l.Contains("8BITMIME", StringComparison.Ordinal));
            Assert.Contains(ehlo, l => l.Contains("SIZE", StringComparison.Ordinal));

            foreach (var unsupported in new[]
                     { "PIPELINING", "CHUNKING", "BINARYMIME", "SMTPUTF8", "DSN", "ENHANCEDSTATUSCODES", "XEXCH50" })
            {
                Assert.DoesNotContain(ehlo,
                    l => l.Contains(unsupported, StringComparison.OrdinalIgnoreCase));
            }

            // No certificate and no auth handler are configured here, so neither may be offered.
            Assert.DoesNotContain(ehlo, l => l.Contains("STARTTLS", StringComparison.Ordinal));
            Assert.DoesNotContain(ehlo, l => l.Contains("AUTH", StringComparison.Ordinal));
        }
        finally
        {
            server.Dispose();
        }
    }

    // ── command stream alignment ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A pipelined command batch sent without waiting for intermediate replies is answered in order,
    /// even though <c>PIPELINING</c> is not advertised.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Source: <see cref="PlatformContract.Provenance.Pipelining"/>. RFC 2920 §3.1 says a client must
    /// not do this unbidden, so a conforming Exchange connector will not. The behaviour is pinned
    /// anyway because the cost of being wrong is asymmetric: a proxy, a load balancer, or a
    /// misconfigured connector can produce a pipelined batch, and a server that mishandles it can
    /// pair one envelope with another message's body.
    /// </para>
    /// <para>
    /// <b>This test records a capability the server does not advertise.</b> Reading commands from a
    /// buffered stream means pipelining happens to work. That is a fact about the current
    /// implementation, not a promise — it must not be read as a reason to advertise
    /// <c>PIPELINING</c>, which additionally requires bounded reply buffering and specific behaviour
    /// on the group boundaries in RFC 2920 §3.2. If this test ever fails, the correct response is to
    /// investigate a desync bug, not to relax the assertion.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task PipelinedBatch_IsAnsweredInOrder_WithoutDesync()
    {
        var delivery = new RecordingDelivery();
        var (server, port) = Start(delivery);
        try
        {
            var (session, _) = await OpenAsync(port);
            await using var _s = session;

            // Sent back-to-back with no reads in between: the batch is in flight together.
            await session.Send("MAIL FROM:<pipelined@corp.example.com>");
            await session.Send("RCPT TO:<recipient@test.local>");
            await session.Send("DATA");

            Assert.StartsWith("250", await session.ReadLineAsync()); // MAIL FROM
            Assert.StartsWith("250", await session.ReadLineAsync()); // RCPT TO
            Assert.StartsWith("354", await session.ReadLineAsync()); // DATA

            await session.Send("Subject: pipelined");
            await session.Send("");
            await session.Send("body");
            await session.Send(".");
            Assert.StartsWith("250", await session.ReadLineAsync());
        }
        finally
        {
            server.Dispose();
        }

        var delivered = Assert.Single(delivery.Delivered);
        Assert.Equal("pipelined@corp.example.com", delivered.From);
    }

    /// <summary>
    /// <c>RSET</c> between two transactions on one connection discards the first envelope entirely.
    /// </summary>
    /// <remarks>
    /// Exchange reuses a connector's TCP connection for many messages and issues <c>RSET</c> between
    /// them. Envelope state leaking across an <c>RSET</c> would attach the previous sender or an extra
    /// recipient to the next message — a misdelivery, not a rejection, and therefore silent.
    /// </remarks>
    [Fact]
    public async Task Rset_BetweenTransactions_DoesNotLeakEnvelopeState()
    {
        var delivery = new RecordingDelivery();
        var (server, port) = Start(delivery);
        try
        {
            var (session, _) = await OpenAsync(port);
            await using var _s = session;

            await session.Send("MAIL FROM:<abandoned@corp.example.com>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("RCPT TO:<abandoned-rcpt@test.local>");
            Assert.StartsWith("250", await session.ReadLineAsync());

            await session.Send("RSET");
            Assert.StartsWith("250", await session.ReadLineAsync());

            await AssertTransactionSucceedsAsync(session, "after-rset");
        }
        finally
        {
            server.Dispose();
        }

        var delivered = Assert.Single(delivery.Delivered);
        Assert.Equal("sender@corp.example.com", delivered.From);
        Assert.Single(delivered.DeliverTo);
        Assert.DoesNotContain("abandoned", delivered.From, StringComparison.Ordinal);
    }

    /// <summary>
    /// Many messages sent back-to-back on one connection are each delivered once, with the correct
    /// envelope.
    /// </summary>
    /// <remarks>
    /// This is the shape of real connector traffic: Exchange holds a connection open and streams a
    /// queue through it. The assertion that matters is the pairing of each envelope with its own
    /// body — a per-connection state bug shows up here and nowhere in single-message tests.
    /// </remarks>
    [Fact]
    public async Task ManyMessagesOnOneConnection_EachPairedWithItsOwnEnvelope()
    {
        const int count = 12;
        var delivery = new RecordingDelivery();
        var (server, port) = Start(delivery);
        try
        {
            var (session, _) = await OpenAsync(port);
            await using var _s = session;

            for (var i = 0; i < count; i++)
            {
                await session.Send($"MAIL FROM:<sender{i}@corp.example.com>");
                Assert.StartsWith("250", await session.ReadLineAsync());
                await session.Send($"RCPT TO:<recipient{i}@test.local>");
                Assert.StartsWith("250", await session.ReadLineAsync());
                await session.Send("DATA");
                Assert.StartsWith("354", await session.ReadLineAsync());
                await session.Send($"Subject: message-{i}");
                await session.Send("");
                await session.Send($"body-{i}");
                await session.Send(".");
                Assert.StartsWith("250", await session.ReadLineAsync());
            }
        }
        finally
        {
            server.Dispose();
        }

        Assert.Equal(count, delivery.Delivered.Count);
        for (var i = 0; i < count; i++)
        {
            var t = delivery.Delivered[i];
            Assert.Equal($"sender{i}@corp.example.com", t.From);
            Assert.Contains($"body-{i}", t.RawBody, StringComparison.Ordinal);
        }
    }

    // ── message fidelity ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>X-MS-Exchange-*</c> headers survive the DATA path byte-for-byte, including a header whose
    /// value is folded across continuation lines.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Source: <see cref="PlatformContract.Provenance.HeaderPreservation"/>. Exchange stamps
    /// organisation-scoped headers that downstream compliance and routing tooling reads. Reordering,
    /// unfolding, or re-casing them is a silent data change — the message still delivers, and the
    /// tooling downstream draws a different conclusion.
    /// </para>
    /// <para>
    /// Folding is included deliberately: a naive line-by-line header parse that rejoins continuation
    /// lines with a single space, or drops the leading whitespace, passes a flat-header test and
    /// fails this one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ExchangeHeaders_IncludingFoldedValues_ArePreservedVerbatim()
    {
        var delivery = new RecordingDelivery();
        var (server, port) = Start(delivery);
        try
        {
            var (session, _) = await OpenAsync(port);
            await using var _s = session;

            await session.Send("MAIL FROM:<sender@corp.example.com>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("RCPT TO:<recipient@test.local>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("DATA");
            Assert.StartsWith("354", await session.ReadLineAsync());

            await session.Send("Received: from EXCH01.corp.example.com (10.0.0.5) by relay.test.local");
            await session.Send(" with Microsoft SMTP Server; Mon, 1 Sep 2026 12:00:00 +0000");
            await session.Send("X-MS-Exchange-Organization-AuthAs: Internal");
            await session.Send("X-MS-Exchange-Organization-AuthMechanism: 04");
            await session.Send("X-MS-Exchange-Organization-SCL: -1");
            await session.Send("X-MS-Exchange-Organization-MessageDirectionality: Originating");
            await session.Send("Subject: header fidelity");
            await session.Send("");
            await session.Send("body");
            await session.Send(".");
            Assert.StartsWith("250", await session.ReadLineAsync());
        }
        finally
        {
            server.Dispose();
        }

        var body = Assert.Single(delivery.Delivered).RawBody;
        Assert.NotNull(body);

        Assert.Contains("X-MS-Exchange-Organization-AuthAs: Internal", body, StringComparison.Ordinal);
        Assert.Contains("X-MS-Exchange-Organization-AuthMechanism: 04", body, StringComparison.Ordinal);
        Assert.Contains("X-MS-Exchange-Organization-SCL: -1", body, StringComparison.Ordinal);
        Assert.Contains("X-MS-Exchange-Organization-MessageDirectionality: Originating", body,
            StringComparison.Ordinal);

        // The folded continuation keeps its leading whitespace and its own line.
        Assert.Contains("\r\n with Microsoft SMTP Server; Mon, 1 Sep 2026 12:00:00 +0000", body,
            StringComparison.Ordinal);

        // Header order is preserved: AuthAs was stamped before SCL and must still precede it.
        Assert.True(
            body.IndexOf("AuthAs", StringComparison.Ordinal) <
            body.IndexOf("MessageDirectionality", StringComparison.Ordinal),
            "Exchange header order was not preserved.");
    }

    /// <summary>
    /// A body line beginning with a period is dot-unstuffed exactly once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RFC 5321 §4.5.2. Exchange transmits quoted plain-text bodies and signature blocks that begin
    /// lines with <c>.</c>, and MIME base64 chunks can begin with one by chance. Unstuffing twice
    /// corrupts the body; not unstuffing at all corrupts it differently. This test pins the exact
    /// number of periods rather than merely asserting the line is present.
    /// </para>
    /// <para>
    /// The <c>..</c> case is the ordinary one; <c>...</c> is included because an implementation that
    /// strips all leading periods rather than exactly one passes the two-period case.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task DotStuffedBody_IsUnstuffedExactlyOnce()
    {
        var delivery = new RecordingDelivery();
        var (server, port) = Start(delivery);
        try
        {
            var (session, _) = await OpenAsync(port);
            await using var _s = session;

            await session.Send("MAIL FROM:<sender@corp.example.com>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("RCPT TO:<recipient@test.local>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("DATA");
            Assert.StartsWith("354", await session.ReadLineAsync());

            await session.Send("Subject: dot stuffing");
            await session.Send("");
            await session.Send("..leading single period");
            await session.Send("...leading double period");
            await session.Send("no leading period");
            await session.Send(".");
            Assert.StartsWith("250", await session.ReadLineAsync());
        }
        finally
        {
            server.Dispose();
        }

        var body = Assert.Single(delivery.Delivered).RawBody;
        Assert.NotNull(body);

        Assert.Contains("\r\n.leading single period\r\n", body, StringComparison.Ordinal);
        Assert.Contains("\r\n..leading double period\r\n", body, StringComparison.Ordinal);
        Assert.Contains("\r\nno leading period", body, StringComparison.Ordinal);
    }

    // ── failure mapping ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A delivery handler that reports a temporary failure yields a 4xx, so the Exchange queue
    /// retries rather than generating an NDR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Source: <see cref="PlatformContract.Provenance.RetrySemantics"/>. This is the on-premises
    /// counterpart to the Office 365 retry test, and the reasoning differs: on-premises internal
    /// relay has no journaling loss asymmetry, so the requirement is simply that a transient
    /// downstream condition must not be reported as permanent. A 5xx here makes Exchange generate an
    /// NDR to an internal user for what was a momentary database or disk problem.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TemporaryDeliveryFailure_YieldsFourXx_SoExchangeRetries()
    {
        var delivery = new RecordingDelivery
        {
            HandlerOverride = (_, _) => Task.FromResult(SmtpDeliveryResult.TemporaryFailure())
        };

        var (server, port) = Start(delivery);
        try
        {
            var (session, _) = await OpenAsync(port);
            await using var _s = session;

            await session.Send("MAIL FROM:<sender@corp.example.com>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("RCPT TO:<recipient@test.local>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("DATA");
            Assert.StartsWith("354", await session.ReadLineAsync());
            await session.Send("Subject: transient");
            await session.Send("");
            await session.Send("body");
            await session.Send(".");

            var reply = await session.ReadLineAsync();
            Assert.NotNull(reply);
            Assert.StartsWith("4", reply);
        }
        finally
        {
            server.Dispose();
        }
    }

    /// <summary>
    /// A connection refused by the filter is rejected at greeting time and never reaches a
    /// transaction.
    /// </summary>
    /// <remarks>
    /// An on-premises receive connector is typically restricted to the internal ranges that host
    /// Exchange, and that restriction is enforced here through <c>IMailFilter</c>. Rejecting at
    /// connect, before any envelope is accepted, is what keeps an unauthorised peer from consuming a
    /// transaction slot at all.
    /// </remarks>
    [Fact]
    public async Task ConnectionRefusedByFilter_IsRejectedBeforeAnyTransaction()
    {
        var delivery = new RecordingDelivery();
        var filter = new ConfigurableFilter
        {
            Connection = new SmtpResult(SmtpResultType.PermanentFail)
        };

        var (server, port) = Start(delivery, filter);
        try
        {
            await using var session = await SmtpSession.ConnectAsync(port);
            var greeting = await session.ReadLineAsync();

            Assert.NotNull(greeting);
            Assert.StartsWith("5", greeting);
        }
        finally
        {
            server.Dispose();
        }

        Assert.Empty(delivery.Delivered);
        Assert.NotNull(filter.LastConnectionEp);
    }
}
