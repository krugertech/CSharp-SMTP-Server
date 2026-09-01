# Known issues and pending work

This document is the current backlog for the Krugertech fork. Completed changes belong in
[`CHANGELOG.md`](CHANGELOG.md); implementation details belong in
[`ARCHITECTURE.md`](ARCHITECTURE.md).

The identifiers in parentheses are retained because regression tests and older commits use them.

## Production decisions

### Graceful shutdown and duplicate delivery

`SMTPServer.Dispose()` stops listeners and terminates active sessions; it does not drain them. With
ACK-gated delivery, shutdown can occur after a handler commits a message but before the sender
receives `250`. The sender will retry, so delivery storage must be idempotent or deduplicate messages.

Pending decision: implement a drain mode that stops accepting, waits for in-flight sessions within
the Kubernetes termination grace period, and then disposes the server. The host must call
`Dispose()` during `SIGTERM` handling even if idempotency is considered sufficient.

### Delivery cancellation does not detect an idle peer disconnect (Q8)

The token passed to `EmailReceivedAsync` is cancelled when the server tears down the connection.
While the handler is running, the receive loop is awaiting that handler and does not poll the socket,
so a remote disconnect alone may not cancel the token until the handler returns and the response
write fails.

Pending work: add independent disconnect detection or a configurable delivery timeout. Handlers
should enforce their own timeout until then.

### No DKIM verification

The server preserves DATA bytes and reverses SMTP dot-stuffing, so a downstream archive can verify
DKIM signatures. The server itself does not verify DKIM. This also means SPF is the only
authentication mechanism available to DMARC for null reverse-path messages.

The unfinished upstream `dkim` branch was deliberately not merged. Treat DKIM verification as a
future feature, not an advertised capability.

## SPF and DMARC

These items matter only when SPF or DMARC validation is enabled. The Office 365 journaling profile in
the README disables both.

### Split TXT records are dropped by the DNS dependency (Q11)

`zabszk.DnsClient` 1.0.1 does not concatenate the character-strings in a multi-string TXT record.
Long SPF or DMARC records can therefore appear absent, producing SPF `None` or preventing the
expected DMARC policy lookup.

This is the highest-priority validation issue. Fixing it requires replacing or patching the DNS
dependency and retaining the regression coverage in `SpfValidatorTests`.

### Remaining SPF result deviations (Q12a/Q12c)

- A top-level TXT lookup returning NXDOMAIN produces `Temperror`; RFC 7208 requires `None`.
- An SPF `redirect=` target returning NXDOMAIN produces `Temperror`; it should become `Permerror`.

The former DNS fail-open for `a` and `mx` mechanisms (Q12b) is fixed and is documented in the
changelog.

### `redirect=` is evaluated positionally (Q13)

The validator evaluates `redirect=` where it appears and can skip mechanisms that follow it. RFC
7208 treats redirect as the fallback after all mechanisms fail to match. Defer redirect processing
until the mechanism loop completes.

### SPF results can remain stale for a connection

`ClientProcessor.SpfResultsCache` stores results by domain for the lifetime of a connection. It has
no TTL and is not cleared by `RSET` or a repeated greeting, so a previously authorized client can
retain a stale `Pass` after DNS changes.

This is accepted for the current journaling deployment because SPF is disabled there. If it is
addressed, prefer honoring DNS TTLs over clearing the cache for every message: the DNS client has no
cache of its own and SPF include chains can require several blocking queries.

### Public suffix state is process-wide

`DmarcValidator` holds the public suffix set in static state. Multiple servers in one process cannot
safely use different suffix-list sources or force independent refreshes. This is accepted under the
one-server-per-pod deployment model. Revisit it before supporting isolated multi-instance hosts.

## Protocol and API cleanup

### Two-argument response overload drops descriptive text (R1/Q7)

Calls such as `WriteCode(250, "2.0.0")` bind to the `(int, string)` overload intended for a complete
handler-supplied message, rather than `(ushort, string enhancedStatus)`. The resulting response is
legal but contains no human-readable table text. Remove the overload ambiguity and update the exact
wire assertions together.

Related opportunistic cleanups:

- VRFY currently combines SMTP `252` with failure-class enhanced status `5.5.1` (Q2).
- An invalid RCPT address returns bare `501` without an enhanced status (Q5).

### Invalid AUTH LOGIN initial response falls back to another prompt (R2)

An undecodable inline response to `AUTH LOGIN` is treated like an absent username and the server
prompts again. Prefer rejecting malformed Base64 with `501` instead of extending the authentication
exchange.

### AUTH LOGIN case indentation is damaged (R3)

The `case "LOGIN":` block in `AuthenticationCommands.cs` behaves correctly, but its comment has a
stray deep indent and the remaining statements are aligned with the switch rather than the case body.
Reformat the block to match the repository's tab-based Allman style when that command is next touched.

### Custom delivery status codes are not validated (R4)

`SmtpDeliveryResult.Status(int, ...)` accepts values outside the SMTP three-digit range and does not
ensure the enhanced-status class agrees with the SMTP status class. CR/LF injection is already
blocked. Add validation without weakening that response-splitting protection.

### Authenticated `Received` header omits the client address (Q3)

For authenticated sessions, the prepended `Received:` header omits `from <ip>`. Preserve the client
address for forensic traceability regardless of authentication state.

### Minor accepted protocol behavior

- STARTTLS is accepted before EHLO (Q4). This is tolerant but out of the usual command order.
- Despite its historical name, `MessageCharactersLimit` counts stored DATA bytes after
  dot-unstuffing and excludes CRLF (Q6). It is not the exact RFC 1870 wire-octet count, so configure
  headroom.
- DATA receives only the RFC-required final response, not a response per input line (Q9).

## Test-suite maintenance

- `TestPorts.Allocate()` releases its ephemeral listener before the server binds the selected port,
  leaving a theoretical time-of-check/time-of-use race (R8). Test collections remain serial.
- A few lifecycle tests use timing delays (R9). Replace them with observable synchronization when
  those tests are next changed.
- Pin identifiers are represented inconsistently in names, comments, and traits (R10). Standardize
  them only if it improves discoverability; the behavior is already covered.

### Latent processor-registration ordering smell (not reproduced)

`ClientProcessor` starts `Init()` from its constructor, before `Listener.AddProcessor` registers it.
In theory, a synchronously failed connection filter could dispose the processor before registration,
making `RemoveProcessor` a no-op and allowing a dead processor to be added afterwards.

The proposed leak was not observed across 5,000 connections using instantly rejecting and
synchronously throwing filters: the final processor count was always zero, while the observed peak
represented genuine in-flight connections. The `Task.Run(Init)` hop currently gives registration
time to win the race.

Re-test this ordering first if processor initialization ever starts inline. If it becomes observable,
use two-phase initialization: construct, register, then start `Init()`.
