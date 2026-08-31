using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Shared raw-TCP SMTP client session for integration tests (TEST_PLAN.md §1.2).
/// Wraps connect / send / read-line / multi-line-response with a default 10 s timeout so a
/// non-responsive server fails the test instead of hanging the suite.
/// </summary>
public sealed class SmtpSession : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpClient _client;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    public int Port { get; }

    private SmtpSession(TcpClient client, int port)
    {
        _client = client;
        Port = port;
        var stream = client.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\r\n" };
    }

    /// <summary>Connects to the server. Bounded by a 10 s timeout.</summary>
    public static async Task<SmtpSession> ConnectAsync(ushort port, IPAddress? address = null)
    {
        var client = new TcpClient();
        using var cts = new CancellationTokenSource(DefaultTimeout);
        try
        {
            await client.ConnectAsync(address ?? IPAddress.Loopback, port).WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            client.Close();
            throw new TimeoutException($"timed out connecting to SMTP server on port {port}");
        }

        return new SmtpSession(client, port);
    }

    /// <summary>Reads one line. Returns null on clean EOF; throws <see cref="TimeoutException"/> after 10 s.</summary>
    public async Task<string?> ReadLineAsync()
    {
        using var cts = new CancellationTokenSource(DefaultTimeout);
        try
        {
            return await _reader.ReadLineAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"timed out waiting for a response line on port {Port}");
        }
    }

    /// <summary>
    /// Reads a full SMTP response: one or more lines where continuation lines have '-' at index 3
    /// and the final line has ' '. Returns all lines including the final one.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReadResponseAsync()
    {
        var lines = new List<string>();
        string? line;
        do
        {
            line = await ReadLineAsync();
            if (line == null) break;
            lines.Add(line);
        } while (line.Length > 3 && line[3] == '-');

        return lines;
    }

    /// <summary>Sends one command line (CRLF-terminated, flushed immediately).</summary>
    public async Task Send(string command)
    {
        using var cts = new CancellationTokenSource(DefaultTimeout);
        try
        {
            _writer.WriteLine(command);
            await _writer.FlushAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"timed out sending command on port {Port}");
        }
    }

    /// <summary>Sends raw bytes without framing — for garbage-input tests.</summary>
    public async Task SendRaw(byte[] bytes)
    {
        using var cts = new CancellationTokenSource(DefaultTimeout);
        try
        {
            await _client.GetStream().WriteAsync(bytes, cts.Token);
            await _client.GetStream().FlushAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"timed out sending raw bytes on port {Port}");
        }
    }

    /// <summary>Abruptly closes the connection with RST (linger 0), simulating a client crash.</summary>
    public void Abort()
    {
        try
        {
            _client.Client.LingerState = new LingerOption(true, 0);
        }
        catch
        {
            // best effort — the socket may already be gone
        }

        _client.Close();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // best effort on dispose
        }

        _reader.Dispose();
        _writer.Dispose();
        _client.Close();
    }
}

/// <summary>Loopback port allocator shared by all integration tests.</summary>
public static class TestPorts
{
    /// <summary>Allocates a free loopback port (bind to 0, read the assigned port, release).</summary>
    public static ushort Allocate()
    {
        var tmp = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            tmp.Start();
            return (ushort)((IPEndPoint)tmp.LocalEndpoint).Port;
        }
        finally
        {
            tmp.Stop();
        }
    }
}
