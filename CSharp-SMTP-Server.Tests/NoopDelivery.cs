using System.Threading;
using System.Threading.Tasks;
using CSharp_SMTP_Server.Interfaces;
using CSharp_SMTP_Server.Protocol.Responses;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Minimal IMailDelivery stub for tests that construct an SMTPServer but never deliver mail.
/// </summary>
internal sealed class NoopDelivery : IMailDelivery
{
    public static readonly NoopDelivery Instance = new();

    public Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction transaction, CancellationToken cancellationToken = default) =>
        Task.FromResult(SmtpDeliveryResult.Ok());

    public Task<UserExistsCodes> DoesUserExist(string emailAddress) =>
        Task.FromResult(UserExistsCodes.DestinationAddressValid);
}
