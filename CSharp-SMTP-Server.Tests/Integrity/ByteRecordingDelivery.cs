using System.Collections.Concurrent;
using CSharp_SMTP_Server.Interfaces;
using CSharp_SMTP_Server.Protocol.Responses;

namespace CSharp_SMTP_Server.Tests.Integrity;

/// <summary>
/// Delivery handler that captures each message as raw octets, read from
/// <see cref="MailTransaction.GetBodyStream"/>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately does NOT read <see cref="MailTransaction.RawBody"/>. That property materializes the
/// message as a UTF-16 string via a UTF-8 <c>StreamReader</c>, which replaces every byte that is not
/// valid UTF-8 with U+FFFD — so a handler that captured it would destroy exactly the evidence these
/// tests exist to examine, and an 8-bit or Latin-1 body would appear corrupted no matter how
/// faithfully the server preserved it.
/// </para>
/// <para>
/// The stream is valid only for the duration of the delivery call — the server disposes the body,
/// and its temp file, once the handler returns — so the bytes are copied out before returning
/// rather than the stream being retained.
/// </para>
/// </remarks>
internal sealed class ByteRecordingDelivery : IMailDelivery
{
    /// <summary>Every delivered message, in delivery order, as complete octets.</summary>
    internal ConcurrentQueue<byte[]> Delivered { get; } = new();

    /// <summary>The single delivered message; fails the test if there was not exactly one.</summary>
    internal byte[] Single()
    {
        Assert.Single(Delivered);
        Assert.True(Delivered.TryPeek(out var only));
        return only!;
    }

    public Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        using var stream = transaction.GetBodyStream();
        using var buffer = new MemoryStream();

        stream.CopyTo(buffer);
        Delivered.Enqueue(buffer.ToArray());

        return Task.FromResult(SmtpDeliveryResult.Ok());
    }

    public Task<UserExistsCodes> DoesUserExist(string emailAddress) =>
        Task.FromResult(UserExistsCodes.DestinationAddressValid);
}
