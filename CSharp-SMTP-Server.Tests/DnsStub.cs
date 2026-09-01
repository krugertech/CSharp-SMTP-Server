using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Minimal UDP DNS stub for SPF/DMARC tests (see TESTING.md). Answers TXT/A/AAAA/MX/PTR queries
/// from in-memory tables on a loopback ephemeral port so SpfValidator / DmarcValidator can be tested
/// deterministically without internet access. ServerOptions.DnsServerEndpoint points at it.
///
/// Wire behavior verified against zabszk.DnsClient 1.0.1 (scratch capture): plain UDP, no EDNS, one
/// query per datagram, sequential retries (5 × 500 ms) on silence; responses are matched by ID and
/// the question section is echoed back verbatim. Names in answers are written in full form (no
/// compression). The stub never sets the TC bit, so DnsClient's TCP fallback is never triggered.
///
/// NOTE: zabszk.DnsClient only parses TXT records whose RDATA is a single character-string; responses
/// with multiple strings per record are silently dropped (pinned by SpfValidatorTests — Q11).
/// </summary>
public sealed class DnsStub : IDisposable
{
    private readonly Socket _socket;
    private readonly Thread _thread;
    private volatile bool _stop;

    private readonly object _lock = new();
    private readonly Dictionary<string, string[]> _txt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[][]> _rawTxt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPAddress[]> _a = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPAddress[]> _aaaa = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (ushort Pref, string Exchange)[]> _mx = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IPAddress, string> _ptr = new();
    private readonly HashSet<string> _servfail = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _nxdomain = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Name, ushort QType)> _queries = new();

    /// <summary>Ephemeral loopback UDP port the stub listens on.</summary>
    public int Port { get; }

    public DnsStub()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        _thread = new Thread(Serve) { IsBackground = true, Name = "DnsStub" };
        _thread.Start();
    }

    #region Configuration (thread-safe; may be called while queries are in flight)

    /// <summary>Registers TXT records for a domain. Each string becomes one TXT RR with a single character-string.</summary>
    public void AddTxt(string domain, params string[] records)
    {
        lock (_lock) _txt[domain] = records;
    }

    /// <summary>
    /// Registers raw TXT RDATA for a domain (each byte[] is one TXT RR). Used to emit multi-string
    /// character-strings — which zabszk.DnsClient silently drops (Q11 pin).
    /// </summary>
    public void AddRawTxt(string domain, params byte[][] rdatas)
    {
        lock (_lock) _rawTxt[domain] = rdatas;
    }

    /// <summary>Registers A records for a domain.</summary>
    public void AddA(string domain, params IPAddress[] addresses)
    {
        lock (_lock) _a[domain] = addresses;
    }

    /// <summary>Registers AAAA records for a domain.</summary>
    public void AddAAAA(string domain, params IPAddress[] addresses)
    {
        lock (_lock) _aaaa[domain] = addresses;
    }

    /// <summary>Registers MX records for a domain (preference + exchange host).</summary>
    public void AddMx(string domain, params (ushort Pref, string Exchange)[] records)
    {
        lock (_lock) _mx[domain] = records;
    }

    /// <summary>Registers a PTR answer: reverse lookups of clientIp return domainName.</summary>
    public void AddPtr(IPAddress clientIp, string domainName)
    {
        lock (_lock) _ptr[clientIp] = domainName;
    }

    /// <summary>Makes any query for these names fail with RCODE 2 (SERVFAIL).</summary>
    public void SetServFail(params string[] domains)
    {
        lock (_lock) foreach (var d in domains) _servfail.Add(d);
    }

    /// <summary>Makes queries for these names fail with RCODE 3 (NXDOMAIN).</summary>
    public void SetNxDomain(params string[] domains)
    {
        lock (_lock) foreach (var d in domains) _nxdomain.Add(d);
    }

    #endregion

    #region Observation

    /// <summary>Total number of queries received since construction.</summary>
    public int QueryCount
    {
        get { lock (_lock) return _queries.Count; }
    }

    /// <summary>Snapshot of all (name, qtype) pairs received so far — for asserting lookup sequences.</summary>
    public IReadOnlyList<(string Name, ushort QType)> Queries
    {
        get { lock (_lock) return _queries.ToArray(); }
    }

    #endregion

    private void Serve()
    {
        var rxbuf = new byte[65536];
        // ReceiveFrom requires a non-null initial endpoint; it is overwritten with the actual peer.
        EndPoint peer = new IPEndPoint(IPAddress.Any, 0);

        while (!_stop)
        {
            int n;
            try
            {
                n = _socket.ReceiveFrom(new ArraySegment<byte>(rxbuf), SocketFlags.None, ref peer);
            }
            catch (Exception)
            {
                return; // socket closed by Dispose — normal shutdown
            }

            var query = rxbuf.Take(n).ToArray(); // exact copy of the datagram
            byte[]? response = TryBuildResponse(query);
            if (response != null)
            {
                try { _socket.SendTo(response, peer); }
                catch (Exception) { /* client went away — ignore */ }
            }
        }
    }

    private byte[]? TryBuildResponse(byte[] q)
    {
        // --- parse the question section: 12-byte header + QNAME + QTYPE(2) + QCLASS(2) ---
        if (q.Length < 17) return null;
        int off = 12;
        var labels = new List<string>();
        while (off < q.Length && q[off] != 0)
        {
            int len = q[off++];
            if (off + len > q.Length) return null;
            labels.Add(Encoding.ASCII.GetString(q, off, len));
            off += len;
        }

        if (off >= q.Length - 4) return null; // no terminator / truncated question
        off++; // zero byte
        ushort qtype = (ushort)(q[off] << 8 | q[off + 1]);
        var name = string.Join(".", labels);

        // --- decide the answer under lock, snapshot everything we need ---
        int rcode;
        List<byte[]> txtRdatas = new();
        IPAddress[] aRecords = Array.Empty<IPAddress>();
        (ushort Pref, string Exchange)[] mxRecords = Array.Empty<(ushort, string)>();
        string? ptrDomain = null;

        lock (_lock)
        {
            _queries.Add((name, qtype));

            if (_servfail.Contains(name)) rcode = 2;
            else if (_nxdomain.Contains(name)) rcode = 3;
            else
            {
                rcode = 0;
                switch (qtype)
                {
                    case 16: // TXT
                        if (_txt.TryGetValue(name, out var txts))
                            foreach (var t in txts) txtRdatas.Add(TxtString(t));
                        if (_rawTxt.TryGetValue(name, out var raws))
                            txtRdatas.AddRange(raws);
                        break;

                    case 1: // A
                        _a.TryGetValue(name, out aRecords);
                        break;

                    case 28: // AAAA
                        _aaaa.TryGetValue(name, out aRecords);
                        break;

                    case 15: // MX
                        _mx.TryGetValue(name, out mxRecords);
                        break;

                    case 12: // PTR — key by the IP encoded in the reverse name
                        if (TryParseReverseName(name, out var ip) && _ptr.TryGetValue(ip, out ptrDomain))
                            ;
                        break;
                }
            }
        }

        // --- build RRs ---
        var rrs = new List<(ushort Type, byte[] Rdata)>();
        if (rcode == 0)
        {
            foreach (var t in txtRdatas) rrs.Add(((ushort)16, t));
            foreach (var a in aRecords)
                rrs.Add(qtype == 28 ? ((ushort)28, a.GetAddressBytes()) : ((ushort)1, a.GetAddressBytes()));
            foreach (var (pref, exchange) in mxRecords)
                rrs.Add(((ushort)15, MxRdata(pref, exchange)));
            if (ptrDomain != null) rrs.Add(((ushort)12, EncodeName(ptrDomain)));
        }

        return BuildResponse(q, (ushort)(0x8180 | rcode), rrs);
    }

    private static byte[] TxtString(string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        if (bytes.Length > 255) throw new ArgumentException("TXT character-string exceeds 255 bytes");
        return [.. new byte[] { (byte)bytes.Length }, .. bytes];
    }

    private static byte[] MxRdata(ushort pref, string exchange) =>
        [.. new byte[] { (byte)(pref >> 8), (byte)pref }, .. EncodeName(exchange)];

    /// <summary>Encodes a domain name in full form (no compression), trailing root label included.</summary>
    private static byte[] EncodeName(string name)
    {
        var list = new List<byte>();
        foreach (var label in name.Split('.'))
        {
            if (label.Length > 63) throw new ArgumentException("DNS label exceeds 63 bytes");
            list.Add((byte)label.Length);
            list.AddRange(Encoding.ASCII.GetBytes(label));
        }

        list.Add(0);
        return [.. list];
    }

    /// <summary>Parses an in-addr.arpa / ip6.arpa reverse name back into the IP address it encodes.</summary>
    private static bool TryParseReverseName(string name, out IPAddress ip)
    {
        ip = null!;
        var labels = name.Split('.');

        if (name.EndsWith(".in-addr.arpa", StringComparison.OrdinalIgnoreCase))
        {
            // last 5 labels are "x.x.x.x.in-addr.arpa" with octets in reverse order
            if (labels.Length < 6) return false;
            var octets = new byte[4];
            for (var i = 0; i < 4; i++)
                if (!byte.TryParse(labels[labels.Length - 5 + i], out octets[i])) return false;

            ip = new IPAddress(octets);
            return true;
        }

        if (name.EndsWith(".ip6.arpa", StringComparison.OrdinalIgnoreCase))
        {
            // 32 hex nibble labels in reverse order, most-significant nibble first within each byte
            var nibbles = labels.Take(32).Reverse().ToArray();
            if (nibbles.Length != 32 || nibbles.Any(l => l.Length != 1)) return false;

            var bytes = new byte[16];
            for (var i = 0; i < 16; i++)
                if (!byte.TryParse(new string(nibbles[i * 2][0], nibbles[i * 2 + 1][0]), out bytes[i])) return false;

            ip = new IPAddress(bytes);
            return true;
        }

        return false;
    }

    /// <summary>Builds a full DNS response: echoed ID + question, then the answer RRs.</summary>
    private static byte[] BuildResponse(byte[] query, ushort flags, List<(ushort Type, byte[] Rdata)> rrs)
    {
        var resp = new List<byte>();
        resp.AddRange(query.Take(2));                 // transaction ID (echoed)
        resp.Add((byte)(flags >> 8)); resp.Add((byte)flags);
        resp.AddRange(new byte[2] { 0, 1 });          // QDCOUNT = 1
        resp.AddRange(BE16((ushort)rrs.Count));       // ANCOUNT
        resp.AddRange(new byte[4]);                   // NSCOUNT / ARCOUNT = 0
        resp.AddRange(query.Skip(12));                // echo the question section verbatim

        foreach (var (type, rdata) in rrs)
        {
            resp.AddRange(ExtractQuestionName(query));
            resp.AddRange(BE16(type));
            resp.AddRange(new byte[] { 0x01, 0x00 }); // class IN
            resp.AddRange(new byte[] { 0, 0, 0, 60 }); // TTL = 60 s
            resp.AddRange(BE16((ushort)rdata.Length));
            resp.AddRange(rdata);
        }

        return [.. resp];
    }

    private static byte[] BE16(ushort v) => new byte[] { (byte)(v >> 8), (byte)v };

    /// <summary>Re-encodes the question name in full form for use as the answer RR owner name.</summary>
    private static byte[] ExtractQuestionName(byte[] query)
    {
        var list = new List<byte>();
        int off = 12;
        while (query[off] != 0)
        {
            list.AddRange(query.Skip(off).Take(query[off] + 1));
            off += query[off] + 1;
        }

        list.Add(0);
        return [.. list];
    }

    public void Dispose()
    {
        _stop = true;
        try { _socket.Close(); } catch (Exception) { /* already closed */ }
    }
}
