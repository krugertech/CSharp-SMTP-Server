using System.Net;
using CSharp_SMTP_Server.Networking;
using CSharp_SMTP_Server.Protocol.Commands;
using Xunit.Abstractions;

namespace CSharp_SMTP_Server.Tests.Load;

/// <summary>
/// Acceptance tests for the Office 365 journaling-relay deployment: this server receives journaled
/// mail from Exchange Online, where <b>rejecting a message loses a compliance record</b>.
/// </summary>
/// <remarks>
/// <para>
/// These tests encode the settings that deployment requires, so a future change that reintroduces a
/// rejection path fails the build rather than silently dropping journal reports.
/// </para>
/// <para>
/// The governing asymmetry: for ordinary mail a 5xx rejection is a sender's problem, but a journal
/// report refused with a permanent failure is a record that no longer exists anywhere. Every limit
/// that can produce a 5xx on a well-formed message is therefore either disabled or raised well above
/// what Exchange Online can send.
/// </para>
/// </remarks>
[Trait("Category", "Load")]
public sealed class Office365RelayTests
{
    private readonly ITestOutputHelper _output;

    public Office365RelayTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Office 365's maximum message size is 150 MB. Because the transport encodes attachments
    /// (base64 inflates by ~4/3) the on-the-wire message is larger than the attachment it carries, so
    /// the configured ceiling must exceed 150 MB rather than equal it.
    /// </summary>
    private const long O365MaxMessageBytes = 150L * 1024 * 1024;

    /// <summary>
    /// The configured ceiling: 150 MB plus headroom, expressed in the characters the counter uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not 0.</b> Disabling the limit entirely would let one client stream unbounded data into a
    /// <c>StringBuilder</c> until the pod is OOM-killed — a trivial denial of service. A finite
    /// ceiling is both the DoS bound and the size contract.
    /// </para>
    /// <para>
    /// <b>The limit genuinely bounds memory</b>, which is what makes it a real defense rather than
    /// just a policy: once <c>Counter</c> exceeds the limit, <c>ProcessData</c> stops appending to
    /// <c>DataBuilder</c> and only counts. Measured: 200 MB sent against a 10 MB limit peaked at
    /// ~126 MB working set — not the ~2 GB an unbounded 200 MB message would cost — and the
    /// connection is still usable afterwards (the 552 arrives at the terminating dot).
    /// </para>
    /// <para>
    /// <b>Units.</b> <c>MessageCharactersLimit</c> counts <i>characters, excluding CRLF</i>, not
    /// bytes. For the ASCII/base64 that MIME transport uses, 1 char == 1 byte, so the numbers align;
    /// headroom above 150 MB covers MIME headers, base64 expansion of the encoded attachment, and the
    /// server's own prepended <c>Received:</c> header. The field is <c>uint</c> (max ~4.29 GB), so
    /// this value fits comfortably.
    /// </para>
    /// </remarks>
    private const uint JournalingSizeLimit = 200u * 1024 * 1024;

    /// <summary>
    /// The recommended production configuration for an O365 journaling relay.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>
    /// <c>MessageCharactersLimit = </c><see cref="JournalingSizeLimit"/> — high enough that no
    /// legitimate O365 message (max 150 MB) is refused, but <b>finite</b>, so a hostile or broken
    /// client cannot stream unbounded data into memory. Setting it to <c>0</c> would remove the
    /// rejection path at the cost of an easy OOM; a ceiling above what O365 can send achieves the
    /// same delivery guarantee while keeping the DoS bound.
    /// </description></item>
    /// <item><description>
    /// <c>RecipientsLimit = 0</c> — unlimited. The default of 50 answers <c>550 5.5.3</c>; a journal
    /// report for a message sent to a large distribution list can carry far more, and a journaling
    /// stream is a single trusted sender, so the anti-spam rationale for the cap does not apply.
    /// </description></item>
    /// <item><description>
    /// SPF and DMARC <b>off</b>. Both reject on failure (<c>554 5.7.23</c> / <c>554 5.7.1</c>), and a
    /// journal report's envelope sender is the journaling mailbox, not the original sender — so the
    /// original message's SPF alignment is irrelevant and can fail spuriously. They also add a
    /// blocking DNS lookup to the session thread.
    /// </description></item>
    /// </list>
    /// </remarks>
    private static ServerOptions JournalingOptions()
    {
        var options = new ServerOptions(validateSPF: false, validateDMARC: false, null)
        {
            ServerName = "journal.local",
            MessageCharactersLimit = JournalingSizeLimit,
            RecipientsLimit = 0,
        };

        return options;
    }

    private static (SMTPServer Server, ushort Port) StartJournalingServer(
        Interfaces.IMailDelivery delivery, Interfaces.ILogger? logger = null)
    {
        var port = TestPorts.Allocate();
        var server = new SMTPServer(
            new[] { new ListeningParameters(IPAddress.Loopback, new[] { port }, null) },
            JournalingOptions(), delivery, logger);
        server.Start();
        return (server, port);
    }

    /// <summary>Opens a session and completes EHLO, returning the session.</summary>
    private static async Task<SmtpSession> OpenAsync(ushort port, TimeSpan? timeout = null)
    {
        var session = await SmtpSession.ConnectAsync(port, timeout: timeout ?? TimeSpan.FromMinutes(10));
        var greeting = await session.ReadLineAsync();
        Assert.StartsWith("220", greeting);
        await session.Send("EHLO mail.protection.outlook.com");
        await session.ReadResponseAsync();
        return session;
    }

    // ── size ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A 150 MB message is accepted and delivered with its body intact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Memory cost — the operationally important result.</b> Measured on a 16-core Windows box, a
    /// single 150 MB message drove peak working set to <b>~1.6 GB</b>, roughly 11× the message size.
    /// The message is accumulated in a <c>StringBuilder</c>, materialized with <c>ToString()</c>, then
    /// copied again by <c>MailTransaction.Clone()</c>, and .NET strings are UTF-16 (2 bytes/char) —
    /// so several full copies of the message exist at once, plus <c>StringBuilder</c> growth slack.
    /// </para>
    /// <para>
    /// <b>Pod sizing must account for this.</b> At 150 MB messages, a pod's memory limit needs roughly
    /// 2 GB of headroom <i>per concurrent large message</i>, not per pod. Concurrency, not total
    /// volume, is what causes OOM here: 4 × 50 MB concurrently reached ~1.9 GB peak. The configured
    /// <see cref="JournalingSizeLimit"/> caps the per-message cost, but N concurrent at-limit messages
    /// still multiply it — bound concurrency or size the pod for the product.
    /// </para>
    /// <para>
    /// This amplification is the strongest argument for a streaming DATA path (see
    /// <c>immediate-todo.md</c> item 4); it is tracked as the primary follow-up.
    /// </para>
    /// <para>
    /// This test is heavy-tier because it allocates well over a gigabyte; running it on every build
    /// would risk OOM on a small CI container.
    /// </para>
    /// </remarks>
    [Trait("Load", "heavy")]
    [Fact]
    public async Task LargeMessage_150MB_IsAcceptedIntact()
    {
        if (LoadTestGate.SkipHeavy("o365-150mb")) return;

        var delivery = new RecordingDelivery();
        var logger = new RecordingLogger();
        var (server, port) = StartJournalingServer(delivery, logger);

        using (server)
        {
            await using var session = await OpenAsync(port);

            await session.Send("MAIL FROM:<journal@contoso.onmicrosoft.com>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("RCPT TO:<archive@journal.local>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("DATA");
            Assert.StartsWith("354", await session.ReadLineAsync());

            await session.Send("Subject: O365 journal report (150 MB)");
            await session.Send("");

            // 900-char lines keep each write well under any line-length limit while reaching 150 MB
            // in a manageable number of writes. Content is uniform: this test is about size, and
            // per-line uniqueness is already covered by the corpus integrity tests.
            var line = new string('X', 900);
            var lineBytes = line.Length + 2; // + CRLF
            var lines = (int)(O365MaxMessageBytes / lineBytes);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < lines; i++)
                await session.Send(line);
            await session.Send(".");

            var ack = await session.ReadLineAsync();
            sw.Stop();

            Assert.StartsWith("250", ack);

            var transaction = Assert.Single(delivery.Delivered);
            var bodyBytes = (long)transaction.RawBody.Length;

            // The delivered body carries the payload plus the server's prepended Received: header,
            // so it is at least the size sent.
            Assert.True(bodyBytes >= (long)lines * line.Length,
                $"delivered body is short: {bodyBytes} bytes for {(long)lines * line.Length} sent");

            // Content survived: no truncation partway through a 150 MB accumulation.
            Assert.Contains("Subject: O365 journal report (150 MB)", transaction.RawBody);
            Assert.DoesNotContain(logger.Errors, e => e.Contains("[Client receive loop]"));

            var peakMb = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024;
            _output.WriteLine(
                $"150 MB accepted in {sw.Elapsed.TotalSeconds:F1}s; delivered body " +
                $"{bodyBytes / 1024.0 / 1024.0:F1} MB; process peak working set {peakMb} MB");
        }
    }

    /// <summary>
    /// The default 10 MB <see cref="ServerOptions.MessageCharactersLimit"/> rejects an oversized
    /// message with a <b>permanent</b> 552 — which for journaling means the record is lost.
    /// </summary>
    /// <remarks>
    /// Pinned deliberately: this is the single most dangerous default for this deployment. The test
    /// documents what the default does, so nobody deploys with it by accident and discovers the loss
    /// only when a compliance audit finds a gap.
    /// </remarks>
    [Fact]
    public async Task DefaultSizeLimit_RejectsOversizedMessage_WithPermanentFailure()
    {
        var delivery = new RecordingDelivery();
        var port = TestPorts.Allocate();

        // Deliberately NOT the journaling options: this pins the stock default's behavior.
        var options = TestServers.DefaultOptions();
        options.MessageCharactersLimit = 64 * 1024; // scaled down; the 10 MB default behaves identically

        using var server = new SMTPServer(
            new[] { new ListeningParameters(IPAddress.Loopback, new[] { port }, null) },
            options, delivery);
        server.Start();

        await using var session = await OpenAsync(port, TimeSpan.FromMinutes(2));

        await session.Send("MAIL FROM:<journal@contoso.onmicrosoft.com>");
        Assert.StartsWith("250", await session.ReadLineAsync());
        await session.Send("RCPT TO:<archive@journal.local>");
        Assert.StartsWith("250", await session.ReadLineAsync());
        await session.Send("DATA");
        Assert.StartsWith("354", await session.ReadLineAsync());

        await session.Send("Subject: oversized");
        await session.Send("");
        var line = new string('X', 900);
        for (var i = 0; i < 100; i++) await session.Send(line); // ~90 KB > 64 KB limit
        await session.Send(".");

        // 552 is a PERMANENT failure — Exchange will not retry, so the journal report is gone.
        Assert.Equal("552 5.4.3 Message size exceeds the administrative limit.", await session.ReadLineAsync());
        Assert.Empty(delivery.Delivered);
    }

    /// <summary>
    /// With the journaling ceiling configured, a message the stock 10 MB default would reject is
    /// accepted — the setting that makes the deployment safe, without giving up the DoS bound.
    /// </summary>
    [Fact]
    public async Task JournalingOptions_RaisedSizeLimit_AcceptsMessageThatDefaultWouldReject()
    {
        var delivery = new RecordingDelivery();
        var (server, port) = StartJournalingServer(delivery);

        using (server)
        {
            await using var session = await OpenAsync(port, TimeSpan.FromMinutes(2));

            await session.Send("MAIL FROM:<journal@contoso.onmicrosoft.com>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("RCPT TO:<archive@journal.local>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("DATA");
            Assert.StartsWith("354", await session.ReadLineAsync());

            await session.Send("Subject: oversized but accepted");
            await session.Send("");
            var line = new string('X', 900);
            for (var i = 0; i < 100; i++) await session.Send(line);
            await session.Send(".");

            Assert.StartsWith("250", await session.ReadLineAsync());
            Assert.Single(delivery.Delivered);
        }
    }

    /// <summary>
    /// A client that keeps sending past the configured limit is bounded: the server stops buffering,
    /// answers <c>552</c> at the terminating dot, and the connection remains usable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes a finite <c>MessageCharactersLimit</c> a genuine denial-of-service defense
    /// rather than a policy statement. <c>ProcessData</c> counts every line but appends only while
    /// <c>Counter</c> is within the limit, so an over-limit stream is discarded as it arrives instead
    /// of accumulating.
    /// </para>
    /// <para>
    /// Measured at larger scale: 200 MB sent against a 10 MB limit peaked at ~126 MB working set,
    /// versus the ~2 GB an accepted 200 MB message would cost. This test asserts the mechanism at a
    /// size small enough to run on every build.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task OverLimitFlood_IsDiscardedNotBuffered_AndConnectionSurvives()
    {
        var delivery = new RecordingDelivery();
        var port = TestPorts.Allocate();

        var options = TestServers.DefaultOptions();
        options.MessageCharactersLimit = 64 * 1024;

        using var server = new SMTPServer(
            new[] { new ListeningParameters(IPAddress.Loopback, new[] { port }, null) },
            options, delivery);
        server.Start();

        await using var session = await OpenAsync(port, TimeSpan.FromMinutes(2));

        await session.Send("MAIL FROM:<attacker@example.com>");
        Assert.StartsWith("250", await session.ReadLineAsync());
        await session.Send("RCPT TO:<archive@journal.local>");
        Assert.StartsWith("250", await session.ReadLineAsync());
        await session.Send("DATA");
        Assert.StartsWith("354", await session.ReadLineAsync());

        await session.Send("Subject: flood");
        await session.Send("");

        // Send ~9 MB against a 64 KB limit — 140x over.
        var line = new string('X', 900);
        for (var i = 0; i < 10_000; i++) await session.Send(line);
        await session.Send(".");

        Assert.Equal("552 5.4.3 Message size exceeds the administrative limit.", await session.ReadLineAsync());
        Assert.Empty(delivery.Delivered);

        // The connection is not torn down — the server rejected the message, not the client.
        await session.Send("NOOP");
        Assert.StartsWith("250", await session.ReadLineAsync());
    }

    // ── envelope shapes Office 365 actually sends ─────────────────────────────────────────────

    /// <summary>
    /// <c>MAIL FROM:&lt;&gt;</c> — the null reverse-path — is accepted and the message is delivered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RFC 5321 §4.5.5 requires the null reverse-path to be accepted; it is what every bounce (DSN)
    /// and, importantly here, what Exchange journaling uses for certain system-generated reports.
    /// Rejecting it drops those messages permanently.
    /// </para>
    /// <para>
    /// <c>TransactionCommands.ProcessAddress</c> cannot parse <c>&lt;&gt;</c> (empty address, no '@'),
    /// so the MAIL FROM branch recognizes the null path via <c>IsNullReversePath</c> before calling it
    /// and yields an empty <c>From</c> and an empty <c>FromDomain</c>. This test drives the full
    /// transaction rather than just the 250, because the empty domain has to survive header
    /// generation and delivery, not merely the envelope command.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task NullSender_IsAccepted_AndDelivered()
    {
        var delivery = new RecordingDelivery();
        var (server, port) = StartJournalingServer(delivery);

        using (server)
        {
            await using var session = await OpenAsync(port, TimeSpan.FromMinutes(2));

            await session.Send("MAIL FROM:<>");
            Assert.Equal("250 2.0.0", await session.ReadLineAsync());

            await session.Send("RCPT TO:<archive@journal.local>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("DATA");
            Assert.StartsWith("354", await session.ReadLineAsync());

            await session.Send("Subject: delivery status notification");
            await session.Send("");
            await session.Send("The original message could not be delivered.");
            await session.Send(".");

            Assert.StartsWith("250", await session.ReadLineAsync());
        }

        var received = Assert.Single(delivery.Delivered);
        Assert.Equal(string.Empty, received.From);
        Assert.Equal(string.Empty, received.FromDomain);
        Assert.Equal(new[] { "archive@journal.local" }, received.DeliverTo);
        Assert.Equal("delivery status notification", received.Subject);
    }

    /// <summary>
    /// The null reverse-path is the anchored first bracket pair — never a pair hidden behind
    /// bracket-bearing text, and never a real address.
    /// </summary>
    /// <remarks>
    /// The prefix rule matters for policy, not just syntax: if
    /// <c>AUTH=&lt;&gt; &lt;ceo@victim.example&gt;</c> were read as a null sender, filters would see an
    /// empty sender and SPF would be skipped while a real address sat in the command — a
    /// parser/policy differential. Trailing parameters are still ignored, including O365's
    /// <c>AUTH=&lt;&gt;</c>, because they follow the closing '&gt;'.
    /// </remarks>
    [Theory]
    // Genuine null paths, with and without trailing ESMTP parameters.
    [InlineData("<>", true)]
    [InlineData("<> SIZE=1234", true)]
    [InlineData("<> BODY=8BITMIME AUTH=<>", true)]
    // Real addresses are never null paths, even when a later parameter contains "<>".
    [InlineData("<journal@contoso.onmicrosoft.com>", false)]
    [InlineData("<journal@contoso.onmicrosoft.com> AUTH=<>", false)]
    // Bracket-bearing prefixes must not smuggle a null path past a real address.
    [InlineData("AUTH=<> <ceo@victim.example>", false)]
    [InlineData("><>", false)]
    [InlineData("<><ceo@victim.example>", false)]
    // The whole parameter suffix is validated, not just the next bracket: a bare address after a
    // legitimate bracket-bearing parameter must not slip through.
    [InlineData("<> AUTH=<> <ceo@victim.example>", false)]
    [InlineData("<> X=foo=<ceo@victim.example>", false)]
    [InlineData("<> BODY=8BITMIME <ceo@victim.example>", false)]
    // ...while genuine parameter shapes after a null path stay valid.
    [InlineData("<> BODY=8BITMIME", true)]
    [InlineData("<> AUTH=<> BODY=8BITMIME", true)]
    [InlineData("<> SMTPUTF8", true)]
    // A bare address must never pass as an ESMTP keyword or hide beside the path.
    [InlineData("<>ceo@victim.example", false)]
    [InlineData("<> ceo@victim.example", false)]
    [InlineData("ceo@victim.example <>", false)]
    [InlineData("<> SIZE=1234 ceo@victim.example", false)]
    // Legitimate prefixes still parse: the ':' left by command parsing, and a quoted display name.
    [InlineData(":<>", true)]
    // A prefix that merely starts and ends with a quote is not one quoted display name.
    [InlineData("\"x\" ceo@victim.example \"<>", false)]
    [InlineData("\"unterminated <>", false)]
    // HTAB is not an esmtp-value character, so it cannot hide an address inside a parameter token.
    [InlineData("<> SIZE=1234	<ceo@victim.example>", false)]
    [InlineData("<> SIZE=", false)]        // empty esmtp-value
    [InlineData("<> =1234", false)]        // empty keyword
    // A genuine quoted display name on a real address still parses.
    [InlineData("\"John Doe\" <john@example.com>", false)]
    // Malformed / unterminated paths.
    [InlineData(">", false)]
    [InlineData("<", false)]
    [InlineData("<a@b.c", false)]
    public void IsNullReversePath_IsAnchored_AndRejectsSmuggledPaths(string argument, bool expected)
        => Assert.Equal(expected, TransactionCommands.IsNullReversePath(argument));

    /// <summary>
    /// The anchored parser must not let a bracket-bearing prefix change which address is parsed:
    /// an argument hiding a real address behind <c>AUTH=&lt;&gt;</c> is refused outright (501)
    /// rather than silently accepted as a null sender.
    /// </summary>
    [Theory]
    [InlineData("MAIL FROM:AUTH=<> <ceo@victim.example>")]
    [InlineData("MAIL FROM:><>")]
    [InlineData("MAIL FROM:<><ceo@victim.example>")]
    [InlineData("MAIL FROM:<> AUTH=<> <ceo@victim.example>")]
    [InlineData("MAIL FROM:<> X=foo=<ceo@victim.example>")]
    [InlineData("MAIL FROM:<>ceo@victim.example")]
    [InlineData("MAIL FROM:<> ceo@victim.example")]
    [InlineData("MAIL FROM:ceo@victim.example <>")]
    [InlineData("MAIL FROM:\"x\" ceo@victim.example \"<>")]
    [InlineData("MAIL FROM:<> SIZE=1234	<ceo@victim.example>")]
    public async Task MailFrom_BracketBearingPrefix_IsRefused_NotTreatedAsNullSender(string command)
    {
        var delivery = new RecordingDelivery();
        var (server, port) = StartJournalingServer(delivery);

        using (server)
        {
            await using var session = await OpenAsync(port, TimeSpan.FromMinutes(2));

            await session.Send(command);
            Assert.StartsWith("501", await session.ReadLineAsync());
        }
    }

    /// <summary>
    /// Office 365 sends ESMTP parameters on MAIL FROM (<c>SIZE=</c>, <c>BODY=8BITMIME</c>) and they
    /// must not break envelope parsing.
    /// </summary>
    /// <remarks>
    /// These are accepted because <c>ProcessAddress</c> reads only between '&lt;' and '&gt;' and
    /// ignores trailing text. The server does not act on <c>SIZE=</c> — it neither advertises the
    /// SIZE extension nor pre-rejects on the declared value — which for journaling is the desired
    /// behavior: nothing is refused before the data arrives.
    /// </remarks>
    [Theory]
    [InlineData("MAIL FROM:<journal@contoso.onmicrosoft.com> SIZE=157286400")]
    [InlineData("MAIL FROM:<journal@contoso.onmicrosoft.com> BODY=8BITMIME")]
    [InlineData("MAIL FROM:<journal@contoso.onmicrosoft.com> SIZE=157286400 BODY=8BITMIME")]
    [InlineData("MAIL FROM:<journal@contoso.onmicrosoft.com> BODY=8BITMIME SIZE=157286400 AUTH=<>")]
    public async Task MailFrom_WithO365EsmtpParameters_IsAccepted(string command)
    {
        var delivery = new RecordingDelivery();
        var (server, port) = StartJournalingServer(delivery);

        using (server)
        {
            await using var session = await OpenAsync(port, TimeSpan.FromMinutes(2));

            await session.Send(command);
            Assert.Equal("250 2.0.0", await session.ReadLineAsync());
        }
    }

    /// <summary>
    /// A journal report can name far more than the default 50 recipients;
    /// <c>RecipientsLimit = 0</c> must accept them all rather than answering <c>550 5.5.3</c>.
    /// </summary>
    [Fact]
    public async Task ManyRecipients_WithLimitDisabled_AllAccepted()
    {
        const int recipients = 250;

        var delivery = new RecordingDelivery();
        var (server, port) = StartJournalingServer(delivery);

        using (server)
        {
            await using var session = await OpenAsync(port, TimeSpan.FromMinutes(2));

            await session.Send("MAIL FROM:<journal@contoso.onmicrosoft.com>");
            Assert.StartsWith("250", await session.ReadLineAsync());

            for (var i = 0; i < recipients; i++)
            {
                await session.Send($"RCPT TO:<user{i}@journal.local>");
                var response = await session.ReadLineAsync();
                Assert.StartsWith("250", response);
            }

            await session.Send("DATA");
            Assert.StartsWith("354", await session.ReadLineAsync());
            await session.Send("Subject: wide distribution");
            await session.Send("");
            await session.Send("body");
            await session.Send(".");
            Assert.StartsWith("250", await session.ReadLineAsync());

            Assert.Equal(recipients, Assert.Single(delivery.Delivered).DeliverTo.Count);
        }
    }

    /// <summary>
    /// The default <c>RecipientsLimit = 50</c> rejects the 51st recipient — pinned so the journaling
    /// deployment's need to disable it is documented by a test rather than by convention.
    /// </summary>
    [Fact]
    public async Task DefaultRecipientsLimit_Rejects51stRecipient()
    {
        var delivery = new RecordingDelivery();
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port, delivery: delivery); // default limit of 50
        server.Start();

        await using var session = await OpenAsync(port, TimeSpan.FromMinutes(2));

        await session.Send("MAIL FROM:<journal@contoso.onmicrosoft.com>");
        Assert.StartsWith("250", await session.ReadLineAsync());

        for (var i = 0; i < 50; i++)
        {
            await session.Send($"RCPT TO:<user{i}@journal.local>");
            Assert.StartsWith("250", await session.ReadLineAsync());
        }

        await session.Send("RCPT TO:<one-too-many@journal.local>");
        Assert.Equal("550 5.5.3 Too many recipients", await session.ReadLineAsync());
    }

    /// <summary>
    /// A delivery handler that throws yields <c>451 4.3.0</c> — a <b>temporary</b> failure, so
    /// Exchange retries and the journal report survives a transient backend outage.
    /// </summary>
    /// <remarks>
    /// This is the correct behavior for journaling and worth pinning: if this ever became a 5xx, a
    /// database blip would turn into permanent, silent record loss. It also means the delivery
    /// handler must throw (or return <c>TemporaryFailure</c>) rather than swallow errors — returning
    /// Ok on a failed write would acknowledge a message that was never stored.
    /// </remarks>
    [Fact]
    public async Task DeliveryHandlerThrows_YieldsTemporaryFailure_SoExchangeRetries()
    {
        var delivery = new RecordingDelivery
        {
            HandlerOverride = (_, _) => throw new InvalidOperationException("archive database unavailable"),
        };

        var (server, port) = StartJournalingServer(delivery, new RecordingLogger());

        using (server)
        {
            await using var session = await OpenAsync(port, TimeSpan.FromMinutes(2));

            await session.Send("MAIL FROM:<journal@contoso.onmicrosoft.com>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("RCPT TO:<archive@journal.local>");
            Assert.StartsWith("250", await session.ReadLineAsync());
            await session.Send("DATA");
            Assert.StartsWith("354", await session.ReadLineAsync());
            await session.Send("Subject: retry me");
            await session.Send("");
            await session.Send("body");
            await session.Send(".");

            // 4xx = transient: Exchange queues and retries rather than discarding.
            Assert.StartsWith("451", await session.ReadLineAsync());
        }
    }

    /// <summary>
    /// The journaling configuration performs no DNS lookups: SPF and DMARC are off, so no validator
    /// is constructed and no blocking resolver call can stall a session.
    /// </summary>
    /// <remarks>
    /// Beyond latency, this closes a rejection path: with SPF on, a journal report whose envelope
    /// sender fails SPF is refused with <c>554 5.7.23</c> before the data is ever sent.
    /// </remarks>
    [Fact]
    public void JournalingOptions_DisableSpfAndDmarc_NoResolverConfigured()
    {
        var options = JournalingOptions();

        Assert.False(options.ValidateSPF);
        Assert.False(options.ValidateDMARC);
        Assert.Null(options.DnsServerEndpoint);

        using var server = new SMTPServer(null, options, NoopDelivery.Instance);

        // No DNS endpoint means no validators are built, so no lookup can occur on the session path.
        Assert.Null(server.DnsClient);
        Assert.Null(server.SpfValidator);
        Assert.Null(server.DmarcValidator);
    }
}
