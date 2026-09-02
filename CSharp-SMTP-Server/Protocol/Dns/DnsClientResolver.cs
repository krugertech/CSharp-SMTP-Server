using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using CSharp_SMTP_Server.Interfaces;
using DnsClient;
using DnsClient.Protocol;

namespace CSharp_SMTP_Server.Protocol.Dns;

/// <summary>
/// <see cref="IDnsResolver"/> backed by DnsClient.NET, with an in-process TTL-aware response cache.
/// </summary>
/// <remarks>
/// <para>
/// The cache is the reason for the change. Every SPF evaluation issues TXT, MX and A/AAAA queries and
/// recurses through <c>include:</c> and <c>redirect=</c> up to RFC 7208's limit of ten DNS-consuming
/// terms, and DMARC adds a TXT lookup on top. Sustained traffic from a handful of customer domains
/// re-resolved the same provider include-chain on every single message, because the previous client
/// had no cache at all and the only caching in the server was a per-connection map of final SPF
/// verdicts, discarded on close.
/// </para>
/// <para>
/// Internal on purpose. Its constructor takes <c>LookupClientOptions</c>, a DnsClient.NET type, and
/// making that public would put the resolver library back into this package's exported surface — the
/// exact coupling <see cref="IDnsResolver"/> exists to remove. Callers reach the stock resolver
/// through <see cref="SMTPServer.CreateResolver"/> and configure it with project-owned types.
/// </para>
/// </remarks>
internal sealed class DnsClientResolver : IDnsResolver
{
	private readonly LookupClient _client;
	private readonly ILogger? _logger;
	private readonly NegativeCache _negativeCache = new();

	internal DnsClientResolver(LookupClientOptions options, ILogger? logger = null)
	{
		_logger = logger;

		// Caching is the point of this class, so it is set here rather than left to the caller to
		// remember.
		options.UseCache = true;

		// A floor stops a hostile or misconfigured zone publishing TTL=0 to defeat caching entirely and
		// turning every message into a fresh round of lookups.
		options.MinimumCacheTimeout = TimeSpan.FromSeconds(5);

		// The ceiling bounds this cache in TIME only. DnsClient.NET's positive cache has no entry-count
		// limit, and the cache key includes the queried name — chosen by whoever connects. A flood of
		// distinct sender domains therefore grows it, with nothing evicting an entry before it expires.
		// Five minutes keeps the working set bounded by connection rate rather than by uptime, at the
		// cost of re-resolving long-TTL records more often than their TTL requires. This is a
		// mitigation, not a solved problem — see KNOWN_ISSUES.md.
		options.MaximumCacheTimeout = TimeSpan.FromMinutes(5);

		// DnsClient.NET classifies BOTH transient failures and definitive negative answers (NXDOMAIN,
		// and NOERROR carrying no matching records) as "failed results" under this one flag, so it
		// cannot tell them apart. They must not be treated alike here:
		//
		//   - A transient failure must never be cached. SPF reports Temperror for it and DMARC defers
		//     the message with 451 4.7.1, so a cached SERVFAIL would keep deferring a sender that
		//     retries within the window, after resolution had already recovered — the same
		//     stale-transient-error trap the per-connection SPF verdict cache had, one layer down.
		//   - A definitive negative SHOULD be cached. "This domain publishes no SPF or DMARC record" is
		//     the common case for the unauthenticated mail this relay sees, and re-querying it on every
		//     message turns junk traffic into amplified outbound DNS load.
		//
		// So the flag stays off and NegativeCache below handles the definitive half — which also gives
		// that half the hard entry bound the library does not provide.
		options.CacheFailedResults = false;

		// With ThrowDnsErrors off (the default) an error RCODE comes back on the response, which
		// Translate below interprets. Letting the library throw instead would surface a resolver hiccup
		// as an unhandled exception on the connection path.
		options.ThrowDnsErrors = false;

		_client = new LookupClient(options);
	}

	/// <inheritdoc />
	public async Task<DnsQueryResult> QueryAsync(string name, DnsRecordType type)
	{
		if (_negativeCache.TryGet(name, type, out var cached))
			return cached;

		IDnsQueryResponse response;

		try
		{
			response = await _client.QueryAsync(name, ToQueryType(type)).ConfigureAwait(false);
		}
		catch (DnsResponseException ex)
		{
			// Timeout, refused connection, unparseable reply: the question went unanswered, which is
			// not evidence about the domain.
			return Failure(name, type, ex);
		}

		return Translate(response, name, type);
	}

	/// <inheritdoc />
	public async Task<DnsQueryResult> QueryReverseAsync(IPAddress address)
	{
		IDnsQueryResponse response;

		try
		{
			response = await _client.QueryReverseAsync(address).ConfigureAwait(false);
		}
		catch (DnsResponseException ex)
		{
			return Failure(address.ToString(), DnsRecordType.Ptr, ex);
		}

		// Reverse lookups are not negatively cached: they are keyed by the connecting client's address
		// rather than by a sender-chosen name, and only the "ptr" SPF mechanism reaches them.
		return Translate(response, name: null, DnsRecordType.Ptr);
	}

	private DnsQueryResult Failure(string name, DnsRecordType type, Exception? ex)
	{
		// A DNS failure defers every DMARC-protected message with 451, so a broad resolver outage is an
		// availability incident. It must not be silent: the previous client had a logging shim for
		// exactly this, and dropping it would have made such an outage invisible to operators.
		_logger?.LogError($"[DNS] Lookup failed for {type} {name}" + (ex == null ? string.Empty : $": {ex.GetType().Name} - {ex.Message}"));

		return DnsQueryResult.Empty(DnsQueryStatus.Failure);
	}

	private DnsQueryResult Translate(IDnsQueryResponse response, string? name, DnsRecordType type)
	{
		// NXDOMAIN is an answer: the name does not exist, so it publishes nothing. Every other error
		// code means the query failed, and RFC 7208 §4.3 keeps those apart for good reason — see
		// DnsQueryStatus.
		if (response.Header.ResponseCode == DnsHeaderResponseCode.NotExistentDomain)
			return Negative(name, type, DnsQueryResult.Empty(DnsQueryStatus.NameError), response);

		if (response.Header.ResponseCode != DnsHeaderResponseCode.NoError || response.HasError)
			return Failure(name ?? "(reverse lookup)", type, null);

		var records = new List<DnsRecordData>();

		foreach (var record in response.Answers)
		{
			var translated = Translate(record, type);

			if (translated != null)
				records.Add(translated);
		}

		var result = new DnsQueryResult(DnsQueryStatus.Success, records);

		// NOERROR carrying nothing of the requested type is NODATA — also a definitive "publishes no
		// such record", and the answer for every domain with no SPF or DMARC policy.
		return records.Count == 0 ? Negative(name, type, result, response) : result;
	}

	private DnsQueryResult Negative(string? name, DnsRecordType type, DnsQueryResult result, IDnsQueryResponse response)
	{
		if (name != null)
			_negativeCache.Set(name, type, result, NegativeTtl(response));

		return result;
	}

	/// <summary>
	/// How long a definitive negative answer may be reused: RFC 2308 takes the SOA MINIMUM from the
	/// authority section, clamped here to this resolver's own bounds.
	/// </summary>
	private static TimeSpan NegativeTtl(IDnsQueryResponse response)
	{
		double seconds = 60;

		foreach (var authority in response.Authorities)
			if (authority is SoaRecord soa)
			{
				seconds = soa.Minimum;
				break;
			}

		return TimeSpan.FromSeconds(Math.Max(5, Math.Min(seconds, 300)));
	}

	private static DnsRecordData? Translate(DnsResourceRecord record, DnsRecordType type) => record switch
	{
		// RFC 7208 §3.3: a TXT record split into several character-strings is evaluated as their
		// concatenation, with no separator. Joining here rather than in the validators keeps that rule
		// in one place — and the previous client silently dropped multi-string TXT records entirely.
		TxtRecord txt when type == DnsRecordType.Txt =>
			new DnsRecordData(DnsRecordType.Txt, text: string.Concat(txt.Text)),

		MxRecord mx when type == DnsRecordType.Mx =>
			new DnsRecordData(DnsRecordType.Mx, domainName: Trim(mx.Exchange)),

		PtrRecord ptr when type == DnsRecordType.Ptr =>
			new DnsRecordData(DnsRecordType.Ptr, domainName: Trim(ptr.PtrDomainName)),

		// A and AAAA share AddressRecord; the requested type decides which is wanted, since a name can
		// hold both and matching the wrong family would compare an IPv6 answer against an IPv4 client.
		AddressRecord addr when type == DnsRecordType.A && addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork =>
			new DnsRecordData(DnsRecordType.A, address: addr.Address),

		AddressRecord addr when type == DnsRecordType.Aaaa && addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 =>
			new DnsRecordData(DnsRecordType.Aaaa, address: addr.Address),

		_ => null
	};

	/// <summary>
	/// Strips the trailing root dot. DnsClient.NET returns fully-qualified names ("example.com."),
	/// while the SPF comparisons here are against names taken from records and headers, which are not.
	/// </summary>
	private static string Trim(DnsString name) => name.Value.TrimEnd('.');

	private static QueryType ToQueryType(DnsRecordType type) => type switch
	{
		DnsRecordType.A => QueryType.A,
		DnsRecordType.Aaaa => QueryType.AAAA,
		DnsRecordType.Mx => QueryType.MX,
		DnsRecordType.Txt => QueryType.TXT,
		DnsRecordType.Ptr => QueryType.PTR,
		_ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported record type")
	};

	/// <summary>
	/// A size-bounded cache of definitive negative answers (NXDOMAIN and NODATA).
	/// </summary>
	/// <remarks>
	/// Bounded on purpose, and this is the half that most needs it: negative answers are exactly what a
	/// flood of made-up sender domains produces, so an unbounded one would let attacker-chosen names
	/// grow memory without limit. Eviction is a whole-cache clear at the cap rather than an LRU — these
	/// entries are small and short-lived, so a clear costs one round of re-queries instead of the
	/// per-hit bookkeeping an LRU needs.
	/// </remarks>
	private sealed class NegativeCache
	{
		private const int MaxEntries = 4096;

		private readonly Dictionary<(string Name, DnsRecordType Type), (DnsQueryResult Result, DateTime Expires)> _entries = new();
		private readonly object _lock = new();

		internal bool TryGet(string name, DnsRecordType type, out DnsQueryResult result)
		{
			lock (_lock)
			{
				if (_entries.TryGetValue((name, type), out var entry))
				{
					if (entry.Expires > DateTime.UtcNow)
					{
						result = entry.Result;
						return true;
					}

					_entries.Remove((name, type));
				}
			}

			result = null!;
			return false;
		}

		internal void Set(string name, DnsRecordType type, DnsQueryResult result, TimeSpan ttl)
		{
			lock (_lock)
			{
				if (_entries.Count >= MaxEntries)
					_entries.Clear();

				_entries[(name, type)] = (result, DateTime.UtcNow + ttl);
			}
		}
	}
}
