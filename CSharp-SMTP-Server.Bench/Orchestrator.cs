using System.Diagnostics;
using System.Text.Json;

namespace CSharp_SMTP_Server.Bench;

/// <summary>
/// Launches a server process and a generator process with independent CPU affinity, then joins
/// their reports.
/// </summary>
/// <remarks>
/// <para>
/// Affinity is applied per process via <see cref="Process.ProcessorAffinity"/>. The server is
/// pinned to the cores under study; the generator is pinned to a disjoint set, sized generously so
/// it is not the limiting side. Because the sets are disjoint, a scaling curve taken by varying
/// only the server mask is attributable to the server.
/// </para>
/// <para>
/// The generator is also asked to report its own CPU. If generator CPU approaches its core budget,
/// the client saturated and the measurement is invalid — that check is what the previous harness
/// could not perform, since the two sides shared a process and a clock.
/// </para>
/// </remarks>
internal static class Orchestrator
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var serverMask = Convert.ToInt64(Program.Arg(args, "--server-affinity") ?? "1", 16);
        var clientMask = Convert.ToInt64(Program.Arg(args, "--client-affinity") ?? "ff00", 16);
        var totalMessages = Program.Arg(args, "--messages") ?? "120";
        var trials = Program.Arg(args, "--trials") ?? "3";
        var ladder = Program.Arg(args, "--concurrency") ?? "1,2,4,8,16,32,64";
        var handler = Program.Arg(args, "--handler") ?? "drain";
        var label = Program.Arg(args, "--label") ?? "run";
        var outPath = Program.Arg(args, "--out");

        var port = Program.FreePort();
        var exePath = Environment.ProcessPath!;
        var assembly = System.Reflection.Assembly.GetEntryAssembly()!.Location;

        // The host may be `dotnet bench.dll` or a published apphost; reproduce whichever launched us.
        // cores sizes the child's runtime BEFORE it starts: Environment.ProcessorCount, Server GC
        // heap count and thread-pool sizing are all fixed at CLR startup, so setting affinity after
        // Process.Start would leave a runtime provisioned for all 16 logical CPUs merely restricted
        // by a scheduler mask — an oversubscribed runtime, not one configured for N cores.
        static ProcessStartInfo Spawn(string exePath, string assembly, string arguments, int cores)
        {
            var isDotnetHost = Path.GetFileNameWithoutExtension(exePath)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase);

            var info = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = isDotnetHost ? $"\"{assembly}\" {arguments}" : arguments,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            info.Environment["DOTNET_PROCESSOR_COUNT"] = cores.ToString();
            return info;
        }

        var serverCores = System.Numerics.BitOperations.PopCount((ulong)serverMask);
        var clientCores = System.Numerics.BitOperations.PopCount((ulong)clientMask);

        var serverInfo = Spawn(exePath, assembly, $"serve --port {port} --handler {handler}", serverCores);
        using var server = Process.Start(serverInfo)!;
        server.ProcessorAffinity = (nint)serverMask;

        // Wait for READY so connect latency never includes server startup.
        var ready = await server.StandardOutput.ReadLineAsync();
        if (ready != "READY")
        {
            var err = await server.StandardError.ReadToEndAsync();
            Console.Error.WriteLine($"server failed to start: '{ready}' {err}");
            return 1;
        }

        var clientInfo = Spawn(exePath, assembly,
            $"generate --port {port} --messages {totalMessages} --trials {trials} --concurrency {ladder}", clientCores);
        using var client = Process.Start(clientInfo)!;
        client.ProcessorAffinity = (nint)clientMask;

        var trafficWall = System.Diagnostics.Stopwatch.StartNew();

        // Per-trial server CPU, sampled on the generator's [mark] boundaries. Each measured window
        // covers exactly one timed DATA region, so utilisation can be quoted alongside the
        // throughput from that same trial instead of being averaged over warm-up and idle.
        var serverCpuPerTrial = new List<double>();
        var trialWallPerTrial = new List<double>();

        // Ask the server to sample its own CPU counter and return the value. Synchronous, so the
        // sample is taken at the trial boundary rather than whenever the parent happens to be
        // scheduled.
        async Task<double> MarkAsync()
        {
            await server.StandardInput.WriteLineAsync("MARK");
            await server.StandardInput.FlushAsync();

            while (await server.StandardOutput.ReadLineAsync() is { } line)
                if (line.StartsWith("CPU "))
                    return double.Parse(line[4..], System.Globalization.CultureInfo.InvariantCulture);

            return double.NaN;
        }

        var progress = Task.Run(async () =>
        {
            var markCpu = 0.0;
            var markWall = System.Diagnostics.Stopwatch.StartNew();

            while (await client.StandardError.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("[mark] begin"))
                {
                    markCpu = await MarkAsync();
                    markWall.Restart();
                    await client.StandardInput.WriteLineAsync("ACK");
                    await client.StandardInput.FlushAsync();
                    continue;
                }

                if (line == "[mark] end")
                {
                    markWall.Stop();
                    serverCpuPerTrial.Add(await MarkAsync() - markCpu);
                    trialWallPerTrial.Add(markWall.Elapsed.TotalSeconds);
                    await client.StandardInput.WriteLineAsync("ACK");
                    await client.StandardInput.FlushAsync();
                    continue;
                }

                Console.Error.WriteLine(line);
            }
        });

        var trialJson = await client.StandardOutput.ReadToEndAsync();
        await client.WaitForExitAsync();
        await progress;

        trafficWall.Stop();

        // Whole-run totals are kept only as a secondary sanity check. The per-trial figures above
        // are the ones that may be quoted beside a throughput number.
        var serverCpuDuringTraffic = TimeSpan.FromSeconds(serverCpuPerTrial.Sum());

        client.Refresh();
        var clientCpuTotal = client.TotalProcessorTime;

        // Ask the server for its accounting, then shut it down.
        await server.StandardInput.WriteLineAsync("STOP");
        await server.StandardInput.FlushAsync();

        string? resultLine = null;
        while (await server.StandardOutput.ReadLineAsync() is { } line)
            if (line.StartsWith("RESULT "))
            {
                resultLine = line["RESULT ".Length..];
                break;
            }

        await server.WaitForExitAsync();

        var trialResults = JsonSerializer.Deserialize<List<TrialResult>>(trialJson) ?? new();
        var serverReport = resultLine == null
            ? null
            : JsonSerializer.Deserialize<ServerReport>(resultLine);

        // The markers cover warm-ups and discarded trials too; the recorded trials are the tail.
        var offset = serverCpuPerTrial.Count - trialResults.Count;
        for (var i = 0; i < trialResults.Count; i++)
        {
            var k = offset + i;
            if (k < 0 || k >= serverCpuPerTrial.Count) continue;

            trialResults[i].ServerCpuSeconds = Math.Round(serverCpuPerTrial[k], 4);
            trialResults[i].ServerCoreUtilisation = trialWallPerTrial[k] > 0
                ? Math.Round(serverCpuPerTrial[k] / (trialWallPerTrial[k] * serverCores), 3)
                : 0;
            trialResults[i].ClientCoreUtilisation = trialWallPerTrial[k] > 0
                ? Math.Round(trialResults[i].ClientCpuSeconds / (trialWallPerTrial[k] * clientCores), 3)
                : 0;
        }

        // Integrity gate. A regression that truncates, drops or duplicates bodies while still
        // answering 250 would otherwise show up as a faster benchmark, which is the one result a
        // load harness must never report.
        var acceptedTotal = trialResults.Sum(t => (long)t.Accepted);
        var integrityErrors = new List<string>();

        if (serverReport != null)
        {
            if (serverReport.Duplicates > 0)
                integrityErrors.Add($"{serverReport.Duplicates} duplicate delivery(ies)");
            if (serverReport.Unidentified > 0)
                integrityErrors.Add($"{serverReport.Unidentified} message(s) with no id header");
            if (serverReport.DistinctIds != serverReport.Delivered)
                integrityErrors.Add($"distinct ids {serverReport.DistinctIds} != delivered {serverReport.Delivered}");
            if (serverReport.Delivered < acceptedTotal)
                integrityErrors.Add($"delivered {serverReport.Delivered} < accepted {acceptedTotal}");
            if (serverReport.ProcessorCount != serverCores)
                integrityErrors.Add($"server runtime saw {serverReport.ProcessorCount} processors, expected {serverCores}");
        }

        var report = new BenchReport
        {
            Label = label,
            ServerAffinityMask = $"0x{serverMask:x}",
            ClientAffinityMask = $"0x{clientMask:x}",
            ServerCores = serverCores,
            ClientCores = clientCores,
            DeliveryHandler = handler,
            Trials = trialResults,
            Server = serverReport,
            ServerCpuSecondsDuringTraffic = Math.Round(serverCpuDuringTraffic.TotalSeconds, 3),
            ClientCpuSecondsTotal = Math.Round(clientCpuTotal.TotalSeconds, 3),
            TrafficWallSeconds = Math.Round(trafficWall.Elapsed.TotalSeconds, 3),

            // Fraction of its core budget each side burned. If the client figure approaches 1.0 the
            // generator was the constraint and the server number is a floor, not a ceiling.
            ServerCoreUtilisation = Math.Round(
                serverCpuDuringTraffic.TotalSeconds / (trafficWall.Elapsed.TotalSeconds * Math.Max(1, serverCores)), 3),
            ClientCoreUtilisation = Math.Round(
                clientCpuTotal.TotalSeconds / (trafficWall.Elapsed.TotalSeconds * Math.Max(1, clientCores)), 3),

            IntegrityErrors = integrityErrors,
            Valid = integrityErrors.Count == 0,
        };

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });

        if (outPath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            await File.WriteAllTextAsync(outPath, json);
            Console.Error.WriteLine($"[bench] wrote {outPath}");
        }

        Console.WriteLine(json);
        return 0;
    }
}

/// <summary>Full report for one orchestrated run.</summary>
internal sealed class BenchReport
{
    public string Label { get; init; } = "";
    public string ServerAffinityMask { get; init; } = "";
    public string ClientAffinityMask { get; init; } = "";
    public int ServerCores { get; init; }
    public int ClientCores { get; init; }
    public string DeliveryHandler { get; init; } = "";
    public List<TrialResult> Trials { get; init; } = new();
    public ServerReport? Server { get; init; }
    public double ServerCpuSecondsDuringTraffic { get; init; }
    public double ClientCpuSecondsTotal { get; init; }
    public double TrafficWallSeconds { get; init; }

    /// <summary>Server CPU as a fraction of its pinned core budget.</summary>
    public double ServerCoreUtilisation { get; init; }

    /// <summary>Client CPU as a fraction of its pinned core budget. Near 1.0 invalidates the run.</summary>
    public double ClientCoreUtilisation { get; init; }

    /// <summary>Reasons this run must not be quoted. Empty when the run is valid.</summary>
    public List<string> IntegrityErrors { get; init; } = new();

    /// <summary>False when any integrity or configuration invariant failed.</summary>
    public bool Valid { get; init; }
}
