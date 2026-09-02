using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Authentication;
using CSharp_SMTP_Server.Protocol.Dns;
// ReSharper disable FieldCanBeMadeReadOnly.Global
// ReSharper disable ConvertToConstant.Global

namespace CSharp_SMTP_Server
{
	/// <summary>
	/// Options of the <see cref="SMTPServer"/>
	/// </summary>
	public class ServerOptions
	{
		/// <summary>
		/// Server name, sent on connection
		/// </summary>
		public string ServerName = "CSharp SMTP Server";

		/// <summary>
		/// Requirement of using encryption to authenticate
		/// Default: true
		/// </summary>
		public bool RequireEncryptionForAuth = true;

		/// <summary>
		/// Allowed SSL/TLS protocols.
		/// Default: TLS 1.2
		/// </summary>
		public SslProtocols Protocols = SslProtocols.Tls12;

		/// <summary>
		/// Stored DATA byte limit after dot-unstuffing, excluding CRLF. The property name is retained
		/// for compatibility.
		/// Set to 0 to disable the limit.
		/// Default: 10 485 760
		/// </summary>
		public uint MessageCharactersLimit = 10485760;

		/// <summary>
		/// Recipients limit per message.
		/// Set to 0 to disable.
		/// Default: 50
		/// </summary>
		public uint RecipientsLimit = 50;

		/// <summary>
		/// URL of list of all public suffixes of domains
		/// </summary>
		public string PublicSuffixList = "https://raw.githubusercontent.com/publicsuffix/list/master/public_suffix_list.dat";

		/// <summary>
		/// Enables or disables SPF validation of emails sent by unauthenticated users.
		/// Default: true
		/// </summary>
		/// <exception cref="InvalidOperationException">
		/// Thrown when enabling validation while <see cref="ResolverMode"/> is
		/// <see cref="DnsResolverMode.Disabled"/> — there would be no resolver to validate with.
		/// </exception>
		// ReSharper disable once InconsistentNaming
		public bool ValidateSPF
		{
			get => _validateSPF;

			set
			{
				if (value)
					RequireResolver(nameof(ValidateSPF));

				_validateSPF = value;
			}
		}

		/// <summary>
		/// Enables or disables DMARC validation of emails sent by unauthenticated users.
		/// </summary>
		/// <remarks>
		/// DMARC needs an authenticated identifier to align (RFC 7489 §4.1), and with DKIM verification
		/// unimplemented SPF is the only mechanism that can supply one. Enabling this while
		/// <see cref="ValidateSPF"/> is off leaves DMARC unable to pass anything;
		/// <see cref="SMTPServer"/> warns about that combination at startup.
		/// Default: true
		/// </remarks>
		/// <exception cref="InvalidOperationException">
		/// Thrown when enabling validation while <see cref="ResolverMode"/> is
		/// <see cref="DnsResolverMode.Disabled"/>.
		/// </exception>
		// ReSharper disable once InconsistentNaming
		public bool ValidateDMARC
		{
			get => _validateDMARC;

			set
			{
				if (value)
					RequireResolver(nameof(ValidateDMARC));

				_validateDMARC = value;
			}
		}

		/// <summary>
		/// How the DNS resolver used for SPF and DMARC validation is obtained.
		/// <para>
		/// Default: <see cref="DnsResolverMode.System"/> — the machine's own configured name servers.
		/// No public resolver is ever substituted silently.
		/// </para>
		/// </summary>
		public readonly DnsResolverMode ResolverMode;

		/// <summary>
		/// DNS server endpoints used when <see cref="ResolverMode"/> is
		/// <see cref="DnsResolverMode.Explicit"/>. Empty otherwise.
		/// </summary>
		public readonly IReadOnlyList<IPEndPoint> DnsServerEndpoints;

		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="validateSPF">Indicates whether SPF validation should be enabled</param>
		/// <param name="validateDMARC">Indicates whether DMARC validation should be enabled</param>
		/// <param name="dnsServerEndpoint">
		/// An explicit DNS server endpoint. When null, the machine's configured name servers are used
		/// (<see cref="DnsResolverMode.System"/>).
		/// </param>
		// ReSharper disable InconsistentNaming
		public ServerOptions(bool validateSPF = true, bool validateDMARC = true, EndPoint? dnsServerEndpoint = null)
			: this(validateSPF, validateDMARC,
				dnsServerEndpoint == null ? DnsResolverMode.System : DnsResolverMode.Explicit,
				dnsServerEndpoint == null ? null : new[] {AsIPEndPoint(dnsServerEndpoint, nameof(dnsServerEndpoint))})
		{
		}

		/// <summary>
		/// Constructor taking an explicit resolver mode.
		/// </summary>
		/// <param name="validateSPF">Indicates whether SPF validation should be enabled</param>
		/// <param name="validateDMARC">Indicates whether DMARC validation should be enabled</param>
		/// <param name="resolverMode">How the DNS resolver is obtained</param>
		/// <param name="dnsServerEndpoints">
		/// Endpoints to query, required when <paramref name="resolverMode"/> is
		/// <see cref="DnsResolverMode.Explicit"/> and rejected otherwise.
		/// </param>
		/// <exception cref="ArgumentException">Thrown when the arguments contradict each other.</exception>
		public ServerOptions(bool validateSPF, bool validateDMARC, DnsResolverMode resolverMode, IEnumerable<IPEndPoint>? dnsServerEndpoints)
		{
			ResolverMode = resolverMode;
			DnsServerEndpoints = dnsServerEndpoints?.ToArray() ?? Array.Empty<IPEndPoint>();

			if (resolverMode == DnsResolverMode.Explicit && DnsServerEndpoints.Count == 0)
				throw new ArgumentException("At least one DNS server endpoint is required when the resolver mode is Explicit.", nameof(dnsServerEndpoints));

			if (resolverMode != DnsResolverMode.Explicit && DnsServerEndpoints.Count > 0)
				throw new ArgumentException($"DNS server endpoints cannot be supplied when the resolver mode is {resolverMode}.", nameof(dnsServerEndpoints));

			// The constructor and the property setters used to disagree: the constructor wrote the
			// backing fields directly and invented an endpoint, while the setters threw for the same
			// enabled-without-a-resolver state. Because the endpoint was readonly, an instance built
			// with validation off could never turn it on afterwards — so identical configuration
			// succeeded or failed based only on the order it was applied in. Both paths now go through
			// the same rule.
			if ((validateSPF || validateDMARC) && resolverMode == DnsResolverMode.Disabled)
				throw new ArgumentException("SPF and DMARC validation require a resolver; the resolver mode cannot be Disabled.", nameof(resolverMode));

			_validateSPF = validateSPF;
			_validateDMARC = validateDMARC;
		}

		private static IPEndPoint AsIPEndPoint(EndPoint endPoint, string paramName) =>
			endPoint as IPEndPoint ?? throw new ArgumentException("Only IPEndPoint DNS endpoints are supported.", paramName);

		private void RequireResolver(string propertyName)
		{
			if (ResolverMode == DnsResolverMode.Disabled)
				throw new InvalidOperationException($"{propertyName} can't be enabled when the DNS resolver mode is Disabled.");
		}

		// ReSharper disable once InconsistentNaming
		private bool _validateSPF;

		// ReSharper disable once InconsistentNaming
		private bool _validateDMARC;
	}
}
