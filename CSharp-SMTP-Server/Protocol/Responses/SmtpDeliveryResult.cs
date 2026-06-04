using System;

namespace CSharp_SMTP_Server.Protocol.Responses
{
	/// <summary>
	/// Result returned by the delivery handler, mapped directly to the SMTP response sent to the client.
	/// </summary>
	public sealed class SmtpDeliveryResult
	{
		private SmtpDeliveryResult(int statusCode, string message)
		{
			StatusCode = statusCode;
			// Reject CR/LF in the message text to prevent SMTP response-splitting at the point
			// where this value is written into the wire response by ClientProcessor.WriteCode.
			if (message.IndexOf('\r') >= 0 || message.IndexOf('\n') >= 0)
				throw new ArgumentException("SMTP response message must not contain CR or LF characters.", nameof(message));
			Message = message;
		}

		/// <summary>SMTP status code to send to the client.</summary>
		public int StatusCode { get; }

		/// <summary>Human-readable status message sent to the client.</summary>
		public string Message { get; }

		/// <summary>Message was accepted for delivery.</summary>
		public static SmtpDeliveryResult Ok(string message = "OK") =>
			new SmtpDeliveryResult(250, message);

		/// <summary>Transient failure — the sending MTA should retry later.</summary>
		public static SmtpDeliveryResult TemporaryFailure(string message = "Requested action aborted: local error in processing") =>
			new SmtpDeliveryResult(451, message);

		/// <summary>Permanent failure — the sending MTA must not retry.</summary>
		public static SmtpDeliveryResult PermanentFailure(string message = "Message rejected") =>
			new SmtpDeliveryResult(554, message);
	}
}
