using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace CSharp_SMTP_Server.Protocol.Dns;

/// <summary>
/// The DNS surface SPF and DMARC validation actually needs: look up records of one type for one
/// name, and reverse-resolve an address.
/// </summary>
/// <remarks>
/// This exists so the validators do not name a third-party client. The previous design exposed the
/// concrete client as a public field, which made the resolver library part of this package's public
/// API — replacing it was a source and binary break for consumers over an implementation detail
/// they never chose. Everything crossing this boundary is defined in this namespace.
/// </remarks>
public interface IDnsResolver
{
	/// <summary>
	/// Queries <paramref name="name"/> for records of <paramref name="type"/>.
	/// </summary>
	Task<DnsQueryResult> QueryAsync(string name, DnsRecordType type);

	/// <summary>
	/// Reverse-resolves <paramref name="address"/> to its PTR records.
	/// </summary>
	Task<DnsQueryResult> QueryReverseAsync(IPAddress address);
}

/// <summary>
/// Record types this library queries. Deliberately not the full RFC 1035 set — it covers exactly
/// what SPF (RFC 7208) and DMARC (RFC 7489) evaluation requires.
/// </summary>
public enum DnsRecordType
{
	/// <summary>IPv4 address record.</summary>
	A,

	/// <summary>IPv6 address record.</summary>
	Aaaa,

	/// <summary>Mail exchange record.</summary>
	Mx,

	/// <summary>Text record — carries SPF and DMARC policies.</summary>
	Txt,

	/// <summary>Pointer record, used for reverse lookups.</summary>
	Ptr
}

/// <summary>
/// Outcome of a lookup, reduced to the three cases SPF and DMARC evaluation distinguishes.
/// </summary>
/// <remarks>
/// The distinction between <see cref="NameError"/> and <see cref="Failure"/> is load-bearing rather
/// than cosmetic: RFC 7208 §4.3 makes a non-existent domain a definitive "no record" answer, while
/// an unanswered query says nothing about the domain and must not be treated as evidence. Collapsing
/// the two let a resolver outage look like "no policy published" for every domain at once.
/// </remarks>
public enum DnsQueryStatus
{
	/// <summary>The query was answered. <see cref="DnsQueryResult.Records"/> may still be empty.</summary>
	Success,

	/// <summary>The name does not exist (NXDOMAIN) — a definitive answer.</summary>
	NameError,

	/// <summary>The query could not be answered: timeout, SERVFAIL, refused, malformed reply.</summary>
	Failure
}

/// <summary>
/// The result of a single DNS lookup.
/// </summary>
public sealed class DnsQueryResult
{
	/// <summary>Whether the query was answered, and definitively so.</summary>
	public DnsQueryStatus Status { get; }

	/// <summary>Records returned, filtered to the queried type. Empty unless <see cref="Status"/> is <see cref="DnsQueryStatus.Success"/>.</summary>
	public IReadOnlyList<DnsRecordData> Records { get; }

	/// <summary>Creates a result.</summary>
	public DnsQueryResult(DnsQueryStatus status, IReadOnlyList<DnsRecordData>? records = null)
	{
		Status = status;
		Records = records ?? System.Array.Empty<DnsRecordData>();
	}

	/// <summary>A result carrying no records, for a non-success status.</summary>
	public static DnsQueryResult Empty(DnsQueryStatus status) => new(status);
}

/// <summary>
/// One returned record. Which members are populated follows from <see cref="Type"/>.
/// </summary>
public sealed class DnsRecordData
{
	/// <summary>The record's type.</summary>
	public DnsRecordType Type { get; }

	/// <summary>Address, for <see cref="DnsRecordType.A"/> and <see cref="DnsRecordType.Aaaa"/>.</summary>
	public IPAddress? Address { get; }

	/// <summary>
	/// Text, for <see cref="DnsRecordType.Txt"/>. A TXT record's character-strings are concatenated,
	/// as RFC 7208 §3.3 requires for SPF records split across several strings.
	/// </summary>
	public string? Text { get; }

	/// <summary>
	/// Domain name: the exchange for <see cref="DnsRecordType.Mx"/>, the target for
	/// <see cref="DnsRecordType.Ptr"/>. Returned without the trailing root dot.
	/// </summary>
	public string? DomainName { get; }

	/// <summary>Creates a record.</summary>
	public DnsRecordData(DnsRecordType type, IPAddress? address = null, string? text = null, string? domainName = null)
	{
		Type = type;
		Address = address;
		Text = text;
		DomainName = domainName;
	}
}
