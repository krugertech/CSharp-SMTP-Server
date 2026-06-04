# CSharp-SMTP-Server — ACK-Gated Fork

> **This is a fork of [zabszk/CSharp-SMTP-Server](https://github.com/zabszk/CSharp-SMTP-Server) v1.1.6 (released 23 Dec 2023).**
> It was branched specifically to add **ACK gating** to the DATA command.
> It is not affiliated with or endorsed by the original author.
> The original library is available on [NuGet](https://www.nuget.org/packages/CSharp-SMTP-Server/).

Simple (receive-only) SMTP server library for C#.

---

## What is ACK gating and why does it matter?

In the original library the DATA command fires the delivery handler in a background task and immediately returns `250 OK` to the sending MTA — a pattern called *fire-and-forget*. This means the sending MTA considers the message delivered the moment the server acknowledges it, even though your application code has not yet finished (or even started) processing it.

**This fork changes that contract:**

- The server **awaits** your delivery handler before sending any SMTP response.
- Your handler returns a `SmtpDeliveryResult` that controls exactly what code the client sees.
- `250 OK` is sent **only after** your handler returns `SmtpDeliveryResult.Ok(...)`.
- A transient failure (`451`) tells the sender to retry later.
- A permanent failure (`554`) tells the sender not to retry.
- An unhandled exception in your handler produces a `451` so the sender retries rather than silently losing the message.

This makes the SMTP `250 OK` a true durability guarantee: the sending MTA will not discard its copy of the message until your handler says it has been safely accepted.

### What this means if you are migrating from the original library

| Original | This fork |
|---|---|
| `Task EmailReceived(MailTransaction transaction)` | `Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction transaction, CancellationToken cancellationToken = default)` |
| Return value ignored — always sends `250 OK` | Return value determines the SMTP response sent to the client |
| Delivery runs in the background | Server blocks the SMTP session until delivery completes |
| Exception in handler is silently swallowed | Exception produces `451`; sending MTA will retry |

You must rename and update the signature of your `EmailReceived` implementation. No other interface changes are required.

---

## Supported features
* TLS and STARTTLS
* AUTH LOGIN and AUTH PLAIN
* ACK-gated delivery (this fork)

## Compatible with
* RFC 822 (STANDARD FOR THE FORMAT OF ARPA INTERNET TEXT MESSAGES)
* RFC 1869 (SMTP Service Extensions)
* RFC 2554 (SMTP Service Extension for Authentication)
* RFC 3463 (Enhanced Mail System Status Codes)
* RFC 4616 (The PLAIN Simple Authentication and Security Layer (SASL) Mechanism)
* RFC 4954 (SMTP Service Extension for Authentication)
* RFC 5321 (SMTP Protocol)
* RFC 7208 (Sender Policy Framework)
* RFC 7372 (Email Authentication Status Codes)
* RFC 7489 (Domain-based Message Authentication, Reporting, and Conformance (DMARC)) [Partially Supported]

---

## Basic usage

### Server setup

```cs
var server = new SMTPServer(new[]
{
    new ListeningParameters(IPAddress.IPv6Any, new ushort[] { 25, 587 }, new ushort[] { 465 }, true)
}, new ServerOptions { ServerName = "My SMTP Server", RequireEncryptionForAuth = false },
   new DeliveryInterface(),
   new LoggerInterface());

// With TLS certificate:
// }, new ServerOptions { ServerName = "My SMTP Server", RequireEncryptionForAuth = true },
//    new DeliveryInterface(), new LoggerInterface(),
//    new X509Certificate2("PathToCertWithKey.pfx"));

server.SetAuthLogin(new AuthenticationInterface());
server.SetFilter(new FilterInterface());
server.Start();
```

### SmtpDeliveryResult

Your delivery handler returns one of three factory results:

```cs
// Message accepted — sends 250 OK to the client
SmtpDeliveryResult.Ok()
SmtpDeliveryResult.Ok("Message queued for delivery")

// Transient failure — sends 451; the sending MTA will retry
SmtpDeliveryResult.TemporaryFailure()
SmtpDeliveryResult.TemporaryFailure("Storage unavailable, try again later")

// Permanent failure — sends 554; the sending MTA will not retry
SmtpDeliveryResult.PermanentFailure()
SmtpDeliveryResult.PermanentFailure("Message policy violation")
```

### Delivery interface

```cs
class DeliveryInterface : IMailDelivery
{
    public async Task<SmtpDeliveryResult> EmailReceivedAsync(
        MailTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Do your durable work here — write to disk, insert to DB, etc.
            // The sending MTA will not receive 250 OK until this method returns.
            await SaveMessageAsync(transaction, cancellationToken);
            return SmtpDeliveryResult.Ok();
        }
        catch (StorageUnavailableException)
        {
            // Transient — ask the sender to retry
            return SmtpDeliveryResult.TemporaryFailure("Storage unavailable, please retry");
        }
        catch (PolicyViolationException ex)
        {
            // Permanent — do not retry
            return SmtpDeliveryResult.PermanentFailure(ex.Message);
        }
        // Any unhandled exception becomes a 451 automatically
    }

    // Called during RCPT TO — return DestinationAddressValid to accept the recipient
    public Task<UserExistsCodes> DoesUserExist(string emailAddress) =>
        Task.FromResult(emailAddress.EndsWith("@example.com", StringComparison.OrdinalIgnoreCase)
            ? UserExistsCodes.DestinationAddressValid
            : UserExistsCodes.BadDestinationSystemAddress);
}
```

### Logger interface

```cs
class LoggerInterface : ILogger
{
    public void LogError(string text) => Console.WriteLine("[LOG] " + text);
}
```

### Authentication interface

```cs
class AuthenticationInterface : IAuthLogin
{
    // 123 is the password for all users — NOT SECURE, DEMO ONLY
    public Task<bool> AuthPlain(string authorizationIdentity, string authenticationIdentity,
        string password, EndPoint remoteEndPoint, bool secureConnection) =>
        Task.FromResult(password == "123");

    public Task<bool> AuthLogin(string login, string password,
        EndPoint remoteEndPoint, bool secureConnection) =>
        Task.FromResult(password == "123");
}
```

### Filter interface

```cs
class FilterInterface : IMailFilter
{
    // Allow all connections
    public Task<SmtpResult> IsConnectionAllowed(EndPoint ep) =>
        Task.FromResult(new SmtpResult(SmtpResultType.Success));

    // Block .invalid TLD
    public Task<SmtpResult> IsAllowedSender(string source, EndPoint ep) =>
        Task.FromResult(source.TrimEnd().EndsWith(".invalid")
            ? new SmtpResult(SmtpResultType.PermanentFail)
            : new SmtpResult(SmtpResultType.Success));

    // Reject SPF Softfail
    public Task<SmtpResult> IsAllowedSenderSpfVerified(string source, EndPoint? ep,
        string? username, ValidationResult spfResult) =>
        Task.FromResult(spfResult == ValidationResult.Softfail
            ? new SmtpResult(SmtpResultType.PermanentFail)
            : new SmtpResult(SmtpResultType.Success));

    // Block emails addressed to root@*
    public Task<SmtpResult> CanDeliver(string source, string destination,
        bool authenticated, string? username, EndPoint? ep) =>
        Task.FromResult(destination.TrimStart().StartsWith("root@", StringComparison.OrdinalIgnoreCase)
            ? new SmtpResult(SmtpResultType.PermanentFail)
            : new SmtpResult(SmtpResultType.Success));

    // Reject messages containing "spam"
    public Task<SmtpResult> CanProcessTransaction(MailTransaction transaction) =>
        Task.FromResult(transaction.GetMessageBody() != null &&
                        transaction.GetMessageBody()!.Contains("spam", StringComparison.OrdinalIgnoreCase)
            ? new SmtpResult(SmtpResultType.PermanentFail)
            : new SmtpResult(SmtpResultType.Success));
}
```

---

## 3rd party services and libraries

* By default this library uses Cloudflare Public DNS (1.1.1.1) for SPF and DMARC validation. The DNS endpoint can be changed or both validations disabled via `ServerOptions`.
* By default this library downloads the Public Suffix List managed by the Mozilla Foundation from GitHub (licensed under MPL v2.0). The URL can be changed in `ServerOptions`. The list is not downloaded when `DnsServerEndpoint` is `null`.
* This library uses [MimeKit](https://github.com/jstedfast/MimeKit) 4.17.0 by the .NET Foundation and Contributors, licensed under the MIT License.

---

## Generating a PFX from PEM keys

```
openssl pkcs12 -export -in public.pem -inkey private.pem -out CertWithKey.pfx
```
