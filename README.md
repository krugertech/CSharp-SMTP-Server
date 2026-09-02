# CSharp-SMTP-Server — ACK-Gated Fork

> **This is a fork of [zabszk/CSharp-SMTP-Server](https://github.com/zabszk/CSharp-SMTP-Server) v1.1.6 (released 23 Dec 2023).**
> It was branched specifically to add **ACK gating** to the DATA command.
> It is not affiliated with or endorsed by the original author.
> The original library is available on [NuGet](https://www.nuget.org/packages/CSharp-SMTP-Server/).

Simple (receive-only) SMTP server library for C#.

## Documentation

- [`ARCHITECTURE.md`](ARCHITECTURE.md) — current runtime design and ownership model.
- [`TESTING.md`](TESTING.md) — normal, load, and integrity test commands.
- [`KNOWN_ISSUES.md`](KNOWN_ISSUES.md) — open work, accepted risks, and protocol quirks.
- [`CHANGELOG.md`](CHANGELOG.md) — release history and compatibility notes.

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

You must rename and update the signature of your `EmailReceived` implementation. Version 2 also
changes message-body lifetime and the address getter results; read [`CHANGELOG.md`](CHANGELOG.md)
before upgrading an existing consumer.

---

## Supported features
* TLS and STARTTLS
* AUTH LOGIN and AUTH PLAIN
* ACK-gated delivery (this fork)
* Stream-backed DATA storage with bounded line buffering
* RFC 1870 `SIZE` advertisement and RFC 5321 dot-unstuffing

## Compatible with
* RFC 822 (STANDARD FOR THE FORMAT OF ARPA INTERNET TEXT MESSAGES)
* RFC 1869 (SMTP Service Extensions)
* RFC 1870 (SMTP Service Extension for Message Size Declaration)
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

### Reading the message body

Prefer `MailTransaction.GetBodyStream()` when persisting a message:

```cs
public async Task<SmtpDeliveryResult> EmailReceivedAsync(
    MailTransaction transaction,
    CancellationToken cancellationToken = default)
{
    await using var source = transaction.GetBodyStream();
    await using var destination = File.Create(GetArchivePath(transaction));
    await source.CopyToAsync(destination, cancellationToken);
    await destination.FlushAsync(cancellationToken);

    return SmtpDeliveryResult.Ok();
}
```

`BodyLength` returns the stored byte count without reading the body. `RawBody` remains available for
compatibility, but it materializes the entire message as a UTF-16 string on every read. Body streams
and parsed-message access are valid only during `EmailReceivedAsync`; large bodies may live in a
temporary file that is released when the handler returns.

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

## Office 365 journaling relay profile

Journal reports are compliance records. A permanent SMTP rejection can destroy the only remaining
copy, while an unlimited internet-facing receiver is also unsafe. Use a finite limit above the
largest report Exchange Online can submit and disable sender authentication checks that do not
describe the original journaled message:

```cs
var options = new ServerOptions(
    validateSPF: false,
    validateDMARC: false,
    dnsServerEndpoint: null)
{
    ServerName = "journal.example.com",
    MessageCharactersLimit = 200u * 1024 * 1024,
    RecipientsLimit = 0,
};
```

For this deployment:

- Keep `MessageCharactersLimit` finite. `0` is unlimited; `200 MB` provides headroom above Exchange
  Online's configurable maximum of 150 MB while still bounding storage. Externally routed messages
  can have a lower effective limit because of transport encoding; see Microsoft's
  [Exchange Online limits](https://learn.microsoft.com/en-us/office365/servicedescriptions/exchange-online-service-description/exchange-online-limits).
  Despite the historical property name, the counter measures stored DATA bytes after dot-unstuffing
  and excludes CRLF, so it is not the exact RFC 1870 wire-octet count.
- `RecipientsLimit = 0` avoids rejecting a journal report for a large distribution list.
- Leave SPF and DMARC disabled. The journal envelope identifies the journaling system rather than the
  original sender, so those checks can reject a valid compliance record.
- Do not install a rejecting `IMailFilter` on the journaling listener.
- Return `TemporaryFailure` or throw when archive storage is unavailable. Never return `Ok` until the
  record is durably stored.
- Make storage idempotent. Current shutdown stops active sessions rather than draining them, so a
  commit followed by a lost `250` can cause the sender to retry. See
  [`KNOWN_ISSUES.md`](KNOWN_ISSUES.md#graceful-shutdown-and-duplicate-delivery).

The heavy test tier validates 150 MB delivery, concurrent large messages, bounded memory behavior,
and the relay-specific defaults; see [`TESTING.md`](TESTING.md#load-and-integrity-tests).

---

## Third-party services and libraries

* By default this library resolves SPF and DMARC through **the machine's own configured name servers** (`DnsResolverMode.System`). No public resolver is substituted: earlier versions silently fell back to Cloudflare `1.1.1.1`, which sent the sending domains of all inbound mail to a third-party operator the deployment never chose. Pass an endpoint for `DnsResolverMode.Explicit`, or use `DnsResolverMode.Disabled` to switch validation off entirely.
* Responses are cached in process (TTL-aware, 5 s floor / 5 min ceiling). Transient DNS failures are deliberately not cached.
* This library uses [DnsClient.NET](https://github.com/MichaCo/DnsClient.NET) 1.8.0 by Michael Conrad, licensed under the Apache License 2.0.
* By default this library downloads the Public Suffix List managed by the Mozilla Foundation from GitHub (licensed under MPL v2.0). The URL can be changed in `ServerOptions`. The list is not downloaded when the resolver mode is `Disabled`.
* This library uses [MimeKit](https://github.com/jstedfast/MimeKit) 4.17.0 by the .NET Foundation and Contributors, licensed under the MIT License.

---

## Generating a PFX from PEM keys

On Windows, a certificate returned directly by `CertificateRequest.CreateSelfSigned()` may use an
ephemeral private key that SChannel cannot use for server TLS. Export and re-import it as PFX, or
generate a PFX from PEM keys:

```
openssl pkcs12 -export -in public.pem -inkey private.pem -out CertWithKey.pfx
```
