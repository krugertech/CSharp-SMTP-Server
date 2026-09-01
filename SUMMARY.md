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
- Tests: **343/343 green**, ~15 s per run, stable across repeated runs.
- **➜ Next actions: [`immediate-todo.md`](immediate-todo.md)** — O365 journaling config, two defects
  (null sender, memory amplification), and the streaming-DATA fix. Read that first.

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

## 2a. Deployment model (drives several priority calls below)

**One server instance per Kubernetes pod**, each with its own IP, all on the same port. Scaling is
horizontal across pods — never two instances in one process or on one host.

Consequences worth knowing before judging any finding:
- **Per-process static state is acceptable**, because the process is the unit of deployment. This is
  why the `DmarcValidator` public-suffix statics (§8 item 2) are deprioritised rather than treated as
  a defect. Pods share nothing, so horizontal scaling is close to linear.
- **Shutdown correctness matters more than it would for a long-lived single server.** Pods terminate
  routinely — scale-down, rolling deploys, node drains, evictions — so the shutdown paths fixed by R11
  and R7 are exercised constantly rather than rarely. Graceful *drain* is still open (§8 item 3).
- **Throughput is bounded by the delivery handler, not this library.** Delivery is ACK-gated and
  awaited inside the session, so per-pod throughput is roughly
  `concurrent_sessions / (handler_latency + round_trips)`. If SPF/DMARC are enabled, cold-cache DNS
  lookups on the session thread will likely dominate everything else — consider a resolver cache
  before optimising anything in this codebase. **No benchmark has been run**; these are structural
  expectations, not measurements.

## 3. Build & test (exact commands)

```powershell
dotnet build CSharp-SMTP-Server.sln
# Tests target net7.0; machine has .NET 9 SDK — roll forward is REQUIRED:
$env:DOTNET_ROLL_FORWARD="Major"; dotnet test CSharp-SMTP-Server.Tests/CSharp-SMTP-Server.Tests.csproj --no-build
```

Gotchas:
- Without `DOTNET_ROLL_FORWARD=Major`, tests abort with "framework not found".
- Test classes run **serially** (`xunit.runner.json`: `parallelizeTestCollections: false`), and that is
  the intended steady state — see §2a. Measured, not assumed: with `maxParallelThreads: 4`, 6 of 8 runs
  fail, always on `DmarcOrganizationalDomainTests.MultiLevelPublicSuffix_WalksUpUntilNonSuffix`
  (process-wide suffix-list statics mutated mid-run). Don't flip it without reading §8 item 2. ~11 s
  serial is cheap; flaky is not.
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

## 7. Test suite layout (343 tests)

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

All four test-plan phases are complete. **343 tests** after the REVIEW.md Tier 1 fixes (B1, B3, B4,
Q12(b), R11, R6) and the Codex adversarial follow-up — see `CHANGELOG.md` for the 2.0.0 release notes.
Left:

1. **Remaining REVIEW.md items**, none started: **B2** (`AddHeader` duplicates the header before first
   parse), **R1** (the `WriteCode(int,string)` overload accident — ~53 assertions across 10 files),
   **Q1** (no dot-stuffing in DATA — data-integrity, arguably P1), **Q3**, **Q8**, **Q11** (multi-string
   TXT records, needs a dependency decision), **Q12(a)/(c)**, **Q13**, plus R2/R4/R8/R9 (R7 is now done — see §4).
2. **Parallel test classes — DEPRIORITISED (deployment decision, 2026-09-01).** Production scales by
   running one server per Kubernetes pod, each with its own IP and the same port — never two instances
   in one process or on one host. The parallelism work was motivated by an in-process multi-instance
   load harness, which is no longer the plan, so this is now optional cleanup rather than a
   prerequisite. `xunit.runner.json` stays serial.

   Recorded for whoever revisits it. R7 (done) was a prerequisite but not sufficient: with
   `maxParallelThreads: 4`, **8 consecutive runs, 6 failed**, always
   `DmarcOrganizationalDomainTests.MultiLevelPublicSuffix_WalksUpUntilNonSuffix`. `DmarcValidator`
   holds the public-suffix list in **process-wide statics** (`PublicSuffixes`, `_publicSuffixesLoaded`)
   and `ForceRefreshList_SwitchesTheActiveSuffixSet` swaps that set at runtime, so concurrent DMARC
   tests read a half-swapped list. Options: (a) make the suffix set per-`DmarcValidator` instance —
   the honest fix, a public behavior change; (b) put the mutating tests in one xUnit collection;
   (c) have that test build its own validator instead of using the shared fixture.

   **Under the pod-per-instance model the statics are no longer a defect** — the process *is* the unit
   of deployment, so per-process suffix state is the right granularity. Only revisit if something ever
   needs two `SMTPServer` instances with different suffix lists in one process.

   R8 (port-allocation TOCTOU in `TestPorts.Allocate()` — binds port 0, reads the assignment, releases
   before the caller rebinds) did **not** surface in those runs and stays theoretical while the suite
   is serial.
3. **Graceful drain on SIGTERM — the open question that actually matters in production.** Not
   implemented, and worth a decision before heavy production traffic. `Dispose()` now shuts down
   *correctly* (R11: connections accepted during shutdown are refused and disposed; R7: the accept
   thread is joined before the TLS certificate is disposed), but it **terminates** open sessions rather
   than draining them. Because delivery is ACK-gated and awaited inside the session, a pod killed
   mid-delivery can leave the handler having committed a message while the sender never received its
   250 — the sender then retries, producing a **duplicate**. Two things to settle:
   - Confirm the host actually calls `SMTPServer.Dispose()` on `SIGTERM` rather than letting the
     process exit. Without that, none of the shutdown correctness above runs at all.
   - Decide whether idempotent delivery is enough, or whether to implement real drain (stop accepting,
     let in-flight sessions finish, then exit) inside the K8s grace period (default 30 s).
4. Anything from the upstream re-audit.

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
$env:DOTNET_ROLL_FORWARD="Major"; dotnet test CSharp-SMTP-Server.Tests/CSharp-SMTP-Server.Tests.csproj --no-build   # expect 343/343 in ~15 s
```

Key source files: `CSharp-SMTP-Server/Networking/{ClientProcessor,Listener}.cs` (connection lifecycle —
all three fixed bugs live here), `Protocol/Commands/{TransactionCommands,AuthenticationCommands}.cs`,
`MailTransaction.cs` (B1–B4), `SMTPServer.cs` (public API surface).

## 11. Load & integrity harness (`CSharp-SMTP-Server.Tests/Load/`)

Added 2026-09-01. Answers three questions: does the server stay reliable under concurrency, does
message content survive transport intact, and how does throughput change between commits.

**Two tiers.** The fast tier (4 tests + the Q1 pin) runs on every `dotnet test` — modest scale,
deterministic, ~2 s. The heavy tier (concurrency ladder to 500, sustained 1000-message run,
max-receive-rate burst) is opt-in:

```powershell
$env:SMTP_LOADTEST="1"; dotnet test CSharp-SMTP-Server.Tests/CSharp-SMTP-Server.Tests.csproj --no-build --filter "Category=Load"
```

**Asserted vs. measured — deliberately separated.** Assertions are machine-independent invariants:
every accepted message delivered exactly once, payload digest unchanged, no duplicates, no dropped
connections, no `[Client receive loop]` errors, listener still accepting afterwards. Throughput and
latency percentiles are *reported* to `load-metrics.json` (gitignored, next to the test binary;
override the directory with `SMTP_LOADTEST_OUT`), never asserted — a msgs/sec floor tuned on one
machine becomes a flaky red build on another, and flaky builds get ignored.

**Integrity is hash-based but not byte-exact on the wire, for two structural reasons.** The server
prepends a `Received:` header containing `DateTime.UtcNow`, so every delivery differs even for
identical input; and DATA capture uses `StringBuilder.AppendLine`, i.e. `Environment.NewLine`, so a
byte-exact digest would pass on Windows and fail on Linux CI. `MessageCorpus.ExtractPayload` strips
server-prepended headers (anchoring on `Subject:`) and `Canonicalize` normalizes line endings; the
SHA-256 is taken over that. Verified non-vacuous by fault injection: flipping **one character** in a
large body failed the run with per-message diagnostics (re-verified against the current corpus).

**Corpus: 100 KB / 200 KB / 1000 KB** (rebalanced 2026-09-01 from an earlier 284 B–74 KB set, which
was too small to say anything about byte throughput). Sizes are exact to 0.1 KB and all sit well
under the 10 MB default `MessageCharactersLimit`. The 200 KB sample carries ~72 KB of genuine
multi-byte UTF-8 (Polish/Greek/CJK/emoji), so it actually exercises the UTF-8 decode path; the
1000 KB sample is the structurally important one, spanning many socket reads and repeatedly growing
the DATA `StringBuilder`. Mean message ≈ 433 KB, so a 1000-message run moves ~423 MB.

**The corpus avoids lines beginning with `.`** because dot-unstuffing is not implemented (Q1). That
defect is pinned separately by `DotStuffing_IsNotImplemented_PinsQ1` rather than being allowed to
surface as intermittent "corruption" in load runs. **When Q1 is fixed that test fails by design** —
it marks the behavior change.

**Byte metrics.** `MegabytesPerSecond` and `BytesAccepted` (plus `MeanMessageBytes`) are reported
alongside msgs/sec. With a mixed-size corpus msgs/sec alone cannot distinguish "slower" from "moving
larger messages", and MB/s stays comparable if the corpus is rebalanced again. Byte counts include
only *accepted* messages, so they always agree with the msgs/sec figure.

**Measured baseline** (KAT6, 16 logical cores, .NET 7.0.20, Debug build, loopback, no-op delivery
handler — Debug and loopback both matter, these are not production numbers):

| Scenario | Conc. | Msgs | Failures | msgs/sec | MB/s | Volume | p95 |
|---|---|---|---|---|---|---|---|
| ladder-conc-500 | 500 | 1000 | 0 | 58 | 24.6 | 423 MB | 1099 ms |
| ladder-conc-200 | 200 | 400 | 0 | 52 | 21.9 | 169 MB | 1573 ms |
| sustained-1000 | 50 | 1000 | 0 | 45 | 18.8 | 423 MB | 1511 ms |
| ladder-conc-100 | 100 | 200 | 0 | 47 | 19.9 | 84 MB | 1502 ms |
| max-receive-rate | 200 | 200 | 0 | 31 | 13.0 | 84 MB | 1800 ms |
| pipelined-single-conn | 1 | 25 | 0 | 19 | 7.6 | 10 MB | 133 ms |

At ~433 KB/message the server is **byte-bound, not message-bound**: MB/s is flat at roughly
**15–25 MB/s** across the whole ladder from 5 to 500 connections, while msgs/sec varies only because
message size does. Single-connection throughput (~7.6 MB/s) is about a third of the concurrent
ceiling, so concurrency buys ~3× and then saturates — consistent with a per-connection stream copy
being the bottleneck rather than the accept path. **Zero failures and zero corruption at every
level**, which is the part that is asserted.

Contrast with the earlier small corpus (mean 25 KB), which reported 300–870 msgs/sec but only
~8–19 MB/s: message rate collapses ~15× while byte rate stays in the same band. That is the clearest
argument for keeping the byte metrics — msgs/sec on its own would have suggested a catastrophic
regression where the server's actual data throughput barely moved.

**Client timeout raised for load sessions.** `SmtpSession` now takes an optional per-session timeout
(default 10 s, unchanged for the 316 protocol tests); `LoadDriver` uses 2 minutes. At 200+
concurrency with ~433 KB messages, connections queue behind one another and one accepted late can
wait past 10 s just to be greeted — with the fixed default the harness reported **its own client
timeout as a server failure** (54 spurious failures at conc=500). The work was queued, not lost:
every message that was actually attempted succeeded.

`SlowHandler_SessionsOverlap_ThroughputScalesWithConcurrency` is the one timing-sensitive assertion,
and it is deliberately loose: 12 sessions × a 200 ms handler complete in ~0.5 s against a
fully-serialized ~2.4 s, so it proves sessions overlap without being sensitive to scheduler jitter.
This is what confirms ACK-gated delivery does not serialize the server — the structural expectation
recorded in §2a, now measured rather than assumed.

## 12. Office 365 journaling relay — required configuration

> **➜ Action items for the next session are in [`immediate-todo.md`](immediate-todo.md).** It covers
> the size limit and missing SIZE extension, the two defects found, the four mail-losing defaults,
> and the streaming-DATA design that fixes the memory amplification. This section is the summary;
> that file is the working brief.

Added 2026-09-01. Deployment target: this server receives **journaled** mail relayed from Exchange
Online. The governing asymmetry is that **a rejected journal report is a compliance record that no
longer exists anywhere** — for ordinary mail a 5xx is the sender's problem; here it is permanent data
loss. Every limit that can 5xx a well-formed message must therefore be raised above what O365 sends —
but **not disabled**, since the server is internet-adjacent and an unbounded limit is a trivial OOM.

Encoded as tests in `Load/Office365RelayTests.cs` (13 tests) so a regression fails the build.

### Required settings

```csharp
var options = new ServerOptions(validateSPF: false, validateDMARC: false, null)
{
    ServerName             = "journal.example.com",
    MessageCharactersLimit = 200u * 1024 * 1024,  // finite, above O365's 150 MB max — NOT 0
    RecipientsLimit        = 0,
};
```

| Setting | Default | Required | Why the default loses mail |
|---|---|---|---|
| `MessageCharactersLimit` | 10 MB | **200 MB** (finite) | Over-limit → `552 5.4.3`, a **permanent** failure. Exchange does not retry. O365's own max is 150 MB. |
| `RecipientsLimit` | 50 | **0** | 51st recipient → `550 5.5.3`. A journal report for a large distribution list easily exceeds 50. |
| `ValidateSPF` | on | **off** | SPF fail → `554 5.7.23` before DATA. A journal report's envelope sender is the journaling mailbox, so the original message's alignment is irrelevant and can fail spuriously. Also a blocking DNS lookup on the session thread. |
| `ValidateDMARC` | on | **off** | DMARC fail → `554 5.7.1`. Same reasoning. |

Also verify: any `IMailFilter` must not reject (every filter hook can 5xx), and the `IMailDelivery`
handler must **throw or return `TemporaryFailure`** on a backend outage — never `Ok`. Throwing yields
`451 4.3.0`, which is transient, so Exchange queues and retries; returning `Ok` acknowledges a
message that was never stored. Pinned by `DeliveryHandlerThrows_YieldsTemporaryFailure_SoExchangeRetries`.

### Why the limit must be finite — and why that is safe

Setting `MessageCharactersLimit = 0` removes the rejection path but lets one client stream unbounded
data into a `StringBuilder` until the pod is OOM-killed. A finite ceiling above O365's 150 MB gives
the same delivery guarantee while keeping the DoS bound.

**A finite limit genuinely bounds memory** — `ProcessData` counts every line but appends only while
`Counter` is within the limit, so over-limit data is discarded as it arrives rather than accumulated.
Measured: **200 MB sent against a 10 MB limit peaked at ~126 MB** working set (not ~2 GB), returned
`552` at the terminating dot, and the connection stayed usable. Pinned by
`OverLimitFlood_IsDiscardedNotBuffered_AndConnectionSurvives`.

Two caveats: the limit counts **characters excluding CRLF**, not bytes (1:1 for MIME's ASCII/base64,
and the headroom above 150 MB covers base64 expansion plus headers); and it is enforced **late**, at
the terminating dot, so it bounds memory but not bandwidth or connection time.

### Measured: 150 MB works, but memory is the real constraint

A 150 MB message is accepted and delivered intact in ~2.5 s. **Peak working set for that single
message: ~1.6–1.9 GB — roughly 11× the message size.** The body is accumulated in a `StringBuilder`,
materialized by `ToString()`, copied again by `MailTransaction.Clone()`, and .NET strings are UTF-16
(2 bytes/char), so several full copies coexist.

**Pod sizing must budget ~2 GB per _concurrent_ large message, not per pod.** Concurrency is what
causes OOM, not total volume: 4 × 50 MB concurrently peaked at ~1.9 GB. A pod accepting several
150 MB journal reports at once on a 2 GB limit will be OOM-killed mid-transaction.

**This is unacceptable for production and is the top follow-up** — the fix is a streaming DATA path
(buffer to a `Stream`, spill to disk past a threshold, expose `GetBodyStream()`, stop materializing
`RawBody`), which would make peak memory *O(buffer)* instead of *O(11 × message)*. Design sketch and
sequencing in [`immediate-todo.md`](immediate-todo.md) item 4. It is a breaking public API change
(`RawBody` is public), so it wants a major version.

### Two gaps found while writing these tests — both now fixed (2026-09-01)

1. **`MAIL FROM:<>` (null reverse-path) was rejected with `501 5.5.2`; it is now accepted.** RFC 5321
   §4.5.5 requires it; it is what DSNs and some Exchange system-generated reports use, so rejecting it
   lost those messages permanently. The cause was `TransactionCommands.ProcessAddress`, which requires
   a non-empty address with '@' and a dotted domain. The MAIL FROM branch now recognises the null path
   via `IsNullReversePath` *before* calling it, yielding an empty `From` and `FromDomain`.

   SPF is skipped for the null path (no envelope domain to query), so no empty `smtp.mailfrom=` stanza
   is emitted. **DMARC returns `None`** for a null path rather than evaluating alignment: with no
   envelope identity and no DKIM support there is nothing to align, and an adversarial review caught
   that treating the empty `FromDomain` as a mismatched identity made a `p=reject` policy return
   `554` — permanently destroying a legitimate bounce sent by the domain that published the policy.
   Pinned by `NullSender_WithAlignedFromHeader_UnderDmarcReject_IsDelivered`.

   **Known limitation — a null sender is unauthenticated in both directions.** RFC 7208 §2.4 defines
   the null-path MAIL FROM identity as `postmaster@<HELO domain>`, but the EHLO/HELO argument is
   discarded (only `_protocolVersion` is kept), and DKIM is not implemented. So an unauthenticated
   client can send `MAIL FROM:<>` with a spoofed `From:` under a `p=reject` domain and be accepted;
   the server cannot distinguish that from a genuine bounce. Applying the policy instead would destroy
   legitimate bounces, which for journaling is unrecoverable — so delivering is the chosen side, and
   the gap is unreachable here because DMARC is off. Pinned as a known limitation by
   `NullSender_WithSpoofedFrom_UnderDmarcReject_IsAccepted_KnownLimitation`; closing it means
   retaining the HELO identity and running the §2.4 check.

   The reverse-path parser is **anchored**: `TryGetBracketedPath` is shared by `IsNullReversePath` and
   `ProcessAddress` so the two cannot disagree about which pair is the path. An argument hiding a real
   address behind an empty pair — `AUTH=<> <ceo@victim.example>`, `<><ceo@victim.example>`, `><>` —
   is refused `501` rather than read as a null sender, which would have shown filters an empty sender
   while a real address sat in the command. Pinned by
   `IsNullReversePath_IsAnchored_AndRejectsSmuggledPaths` and
   `MailFrom_BracketBearingPrefix_IsRefused_NotTreatedAsNullSender`.

   **This is a public behavior change**: a command that used to be refused is now accepted.

2. **The `SIZE` extension (RFC 1870) is now advertised.** EHLO previously returned only `250-<name>`
   and `250 8BITMIME`, so a sender could not learn the limit up front and Exchange discovered an
   over-limit message only after transmitting it in full. EHLO now ends with
   `250 SIZE <MessageCharactersLimit>`; a limit of 0 advertises `SIZE 0`, which RFC 1870 §6 defines
   as "no fixed maximum" — the same meaning the option already had.

   **The declared `SIZE=` on MAIL FROM is deliberately not acted upon.** Pre-rejecting on it would
   refuse a report before its data arrived, and for journaling a refused report is a compliance record
   that no longer exists anywhere — a sender that over-declares would lose a message it could have
   delivered. Oversized messages are still caught at the terminating dot. O365's
   `SIZE=`/`BODY=8BITMIME` parameters remain parsed-and-ignored, pinned by
   `MailFrom_WithO365EsmtpParameters_IsAccepted`.

   The advertised value is the limit unchanged, which already understates the octet count: the DATA
   counter adds each line's characters *after* CRLF is stripped, so
   `octets = sum(line bytes) + 2*lines >= counted characters` always. CRLF, UTF-8 multibyte and
   dot-stuffing all push octets up relative to characters, never down. Finite limits are never rounded
   down into `SIZE 0` ("no fixed maximum"). Pinned by
   `AdvertisedSizeLimit_NeverOverstates_AndPreservesFiniteLimits`.
