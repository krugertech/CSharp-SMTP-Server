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
		internal MailTransaction(string from, string fromDomain, ValidationResult validationResult, bool isNullReversePath = false)
		{
			From = from;
			FromDomain = fromDomain;
			IsNullReversePath = isNullReversePath;
			SPFValidationResult = validationResult;
			DeliverTo = new List<string>();
			AuthenticatedUser = null;
			RawBody = string.Empty;
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
		/// Raw message body
		/// </summary>
		public string RawBody;

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

				_parsedMessage = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(RawBody)));
				return _parsedMessage;
			}
		}

		private MimeMessage? _parsedMessage;

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
			RawBody = $"{name}: {value}\r\n{RawBody}";
			ParsedMessage.Headers.Add(name, value);
		}

		/// <inheritdoc />
		public object Clone()
		{
			return new MailTransaction(From, FromDomain, SPFValidationResult, IsNullReversePath)
			{
				AuthenticatedUser = AuthenticatedUser,
				RawBody = RawBody,
				_parsedMessage = ParsedMessage,
				RemoteEndPoint = RemoteEndPoint,
				DeliverTo = new List<string>(DeliverTo),
				Encryption = Encryption,
				DMARCValidationResult = DMARCValidationResult
			};
		}
	}
}