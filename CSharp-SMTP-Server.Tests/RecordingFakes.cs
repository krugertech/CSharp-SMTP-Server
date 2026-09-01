using System.Net;
using CSharp_SMTP_Server.Interfaces;
using CSharp_SMTP_Server.Protocol;
using CSharp_SMTP_Server.Protocol.Responses;

namespace CSharp_SMTP_Server.Tests;

/// <summary>IMailDelivery fake that records every delivered transaction. See TESTING.md.</summary>
public sealed class RecordingDelivery : IMailDelivery
{
    public List<MailTransaction> Delivered { get; } = new();

    /// <summary>Optional per-test handler override; defaults to Ok().</summary>
    public Func<MailTransaction, CancellationToken, Task<SmtpDeliveryResult>>? HandlerOverride;

    /// <summary>Value returned by DoesUserExist for the next (and subsequent) calls.</summary>
    public UserExistsCodes NextUserExistsCode = UserExistsCodes.DestinationAddressValid;

    public async Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction transaction, CancellationToken cancellationToken = default)
    {
        lock (Delivered) Delivered.Add(transaction);
        return HandlerOverride != null ? await HandlerOverride(transaction, cancellationToken) : SmtpDeliveryResult.Ok();
    }

    public Task<UserExistsCodes> DoesUserExist(string emailAddress) => Task.FromResult(NextUserExistsCode);
}

/// <summary>IMailFilter fake with per-method configurable results and last-call argument recording.</summary>
public sealed class ConfigurableFilter : IMailFilter
{
    // ── configured responses (all Success by default) ──────────────────────────
    public SmtpResult Connection = new(SmtpResultType.Success);
    public SmtpResult Sender = new(SmtpResultType.Success);
    public SmtpResult SenderSpfVerified = new(SmtpResultType.Success);
    public SmtpResult Deliver = new(SmtpResultType.Success);
    public SmtpResult ProcessTransaction = new(SmtpResultType.Success);

    /// <summary>When set, IsConnectionAllowed throws this instead of returning (R6 regression tests).</summary>
    public Exception? ConnectionThrows;

    // ── last-call argument recording ───────────────────────────────────────────
    public EndPoint? LastConnectionEp;
    public string? LastSender;
    public EndPoint? LastSenderEp;
    public string? LastSenderUsername;
    public ValidationResult? LastSpfResult;
    public string? LastDeliverSource;
    public string? LastDeliverDestination;
    public bool? LastDeliverAuthenticated;
    public string? LastDeliverUsername;

    public Task<SmtpResult> IsConnectionAllowed(EndPoint? ep)
    {
        LastConnectionEp = ep;
        if (ConnectionThrows != null) throw ConnectionThrows;
        return Task.FromResult(Connection);
    }

    public Task<SmtpResult> IsAllowedSender(string source, EndPoint? ep, string? username)
    {
        LastSender = source;
        LastSenderEp = ep;
        LastSenderUsername = username;
        return Task.FromResult(Sender);
    }

    public Task<SmtpResult> IsAllowedSenderSpfVerified(string source, EndPoint? ep, string? username, ValidationResult validationResult)
    {
        LastSpfResult = validationResult;
        return Task.FromResult(SenderSpfVerified);
    }

    public Task<SmtpResult> CanDeliver(string source, string destination, bool authenticated, string? username, EndPoint? ep)
    {
        LastDeliverSource = source;
        LastDeliverDestination = destination;
        LastDeliverAuthenticated = authenticated;
        LastDeliverUsername = username;
        return Task.FromResult(Deliver);
    }

    public Task<SmtpResult> CanProcessTransaction(MailTransaction transaction) => Task.FromResult(ProcessTransaction);
}

/// <summary>ILogger fake capturing every error line. See TESTING.md.</summary>
public sealed class RecordingLogger : ILogger
{
    private readonly List<string> _errors = new();

    public IReadOnlyList<string> Errors
    {
        get { lock (_errors) return _errors.ToArray(); }
    }

    public void LogError(string text)
    {
        lock (_errors) _errors.Add(text);
    }
}
