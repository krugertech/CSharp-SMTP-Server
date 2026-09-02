using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace CSharp_SMTP_Server.Bench;

/// <summary>
/// Entry point. One executable, three roles.
/// </summary>
/// <remarks>
/// <para>
/// <c>serve</c> hosts the SMTP server, <c>generate</c> drives traffic at it, and <c>run</c> is the
/// orchestrator that launches one of each as separate OS processes with independent CPU affinity.
/// The separation is the entire point: with generator and server in one process, pinning "the
/// server" to a core also pins the client feeding it, and the resulting number describes the pair,
/// not the server.
/// </para>
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: bench <run|serve|generate> [options]");
            return 2;
        }

        return args[0] switch
        {
            "serve" => await ServerHost.RunAsync(
                port: ushort.Parse(Arg(args, "--port") ?? "0"),
                handler: Arg(args, "--handler") ?? "drain"),
            "generate" => await GenerateAsync(args),
            "run" => await Orchestrator.RunAsync(args),
            _ => Fail($"unknown role '{args[0]}'"),
        };
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    /// <summary>Reads a <c>--name value</c> argument.</summary>
    internal static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Allocates a free loopback TCP port.</summary>
    internal static ushort FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Generator role: warm up, then run repeated trials across a concurrency ladder at fixed total
    /// bytes, and emit the results as JSON on stdout.
    /// </summary>
    private static async Task<int> GenerateAsync(string[] args)
    {
        var port = ushort.Parse(Arg(args, "--port")!);
        var totalMessages = int.Parse(Arg(args, "--messages") ?? "120");
        var trials = int.Parse(Arg(args, "--trials") ?? "3");
        var ladder = (Arg(args, "--concurrency") ?? "1,2,4,8,16,32,64")
            .Split(',').Select(int.Parse).ToArray();

        // Warm-up: pay JIT, thread-pool growth and socket setup before anything is recorded.
        // Sized to actually move bytes through the DATA path, not just touch it.
        for (var w = 0; w < 2; w++)
            await Generator.RunTrialAsync(port, concurrency: 8, totalMessages: 80);

        var results = new List<TrialResult>();

        foreach (var concurrency in ladder)
        {
            // One discarded trial per rung: the thread pool and socket pool re-stabilise whenever
            // the connection count changes, and that transient lands entirely in the first trial.
            await Generator.RunTrialAsync(port, concurrency, totalMessages);

            for (var t = 0; t < trials; t++)
            {
                var result = await Generator.RunTrialAsync(port, concurrency, totalMessages);
                results.Add(result);
                await Console.Error.WriteLineAsync(
                    $"[bench] conc={concurrency,-4} trial={t + 1}/{trials} " +
                    $"{result.MegabytesPerSecond,7:N2} MB/s  {result.MessagesPerSecond,6:N1} msg/s  " +
                    $"fail={result.Failed}  clientCpu={result.ClientCpuSeconds:N2}s");
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(results));
        return 0;
    }
}
