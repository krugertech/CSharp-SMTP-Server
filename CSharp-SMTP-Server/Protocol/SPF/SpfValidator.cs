using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using CSharp_SMTP_Server.Protocol.Dns;
// ReSharper disable MemberCanBePrivate.Global

namespace CSharp_SMTP_Server.Protocol.SPF;

/// <summary>
/// SPF validator
/// </summary>
public class SpfValidator
{
	/// <summary>
	/// Resolver used to look up SPF records.
	/// </summary>
	/// <remarks>
	/// This was a public field typed on the concrete third-party client, which made the resolver
	/// library part of this package's API surface — swapping it was a source and binary break over an
	/// implementation detail. It is an interface now; see <see cref="IDnsResolver"/>.
	/// </remarks>
	public readonly IDnsResolver Resolver;

	#region Constructors
	/// <summary>
	/// Class constructor
	/// </summary>
	/// <param name="server">SMTP server which configuration should be used</param>
	public SpfValidator(SMTPServer server) => Resolver = server.DnsResolver ?? throw new ArgumentException("Server has a null DnsResolver", nameof(server));

	/// <summary>
	/// Class constructor
	/// </summary>
	/// <param name="resolver">Resolver used for SPF validation</param>
	public SpfValidator(IDnsResolver resolver) => Resolver = resolver;

	/// <summary>
	/// Class constructor
	/// </summary>
	/// <param name="dnsServerEndpoint">DNS server endpoint</param>
	public SpfValidator(IPEndPoint dnsServerEndpoint) : this(SMTPServer.CreateResolver(DnsResolverMode.Explicit, new[] {dnsServerEndpoint})) { }
	#endregion

	/// <summary>
	/// RFC 7208 (SPF) check_host() function
	/// Authenticates remote SMTP server.
	/// </summary>
	/// <param name="ipAddress">IP address of the remote SMTP server</param>
	/// <param name="domain">Email sender domain</param>
	/// <returns>SPF validation result</returns>
	public async Task<ValidationResult> CheckHost(IPAddress ipAddress, string domain) => await CheckHost(ipAddress, domain, 0);

	private async Task<ValidationResult> CheckHost(IPAddress ipAddress, string domain, uint requestsCounter, bool ptrUsed = false)
	{
		if (ipAddress.IsIPv4MappedToIPv6)
			ipAddress = ipAddress.MapToIPv4();

		var txtQuery = await Resolver.QueryAsync(domain, DnsRecordType.Txt);

		// RFC 7208 §4.3: if the domain does not exist, the result is None — "no SPF record" — not a
		// transient failure. The MX and A/AAAA paths below already draw this line (see §5); the initial
		// TXT lookup did not, so every non-existent sender domain reported Temperror.
		//
		// That was survivable while nothing consumed Temperror, but DMARC now defers on it (451 4.7.1
		// per RFC 7489 §6.6.3). Left unfixed, mail from any domain without a DNS entry would be
		// retried forever instead of being handled as the unauthenticated mail it is.
		if (txtQuery.Status == DnsQueryStatus.NameError)
			return ValidationResult.None;

		if (txtQuery.Status != DnsQueryStatus.Success)
			return ValidationResult.Temperror;

		string? record = null;

		foreach (var r in txtQuery.Records)
		{
			if (r.Text == null || !r.Text.StartsWith("v=spf1 ", StringComparison.Ordinal))
				continue;

			if (record != null)
				return ValidationResult.Permerror;

			record = r.Text;
		}

		if (record == null)
			return ValidationResult.None;

		record = record[7..].TrimEnd();
		var sp = record.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		uint requestsMade = requestsCounter;
		bool ptrWasUsed = ptrUsed;

		foreach (var s in sp)
		{
			var qualifier = ValidationResult.Pass;
			var mechanism = s;
			string? args = null;
			byte? cidr = null;

			switch (s[0])
			{
				case '+':
					mechanism = s[1..];
					break;

				case '-':
					qualifier = ValidationResult.Fail;
					mechanism = s[1..];
					break;

				case '~':
					qualifier = ValidationResult.Softfail;
					mechanism = s[1..];
					break;

				case '?':
					qualifier = ValidationResult.Neutral;
					mechanism = s[1..];
					break;
			}

			if (mechanism == "")
				continue;

			if (mechanism.Contains('/'))
			{
				var index = mechanism.IndexOf('/');

				if (mechanism.Length > index && byte.TryParse(mechanism[(index + 1)..], out var cd))
					cidr = cd;

				mechanism = mechanism[..index];
			}

			if (mechanism.Contains(':'))
			{
				var index = mechanism.IndexOf(':');

				if (mechanism.Length > index)
					args = mechanism[(index + 1)..];

				mechanism = mechanism[..index];
			}
			else if (mechanism.Contains('='))
			{
				var index = mechanism.IndexOf('=');

				if (mechanism.Length > index)
					args = mechanism[(index + 1)..];

				mechanism = mechanism[..index];
			}

			if (mechanism == "")
				continue;

			switch (mechanism.ToLowerInvariant())
			{
				case "all":
					return qualifier;

				case "a" when ipAddress.AddressFamily == AddressFamily.InterNetwork:
				case "a" when ipAddress.AddressFamily == AddressFamily.InterNetworkV6:
					{
						if (requestsMade > 10)
							return ptrWasUsed ? ValidationResult.Fail : ValidationResult.Permerror;

						requestsMade++;

						var result = await CheckAddressMatch(ipAddress, domain, args, cidr, qualifier);

						// RFC 7208 §5: a DNS error during the address lookup stops evaluation with
						// temperror. Treating any non-None result as a match would fail *open* — a
						// bare "a" (implicit "+") would return Pass during a resolver outage.
						if (result == ValidationResult.Temperror)
							return ValidationResult.Temperror;

						if (result != ValidationResult.None)
							return qualifier;
					}
					break;

				case "a":
					return ValidationResult.Permerror;

				case "mx":
					{
						if (requestsMade > 10)
							return ptrWasUsed ? ValidationResult.Fail : ValidationResult.Permerror;

						requestsMade++;

						var mxQuery = await Resolver.QueryAsync(args ?? domain, DnsRecordType.Mx);

						// NXDOMAIN: the name does not exist, so there are no MX hosts to match against.
						// That is a definitive no-match, not a transient failure — fall through to the
						// next mechanism (see the note in CheckAddressMatch).
						if (mxQuery.Status == DnsQueryStatus.NameError)
							break;

						if (mxQuery.Status != DnsQueryStatus.Success)
							return ValidationResult.Temperror;

						foreach (var q in mxQuery.Records)
						{
							if (q.DomainName == null)
								continue;

							if (requestsMade > 10)
								return ValidationResult.Permerror;

							requestsMade++;

							var result = await CheckAddressMatch(ipAddress, q.DomainName, null, cidr, qualifier);

							// Same fail-open as the "a" mechanism above: a failed A/AAAA lookup for an
							// MX host is a temperror, not a match (RFC 7208 §5).
							if (result == ValidationResult.Temperror)
								return ValidationResult.Temperror;

							if (result != ValidationResult.None)
								return qualifier;
						}
					}
					break;

				case "ptr":
					{
						if (requestsMade > 10)
							return ptrWasUsed ? ValidationResult.Fail : ValidationResult.Permerror;

						requestsMade++;

						var ptrQuery = await Resolver.QueryReverseAsync(ipAddress);

						if (ptrQuery.Status != DnsQueryStatus.Success)
							continue;

						foreach (var q in ptrQuery.Records)
						{
							if (q.DomainName == null)
								continue;

							if (!q.DomainName.Equals(domain, StringComparison.OrdinalIgnoreCase) && !q.DomainName.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
								continue;

							if (requestsMade > 10)
								return ValidationResult.Fail;

							requestsMade++;
							ptrWasUsed = true;

							if (await CheckAddressMatch(ipAddress, q.DomainName, null, null, ValidationResult.Pass) == ValidationResult.Pass)
								return qualifier;
						}
					}
					break;

				case "ip4" when args != null && ipAddress.AddressFamily == AddressFamily.InterNetwork && IPAddress.TryParse(args, out var ipParsed):
					{
						if (cidr is null or > 32)
							cidr = 32;

						if (CheckCIDR(ipAddress, ipParsed, cidr.Value))
							return qualifier;
					}
					break;

				case "ip6" when args != null && ipAddress.AddressFamily == AddressFamily.InterNetworkV6 && IPAddress.TryParse(args, out var ipParsed):
					{
						if (cidr is null or > 128)
							cidr = 128;

						if (CheckCIDR(ipAddress, ipParsed, cidr.Value))
							return qualifier;
					}
					break;

				case "redirect" when args != null:
					{
						if (sp.Any(c => c.Equals("all", StringComparison.OrdinalIgnoreCase) || (c.Length == 4 && c.EndsWith("all", StringComparison.OrdinalIgnoreCase))))
							continue;

						if (requestsMade > 10)
							return ValidationResult.Permerror;

						requestsMade++;

						var check = await CheckHost(ipAddress, args, requestsMade, ptrWasUsed);
						return check == ValidationResult.None ? ValidationResult.Permerror : check;
					}

				case "include" when args != null:
					{
						if (requestsMade > 10)
							return ValidationResult.Permerror;

						requestsMade++;

						switch (await CheckHost(ipAddress, args, requestsMade, ptrWasUsed))
						{
							case ValidationResult.Pass:
								return qualifier;

							case ValidationResult.Temperror:
								return ValidationResult.Temperror;

							case ValidationResult.Permerror:
							case ValidationResult.None:
								return ValidationResult.Permerror;
						}
						break;
					}
			}
		}

		return ValidationResult.Neutral;
	}

	private async Task<ValidationResult> CheckAddressMatch(IPAddress ipAddress, string domain, string? args, int? cidr, ValidationResult qualifier)
	{
		var aQuery = await Resolver.QueryAsync(args ?? domain, ipAddress.AddressFamily == AddressFamily.InterNetwork ? DnsRecordType.A : DnsRecordType.Aaaa);

		// RFC 7208 §5: NXDOMAIN ("NameError") is a definitive answer — the name simply does not exist,
		// so the mechanism does NOT match and evaluation continues to the next one (typically reaching
		// a terminal "-all"). Only a genuinely transient failure (SERVFAIL, no response, unparseable)
		// is a temperror. Collapsing the two would make "v=spf1 a:missing.test -all" return Temperror
		// instead of Fail, and since SMTP rejects only on Fail, that accepts mail it should reject.
		if (aQuery.Status == DnsQueryStatus.NameError)
			return ValidationResult.None;

		if (aQuery.Status != DnsQueryStatus.Success)
			return ValidationResult.Temperror;

		if (ipAddress.AddressFamily == AddressFamily.InterNetwork)
		{
			if (cidr is null or > 32)
				cidr = 32;

			foreach (var q in aQuery.Records)
			{
				if (q.Address == null)
					continue;

				if (CheckCIDR(q.Address, ipAddress, (byte)cidr))
					return qualifier;
			}
		}
		else
		{
			if (cidr is null or > 128)
				cidr = 128;

			foreach (var q in aQuery.Records)
			{
				if (q.Address == null)
					continue;

				if (CheckCIDR(q.Address, ipAddress, (byte)cidr))
					return qualifier;
			}
		}

		return ValidationResult.None;
	}

	/// <summary>
	/// Checks if two IP addresses belong to the same subnet
	/// </summary>
	/// <param name="a">First IP address</param>
	/// <param name="b">Second IP address</param>
	/// <param name="mask">Subnet mask length</param>
	/// <returns>Whether two IP addresses are in the same subnet</returns>
	/// <exception cref="ArgumentException">Subnet mask length is not greater or equal to 0</exception>
	// ReSharper disable once InconsistentNaming
	public static bool CheckCIDR(IPAddress a, IPAddress b, int mask)
	{
		if (a.AddressFamily != b.AddressFamily)
			return false;

		switch (mask)
		{
			case 0:
				return true;

			case < 0:
				throw new ArgumentException(nameof(mask));

			case 32 when a.AddressFamily == AddressFamily.InterNetwork:
			case 128 when a.AddressFamily == AddressFamily.InterNetworkV6:
				return a.Equals(b);
		}

		var aBytes = a.GetAddressBytes();
		var bBytes = b.GetAddressBytes();

		if (mask > aBytes.Length * 8)
			mask = aBytes.Length * 8;

		for (var i = 0; i < aBytes.Length; i++)
		{
			var diff = mask - (i * 8);

			switch (diff)
			{
				case 0:
					return true;

				case >= 8 when aBytes[i] != bBytes[i]:
					return false;

				case >= 8:
					continue;

				default:
					{
						var m = (byte)(0xFF << (8 - diff));
						return (aBytes[i] & m) == (bBytes[i] & m);
					}
			}
		}

		return true;
	}
}