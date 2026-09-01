using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using CSharp_SMTP_Server.Networking;
using CSharp_SMTP_Server.Protocol;
using MimeKit;

namespace CSharp_SMTP_Server
{
	/// <summary>
	/// SMTP transaction
	/// </summary>
	public class MailTransaction : ICloneable
	{
		internal MailTransaction(string from, string fromDomain, ValidationResult validationResult, bool isNullReversePath = false, string? heloDomain = null)
		{
			From = from;
			FromDomain = fromDomain;
			IsNullReversePath = isNullReversePath;
			HeloDomain = heloDomain;
			SPFValidationResult = validationResult;
			DeliverTo = new List<string>();
			AuthenticatedUser = null;
			Body = new MessageBody();
		}

		/// <summary>
		/// Mail sender
		/// </summary>
		public readonly string From;

		/// <summary>
		/// Mail sender domain
		/// If SPF validation is enabled, this domain is validated
		/// </summary>
		public readonly string FromDomain;

		/// <summary>
		/// True when the sender used the RFC 5321 §4.5.5 null reverse-path (<c>MAIL FROM:&lt;&gt;</c>),
		/// as every DSN/bounce does.
		/// </summary>
		/// <remarks>
		/// Distinguishes "there is no envelope sender" from "the envelope sender is an empty string".
		/// Both leave <see cref="From"/> and <see cref="FromDomain"/> empty, but only the former is a
		/// valid transaction, and treating an absent identity as a mismatched one fails closed: DMARC
		/// would find nothing to align and reject a legitimate bounce under <c>p=reject</c>.
		/// </remarks>
		public readonly bool IsNullReversePath;

		/// <summary>
		/// The domain the client gave in its EHLO/HELO, or null if it gave none that could be checked.
		/// </summary>
		/// <remarks>
		/// RFC 7208 §2.4 makes this the SPF identity when the reverse-path is null: the check runs
		/// against <c>postmaster@&lt;HELO domain&gt;</c>, since there is no envelope sender to check
		/// instead. It is also the domain DMARC aligns for such a message — see
		/// <see cref="Protocol.DMARC.DmarcValidator"/>. Null when the client sent an address literal or
		/// a name that is not a DNS domain, neither of which can carry an SPF record.
		/// </remarks>
		public readonly string? HeloDomain;

		/// <summary>
		/// The message body, as a stream-backed store.
		/// </summary>
		/// <remarks>
		/// Owned by the transaction: a clone shares this instance rather than copying the bytes, and
		/// the server disposes it once delivery has been acknowledged.
		/// </remarks>
		internal MessageBody Body;

		/// <summary>
		/// Opens a forward-only stream over the raw message — the server's prepended headers followed
		/// by the body as it arrived on the wire.
		/// </summary>
		/// <remarks>
		/// <para>
		/// This is the allocation-free way to consume a message, and the one a delivery handler should
		/// prefer: a handler that persists the message to disk or to object storage can copy this
		/// stream straight to its destination without the message ever existing as a .NET string.
		/// </para>
		/// <para>
		/// The returned stream must be disposed, and is valid only for the duration of the
		/// <see cref="Interfaces.IMailDelivery.EmailReceivedAsync"/> call — the transaction's storage
		/// (including its temp file, for a large message) is released once that returns.
		/// </para>
		/// </remarks>
		/// <returns>A readable stream positioned at the start of the message.</returns>
		public Stream GetBodyStream() => Body.OpenRead();

		/// <summary>
		/// Length of the raw message in bytes, headers included.
		/// </summary>
		/// <remarks>
		/// Available without materializing the message, unlike <c>RawBody.Length</c> — and a byte
		/// count rather than a character count.
		/// </remarks>
		public long BodyLength => Body.Length;

		/// <summary>
		/// Raw message body, as text.
		/// </summary>
		/// <remarks>
		/// <para>
		/// Materializes the entire message in memory as UTF-16 — two bytes per character — on every
		/// read, and is the property that made a 150 MB message cost gigabytes. Kept for compatibility
		/// and for small messages, where it is harmless; use <see cref="GetBodyStream"/> for anything
		/// that may be large.
		/// </para>
		/// <para>
		/// Assigning to it replaces the body wholesale, discarding anything accumulated so far.
		/// </para>
		/// </remarks>
		public string RawBody
		{
			get => Body.ReadAsString();

			set
			{
				Body.Dispose();
				Body = new MessageBody(value ?? string.Empty);
				_parsedMessage = null;
			}
		}

		/// <summary>
		/// Subject of the message
		/// </summary>
		public string? Subject => ParsedMessage.Subject;

		/// <summary>
		/// Recipients specified in the transaction
		/// </summary>
		public List<string> DeliverTo { get; private set; }

		/// <summary>
		/// Sender of the message specified in the header, as an email address (e.g. "user@example.com").
		/// Returns null if the From header is missing or contains no usable mailbox.
		/// Note that this is NOT validated using SPF.
		/// </summary>
		/// <remarks>
		/// Returns the address, NOT the display name. Before v1.2.0 this returned MimeKit's display
		/// name ("" for "user@example.com", "John" for "John &lt;j@e.c&gt;"), which made DMARC
		/// validation inert. Use <see cref="GetFromName"/> if you need the display name.
		/// </remarks>
		public string? GetFrom => ParsedMessage.From.Mailboxes.FirstOrDefault()?.Address;

		/// <summary>
		/// Display name of the sender specified in the header (e.g. "John Doe"), or null if the From
		/// header is missing or contains no usable mailbox. Empty for an address with no display name.
		/// </summary>
		public string? GetFromName => ParsedMessage.From.Mailboxes.FirstOrDefault()?.Name;

		/// <summary>
		/// Recipients specified in the header (To), as email addresses.
		/// Group addresses are flattened to the mailboxes they contain.
		/// </summary>
		/// <remarks>Returns addresses, NOT display names — see <see cref="GetFrom"/>.</remarks>
		public IEnumerable<string> GetTo() => ParsedMessage.To.Mailboxes.Select(x => x.Address);

		/// <summary>
		/// Recipients specified in the header (CC), as email addresses.
		/// Group addresses are flattened to the mailboxes they contain.
		/// </summary>
		/// <remarks>Returns addresses, NOT display names — see <see cref="GetFrom"/>.</remarks>
		public IEnumerable<string> GetCc() => ParsedMessage.Cc.Mailboxes.Select(x => x.Address);

		/// <summary>
		/// Recipients specified in the header (BCC), as email addresses.
		/// Group addresses are flattened to the mailboxes they contain.
		/// </summary>
		/// <remarks>Returns addresses, NOT display names — see <see cref="GetFrom"/>.</remarks>
		public IEnumerable<string> GetBcc() => ParsedMessage.Bcc.Mailboxes.Select(x => x.Address);

		/// <summary>
		/// Returns email body without headers
		/// </summary>
		/// <returns>Email body</returns>
		public string? GetMessageBody() => ParsedMessage.TextBody ?? ParsedMessage.HtmlBody;

		/// <summary>
		/// Parsed email message
		/// </summary>
		public MimeMessage ParsedMessage
		{
			get
			{
				if (_parsedMessage != null) return _parsedMessage;

				// Loaded from the body stream rather than from RawBody: MimeKit is stream-native, so
				// this parses without the message first existing as a UTF-16 string and then being
				// re-encoded back to bytes — two full copies the old path paid on every first access.
				using (var stream = Body.OpenRead())
					_parsedMessage = MimeMessage.Load(stream);

				return _parsedMessage;
			}
		}

		/// <summary>
		/// The parsed message, if one has been produced, held on the body rather than on this instance.
		/// </summary>
		/// <remarks>
		/// A clone shares the body, so putting the cache there is what lets the clone reuse the
		/// original's parsed instance — the documented <c>Clone()</c> behaviour — without <c>Clone()</c>
		/// having to force a parse of every message on its way to delivery.
		/// </remarks>
		private MimeMessage? _parsedMessage
		{
			get => Body.ParsedMessageCache;
			set => Body.ParsedMessageCache = value;
		}

		/// <summary>
		/// Endpoint of the client/server sending the message
		/// </summary>
		public IPEndPoint? RemoteEndPoint { get; internal set; }

		/// <summary>
		/// Username of authenticated users. Empty if user is not authenticated.
		/// </summary>
		public string? AuthenticatedUser { get; internal set; }

		/// <summary>
		/// Encryption used for receiving this message
		/// </summary>
		public ConnectionEncryption Encryption { get; internal set; }

		/// <summary>
		/// SPF validation result
		/// </summary>
		// ReSharper disable once MemberCanBePrivate.Global
		// ReSharper disable once InconsistentNaming
		public readonly ValidationResult SPFValidationResult;

		/// <summary>
		/// DMARC validation result
		/// </summary>
		// ReSharper disable once MemberCanBePrivate.Global
		// ReSharper disable once InconsistentNaming
		// ReSharper disable once UnusedAutoPropertyAccessor.Global
		public ValidationResult DMARCValidationResult { get; internal set; }

		/// <summary>
		/// Adds a header to the email message
		/// </summary>
		/// <param name="name">Header name</param>
		/// <param name="value">Header value</param>
		public void AddHeader(string name, string value)
		{
			// Recorded on the body rather than written into it: prepending to a 150 MB message by
			// rewriting it is exactly the copy the streaming path exists to avoid. The header is
			// spliced in ahead of the body when the body is read.
			Body.PrependHeader(name, value);

			// Only an ALREADY-parsed message is updated. Reading ParsedMessage here would parse the
			// body for the sole purpose of adding a header to it, and when the parse instead happens
			// after this call it picks the header up from the body on its own — adding it here too is
			// what made the header appear twice in ParsedMessage while RawBody had one (bug B2).
			_parsedMessage?.Headers.Add(name, value);
		}

		/// <inheritdoc />
		public object Clone()
		{
			// The body is SHARED, not copied: duplicating it is a second full copy of the message, which
			// for a 150 MB journal report was the single largest allocation on the delivery path.
			var clone = new MailTransaction(From, FromDomain, SPFValidationResult, IsNullReversePath, HeloDomain)
			{
				AuthenticatedUser = AuthenticatedUser,
				RemoteEndPoint = RemoteEndPoint,
				DeliverTo = new List<string>(DeliverTo),
				Encryption = Encryption,
				DMARCValidationResult = DMARCValidationResult
			};

			// The empty body the constructor made is discarded unread; it never spilled, so there is
			// nothing to release. Sharing the body also shares the parsed-message cache that lives on
			// it, which is how the clone reuses the original's MimeMessage instance — lazily, without
			// either side being forced to parse.
			clone.Body = Body;

			return clone;
		}
	}
}