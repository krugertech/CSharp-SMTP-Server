# Architecture

This document describes the current design of the Krugertech CSharp SMTP Server fork. Release history
and completed fixes are recorded in [`CHANGELOG.md`](CHANGELOG.md); unresolved behavior is tracked in
[`KNOWN_ISSUES.md`](KNOWN_ISSUES.md).

## Purpose

The project is a receive-only SMTP server library forked from
[`zabszk/CSharp-SMTP-Server`](https://github.com/zabszk/CSharp-SMTP-Server) v1.1.6. Its defining
behavior is ACK-gated delivery: the server awaits the application delivery handler and sends `250`
only after that handler confirms durable acceptance.

The NuGet package is `Krugertech.CSharp-SMTP-Server`, currently version
`2.0.0-krugertech.1`. The library targets `netstandard2.1`, `net6.0`, and `net7.0`.

## Solution layout

```text
CSharp-SMTP-Server.sln
├── CSharp-SMTP-Server/
│   ├── SMTPServer.cs              public server entry point
│   ├── ServerOptions.cs           server configuration
│   ├── MailTransaction.cs         transaction delivered to consumers
│   ├── MessageBody.cs             stream-backed message storage
│   ├── Interfaces/                delivery, authentication, filtering, logging
│   ├── Networking/
│   │   ├── Listener.cs            listener lifecycle and accept loop
│   │   ├── ClientProcessor.cs     one SMTP state machine per connection
│   │   └── BoundedLineReader.cs   bounded command/DATA line reader
│   └── Protocol/
│       ├── Commands/              SMTP transaction and authentication commands
│       ├── Responses/             SMTP result and response types
│       ├── SPF/                   SPF validation
│       └── DMARC/                 DMARC and organizational-domain validation
├── CSharp-SMTP-Server.Tests/      xUnit unit, protocol, load, and integrity tests
└── SampleApp/                     demonstration console application
```

The principal dependencies are MimeKit 4.17.0 for MIME parsing and `zabszk.DnsClient` 1.0.1 for
SPF/DMARC DNS queries.

## Runtime ownership

```text
SMTPServer
└── Listener                         one per configured IP/port
    └── ClientProcessor              one per accepted TCP connection
        ├── BoundedLineReader
        ├── optional MailTransaction one active SMTP transaction
        └── connection cancellation and stream state
```

`SMTPServer` owns listeners and the TLS certificate. A `Listener` owns its accepted processors and
runs a dedicated accept thread. A `ClientProcessor` owns the client socket, protocol state,
per-connection SPF cache, and any active transaction.

Disposing the server stops listeners and tears down active processors. Shutdown is safe against
connections racing with disposal, but it is terminating rather than draining; see the graceful
shutdown item in [`KNOWN_ISSUES.md`](KNOWN_ISSUES.md#graceful-shutdown-and-duplicate-delivery).

## Connection and command flow

1. The listener accepts a TCP client and registers its processor.
2. The processor applies implicit TLS when configured, runs `IsConnectionAllowed`, and writes the
   greeting.
3. `BoundedLineReader` reads commands with a 1 MB maximum line length. It truncates and drains an
   overlong line without allowing unbounded allocation.
4. `ClientProcessor.ProcessResponse` dispatches commands to the transaction or authentication
   command handlers.
5. `EHLO` advertises the configured extensions, including `8BITMIME`, `SIZE`, optional AUTH, and
   optional STARTTLS. A new EHLO/HELO resets any active transaction.
6. `QUIT`, connection failure, `RSET`, a new greeting, policy rejection, and server shutdown all
   release the active transaction through the same discard path.

The protocol is intentionally session-oriented. While delivery is running, that SMTP session waits;
other connections continue on their own processors.

## SMTP transaction flow

### MAIL FROM

The server parses the bracketed reverse-path, including the RFC 5321 null path `<>`. It then applies:

1. `IMailFilter.IsAllowedSender`, if configured.
2. SPF for unauthenticated clients, if enabled.
3. `IMailFilter.IsAllowedSenderSpfVerified`, if configured.

For a null reverse-path, RFC 7208 makes the DNS-form EHLO/HELO name the SPF identity. Address literals
and non-DNS greetings do not supply a checkable SPF identity. SPF results are cached for the
connection; the accepted staleness risk is documented in `KNOWN_ISSUES.md`.

### RCPT TO

RCPT requires an active transaction. The server enforces `RecipientsLimit`, applies
`IMailFilter.CanDeliver`, and calls `IMailDelivery.DoesUserExist`. Accepted recipients are stored in
`MailTransaction.DeliverTo`.

### DATA

DATA requires at least one accepted recipient. After `354`, lines are read as raw bytes rather than
decoded strings:

1. A lone `.` ends DATA; a doubled leading dot is unstuffed per RFC 5321.
2. Lines are stored with CRLF endings in `MessageBody`.
3. Despite its historical name, `MessageCharactersLimit` counts stored DATA bytes after
   dot-unstuffing and excludes CRLF. Once over the limit, input is drained but no longer stored; the
   completed transaction receives `552`.
4. A DATA line exceeding the 1 MB line cap latches a truncation flag. The server drains the line and
   rejects the transaction with `552` at the terminator rather than delivering incomplete bytes.
5. The server prepends `Received:` and applicable `Authentication-Results:` headers.
6. DMARC, if enabled, validates a single header-From mailbox. Authenticated clients bypass SPF and
   DMARC. Null reverse-path DMARC alignment uses the HELO identity only when SPF authenticated it.
7. `IMailFilter.CanProcessTransaction` gets the completed transaction.
8. The transaction is handed to the delivery path and the processor releases its reference.

The body path preserves the DATA octets and is suitable for downstream integrity verification. The
server does not itself implement DKIM verification.

## Message storage and lifetime

`MessageBody` keeps messages in memory up to 4 MB and spills larger messages to a temporary file
opened with delete-on-close behavior. This keeps memory proportional to buffers rather than message
size. `MailTransaction.Clone()` shares the body store instead of copying it.

Delivery handlers should use:

- `GetBodyStream()` for a forward-only stream containing server-prepended headers and raw message
  bytes.
- `BodyLength` for the stored byte length without materialization.
- `ParsedMessage` when MimeKit parsing is required.

`RawBody` remains for compatibility but creates a complete UTF-16 string on every read. It should be
avoided for messages that may be large.

The server releases spilled storage when `EmailReceivedAsync` returns, including when it throws.
Consumers must therefore finish reading or copying the body during the handler call and dispose
streams they open. Retaining a transaction for deferred body access is unsupported.

Headers added through `MailTransaction.AddHeader` are recorded separately and spliced ahead of the
stored body when read. This avoids rewriting a large message merely to prepend metadata.

## ACK-gated delivery

The completed transaction is passed to:

```cs
Task<SmtpDeliveryResult> EmailReceivedAsync(
    MailTransaction transaction,
    CancellationToken cancellationToken = default)
```

The result maps directly to the SMTP response:

- `Ok` produces `250`; the sender may discard its copy.
- `TemporaryFailure` produces `451`; the sender should retry.
- `PermanentFailure` produces `554`; the sender should not retry.
- An unhandled handler exception is logged and produces `451`.

The handler is therefore the durability boundary. It must return `Ok` only after storage is durable.
Delivery runs concurrently across connections but serially within a single SMTP session.

The cancellation token represents server-side connection teardown. A peer disconnect is not
independently observed while the handler is in flight, so handlers should not rely on this token as
their only timeout.

## SPF and DMARC

SPF and DMARC are optional and enabled by default. Their validators use a configured DNS endpoint,
which defaults to Cloudflare `1.1.1.1:53`. DMARC also loads the Mozilla Public Suffix List and caches
it in process-wide state.

Authentication results are prepended to delivered messages when validation runs. SPF hard failure is
rejected during MAIL FROM; DMARC hard failure is rejected after DATA. Authenticated sessions bypass
both validations.

Known dependency and RFC-result deviations are listed in
[`KNOWN_ISSUES.md`](KNOWN_ISSUES.md#spf-and-dmarc). Deployments that do not need sender-policy
enforcement should explicitly disable both validators rather than relying on their defaults.

## Public extension points

- `IMailDelivery`: recipient lookup and ACK-gated durable delivery.
- `IAuthLogin`: AUTH LOGIN and AUTH PLAIN credential checks.
- `IMailFilter`: optional connection, sender, recipient, SPF-result, and completed-message policy
  hooks.
- `ILogger`: error logging.
- `ServerOptions`: listener-independent limits, authentication requirements, TLS choices, identity,
  and SPF/DMARC configuration.

All filter and delivery callbacks run on the connection's asynchronous flow. Slow callbacks hold that
session open, so consumers should use asynchronous I/O and bounded downstream timeouts.

## Deployment model

The primary deployment runs one `SMTPServer` per Kubernetes pod, with independent pod IPs and
horizontal scaling. This makes process-wide DMARC suffix state acceptable and avoids hosting multiple
server configurations inside one process.

For the Office 365 journaling configuration and its durability constraints, see the
[`README`](README.md#office-365-journaling-relay-profile). For test commands and load coverage, see
[`TESTING.md`](TESTING.md).

## Upstream sync record

This is a historical decision record, not a statement about current upstream state. Upstream
(`https://github.com/zabszk/CSharp-SMTP-Server`, remote `upstream`) was audited in July 2026. The
audit covered everything after tag `1.1.6`, open PRs #17 and #19, the `dispose-log`, `dkim`, and
Dependabot branches, and open issues #11, #15, #16, and #18.

| Upstream change | Decision | Where it landed |
|---|---|---|
| `0dadf2d`, issue #16: handle disconnect exceptions, pass the connection token to writes, and improve write-error logging | Included verbatim. Fast response-write failure is especially useful with ACK gating after a sender disconnects. | `8fa02c8` |
| PR #17: support an initial Base64 username in `AUTH LOGIN` for IIS SMTP relay compatibility | Included with the maintainer's state-machine design. Deliberately excluded the breaking `ILogger` expansion, version/sample changes, and verbose logging of authentication payloads because credentials must not enter logs. | `7d0d50f`; `AuthLoginInitialResponseTests` |
| Issue #18: bracketed IPv6 EHLO names were split at their internal colon | Fixed locally by recognizing the command separator only outside square brackets. No upstream fix existed at audit time. | `274069a`; `EhloBracketedIpv6Tests` |
| MimeKit 4.3.0→4.7.1 (`51db717`) and 4.15.1 (PR #19/Dependabot) | Skipped because this fork already used MimeKit 4.17.0. | No change required |
| `49e6a64`: replace separate `AuthPlain`/`AuthLogin` methods with `CheckAuthCredentials` | Skipped as a breaking API redesign rather than a bug fix. Separate hooks also let consumers handle LOGIN and PLAIN differently. | Not merged |
| `dispose-log` branch | Skipped because it was diagnostic stack-trace logging that would add noise during normal shutdown. | Not merged |
| `dkim` branch: unfinished DKIM verification plus a `ServerOptions` rework into `Config/` | Skipped because it was an unfinished feature, not a contained fix; it tracked upstream issue #15. | Not merged |
| Issue #11: namespace collision between `zabszk.DnsClient` and Couchbase's `DnsClient` package | Not actionable without forking or renaming the separate DNS dependency. | Deferred |

To re-audit upstream:

```powershell
git fetch upstream
git log --oneline 1.1.6..upstream/master
```

Also review open upstream pull requests, issues, and non-default branches; the commit log alone does
not include several decisions recorded above.
