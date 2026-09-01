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
					processor.DiscardTransaction();
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
							else if (processor.Server.Options.ValidateSPF && processor.RemoteEndPoint != null)
							{
								// RFC 7208 §2.4: when the reverse-path is null there is no envelope sender to
								// check, and the identity becomes postmaster@<HELO domain> — so the HELO domain
								// is checked in its place rather than SPF being skipped. Skipping was the old
								// behaviour and it left a null sender unauthenticated in either direction:
								// combined with DKIM being unimplemented, DMARC had nothing to align and a
								// spoofed From under p=reject was accepted.
								//
								// A client that sent an address literal or a non-DNS name has no checkable
								// identity at all; that stays unchecked, because there is nothing to look up.
								var checkDomain = nullReversePath ? processor.HeloDomain : domain;

								if (checkDomain != null)
								{
									if (processor.SpfResultsCache!.TryGetValue(checkDomain, out var spfRes))
										spfValidation = spfRes;
									else
									{
										spfValidation = await processor.Server.SpfValidator!.CheckHost(processor.RemoteEndPoint.Address, checkDomain);
										processor.SpfResultsCache.Add(checkDomain, spfValidation);
									}

									if (spfValidation == ValidationResult.Fail)
									{
										await processor.WriteCode(554, "5.7.23", "Delivery not authorized by SPF, message refused");
										return;
									}
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

							processor.Transaction = new MailTransaction(address, domain!, spfValidation, nullReversePath, processor.HeloDomain)
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

					processor.Counter = 0;
					processor.DataTruncated = false;
					processor.DataBareLf = false;
					processor.CaptureData = 1;
					await processor.WriteCode(354);
					break;
			}
		}

		/// <summary>
		/// Consumes one line of DATA, as the bytes that arrived on the wire.
		/// </summary>
		/// <remarks>
		/// <para>
		/// Bytes rather than a string: a message body is an octet stream that may carry any charset or
		/// none, and the previous path decoded each line to UTF-16 and re-encoded it as UTF-8 on the
		/// way into the body store. Every byte that was not valid UTF-8 became U+FFFD and was stored as
		/// EF BF BD, so the archived message was not what the sender transmitted — which matters
		/// directly to a downstream DKIM verifier, since it hashes the octets it is handed and a
		/// transcode invalidates the signature.
		/// </para>
		/// <para>
		/// The buffer is borrowed from the reader and is overwritten by the next read, so it is copied
		/// into the body store before this returns and never retained.
		/// </para>
		/// </remarks>
		/// <param name="processor">The connection.</param>
		/// <param name="buffer">Buffer holding the line, without its terminator.</param>
		/// <param name="length">Length of the line in bytes.</param>
		internal static async Task ProcessData(ClientProcessor processor, byte[] buffer, int length)
		{
			// DATA is only entered with a transaction open, and every path that abandons one also
			// clears CaptureData, so this cannot normally be null. Asserted rather than assumed because
			// the alternative on an unexpected interleaving is an NRE inside the receive loop.
			if (processor.Transaction == null)
			{
				processor.CaptureData = 0;
				return;
			}

			{
				// A dot line is the end of DATA only when it was terminated by CRLF. RFC 5321 §4.1.1.4
				// is explicit that <LF>.<LF> MUST NOT be treated as equivalent to <CRLF>.<CRLF>, and
				// honouring it here would be the smuggling vulnerability itself rather than merely a
				// conformance gap: leaving DATA capture hands every octet the client sent after the
				// bare-LF dot to the command parser, on a connection an upstream hop has already
				// authenticated. A pipelined MAIL FROM/RCPT TO/DATA after such a dot then delivers a
				// second, injected message under the first one's SPF and DMARC results.
				//
				// So the dot is treated as ordinary body content and capture continues. The message is
				// already doomed — DataBareLf is latched, and the transaction is refused at whatever
				// conforming terminator eventually arrives — but staying in DATA is what keeps the
				// attacker's trailing octets inert, because body bytes are stored, never executed.
				if (IsTerminatingDot(buffer, length) && !processor.LastLineWasBareLf)
				{
					processor.CaptureData = 0;

					if (processor.Server.Options.MessageCharactersLimit != 0 &&
					    processor.Server.Options.MessageCharactersLimit < processor.Counter)
					{
						// Disposing releases the body's temp file, if it spilled to one. Dropping the
						// reference alone would leave the file until finalization — and an oversized
						// message is precisely the case that has one.
						processor.DiscardTransaction();
						await processor.WriteCode(552, "5.4.3", "Message size exceeds the administrative limit.");
						return;
					}

					// A line exceeded BoundedLineReader.MaxLineLength and its tail was discarded, so the
					// stored body is not the message that was sent. Refusing is the only honest answer:
					// this path only ever saw the retained prefix, so both the stored bytes and the
					// counted length understate the real message, and acknowledging it with 250 would
					// deliver a silently truncated record. For a journaling relay that is worse than a
					// refusal, because nothing downstream can tell the message was cut.
					//
					// 552 is the correct code — RFC 5321 §4.5.3.1.6 makes an over-long line a size
					// condition, and it is the same permanent class as the limit check above.
					if (processor.DataTruncated)
					{
						processor.DiscardTransaction();
						await processor.WriteCode(552, "5.4.3", "Line length exceeds the administrative limit.");
						return;
					}

					// RFC 5321 §2.3.8 and §4.1.1.4: CR and LF may appear only together, as a terminator,
					// and a server MUST NOT accept lines ending in LF alone "even in the name of improved
					// robustness" — the text names <LF>.<LF> specifically. Honouring a bare LF is the
					// SMTP-smuggling class disclosed in December 2023: when this hop and the next
					// disagree about what ends a message, one submission is read as one message here and
					// as two elsewhere, and the smuggled message inherits this connection's
					// authentication, SPF result and DMARC pass. For a journaling relay that means a
					// forged record archived as authenticated.
					//
					// Refusing is also what keeps the archive faithful. The alternative — silently
					// rewriting bare LF to CRLF, which is what this server used to do — changes the
					// octets the sender transmitted and so invalidates any DKIM signature over them,
					// destroying the origin proof that is the whole point of preserving the bytes.
					// Exchange Online reaches the same conclusion and rejects with
					// SMTPSEND.BareLinefeedsAreIllegal, having deliberately stopped stripping bare LFs
					// for exactly this reason, so refusing costs no mail an Office 365 sender could send.
					//
					// 5.6.0 is the enhanced code for undeliverable message content (RFC 3463 §3.6).
					if (processor.DataBareLf)
					{
						processor.DiscardTransaction();
						await processor.WriteCode(554, "5.6.0",
							"Message contains bare linefeeds, which cannot be accepted via DATA.");
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
					{
						// RFC 8601 §2.7.2: the property names the identity that was actually checked.
						// For a null reverse-path that is the HELO domain (RFC 7208 §2.4), not the
						// envelope sender — reporting smtp.mailfrom= with the empty FromDomain would
						// claim to have checked an identity that does not exist.
						var identity = processor.Transaction.IsNullReversePath
							? $"smtp.helo={processor.Transaction.HeloDomain}"
							: $"smtp.mailfrom={processor.Transaction.FromDomain}";

						processor.Transaction.AddHeader("Authentication-Results", $"{processor.Server.Options.ServerName}; spf={processor.Transaction.SPFValidationResult.ToString().ToLowerInvariant()} {identity}");
					}

					if (processor.Server.Options.ValidateDMARC)
					{
						// DMARC authenticates ONE identity (RFC 7489 §6.6.1), so the message must carry
						// exactly one From mailbox. Counting .From (top-level address entries) is not
						// enough: a single group address — "From: Team: a@evil.com, b@bank.com;" — is one
						// entry but several mailboxes, so it slipped past this gate while validation
						// authenticated only the first member. Count .Mailboxes, which flattens groups.
						if (processor.Transaction.ParsedMessage.From.Mailboxes.Count() > 1)
						{
							processor.DiscardTransaction();
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
								processor.DiscardTransaction();
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
							processor.DiscardTransaction();
							await processor.WriteCode(554,
								filterResult.Type == SmtpResultType.PermanentFail ? "5.7.1" : "4.7.1",
								string.IsNullOrWhiteSpace(filterResult.FailMessage)
									? "Delivery not authorized, message refused"
									: filterResult.FailMessage);
							return;
						}
					}

					// Clone() hands the body over rather than copying it, so the clone is now its sole
					// owner and the processor's reference is cleared without disposing.
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
					finally
					{
						// Releases the body's temp file once the handler is done with it. In finally so a
						// handler that throws — the 451/retry path, which is the one a backend outage
						// takes — does not leak a file per message for as long as the outage lasts.
						//
						// This is why GetBodyStream() is documented as valid only for the duration of the
						// call: a handler that stashes the transaction for later gets a disposed body.
						delivery.Body.Dispose();
					}

					await processor.WriteCode((ushort)deliveryResult.StatusCode, deliveryResult.EnhancedStatus, deliveryResult.Message);
					return;
				}

				// RFC 5321 §4.5.2 transparency: the sending client prefixes an extra '.' to any line
				// that already begins with one, and the receiver MUST strip it. Without this a body
				// line "..text" was stored verbatim, so what reached the archive was not what the
				// sender composed — silent corruption of exactly the leading-dot lines the mechanism
				// exists to carry, and enough on its own to break a DKIM signature over that body.
				//
				// Stripping is a one-byte offset here rather than a string operation because the line
				// is still the wire's bytes; the '.' is ASCII 0x2E and cannot be part of a multi-byte
				// sequence, so this is safe on any charset.
				var offset = 0;

				if (length > 0 && buffer[0] == (byte)'.')
				{
					offset = 1;
					length--;
				}

				// Counted AFTER unstuffing, so the limit measures the message as stored rather than as
				// framed: the stuffing dot is transport, not content, and charging the sender for it
				// would make the enforced limit depend on how many of its body lines happen to start
				// with a dot.
				processor.Counter += (ulong)length;

				// Over-limit data is counted but not stored, so an oversized message costs the limit in
				// memory rather than its own size — the property the 552-at-the-dot design relies on.
				if (processor.Server.Options.MessageCharactersLimit == 0 ||
				    processor.Server.Options.MessageCharactersLimit >= processor.Counter)
				{
					// Written straight into the body store — a file, past the spill threshold — rather
					// than accumulated in a StringBuilder that then has to be ToString()'d, cloned and
					// re-encoded. CRLF is explicit: AppendLine emitted Environment.NewLine, so the
					// stored message had bare LF on Linux.
					processor.Transaction!.Body.WriteLine(buffer, offset, length);
				}
			}
		}

		/// <summary>
		/// Determines whether a DATA line is the terminating dot that ends the message.
		/// </summary>
		/// <remarks>
		/// The terminator is a line consisting of exactly one '.' (RFC 5321 §4.1.1.4). A line of "..",
		/// which unstuffs to a literal ".", is body content and must not end the message — comparing
		/// the bytes rather than a trimmed string keeps the two distinct.
		/// </remarks>
		private static bool IsTerminatingDot(byte[] buffer, int length) =>
			length == 1 && buffer[0] == (byte)'.';

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

			// Quote-aware for the same reason ProcessAddress is: MimeKit hands back the address form of
			// a quoted local-part with its quotes intact, so "a@b"@example.com arrives here carrying an
			// '@' that does not separate local-part from domain.
			var atIndex = LastIndexOfUnquoted(address, '@');

			if (atIndex == -1 || LastIndexOfUnquoted(address[..atIndex], '@') != -1)
				return null;

			var lastDotIndex = address.LastIndexOf('.');

			if (lastDotIndex == -1 || lastDotIndex < atIndex)
				return null;

			var domain = address[(atIndex + 1)..];

			return domain.Contains('.') && !domain.Contains('"', StringComparison.Ordinal) ? domain : null;
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

			var close = FindPathEnd(data, open);
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

			// A '<' inside the path means the pair is not the path — but only outside a quoted-string,
			// where it is an ordinary character of the local-part. Scanning quote-aware is what lets
			// <"a<b"@example.com> through while still refusing "<><ceo@victim.example>".
			return !ContainsUnquoted(path, '<');
		}

		/// <summary>
		/// Finds the '&gt;' that closes a bracketed path, ignoring any that falls inside a
		/// quoted-string.
		/// </summary>
		/// <remarks>
		/// <para>
		/// RFC 5321 §4.1.2 allows the local-part to be a quoted-string, inside which '&lt;' and '&gt;'
		/// are ordinary characters: &lt;"a&gt;b"@example.com&gt; is a valid, if unusual, address.
		/// Treating the first '&gt;' as the terminator split that path at the wrong place, leaving
		/// "a" as the address, which then failed validation and produced a permanent 501 for a
		/// legitimate address. Shared by RCPT TO, so it could lose a recipient as well as a sender.
		/// </para>
		/// <para>
		/// Quoted-pairs are honoured: a backslash escapes the following character, so a quote can
		/// appear inside the quoted-string without ending it. An unterminated quoted-string yields no
		/// terminator rather than falling back to the first '&gt;' — the argument is malformed, and
		/// guessing where the path ends is what the anchored parsing elsewhere in this file exists to
		/// avoid.
		/// </para>
		/// </remarks>
		/// <param name="data">The command argument.</param>
		/// <param name="open">Index of the path's opening '&lt;'.</param>
		/// <returns>Index of the closing '&gt;', or -1 if there is none outside a quoted-string.</returns>
		private static int FindPathEnd(string data, int open)
		{
			var inQuotes = false;

			for (var i = open + 1; i < data.Length; i++)
			{
				var c = data[i];

				if (inQuotes)
				{
					// A backslash escapes the next character, the closing quote included.
					if (c == '\\')
					{
						i++;
						continue;
					}

					if (c == '"')
						inQuotes = false;

					continue;
				}

				if (c == '"')
					inQuotes = true;
				else if (c == '>')
					return i;
			}

			return -1;
		}

		/// <summary>
		/// Determines whether a path contains the given character outside any quoted-string.
		/// </summary>
		/// <param name="path">The text between the path's brackets.</param>
		/// <param name="value">The character to look for.</param>
		/// <returns>True if the character occurs outside a quoted-string.</returns>
		private static bool ContainsUnquoted(string path, char value)
		{
			var inQuotes = false;

			for (var i = 0; i < path.Length; i++)
			{
				var c = path[i];

				if (inQuotes)
				{
					if (c == '\\')
					{
						i++;
						continue;
					}

					if (c == '"')
						inQuotes = false;

					continue;
				}

				if (c == '"')
					inQuotes = true;
				else if (c == value)
					return true;
			}

			return false;
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

			// The local-part/domain split is the last '@' OUTSIDE a quoted-string. RFC 5321 §4.1.2
			// permits a quoted local-part, inside which '@' is an ordinary character, so
			// <"a@b"@example.com> carries two '@' and only the second separates the parts. Requiring
			// exactly one '@' anywhere in the address refused that outright.
			var atIndex = LastIndexOfUnquoted(address, '@');

			if (atIndex == -1)
				return null;

			// Exactly one unquoted '@': more than one is ambiguous and malformed regardless of quoting.
			if (LastIndexOfUnquoted(address[..atIndex], '@') != -1)
				return null;

			var lastDotIndex = address.LastIndexOf('.');

			if (lastDotIndex == -1 || lastDotIndex < atIndex)
				return null;

			domain = address[(atIndex + 1)..];

			// A quoted-string is only a local-part construct; the domain must not contain one, or a
			// trailing quote could hide the dot this check relies on.
			if (domain.Contains('.') && !domain.Contains('"', StringComparison.Ordinal))
				return address;

			domain = null;
			return null;
		}

		/// <summary>
		/// Finds the last occurrence of a character outside any quoted-string.
		/// </summary>
		/// <param name="path">The text to scan.</param>
		/// <param name="value">The character to look for.</param>
		/// <returns>Index of the last unquoted occurrence, or -1.</returns>
		private static int LastIndexOfUnquoted(string path, char value)
		{
			var inQuotes = false;
			var found = -1;

			for (var i = 0; i < path.Length; i++)
			{
				var c = path[i];

				if (inQuotes)
				{
					if (c == '\\')
					{
						i++;
						continue;
					}

					if (c == '"')
						inQuotes = false;

					continue;
				}

				if (c == '"')
					inQuotes = true;
				else if (c == value)
					found = i;
			}

			// An unterminated quoted-string leaves the split point undecidable; refuse rather than
			// guess, consistent with FindPathEnd.
			return inQuotes ? -1 : found;
		}
	}
}