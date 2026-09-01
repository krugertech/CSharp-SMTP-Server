using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using CSharp_SMTP_Server.Protocol.Commands;
using DnsClient.Data.Records;
using DnsClient.Enums;

namespace CSharp_SMTP_Server.Protocol.DMARC;

/// <summary>
/// DMARC validator
/// </summary>
public class DmarcValidator
{
	private readonly SMTPServer _server;

	private static readonly HashSet<string> PublicSuffixes = new();
	private static bool _publicSuffixesLoaded;
	private static readonly object PublicSuffixesLock = new();

	#region Constructors
	/// <summary>
	/// Class constructor
	/// </summary>
	/// <param name="server">SMTP server which configuration should be used</param>
	public DmarcValidator(SMTPServer server)
	{
		_server = server;

		if (!_publicSuffixesLoaded)
			DownloadList(_server.Options.PublicSuffixList).Wait();
	}
	#endregion

	/// <summary>
	/// Forces PublicSuffixes reload
	/// </summary>
	/// <param name="url">URL of the list</param>
	public async Task ForceRefreshList(string? url = null) => await DownloadList(url ?? _server.Options.PublicSuffixList, true);

	private static async Task DownloadList(string url, bool force = false)
	{
		if (!force)
		{
			lock (PublicSuffixesLock)
			{
				if (_publicSuffixesLoaded)
					return;

				_publicSuffixesLoaded = true;
			}
		}
		else _publicSuffixesLoaded = true;

		try
		{
			using var httpClient = new HttpClient();
			using var response = await httpClient.GetAsync(url);

			if (!response.IsSuccessStatusCode)
				throw new Exception("Failed to download list of domains!");

			var data = (await response.Content.ReadAsStringAsync()).Split(new [] {'\r', '\n'}, StringSplitOptions.RemoveEmptyEntries);

			PublicSuffixes.Clear();

			foreach (var line in data)
			{
				if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//", StringComparison.Ordinal))
					continue;

				PublicSuffixes.Add(line);
			}
		}
		catch (Exception)
		{
			_publicSuffixesLoaded = false;
			throw;
		}
	}

	/// <summary>
	/// Returns the Organizational Domain
	/// </summary>
	/// <param name="domain">Domain to process</param>
	/// <returns>Organizational Domain</returns>
	/// <exception cref="Exception">Returned if DMARC Validator was never initialized.</exception>
	// ReSharper disable once MemberCanBePrivate.Global
	public static string GetOrganizationalDomain(string domain)
	{
		if (!_publicSuffixesLoaded)
			throw new Exception("Suffix list is not loaded, because DMARC Validator was never initialized.");

		var sp = domain.Split('.', StringSplitOptions.RemoveEmptyEntries);

		if (sp.Length <= 2)
			return domain;

		string orgDomain = sp[^2] + "." + sp[^1];
		int i = sp.Length - 2;

		while (PublicSuffixes.Contains(orgDomain) && i > 0)
		{
			i--;
			orgDomain = sp[i] + "." + orgDomain;
		}

		return orgDomain;
	}

	/// <summary>
	/// Validates Mail Transaction using DMARC
	/// </summary>
	/// <param name="transaction">Transaction to validate</param>
	/// <returns>Validation result</returns>
	/// <exception cref="Exception">Returned if DMARC Validator was never initialized.</exception>
	public async Task<ValidationResult> ValidateTransaction(MailTransaction transaction)
	{
		if (!_publicSuffixesLoaded)
			throw new Exception("Suffix list is not loaded, because DMARC Validator was never initialized.");

		var from = transaction.GetFrom;

		if (from == null)
			return ValidationResult.None;

		// GetFrom returns a bare address (MimeKit already parsed the header), so the domain is taken
		// directly rather than through ProcessAddress, which parses SMTP command arguments and requires
		// the angle-bracket form. Routing a bare address through ProcessAddress yields null and leaves
		// DMARC silently inert — that was the second half of bug B1.
		var fromDomain = TransactionCommands.GetAddressDomain(from);

		if (fromDomain == null)
			return ValidationResult.None;

		var record = await GetDmarcRecord(fromDomain);
		var fromOrgDomain = GetOrganizationalDomain(fromDomain);
		bool isSubdomain = record == null;

		record ??= await GetDmarcRecord(fromOrgDomain);

		return record != null ? ProcessRecord(transaction, record, fromDomain, fromOrgDomain, isSubdomain) : ValidationResult.None;
	}

	private async Task<string?> GetDmarcRecord(string domain)
	{
		var dmarcQuery = await _server.DnsClient!.Query("_dmarc." + domain, QType.TXT);

		if (dmarcQuery.ErrorCode != DnsErrorCode.NoError || dmarcQuery.Records == null)
			return null;

		string? record = null;

		foreach (var r in dmarcQuery.Records)
		{
			if (r is not DnsRecord.TXTRecord t || !t.Text.StartsWith("v=DMARC1;", StringComparison.Ordinal))
				continue;

			if (record != null)
				return null;

			record = t.Text;
		}

		return record;
	}

	private static ValidationResult ProcessRecord(MailTransaction transaction, string record, string fromDomain, string fromOrgDomain, bool isSubdomain)
	{
		record = record[8..].Trim().Replace("; ", ";", StringComparison.Ordinal);

		var aspf = record.Contains("aspf=s", StringComparison.OrdinalIgnoreCase) ? AlignmentMode.Strict : AlignmentMode.Relaxed;
		var action = DmarcResult.None;

		if (isSubdomain && record.Contains(";sp=", StringComparison.OrdinalIgnoreCase))
		{
			if (record.Contains(";sp=reject", StringComparison.OrdinalIgnoreCase)) action = DmarcResult.Reject;
			else if (record.Contains(";sp=quarantine", StringComparison.OrdinalIgnoreCase)) action = DmarcResult.Quarantine;
		}
		else
		{
			if (record.Contains(";p=reject", StringComparison.OrdinalIgnoreCase)) action = DmarcResult.Reject;
			else if (record.Contains(";p=quarantine", StringComparison.OrdinalIgnoreCase)) action = DmarcResult.Quarantine;
		}

		// The domain to align against RFC5322.From.
		//
		// For an ordinary message that is the envelope sender's domain. For a null reverse-path (every
		// DSN/bounce) there is no MAIL FROM identity, and RFC 7489 §3.1.2 covers this case explicitly:
		//
		//     "Note that the RFC5321.HELO identity is not typically used in the context of DMARC
		//      (except when required to "fake" an otherwise null reverse-path), even though a
		//      "pure SPF" implementation according to [SPF] would check that identifier."
		//
		// The parenthetical IS the null-path case, so the HELO domain is the identity to align — which
		// is also the identity RFC 7208 §2.4 has SPF check (as postmaster@<HELO domain>), so the two
		// specifications agree on which name is being authenticated.
		var envelopeDomain = transaction.IsNullReversePath ? transaction.HeloDomain : transaction.FromDomain;

		// Alignment is only meaningful over an identity SPF actually AUTHENTICATED. DMARC is built on
		// the *result* of SPF authentication (RFC 7489 §4.1), not on a name the client asserted: a
		// HELO domain is attacker-controlled text until SPF says the connecting IP may use it.
		//
		// This gate is what keeps the fix from destroying ordinary bounces. A bouncing MTA greets with
		// its own hostname (mail-out-3.provider.example), which routinely differs from the From domain
		// of the notification it carries, so a bare string comparison refuses legitimate DSNs from the
		// very domain that published p=reject — the permanent, unrecoverable loss this deployment
		// exists to prevent. With SPF disabled, no record, or a DNS temperror there is no authenticated
		// identity at all, so the correct DMARC answer is "no determination" (None) and the message is
		// delivered.
		//
		// The spoofing case is still caught: an attacker sending MAIL FROM:<> with a spoofed
		// "From: ceo@victim.example" either fails SPF on its own HELO domain (refused at MAIL FROM), or
		// passes SPF for a domain that is not victim.example — which then fails to align here and is
		// refused under p=reject.
		if (transaction.IsNullReversePath && transaction.SPFValidationResult != ValidationResult.Pass)
			return ValidationResult.None;

		// No checkable identity — an address literal or a non-DNS HELO name, which cannot carry an SPF
		// record and therefore cannot have been authenticated.
		if (string.IsNullOrEmpty(envelopeDomain))
			return ValidationResult.None;

		bool isAligned = fromDomain.Equals(envelopeDomain, StringComparison.OrdinalIgnoreCase);

		if (!isAligned && aspf == AlignmentMode.Relaxed)
		{
			var envelopeFromOrgDomain = GetOrganizationalDomain(envelopeDomain);
			isAligned = fromOrgDomain.Equals(envelopeFromOrgDomain, StringComparison.OrdinalIgnoreCase);
		}

		if (isAligned)
			return ValidationResult.Pass;

		return action switch
		{
			DmarcResult.Quarantine => ValidationResult.Softfail,
			DmarcResult.Reject => ValidationResult.Fail,
			_ => ValidationResult.None
		};
	}
}