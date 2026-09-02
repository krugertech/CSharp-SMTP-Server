using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace CSharp_SMTP_Server.Bench;

/// <summary>One recorded message: its ACK latency and size.</summary>
internal readonly record struct Attempt(double ElapsedMs, bool Accepted, int Bytes);

/// <summary>
/// Traffic generator. Runs in its own process so it can be pinned to cores the server is not using.
/// </summary>
/// <remarks>
/// <para>
/// Design points that separate this from the in-test harness:
/// </para>
/// <list type="bullet">
/// <item><description><b>Fixed total bytes.</b> Every concurrency rung moves the same volume, so
/// rungs form a strong-scaling series. The in-test ladder sends 2 messages per connection, so
/// concurrency 1 moves 2 messages and concurrency 500 moves 1,000 — the workload grows with the
/// variable under study, and a flat MB/s curve there says nothing about scaling.</description></item>
/// <item><description><b>Timed region excludes setup.</b> Connections are established, greeted and
/// EHLO-negotiated before the clock starts; QUIT and teardown happen after it stops. The in-test
/// wall clock spans connect, greeting, EHLO, QUIT, disposal and client-side digest computation
/// while counting only payload bytes in the numerator.</description></item>
/// <item><description><b>Warm-up and repeats.</b> JIT and thread-pool growth are paid before
/// measurement, and each configuration runs several trials so dispersion is visible.</description></item>
/// <item><description><b>One write per message.</b> Pre-framed bytes, single socket write, so the
/// generator is not the thing being measured.</description></item>
/// </list>
/// </remarks>
internal static class Generator
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Monotonic counter making every message id unique across the whole process.</summary>
    private static int _trialSequence;

    /// <summary>
    /// Waits for the parent's acknowledgement that it has taken a CPU boundary sample. Without this
    /// the generator would keep sending while the parent samples, and the CPU window would not line
    /// up with the timed region.
    /// </summary>
    private static async Task WaitForAckAsync()
    {
        while (await Console.In.ReadLineAsync() is { } line)
            if (line == "ACK") return;
    }

    /// <summary>A live, greeted connection ready to send DATA.</summary>
    private sealed class Connection : IAsyncDisposable
    {
        internal required TcpClient Client { get; init; }
        internal required NetworkStream Stream { get; init; }
        internal required StreamReader Reader { get; init; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await SendLineAsync(Stream, "QUIT");
                await Reader.ReadLineAsync();
            }
            catch
            {
                // Teardown races are not results.
            }

            Reader.Dispose();
            await Stream.DisposeAsync();
            Client.Dispose();
        }
    }

    private static async Task SendLineAsync(NetworkStream stream, string line)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
        await stream.WriteAsync(bytes);
    }

    /// <summary>Opens and greets one connection. Runs before the clock starts.</summary>
    private static async Task<Connection> OpenAsync(ushort port)
    {
        var client = new TcpClient { NoDelay = true };
        using var cts = new CancellationTokenSource(ConnectTimeout);
        await client.ConnectAsync(System.Net.IPAddress.Loopback, port, cts.Token);

        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

        var greeting = await reader.ReadLineAsync();
        if (greeting == null || !greeting.StartsWith("220"))
            throw new InvalidOperationException($"bad greeting: '{greeting}'");

        await SendLineAsync(stream, "EHLO bench.client");

        // Drain the multi-line EHLO response: continuation lines carry '-' at index 3.
        while (await reader.ReadLineAsync() is { } line)
            if (line.Length < 4 || line[3] != '-') break;

        return new Connection { Client = client, Stream = stream, Reader = reader };
    }

    /// <summary>Sends one message on an established connection and waits for the final ACK.</summary>
    private static async Task<bool> SendOneAsync(Connection conn, byte[] wire)
    {
        await SendLineAsync(conn.Stream, "MAIL FROM:<bench@example.com>");
        var mailFrom = await conn.Reader.ReadLineAsync();
        if (mailFrom == null || !mailFrom.StartsWith("250")) return false;

        await SendLineAsync(conn.Stream, "RCPT TO:<sink@example.org>");
        var rcptTo = await conn.Reader.ReadLineAsync();
        if (rcptTo == null || !rcptTo.StartsWith("250")) return false;

        await SendLineAsync(conn.Stream, "DATA");
        var ready = await conn.Reader.ReadLineAsync();
        if (ready == null || !ready.StartsWith("354")) return false;

        // The entire payload in one write: this is the measured server ingestion path.
        await conn.Stream.WriteAsync(wire);

        var ack = await conn.Reader.ReadLineAsync();
        return ack != null && ack.StartsWith("250");
    }

    /// <summary>
    /// Runs one trial at a given concurrency, moving <paramref name="totalMessages"/> messages
    /// regardless of concurrency.
    /// </summary>
    internal static async Task<TrialResult> RunTrialAsync(ushort port, int concurrency, int totalMessages)
    {
        // Establish every connection BEFORE the clock starts.
        var connections = await Task.WhenAll(
            Enumerable.Range(0, concurrency).Select(_ => OpenAsync(port)));

        var perConnection = totalMessages / concurrency;
        var remainder = totalMessages % concurrency;

        // Pre-frame every message this trial will send, before timing.
        var payloads = new byte[concurrency][][];
        for (var c = 0; c < concurrency; c++)
        {
            var count = perConnection + (c < remainder ? 1 : 0);
            payloads[c] = new byte[count][];
            for (var m = 0; m < count; m++)
            {
                var sample = BenchCorpus.Samples[(c + m) % BenchCorpus.Samples.Count];

                // Ids must be unique across the whole process, not just within a trial: the server
                // counts distinct ids to detect drops and duplicates, and per-trial ids would make
                // every repeat trial look like a duplicate delivery.
                payloads[c][m] = sample.WireBytes($"t{Interlocked.Increment(ref _trialSequence)}-c{c}-m{m}");
            }
        }

        var attempts = new System.Collections.Concurrent.ConcurrentBag<Attempt>();
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuAtStart = process.TotalProcessorTime;

        // Trial-boundary markers. The parent samples the server's CPU counter on each marker, so
        // server utilisation is computed over exactly the timed region below rather than over the
        // whole run — bracketing a whole run and pairing it with one rung dilutes the figure with
        // warm-up, discarded trials, framing and idle time.
        // Block until the parent confirms it has taken the boundary sample, so no traffic flows
        // between the sample and the start of the clock.
        Console.Error.WriteLine($"[mark] begin {concurrency}");
        await Console.Error.FlushAsync();
        await WaitForAckAsync();

        var wall = Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async c =>
        {
            var conn = connections[c];
            foreach (var wire in payloads[c])
            {
                var sw = Stopwatch.StartNew();
                var ok = await SendOneAsync(conn, wire);
                sw.Stop();
                attempts.Add(new Attempt(sw.Elapsed.TotalMilliseconds, ok, wire.Length));
            }
        }));

        wall.Stop();

        Console.Error.WriteLine("[mark] end");
        await Console.Error.FlushAsync();
        await WaitForAckAsync();

        process.Refresh();
        var clientCpu = process.TotalProcessorTime - cpuAtStart;

        // Teardown happens after the clock stops.
        foreach (var conn in connections) await conn.DisposeAsync();

        var all = attempts.ToArray();
        var accepted = all.Count(a => a.Accepted);
        var bytes = all.Where(a => a.Accepted).Sum(a => (long)a.Bytes);
        var latencies = all.Select(a => a.ElapsedMs).OrderBy(x => x).ToArray();
        var seconds = wall.Elapsed.TotalSeconds;

        return new TrialResult
        {
            Concurrency = concurrency,
            Accepted = accepted,
            Failed = all.Length - accepted,
            WallSeconds = Math.Round(seconds, 4),
            MessagesPerSecond = seconds > 0 ? Math.Round(accepted / seconds, 2) : 0,
            MegabytesPerSecond = seconds > 0 ? Math.Round(bytes / 1024.0 / 1024.0 / seconds, 2) : 0,
            BytesAccepted = bytes,
            ClientCpuSeconds = Math.Round(clientCpu.TotalSeconds, 4),
            LatencyP50Ms = Percentile(latencies, 50),
            LatencyP95Ms = Percentile(latencies, 95),
            LatencyP99Ms = Percentile(latencies, 99),
        };
    }

    private static double Percentile(double[] sorted, int p)
    {
        if (sorted.Length == 0) return 0;
        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
        return Math.Round(sorted[Math.Clamp(rank, 0, sorted.Length - 1)], 3);
    }
}

/// <summary>Result of a single timed trial.</summary>
internal sealed class TrialResult
{
    public int Concurrency { get; init; }
    public int Accepted { get; init; }
    public int Failed { get; init; }
    public double WallSeconds { get; init; }
    public double MessagesPerSecond { get; init; }
    public double MegabytesPerSecond { get; init; }
    public long BytesAccepted { get; init; }
    public double ClientCpuSeconds { get; init; }
    public double LatencyP50Ms { get; init; }
    public double LatencyP95Ms { get; init; }
    public double LatencyP99Ms { get; init; }

    /// <summary>Server CPU during THIS trial's timed region, sampled by the parent.</summary>
    public double ServerCpuSeconds { get; set; }

    /// <summary>Server CPU as a fraction of its pinned cores, over this trial only.</summary>
    public double ServerCoreUtilisation { get; set; }

    /// <summary>Client CPU as a fraction of its pinned cores. Near 1.0 invalidates the trial.</summary>
    public double ClientCoreUtilisation { get; set; }
}
