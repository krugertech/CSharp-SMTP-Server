using System.Threading;
using System.Threading.Tasks;
using CSharp_SMTP_Server.Protocol.Responses;

namespace CSharp_SMTP_Server.Interfaces
{
	/// <summary>
	/// Interface handling email delivery. The server awaits the handler before sending the SMTP acknowledgement.
	/// </summary>
	public interface IMailDelivery
	{
		/// <summary>
		/// Called when an email transaction has been completed. The SMTP 250 OK is sent only after this method
		/// returns successfully. Return <see cref="SmtpDeliveryResult.TemporaryFailure"/> or
		/// <see cref="SmtpDeliveryResult.PermanentFailure"/> to reject the message; throw to cause a 451 response.
		/// </summary>
		/// <param name="transaction">The completed mail transaction.</param>
		/// <param name="cancellationToken">Cancellation token tied to the client connection.</param>
		Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction transaction, CancellationToken cancellationToken = default);

		/// <summary>
		/// Called when new recipient is being added.
		/// </summary>
		/// <param name="emailAddress">Email address being added as recipient.</param>
		/// <returns>Whether the email address is a valid recipient.</returns>
		Task<UserExistsCodes> DoesUserExist(string emailAddress);
	}
}
