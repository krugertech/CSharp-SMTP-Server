# SUMMARY — Krugertech CSharp-SMTP-Server fork (v1.1.6)

**Handoff / resume document.** If you're picking this repo up fresh, read this first, then:
- `ARCHITECTURE.md` — deep understanding of the codebase + upstream sync record (§9)
- `TEST_PLAN.md` — full test plan with per-phase status and all pinned quirks (Q1–Q10) / bugs (B1–B5)

## 1. What this repo is

Fork of [zabszk/CSharp-SMTP-Server](https://github.com/zabszk/CSharp-SMTP-Server) v1.1.6
(`origin` = krugertech fork, `upstream` = zabszk original). The fork's core purpose: **ACK-gated
DATA delivery** — `IMailDelivery.EmailReceivedAsync(MailTransaction, CancellationToken)` returns a
`SmtpDeliveryResult` (Ok→250/2.0.0, TemporaryFailure→451/4.3.0, PermanentFailure→554/5.7.1,
exception→451) and the SMTP response is only sent after the handler returns. Delivery runs inside
the SMTP session by design.

## 2. Current state

- Branch: **`dev`**, pushed. Tier 1 review fixes (B1, B3, B4, Q12(b), R11, R6) plus a Codex adversarial
  follow-up (C1, C2 below) are new commits on top; version **2.0.0-krugertech.1**.
- Build: 0 errors (pre-existing harmless warnings: net7.0 EOL notice, CS8619 in MailTransaction).
- Tests: **316/316 green**, ~11 s per run, stable across repeated runs. Working tree clean.

### Commit stack (newest first)

| Commit | What |
|---|---|
| `bb880f0` | **Phase 4 SPF/DMARC tests** (§6/§7): `DnsStub` UDP DNS fixture + 56 new; pins Q11–Q13 and B1 end-to-end |
| `7eda61d` | Docs: SUMMARY.md handoff document |
| `715b075` | Docs: Phase 3 completion, B5 fix, Q9/Q10 findings |
| `7cb9fd9` | **Fix B5** (implicit-TLS handshake crash) + Phase 3 TLS tests (§4.8): 11 new |
| `0c06e45` | Docs: Phase 2 completion, Q7/Q8, ClientProcessors fix |
| `df4636e` | **Fix** `Listener.ClientProcessors` race + §5/§8 tests (21 new) |
| `4a4cb06` | Phase 2 protocol matrix (§4.1–4.7): 86 new |
| `994eb07` | Shared `SmtpSession` helper for integration tests (§1.2) |
| `d244539` | Docs: Phase 1 completion + accept-thread fix |
| `e8a241f` | Phase 1 unit tests (§3+§9): 107 new (pins B1–B4) |
| `294adbe` | **Fix** ClientProcessor ctor blocking the listener accept thread |
| `91b5811` | TEST_PLAN.md (~190-case plan) |
| `8250a86` | ARCHITECTURE.md (repo understanding + upstream sync record) |
| `274069a` | Fix EHLO/HELO parsing of bracketed IPv6 literals (upstream issue #18, no upstream fix exists) |
| `7d0d50f` | AUTH LOGIN initial-response support (RFC 4954 / IIS relay; adapted from PR #17 discussion design) |
| `8fa02c8` | Cherry-pick of upstream `0dadf2d` verbatim (issue #16: unhandled exceptions in receive loop) |

Fork diff vs baseline: `git diff 2f7386e..HEAD` (`2f7386e` = squashed import of v1.1.6, not an upstream commit).
Re-audit upstream with: `git fetch upstream && git log --oneline 1.1.6..upstream/master` + check open PRs/issues on GitHub.

## 3. Build & test (exact commands)

```powershell
dotnet build CSharp-SMTP-Server.sln
# Tests target net7.0; machine has .NET 9 SDK — roll forward is REQUIRED:
$env:DOTNET_ROLL_FORWARD="Major"; dotnet test CSharp-SMTP-Server.Tests/CSharp-SMTP-Server.Tests.csproj --no-build
```

Gotchas:
- Without `DOTNET_ROLL_FORWARD=Major`, tests abort with "framework not found".
- Test classes run **serially** (`xunit.runner.json`: `parallelizeTestCollections: false`). Measured,
  not assumed: with `maxParallelThreads: 4`, 6 of 8 runs fail, always on
  `DmarcOrganizationalDomainTests.MultiLevelPublicSuffix_WalksUpUntilNonSuffix` — `DmarcValidator`
  keeps the public-suffix list in process-wide statics that `ForceRefreshList` mutates mid-run. See
  §8 item 2 before re-enabling. (Port allocation, R8, did *not* surface in those runs, but is still
  theoretically racy.)
- Every test read is bounded by a 10 s timeout (in `SmtpSession`), so regressions fail instead of hanging.

## 4. Bugs found & fixed in this fork (each has regression tests)

| Bug | Symptom | Fix commit | Guard |
|---|---|---|---|
| Accept-thread blocking | Ctor ran `Init()` inline; when the greeting write completed synchronously (typical on Windows), the accept thread parked in `Receive()` → **one concurrent client at a time** | `294adbe` (`_ = Task.Run(Init)`) | `ConcurrentGreetingTests` |
| `Listener.ClientProcessors` race | Unsynchronized `List<>` mutated from 3 contexts; concurrent Add/Remove corrupted internals → NRE in `Dispose()` under load (deterministic repro, 3/3 runs pre-fix) | `df4636e` (lock + snapshot-in-Dispose) | `ConcurrencyStress_ParallelSessions_AllDeliveriesSucceed` |
| **B5** implicit-TLS crash | Failed/aborted handshake on a TLS port threw inside `async void Init()` → **whole process died** (silent disconnect, cert rejection, or plaintext probe — any scanner touching the port) | `7cb9fd9` (try/catch: log + drop connection) | 3 regression tests in `TlsStartTlsTests` (pre-fix: test run aborts outright) |
| **R11** shutdown escape | Gap left by `df4636e`: a connection accepted but not yet registered when `Dispose()` snapshotted the list was added *afterwards*, into a list nobody reads again → never disposed, still serving a client (and still using the TLS certificate `SMTPServer.Dispose()` then disposes) | `AddProcessor` returns `bool`, refusing registration when `_dispose` is set; flag-set and snapshot now share one `_processorsLock` critical section; accept loop disposes a refused processor | `ConnectionAcceptedDuringShutdown_IsRefusedAndDisposed_R11` |

**R7 (fixed)** — `Listener` used a plain non-volatile `bool` for shutdown, read by the accept loop
without synchronisation, and `Dispose()` returned without confirming the accept thread had exited —
while `SMTPServer.Dispose()` disposes the TLS certificate immediately afterwards. Termination is now a
`CancellationTokenSource`, and `Dispose()` waits (bounded, 5 s) on a `ManualResetEventSlim` the loop
signals in a `finally`. Guarded by `Dispose_JoinsAcceptThread_BeforeReturning_R7` (asserts
`thread.IsAlive == false` synchronously after `Dispose()` returns — no polling),
`Dispose_IsIdempotent_R7` and `Dispose_NeverStartedListener_ReturnsImmediately_R7`. The last two exist
because owning disposable primitives made `Dispose()` non-idempotent — a real bug this change
introduced and these tests caught, since `SMTPServer.Dispose()` plus a caller's `using` both reach it.

**R6 (fixed)** — a throwing `IMailFilter.IsConnectionAllowed` used to crash the process via the same
`async void Init()` path as B5. It is now caught in *two* places, both needed: `Init()` (plaintext
greeting) and the pre-greeting section of `Receive()` (TLS greeting — on a secure connection `Greet()`
runs there, and `Receive()` is likewise started fire-and-forget). Guarding only `Init()` leaves the TLS
path crashable; verified by disabling the second guard and watching
`ThrowingFilter_ImplicitTlsConnection_OnlyThatConnectionDropped_R6` fail. Both guards have regression
tests in `TlsStartTlsTests` (pre-fix: the test run aborts outright, same signature as B5).

## 5. Confirmed upstream bugs (B1–B4)

Details + repro evidence in TEST_PLAN.md §2; disposition per bug decided in REVIEW.md Part 3.

### Fixed

- **B3** *(fixed)*: `Clone()` dropped `DMARCValidationResult`, so every delivered transaction reported
  `None` regardless of the real outcome — `CheckDisabled` was lost too. Now copied in the `Clone()`
  initializer. Guarded by `MailTransactionTests.Clone_CarriesDmarcValidationResult` (Pass/Fail/CheckDisabled)
  and `AckGatingAdditionsTests.DeliveryClone_CarriesDmarcResult_AndHandlerMutationIsSafe` end-to-end.
- **B4** *(fixed)*: `Clone()` shared the `DeliverTo` `List<string>` by reference, so a delivery handler
  filtering or deduplicating recipients mutated the server-side transaction too — a live risk now that
  delivery runs inside the session (ACK-gating). Now `new List<string>(DeliverTo)`. Guarded by
  `MailTransactionTests.Clone_CopiesDeliverToList_MutationIsIsolated` and the same end-to-end test.
- **B1** *(fixed — breaking, see `CHANGELOG.md`)*: `GetFrom`/`GetTo`/`GetCc`/`GetBcc` returned MimeKit
  *display names* rather than addresses (`From: sender@example.com` → `""`), so DMARC could never see
  the header-From domain and `ValidateDMARC: true` enforced nothing.

  **The fix needed two parts, not one.** Returning `.Address` (and `.Mailboxes` to flatten groups) is
  necessary but insufficient: both `GetFrom` consumers fed the result to `ProcessAddress`, which parses
  SMTP *command* arguments and requires the RFC 5321 angle-bracket form. A bare address returns null
  there, leaving DMARC inert for a different reason. Header domains now go through a new
  `TransactionCommands.GetAddressDomain` helper; `ProcessAddress` is untouched, so MAIL FROM / RCPT TO
  envelope parsing still requires `<…>` as the RFC demands. Verified load-bearing: reverting just the
  helper fails 11 DMARC tests.

  Display names remain reachable via the new `GetFromName`. Version bumped to **2.0.0** — major, not
  minor, because the change is *silent*: consumers still compile but behave differently, so a minor
  bump would carry it through a routine update. This also cleared the pre-existing `VersionString`
  drift (R5). The single-identity gate that protects DMARC is C2 in §8.

### Not yet fixed (decision pending)

- **B2**: `AddHeader` before first parse duplicates the header in `ParsedMessage`.

Pinned by `MailTransactionTests`. Fixing this changes observable behavior.

## 6. Pinned quirks Q1–Q10 (current behavior, asserted exactly — don't "fix" without review)

| # | Quirk |
|---|---|
| Q1 | No dot-stuffing support in DATA |
| Q2 | VRFY returns success code with enhanced status `5.5.1` (`252 5.5.1`) |
| Q3 | Authenticated `Received:` header omits `from <ip>` |
| Q4 | STARTTLS accepted as the very first command (before EHLO) |
| Q5 | RCPT syntax error → bare `501` with no enhanced status |
| Q6 | Size-limit counter excludes CRLF; exactly-at-limit is accepted (`>=`) |
| Q7 | **Overload accident**: every two-arg `WriteCode(code, "x.y.z")` call site binds to the `(int,string)` sanitizer overload (no implicit int→ushort conversion) → most responses carry no table text (NOOP → `250 2.0.0`, not `250 OK`). Only single-arg calls get `SMTPCodes` table text |
| Q8 | Delivery `CancellationToken` does NOT fire on client disconnect mid-delivery (nothing polls the socket while parked in `DeliverMessage`) |
| Q9 | No per-line responses during DATA — only the final response after `<CRLF>.<CRLF>`; clients waiting for a per-line ACK hang (RFC 5321 doesn't require them) |
| Q10 | **Windows**: SChannel cannot use `CertificateRequest.CreateSelfSigned()` certs ("platform does not support ephemeral keys"); PFX round-trip re-import fixes it. Affects real library users generating certs in memory on Windows |
| Q11 | zabszk.DnsClient silently drops multi-string TXT responses (only single character-strings are parsed) → split real-world SPF/DMARC records look like "no record" (SPF `None`; DMARC fallback/`None`) |
| Q12 | SPF DNS error handling deviates from RFC 7208. **(b) FIXED**: a failed `a`/`mx` address lookup now returns Temperror instead of the mechanism's qualifier — it previously failed *open* (a bare `a` returned **Pass** on DNS failure). Still open: (a) top-level NXDOMAIN → Temperror (should be none); (c) redirect to nonexistent domain → Temperror (should be permerror) |
| Q13 | SPF `redirect=` is evaluated positionally and short-circuits later mechanisms; RFC 7208 §6.1/§4.7 only consults it after all mechanisms have failed |

## 7. Test suite layout (316 tests)

- **Phase 1 — pure unit** (107): `SmtpDeliveryResultTests` (16), `ServerOptionsTests` (9),
  `MailTransactionTests` (19, pins B1–B4), `CheckCidrTests` (16), `DmarcOrganizationalDomainTests` (7,
  local suffix-list HTTP helper), `Base64Tests` (10), `ProcessAddressTests` (15), `WireFormattingTests` (6,
  socket-pair harness for internal `WriteCode`), `ValueTypesTests` (6), `VersionConsistencyTests` (2).
- **Phase 2 — protocol matrix + robustness** (107): `GreetingAndFilterTests` (7), `EhloHeloTests` (9),
  `CommandSequencingTests` (13), `MailFromTests` (13), `RcptToTests` (13), `DataAndMessageTests` (12),
  `AuthProtocolTests` (19), `AckGatingAdditionsTests` (6, incl. Q8 pin), `LifecycleAndRobustnessTests` (15).
- **Phase 3 — TLS** (11): `TlsStartTlsTests`.
- **Phase 4 — SPF/DMARC**: `SpfValidatorTests` (28), `DmarcValidatorTests` (15 — rewritten for B1: every
  case used to smuggle the header domain through a quoted display name, so none exercised the path
  normal mail takes; they now use ordinary From headers, and the old inertness pin asserts real
  `_dmarc.` lookups plus a reachable Fail), `SpfDmarcIntegrationTests` (4 — SPF Fail 554 at MAIL FROM,
  DMARC Fail 554 at DATA, AR headers).
  Infrastructure: `DnsStub` loopback UDP DNS responder (§1.5) + reused `LocalHttpServer` suffix-list
  fixture; test project LangVersion bumped to 12 (test-only).
- **Upstream-fix regression tests**: `AckGatingTests` (6), `AuthLoginInitialResponseTests` (4),
  `EhloBracketedIpv6Tests` (3), `ConcurrentGreetingTests` (1).

Shared infrastructure: `SmtpSession` (raw-TCP client with timeouts, multi-line reads, RST abort,
`UpgradeTlsAsync` for TLS), `TestServers`/`TestPorts` factories, `RecordingDelivery`/`ConfigurableFilter`/
`RecordingLogger` fakes, `NoopDelivery`, `LocalHttpServer`, `TlsTestCerts`. Test project has
`InternalsVisibleTo` access to library internals.

## 8. Remaining work

All four test-plan phases are complete. **316 tests** after the REVIEW.md Tier 1 fixes (B1, B3, B4,
Q12(b), R11, R6) and the Codex adversarial follow-up — see `CHANGELOG.md` for the 2.0.0 release notes.
Left:

1. **Remaining REVIEW.md items**, none started: **B2** (`AddHeader` duplicates the header before first
   parse), **R1** (the `WriteCode(int,string)` overload accident — ~53 assertions across 10 files),
   **Q1** (no dot-stuffing in DATA — data-integrity, arguably P1), **Q3**, **Q8**, **Q11** (multi-string
   TXT records, needs a dependency decision), **Q12(a)/(c)**, **Q13**, plus R2/R4/R8/R9 (R7 is now done — see §4).
2. **Parallel test classes — blocked on ONE remaining item, measured.** Goal: enable
   `parallelizeTestCollections` so load/parallelism tests can be built. R7 (done) was a prerequisite,
   not the whole job. Measured with `maxParallelThreads: 4`, **8 consecutive runs: 6 failed**, always
   the same culprit —
   `DmarcOrganizationalDomainTests.MultiLevelPublicSuffix_WalksUpUntilNonSuffix`.

   **Root cause:** `DmarcValidator` holds the public-suffix list in **process-wide statics**
   (`PublicSuffixes`, `_publicSuffixesLoaded`), and `ForceRefreshList_SwitchesTheActiveSuffixSet`
   swaps that set at runtime. Any DMARC test running concurrently reads a half-swapped list. This is a
   *library* design issue, not a test one: the suffix list is per-process, so two `SMTPServer`
   instances in one process cannot use different lists.

   **Options:** (a) make the suffix set per-`DmarcValidator` instance — the honest fix, and a public
   behavior change worth noting; (b) keep the statics and put the mutating tests in one xUnit
   collection, which restores safety without fixing the library; (c) leave `ForceRefreshList` alone and
   have the test assert against a locally-constructed validator instead of the shared fixture.

   Note the port-allocation TOCTOU (R8) did **not** surface in these runs, but is still theoretically
   live: `TestPorts.Allocate()` binds port 0, reads the assignment, then releases before the caller
   rebinds. Fix it opportunistically when doing the above; a load-test suite will hit it far harder
   than the current tests do.
3. Anything from the upstream re-audit.

### Codex adversarial review — dispositions

A Codex adversarial pass over the Tier 1 commits returned five findings. Two were fixed (below), one is
item 2 above, one is a version decision now taken (2.0.0), and one **could not be reproduced**:

- **C1 (fixed)** — SPF NXDOMAIN in `a`/`mx` returned `Temperror` instead of falling through to a
  terminal `-all`. Introduced by the Q12(b) fix: the pre-existing `CheckAddressMatch` collapses every
  non-`NoError` RCODE into `Temperror`, and Q12(b) began propagating that faithfully. Pre-Q12(b) this
  case returned `Fail` by accident (the bogus "match" carried the `-` qualifier). Now `NameError`
  (NXDOMAIN) is a no-match on both the address lookup and the MX lookup. 4 regression tests.
- **C2 (fixed)** — a single group From header held multiple identities while passing the
  `From.Count > 1` gate; DMARC authenticated only the first. Gate now counts `.Mailboxes`. Note the
  pre-B1 code had the same hole, but the B1 work added a test that *blessed* it — that test now
  documents why `GetFrom` alone must not be the authentication seam.
- **NOT REPRODUCED — processor registered after disposal.** Codex argued that `ClientProcessor` starts
  `Init()` from its constructor, so a fast-failing filter could `Dispose()` (→ `RemoveProcessor`, a
  no-op while unregistered) before `Listener.AddProcessor` runs, permanently inserting a dead object
  and growing the list without bound. The ordering claim is accurate. The consequence was not
  observable: **5000 connections** against instantly-rejecting and synchronously-throwing filters, with
  the list sampled continuously, gave a final count of **0** every time (peak 100 = genuine in-flight
  connections). The `Task.Run(Init)` thread-pool hop reliably loses the race to the immediately
  following `AddProcessor`. Recorded as a latent ordering smell, not a demonstrated leak — if the
  constructor ever starts work inline again, re-test this first. The clean fix if it ever bites:
  two-phase init (construct, register, *then* start `Init`), which also subsumes the R11 ordering.

## 9. Working conventions used throughout (keep them)

- **Pin first, fix second**: suspected bugs get empirical verification + exact-behavior tests before any
  change; fixes are separate commits with their own regression test so history stays bisectable.
- Commit shape: library fix + its regression test together → feature/plan tests → docs-only commit last.
- Tests match repo style: real-server raw-TCP integration for protocol behavior, self-contained files,
  exact wire assertions (Q7 means most two-arg responses have NO table text — assert what's actually sent).
- Verify suspicious library/MimeKit behavior empirically in a scratch project before asserting it anywhere.
- Don't log auth payloads; keep fork diff scope minimal where possible.

## 10. Quick orientation for a fresh session

```bash
git log --oneline -15          # commit stack (see §2)
cat ARCHITECTURE.md            # how the code works + upstream sync record
cat TEST_PLAN.md               # what's tested, what's pinned, what's left (§11 build order)
$env:DOTNET_ROLL_FORWARD="Major"; dotnet test CSharp-SMTP-Server.Tests/CSharp-SMTP-Server.Tests.csproj --no-build   # expect 316/316 in ~11 s
```

Key source files: `CSharp-SMTP-Server/Networking/{ClientProcessor,Listener}.cs` (connection lifecycle —
all three fixed bugs live here), `Protocol/Commands/{TransactionCommands,AuthenticationCommands}.cs`,
`MailTransaction.cs` (B1–B4), `SMTPServer.cs` (public API surface).
