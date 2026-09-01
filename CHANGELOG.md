# Changelog

All notable changes to this fork are documented here. The current design is described in
[`ARCHITECTURE.md`](ARCHITECTURE.md), and unresolved work is tracked in
[`KNOWN_ISSUES.md`](KNOWN_ISSUES.md). This fork is based on
[zabszk/CSharp-SMTP-Server](https://github.com/zabszk/CSharp-SMTP-Server) v1.1.6; upstream merge and
skip decisions are retained in the architecture's
[sync record](ARCHITECTURE.md#upstream-sync-record).

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
  For validation, RFC 7208 §2.4 makes the DNS-form EHLO/HELO name the SPF identity when the path is
  null. That identity is retained in the new `MailTransaction.HeloDomain`, SPF-checked, and used for
  DMARC alignment only after an SPF `Pass`. A spoofed null-path message under an aligned `p=reject`
  policy is therefore refused, while a message with no authenticated HELO identity yields DMARC
  `None`. The full validation behavior and new rejection paths are described under **Fixed** below.

  The reverse-path parser is now **anchored**: the path is the first bracket pair, and an argument
  that hides a real address behind an empty pair — `MAIL FROM:AUTH=<> <ceo@victim.example>`,
  `MAIL FROM:<><ceo@victim.example>`, `MAIL FROM:><>` — is refused with `501` instead of being read
  as a null sender. Previously `ProcessAddress` searched for `<` anywhere, so such an argument would
  have let filters see an empty sender while a real address sat in the command. Trailing ESMTP
  parameters are still ignored, including O365's `AUTH=<>`.

  **Why this is listed as breaking:** a command shape that was previously refused is now accepted, and
  reaches delivery handlers with an empty sender.

- **The DATA path is byte-exact, and dot-stuffing is now undone (Q1).** Two defects with one cause:
  every body line was decoded to a .NET string and re-encoded as UTF-8 on the way into the body store.

  **Byte-exactness.** A message body is an octet stream — it may be in any charset, or be an
  unlabelled 8-bit body — and the round trip through UTF-16 replaced every byte that was not valid
  UTF-8 with U+FFFD, stored as `EF BF BD`. The archived message was therefore not what the sender
  transmitted. That matters wherever integrity is established downstream by **DKIM**, which hashes the
  octets it is handed: a transcode in the middle invalidates the signature on any non-UTF-8 body.
  `BoundedLineReader` is now byte-primary, `ReadLineBytesAsync` hands the DATA path the wire bytes
  directly, and what arrives is what is stored.

  **Dot-unstuffing.** RFC 5321 §4.5.2 requires the receiver to strip the transparency dot a sending
  client prefixes to any body line already beginning with one. It was never implemented, so a composed
  line `.text` was archived as `..text` — corruption of exactly the lines the mechanism exists to
  carry, and on its own enough to break a DKIM signature over that body. The dot is now stripped, and
  the line is counted against `MessageCharactersLimit` **after** unstuffing, so the enforced limit
  measures the message as stored rather than as framed.

  **Stored bytes change for affected messages.** A body line beginning with a dot is now archived
  unstuffed, so the same message hashes differently across this boundary. This is the correct form —
  messages archived before it are the ones that do not match what their sender signed.

**On the version number.** This release is 2.0.0 rather than 1.2.0 because several of the changes above
are *silent*: consumers still compile, but behavior changes underneath them. The getter changes alter
what filtering, routing, or display logic sees; the streaming body changes `RawBody` from a field to a
property (a recompile for anyone passing it by `ref`, and a per-read cost where there was none) and
makes a large message's body unreadable once delivery returns. A minor bump would let all of that reach
users through a routine update, and a changelog is not a dependency-resolution barrier. The major bump
is the barrier.

### Fixed

- **Quoted local-parts containing angle brackets or `@` are no longer refused.** RFC 5321 §4.1.2
  permits the local-part to be a quoted-string, inside which `<`, `>` and `@` are ordinary characters.
  The parser was unaware of quoting in two places: `TryGetBracketedPath` took the *first* `>` as the
  path terminator, and `ProcessAddress` required exactly one `@` anywhere in the address. So
  `<"a>b"@example.com>` and `<"a@b"@example.com>` — both valid — got a permanent `501`. Shared by
  `RCPT TO`, so it could lose a recipient as well as a sender.

  Scanning is now quote-aware and honours quoted-pairs. An unterminated quoted-string yields no path
  rather than falling back to the first `>`: this is the anchored path locator that closes the
  null-reverse-path smuggling differential, and a quote that could swallow the terminator would hand
  an attacker control of where the parser thinks the path ends. Those defences are re-asserted
  directly against the new scanner rather than assumed to survive it.

- **A null sender's HELO identity is now SPF-checked (RFC 7208 §2.4).** §2.4 defines the SPF MAIL FROM
  identity for a null reverse-path as `postmaster@<HELO domain>`, but the EHLO/HELO argument was
  discarded, so SPF was skipped entirely for `MAIL FROM:<>` and a HELO domain publishing `-all` was
  not enforced against a null sender. The argument is retained as the new public
  `MailTransaction.HeloDomain` and checked in place of the absent envelope domain;
  `Authentication-Results` reports `smtp.helo=` rather than an empty `smtp.mailfrom=`, per RFC 8601
  §2.7.2.

  Only a plausible DNS name is retained — an address literal or a bare label cannot carry an SPF
  record — so a client with no checkable identity stays unchecked and this does not become a new
  rejection for MTAs that greet with a literal. **New rejection path:** a null sender from a HELO
  domain that fails SPF now gets `554 5.7.23`, reachable only with `ValidateSPF` on.

- **A null sender is now DMARC-enforced against its HELO identity.** RFC 7489 §3.1.2 names the HELO
  identity as the one DMARC aligns *"when required to 'fake' an otherwise null reverse-path"* — the
  same name RFC 7208 §2.4 has SPF authenticate — so `MAIL FROM:<>` carrying a spoofed
  `From: ceo@victim.example` under `victim.example`'s `p=reject` is now refused with `554 5.7.1`
  instead of accepted.

  **Alignment requires an SPF `Pass`.** DMARC is built on the *result* of SPF authentication, not on a
  name the client asserted; a HELO domain is attacker-controlled text until SPF says the connecting IP
  may use it. With `ValidateSPF` off, no SPF record, or a DNS temperror there is no authenticated
  identity, so DMARC returns `None` and the message is **delivered**.

  That gate is what keeps legitimate bounces alive. A bouncing MTA greets with its own hostname, which
  routinely differs from the From domain of the notification it carries, so unconditional alignment
  would destroy ordinary DSNs under the very policy their domain published. A spoof is still caught:
  the attacker either fails SPF on its own HELO domain, or passes for a domain that is not the one it
  spoofed — which then fails to align.

  **New rejection path**, reachable only with both `ValidateSPF` and `ValidateDMARC` on. DKIM remains
  unimplemented and is still the only mechanism that could authenticate a null-path message whose HELO
  identity does not align.

- **A message whose DATA line was truncated is refused rather than delivered.** `BoundedLineReader`
  truncates a line past its 1 MB cap to bound memory against a client that never sends a terminator,
  and reported it via `LastLineTruncated` — but nothing consumed that signal. `ProcessData` saw only
  the retained prefix, so it stored the prefix and counted the prefix against
  `MessageCharactersLimit`: a 3 MB DATA line was delivered as its first 1 MB with a `250`, and the
  size limit could not catch it because the discarded bytes were never counted.

  Truncation is now latched for the message and refused at the terminating dot with `552 5.4.3`. For a
  journaling relay an acknowledged, silently truncated record is worse than a refusal — nothing
  downstream can tell the message was cut, and it breaks any DKIM signature over that body.

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
  the DATA counter adds each line's stored bytes *after* CRLF is stripped and dot-stuffing is undone,
  so `wire octets >= counted bytes` for every message. A message whose RFC 1870 wire size is at most
  the advertised value therefore cannot exceed the stored-byte limit; the server may accept a
  somewhat larger wire message because CRLF and stuffing bytes are excluded. A limit of `0`
  advertises `SIZE 0`,
  which RFC 1870 §6 independently defines as "no fixed maximum"; finite limits are never rounded down
  into it.

  **The `SIZE=` value a client declares on MAIL FROM is deliberately not acted upon**; the server
  advertises only. Pre-rejecting on a declared size would refuse a message before its data arrived,
  which loses anything a sender over-declares. Oversized messages are still caught at the terminating
  dot with `552 5.4.3`.

  Note for consumers asserting on the EHLO response: `8BITMIME` is no longer the last line, so its
  prefix changed from `250 ` to `250-`.

- `SMTPServer.VersionString` and `AssemblyVersionString` had drifted behind the published
  `PackageVersion` (`1.1.6-krugertech.1` vs `1.1.6-krugertech.3`). They now move together:
  package/informational version `2.0.0-krugertech.1`, assembly version `2.0.0.0`.
