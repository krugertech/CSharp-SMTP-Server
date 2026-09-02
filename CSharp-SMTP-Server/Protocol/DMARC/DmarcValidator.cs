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

	// Published by reference swap, never mutated in place. GetOrganizationalDomain reads this from
	// arbitrary connection threads while a refresh may be running, and the previous code cleared and
	// repopulated one shared HashSet: a reader could observe the set mid-rebuild — empty, or partially
	// filled — and compute a different organizational domain for the same name. That silently changes
	// DMARC relaxed-alignment verdicts, which is not something a security gate can afford to get wrong
	// intermittently. Building a fresh set and swapping the reference makes every read see one
	// complete generation or another, never a torn one.
	private static volatile HashSet<string> _publicSuffixes = new();

	// Set only AFTER _publicSuffixes holds a fully built list. It used to be set before the download
	// began, so a concurrent caller saw "loaded" against an empty set and skipped waiting for it.
	private static volatile bool _publicSuffixesLoaded;
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
		// A non-forced call is "load it if it isn't loaded". Reading the latch here only skips work
		// that is already finished; the download itself is serialized below, so two constructors racing
		// on first use fetch the list once rather than both hitting the network.
		if (!force && _publicSuffixesLoaded)
			return;

		using var httpClient = new HttpClient();
		using var response = await httpClient.GetAsync(url);

		if (!response.IsSuccessStatusCode)
			throw new Exception("Failed to download list of domains!");

		var data = (await response.Content.ReadAsStringAsync()).Split(new [] {'\r', '\n'}, StringSplitOptions.RemoveEmptyEntries);

		// Built off to the side, so readers keep using the previous generation until this one is whole.
		var suffixes = new HashSet<string>();

		foreach (var line in data)
		{
			if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//", StringComparison.Ordinal))
				continue;

			suffixes.Add(line);
		}

		lock (PublicSuffixesLock)
		{
			// Lost the race to another loader: it already published a complete list, so keep it rather
			// than swapping in an identical one. A forced refresh always publishes — that is its point.
			if (!force && _publicSuffixesLoaded)
				return;

			_publicSuffixes = suffixes;
			_publicSuffixesLoaded = true;
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

		// Captured once: a concurrent refresh swaps the field, and walking two different generations
		// part-way through one name would produce a domain that matches neither list.
		var publicSuffixes = _publicSuffixes;

		string orgDomain = sp[^2] + "." + sp[^1];
		int i = sp.Length - 2;

		while (publicSuffixes.Contains(orgDomain) && i > 0)
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

		// A transient DNS failure while fetching the policy is not the same as "this domain publishes
		// no policy", but both used to collapse into null and therefore into None. That let a resolver
		// outage disable DMARC enforcement for every domain at once — exactly when an attacker would
		// want it disabled — so an unreachable resolver now defers the message instead (451 4.7.1).
		var (record, temporaryFailure) = await GetDmarcRecord(fromDomain);

		if (temporaryFailure)
			return ValidationResult.Temperror;

		var fromOrgDomain = GetOrganizationalDomain(fromDomain);
		bool isSubdomain = record == null;

		if (record == null)
		{
			(record, temporaryFailure) = await GetDmarcRecord(fromOrgDomain);

			if (temporaryFailure)
				return ValidationResult.Temperror;
		}

		return record != null ? ProcessRecord(transaction, record, fromDomain, fromOrgDomain, isSubdomain) : ValidationResult.None;
	}

	/// <summary>
	/// Fetches the DMARC record for a name.
	/// </summary>
	/// <returns>
	/// The record, or null when the name definitively publishes none. <c>TemporaryFailure</c> is true
	/// when the lookup could not be completed at all — a distinct outcome from "no record", because
	/// only the latter is evidence about the domain.
	/// </returns>
	private async Task<(string? Record, bool TemporaryFailure)> GetDmarcRecord(string domain)
	{
		var dmarcQuery = await _server.DnsClient!.Query("_dmarc." + domain, QType.TXT);

		// RFC 7208 §5 draws this line for SPF and it holds here: NXDOMAIN is a definitive answer — the
		// name does not exist, so there is no policy. Any other error code means the question went
		// unanswered, which says nothing about the domain.
		if (dmarcQuery.ErrorCode == DnsErrorCode.NameError)
			return (null, false);

		if (dmarcQuery.ErrorCode != DnsErrorCode.NoError)
			return (null, true);

		if (dmarcQuery.Records == null)
			return (null, false);

		string? record = null;

		foreach (var r in dmarcQuery.Records)
		{
			if (r is not DnsRecord.TXTRecord t || !t.Text.StartsWith("v=DMARC1;", StringComparison.Ordinal))
				continue;

			// §7: two v=DMARC1; records for one name are treated as if no record existed.
			if (record != null)
				return (null, false);

			record = t.Text;
		}

		return (record, false);
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
		// the *result* of SPF authentication (RFC 7489 §4.1), not on a name the client asserted: both
		// the header-From domain and the envelope domain are attacker-supplied text until SPF says the
		// connecting IP may use the latter. Making the two match costs an attacker nothing, so an
		// alignment comparison alone authenticates no one.
		//
		// This gate previously applied only to the null-reverse-path case, which left ordinary mail —
		// nearly all traffic — passing DMARC on an aligned pair of attacker-chosen names. A domain
		// publishing p=reject with no SPF record could be spoofed outright, and an SPF softfail or a
		// resolver timeout downgraded DMARC to passing rather than failing closed. RFC 7489 §4.1
		// requires at least one authenticated identifier to align; with DKIM unimplemented here
		// (KNOWN_ISSUES.md) that leaves exactly one mechanism, so an aligned SPF Pass is mandatory for
		// every message, not just for bounces.
		//
		// The identity SPF checked and the identity aligned here are the same name by construction:
		// TransactionCommands checks postmaster@<HELO domain> for a null reverse-path (RFC 7208 §2.4)
		// and the MAIL FROM domain otherwise, which is exactly how envelopeDomain is chosen above. So
		// a Pass here is a Pass *for envelopeDomain*, not for some unrelated domain.
		//
		// Returning None rather than Fail is deliberate and is what keeps this from destroying
		// legitimate mail. With SPF disabled, absent, or unresolvable there is no authenticated
		// identity at all, so the correct DMARC answer is "no determination" — the message is
		// delivered and the local filter hooks (WHITELIST.md) decide. Failing closed here would refuse
		// every DSN from a bouncing MTA greeting with its own hostname, and every customer domain that
		// publishes no usable SPF record — the permanent, unrecoverable loss this deployment exists to
		// prevent. RELAY-SENDER-AUTHORIZATION.md is explicit that measurement precedes enforcement.
		//
		// The spoofing case is still caught: an attacker either fails SPF outright (already refused at
		// MAIL FROM), or passes SPF for a domain that is not the victim's — which then fails to align
		// below and is refused under p=reject.
		switch (transaction.SPFValidationResult)
		{
			case ValidationResult.Pass:
				break;

			// A DNS failure while checking SPF is not evidence of anything. Failing open lets a
			// resolver outage silently disable DMARC for every domain at once, which is precisely the
			// window an attacker would choose; failing closed turns a transient hiccup into permanently
			// bounced mail. RFC 7489 §6.6.3 permits a temporary-failure response, so the message is
			// deferred (4.7.1) and the sender retries once resolution recovers.
			case ValidationResult.Temperror:
				return ValidationResult.Temperror;

			// SPF is switched off, so DMARC has no authenticated identifier whatsoever and cannot
			// report Pass for anything. Enabling DMARC without SPF is a configuration error rather than
			// a per-message one; it is reported as "no determination" here and warned about at startup.
			case ValidationResult.CheckDisabled:
			// None / Neutral / Softfail / Permerror: no authenticated identity.
			default:
				return ValidationResult.None;
		}

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