using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using CSharp_SMTP_Server.Interfaces;
using CSharp_SMTP_Server.Networking;
using CSharp_SMTP_Server.Protocol.Responses;

namespace CSharp_SMTP_Server.Bench;

/// <summary>
/// Delivery handler that does nothing but return 250.
/// </summary>
/// <remarks>
/// This is what "no-op" has to mean for a throughput ceiling. The in-test harness calls its handler
/// a no-op while, inside the ACK-gated path, it copies the transaction into a growing MemoryStream,
/// calls ToArray, extracts a second full array, and computes SHA-256 — per message. Delivery is
/// ACK-gated, so every one of those bytes and allocations is charged to server completion time.
/// Measuring that and calling it the library's ceiling overstates the server's cost and, because
/// hashing parallelises well, distorts scaling in particular.
/// </remarks>
internal sealed class NoopDelivery : IMailDelivery
{
    internal long Delivered;

    public Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction transaction, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Delivered);
        return Task.FromResult(SmtpDeliveryResult.Ok());
    }

    public Task<UserExistsCodes> DoesUserExist(string emailAddress) =>
        Task.FromResult(UserExistsCodes.DestinationAddressValid);
}

/// <summary>
/// Delivery handler that reads the body stream to its end and discards it, without hashing.
/// </summary>
/// <remarks>
/// This is the honest floor for a useful server, and the headline configuration. With the pure
/// no-op above, the server writes each body into its store and then nobody ever reads it, so the
/// ingestion path is only half-exercised and server CPU stays near idle — a number that flatters
/// the library by measuring buffer-and-discard. Any real handler at minimum reads the message once,
/// which is what this does.
/// </remarks>
internal sealed class DrainDelivery : IMailDelivery
{
    internal long Delivered;

    /// <summary>Total body bytes actually read, for reconciliation against bytes sent.</summary>
    internal long BytesRead;

    /// <summary>Distinct message ids seen — detects drops and duplicates.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _ids = new();

    /// <summary>Messages whose id header was missing, or that arrived duplicated.</summary>
    internal long Unidentified;
    internal long Duplicates;

    internal int DistinctIds => _ids.Count;

    private const int BufferSize = 64 * 1024;

    public async Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction transaction, CancellationToken cancellationToken = default)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var stream = transaction.GetBodyStream();

            // Fill a prefix before scanning for the id. A single ReadAsync may return far less than
            // the buffer — the server splices its own prepended headers in ahead of the body, so the
            // first read can be just that header chunk — and the id line would then be missed.
            // Scanning a bounded prefix keeps this a drain rather than a full parse, while still
            // making a truncated, dropped or duplicated message impossible to mistake for a fast one.
            var prefix = 0;
            int n;
            while (prefix < BufferSize &&
                   (n = await stream.ReadAsync(buffer.AsMemory(prefix, BufferSize - prefix), cancellationToken)) > 0)
                prefix += n;

            var total = (long)prefix;

            var id = prefix > 0 ? ExtractId(buffer.AsSpan(0, prefix)) : null;

            if (id == null) Interlocked.Increment(ref Unidentified);
            else if (!_ids.TryAdd(id, 0)) Interlocked.Increment(ref Duplicates);

            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
                total += read;

            Interlocked.Add(ref BytesRead, total);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        Interlocked.Increment(ref Delivered);
        return SmtpDeliveryResult.Ok();
    }

    /// <summary>Reads the X-Load-Id value from the first line, or null if it is not there.</summary>
    private static string? ExtractId(ReadOnlySpan<byte> prefix)
    {
        var marker = "X-Load-Id: "u8;
        var at = prefix.IndexOf(marker);
        if (at < 0) return null;

        var rest = prefix[(at + marker.Length)..];
        var eol = rest.IndexOf((byte)'\r');
        if (eol < 0) eol = rest.IndexOf((byte)'\n');
        if (eol < 0) return null;

        return System.Text.Encoding.ASCII.GetString(rest[..eol]);
    }

    public Task<UserExistsCodes> DoesUserExist(string emailAddress) =>
        Task.FromResult(UserExistsCodes.DestinationAddressValid);
}

/// <summary>
/// Delivery handler that drains and hashes the body, matching what the in-test harness does.
/// Used only to quantify the integrity-check overhead as its own number.
/// </summary>
internal sealed class HashingDelivery : IMailDelivery
{
    internal long Delivered;

    public async Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction transaction, CancellationToken cancellationToken = default)
    {
        using (var stream = transaction.GetBodyStream())
        using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer, cancellationToken);
            var body = buffer.ToArray();
            _ = Convert.ToHexString(SHA256.HashData(body));
        }

        Interlocked.Increment(ref Delivered);
        return SmtpDeliveryResult.Ok();
    }

    public Task<UserExistsCodes> DoesUserExist(string emailAddress) =>
        Task.FromResult(UserExistsCodes.DestinationAddressValid);
}

/// <summary>
/// Hosts a server and reports its own CPU consumption, so throughput can be attributed to the
/// server process rather than inferred from a wall clock that spans both sides.
/// </summary>
internal static class ServerHost
{
    /// <summary>
    /// Starts a server on <paramref name="port"/> and blocks until the parent signals shutdown by
    /// closing stdin. Prints a single JSON line of server-side CPU accounting on exit.
    /// </summary>
    internal static async Task<int> RunAsync(ushort port, string handler)
    {
        IMailDelivery delivery = handler switch
        {
            "noop" => new NoopDelivery(),
            "hashing" => new HashingDelivery(),
            _ => new DrainDelivery(),
        };

        var server = new SMTPServer(
            new[] { new ListeningParameters(IPAddress.Loopback, new[] { port }, null) },
            new ServerOptions(false, false, null) { ServerName = "bench.local" },
            delivery, loggerInterface: null);

        server.Start();

        var process = Process.GetCurrentProcess();

        // Refresh before sampling: Process caches its counters, so reading TotalProcessorTime on a
        // freshly-obtained handle without a Refresh yields a stale baseline and the delta comes out
        // wrong (and can even come out negative-biased against longer runs).
        process.Refresh();
        var cpuAtStart = process.TotalProcessorTime;
        var wall = Stopwatch.StartNew();

        // Ready handshake: the generator waits for this line before connecting, so connect latency
        // never includes server startup.
        Console.WriteLine("READY");
        Console.Out.Flush();

        // Command loop. MARK makes the server sample its OWN CPU counter at a trial boundary and
        // report it synchronously. Having the parent sample instead races: the parent learns of the
        // boundary by reading the generator's stderr asynchronously, so its Refresh() lands at an
        // arbitrary point after the trial actually started or ended, and short trials then report
        // physically impossible utilisation (single-digit percent while sustaining 600+ MB/s).
        await Task.Run(() =>
        {
            while (Console.In.ReadLine() is { } line)
            {
                if (line == "STOP") break;

                if (line == "MARK")
                {
                    process.Refresh();
                    Console.WriteLine($"CPU {(process.TotalProcessorTime - cpuAtStart).TotalSeconds:R}");
                    Console.Out.Flush();
                }
            }
        });

        wall.Stop();
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuAtStart;

        var delivered = delivery switch
        {
            NoopDelivery n => n.Delivered,
            DrainDelivery d => d.Delivered,
            HashingDelivery h => h.Delivered,
            _ => 0,
        };

        var drain = delivery as DrainDelivery;

        Console.WriteLine("RESULT " + System.Text.Json.JsonSerializer.Serialize(new ServerReport
        {
            Delivered = delivered,
            BytesRead = drain?.BytesRead ?? 0,
            DistinctIds = drain?.DistinctIds ?? 0,
            Duplicates = drain?.Duplicates ?? 0,
            Unidentified = drain?.Unidentified ?? 0,
            ProcessorCount = Environment.ProcessorCount,
            ServerGc = System.Runtime.GCSettings.IsServerGC,
            ServerCpuSeconds = Math.Round(cpu.TotalSeconds, 4),
            ServerWallSeconds = Math.Round(wall.Elapsed.TotalSeconds, 4),
            PeakWorkingSetMb = Math.Round(process.PeakWorkingSet64 / 1024.0 / 1024.0, 1),
            Gen0 = GC.CollectionCount(0),
            Gen1 = GC.CollectionCount(1),
            Gen2 = GC.CollectionCount(2),
        }));
        Console.Out.Flush();

        server.Dispose();
        return 0;
    }
}

/// <summary>Server-side accounting emitted by the host process.</summary>
internal sealed class ServerReport
{
    public long Delivered { get; init; }

    /// <summary>Body bytes actually read by the handler, for reconciliation against bytes sent.</summary>
    public long BytesRead { get; init; }

    /// <summary>Distinct ids seen. Must equal <see cref="Delivered"/> for a valid run.</summary>
    public int DistinctIds { get; init; }
    public long Duplicates { get; init; }
    public long Unidentified { get; init; }

    /// <summary>Runtime's view of available parallelism — must match the pinned core count.</summary>
    public int ProcessorCount { get; init; }
    public bool ServerGc { get; init; }
    public double ServerCpuSeconds { get; init; }
    public double ServerWallSeconds { get; init; }
    public double PeakWorkingSetMb { get; init; }
    public int Gen0 { get; init; }
    public int Gen1 { get; init; }
    public int Gen2 { get; init; }
}
