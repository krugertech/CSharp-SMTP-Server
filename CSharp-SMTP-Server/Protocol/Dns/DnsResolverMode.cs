namespace CSharp_SMTP_Server.Protocol.Dns;

/// <summary>
/// How the server obtains a DNS resolver for SPF and DMARC validation.
/// </summary>
/// <remarks>
/// This replaces a single nullable endpoint whose null meant two different things — "no validation"
/// and "validate, using a resolver we picked for you". The second silently sent every SPF and DMARC
/// lookup, and with it the sending domains of all inbound mail, to a hardcoded public resolver.
/// </remarks>
public enum DnsResolverMode
{
	/// <summary>
	/// Use the machine's configured name servers. The default.
	/// </summary>
	/// <remarks>
	/// Resolves the system name servers and queries them directly; it does not go through the OS stub
	/// resolver, so caching is in-process rather than the platform's. This keeps lookups on whatever
	/// resolvers the host was configured to trust, with no hardcoded third party.
	/// </remarks>
	System,

	/// <summary>
	/// Use the endpoints supplied by the caller. No substitution is ever applied.
	/// </summary>
	Explicit,

	/// <summary>
	/// No resolver, and therefore no SPF or DMARC validation.
	/// </summary>
	Disabled
}
