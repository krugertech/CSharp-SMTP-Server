# Unit Test Plan — Krugertech CSharp SMTP Server

Working plan of everything worth testing, derived from a full read-through of the source on 2026-07.
**No tests are written yet** — this is the backlog. Each entry lists concrete cases and expected
outcomes so implementation is mechanical.

## 0. Current coverage (do not duplicate)

| File | Covers |
|---|---|
| `AckGatingTests.cs` (6) | DATA awaits delivery; no fire-and-forget; 451 on temp failure / exception; exactly-once delivery |
| `AuthLoginInitialResponseTests.cs` (4) | AUTH LOGIN bare two-step; inline username → password prompt; wrong password 535; invalid base64 fallback |
| `EhloBracketedIpv6Tests.cs` (3) | EHLO `[IPv6:…]` accepted + session usable; plain EHLO regression; MAIL FROM with bracketed IP literal still parses |

Everything below is **new**.

## 1. Test infrastructure recommendations (do first)

1. **Add `InternalsVisibleTo("CSharp-SMTP-Server.Tests")`** to the library (csproj `<ItemGroup>` or
   `Properties/AssemblyInfo.cs`). Unlocks direct unit tests of internal helpers:
   `TransactionCommands.ProcessAddress`, `Misc.Base64`, `SMTPCodes.SendCode`,
   `ClientProcessor.WriteCode(int,string)` sanitization. Without it these are only reachable via raw TCP.
2. **Extract a shared `SmtpSession` helper** (connect/read-line/send/multi-line-response + port allocator)
   into one common file — currently duplicated in all three test files; will be needed by ~8 more files.
3. **Self-signed TLS certificate helper**: generate at test time (RSA 2048, CN=localhost, SAN =
   `localhost` + `127.0.0.1`) or check in a throwaway `.pfx`. Needed for all TLS/STARTTLS tests.
4. **Local HTTP server helper** (`HttpListener` on loopback) serving a minimal Public Suffix List —
   required by `DmarcValidator` (its constructor downloads the list and blocks). Points at
   `ServerOptions.PublicSuffixList`; keeps DMARC tests offline.
5. **Optional UDP DNS stub** (P2, biggest investment): minimal responder for TXT/MX/A/AAAA/PTR queries so
   `SpfValidator.CheckHost` / `DmarcValidator.GetDmarcRecord` can be unit-tested deterministically.
   `ServerOptions.DnsServerEndpoint` accepts any endpoint, so the stub slots in cleanly. Estimate 300–500 LOC.
6. **xUnit traits** for anything touching real network (`[Trait("Network","dns")]`) so offline CI runs only
   loopback tests by default. Keep the existing 10 s read-timeout pattern everywhere.

## 2. Confirmed bugs & quirks to pin down with tests ⚠️

These were verified against the source (and, where noted, empirically). Tests should **document current
behavior** first; fixing is a separate decision. Each needs at least one test that would fail if the
behavior changed silently.

| # | Where | Finding | Verified |
|---|---|---|---|
| B1 | `MailTransaction.GetFrom` / `GetTo()` / `GetCc()` / `GetBcc()` | Return MimeKit **display names**, not addresses: `From: sender@example.com` → `""`; `From: John <j@e.c>` → `"John"`. Consequence: DMARC validation (`ProcessAddress(GetFrom, …)` requires `<…>`) **effectively never validates real mail** — it returns `None` for any normal From header. | ✅ empirical (MimeKit 4.17 scratch run) |
| B2 | `MailTransaction.AddHeader` | Prepending to `RawBody` **and** explicit `ParsedMessage.Headers.Add`: when called before the first parse, the parsed message contains the header **twice** (once from parsing the modified raw body, once explicit). Affects the server-added `Received:` / `Authentication-Results:` headers. | ✅ empirical (count = 2) |
| B3 | `MailTransaction.Clone()` | Does **not copy `DMARCValidationResult`** → the transaction handed to the delivery handler always has `None`, even when DMARC validation ran and passed/failed. | code read |
| B4 | `MailTransaction.Clone()` | Copies `DeliverTo` by **reference** — clone and original share one list instance. Currently harmless (original is discarded) but surprising; pin it down. | code read |
| Q1 | `TransactionCommands.ProcessData` | **No dot-stuffing support** (RFC 5321 §4.5.2): a body line `..foo` is stored as-is, and any literal line starting with `.` is ambiguous. Bodies containing such lines are corrupted/lossy. | code read |
| Q2 | `ClientProcessor.ProcessResponse` VRFY | Returns `252` (success class) with enhanced status **`5.5.1`** (permanent-failure class). Inconsistent per RFC 3463. | code read |
| Q3 | `TransactionCommands.ProcessData` | For authenticated users the added `Received:` header has no `from <ip>` part — just `by <server> with SMTP; …`. Undocumented behavior. | code read |
| Q4 | `ClientProcessor.ProcessResponse` STARTTLS | No protocol-version gate: STARTTLS is accepted **before EHLO/HELO** (all other extension commands require it). | code read |
| Q5 | `TransactionCommands` RCPT TO | Syntax error returns bare `501` with **no enhanced status**, unlike every other response. | code read |
| Q6 | `ServerOptions.MessageCharactersLimit` enforcement | Counter counts characters **excluding CRLF**; once exceeded, further lines are silently dropped from `RawBody` (moot — the transaction is rejected with 552 anyway). Boundary: exactly-at-limit is accepted (`>=`). | code read |

## 3. Pure unit tests — no I/O (P0)

### 3.1 `SmtpDeliveryResult`
- `Ok()` → `(250, "2.0.0", "OK")`; `Ok("custom")` keeps code/enhanced, custom message.
- `TemporaryFailure()` → `(451, "4.3.0", default msg)`; with custom message.
- `PermanentFailure()` → `(554, "5.7.1", default msg)`; with custom message.
- `Status(550, "5.1.1", "unroutable")` passes all three through verbatim (incl. unusual codes like 552/5.4.3).
- Message containing `\n`, `\r`, or `\r\n` → **throws** `ArgumentException`.
- Enhanced status containing `\n` or `\r` → throws.
- Empty message / empty enhanced status → allowed (document current behavior).
- `null` message → document actual exception type (currently NRE from `IndexOf`) — candidate for ArgumentNullException.

### 3.2 `ServerOptions`
- Defaults: `ServerName="CSharp SMTP Server"`, `RequireEncryptionForAuth=true`, `Protocols=Tls12`,
  `MessageCharactersLimit=10485760`, `RecipientsLimit=50`.
- Ctor `(true, false, null)` → `DnsServerEndpoint` defaults to `1.1.1.1:53` (SPF requested).
- Ctor `(false, true, null)` → same default (DMARC requested).
- Ctor `(false, false, null)` → endpoint stays **null**.
- Ctor with explicit endpoint → preserved verbatim.
- `ValidateSPF = true` when endpoint is null → throws; after ctor set an endpoint path works.
- `ValidateSPF = false` always allowed (even without endpoint). Same pair of cases for `ValidateDMARC`.

### 3.3 `MailTransaction` (MimeKit-backed)
Build transactions via a small factory that uses the internal ctor (IVT) or via a full SMTP session; prefer IVT for speed.
- `ParsedMessage`: lazy parse — same instance returned on second access; parses valid RFC 5322 body.
- `Subject` extraction from raw body with/without encoded-word subjects (`=?UTF-8?B?…?=`).
- `GetFrom` / `GetTo()` / `GetCc()` / `GetBcc()`: plain address, display name + angle brackets, missing header (null/empty), multiple addresses. **Pin B1** (display-name semantics) explicitly.
- `GetMessageBody()`: text/plain only; multipart (TextBody wins); HTML-only fallback (HtmlBody); both present → TextBody.
- `AddHeader` before first parse: RawBody gets one copy, ParsedMessage gets **two** — pin B2. After parse: exactly one added copy in each.
- `Clone()`: preserves From/FromDomain/RawBody/AuthenticatedUser/RemoteEndPoint/Encryption/SPF result;
  **pins B3** (DMARCValidationResult lost) and **B4** (`ReferenceEquals(clone.DeliverTo, original.DeliverTo)` is true today).
- `ICloneable` contract: `Clone()` returns non-null object castable to `MailTransaction`.

### 3.4 `SpfValidator.CheckCIDR` (public static — direct)
Full matrix:
- mask `0` → always true (same family); different families (`InterNetwork` vs `InterNetworkV6`) → false even at /0.
- exact match at `/32` and `/128`; one-bit difference at max mask → false.
- partial-byte masks: `/24`, `/25`, `/17` — in-subnet true, out-of-subnet false (both byte-aligned and mid-byte boundaries).
- mask larger than address width (`/33` for IPv4) → clamped to full width (document behavior).
- negative mask → `ArgumentException`.

### 3.5 `DmarcValidator.GetOrganizationalDomain` (public static — needs suffix list loaded)
Use the local HTTP helper (§1.4) with a controlled list containing e.g. `com`, `co.uk`, `uk`:
- `"mail.example.com"` → `"example.com"`.
- `"a.b.co.uk"` → `"b.co.uk"` (public-suffix walk).
- `"example.com"` / `"x.y"` (≤ 2 labels) → returned unchanged.
- Suffix list never loaded → throws `Exception` with the documented message (test both ctor-less call and after failed load).

### 3.6 `Misc.Base64` (internal — IVT)
- Round-trip: ASCII, UTF-8 multibyte (e.g. "héllo"), empty string.
- Decode valid input **with** padding and **without** padding (`Convert.FromBase64String` accepts both).
- Invalid characters / bad length → `null`.
- Whitespace-only / embedded newline behavior — document what `Convert` does today.

### 3.7 `TransactionCommands.ProcessAddress` (internal — IVT)
The address-validation matrix, each case asserting `(address, domain)` out-values:
| Input | Expected |
|---|---|
| `null`, `""` | null / null |
| `"a@b.com"` (no angle brackets) | null |
| `"<>"`, `"<   >"` | null |
| `"<a@b>"` (domain without dot) | null |
| `"<ab@c.d.e>"` | address + domain `c.d.e` |
| `"<a.b@c>"` (dot before @) | null |
| `"<a@@b.c>"`, `"<@b.c>"` | null (two @ / empty local part) |
| `"<a@.c>"` — domain is `.c`: lastDot > atIndex and domain contains '.' → **accepted** with domain `.c`. Pin this surprising edge case! | accepted per code |
| `"\"John Doe\" <john@example.com>"` (display name) | `john@example.com` |
| leading colon from command parsing (`":<a@b.c>"`) | still parses (first `<…>`) |
| multiple angle-bracket pairs → first pair wins — pin | per code |

### 3.8 Wire-output helpers (internal — IVT)
- `ClientProcessor.WriteCode(int, string)` sanitizes `\r`/`\n` to spaces in the message (defense-in-depth;
  currently unreachable with bad input because `SmtpDeliveryResult` throws first — test it directly anyway).
- `SMTPCodes.SendCode`: each table entry produces `"code text"` / `"code enhanced text"` exactly.

### 3.9 Trivial value types
- `SmtpResult` struct: ctor stores type + message; default `FailMessage` null.
- Enum sanity (low priority): `UserExistsCodes` values 0–5 in documented order; `SmtpResultType`,
  `ConnectionEncryption`, `ValidationResult` member sets — guards against accidental reordering that would
  change wire mappings or persistence assumptions.

## 4. Protocol state machine over raw TCP (P1, loopback)

One test file per command group; shared `SmtpSession` helper throughout.

### 4.1 Greeting & connection filter
- First line is exactly `220 <ServerName> ESMTP`.
- `IsConnectionAllowed` → Success: greeting sent.
- → PermanentFail: first line is `550 … 5.7.1 Delivery not authorized, connection refused`, connection closed immediately (no 220 ever).
- → TemporaryFail: same but enhanced status `4.7.1`.
- Custom non-empty `FailMessage` replaces the default text; whitespace-only message falls back to default.

### 4.2 EHLO / HELO
- EHLO multi-line response, exact sequence per configuration matrix:
  | Auth set? | Cert? | Secure port? | Advertised lines (after first) |
  |---|---|---|---|
  | no | no | – | none — final `250 8BITMIME` only |
  | yes | no | – | `AUTH LOGIN PLAIN` |
  | no | yes | plain | `STARTTLS` |
  | yes | yes | plain | both, in order AUTH then STARTTLS |
  | any | yes | TLS port (already secure) | **no** STARTTLS line |
- HELO → single-line `250 <ServerName> at your service`, no extensions.
- EHLO resets an in-flight transaction: MAIL FROM + RCPT TO, then EHLO, then DATA → `503 … RCPT TO first.`.
- Case insensitivity: `ehlo x`, `hElO x` work; lowercase `mail from:<a@b.c>` works.
- Parsing edge cases (pin current behavior): empty line → 502/503 per protocol state; double space in
  command (`MAIL  FROM:`) → treated as unknown command 502 — pin; trailing whitespace tolerated.
- HELO with bracketed IPv6 literal (sibling of the EHLO fix already tested).

### 4.3 Command sequencing & misc commands
- Each of `AUTH`, `RSET`, `MAIL FROM`, `RCPT TO`, `DATA`, `VRFY` before EHLO/HELO → `503 … EHLO/HELO first.`.
- Unknown command after EHLO → `502 Unrecognized command`; before EHLO → 503.
- `NOOP` → `250 OK`; `HELP` → `214 There is no help for you`; `QUIT` → `221 …` and server closes the connection.
- `RSET` mid-transaction → `250 2.1.5 Flushed`, then DATA → 503 (transaction cleared).
- `VRFY` after EHLO → `252` — **pin Q2** (enhanced status is `5.5.1`).

### 4.4 MAIL FROM
Wire-level matrix (SPF disabled in these tests):
| Sent | Expected |
|---|---|
| `MAIL FROM:<a@b.com>` | `250 2.0.0` |
| `MAIL FROM: <a@b.com>` (space after colon) | `250 2.0.0` |
| `MAIL FROM:a@b.com` (no brackets) | `501 5.5.2` |
| `MAIL FROM:<a@b>` / `<a@@b.c>` / `<a.b@c>` / `<>` | `501 5.5.2` each |
| display name form `"J" <j@e.c>` | accepted, transaction.From = `j@e.c` (verify via delivery) |
- `IsAllowedSender`: Success → proceed; PermanentFail → `554 … 5.7.1 Delivery not authorized (MAIL FROM address not allowed), message refused`; TemporaryFail → same text with `4.7.1`; custom FailMessage propagation.
- Filter receives correct args: source address, remote endpoint, username (null when unauthenticated).
- SPF-disabled path sets transaction SPF result to `CheckDisabled` (verify via delivered transaction).

### 4.5 RCPT TO
| Scenario | Expected |
|---|---|
| before MAIL FROM | `503 … MAIL FROM first.` |
| invalid address | bare `501` — **pin Q5** (no enhanced status) |
| each of the 6 `UserExistsCodes` values | exact wire mapping: valid→`250 2.1.5`; BadMailbox→`550 5.1.1`; BadSystem→`550 5.1.2`; Ambiguous→`550 5.1.4`; MovedNoForwarding→`550 5.1.6`; BadSendersSystem→`550 5.1.8` |
| `RecipientsLimit = 2`: 1st, 2nd accepted; 3rd | `550 5.5.3 Too many recipients` (boundary: exactly limit allowed) |
| `CanDeliver` PermanentFail / TemporaryFail / custom message | `550 … 5.7.1/4.7.1 Delivery to this recipients is not allowed, message refused` (+custom) |
- Multiple RCPT TO accumulate: delivered transaction's `DeliverTo` contains all in order (verify via handler).

### 4.6 DATA & message processing
| Scenario | Expected |
|---|---|
| before RCPT TO | `503 … RCPT TO first.` |
| normal body terminated by lone `.` | `354` then delivery result code |
| `MessageCharactersLimit = N`: body exactly N chars (excl. CRLF) | accepted; N+1 → `552 5.4.3 Message size exceeds the administrative limit.` (**pin Q6** boundary + counting rule) |
| `MessageCharactersLimit = 0` | no limit |
| delivered RawBody contains prepended `Received:` header, format `from <ip> by <ServerName> with SMTP; <UTC stamp> (UTC)` — **pin Q3** for authenticated users (`by …` only, no `from`) |
| body line starting with `..` is stored verbatim (no unstuffing) — **pin Q1**; lone `.` mid-body terminates capture |
| `CanProcessTransaction` PermanentFail/TemporaryFail/custom → `554 … 5.7.1/4.7.1 Delivery not authorized, message refused`; transaction reset afterwards (next DATA without MAIL FROM → 503) |
| after successful delivery the session can start a **new** transaction (MAIL FROM works again) |
| delivered metadata: `From`, `DeliverTo[]`, `RemoteEndPoint` = loopback peer, `Encryption` per scenario (§4.8), `AuthenticatedUser` set only when authenticated |

### 4.7 AUTH (beyond existing AuthLoginInitialResponseTests)
- No auth configured → `AUTH …` gives `502 5.5.1`.
- `RequireEncryptionForAuth = true` on plaintext → `538 5.7.11 Encryption required…`; after STARTTLS → allowed (see §4.8).
- Unknown mechanism (`AUTH CRAM-MD5`) → `501 5.7.4 Unrecognized Authentication Method`.
- Bare `AUTH` (no args) → `501 5.7.4`.
- **PLAIN inline**: correct creds → `235 2.7.0 Authentication Succeeded`; wrong password → `535 5.7.8`; malformed base64 → 535; payload with only two NUL-separated parts (missing authzid) → 535; four parts → 535; empty authzid (`\0user\0pass`) → success path; `IAuthLogin.AuthPlain` receives all three identities + endpoint + secure flag (recording fake).
- **PLAIN interactive** (no inline): `334` prompt, then base64 line → 235/535.
- **LOGIN**: wrong username at step 1 → 535 after password; invalid base64 at either step → 535; empty decoded password → 535 (document); success sets `Username` — verify via `CanDeliver(authenticated: true, username)` and delivered `AuthenticatedUser`.
- **LOGIN inline with decodable-but-empty username** (`AUTH LOGIN ` + base64(`""`)) → falls back to standard two-step prompt flow.
- Auth state survives a re-EHLO (document current behavior).

### 4.8 TLS / STARTTLS (needs cert helper §1.3)
- Implicit-TLS port: client must handshake first; then `220` greeting over TLS; delivered transaction has `Encryption = Tls`.
- STARTTLS on plain port with cert: EHLO advertises it; after `220 Ready for TLS` the upgrade succeeds; subsequent commands work; delivered transaction has `Encryption = StartTls`; second STARTTLS → `503 5.5.1`.
- STARTTLS without certificate configured → `502 5.5.1`.
- **Pin Q4**: STARTTLS as the very first command (before EHLO) is accepted today.
- TLS port constructed **without** a certificate: server falls back to plaintext greeting (`Secure = secure && Certificate != null`) — pin this behavior.
- `SetTLSCertificate` after construction: new connections' EHLO then advertises STARTTLS (dynamic lookup).
- `RequireEncryptionForAuth`: AUTH before upgrade → 538; after upgrade → 235 with correct creds.

## 5. ACK-gating additions (P1, beyond AckGatingTests)

- **Cancellation**: client abruptly closes the socket while delivery is gated in a slow handler →
  `CancellationToken` passed to the handler fires within a bounded time; server stays healthy for the next client.
- **Clone semantics at delivery** (pins B3/B4): handler inspects its transaction — `DMARCValidationResult`
  is always `None` today even with DMARC enabled and passing; mutating `DeliverTo` in the handler does not
  affect server state (and document shared-list reference).
- **Exception mapping detail**: handler throwing `OperationCanceledException` specifically → still 451 + logged
  (document); log line contains exception type + message.
- **Response fidelity**: `Status(552, "5.4.3", "too big")` from the handler reaches the wire verbatim
  (`552 5.4.3 too big`).
- **Concurrent sessions**: two clients delivering simultaneously — both get correct responses, handlers run in parallel (no cross-talk).

## 6. SPF validator (P2 — needs DNS stub §1.5)

`CheckHost(ip, domain)` against stub-controlled records:
- No TXT record / query error → `Temperror`; no `v=spf1` record → `None`.
- Two SPF records in one response → `Permerror`.
- Qualifiers: `-all` (Fail), `~all` (Softfail), `?all` (Neutral), `+all`/bare `all` (Pass) — as the terminal mechanism.
- Mechanisms: `ip4:<addr>` exact + `/cidr` match & mismatch; `ip6:` same for IPv6 client IP; family mismatch (`ip4` record vs IPv6 client) → falls through to final result; `a` / `mx` (A/AAAA + MX lookups via stub); `include:` (Pass propagates with qualifier; Permerror/None in included domain → Permerror); `redirect=` (result propagation; redirect to nonexistent → Permerror).
- IPv4-mapped-to-IPv6 client address is unmapped before matching.
- Request-limit behavior: >10 DNS requests during evaluation → `Permerror` (or `Fail` when PTR was used) — construct a record with many mechanisms.
- Final fallback: no mechanism matched → `Neutral`.

## 7. DMARC validator (P2 — needs suffix-list HTTP helper + DNS stub)

- `GetDmarcRecord`: TXT not starting `v=DMARC1;` ignored; two DMARC records in one response → treated as none; missing record → null.
- `ValidateTransaction`: no From header / unparseable From domain → `None`; no `_dmarc` record on domain **or** org domain → `None`.
- Alignment: envelope domain == header domain → `Pass` regardless of policy; misaligned + relaxed (`aspf=r`) with same org domain → `Pass`; strict (`aspf=s`) with subdomain mismatch → not aligned.
- Policy mapping when unaligned: `p=reject` → `Fail`; `p=quarantine` → `Softfail`; no p / other → `None`.
- Subdomain policy: header from a subdomain (no own `_dmarc`) + org record with `sp=reject/quarantine` applies instead of `p=`.
- **Pin B1 end-to-end**: a perfectly normal message (`From: sender@example.com`, SPF-aligned) yields `None` today because `GetFrom` returns the display name — test documents that DMARC is effectively inert until B1 is fixed.

## 8. Lifecycle, listeners & robustness (P1)

- Ctor with `null` parameters list → server starts with zero listeners; `AddListener` after `Start()` begins accepting immediately (`_started` flag path).
- Ctor parameter list containing a `null` entry / null port arrays → skipped without throwing.
- Multiple `ListeningParameters` (several regular + TLS ports) → each port accepts connections independently.
- `Dispose()`: listeners stop; open client connections are reset (RST via linger option); subsequent connects fail.
- Port already in use: `Start()` does not throw, the failing listener logs an error, other listeners keep working.
- `dualMode = true` with IPv4 address → DualMode **not** set on the socket (regression guard for our fork fix); with `IPv6Any` it is set (platform-permitting).
- **Garbage input**: binary bytes, a single 1 MB line, UTF-8 multibyte sequences in DATA — server survives and serves the next client.
- **Abrupt disconnect** at each phase (after greeting / mid-EHLO response / mid-DATA / during gated delivery) → no unhandled exceptions (recording logger asserts), listener keeps accepting new clients.
- **Concurrency stress**: N parallel full SMTP sessions (e.g. 20 × 10 messages) — all succeed; guards the known unsynchronized `Listener.ClientProcessors` race (documented TODO in `Listener.cs`). Expected to be flaky-by-design until that fix lands — mark with a trait and treat failures as evidence, not regressions.

## 9. Meta / consistency tests (P1, cheap)

- `SMTPServer.VersionString` numeric prefix matches the csproj `PackageVersion` major.minor.patch
  (currently **drifted**: constant says `-krugertech.1`, package is `.3`) — read the csproj from disk relative to the test assembly; fail with a clear message on drift.
- `AssemblyVersionString` parses as a valid 4-part version and matches `typeof(SMTPServer).Assembly.GetName().Version`.

## 10. Out of scope (for now)

- SampleApp (console demo, no logic worth testing).
- Real-network SPF/DMARC tests against live domains — flaky by nature; the DNS stub (§1.5) replaces them.
- Performance/benchmarking (throughput under load) — separate effort if ever needed.
- DKIM — not implemented in this fork (upstream `dkim` branch deliberately not merged).

## 11. Suggested build order & rough size

| Phase | Sections | Est. test count | New infra needed |
|---|---|---|---|
| 1 | §3 (pure) + §9 (meta) | ~70 | IVT only |
| 2 | §4.1–4.7, §5, §8 (loopback protocol + robustness) | ~80 | shared SmtpSession helper |
| 3 | §4.8 (TLS) | ~10 | cert helper |
| 4 | §6, §7 (SPF/DMARC) | ~30 | suffix-list HTTP helper + DNS stub |

Total ≈ **190 test cases**. Phases 1–2 give the highest value-per-effort and need almost no new infrastructure.
