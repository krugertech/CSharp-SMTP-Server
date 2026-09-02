using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using DnsClient;
using DnsClient.Protocol;

namespace CSharp_SMTP_Server.Protocol.Dns;

/// <summary>
/// <see cref="IDnsResolver"/> backed by DnsClient.NET, with an in-process TTL-aware response cache.
/// </summary>
/// <remarks>
/// The cache is the reason for the change. Every SPF evaluation issues TXT, MX and A/AAAA queries
/// and recurses through <c>include:</c> and <c>redirect=</c> up to RFC 7208's limit of ten
/// DNS-consuming terms, and DMARC adds a TXT lookup on top. Sustained traffic from a handful of
/// customer domains re-resolved the same provider include-chain on every single message, because the
/// previous client had no cache at all and the only caching in the server was a per-connection map of
/// final SPF verdicts, discarded on close.
/// </remarks>
public sealed class DnsClientResolver : IDnsResolver
{
	private readonly LookupClient _client;

	/// <summary>
	/// Creates a resolver.
	/// </summary>
	/// <param name="options">Client options; caching is applied on top of whatever is supplied.</param>
	public DnsClientResolver(LookupClientOptions options)
	{
		// Caching is the point of this class, so it is set here rather than left to the caller to
		// remember.
		options.UseCache = true;

		// A floor stops a hostile or misconfigured zone publishing TTL=0 to defeat caching entirely and
		// turn every message into a fresh round of lookups.
		options.MinimumCacheTimeout = TimeSpan.FromSeconds(5);

		// The ceiling is what bounds this cache, and it bounds it only in TIME. DnsClient.NET's cache
		// has no entry-count limit, and the cache key includes the queried name — which is chosen by
		// whoever connects. A flood of distinct sender domains therefore grows the cache, and nothing
		// evicts an entry early; entries are only reclaimed once expired. Five minutes keeps that
		// window small enough that the working set is bounded by connection rate rather than by
		// uptime, at the cost of re-resolving long-TTL records more often than their TTL requires.
		// This is a documented limitation, not a solved problem — see KNOWN_ISSUES.md.
		options.MaximumCacheTimeout = TimeSpan.FromMinutes(5);

		// Deliberately OFF. This caches DNS *failures* — SERVFAIL, timeouts — and a cached failure is
		// exactly what must not happen here: SPF reports Temperror for them and DMARC defers the
		// message with 451 4.7.1, so caching one would keep deferring a sender that retries within the
		// window, after resolution had already recovered. It is the same stale-transient-error trap the
		// per-connection SPF verdict cache had, one layer down.
		//
		// This does not give up negative caching that matters: NXDOMAIN and an empty NOERROR answer are
		// definitive answers, cached by the normal TTL/SOA path, so a domain that genuinely publishes
		// no SPF or DMARC record is still not re-queried on every message.
		options.CacheFailedResults = false;

		// A DNS failure must stay visible as a failure. With ThrowDnsErrors off (the default) an error
		// RCODE comes back on the response, which is what QueryAsync below translates; letting the
		// library throw instead would surface a resolver hiccup as an unhandled exception on the
		// connection path.
		options.ThrowDnsErrors = false;

		_client = new LookupClient(options);
	}

	/// <inheritdoc />
	public async Task<DnsQueryResult> QueryAsync(string name, DnsRecordType type)
	{
		IDnsQueryResponse response;

		try
		{
			response = await _client.QueryAsync(name, ToQueryType(type)).ConfigureAwait(false);
		}
		catch (DnsResponseException)
		{
			// Timeout, refused connection, unparseable reply: the question went unanswered, which is
			// not evidence about the domain.
			return DnsQueryResult.Empty(DnsQueryStatus.Failure);
		}

		return Translate(response, type);
	}

	/// <inheritdoc />
	public async Task<DnsQueryResult> QueryReverseAsync(IPAddress address)
	{
		IDnsQueryResponse response;

		try
		{
			response = await _client.QueryReverseAsync(address).ConfigureAwait(false);
		}
		catch (DnsResponseException)
		{
			return DnsQueryResult.Empty(DnsQueryStatus.Failure);
		}

		return Translate(response, DnsRecordType.Ptr);
	}

	private static DnsQueryResult Translate(IDnsQueryResponse response, DnsRecordType type)
	{
		// NXDOMAIN is an answer: the name does not exist, so it publishes nothing. Every other error
		// code means the query failed, and RFC 7208 §4.3 keeps those apart for good reason — see
		// DnsQueryStatus.
		if (response.Header.ResponseCode == DnsHeaderResponseCode.NotExistentDomain)
			return DnsQueryResult.Empty(DnsQueryStatus.NameError);

		if (response.Header.ResponseCode != DnsHeaderResponseCode.NoError || response.HasError)
			return DnsQueryResult.Empty(DnsQueryStatus.Failure);

		var records = new List<DnsRecordData>();

		foreach (var record in response.Answers)
		{
			var translated = Translate(record, type);

			if (translated != null)
				records.Add(translated);
		}

		return new DnsQueryResult(DnsQueryStatus.Success, records);
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
	/// while SPF comparisons here are against names taken from records and headers, which are not.
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
}
