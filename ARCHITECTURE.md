# Repository Understanding — Krugertech CSharp SMTP Server (fork of v1.1.6)

> Working notes for anyone picking up this repo. Verified against the source on 2026-07; build and tests confirmed passing at time of writing.

## 1. What this is

A **receive-only SMTP server library for C#**. It is a fork of
[zabszk/CSharp-SMTP-Server](https://github.com/zabszk/CSharp-SMTP-Server) v1.1.6 (released 23 Dec 2023),
rebranded as **Krugertech CSharp SMTP Server** and published to NuGet as
`Krugertech.CSharp-SMTP-Server` version `1.1.6-krugertech.3`.

The fork exists for one reason: **ACK gating on the DATA command**. Upstream fires delivery in a
background task and immediately returns `250 OK` (fire-and-forget). This fork *awaits* the delivery
handler before sending any SMTP response, so `250 OK` is a true durability guarantee — the sending
MTA keeps its copy until your handler says the message was safely accepted.

Not affiliated with or endorsed by the original author.

## 2. Git history (11 commits)

| Commit | Meaning |
|---|---|
| `2f7386e` Add project files. | Import of upstream v1.1.6 — **the baseline for all fork diffs** |
| `50276ad` Implement ACK-gating for reliable SMTP delivery | The core feature |
| `3a3dd1c` Rebrand to Krugertech CSharp SMTP Server | Package ID, authors, version strings |
| `516702a` Update project metadata and license details | NuGet/LICENSE metadata |
| `9dce319` Add support for enhanced SMTP status codes | RFC 3463 codes + `SmtpDeliveryResult.Status(...)` |
| `221a2b5` Update package version to 1.1.6-krugertech.3 | Version bump |
| `8fa02c8` Handled unhandled exceptions reported in #16 | **Cherry-pick from upstream** — IOException handling + write cancellation token (see §9) |
| `7d0d50f` Support AUTH LOGIN initial response (RFC 4954)… | Adapted from upstream PR #17 — IIS SMTP relay compatibility (see §9) |
| `274069a` Fix EHLO/HELO parsing of bracketed IPv6 literals | Upstream issue #18, fixed here (no upstream fix exists; see §9) |
| `294adbe` Fix ClientProcessor ctor blocking the listener accept thread | Fork fix found during Phase 1 testing — when the greeting write completes synchronously (typical on Windows), `Init()` ran inline in the ctor and parked inside `Receive()`'s `EndOfStream` check, consuming the accept thread: a second concurrent client never got its 220. `Init` now runs on the thread pool; regression test `ConcurrentGreetingTests` |
| `e8a241f` Add Phase 1 unit tests (TEST_PLAN.md §3 + §9): 107 new tests | Pure unit suite per `TEST_PLAN.md`; includes pins for confirmed upstream bugs B1–B4 in `MailTransaction` (see TEST_PLAN.md §2) |
| `994eb07` Extract shared SmtpSession helper for integration tests (§1.2) | Test infrastructure — connect/send/read with 10 s timeouts, raw-byte send, RST abort; existing test files refactored onto it |
| `4a4cb06` Add Phase 2 protocol matrix tests (TEST_PLAN.md §4.1–4.7): 86 new tests | Exact-wire assertions for every command group; pins quirks Q1/Q2/Q3/Q5/Q6 and the newly found **Q7** (two-arg `WriteCode(code, enhanced)` call sites bind to the `(int,string)` sanitizer overload — no table text on most responses) |
| `df4636e` Fix Listener.ClientProcessors thread-safety; add §5/§8 tests | The documented TODO race was real: concurrent Add/Remove corrupted the list → NRE in `Dispose()` under load (deterministic repro via new `ConcurrencyStress` test); fixed with lock + snapshot-in-Dispose. Also pins **Q8**: delivery CancellationToken does not fire on client disconnect mid-delivery |

To see exactly what the fork changed: `git diff 2f7386e..HEAD`.

## 3. Solution layout

```
CSharp-SMTP-Server.sln
├── CSharp-SMTP-Server/            ← THE LIBRARY (NuGet package)
│   ├── SMTPServer.cs              public entry point
│   ├── ServerOptions.cs           configuration POCO
│   ├── MailTransaction.cs         per-message object handed to your handler
│   ├── Interfaces/                IMailDelivery, IAuthLogin, IMailFilter, ILogger
│   ├── Networking/                Listener (accept loop), ClientProcessor (per-connection state machine)
│   ├── Protocol/
│   │   ├── Commands/              TransactionCommands.cs (MAIL/RCPT/DATA), AuthenticationCommands.cs (AUTH LOGIN/PLAIN)
│   │   ├── Responses/             SmtpResult, SmtpDeliveryResult, UserExistsCodes
│   │   ├── SPF/SpfValidator.cs    SPF via DNS TXT lookups
│   │   ├── DMARC/                 DmarcValidator (+ Public Suffix List download), AlignmentMode, DmarcResult
│   │   ├── SMTPCodes.cs           static code→text table for built-in responses
│   │   └── ValidationResult.cs    enum: Pass/Softfail/Fail/UserAuthenticated/CheckDisabled…
│   ├── Misc/Base64.cs             tiny Base64 helper (AUTH payloads)
│   └── Properties/VersionInfo.cs  AssemblyVersion attributes (numeric-only "1.1.6.1")
├── SampleApp/                     demo console app (net7.0, not packable)
└── CSharp-SMTP-Server.Tests/      xUnit integration tests (net7.0)
```

**Library project facts:** multi-targets `netstandard2.1; net6.0; net7.0`, LangVersion 11,
`Nullable enable`, packs on every build (`GeneratePackageOnBuild=true`). Dependencies:
- **MimeKit 4.17.0** — MIME parsing of the message body
- **zabszk.DnsClient 1.0.1** — DNS queries for SPF/DMARC

## 4. Architecture & runtime flow

```
SMTPServer (public)
 └── Listener  (one per IP+port; dedicated OS thread runs TcpListener accept loop)
      └── ClientProcessor  (one per TCP connection; async read loop = the SMTP state machine)
           ├── ProcessResponse()   command dispatch switch
           │    ├── TransactionCommands.ProcessCommand / .ProcessData
           │    └── AuthenticationCommands.ProcessCommand / .ProcessData
           └── WriteText / WriteCode → wire output (CR/LF-terminated, UTF-8)
```

### Connection lifecycle (`ClientProcessor`)

1. **Init**: if the listener is a TLS port and a certificate exists, wrap in `SslStream` +
   `AuthenticateAsServerAsync`; otherwise send the greeting. Greeting first runs the optional
   `IMailFilter.IsConnectionAllowed(EndPoint)` — non-success → `550 4.7.1/5.7.1` and disconnect.
2. **Receive loop**: line-based (`StreamReader.ReadLineAsync`). Exceptions are logged; after 3 fails
   the connection is dropped. A per-connection `CancellationTokenSource` (exposed as
   `ConnectionToken`) cancels on dispose — this token is passed into your delivery handler.
3. **Command dispatch** (`ProcessResponse`):
   - `EHLO` → protocol v2; multi-line 250 advertising `AUTH LOGIN PLAIN` (if auth set),
     `STARTTLS` (if cert + not yet secure), `8BITMIME`. Resets any in-flight transaction.
   - `HELO` → protocol v1, no extensions.
   - `STARTTLS` → 220, upgrade stream to TLS mid-session (`ConnectionEncryption.StartTls`).
   - `AUTH` → requires EHLO/HELO first (else 503); LOGIN is a two-step Base64 exchange
     (`CaptureData` codes 2→3), PLAIN is one step (code 4) or inline credentials.
   - `MAIL FROM` / `RCPT TO` / `DATA` / `RSET` → require protocol ≥ v1, else `503`.
   - Unknown command → `502`; VRFY → `252` (stub); NOOP/HELP/QUIT are trivial.

### Transaction flow (`TransactionCommands`)

- **MAIL FROM**: parse `<addr>` (must contain exactly one `@`, a dot after it, non-empty).
  Then in order: `IMailFilter.IsAllowedSender` → SPF check (skipped for authenticated users;
  results cached per domain in `SpfResultsCache`; hard `Fail` → `554 5.7.23`) →
  `IsAllowedSenderSpfVerified`. Success creates the `MailTransaction`, replies `250 2.0.0`.
- **RCPT TO**: needs an open transaction; enforces `RecipientsLimit`; runs `CanDeliver` filter, then
  `IMailDelivery.DoesUserExist(address)` whose six `UserExistsCodes` map to RFC 3463 codes
  (5.1.1 bad mailbox, 5.1.2 bad system address, 5.1.4 ambiguous, 5.1.6 moved/no-forwarding,
  5.1.8 bad sender syntax; default = accept). Accepted recipients accumulate in `DeliverTo`.
- **DATA**: needs ≥1 recipient → `354`, then line capture until a lone `.`:
  1. Enforce `MessageCharactersLimit` (default 10,485,760 chars) → else `552 5.4.3`.
  2. Prepend `Received:` header; add `Authentication-Results:` for SPF when applicable.
  3. If DMARC enabled: reject >1 From header (`554`), run `DmarcValidator.ValidateTransaction`
     (hard Fail → `554 5.7.1`), record result in headers. Authenticated users skip the check.
  4. Run `IMailFilter.CanProcessTransaction(transaction)` — non-success → `554 4.7.1/5.7.1`.
  5. **Deliver (ACK-gated)**: clone the transaction, null out the processor's copy, then
     `await Server.DeliverMessage(clone, ConnectionToken)`:
     - handler returns `SmtpDeliveryResult` → its `(StatusCode, EnhancedStatus, Message)` is written verbatim;
     - handler throws → logged + `451 4.3.0` (sender retries).

### Key objects

- **`MailTransaction`** — what your handler receives: `From`, `FromDomain`, `DeliverTo[]`,
  `RawBody` (mutable string), lazy `ParsedMessage` (MimeKit, parsed once and cached),
  convenience accessors (`Subject`, `GetFrom/GetTo/GetCc/GetBcc`, `GetMessageBody()` = text or HTML body),
  `RemoteEndPoint`, `AuthenticatedUser`, `Encryption`, `SPFValidationResult`, `DMARCValidationResult`,
  `AddHeader(name, value)` (prepends to RawBody AND updates the parsed message).
- **`ServerOptions`** — `ServerName`, `RequireEncryptionForAuth` (**default true**), TLS 1.2 only by default,
  `MessageCharactersLimit` (0 = off), `RecipientsLimit` (0 = off), SPF/DMARC toggles (both default on;
  enabling either without a DNS endpoint throws in the setter), `DnsServerEndpoint` (defaults to
  Cloudflare `1.1.1.1:53` when SPF or DMARC is wanted), `PublicSuffixList` URL.
- **`SmtpDeliveryResult`** — sealed; factories `Ok(msg?)`, `TemporaryFailure(msg?)`, `PermanentFailure(msg?)`,
  and generic `Status(int code, string enhanced, string message)`. Constructor throws on CR/LF in
  message or enhanced status (response-splitting guard).

## 5. The fork's changes vs upstream (`git diff 2f7386e..HEAD`)

1. **ACK gating** (the point of the fork):
   - `IMailDelivery.EmailReceived(MailTransaction)` →
     `Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction, CancellationToken = default)`.
     **Breaking change for consumers** — you must rename and re-sign your implementation.
   - `SMTPServer.DeliverMessage` now returns `Task<SmtpDeliveryResult>` and forwards the connection token.
   - `ProcessData` awaits delivery before writing any response; exceptions → 451.
2. **Enhanced SMTP status codes (RFC 3463)**: new `SmtpDeliveryResult` type with explicit enhanced
   status; built-in responses throughout now carry codes (`5.7.1`, `4.7.1`, `5.7.23`, `5.7.8`, …);
   new `ClientProcessor.WriteCode(int, string)` overload that sanitizes CR/LF in handler-supplied text.
3. **Rebrand + hygiene**: package ID `Krugertech.CSharp-SMTP-Server`, authors "Łukasz Jurczyk, Llewellyn Kruger",
   version constants split into `VersionString` ("1.1.6-krugertech.1") and numeric-only
   `AssemblyVersionString` ("1.1.6.1"); LICENSE/metadata; a **DualMode fix** in `Listener`
   (only set when the address is IPv6 — upstream unconditionally set it, which breaks on some platforms);
   README rewritten around ACK gating incl. migration table.

### Migration cheat-sheet (upstream → this fork)

| Upstream | This fork |
|---|---|
| `Task EmailReceived(MailTransaction t)` | `Task<SmtpDeliveryResult> EmailReceivedAsync(MailTransaction t, CancellationToken ct = default)` |
| Return ignored; always 250 OK | Return value determines the SMTP response |
| Delivery runs in background | Server blocks the session until delivery completes |
| Handler exception silently swallowed | Exception → 451 (sender retries) |

## 6. Public API surface (what consumers implement/use)

- `SMTPServer(params, options, IMailDelivery, ILogger?, X509Certificate?)`, `.Start()`, `.Dispose()`,
  `.SetAuthLogin(IAuthLogin?)`, `.SetFilter(IMailFilter?)`, `.SetTLSCertificate(cert)`,
  `.AddListener(ip, port, tls, dualMode)` (can add listeners after Start).
- `ListeningParameters(IPAddress, ushort[] regularPorts, ushort[] tlsPorts, bool dualMode)` —
  TLS ports get implicit TLS; `dualMode` only works with `IPAddress.IPv6Any`.
- **IMailDelivery**: `EmailReceivedAsync`, `DoesUserExist`.
- **IAuthLogin**: `AuthPlain(authzId, authnId, password, ep, secure)`, `AuthLogin(login, password, ep, secure)` → bool.
- **IMailFilter** (all optional hooks): `IsConnectionAllowed(ep)`, `IsAllowedSender(source, ep, username?)`,
  `IsAllowedSenderSpfVerified(source, ep?, username?, spfResult)`, `CanDeliver(source, dest, authenticated, username?, ep?)`,
  `CanProcessTransaction(transaction)` — all return `SmtpResult` (Success / TemporaryFail / PermanentFail + optional FailMessage).
- **ILogger**: single `LogError(string)`.

## 7. Build & test

```bash
dotnet build CSharp-SMTP-Server.sln          # OK: 0 errors, 11 warnings (see below)
dotnet test CSharp-SMTP-Server.Tests/CSharp-SMTP-Server.Tests.csproj
```

**Environment gotcha:** the test project targets **net7.0**. If only a newer runtime is installed
(e.g. .NET 9 SDK on this machine), tests abort with "framework not found". Fix without touching the
csproj: `DOTNET_ROLL_FORWARD=Major dotnet test …` → all 227 tests pass (~8 s).

Test classes run **serially** (`xunit.runner.json`, `parallelizeTestCollections: false`) — the suite
binds loopback ports, and concurrently running classes race on port allocation.

Known build warnings (pre-existing, harmless): net7.0 EOL notice; CS8619 nullability mismatches in
`MailTransaction.GetTo/GetCc/GetBcc` (`IEnumerable<string?>` vs `IEnumerable<string>` — MimeKit's
`Name` is nullable).

### Tests

Integration-style test files share one pattern: allocate a free loopback port, start an actual
`SMTPServer`, speak raw SMTP over TCP with 10 s read timeouts — all through the shared
`SmtpSession` helper (connect/send/read-line/multi-line-response/RST-abort). Phase 1 (per
`TEST_PLAN.md`) adds pure unit tests (no I/O) plus one socket-pair harness for the wire-output
helpers; the test project gains `InternalsVisibleTo` access to internal helpers (`ProcessAddress`,
`Base64`, `WriteCode`).

**`AckGatingTests.cs`** (6 facts):
1. DATA → 250 only after handler completes; handler called exactly once.
2. With a gated `SlowDelivery` (TCS pause), **no** response arrives while the handler is running —
   proves no fire-and-forget.
3. `TemporaryFailure()` → client sees 451.
4. Handler throws → client sees 451.
5. Exactly-once delivery per transaction.
6. Second fire-and-forget race check (300 ms window).

**`AuthLoginInitialResponseTests.cs`** (4 facts): bare two-step AUTH LOGIN still works; inline
username (`AUTH LOGIN <b64>`) goes straight to the password prompt and succeeds (IIS scenario);
wrong password → 535; invalid base64 initial response falls back to the username prompt.

**`EhloBracketedIpv6Tests.cs`** (3 facts): `EHLO [IPv6:…]` is accepted and the session stays usable;
plain-hostname EHLO regression guard; `MAIL FROM:<…@[IPv6:…]>` still parses as a MAIL FROM command.

**Phase 1 unit tests** (107, see `TEST_PLAN.md` §3/§9 for the full case list):
`SmtpDeliveryResultTests` (16), `ServerOptionsTests` (9), `MailTransactionTests` (19 — pins bugs B1–B4),
`CheckCidrTests` (16), `DmarcOrganizationalDomainTests` (7, suffix list served from a local HTTP
helper — no internet), `Base64Tests` (10), `ProcessAddressTests` (15), `WireFormattingTests` (6,
direct `WriteCode` tests on a socket pair incl. CR/LF sanitization), `ValueTypesTests` (6),
`VersionConsistencyTests` (2).

**`ConcurrentGreetingTests.cs`** (1 fact): two concurrent clients both receive their 220 greeting while
the first stays idle — regression guard for the accept-thread-blocking fix (`294adbe`).

**Phase 2 protocol matrix** (86, see `TEST_PLAN.md` §4 for the full case list) — exact wire assertions:
`GreetingAndFilterTests` (7), `EhloHeloTests` (9), `CommandSequencingTests` (13), `MailFromTests` (13),
`RcptToTests` (13), `DataAndMessageTests` (12), `AuthProtocolTests` (19).

**Phase 2 robustness & ACK-gating additions** (21): `AckGatingAdditionsTests` (6 — incl. the Q8 pin that
the delivery token does not fire on client disconnect mid-delivery, and a deterministic parallel-handler
no-cross-talk check) and `LifecycleAndRobustnessTests` (15 — ctor edge cases, AddListener-after-Start,
multi-listener, Dispose RST semantics, port-in-use tolerance, dual-mode guards, binary-garbage / 1 MB-line
survival, abrupt disconnect at four phases, and the `ConcurrencyStress` regression guard for the
`ClientProcessors` thread-safety fix).

## 8. Known issues & gotchas

- **Confirmed upstream bugs pinned by tests (B1–B4)** — see `TEST_PLAN.md` §2. Most important:
  `MailTransaction.GetFrom/GetTo/GetCc/GetBcc` return MimeKit *display names*, not addresses, so DMARC
  validation is effectively inert for ordinary mail; `AddHeader` before first parse duplicates the header
  in `ParsedMessage`; `Clone()` drops `DMARCValidationResult` and shares the `DeliverTo` list.
- **Fixed during Phase 1 testing:** `ClientProcessor` ctor blocked the listener accept thread when the
  greeting write completed synchronously (one concurrent client at a time on Windows) — see `294adbe`.
- **Fixed during Phase 2 testing:** the long-documented `Listener.ClientProcessors` race was real —
  unsynchronised Add/Remove corrupted the list and `Dispose()` threw NullReferenceException under load;
  fixed with a shared lock + snapshot-in-Dispose, guarded by
  `ConcurrencyStress_ParallelSessions_AllDeliveriesSucceed` (deterministic repro without the fix) — see `df4636e`.
- **Q7 — most responses carry no table text:** every two-arg `WriteCode(code, enhanced)` call site binds to
  the `(int, string)` sanitizer overload (no implicit int→ushort conversion), so NOOP → `250 2.0.0`,
  HELP → `214 2.0.0`, QUIT → `221 2.0.0` etc. have no message body; only single-arg calls get the
  `SMTPCodes` table text. RFC-wise legal, but surprising — pinned by exact-wire assertions.
- **Q8 — delivery token does not fire on client disconnect:** while the handler is in flight nothing polls
  the socket (the receive loop is parked inside `DeliverMessage`), so a client RST cannot cancel the
  delivery; the token only fires after the handler returns, when the response write fails. If delivery
  cancellation ever matters, this needs a fix — pinned by `AckGatingAdditionsTests`.
- **Delivery runs inside the SMTP session**: a slow handler holds the connection open for as long as it
  takes — by design, but size your timeouts/worker pools accordingly. The `CancellationToken` is tied to
  the client connection; if the client disconnects mid-delivery the token fires (handlers should honor it).
- **SPF/DMARC only apply to unauthenticated senders** and require a DNS endpoint; default resolver is
  Cloudflare 1.1.1.1:53. DMARC additionally downloads Mozilla's Public Suffix List from GitHub on first
  use (static, cached in-process).
- `RequireEncryptionForAuth` defaults to **true** — AUTH over plaintext gets `538`. SampleApp sets it false for demo purposes.
- `DoesUserExist` is called per RCPT TO; recipient limit default 50; message size limit counts characters (not bytes), default ~10 MB.
- `MailTransaction.RawBody` is mutable and headers are prepended to it — the object handed to your handler is a **clone** of the processor's transaction, so mutating it doesn't affect server state.
- Version constants: NuGet/package version lives in the csproj (`PackageVersion 1.1.6-krugertech.3`);
  `SMTPServer.VersionString` still says `-krugertech.1` (informational only — bump both if you release).

## 9. Upstream sync record

Upstream (`https://github.com/zabszk/CSharp-SMTP-Server`, remote `upstream`) was audited on
2026-07: everything after tag `1.1.6` (master commits, open PRs #17/#19, branches `dispose-log`,
`dkim`, dependabot) plus all open issues (#11, #15, #16, #18). Verdict per item:

| Upstream change | Verdict | Where it landed here |
|---|---|---|
| `0dadf2d` — handle unhandled exceptions (issue #16): `IOException` on client disconnect breaks the receive loop cleanly instead of counting as a failure; `WriteAsync` now takes the connection cancellation token; better write-error logging | ✅ **Included** — cherry-picked verbatim, original authorship preserved. The write-cancellation matters extra for ACK gating: if the client drops while delivery is in flight, response writes fail fast instead of hanging | commit `8fa02c8` |
| PR #17 — AUTH LOGIN with initial response (IIS SMTP relay sends `AUTH LOGIN <b64-user>` then only the password; previously the password was misread as a second username → auth always failed) | ✅ **Included, adapted** — implemented per the maintainer's suggested design in the PR discussion: valid inline base64 username ⇒ answer with the *password* prompt (`334 UGFzc3dvcmQ6`) and capture stage 3 directly; invalid/absent initial response keeps the standard two-step flow. **Excluded** from that PR (deliberately): breaking `ILogger` additions (`LogInfo`/`LogVerbose`), verbose logging of auth payloads (security anti-pattern), version bumps, sample-app edits | commit `7d0d50f` + `AuthLoginInitialResponseTests.cs` |
| Issue #18 — `EHLO [IPv6:fe80::…]` misparsed (parser split at the first `:` inside brackets → 503 "EHLO/HELO first"; reported against Thunderbird) | ✅ **Fixed here** — no fix exists on upstream master. The colon separator is now only recognized outside square brackets (`ClientProcessor.ProcessResponse`); `MAIL FROM:`/`RCPT TO:` parsing (incl. bracketed IP literals in addresses) unchanged | commit `274069a` + `EhloBracketedIpv6Tests.cs` |
| MimeKit bumps 4.3.0→4.7.1 (`51db717`) and →4.15.1 (PR #19 / dependabot branch) | ⏭️ Skipped — this fork already pins **MimeKit 4.17.0** (newer than both) |
| `49e6a64` — merge `AuthPlain`+`AuthLogin` into single `CheckAuthCredentials` | ⏭️ Skipped — breaking API redesign, not a bug fix; this fork already carries one breaking change (`EmailReceivedAsync`) and keeps the two-method interface so LOGIN/PLAIN can be handled differently |
| Branch `dispose-log` (stack-trace logging in Listener dispose paths) | ⏭️ Skipped — debug instrumentation only; would spam logs on every normal shutdown |
| Branch `dkim` (~1000 lines: DKIM verification + ServerOptions rework into `Config/`) | ⏭️ Skipped — unfinished feature, not a fix (tracks upstream issue #15) |
| Issue #11 — namespace collision between `zabszk.DnsClient` and Couchbase's `DnsClient` package | ⏭️ Not actionable in this repo — would require forking/renaming the separate DnsClient dependency; noted for future consideration |

Re-audit command: `git fetch upstream && git log --oneline 1.1.6..upstream/master`
plus a check of open PRs/issues on GitHub.

## 10. RFC compliance claims (from README)

RFC 822, 1869, 2554, 3463 (enhanced status codes), 4616 (PLAIN SASL), 4954, 5321, 7208 (SPF),
7372, and **partially** RFC 7489 (DMARC).
