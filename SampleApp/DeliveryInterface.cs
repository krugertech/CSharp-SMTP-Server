using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CSharp_SMTP_Server;
using CSharp_SMTP_Server.Interfaces;
using CSharp_SMTP_Server.Protocol.Responses;

namespace SampleApp;

internal class DeliveryInterface : IMailDelivery
{
	// Printing the whole message only makes sense for a demo, so this reads RawBody — but only after
	// checking the size. RawBody materializes the entire message as a UTF-16 string; for anything
	// large, stream it with transaction.GetBodyStream() instead, which is what a real handler
	// persisting mail to disk or object storage should do.
	private const long MaxBytesToPrint = 64 * 1024;

	// Print the email and return 250 OK to the sender.
	public Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction transaction, CancellationToken cancellationToken = default)
	{
		var rawBody = transaction.BodyLength <= MaxBytesToPrint
			? transaction.RawBody
			: $"({transaction.BodyLength} bytes — too large to print; use GetBodyStream() to read it)";

		Console.WriteLine(
			$"\n\n--- EMAIL TRANSACTION ---\nSource IP: {transaction.RemoteEndPoint}\nAuthenticated: {transaction.AuthenticatedUser ?? "(not authenticated)"}\nFrom: {transaction.From}\nTo: {transaction.DeliverTo.Aggregate((current, item) => current + ", " + item)}\n\nBody:\n{transaction.GetMessageBody()}\n\nRaw Body:\n{rawBody}\n--- END OF TRANSACTION ---\n\n");

		return Task.FromResult(SmtpDeliveryResult.Ok());
	}

	// We only own "@smtp.demo" and we don't want any emails to other domains.
	public Task<UserExistsCodes> DoesUserExist(string emailAddress) => Task.FromResult(emailAddress.EndsWith("@smtp.demo", StringComparison.OrdinalIgnoreCase)
		? UserExistsCodes.DestinationAddressValid
		: UserExistsCodes.BadDestinationSystemAddress);
}
