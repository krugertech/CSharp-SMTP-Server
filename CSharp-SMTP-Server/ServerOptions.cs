using System;
using System.Net;
using System.Security.Authentication;
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
		// ReSharper disable once InconsistentNaming
		public bool ValidateSPF
		{
			get => _validateSPF;

			set
			{
				if (!value)
				{
					_validateSPF = false;
					return;
				}

				if (DnsServerEndpoint == null)
					throw new Exception("SPF validation can't be enabled if DNS endpoint is not defined!");

				_validateSPF = true;
			}
		}

		/// <summary>
		/// Enables or disables DMARC validation of emails sent by unauthenticated users.
		/// Default: true
		/// </summary>
		// ReSharper disable once InconsistentNaming
		public bool ValidateDMARC
		{
			get => _validateDMARC;

			set
			{
				if (!value)
				{
					_validateDMARC = false;
					return;
				}

				if (DnsServerEndpoint == null)
					throw new Exception("DMARC validation can't be enabled if DNS endpoint is not defined!");

				_validateDMARC = true;
			}
		}

		/// <summary>
		/// Endpoint to the DNS Server used for SPF and DMARC validation.
		/// <para>
		/// If SPF or DMARC validation is enabled and no endpoint is supplied, this falls back to
		/// 1.1.1.1:53 (Cloudflare Public DNS). <b>Every SPF and DMARC lookup then leaves your network
		/// to a third-party resolver</b>, exposing the sending domains of your inbound mail — and your
		/// query volume — to that operator. Where that is a privacy, compliance, or availability
		/// concern, pass an explicit endpoint. <see cref="DnsServerEndpointIsDefault"/> reports whether
		/// the fallback was applied, and <see cref="SMTPServer"/> logs a warning at startup when it was.
		/// </para>
		/// Default: 1.1.1.1:53 (Cloudflare Public DNS Server)
		/// </summary>
		public readonly EndPoint? DnsServerEndpoint;

		/// <summary>
		/// True when <see cref="DnsServerEndpoint"/> was not supplied by the caller and the built-in
		/// Cloudflare fallback was applied. False when the caller passed an endpoint explicitly, and
		/// false when no validation was requested and no endpoint was set.
		/// </summary>
		public readonly bool DnsServerEndpointIsDefault;

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="validateSPF">Indicates whether SPF validation should be enabled</param>
		/// <param name="validateDMARC">Indicates whether DMARC validation should be enabled</param>
		/// <param name="dnsServerEndpoint">
		/// Specifies DNS server endpoint. If null and either validation is enabled, 1.1.1.1:53
		/// (Cloudflare Public DNS) is used and all SPF/DMARC lookups go to that third-party resolver.
		/// Pass an explicit endpoint to keep resolution on infrastructure you control.
		/// </param>
		// ReSharper disable InconsistentNaming
		public ServerOptions(bool validateSPF = true, bool validateDMARC = true, EndPoint? dnsServerEndpoint = null)
		{
			_validateSPF = validateSPF;
			_validateDMARC = validateDMARC;
			DnsServerEndpoint = dnsServerEndpoint;

			if ((validateSPF || validateDMARC) && DnsServerEndpoint == null)
			{
				DnsServerEndpoint = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53);
				DnsServerEndpointIsDefault = true;
			}
		}

		// ReSharper disable once InconsistentNaming
		private bool _validateSPF;

		// ReSharper disable once InconsistentNaming
		private bool _validateDMARC;
	}
}
