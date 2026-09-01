using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CSharp_SMTP_Server.Misc;
using CSharp_SMTP_Server.Networking;
using CSharp_SMTP_Server.Protocol.Responses;
using static System.FormattableString;

namespace CSharp_SMTP_Server.Protocol.Commands
{
	internal static class TransactionCommands
	{
		internal static async Task ProcessCommand(ClientProcessor processor, string command, string data)
		{
			switch (command)
			{
				case "RSET":
					processor.Transaction = null;
					await processor.WriteCode(250, "2.1.5", "Flushed");
					break;

				case "MAIL FROM":
					{
						// RFC 5321 §4.5.5 requires the null reverse-path "<>" to be accepted: it is the
						// reverse-path of every DSN/bounce and of some Exchange system-generated reports,
						// so rejecting it drops those messages permanently. ProcessAddress cannot parse it
						// (empty address, no '@'), so it is recognized here, before that call, and yields
						// an empty sender and an empty FromDomain.
						string? address;
						string? domain;
						var nullReversePath = IsNullReversePath(data);

						if (nullReversePath)
						{
							address = string.Empty;
							domain = string.Empty;
						}
						else address = ProcessAddress(data, out domain);

						if (address == null) await processor.WriteCode(501, "5.5.2");
						else
						{
							if (processor.Server.Filter != null)
							{
								var result = await processor.Server.Filter.IsAllowedSender(address, processor.RemoteEndPoint, processor.Username);

								if (result.Type != SmtpResultType.Success)
								{
									await processor.WriteCode(554,
										result.Type == SmtpResultType.PermanentFail ? "5.7.1" : "4.7.1",
										string.IsNullOrWhiteSpace(result.FailMessage)
											? "Delivery not authorized (MAIL FROM address not allowed), message refused"
											: result.FailMessage);
									return;
								}
							}

							var spfValidation = ValidationResult.CheckDisabled;

							if (processor.Username != null)
								spfValidation = ValidationResult.UserAuthenticated;
							// A null reverse-path carries no envelope domain to check, and querying DNS for an
							// empty domain would only yield a spurious Temperror, so SPF is skipped here.
							//
							// NOTE: RFC 7208 §2.4 does not say to skip SPF in this case — it defines the MAIL FROM
							// identity as postmaster@<HELO domain> and has that checked instead. Doing so requires
							// retaining the EHLO/HELO argument, which this server currently discards (only
							// _protocolVersion is kept). Until that is threaded through, a null-path sender is not
							// SPF-checked at all: acceptable while SPF is off (as it is for journaling), but it
							// means a HELO domain publishing -all is not enforced against a null sender.
							else if (processor.Server.Options.ValidateSPF && processor.RemoteEndPoint != null && !nullReversePath)
							{
								if (processor.SpfResultsCache!.TryGetValue(domain!, out var spfRes))
									spfValidation = spfRes;
								else
								{
									spfValidation = await processor.Server.SpfValidator!.CheckHost(processor.RemoteEndPoint.Address, domain!);
									processor.SpfResultsCache.Add(domain!, spfValidation);
								}

								if (spfValidation == ValidationResult.Fail)
								{
									await processor.WriteCode(554, "5.7.23", "Delivery not authorized by SPF, message refused");
									return;
								}
							}

							if (processor.Server.Filter != null)
							{
								var result = await processor.Server.Filter.IsAllowedSenderSpfVerified(address, processor.RemoteEndPoint, processor.Username, spfValidation);

								if (result.Type != SmtpResultType.Success)
								{
									await processor.WriteCode(554,
										result.Type == SmtpResultType.PermanentFail ? "5.7.1" : "4.7.1",
										string.IsNullOrWhiteSpace(result.FailMessage)
											? "Delivery not authorized (mail sender not allowed), message refused"
											: result.FailMessage);
									return;
								}
							}

							processor.Transaction = new MailTransaction(address, domain!, spfValidation, nullReversePath)
							{
								RemoteEndPoint = processor.RemoteEndPoint,
								Encryption = processor.Encryption
							};
							await processor.WriteCode(250, "2.0.0");
						}
					}
					break;

				case "RCPT TO":
					{
						if (processor.Transaction == null)
						{
							await processor.WriteCode(503, "5.5.1", "MAIL FROM first.");
							return;
						}

						var address = ProcessAddress(data, out _);
						if (address == null) await processor.WriteCode(501);
						else
						{
							if (processor.Server.Options.RecipientsLimit > 0 && processor.Server.Options.RecipientsLimit <= processor.Transaction.DeliverTo.Count)
							{
								await processor.WriteCode(550, "5.5.3", "Too many recipients");
								return;
							}

							if (processor.Server.Filter != null)
							{
								var filterResult = await processor.Server.Filter.CanDeliver(processor.Transaction.From, address, !string.IsNullOrEmpty(processor.Username), processor.Username, processor.RemoteEndPoint);

								if (filterResult.Type != SmtpResultType.Success)
								{
									await processor.WriteCode(550,
										filterResult.Type == SmtpResultType.PermanentFail ? "5.7.1" : "4.7.1",
										string.IsNullOrWhiteSpace(filterResult.FailMessage)
											? "Delivery to this recipients is not allowed, message refused"
											: filterResult.FailMessage);
									return;
								}
							}

							var result = await processor.Server.MailDeliveryInterface.DoesUserExist(address);

							switch (result)
							{
								case UserExistsCodes.BadDestinationMailboxAddress:
									await processor.WriteCode(550, "5.1.1", "Requested action not taken: Bad destination mailbox address");
									return;

								case UserExistsCodes.BadDestinationSystemAddress:
									await processor.WriteCode(550, "5.1.2", "Requested action not taken: Bad destination system address");
									return;

								case UserExistsCodes.DestinationMailboxAddressAmbiguous:
									await processor.WriteCode(550, "5.1.4", "Requested action not taken: Destination mailbox address ambiguous");
									return;

								case UserExistsCodes.DestinationAddressHasMovedAndNoForwardingAddress:
									await processor.WriteCode(550, "5.1.6", "Requested action not taken: Destination mailbox has moved, No forwarding address");
									return;

								case UserExistsCodes.BadSendersSystemAddress:
									await processor.WriteCode(550, "5.1.8", "Requested action not taken: Bad sender's mailbox address syntax");
									return;

								default:
									processor.Transaction.DeliverTo.Add(address);
									await processor.WriteCode(250, "2.1.5");
									break;
							}
						}
					}
					break;

				case "DATA":
					if (processor.Transaction == null || processor.Transaction.DeliverTo.Count == 0)
					{
						await processor.WriteCode(503, "5.5.1", "RCPT TO first.");
						return;
					}

					processor.DataBuilder = new StringBuilder();
					processor.Counter = 0;
					processor.CaptureData = 1;
					await processor.WriteCode(354);
					break;
			}
		}

		internal static async Task ProcessData(ClientProcessor processor, string data)
		{
			data = data.Replace("\r", "");
			var dta = data.Split('\n');
			foreach (var dt in dta)
			{
				if (dt == ".")
				{
					processor.CaptureData = 0;
					processor.Transaction!.RawBody = processor.DataBuilder!.ToString();

					if (processor.Server.Options.MessageCharactersLimit != 0 &&
					    processor.Server.Options.MessageCharactersLimit < processor.Counter)
					{
						processor.Transaction = null;
						await processor.WriteCode(552, "5.4.3", "Message size exceeds the administrative limit.");
						return;
					}

					string received = string.Empty;

					if (!string.IsNullOrEmpty(processor.Username)) processor.Transaction.AuthenticatedUser = processor.Username;
					else if (processor.RemoteEndPoint == null) received = "from unknown ";
					else
					{
						var address = processor.RemoteEndPoint.Address;
						if (address.IsIPv4MappedToIPv6)
							address = address.MapToIPv4();

						received = $"from {address} ";
					}

					received += Invariant($"by {processor.Server.Options.ServerName} with SMTP; {DateTime.UtcNow:ddd, dd MMM yyyy HH:mm:ss} +0000 (UTC)");

					processor.Transaction.AddHeader("Received", received);

					if (processor.Transaction.SPFValidationResult != ValidationResult.UserAuthenticated && processor.Transaction.SPFValidationResult != ValidationResult.CheckDisabled)
						processor.Transaction.AddHeader("Authentication-Results", $"{processor.Server.Options.ServerName}; spf={processor.Transaction.SPFValidationResult.ToString().ToLowerInvariant()} smtp.mailfrom={processor.Transaction.FromDomain}");

					if (processor.Server.Options.ValidateDMARC)
					{
						// DMARC authenticates ONE identity (RFC 7489 §6.6.1), so the message must carry
						// exactly one From mailbox. Counting .From (top-level address entries) is not
						// enough: a single group address — "From: Team: a@evil.com, b@bank.com;" — is one
						// entry but several mailboxes, so it slipped past this gate while validation
						// authenticated only the first member. Count .Mailboxes, which flattens groups.
						if (processor.Transaction.ParsedMessage.From.Mailboxes.Count() > 1)
						{
							await processor.WriteCode(554, "5.7.1", "Message must not contain more than one From header, message refused");
							return;
						}

						if (processor.Username != null)
							processor.Transaction.DMARCValidationResult = ValidationResult.UserAuthenticated;
						else
						{
							var dmarcValidation = await processor.Server.DmarcValidator!.ValidateTransaction(processor.Transaction);
							processor.Transaction.DMARCValidationResult = dmarcValidation;

							if (dmarcValidation == ValidationResult.Fail)
							{
								await processor.WriteCode(554, "5.7.1", "Delivery not authorized by DMARC, message refused");
								return;
							}

							var fromDomain = GetAddressDomain(processor.Transaction.GetFrom);
							processor.Transaction.AddHeader("Authentication-Results", $"{processor.Server.Options.ServerName}; dmarc={dmarcValidation.ToString().ToLowerInvariant()} header.from={fromDomain ?? "(none)"}");
						}
					}
					else processor.Transaction.DMARCValidationResult = ValidationResult.CheckDisabled;

					if (processor.Server.Filter != null)
					{
						var filterResult = await processor.Server.Filter.CanProcessTransaction(processor.Transaction);

						if (filterResult.Type != SmtpResultType.Success)
						{
							processor.Transaction = null;
							await processor.WriteCode(554,
								filterResult.Type == SmtpResultType.PermanentFail ? "5.7.1" : "4.7.1",
								string.IsNullOrWhiteSpace(filterResult.FailMessage)
									? "Delivery not authorized, message refused"
									: filterResult.FailMessage);
							return;
						}
					}

					var delivery = (MailTransaction)processor.Transaction.Clone();
					processor.Transaction = null;

					SmtpDeliveryResult deliveryResult;
					try
					{
						deliveryResult = await processor.Server.DeliverMessage(delivery, processor.ConnectionToken);
					}
					catch (Exception ex)
					{
						processor.Server.LoggerInterface?.LogError("[DATA] Delivery handler threw before SMTP ACK: " + ex.GetType().FullName + ": " + ex.Message);
						await processor.WriteCode(451, "4.3.0", "Requested action aborted: local error in processing");
						return;
					}

					await processor.WriteCode((ushort)deliveryResult.StatusCode, deliveryResult.EnhancedStatus, deliveryResult.Message);
					return;
				}

				processor.Counter += (ulong)dt.Length;
				if (processor.Server.Options.MessageCharactersLimit == 0 ||
				    processor.Server.Options.MessageCharactersLimit >= processor.Counter)
				{
					processor.DataBuilder!.AppendLine(dt);
				}
			}
		}

		/// <summary>
		/// Extracts the domain from a bare email address ("user@example.com" → "example.com").
		/// </summary>
		/// <remarks>
		/// Distinct from <see cref="ProcessAddress"/>, which parses an SMTP command argument and
		/// therefore requires the RFC 5321 angle-bracket form ("&lt;user@example.com&gt;"). Header
		/// addresses from <see cref="MailTransaction.GetFrom"/> are already parsed by MimeKit and come
		/// without brackets, so they need this instead — feeding them to ProcessAddress returns null
		/// and silently disables DMARC (that was bug B1's second half).
		/// Applies the same validity rules ProcessAddress does: exactly one '@', and a dotted domain.
		/// </remarks>
		/// <param name="address">Bare email address, without angle brackets.</param>
		/// <returns>The domain, or null if the address is missing or malformed.</returns>
		internal static string? GetAddressDomain(string? address)
		{
			if (string.IsNullOrWhiteSpace(address))
				return null;

			var atIndex = address.LastIndexOf('@');
			var lastDotIndex = address.LastIndexOf('.');

			if (lastDotIndex == -1 || atIndex == -1 || lastDotIndex < atIndex)
				return null;

			if (address.Count(x => x == '@') != 1)
				return null;

			var domain = address[(atIndex + 1)..];

			return domain.Contains('.') ? domain : null;
		}

		/// <summary>
		/// Determines whether the text preceding a bracketed path is one of the two prefixes that may
		/// legitimately appear there.
		/// </summary>
		/// <remarks>
		/// Those are the ':' left by command parsing (<c>ProcessAddress</c> is called with arguments
		/// like ":&lt;a@b.c&gt;") with optional whitespace, and a quoted display name such as
		/// "John Doe" &lt;john@example.com&gt;. Allowing arbitrary text here is what let
		/// "ceo@victim.example &lt;&gt;" read as a null reverse-path.
		/// </remarks>
		/// <param name="prefix">Everything before the path's opening '&lt;'.</param>
		/// <returns>True if the prefix is permitted.</returns>
		private static bool IsAllowedPathPrefix(string prefix)
		{
			var trimmed = prefix.Trim();

			if (trimmed.Length == 0 || trimmed == ":")
				return true;

			if (trimmed[0] == ':')
				trimmed = trimmed[1..].TrimStart();

			if (trimmed.Length == 0)
				return true;

			// What remains must be exactly ONE quoted display name. Checking only the first and last
			// character is not enough: "\"x\" ceo@victim.example \"" starts and ends with a quote while
			// carrying a bare address between them, which would let the following <> pass as a null
			// reverse-path. Walk the quoted-string to its first unescaped closing quote and require
			// nothing but whitespace after it.
			if (trimmed[0] != '"')
				return false;

			var i = 1;

			while (i < trimmed.Length && trimmed[i] != '"')
			{
				// RFC 5321 quoted-pair: a backslash escapes the next character, quote included.
				if (trimmed[i] == '\\')
					i++;

				i++;
			}

			// Unterminated quoted-string.
			if (i >= trimmed.Length)
				return false;

			return trimmed[(i + 1)..].Trim().Length == 0;
		}

		/// <summary>
		/// Determines whether the text following a null reverse-path is entirely well-formed ESMTP
		/// parameters, rather than a bare address masquerading as one.
		/// </summary>
		/// <remarks>
		/// RFC 5321 §4.1.2 gives esmtp-param = esmtp-keyword ["=" esmtp-value], with parameters
		/// separated by spaces and neither keyword nor value containing a space. This checks the shape
		/// only — unknown keywords are still ignored elsewhere, matching the server's
		/// parse-but-do-not-act stance on SIZE= and BODY=. What it refuses is a token that is not a
		/// keyword/value pair at all, which is what a smuggled "&lt;ceo@victim.example&gt;" is.
		/// </remarks>
		/// <param name="suffix">Everything after the closing '&gt;' of an empty reverse-path.</param>
		/// <returns>True if every whitespace-separated token is a valid ESMTP parameter.</returns>
		private static bool IsEsmtpParameterSuffix(string suffix)
		{
			if (suffix.Length == 0)
				return true;

			// Parameters are separated from the path, and from each other, by a space.
			if (suffix[0] != ' ')
				return false;

			foreach (var token in suffix.Split(' ', StringSplitOptions.RemoveEmptyEntries))
			{
				var eq = token.IndexOf('=', StringComparison.Ordinal);
				var keyword = eq == -1 ? token : token[..eq];

				// esmtp-keyword = (ALPHA / DIGIT) *(ALPHA / DIGIT / "-"). Checking this is what stops a
				// bare address being taken for a valueless keyword: "ceo@victim.example" fails on '@'.
				// ASCII-only on purpose — char.IsLetterOrDigit would admit non-ASCII keywords.
				if (keyword.Length == 0 || !IsAsciiLetterOrDigit(keyword[0]))
					return false;

				foreach (var c in keyword)
				{
					if (!IsAsciiLetterOrDigit(c) && c != '-')
						return false;
				}

				if (eq == -1)
					continue;

				// Only one '=' separates keyword from value, so "X=foo=<addr>" is refused.
				if (token.IndexOf('=', eq + 1) != -1)
					return false;

				// esmtp-value = 1*(%d33-60 / %d62-126): printable ASCII except '=' (61). Validating this
				// is what stops "SIZE=1234	<ceo@victim.example>" being one token that smuggles an
				// address — HTAB is not a value character, but splitting on spaces alone kept it inside
				// the token. Also excludes controls and non-ASCII.
				var value = token[(eq + 1)..];

				if (value.Length == 0)
					return false;

				foreach (var c in value)
				{
					if (c < (char)33 || c > (char)126 || c == '=')
						return false;
				}
			}

			return true;
		}

		private static bool IsAsciiLetterOrDigit(char c) =>
			c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

		/// <summary>
		/// Locates the bracketed path at the start of an SMTP address argument.
		/// </summary>
		/// <remarks>
		/// <para>
		/// The path is the FIRST angle-bracket pair, and nothing before it may contain '&lt;' or '&gt;'.
		/// That prefix rule is what makes this anchored: it still tolerates the prefixes the protocol
		/// and this codebase actually produce — a leading ':' left by command parsing, and a display
		/// name such as "John Doe" &lt;john@example.com&gt; — while refusing an argument that hides the
		/// real path behind something bracket-bearing.
		/// </para>
		/// <para>
		/// Without that rule, a search for the first '&lt;' anywhere lets "AUTH=&lt;&gt; &lt;ceo@victim.example&gt;"
		/// read as a null reverse-path: the caller sees an empty sender and skips SPF while a real
		/// address sits in the command — a parser/policy differential. "&gt;&lt;&gt;" is refused for the
		/// same reason. Trailing ESMTP parameters are still ignored, including bracket-bearing ones
		/// like O365's AUTH=&lt;&gt;, because they follow the closing '&gt;'.
		/// </para>
		/// </remarks>
		/// <param name="data">The command argument (everything after "MAIL FROM:" / "RCPT TO:").</param>
		/// <param name="path">The text between the brackets, unvalidated; empty for "&lt;&gt;".</param>
		/// <returns>True if a well-formed bracketed path was found at the anchored position.</returns>
		private static bool TryGetBracketedPath(string? data, out string path)
		{
			path = string.Empty;

			if (data == null)
				return false;

			var open = data.IndexOf('<', StringComparison.Ordinal);
			if (open == -1)
				return false;

			// A '>' before the path's '<' is malformed — e.g. "><>".
			var firstClose = data.IndexOf('>', StringComparison.Ordinal);
			if (firstClose < open)
				return false;

			// Only two prefixes legitimately precede the path: the ':' left by command parsing (with
			// optional surrounding whitespace), and an RFC 5322 quoted display name. Anything else is
			// refused, so "ceo@victim.example <>" cannot present itself as a null reverse-path with the
			// real address written off to one side.
			if (!IsAllowedPathPrefix(data[..open]))
				return false;

			var close = data.IndexOf('>', open);
			if (close == -1)
				return false;

			// Everything after the path must be ESMTP parameters (RFC 5321 §4.1.2:
			// esmtp-param = esmtp-keyword ["=" esmtp-value], whitespace-separated). A later bracket pair
			// is legitimate only as a parameter VALUE — O365 sends "<> BODY=8BITMIME AUTH=<>" — and is an
			// attack when it is a bare path instead: "AUTH=<> <ceo@victim.example>" and
			// "<><ceo@victim.example>" hide a real address behind an empty pair. Read as a null sender,
			// those would leave filters seeing an empty sender and SPF skipped while a real address sat
			// in the command.
			//
			// The whole suffix is validated, not just the next bracket. Checking only the following '<'
			// left "<> AUTH=<> <ceo@victim.example>" smuggleable, because the AUTH value's '<' satisfied
			// the check and the bare address after it was never examined.
			if (close == open + 1 && !IsEsmtpParameterSuffix(data[(close + 1)..]))
				return false;

			path = data[(open + 1)..close];

			// A '<' inside the path means the pair is not the path.
			return !path.Contains('<', StringComparison.Ordinal);
		}

		/// <summary>
		/// Determines whether a MAIL FROM argument carries the RFC 5321 §4.5.5 null reverse-path
		/// ("&lt;&gt;"), as every DSN/bounce does.
		/// </summary>
		/// <remarks>
		/// Uses the same anchored path locator as <see cref="ProcessAddress"/>, so the two cannot
		/// disagree about which bracket pair is the reverse-path. See
		/// <see cref="TryGetBracketedPath"/> for why that matters.
		/// </remarks>
		/// <param name="data">The MAIL FROM command argument.</param>
		/// <returns>True if the reverse-path is present and empty.</returns>
		internal static bool IsNullReversePath(string? data) =>
			TryGetBracketedPath(data, out var path) && path.Length == 0;

		internal static string? ProcessAddress(string? data, out string? domain)
		{
			domain = null;
			if (data == null)
				return null;

			if (!TryGetBracketedPath(data, out var address))
				return null;

			if (string.IsNullOrWhiteSpace(address))
				return null;

			var lastDotIndex = address.LastIndexOf('.');
			var atIndex = address.LastIndexOf('@');

			if (lastDotIndex == -1 || atIndex == -1 || lastDotIndex < atIndex)
				return null;

			if (address.Count(x => x == '@') != 1)
				return null;

			domain = address[(atIndex + 1)..];

			if (domain.Contains('.'))
				return address;

			domain = null;
			return null;
		}
	}
}