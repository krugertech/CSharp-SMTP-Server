using Xunit.Abstractions;

namespace CSharp_SMTP_Server.Tests.Load;

/// <summary>
/// Load, concurrency and message-integrity tests.
/// </summary>
/// <remarks>
/// <para><b>What is asserted vs. what is measured.</b> These are different things and mixing them
/// produces either flaky builds or useless ones:</para>
/// <list type="bullet">
/// <item><description>
/// <b>Asserted</b> (deterministic, machine-independent): every accepted message is delivered exactly
/// once, with its payload byte-identical to what was sent; no connection is refused or dropped; no
/// unhandled exception reaches the logger; the server still accepts new connections afterwards.
/// </description></item>
/// <item><description>
/// <b>Measured</b> (machine-dependent, reported not asserted): throughput and latency percentiles,
/// written to <c>load-metrics.json</c> by <see cref="LoadReport"/>. Gating a build on a msgs/sec
/// floor tuned on one machine yields red builds on another, so the numbers are recorded for
/// deliberate comparison between changes rather than enforced.
/// </description></item>
/// </list>
/// <para>
/// Note on the throughput ceiling: delivery is ACK-gated — the server awaits
/// <c>IMailDelivery.EmailReceivedAsync</c> before writing the 250 — so per-connection throughput is
/// bounded by handler latency. The no-op-handler scenarios measure the library's own ceiling; the
/// <see cref="SlowHandler_SessionsOverlap_ThroughputScalesWithConcurrency"/> scenario uses a
/// deliberately slow handler to prove sessions genuinely overlap instead of serializing.
/// </para>
/// </remarks>
[Trait("Category", "Load")]
public sealed class LoadAndIntegrityTests
{
    private readonly ITestOutputHelper _output;

    public LoadAndIntegrityTests(ITestOutputHelper output) => _output = output;

    /// <summary>Reports a result to the console, the xUnit output and the JSON report.</summary>
    private void Report(LoadResult result)
    {
        _output.WriteLine(result.ToString());
        LoadReport.Add(result);
    }

    /// <summary>
    /// Asserts the integrity contract: every message sent was delivered exactly once and its payload
    /// survived transport unaltered.
    /// </summary>
    private static void AssertIntegrity(IReadOnlyDictionary<string, string> expected, LoadDelivery delivery)
    {
        Assert.Empty(delivery.Unidentified);
        Assert.Empty(delivery.Duplicates);

        var missing = expected.Keys.Where(id => !delivery.DeliveredHashes.ContainsKey(id)).ToArray();
        Assert.True(missing.Length == 0,
            $"{missing.Length} accepted message(s) were never delivered: {string.Join(", ", missing.Take(10))}");

        var corrupted = expected
            .Where(kv => delivery.DeliveredHashes[kv.Key] != kv.Value)
            .Select(kv => $"{kv.Key}: expected {kv.Value[..12]}…, got {delivery.DeliveredHashes[kv.Key][..12]}…")
            .ToArray();

        Assert.True(corrupted.Length == 0,
            $"{corrupted.Length} message(s) were corrupted in transit:\n{string.Join("\n", corrupted.Take(10))}");
    }

    // ── fast tier: runs on every `dotnet test` ────────────────────────────────────────────────

    /// <summary>
    /// Baseline integrity with no concurrency: each corpus sample survives a round trip intact.
    /// Isolates payload fidelity from any concurrency effect, so a corpus/normalization problem is
    /// distinguishable from a race.
    /// </summary>
    [Fact]
    public async Task SingleConnection_EachSample_ArrivesIntact()
    {
        var delivery = new LoadDelivery();
        var (server, port) = LoadDriver.StartServer(delivery);

        using (server)
        {
            var (result, expected) = await LoadDriver.RunAsync(
                "single-connection", port, concurrency: 1,
                messagesPerConnection: MessageCorpus.Samples.Count);

            Report(result);

            Assert.True(result.Clean, $"expected a clean run, got {result}");
            AssertIntegrity(expected, delivery);

            // Every distinct sample was exercised, not the same one three times.
            Assert.Equal(MessageCorpus.Samples.Count, delivery.DeliveredHashes.Values.Distinct().Count());
        }
    }

    /// <summary>
    /// Modest concurrency with integrity verification — the standing regression guard for the
    /// <c>Listener.ClientProcessors</c> race and for any future cross-talk between sessions.
    /// </summary>
    [Fact]
    public async Task ConcurrentConnections_AllMessagesIntact_NoFailures()
    {
        var logger = new RecordingLogger();
        var delivery = new LoadDelivery();
        var (server, port) = LoadDriver.StartServer(delivery, logger);

        using (server)
        {
            var (result, expected) = await LoadDriver.RunAsync(
                "concurrent-fast", port, concurrency: 16, messagesPerConnection: 4);

            Report(result);

            Assert.True(result.Clean, $"expected a clean run, got {result}");
            AssertIntegrity(expected, delivery);

            // Sessions genuinely overlapped rather than being serialized by the accept loop.
            Assert.True(delivery.PeakConcurrentHandlers > 1,
                $"expected overlapping deliveries, peak was {delivery.PeakConcurrentHandlers}");

            Assert.DoesNotContain(logger.Errors, e => e.Contains("[Client receive loop]"));
            Assert.DoesNotContain(logger.Errors, e => e.Contains("[Listening]"));

            // The listener still serves new clients after the burst.
            await using var after = await SmtpSession.ConnectAsync(port);
            Assert.StartsWith("220 ", await after.ReadLineAsync());
        }
    }

    /// <summary>
    /// Many sequential transactions on one connection: per-session state must reset cleanly between
    /// messages, with no bleed of sender, recipients or body from the previous transaction.
    /// </summary>
    [Fact]
    public async Task ManyMessagesOnOneConnection_StateResetsBetweenTransactions()
    {
        var delivery = new LoadDelivery();
        var (server, port) = LoadDriver.StartServer(delivery);

        using (server)
        {
            var (result, expected) = await LoadDriver.RunAsync(
                "pipelined-single-conn", port, concurrency: 1, messagesPerConnection: 25);

            Report(result);

            Assert.True(result.Clean, $"expected a clean run, got {result}");
            AssertIntegrity(expected, delivery);
            Assert.Equal(25, delivery.DeliveredHashes.Count);
        }
    }

    /// <summary>
    /// A slow delivery handler must not serialize the server: with delivery ACK-gated, N concurrent
    /// sessions each waiting on a 200 ms handler must still finish in far less than N × 200 ms.
    /// </summary>
    /// <remarks>
    /// The assertion is deliberately loose (well under the fully-serialized time, rather than near
    /// the ideal parallel time) so it proves overlap without being sensitive to scheduler jitter.
    /// </remarks>
    [Fact]
    public async Task SlowHandler_SessionsOverlap_ThroughputScalesWithConcurrency()
    {
        const int concurrency = 12;
        var handlerDelay = TimeSpan.FromMilliseconds(200);

        var delivery = new LoadDelivery(handlerDelay);
        var (server, port) = LoadDriver.StartServer(delivery);

        using (server)
        {
            var (result, expected) = await LoadDriver.RunAsync(
                "slow-handler-overlap", port, concurrency, messagesPerConnection: 1);

            Report(result);

            Assert.True(result.Clean, $"expected a clean run, got {result}");
            AssertIntegrity(expected, delivery);

            var serializedSeconds = concurrency * handlerDelay.TotalSeconds;
            Assert.True(result.WallClockSeconds < serializedSeconds / 2,
                $"deliveries appear serialized: {result.WallClockSeconds:F2}s for {concurrency} " +
                $"× {handlerDelay.TotalMilliseconds}ms handlers (serialized would be ~{serializedSeconds:F2}s)");

            Assert.True(delivery.PeakConcurrentHandlers >= concurrency / 2,
                $"expected substantial handler overlap, peak was {delivery.PeakConcurrentHandlers}/{concurrency}");
        }
    }

    /// <summary>
    /// Guards the Q1 fix: RFC 5321 dot-stuffing is removed before the body reaches delivery.
    /// </summary>
    /// <remarks>
    /// The load corpus still avoids leading-dot lines so this focused test remains the exact regression
    /// guard for the transport transformation.
    /// </remarks>
    [Fact]
    public async Task DotStuffing_IsUnstuffed_Q1Fixed()
    {
        var delivery = new RecordingDelivery();
        var port = TestPorts.Allocate();
        using var server = TestServers.Build(port, delivery: delivery);
        server.Start();

        await using var session = await SmtpSession.ConnectAsync(port);
        Assert.StartsWith("220 ", await session.ReadLineAsync());
        await session.Send("EHLO load.client");
        await session.ReadResponseAsync();

        await session.Send("MAIL FROM:<a@example.com>");
        Assert.StartsWith("250", await session.ReadLineAsync());
        await session.Send("RCPT TO:<b@example.org>");
        Assert.StartsWith("250", await session.ReadLineAsync());
        await session.Send("DATA");
        Assert.StartsWith("354", await session.ReadLineAsync());

        await session.Send("Subject: dot test");
        await session.Send("");
        // An RFC-compliant client dot-stuffs a literal ".leading" as "..leading".
        await session.Send("..leading dot line");
        await session.Send(".");
        Assert.StartsWith("250", await session.ReadLineAsync());

        var body = Assert.Single(delivery.Delivered).RawBody;

        // RFC 5321 §4.5.2: the stuffing dot is transport framing and is stripped, so the archive holds
        // the line the sender composed.
        Assert.DoesNotContain("..leading dot line", body);
        Assert.Contains("\n.leading dot line", MessageCorpus.Canonicalize(body));
    }

    // ── heavy tier: opt in with SMTP_LOADTEST=1 ───────────────────────────────────────────────

    /// <summary>
    /// The concurrency ladder: identical work at rising connection counts, to find where throughput
    /// stops scaling or errors begin. Integrity is verified at every rung, so a level that degrades
    /// silently under load is caught rather than just being slow.
    /// </summary>
    [Trait("Load", "heavy")]
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(500)]
    public async Task ConcurrencyLadder_IntegrityHolds_AtEveryLevel(int concurrency)
    {
        if (LoadTestGate.SkipHeavy($"ladder-{concurrency}")) return;

        var logger = new RecordingLogger();
        var delivery = new LoadDelivery();
        var (server, port) = LoadDriver.StartServer(delivery, logger);

        using (server)
        {
            var (result, expected) = await LoadDriver.RunAsync(
                $"ladder-conc-{concurrency}", port, concurrency, messagesPerConnection: 2);

            Report(result);

            Assert.True(result.Clean, $"expected a clean run, got {result}");
            AssertIntegrity(expected, delivery);
            Assert.DoesNotContain(logger.Errors, e => e.Contains("[Client receive loop]"));
        }
    }

    /// <summary>
    /// Sustained volume: 1000 messages over a moderate connection pool, the closest scenario to a
    /// real burst. Guards against leaks and slow degradation that a short run would not reveal.
    /// </summary>
    [Trait("Load", "heavy")]
    [Fact]
    public async Task SustainedVolume_OneThousandMessages_AllIntact()
    {
        if (LoadTestGate.SkipHeavy("sustained-1000")) return;

        var logger = new RecordingLogger();
        var delivery = new LoadDelivery();
        var (server, port) = LoadDriver.StartServer(delivery, logger);

        using (server)
        {
            var (result, expected) = await LoadDriver.RunAsync(
                "sustained-1000", port, concurrency: 50, messagesPerConnection: 20);

            Report(result);

            Assert.Equal(1000, result.Attempted);
            Assert.True(result.Clean, $"expected a clean run, got {result}");
            AssertIntegrity(expected, delivery);
            Assert.DoesNotContain(logger.Errors, e => e.Contains("[Client receive loop]"));

            await using var after = await SmtpSession.ConnectAsync(port);
            Assert.StartsWith("220 ", await after.ReadLineAsync());
        }
    }

    /// <summary>
    /// Maximum receive rate: many short-lived connections each sending one message, which is the
    /// accept-path-dominated shape and the most aggressive stress on connection registration.
    /// </summary>
    [Trait("Load", "heavy")]
    [Fact]
    public async Task MaxReceiveRate_ShortLivedConnections_NoConnectionRefused()
    {
        if (LoadTestGate.SkipHeavy("max-rate")) return;

        var logger = new RecordingLogger();
        var delivery = new LoadDelivery();
        var (server, port) = LoadDriver.StartServer(delivery, logger);

        using (server)
        {
            var (result, expected) = await LoadDriver.RunAsync(
                "max-receive-rate", port, concurrency: 200, messagesPerConnection: 1);

            Report(result);

            Assert.True(result.Clean, $"expected a clean run, got {result}");
            AssertIntegrity(expected, delivery);
        }
    }
}
