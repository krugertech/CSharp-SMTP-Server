using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using CSharp_SMTP_Server.Interfaces;
using CSharp_SMTP_Server.Networking;
using CSharp_SMTP_Server.Protocol.Responses;

namespace CSharp_SMTP_Server.Tests.Load;

/// <summary>
/// IMailDelivery that records every delivered message keyed by its <see cref="MessageCorpus.IdHeader"/>,
/// with an optional per-message delay to simulate a real (non-instant) delivery backend.
/// </summary>
/// <remarks>
/// Only the id and the extracted payload are retained, not the whole <see cref="MailTransaction"/>:
/// a 1000-message run holding every transaction (each with a parsed MimeMessage) would measure the
/// harness's allocation behavior as much as the server's.
/// </remarks>
internal sealed class LoadDelivery : IMailDelivery
{
    private readonly TimeSpan _handlerDelay;

    /// <summary>Delivered payload digests, keyed by the sender-stamped load id.</summary>
    internal ConcurrentDictionary<string, string> DeliveredHashes { get; } = new();

    /// <summary>Ids delivered more than once — must stay empty (duplicate delivery is a defect).</summary>
    internal ConcurrentBag<string> Duplicates { get; } = new();

    /// <summary>Deliveries whose body carried no id header at all.</summary>
    internal ConcurrentBag<string> Unidentified { get; } = new();

    /// <summary>Peak number of handlers running at once — evidence sessions actually overlap.</summary>
    internal int PeakConcurrentHandlers;

    private int _currentHandlers;

    internal LoadDelivery(TimeSpan handlerDelay = default) => _handlerDelay = handlerDelay;

    public async Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction transaction, CancellationToken cancellationToken = default)
    {
        var running = Interlocked.Increment(ref _currentHandlers);

        // Lock-free running maximum: retry while our observed value still exceeds the recorded peak.
        var peak = Volatile.Read(ref PeakConcurrentHandlers);
        while (running > peak && Interlocked.CompareExchange(ref PeakConcurrentHandlers, running, peak) != peak)
            peak = Volatile.Read(ref PeakConcurrentHandlers);

        try
        {
            if (_handlerDelay > TimeSpan.Zero)
                await Task.Delay(_handlerDelay, cancellationToken);

            var body = transaction.RawBody;
            var id = MessageCorpus.ExtractId(body);

            if (id == null)
            {
                Unidentified.Add(body.Length > 200 ? body[..200] : body);
                return SmtpDeliveryResult.Ok();
            }

            var hash = MessageCorpus.Hash(MessageCorpus.ExtractPayload(body));
            if (!DeliveredHashes.TryAdd(id, hash))
                Duplicates.Add(id);

            return SmtpDeliveryResult.Ok();
        }
        finally
        {
            Interlocked.Decrement(ref _currentHandlers);
        }
    }

    public Task<UserExistsCodes> DoesUserExist(string emailAddress) =>
        Task.FromResult(UserExistsCodes.DestinationAddressValid);
}

/// <summary>
/// Drives concurrent SMTP sessions against a server and reports throughput, latency and integrity.
/// </summary>
/// <remarks>
/// <para>
/// Two axes are varied independently, because they stress different code:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Concurrency</b> — simultaneous connections. Stresses the accept loop and the
/// <c>Listener.ClientProcessors</c> registration path, which is where this server's one known
/// load-induced crash lived (the unsynchronized List race fixed in <c>df4636e</c>).
/// </description></item>
/// <item><description>
/// <b>Messages per connection</b> — sequential transactions reusing one session. Stresses per-session
/// state reset between transactions, which is where cross-talk and state-bleed bugs would appear.
/// </description></item>
/// </list>
/// </remarks>
internal static class LoadDriver
{
    /// <summary>Builds and starts a server wired to the supplied delivery handler.</summary>
    internal static (SMTPServer Server, ushort Port) StartServer(IMailDelivery delivery, ILogger? logger = null)
    {
        var port = TestPorts.Allocate();
        var server = new SMTPServer(
            new[] { new ListeningParameters(IPAddress.Loopback, new[] { port }, null) },
            TestServers.DefaultOptions(), delivery, logger);
        server.Start();
        return (server, port);
    }

    /// <summary>
    /// Runs <paramref name="concurrency"/> parallel connections, each sending
    /// <paramref name="messagesPerConnection"/> messages drawn round-robin from the corpus.
    /// </summary>
    /// <returns>
    /// The metrics summary plus the id-to-expected-digest map the caller verifies deliveries against.
    /// </returns>
    /// <summary>
    /// I/O timeout used by load sessions, deliberately far above the 10 s protocol-test default.
    /// </summary>
    /// <remarks>
    /// With hundreds of concurrent connections each pushing hundreds of kilobytes, a connection
    /// accepted late can wait tens of seconds simply to be greeted — the machine is saturated and the
    /// work is queued, which is the condition under test, not a failure. At the 10 s default the
    /// harness reported its own client timeout as a server failure at 200+ concurrency.
    /// </remarks>
    internal static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(2);

    internal static async Task<(LoadResult Result, IReadOnlyDictionary<string, string> Expected)> RunAsync(
        string scenario, ushort port, int concurrency, int messagesPerConnection,
        CancellationToken cancellationToken = default)
    {
        var metrics = new LoadMetrics();
        var expected = new ConcurrentDictionary<string, string>();

        async Task RunConnectionAsync(int connectionId)
        {
            SmtpSession? session = null;
            try
            {
                session = await SmtpSession.ConnectAsync(port, timeout: LoadTimeout);

                var greeting = await session.ReadLineAsync();
                if (greeting == null || !greeting.StartsWith("220"))
                {
                    metrics.RecordFailure($"conn {connectionId}: bad greeting '{greeting}'");
                    return;
                }

                await session.Send("EHLO load.client");
                await session.ReadResponseAsync();

                for (var i = 0; i < messagesPerConnection; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var sample = MessageCorpus.Samples[(connectionId + i) % MessageCorpus.Samples.Count];
                    var id = $"c{connectionId}-m{i}";

                    var sw = Stopwatch.StartNew();
                    var accepted = await SendOneAsync(session, sample, id, connectionId, i, metrics);
                    sw.Stop();

                    // Registered only once the server has acknowledged with 250. The integrity check
                    // asserts "everything acknowledged was delivered intact"; registering before the
                    // ACK would also demand delivery of messages the server explicitly refused, or
                    // that were never fully sent, turning a correct rejection into a false corruption
                    // report.
                    if (accepted) expected[id] = sample.Sha256;

                    metrics.Record(sw.Elapsed.TotalMilliseconds, accepted, sample.Bytes);

                    // A session that failed mid-transaction is in an unknown protocol state; abandoning
                    // it here keeps one failure from cascading into every later message on the same
                    // connection and obscuring the original cause.
                    if (!accepted) return;
                }

                await session.Send("QUIT");
                await session.ReadLineAsync();
            }
            catch (Exception e)
            {
                metrics.RecordFailure($"conn {connectionId}: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                if (session != null) await session.DisposeAsync();
            }
        }

        metrics.Start();
        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(RunConnectionAsync));
        metrics.Stop();

        var result = metrics.Summarize(scenario, concurrency, messagesPerConnection);
        foreach (var failure in metrics.Failures.Take(10))
            Console.WriteLine($"[load] {scenario}: {failure}");

        return (result, expected);
    }

    /// <summary>Sends one message over an established session. Returns whether it was accepted (250).</summary>
    private static async Task<bool> SendOneAsync(SmtpSession session, MessageCorpus.Sample sample,
        string id, int connectionId, int messageIndex, LoadMetrics metrics)
    {
        await session.Send($"MAIL FROM:<load{connectionId}@example.com>");
        var mailFrom = await session.ReadLineAsync();
        if (mailFrom == null || !mailFrom.StartsWith("250"))
        {
            metrics.RecordFailure($"conn {connectionId} msg {messageIndex}: MAIL FROM -> '{mailFrom}'");
            return false;
        }

        await session.Send("RCPT TO:<sink@example.org>");
        var rcptTo = await session.ReadLineAsync();
        if (rcptTo == null || !rcptTo.StartsWith("250"))
        {
            metrics.RecordFailure($"conn {connectionId} msg {messageIndex}: RCPT TO -> '{rcptTo}'");
            return false;
        }

        await session.Send("DATA");
        var dataReady = await session.ReadLineAsync();
        if (dataReady == null || !dataReady.StartsWith("354"))
        {
            metrics.RecordFailure($"conn {connectionId} msg {messageIndex}: DATA -> '{dataReady}'");
            return false;
        }

        // The id header goes first so it survives the server prepending its own headers, and so
        // ExtractPayload's Subject: anchor still finds the corpus payload beneath it.
        await session.Send($"{MessageCorpus.IdHeader}: {id}");

        foreach (var line in sample.Payload.Split("\r\n"))
            await session.Send(line);

        await session.Send(".");

        var ack = await session.ReadLineAsync();
        if (ack != null && ack.StartsWith("250")) return true;

        metrics.RecordFailure($"conn {connectionId} msg {messageIndex}: final ACK -> '{ack}'");
        return false;
    }
}
