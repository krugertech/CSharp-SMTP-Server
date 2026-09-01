using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSharp_SMTP_Server.Tests.Load;

/// <summary>One recorded message attempt: how long it took, whether it was accepted, and its size.</summary>
internal readonly record struct Attempt(double ElapsedMs, bool Accepted, int Bytes);

/// <summary>
/// Collects per-message timings across concurrent senders and reduces them to a
/// <see cref="LoadResult"/>. Thread-safe: senders call <see cref="Record"/> from many tasks at once.
/// </summary>
internal sealed class LoadMetrics
{
    private readonly List<Attempt> _attempts = new();
    private readonly List<string> _failures = new();
    private readonly Stopwatch _wall = new();

    internal void Start() => _wall.Start();

    internal void Stop() => _wall.Stop();

    internal void Record(double elapsedMs, bool accepted, int bytes)
    {
        lock (_attempts) _attempts.Add(new Attempt(elapsedMs, accepted, bytes));
    }

    /// <summary>Records a hard failure (exception, timeout, refused connection) with context.</summary>
    internal void RecordFailure(string detail)
    {
        lock (_failures) _failures.Add(detail);
    }

    internal IReadOnlyList<string> Failures
    {
        get { lock (_failures) return _failures.ToArray(); }
    }

    /// <summary>Reduces the collected samples into a summary. Safe to call after all senders finish.</summary>
    internal LoadResult Summarize(string scenario, int concurrency, int messagesPerConnection)
    {
        Attempt[] attempts;
        lock (_attempts) attempts = _attempts.ToArray();

        var latencies = attempts.Select(a => a.ElapsedMs).OrderBy(x => x).ToArray();
        var seconds = _wall.Elapsed.TotalSeconds;
        var accepted = attempts.Count(a => a.Accepted);

        // Byte volume counts only accepted messages, so it always agrees with MessagesPerSecond:
        // both describe work the server actually completed, not work that was attempted.
        var acceptedBytes = attempts.Where(a => a.Accepted).Sum(a => (long)a.Bytes);

        return new LoadResult
        {
            Scenario = scenario,
            Concurrency = concurrency,
            MessagesPerConnection = messagesPerConnection,
            Attempted = attempts.Length,
            Accepted = accepted,
            Failed = attempts.Length - accepted + Failures.Count,
            WallClockSeconds = Math.Round(seconds, 4),
            MessagesPerSecond = seconds > 0 ? Math.Round(accepted / seconds, 2) : 0,
            BytesAccepted = acceptedBytes,
            MegabytesPerSecond = seconds > 0 ? Math.Round(acceptedBytes / 1024.0 / 1024.0 / seconds, 2) : 0,
            MeanMessageBytes = accepted > 0 ? (int)(acceptedBytes / accepted) : 0,
            LatencyP50Ms = Percentile(latencies, 50),
            LatencyP95Ms = Percentile(latencies, 95),
            LatencyP99Ms = Percentile(latencies, 99),
            LatencyMaxMs = latencies.Length > 0 ? Math.Round(latencies[^1], 3) : 0,
        };
    }

    /// <summary>Nearest-rank percentile over a pre-sorted array.</summary>
    private static double Percentile(double[] sorted, int percentile)
    {
        if (sorted.Length == 0) return 0;

        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1;
        return Math.Round(sorted[Math.Clamp(rank, 0, sorted.Length - 1)], 3);
    }
}

/// <summary>Summary of one load scenario. Serialized to the metrics report consumed across runs.</summary>
internal sealed class LoadResult
{
    public string Scenario { get; init; } = "";
    public int Concurrency { get; init; }
    public int MessagesPerConnection { get; init; }
    public int Attempted { get; init; }
    public int Accepted { get; init; }
    public int Failed { get; init; }
    public double WallClockSeconds { get; init; }
    public double MessagesPerSecond { get; init; }

    /// <summary>Total payload bytes accepted — the run's actual data volume.</summary>
    public long BytesAccepted { get; init; }

    /// <summary>
    /// Byte throughput. The key companion to <see cref="MessagesPerSecond"/>: with a mixed-size
    /// corpus, msgs/sec alone cannot distinguish "slower" from "moving larger messages", and this
    /// is the metric that stays comparable when the corpus changes.
    /// </summary>
    public double MegabytesPerSecond { get; init; }

    /// <summary>Mean accepted message size, so a rate is always interpretable against a size.</summary>
    public int MeanMessageBytes { get; init; }

    public double LatencyP50Ms { get; init; }
    public double LatencyP95Ms { get; init; }
    public double LatencyP99Ms { get; init; }
    public double LatencyMaxMs { get; init; }

    /// <summary>Whether every attempted message was accepted and no hard failure occurred.</summary>
    [JsonIgnore]
    public bool Clean => Failed == 0 && Accepted == Attempted;

    public override string ToString() =>
        $"{Scenario,-28} conc={Concurrency,4} msgs={Attempted,5} ok={Accepted,5} fail={Failed,3} " +
        $"{WallClockSeconds,7:F2}s {MessagesPerSecond,8:F1}msg/s {MegabytesPerSecond,7:F1}MB/s " +
        $"avg={MeanMessageBytes / 1024,5}KB tot={BytesAccepted / 1024.0 / 1024.0,7:F1}MB " +
        $"p50={LatencyP50Ms,7:F1}ms p95={LatencyP95Ms,8:F1}ms p99={LatencyP99Ms,8:F1}ms";
}

/// <summary>
/// Writes the metrics report for a run. Results accumulate in memory across the load test classes
/// and are flushed to a JSON file so runs can be compared between code changes.
/// </summary>
/// <remarks>
/// Reporting is deliberately decoupled from assertions. Throughput on a shared or virtualized CI
/// machine varies far too much to gate a build on: a hard msgs/sec floor tuned on one machine
/// becomes a flaky red build on another, and flaky red builds get ignored. So the tests assert only
/// invariants that hold regardless of speed (no failures, no corruption, no lost messages) and the
/// numbers are recorded here for deliberate comparison.
/// </remarks>
internal static class LoadReport
{
    private static readonly List<LoadResult> Results = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>Directory the report is written to; override with SMTP_LOADTEST_OUT.</summary>
    internal static string OutputDirectory =>
        Environment.GetEnvironmentVariable("SMTP_LOADTEST_OUT") ?? AppContext.BaseDirectory;

    internal static string ReportPath => Path.Combine(OutputDirectory, "load-metrics.json");

    /// <summary>Records a result and rewrites the report. Rewriting each time keeps partial runs useful.</summary>
    internal static void Add(LoadResult result)
    {
        lock (Results)
        {
            Results.Add(result);

            try
            {
                var payload = new
                {
                    generatedUtc = DateTime.UtcNow.ToString("O"),
                    machine = Environment.MachineName,
                    processorCount = Environment.ProcessorCount,
                    runtime = Environment.Version.ToString(),
                    os = Environment.OSVersion.ToString(),
                    serverVersion = SMTPServer.VersionString,
                    results = Results,
                };

                Directory.CreateDirectory(OutputDirectory);
                File.WriteAllText(ReportPath, JsonSerializer.Serialize(payload, JsonOptions));
            }
            catch (Exception e)
            {
                // A read-only or missing output directory must never fail a test — the report is a
                // diagnostic artifact, not part of the contract under test.
                Console.WriteLine($"[load] could not write {ReportPath}: {e.Message}");
            }
        }
    }
}
