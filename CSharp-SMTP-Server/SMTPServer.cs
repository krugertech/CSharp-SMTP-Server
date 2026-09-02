using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using CSharp_SMTP_Server.Interfaces;
using CSharp_SMTP_Server.Networking;
using CSharp_SMTP_Server.Protocol;
using CSharp_SMTP_Server.Protocol.Responses;
using CSharp_SMTP_Server.Protocol.SPF;
using CSharp_SMTP_Server.Protocol.DMARC;
using DnsClient;

namespace CSharp_SMTP_Server
{
	/// <summary>
	/// Instance of the SMTP server
	/// </summary>
	// ReSharper disable once InconsistentNaming
	// ReSharper disable once ClassNeverInstantiated.Global
	public class SMTPServer : IDisposable
	{
		/// <summary>
		/// Library version (NuGet / informational — may include pre-release suffix).
		/// </summary>
		public const string VersionString = "2.0.0-krugertech.1";

		/// <summary>
		/// Numeric-only assembly version required by AssemblyVersion / AssemblyFileVersion attributes.
		/// </summary>
		public const string AssemblyVersionString = "2.0.0.0";

		/// <summary>
		/// Server options
		/// </summary>
		public readonly ServerOptions Options;

		/// <summary>
		/// DNS Client used for email messages authentication
		/// </summary>
		public readonly DnsClient.DnsClient? DnsClient;

		/// <summary>
		/// SPF validator
		/// </summary>
		public readonly SpfValidator? SpfValidator;

		/// <summary>
		/// DMARC validator
		/// </summary>
		public readonly DmarcValidator? DmarcValidator;

		internal readonly IMailDelivery MailDeliveryInterface;

		internal IAuthLogin? AuthLogin { get; private set; }

		internal IMailFilter? Filter { get; private set; }

		internal readonly ILogger? LoggerInterface;

		internal X509Certificate? Certificate { get; private set; }

		private readonly List<Listener> _listeners = new();

		private bool _started;

		/// <summary>
		/// Initializes the instance of SMTP server with TLS certificate.
		/// </summary>
		/// <param name="parameters">Listening parameters</param>
		/// <param name="options">Server options</param>
		/// <param name="deliveryInterface">Interface used for email delivery.</param>
		/// <param name="loggerInterface">Interface used for logging server errors.</param>
		/// <param name="certificate">TLS certificate of the server.</param>
		public SMTPServer(IEnumerable<ListeningParameters>? parameters, ServerOptions? options,
			IMailDelivery deliveryInterface, ILogger? loggerInterface = null,
			X509Certificate? certificate = null)
		{
			Options = options ?? new();
			MailDeliveryInterface = deliveryInterface;
			LoggerInterface = loggerInterface;
			Certificate = certificate;

			if (Options.DnsServerEndpoint != null)
			{
				// The Cloudflare fallback is silent at construction time — ServerOptions has no logger.
				// Surface it here instead: a deployment that never chose a resolver still sends every
				// SPF/DMARC lookup, and with it the sending domains of its inbound mail, to a third party.
				if (Options.DnsServerEndpointIsDefault)
					LoggerInterface?.LogError(
						$"[Startup] No DNS server endpoint was configured; SPF/DMARC validation will use the default public resolver {Options.DnsServerEndpoint}. " +
						"All SPF and DMARC lookups — including the sending domains of inbound mail — will be sent to that third-party operator. " +
						"Pass an explicit endpoint to ServerOptions to keep DNS resolution on infrastructure you control.");

				// DMARC authenticates by checking that an already-AUTHENTICATED identifier aligns with the
				// header-From domain (RFC 7489 §4.1). DKIM verification is unimplemented here
				// (KNOWN_ISSUES.md), so SPF is the only mechanism that can supply one. With SPF off, DMARC
				// has nothing to align and can never return Pass — it is enabled, visibly configured, and
				// inert. Warn rather than throw: refusing to start would break existing deployments, and
				// the validator now answers None instead of the Pass it used to invent.
				if (Options.ValidateDMARC && !Options.ValidateSPF)
					LoggerInterface?.LogError(
						"[Startup] DMARC validation is enabled but SPF validation is disabled. DMARC needs an authenticated identifier to align, " +
						"and with DKIM verification unimplemented SPF is the only source of one, so DMARC cannot authenticate any message and will " +
						"never return a pass. Enable SPF validation, or disable DMARC to make its inertness explicit.");

				DnsClient = new DnsClient.DnsClient(Options.DnsServerEndpoint, new DnsClientOptions {ErrorLogging = new DnsLogger(this)});
				SpfValidator = new SpfValidator(this);
				DmarcValidator = new DmarcValidator(this);
			}

			if (parameters != null)
				foreach (var parameter in parameters)
				{
					if (parameter == null)
						continue;

					if (parameter.RegularPorts != null)
						foreach (var port in parameter.RegularPorts)
							_listeners.Add(new Listener(parameter.IpAddress, port, this, false, parameter.DualMode));

					if (parameter.TlsPorts != null)
						foreach (var port in parameter.TlsPorts)
							_listeners.Add(new Listener(parameter.IpAddress, port, this, true, parameter.DualMode));
				}
		}

		/// <summary>
		/// Starts the server.
		/// </summary>
		public void Start()
		{
			_started = true;
			_listeners.ForEach(listener => listener.Start());
		}

		/// <summary>
		/// Stops and disposes the server.
		/// </summary>
		public void Dispose()
		{
			GC.SuppressFinalize(this);

			foreach (var listener in _listeners)
				listener.Dispose();

			Certificate?.Dispose();
		}

		/// <summary>
		/// Sets the interface used for authentication. Enables authentication if not null.
		/// </summary>
		/// <param name="authInterface"></param>
		public void SetAuthLogin(IAuthLogin? authInterface) => AuthLogin = authInterface;

		/// <summary>
		/// Sets the email filter.
		/// </summary>
		/// <param name="mailFilter">Filter instance.</param>
		public void SetFilter(IMailFilter? mailFilter) => Filter = mailFilter;

		/// <summary>
		/// Sets the TLS certificate of the server.
		/// </summary>
		/// <param name="certificate">Certificate used by the server</param>
		// ReSharper disable once InconsistentNaming
		public void SetTLSCertificate(X509Certificate certificate) => Certificate = certificate;

		internal Task<SmtpDeliveryResult> DeliverMessage(MailTransaction transaction, CancellationToken cancellationToken = default) =>
			MailDeliveryInterface.EmailReceivedAsync(transaction, cancellationToken);

		/// <summary>
		/// Adds a new listener to the server.
		/// </summary>
		/// <param name="ipAddress">Listening IP address</param>
		/// <param name="port">Listening port</param>
		/// <param name="tls">Whether listener always uses TLS</param>
		/// <param name="dualMode">Whether socket should use DualMode (listen on both IPv4 and IPv6 address). Works only if ipAddress is set to IPAddress.IPv6Any.</param>
		public void AddListener(IPAddress ipAddress, ushort port, bool tls, bool dualMode = false)
		{
			var l = new Listener(ipAddress, port, this, tls, dualMode);
			_listeners.Add(l);

			if (_started)
				l.Start();
		}

		/// <summary>
		/// <see cref="SMTPServer"/> finalizer
		/// </summary>
		~SMTPServer() => Dispose();
	}
}