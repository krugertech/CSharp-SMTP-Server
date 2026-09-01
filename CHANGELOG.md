# Changelog

All notable changes to this fork are documented here. This fork tracks
[zabszk/CSharp-SMTP-Server](https://github.com/zabszk/CSharp-SMTP-Server); see `ARCHITECTURE.md` §9 for
the upstream sync record.

## [2.0.0-krugertech.1]

### Breaking

- **The DATA path streams to a byte-backed store instead of accumulating a string, cutting peak
  memory for a large message by more than 10×.** A 150 MB message previously drove peak working set
  to **~1900 MB**; it now grows the working set by **no measurable amount** (whole-process peak in
  isolation: ~114 MB). Four 50 MB messages arriving concurrently likewise went from ~1900 MB to no
  measurable growth.

  **Why it cost that much.** The body was accumulated in a `StringBuilder` (which grows by chunked
  reallocation, carrying slack), materialized with `ToString()`, copied a third time by
  `MailTransaction.Clone()` before delivery, and re-encoded to a `byte[]` for MimeKit — several full
  copies coexisting at peak, each doubled because .NET strings are UTF-16 while the message on the
  wire is bytes. Worse, the cost scaled with *concurrent* large messages rather than with throughput,
  so a pod on a 2 GB limit taking two 150 MB journal reports at once was OOM-killed mid-transaction
  and lost both.

  Body bytes are now written as they arrive into a `MessageBody` that keeps small messages in memory
  and spills past ~4 MB to a temp file, `Clone()` shares that store rather than copying it, and
  MimeKit parses from the stream directly. Peak memory is now O(buffer) rather than O(message), so
  pod sizing no longer scales with concurrency.

  **New API.** `MailTransaction.GetBodyStream()` returns a forward-only stream over the raw message —
  headers included — and `BodyLength` gives its size in bytes without materializing it. A handler
  that persists mail should copy that stream to its destination; the message then never exists as a
  .NET string.

  **Migration.** `RawBody` still works and still returns the same text, so most consumers need no
  change. It is now a property that materializes the whole message in UTF-16 on **every read**, so a
  handler that reads it repeatedly should read it once — and any handler that may see large messages
  should move to `GetBodyStream()`.

  **The one behavioral break:** a message large enough to have spilled to a temp file has that file
  released when the delivery handler returns, so reading its body from a *retained* transaction
  afterwards throws `ObjectDisposedException`. Messages that never spilled stay readable after
  delivery exactly as before — the asymmetry is deliberate, so that ordinary mail keeps working and
  only the case that was never viable to retain is affected. Consume the body inside
  `EmailReceivedAsync`.

  Line endings in the stored body are now always CRLF. The old path used `StringBuilder.AppendLine`,
  i.e. `Environment.NewLine`, so the same server produced bare-LF bodies on Linux and CRLF on
  Windows; SMTP requires CRLF.

- **`MailTransaction.AddHeader` no longer duplicates a header added before the first parse (bug B2).**
  It used to prepend to `RawBody` *and* explicitly add to `ParsedMessage`; reading `ParsedMessage`
  inside `AddHeader` forced a parse of the already-modified body, which therefore already carried the
  header, and the explicit add put in a second copy. `ParsedMessage` ended up with two where
  `RawBody` had one. The header is now recorded on the body and only an already-parsed message is
  updated, so either order yields exactly one copy.

- **A transaction rejected at the terminating dot is now reset.** The DMARC multi-mailbox `554`, the
  DMARC-fail `554`, and the oversize `552` previously left the transaction live on the connection.
  Besides being wrong (the transaction is over), it held the body's temp file for the remainder of
  the session. A subsequent `DATA` on the same connection now correctly answers `503 RCPT TO first.`

- **Unterminated lines are now bounded (pre-existing hole, unauthenticated).** `Receive` awaited
  `StreamReader.ReadLineAsync`, which materializes a complete line before returning, so a client that
  connected and streamed bytes without ever sending CRLF grew the reader's buffer without limit and
  never reached `MessageCharactersLimit` — that limit is applied per line, *after* the line exists,
  so it bounds a message made of terminated lines but not the line itself. Reachable unauthenticated
  on an internet-facing listener. A new `BoundedLineReader` caps a single line at 1 MB (RFC 5321
  §4.5.3.1.6 sets the conforming limit at 1000 octets), truncating beyond that and draining to the
  next terminator so session framing is preserved.

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

- **DMARC now refuses a From header carrying more than one mailbox.** The single-identity gate counted
  top-level address entries, so one *group* address — `From: Team: attacker@evil.com, victim@bank.com;`
  — counted as one entry while holding two identities, and validation authenticated only the first.
  The gate counts mailboxes now, so such a message is refused with `554` before any policy is applied.
  A group containing exactly one mailbox is still a single identity and is accepted.

  **Why this is listed as breaking:** a message shape that was previously delivered is now rejected.

- **`MAIL FROM:<>` (the null reverse-path) is now accepted instead of refused with `501 5.5.2`.**
  RFC 5321 §4.5.5 requires it to be accepted: it is the reverse-path of every DSN/bounce and of some
  Exchange system-generated reports, so refusing it dropped those messages permanently.
  `ProcessAddress` cannot parse `<>` (empty address, no '@'), so the MAIL FROM branch now recognises
  the null path via `IsNullReversePath` before calling it.

  A null-sender transaction is flagged by the new `MailTransaction.IsNullReversePath` and carries
  `From` and `FromDomain` as **empty strings** (not null).
  **Consumers must expect this**: an `IMailFilter` or `IMailDelivery` that assumes a non-empty
  sender — a `.Split('@')[1]`, a domain-allowlist lookup — will now see `""` on these messages.
  SPF is skipped for the null path (no envelope domain to query), so `SPFValidationResult` is
  `CheckDisabled` and no `Authentication-Results: ... spf=` header is added. **DMARC returns `None`
  for a null path** rather than evaluating alignment: with no envelope identity and no DKIM support,
  RFC 7489 §3.1 has nothing to align, and treating an absent identity as a mismatched one made a
  `p=reject` policy permanently destroy legitimate bounces from the very domain that published it.

  **Known limitation — a null sender is not authenticated in either direction.** RFC 7208 §2.4
  defines the null-path MAIL FROM identity as `postmaster@<HELO domain>`, but the EHLO/HELO argument
  is discarded (only `_protocolVersion` is kept), and DKIM is not implemented. Consequently an
  unauthenticated client can send `MAIL FROM:<>` with a spoofed `From:` under the victim domain's
  `p=reject` and be accepted. The server cannot tell that from a genuine bounce.

  This is a deliberate trade, not an oversight: the alternative — applying the policy — destroys
  legitimate bounces from `p=reject` domains, which for a journaling relay is unrecoverable loss of a
  compliance record. It is also unreachable in that deployment, where DMARC is off entirely. Closing
  it properly means retaining the HELO identity and running the §2.4 check. Pinned as a known
  limitation by `NullSender_WithSpoofedFrom_UnderDmarcReject_IsAccepted_KnownLimitation`, which
  should be inverted to assert `554` when that lands.

  The reverse-path parser is now **anchored**: the path is the first bracket pair, and an argument
  that hides a real address behind an empty pair — `MAIL FROM:AUTH=<> <ceo@victim.example>`,
  `MAIL FROM:<><ceo@victim.example>`, `MAIL FROM:><>` — is refused with `501` instead of being read
  as a null sender. Previously `ProcessAddress` searched for `<` anywhere, so such an argument would
  have let filters see an empty sender while a real address sat in the command. Trailing ESMTP
  parameters are still ignored, including O365's `AUTH=<>`.

  **Why this is listed as breaking:** a command shape that was previously refused is now accepted, and
  reaches delivery handlers with an empty sender.

**On the version number.** This release is 2.0.0 rather than 1.2.0 because several of the changes above
are *silent*: consumers still compile, but behavior changes underneath them. The getter changes alter
what filtering, routing, or display logic sees; the streaming body changes `RawBody` from a field to a
property (a recompile for anyone passing it by `ref`, and a per-read cost where there was none) and
makes a large message's body unreadable once delivery returns. A minor bump would let all of that reach
users through a routine update, and a changelog is not a dependency-resolution barrier. The major bump
is the barrier.

### Fixed

- **Listener shutdown now waits for its accept thread.** `Listener.Dispose()` returned without
  confirming the accept thread had exited, so the thread could still be inside `AcceptTcpClient` when
  `SMTPServer.Dispose()` went on to dispose the TLS certificate — the same certificate-lifetime hazard
  as the accepted-connection case fixed above, one level up. Shutdown is signalled through a
  `CancellationTokenSource` rather than a non-volatile `bool` (which gave the accept loop's read no
  visibility guarantee and could, on a stale read, leave it retrying and logging against a stopped
  socket), and `Dispose()` waits up to 5 seconds for the loop to signal that it exited, logging and
  continuing if it does not. `Dispose()` remains idempotent.
- **SPF: NXDOMAIN in `a`/`mx` is a no-match, not a temperror.** RFC 7208 §5 treats a nonexistent name
  as a definitive answer, so evaluation must continue to the next mechanism (typically a terminal
  `-all`). Both the address lookup and the MX lookup previously collapsed every non-`NoError` response
  into `Temperror`, so `v=spf1 a:missing.test -all` returned `Temperror` instead of `Fail` — and since
  SMTP rejects only on `Fail`, that accepted mail it should have rejected. Only genuinely transient
  failures (SERVFAIL, no response, unparseable) are temperrors now.
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

- **EHLO now advertises the `SIZE` extension (RFC 1870).** The greeting previously ended at
  `250 8BITMIME`; it now ends with `250 SIZE <n>`, letting a sender learn the limit up front rather
  than discovering it only after transmitting a whole message.

  The advertised value is `MessageCharactersLimit` unchanged, which is already a safe understatement:
  the DATA counter adds each line's characters *after* CRLF is stripped, so
  `octets = sum(line bytes) + 2 * lines >= counted characters` for every message. CRLF, UTF-8
  multibyte and dot-stuffing all push octets up relative to characters, never down, so a message the
  character limit accepts can never exceed that many octets. A limit of `0` advertises `SIZE 0`,
  which RFC 1870 §6 independently defines as "no fixed maximum"; finite limits are never rounded down
  into it.

  **The `SIZE=` value a client declares on MAIL FROM is deliberately not acted upon**; the server
  advertises only. Pre-rejecting on a declared size would refuse a message before its data arrived,
  which loses anything a sender over-declares. Oversized messages are still caught at the terminating
  dot with `552 5.4.3`.

  Note for consumers asserting on the EHLO response: `8BITMIME` is no longer the last line, so its
  prefix changed from `250 ` to `250-`.

- `SMTPServer.VersionString` and `AssemblyVersionString` had drifted behind the published
  `PackageVersion` (`1.1.6-krugertech.1` vs `1.1.6-krugertech.3`). All three now move together at
  `1.2.0`.
