# Changelog

All notable changes to this fork are documented here. This fork tracks
[zabszk/CSharp-SMTP-Server](https://github.com/zabszk/CSharp-SMTP-Server); see `ARCHITECTURE.md` §9 for
the upstream sync record.

## [1.2.0-krugertech.1]

### Breaking

- **`MailTransaction.GetFrom` / `GetTo()` / `GetCc()` / `GetBcc()` now return email addresses, not
  display names.** They previously returned MimeKit's *display name*: `""` for
  `From: user@example.com`, and `"John"` for `From: John <j@e.c>`.

  **Security impact — this is why the break is worth taking.** DMARC validation reads `GetFrom`, so it
  could never determine the header-From domain for ordinary mail. `ValidateDMARC: true` performed no
  effective enforcement, and every message got an `Authentication-Results: ... dmarc=none
  header.from=(none)` header. Operators believed they had DMARC protection and did not.

  **Migration.** If you relied on the old output to get a display name, use the new `GetFromName`
  property. There is no separate display-name accessor for To/Cc/Bcc; read `ParsedMessage.To` (etc.)
  directly if you need one. `GetTo()`/`GetCc()`/`GetBcc()` now also flatten group addresses to the
  mailboxes they contain, so a grouped recipient list yields its members rather than the group name.

### Fixed

- **DMARC is now actually enforced.** Beyond the `GetFrom` change above, the header-From domain is
  extracted with a new internal helper rather than by `ProcessAddress`, which parses SMTP *command*
  arguments and requires the RFC 5321 angle-bracket form. A bare header address routed through it
  returned null, which would have left DMARC inert even after the `GetFrom` fix. An unaligned message
  under `p=reject` now reaches `Fail` and is rejected with `554` at DATA.
- **SPF no longer fails open on DNS errors.** A failed A/AAAA lookup inside an `a` or `mx` mechanism
  was treated as a *match*, so `v=spf1 a:host` (implicit `+`) returned **Pass** during a resolver
  outage — turning SPF from a control into an authorizer. Both mechanisms now return `Temperror` per
  RFC 7208 §5.
- **A throwing `IMailFilter` no longer crashes the process.** `IsConnectionAllowed` is awaited from
  `async void` paths (`Init()` for plaintext, `Receive()` for TLS); an exception from a filter — a
  database timeout is enough — terminated the host process. Both paths are now guarded: the connection
  is dropped and logged, and the server keeps serving.
- **Connections accepted during shutdown are no longer orphaned.** A connection accepted but not yet
  registered when `Listener.Dispose()` snapshotted its processor list was registered afterwards, into a
  list nobody reads again — so it was never disposed and kept running after shutdown, while
  `SMTPServer.Dispose()` went on to dispose the TLS certificate it might still be using. Registration
  is now refused once shutdown has begun, and the accept loop disposes what it turns away.
- **`MailTransaction.Clone()` carries `DMARCValidationResult`.** The clone handed to `IMailDelivery`
  previously always reported `None` regardless of the real outcome, losing `CheckDisabled` too.
- **`MailTransaction.Clone()` copies the `DeliverTo` list.** Clone and original shared one
  `List<string>`, so a delivery handler filtering or deduplicating recipients mutated the server-side
  transaction. This matters more here than upstream: ACK-gating runs delivery inside the session.

### Changed

- `SMTPServer.VersionString` and `AssemblyVersionString` had drifted behind the published
  `PackageVersion` (`1.1.6-krugertech.1` vs `1.1.6-krugertech.3`). All three now move together at
  `1.2.0`.
