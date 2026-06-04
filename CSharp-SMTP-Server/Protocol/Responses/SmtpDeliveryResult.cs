using System;

namespace CSharp_SMTP_Server.Protocol.Responses
{
	/// <summary>
	/// Result returned by the delivery handler, mapped directly to the SMTP response sent to the client.
	/// </summary>
	public sealed class SmtpDeliveryResult
	{
		private SmtpDeliveryResult(int statusCode, string enhancedStatus, string message)
		{
			StatusCode = statusCode;
			// Reject CR/LF in the message text to prevent SMTP response-splitting at the point
			// where this value is written into the wire response by ClientProcessor.WriteCode.
			if (message.IndexOf('\r') >= 0 || message.IndexOf('\n') >= 0)
				throw new ArgumentException("SMTP response message must not contain CR or LF characters.", nameof(message));
			// The enhanced status (RFC 3463, e.g. "5.1.1") is also emitted on the wire, so guard it too.
			if (enhancedStatus.IndexOf('\r') >= 0 || enhancedStatus.IndexOf('\n') >= 0)
				throw new ArgumentException("SMTP enhanced status must not contain CR or LF characters.", nameof(enhancedStatus));
			EnhancedStatus = enhancedStatus;
			Message = message;
		}

		/// <summary>SMTP status code to send to the client.</summary>
		public int StatusCode { get; }

		/// <summary>RFC 3463 enhanced status code (e.g. "2.0.0", "4.3.0", "5.1.1") sent to the client.</summary>
		public string EnhancedStatus { get; }

		/// <summary>Human-readable status message sent to the client.</summary>
		public string Message { get; }

		/// <summary>
		/// Builds a result with an explicit SMTP status code, RFC 3463 enhanced status, and message.
		/// Use this when the standard <see cref="Ok"/>/<see cref="TemporaryFailure"/>/<see cref="PermanentFailure"/>
		/// factories do not express the precise rejection (e.g. 550 5.1.1 unroutable recipient,
		/// 554 5.6.0 content rejected).
		/// </summary>
		public static SmtpDeliveryResult Status(int statusCode, string enhancedStatus, string message) =>
			new SmtpDeliveryResult(statusCode, enhancedStatus, message);

		/// <summary>Message was accepted for delivery.</summary>
		public static SmtpDeliveryResult Ok(string message = "OK") =>
			new SmtpDeliveryResult(250, "2.0.0", message);

		/// <summary>Transient failure — the sending MTA should retry later.</summary>
		public static SmtpDeliveryResult TemporaryFailure(string message = "Requested action aborted: local error in processing") =>
			new SmtpDeliveryResult(451, "4.3.0", message);

		/// <summary>Permanent failure — the sending MTA must not retry.</summary>
		public static SmtpDeliveryResult PermanentFailure(string message = "Message rejected") =>
			new SmtpDeliveryResult(554, "5.7.1", message);
	}
}
