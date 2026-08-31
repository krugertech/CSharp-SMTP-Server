using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Minimal single-file HTTP server on a loopback ephemeral port. Used to serve the Public Suffix List
/// so DMARC tests never touch the internet (DmarcValidator downloads it from ServerOptions.PublicSuffixList).
/// </summary>
internal sealed class LocalHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Thread _thread;
    private readonly byte[] _payload;
    private volatile bool _stop;

    public string Url { get; }

    public LocalHttpServer(string content)
    {
        var tmp = new TcpListener(IPAddress.Loopback, 0);
        tmp.Start();
        var port = ((IPEndPoint)tmp.LocalEndpoint).Port;
        tmp.Stop();

        _payload = Encoding.UTF8.GetBytes(content);
        Url = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(Url);
        _listener.Start();
        _thread = new Thread(Serve) { IsBackground = true, Name = "LocalHttpServer" };
        _thread.Start();
    }

    private void Serve()
    {
        while (!_stop && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = _listener.GetContext();
            }
            catch (Exception)
            {
                break; // listener stopped during shutdown
            }

            try
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/plain";
                ctx.Response.ContentLength64 = _payload.Length;
                ctx.Response.OutputStream.Write(_payload, 0, _payload.Length);
                ctx.Response.Close();
            }
            catch (Exception)
            {
                // client went away — ignore
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        try { _listener.Stop(); } catch (Exception) { /* already stopped */ }
        try { _listener.Close(); } catch (Exception) { /* already closed */ }
    }
}
