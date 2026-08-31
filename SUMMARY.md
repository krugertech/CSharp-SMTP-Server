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

## 2. Current state (as of `bb880f0`)

- Branch: **`dev`, 17 commits ahead of `origin/master`, NOT pushed** (`git push origin dev` when ready).
- Build: 0 errors (pre-existing harmless warnings: net7.0 EOL notice, CS8619 in MailTransaction).
- Tests: **294/294 green**, ~9 s per run, stable across repeated runs. Working tree clean.

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
- Test classes run **serially** (`xunit.runner.json`: `parallelizeTestCollections: false`) — the suite
  binds loopback ports; parallel classes race on port allocation. Don't re-enable without fixing that.
- Every test read is bounded by a 10 s timeout (in `SmtpSession`), so regressions fail instead of hanging.

## 4. Bugs found & fixed in this fork (each has regression tests)

| Bug | Symptom | Fix commit | Guard |
|---|---|---|---|
| Accept-thread blocking | Ctor ran `Init()` inline; when the greeting write completed synchronously (typical on Windows), the accept thread parked in `Receive()` → **one concurrent client at a time** | `294adbe` (`_ = Task.Run(Init)`) | `ConcurrentGreetingTests` |
| `Listener.ClientProcessors` race | Unsynchronized `List<>` mutated from 3 contexts; concurrent Add/Remove corrupted internals → NRE in `Dispose()` under load (deterministic repro, 3/3 runs pre-fix) | `df4636e` (lock + snapshot-in-Dispose) | `ConcurrencyStress_ParallelSessions_AllDeliveriesSucceed` |
| **B5** implicit-TLS crash | Failed/aborted handshake on a TLS port threw inside `async void Init()` → **whole process died** (silent disconnect, cert rejection, or plaintext probe — any scanner touching the port) | `7cb9fd9` (try/catch: log + drop connection) | 3 regression tests in `TlsStartTlsTests` (pre-fix: test run aborts outright) |

Known limitation (documented, not fixed): a throwing `IMailFilter.IsConnectionAllowed` still crashes via
the same `async void Init()` path.

## 5. Confirmed upstream bugs (B1–B4)

Details + repro evidence in TEST_PLAN.md §2; disposition per bug decided in REVIEW.md Part 3.

### Fixed

- **B3** *(fixed)*: `Clone()` dropped `DMARCValidationResult`, so every delivered transaction reported
  `None` regardless of the real outcome — `CheckDisabled` was lost too. Now copied in the `Clone()`
  initializer. Guarded by `MailTransactionTests.Clone_CarriesDmarcValidationResult` (Pass/Fail/CheckDisabled)
  and `AckGatingAdditionsTests.DeliveryClone_CarriesDmarcResult_AndHandlerMutationIsSafe` end-to-end.

### Not yet fixed (decision pending)

- **B1**: `MailTransaction.GetFrom/GetTo/GetCc/GetBcc` return MimeKit *display names*, not addresses
  (`From: sender@example.com` → `""`; `From: John <j@e.c>` → `"John"`) — makes DMARC validation effectively inert.
- **B2**: `AddHeader` before first parse duplicates the header in `ParsedMessage`.
- **B4**: `Clone()` shares the `DeliverTo` list by reference.

Pinned by `MailTransactionTests`. Fixing these changes observable behavior.

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
| Q12 | SPF DNS error handling deviates from RFC 7208: top-level NXDOMAIN → Temperror (not none); failed `a`/`mx` lookup returns the mechanism's qualifier instead of temperror (bare `a` even PASSES on DNS failure); redirect to nonexistent domain → Temperror (not permerror) |
| Q13 | SPF `redirect=` is evaluated positionally and short-circuits later mechanisms; RFC 7208 §6.1/§4.7 only consults it after all mechanisms have failed |

## 7. Test suite layout (294 tests)

- **Phase 1 — pure unit** (107): `SmtpDeliveryResultTests` (16), `ServerOptionsTests` (9),
  `MailTransactionTests` (19, pins B1–B4), `CheckCidrTests` (16), `DmarcOrganizationalDomainTests` (7,
  local suffix-list HTTP helper), `Base64Tests` (10), `ProcessAddressTests` (15), `WireFormattingTests` (6,
  socket-pair harness for internal `WriteCode`), `ValueTypesTests` (6), `VersionConsistencyTests` (2).
- **Phase 2 — protocol matrix + robustness** (107): `GreetingAndFilterTests` (7), `EhloHeloTests` (9),
  `CommandSequencingTests` (13), `MailFromTests` (13), `RcptToTests` (13), `DataAndMessageTests` (12),
  `AuthProtocolTests` (19), `AckGatingAdditionsTests` (6, incl. Q8 pin), `LifecycleAndRobustnessTests` (15).
- **Phase 3 — TLS** (11): `TlsStartTlsTests`.
- **Phase 4 — SPF/DMARC** (56): `SpfValidatorTests` (27), `DmarcValidatorTests` (13, incl. the B1
  end-to-end pin: a normal SPF-aligned message yields None with zero DNS queries),
  `SpfDmarcIntegrationTests` (4 — SPF Fail 554 at MAIL FROM, DMARC Fail 554 at DATA, AR headers).
  Infrastructure: `DnsStub` loopback UDP DNS responder (§1.5) + reused `LocalHttpServer` suffix-list
  fixture; test project LangVersion bumped to 12 (test-only).
- **Upstream-fix regression tests**: `AckGatingTests` (6), `AuthLoginInitialResponseTests` (4),
  `EhloBracketedIpv6Tests` (3), `ConcurrentGreetingTests` (1).

Shared infrastructure: `SmtpSession` (raw-TCP client with timeouts, multi-line reads, RST abort,
`UpgradeTlsAsync` for TLS), `TestServers`/`TestPorts` factories, `RecordingDelivery`/`ConfigurableFilter`/
`RecordingLogger` fakes, `NoopDelivery`, `LocalHttpServer`, `TlsTestCerts`. Test project has
`InternalsVisibleTo` access to library internals.

## 8. Remaining work

All four test-plan phases are complete (294 tests). Left:

1. **Push**: `git push origin dev` (17 commits, unpushed).
2. **Optional decisions** (user to make): fix B1–B4? (B1 is the important one — DMARC inert; now pinned
   end-to-end by Phase 4); address the Q11/Q12/Q13-class deviations in SpfValidator / zabszk.DnsClient?
   harden the filter-throwing path in `Init()`; anything from the upstream re-audit.

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
$env:DOTNET_ROLL_FORWARD="Major"; dotnet test CSharp-SMTP-Server.Tests/CSharp-SMTP-Server.Tests.csproj --no-build   # expect 294/294 in ~9 s
```

Key source files: `CSharp-SMTP-Server/Networking/{ClientProcessor,Listener}.cs` (connection lifecycle —
all three fixed bugs live here), `Protocol/Commands/{TransactionCommands,AuthenticationCommands}.cs`,
`MailTransaction.cs` (B1–B4), `SMTPServer.cs` (public API surface).
