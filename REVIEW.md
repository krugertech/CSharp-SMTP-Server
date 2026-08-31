# REVIEW — findings for the `dev` branch (17 commits, `master..dev` = `221a2b5..fb820ac`)

**Purpose.** Discovery document. Everything below is something that needs to be *fixed, decided, or
corrected*. Work already done correctly is not listed. Written for a later AI/engineer to action:
each item has a location, evidence, an impact statement, and a concrete recommendation.

**Verification performed for this review**
- `dotnet build CSharp-SMTP-Server.sln` → exit 0 (warnings only: net7.0 EOL, CS8619 in MailTransaction).
- `DOTNET_ROLL_FORWARD=Major dotnet test --no-build` → **294/294 passed, 10 s**. The prior model's
  headline claim is accurate.
- Overload-resolution finding (R1) confirmed **empirically** with a standalone four-overload repro.
- B2 duplication confirmed by an existing passing pin test plus a trace of the production DATA path.
- Provenance of every finding checked with `git log master..dev -- <file>` and `git blame`.

**Attribution note — read before assigning blame.** The branch under review is `master..dev`
(17 commits). Several defects below live in code that reached `master` **earlier**, in the fork's
own pre-`dev` commits (`50276ad` ACK-gating, `9dce319` enhanced status codes, `3a3dd1c`/`516702a`
rebranding). They are flagged **[pre-existing on master]**. They are still real and still need fixing,
but the `dev` work did not introduce them — it documented several of them as pinned quirks. Items
marked **[new in dev]** were introduced by the commits under review.

**Overall assessment.** The work is of good quality: the three bug fixes are real, correctly reasoned,
and each has a regression test; the test infrastructure (`SmtpSession`, `DnsStub`, `TestPorts`) is well
built; documentation is thorough and largely accurate; and the "pin first, fix second" convention is
sound practice, applied consistently. The genuinely urgent items are **not** in the `dev` work itself —
they are the inherited security defects it documented but deliberately left unfixed (**B1**, **Q12(b)**),
plus one **gap in its own `Listener` fix** (**R11**). Everything else is cleanup.

**This document was itself adversarially reviewed** (Codex, `gpt-5.6-sol`), and five of my original
findings were wrong or overstated. Those are corrected in place and marked — R1 downgraded P0→P2 (my
RFC-conformance claim was false), R10 retracted P1→P3 (my criticism of the pinning convention was
unfair), R5 downgraded P2→P3 (my "weakened test" claim was factually false), R8 downgraded P2→P3, R2
downgraded P1→P2. R11 was found by that pass, not by me. Corrections are left visible rather than
silently edited out, so a later reader can see which judgements were contested.

Severity key: **P0** = fix before any release · **P1** = fix soon · **P2** = should fix · **P3** = judgement call.

---

## Part 0 — Working instructions for the implementer

**Read first:** `SUMMARY.md` (handoff/orientation), then `TEST_PLAN.md` §2 for the B1–B5 / Q1–Q13
evidence, then `ARCHITECTURE.md` if you need the connection-lifecycle model. This document assumes
that context and does not repeat it.

### Build and test

```powershell
dotnet build CSharp-SMTP-Server.sln
# REQUIRED: tests target net7.0, machine has .NET 9 SDK. Without this they abort
# with "framework not found" — it is not optional.
$env:DOTNET_ROLL_FORWARD="Major"
dotnet test CSharp-SMTP-Server.Tests/CSharp-SMTP-Server.Tests.csproj --no-build
```

Baseline before you touch anything: **294/294 green, ~10 s.** If that is not what you see, stop and
find out why before making changes.

### Constraints

- **The branch is already pushed.** `dev` and `origin/dev` are both at `fb820ac`. Fix forward in new
  commits; **do not rewrite published history**.
- **Test classes run serially** (`xunit.runner.json`: `parallelizeTestCollections: false`,
  `maxParallelThreads: 1`) because the suite binds loopback ports. Don't re-enable parallelism without
  first fixing port allocation (R8).
- **Keep the pin-first/fix-second convention** (SUMMARY.md §9). It is sound — see R10. When you fix a
  pinned bug, update its pin test in the *same commit* as the library change so history stays
  bisectable, and drop the `_BugB#` / `_PinQ#` suffix once the behavior is no longer a known defect.
- Several fixes below are **behavior-breaking for library consumers** (B1 especially). They need a
  changelog entry and a version bump — and note `VersionString` is already stale (R5).

### Per-finding: what to change, which tests move, and when you're done

Test names below were enumerated from the current tree; they are the actual work, not estimates.

| Finding | Library change | Tests that must change | Done when |
|---|---|---|---|
| **B1** (P0) | `MailTransaction.cs:58-73` — return `.Address` not `.Name`; use `.Mailboxes` to flatten groups | `MailTransactionTests`: `GetFrom_PlainAddress_ReturnsEmptyDisplayName_BugB1`, `GetFrom_DisplayName_ReturnsDisplayNameOnly_BugB1`, and the `GetTo` display-name assertion (line ~83). `DmarcValidatorTests.NormalFrom_DmarcIsInert_PinB1` **inverts** — it currently asserts zero DNS queries | A normal `From: user@domain` message causes real `_dmarc.` DNS lookups, and an unaligned message with `p=reject` reaches `Fail` → **554** at DATA. The `PinB1` test should now assert enforcement, not inertness |
| **B3** (P2) | `MailTransaction.cs:139-150` — add `DMARCValidationResult` to the `Clone()` initializer | `MailTransactionTests.Clone_DropsDmarcValidationResult_BugB3`; `AckGatingAdditionsTests.DeliveryClone_DmarcResultIsNone_AndHandlerMutationIsSafe` (first half) | The delivered clone reports the real DMARC result, incl. `CheckDisabled` |
| **B4** (P2) | `MailTransaction.cs:147` — `DeliverTo = new List<string>(DeliverTo)` | `MailTransactionTests.Clone_SharedDeliverToListInstance_BugB4`; same `AckGatingAdditionsTests` test (second half) | Handler mutation of `DeliverTo` no longer affects the server-side transaction |
| **Q12(b)** (P1) | `SpfValidator.cs:177-192` — propagate `Temperror` from `CheckAddressMatch` instead of treating any non-`None` as a match; same shape for `mx` | `SpfValidatorTests.AMechanism_DnsFailure_ReturnsQualifierNotTemperror_PinQ12` — both assertions flip to `Temperror` | `v=spf1 a:failsrv.test` yields **Temperror**, not `Pass`, when the A lookup SERVFAILs |
| **R11** (P1) | `Listener.cs:63-96` — check `_dispose` and register under one `_processorsLock` critical section; dispose a processor arriving post-shutdown | New test needed; extend `LifecycleAndRobustnessTests` alongside `Dispose_StopsListener_AndKillsOpenConnections`. Use a barrier to hold a connection between accept and registration while `Dispose()` runs | No connection survives `SMTPServer.Dispose()`; nothing is still using the certificate when it is disposed |
| **R6** (P1) | `ClientProcessor.cs:71-97` — wrap the whole `Init()` body in try/catch (log + `Dispose()`), covering `Greet()` and the filter call | New test; mirror the B5 pattern in `TlsStartTlsTests`, using `ConfigurableFilter` set to throw | A filter throwing in `IsConnectionAllowed` drops only that connection; the process survives and the next client is greeted |
| **R1** (P2) | `ClientProcessor.cs:196-203` — rename to `WriteCodeWithMessage`, or change param to `ushort` | **53 assertions across 10 files** — `AckGatingAdditionsTests`(4), `AuthProtocolTests`(5), `CommandSequencingTests`(5), `DataAndMessageTests`(5), `EhloHeloTests`(3), `LifecycleAndRobustnessTests`(2), `MailFromTests`(3+7 `InlineData`), `RcptToTests`(5+1), `SpfDmarcIntegrationTests`(6), `TlsStartTlsTests`(7). My earlier "~15" was wrong | `NOOP` → `250 2.0.0 OK`; `VRFY` → `252 5.5.1 Cannot VRFY user...`. Budget real time for the test sweep — it is the bulk of this fix |

**Suggested commit shape** (matches the existing history): one commit per finding, library change plus
its test updates together, docs-only commits last. Update `SUMMARY.md` §5/§6 as you close each B/Q item
so the handoff document stays true.

---

## Part 1 — Defects in the current code

Highest-value items. Each is tagged with whether the `dev` branch introduced it.

### R1 — **P2** — `WriteCode(int, string)` overload silently hijacks ~20 call sites, dropping the human-readable response text · **[pre-existing on master]**

**Location:** [ClientProcessor.cs:196-203](CSharp-SMTP-Server/Networking/ClientProcessor.cs#L196-L203)
**Introduced by:** `50276ad` "Implement ACK-gating for reliable SMTP delivery" — **on `master`**, not in
the `dev` range under review (`git blame` lines 196-202 → `50276adf`).

A fourth overload sits alongside the three original `ushort` ones:

```csharp
internal async Task WriteCode(int code, string message)   // added in 50276ad, on master
```

alongside the three pre-existing `ushort` overloads. Because an untyped integer literal is `int`, and
`int`→`ushort` has **no implicit conversion**, every two-argument call site written as
`WriteCode(250, "2.0.0")` now binds to the *new* overload — which treats the second argument as the
**message**, not the enhanced status, and never consults the `SMTPCodes` table.

**Empirically confirmed** (standalone repro, four overloads, same signatures):

```
WriteCode(250, "2.0.0")      -> int2-SANITIZER   ← the new overload wins
WriteCode(503,"5.5.1","x")   -> ushort3          ← 3-arg unaffected
WriteCode(250)               -> ushort1          ← 1-arg unaffected
WriteCode(u, "2.0.0")  [var] -> ushort2          ← only a typed ushort variable binds correctly
```

**Wire impact.** Baseline `2f7386e` sent `250 2.0.0 OK`. The fork sends `250 2.0.0`. Affected responses
include NOOP/RSET/QUIT (`250 2.0.0`), MAIL FROM (`250 2.0.0`), RCPT TO (`250 2.1.5`), VRFY (`252 5.5.1`),
HELP (`214 2.0.0`), bad-sequence (`503 5.5.1`), unrecognized command (`502 5.5.1`), and
auth-encryption-required (`538 5.7.11`) — roughly 20 call sites.

**Not an RFC violation — corrected.** An earlier draft of this review claimed RFC 5321 §4.2 requires
text after the reply code and that strict clients would break. That is wrong on both points. §4.2's
grammar is `Reply-code [ SP textstring ]` — the text is **optional**; the RFC explicitly requires
clients to accept a bare code and only says senders SHOULD NOT omit it. Furthermore the emitted line is
not textless: `250 2.0.0` passes the enhanced status through as the text part. The real cost is
**diagnostics and operability** — human operators and log readers lose `OK` / `Cannot VRFY user...`,
and the enhanced status is no longer distinguishable from a message. That is a genuine regression from
baseline, but it is a quality-of-implementation issue, not a conformance or interop failure.

**Attribution, corrected.** The upstream import `2f7386e` had only the three `ushort` overloads, so
every affected call site once produced table text. But the fourth overload was added by `50276ad`,
which is **already on `master`** — so this is a regression of *the fork as a whole*, not of the `dev`
branch. The prior model's Q7 label ("overload accident", current behavior to be pinned) is therefore
defensible as a description of the code it inherited; it simply never identified the cause or flagged
it as a defect worth fixing.

**53 assertions across 10 test files** assert the current stripped form (enumerated in Part 0; e.g.
[CommandSequencingTests.cs:74-76](CSharp-SMTP-Server.Tests/CommandSequencingTests.cs#L74-L76),
[AuthProtocolTests.cs:61](CSharp-SMTP-Server.Tests/AuthProtocolTests.cs#L61), commented
`// Q7: no table text`), so any fix must update them together. They are labelled as Q7 pins, which is
the prior model's documented characterization-test convention — see R10.

**Recommendation.** Worth fixing, but as cleanup rather than urgent work. Rename the overload to remove
the ambiguity — e.g. `WriteCodeWithMessage(int code, string message)` — or change its first parameter to
`ushort` so callers must be explicit. Then update all 53 pinned assertions to expect the restored table text
(`250 2.0.0 OK`, `252 5.5.1 Cannot VRFY user...`). Add the cause (the `50276ad` overload) to the Q7 entry
in TEST_PLAN.md / SUMMARY.md, which records the symptom but not the mechanism. Verify no call site was
*intending* the sanitizer.

---

### R2 — **P2** — `AUTH LOGIN` with an undecodable initial response silently falls back to the username prompt · **[new in dev]**

**Location:** [AuthenticationCommands.cs:28-45](CSharp-SMTP-Server/Protocol/Commands/AuthenticationCommands.cs#L28-L45)

```csharp
var initialUsername = args.Length > 1 ? Misc.Base64.Base64Decode(args[1]) : null;

if (!string.IsNullOrEmpty(initialUsername)) { /* capture password */ }
else { /* prompt for username */ }
```

`Base64Decode` returns `null` on invalid base64 (it swallows the exception). So `AUTH LOGIN !!!invalid!!!`
— a client that *did* send an initial response, just a malformed one — is treated identically to
`AUTH LOGIN` with no initial response: the server replies `334 VXNlcm5hbWU6` and waits for a username.
The client, believing it already sent the username, replies with its **password**, which the server then
stores as `TempUsername`. Authentication fails confusingly, and the password has been consumed in the
username slot.

RFC 4954 §4 requires the server to reject a malformed initial response with `501`. The same applies to a
client sending the RFC 4954 `=` (empty initial response), which decodes to empty and also falls through
to the prompt path.

**Note:** the two-arg `WriteCode(501, "5.7.4", ...)` form used elsewhere in this file is the 3-arg
overload and is unaffected by R1.

**Recommendation.** Distinguish the three cases explicitly: `args.Length == 1` → prompt;
`args[1] == "="` → prompt (empty initial response, per RFC); decode failure → `501 5.5.2 Invalid
base64 in initial response` and abort the exchange. Add tests for malformed and `=` initial responses —
neither is currently covered by [AuthLoginInitialResponseTests.cs](CSharp-SMTP-Server.Tests/AuthLoginInitialResponseTests.cs).

---

### R3 — **P3** — Broken indentation in the AUTH LOGIN block · **[new in dev]**

**Location:** [AuthenticationCommands.cs:28-30](CSharp-SMTP-Server/Protocol/Commands/AuthenticationCommands.cs#L28-L30)

The `case "LOGIN":` body is mis-indented — the comment sits at a deep stray indent, the statements sit
at the `switch` level rather than the case level, and the `break` is detached from the block. It parses
and behaves correctly; it just reads as damaged and violates the repo's `.editorconfig` (tabs, Allman).

**Recommendation.** Reformat the case block. Trivial, but it is the most visually obvious "an AI wrote
this" artifact in the fork diff.

---

### R4 — **P2** — `SmtpDeliveryResult.Status()` accepts any `int` status code, including invalid ones · **[pre-existing on master]**

**Location:** [SmtpDeliveryResult.cs:10-23, 39-40](CSharp-SMTP-Server/Protocol/Responses/SmtpDeliveryResult.cs#L10-L23)

The constructor validates CR/LF in `message` and `enhancedStatus` (good — response-splitting is properly
prevented) but performs **no range validation on `statusCode`**. `SmtpDeliveryResult.Status(99999, ...)`
or `Status(-1, ...)` is constructed happily and then hits:

```csharp
await processor.WriteCode((ushort)deliveryResult.StatusCode, ...);   // TransactionCommands.cs:268
```

The unchecked `(ushort)` cast **wraps silently**: `Status(65536+250)` → `250`, turning an intended
rejection into an acceptance. `Status(-1)` → `65535`. A library consumer's typo becomes a
silently-accepted message.

Also note `EnhancedStatus` is only CR/LF-checked, not format-checked, so `Status(250, "not-a-status", ...)`
emits a malformed enhanced status.

**Recommendation.** Validate in the constructor: `statusCode` in `[200, 599]` (throw `ArgumentOutOfRangeException`
otherwise), and optionally regex-validate `enhancedStatus` against `^[245]\.\d{1,3}\.\d{1,3}$`. Change the
field type to `ushort` so the cast at the call site disappears. Add unit tests — current
[SmtpDeliveryResultTests.cs](CSharp-SMTP-Server.Tests/SmtpDeliveryResultTests.cs) (16 tests) covers CR/LF
rejection but not code ranges.

---

### R5 — **P3** — `VersionString` reports a stale prerelease suffix · **[drift pre-existing on master]**

**Locations:** [SMTPServer.cs:27](CSharp-SMTP-Server/SMTPServer.cs#L27) (`"1.1.6-krugertech.1"`) ·
[CSharp-SMTP-Server.csproj:24](CSharp-SMTP-Server/CSharp-SMTP-Server.csproj#L24) (`1.1.6-krugertech.3`)

The public `SMTPServer.VersionString` constant reports `-krugertech.1` while the NuGet package ships as
`-krugertech.3`. A consumer logging `SMTPServer.VersionString` gets a stale prerelease suffix. Both
values predate the `dev` branch.

**Correction — the "weakened test" claim is withdrawn.** An earlier draft asserted that
`VersionConsistencyTests` had been written strict and then weakened to tolerate this drift. That is
false, and I should have checked the history before writing it.
`git log --all -- CSharp-SMTP-Server.Tests/VersionConsistencyTests.cs` shows the file was created
**once**, in `e8a241f`, already asserting only the numeric prefix — there was never a stronger
assertion. The test's own name (`VersionString_NumericPrefix_MatchesCsprojPackageVersion`) and
TEST_PLAN.md §338 both state numeric-prefix consistency as the intended scope. It is a deliberately
scoped test that documents a known limitation, not a retreat.

**Recommendation.** Set `VersionString` to `1.1.6-krugertech.3`, or better, drive both from one MSBuild
property so they cannot diverge. Tightening the assertion to full-string equality is optional — worth it
only if consumers or release automation depend on the exact informational version.

---

## Part 2 — Correctness and robustness gaps not yet covered

### R6 — **P1** — A throwing `IMailFilter` still crashes the process (same `async void` path as the fixed B5) · **[pre-existing; knowingly left open by dev]**

**Location:** [ClientProcessor.cs:71-97](CSharp-SMTP-Server/Networking/ClientProcessor.cs#L71-L97)

B5 (the implicit-TLS handshake crash) was correctly fixed by wrapping `AuthenticateAsServerAsync` in
try/catch. But `Init()` is still `async void`, and the *other* awaited call on that path — `Greet()`,
which calls `await Server.Filter.IsConnectionAllowed(RemoteEndPoint)` — remains unguarded. A filter
implementation that throws (a database timeout in a real deployment is enough) propagates out of
`async void` and **terminates the process**, exactly as B5 did.

The prior model identified this and explicitly documented it as a "known limitation, not fixed"
(SUMMARY.md §4). Given it is the same crash class as a bug rated severe enough to fix immediately, and
is reachable from ordinary consumer code rather than a hostile scanner, it should not stay open.

**Recommendation.** Wrap the whole `Init()` body in try/catch (log + `Dispose()`), which covers `Greet()`,
the filter call, the `StreamReader` construction, and any future addition on that path. Add a regression
test with a throwing `ConfigurableFilter` asserting the process survives and the next client is greeted —
mirroring the existing B5 tests in `TlsStartTlsTests`.

---

### R11 — **P1** — A connection accepted during shutdown escapes `Listener.Dispose()` entirely · **[new in dev — a gap in the `df4636e` fix]**

**Location:** [Listener.cs:63-96](CSharp-SMTP-Server/Networking/Listener.cs#L63-L96) ·
[SMTPServer.cs:120-128](CSharp-SMTP-Server/SMTPServer.cs#L120-L128)

The `df4636e` fix correctly serialized the `ClientProcessors` list, but left a window between **accept**
and **registration**:

```csharp
var client = _listener.AcceptTcpClient();               // Listener.cs:67 — accepted, not yet registered
AddProcessor(new ClientProcessor(client, this, _secure)); // Listener.cs:68 — registration
```

`Dispose()` sets `_dispose`, stops the socket, then snapshots the list under the lock
([Listener.cs:83-96](CSharp-SMTP-Server/Networking/Listener.cs#L83-L96)). If `AcceptTcpClient` has
already returned but `AddProcessor` has not yet run, **`Dispose` snapshots first and the connection is
registered afterwards** — into a list nobody will read again. That client is never disposed and keeps
running after shutdown.

`294adbe` widened this window: `ClientProcessor`'s constructor now does `_ = Task.Run(Init)`, so the
processor begins greeting and serving the client on a thread-pool thread independently of whether
registration has happened.

The consequence is worse than a leaked socket. `SMTPServer.Dispose()` disposes every listener and then
**disposes the TLS certificate** ([SMTPServer.cs:127](CSharp-SMTP-Server/SMTPServer.cs#L127)) — while
an escaped connection may still be mid-handshake against it.

The in-code comment at [Listener.cs:89-91](CSharp-SMTP-Server/Networking/Listener.cs#L89-L91) asserts
this case is safe ("an accept that slipped in just before Stop is either in the snapshot … or outlives
it, in which case its own dispose path removes itself safely"). The second branch is the bug: outliving
the snapshot means never being disposed at all, not disposing safely.

**Recommendation.** Make the disposed-check and the registration atomic with respect to each other —
inside `AddProcessor`, under `_processorsLock`, refuse registration when `_dispose` is set and dispose
the incoming processor immediately instead. Construct and register **before** starting any client work
(pair with R6's `Init()` hardening, which moves greeting out of the constructor path anyway). A test
using a barrier to hold a connection between accept and registration while `Dispose()` runs would pin it.

*Credit: found by the Codex adversarial pass, not by my first read.*

---

### R7 — **P2** — `Dispose` is not thread-safe; the `_dispose` guard is a plain (non-volatile) `bool` with a check-then-act race · **[pre-existing upstream]**

**Location:** [ClientProcessor.cs:69, 351-355](CSharp-SMTP-Server/Networking/ClientProcessor.cs#L351-L355)

```csharp
private bool _dispose;          // not volatile

public void Dispose(bool dontRemove, bool reset)
{
    if (_dispose) return;       // check…
    _dispose = true;            // …then act — not atomic
    ...
```

`Dispose` is reachable from at least six sites across multiple threads: the receive loop, the write-error
path (`_fails > 3`), the TLS-handshake catch, the QUIT handler, and `Listener.Dispose()` (which calls
`processor.Dispose(true, true)` from the shutdown thread while the connection's own loop may be disposing
concurrently). Two threads can both pass the `if` before either sets the flag, double-disposing the
streams and socket. In practice `Stream.Dispose`/`Socket.Dispose` are idempotent, so the likely outcome
is a benign redundant teardown rather than a guaranteed throw — but the sequence is unsynchronized and
an `ObjectDisposedException` escaping the shutdown path is possible, not inevitable. The non-volatile
field additionally permits stale reads across cores.

This is adjacent to — but distinct from — the `ClientProcessors` list race that *was* fixed in `df4636e`.
That fix protected the list; the processor's own dispose remains unguarded. The existing concurrency
stress test exercises parallel *sessions*, not concurrent dispose of the *same* processor, so it would not
catch this.

**Recommendation.** Replace with `Interlocked.Exchange(ref _disposeFlag, 1) == 1` (an `int` field) for an
atomic test-and-set, and wrap the individual stream/socket teardown calls so one failure does not skip the
rest. A targeted test is hard to write deterministically; a stress test that disposes the server while
sessions are mid-DATA would raise confidence.

---

### R8 — **P3** — `TestPorts.Allocate()` has a latent TOCTOU race (unproven in practice) · **[new in dev]**

**Location:** [SmtpSession.cs:172-187](CSharp-SMTP-Server.Tests/SmtpSession.cs#L172-L187)

```csharp
var tmp = new TcpListener(IPAddress.Loopback, 0);
tmp.Start();
return (ushort)((IPEndPoint)tmp.LocalEndpoint).Port;   // …then finally: tmp.Stop()
```

The port is **released before the caller binds it**. Between `Stop()` and the server's `Start()`, the OS
could hand the same port to another process. The window is real.

**Calibrated down from an earlier draft.** I originally rated this P2 and wrote that it "will cause
intermittent CI failures". That overstates the evidence: **no flake has been observed** — the suite is
294/294 here and was stable across the prior model's repeated runs — and serial execution
(`maxParallelThreads: 1`) plus Windows' ephemeral-port cycling make same-suite reuse unlikely. Treat it
as a latent harness risk, not a predicted failure.

I also withdraw two of the three fixes I proposed: keeping the probe listener alive **cannot** work
(the server must bind that same endpoint, so the probe has to release it first), and a process-local
`HashSet` does nothing about the cross-process collision that motivates the finding.

**Recommendation.** Leave it unless a flake actually reproduces. The robust fix is to let the server
bind port 0 and expose the resulting endpoint, so no probe-and-release is needed at all — a small
library change (`ListeningParameters` / `Listener` surfacing the bound port) that would also help real
consumers wanting ephemeral binds. Failing that, wrap server startup in a retry on
`SocketException`/address-in-use.

---

### R9 — **P3** — Timing-dependent sleeps in tests · **[new in dev]**

**Locations:** [AckGatingAdditionsTests.cs:56,63](CSharp-SMTP-Server.Tests/AckGatingAdditionsTests.cs#L56) (2000 ms + 500 ms) ·
[AckGatingTests.cs:177,240](CSharp-SMTP-Server.Tests/AckGatingTests.cs#L177) (200/300 ms races) ·
[LifecycleAndRobustnessTests.cs:302](CSharp-SMTP-Server.Tests/LifecycleAndRobustnessTests.cs#L302) (300 ms)

Fixed delays used to prove a *negative* (`Assert.False(delivery.TokenFired)`) or to let cleanup settle.
These are correct today and the 2 s margin is generous, but they are the usual source of CI flakes on
loaded machines, and the 2 s sleep is a meaningful slice of the 10 s suite runtime.

**Recommendation.** Low priority — the assertions are sound. Where a negative must be proven, a shorter
bounded wait plus a positive control (assert the token *does* fire when the connection closes normally)
would be both faster and stronger. Do not churn these without cause.

---

### R10 — **P3 (test hygiene)** — Pin discoverability could be improved · *(claim substantially retracted)*

**An earlier draft of this review rated this P1 and accused the suite of encoding unrecognized bugs as
specifications. That was unfair and is withdrawn.** An adversarial pass checked the specific cases and
they do not support the charge:

- **Q12** — the test is named `AMechanism_DnsFailure_ReturnsQualifierNotTemperror_PinQ12`
  ([SpfValidatorTests.cs:223-232](CSharp-SMTP-Server.Tests/SpfValidatorTests.cs#L223-L232)), cites the
  RFC 7208 §5 requirement it deviates from, and comments the fail-open outright:
  `bare "a" → Pass on DNS failure!`. That is not an unrecognized bug — it is a documented one.
- **Q7** — SUMMARY.md §6 labels it an "overload accident" and records the exact symptom.
- **R5** — see below; the "weakened test" premise was simply false.

The prior model's convention (SUMMARY.md §9: *"pin first, fix second — suspected bugs get empirical
verification + exact-behavior tests before any change"*) is **standard characterization-testing
practice** and the correct approach for auditing inherited behavior. It preserves bisectable evidence
and prevents silent behavior drift during a fix. My criticism inverted that: I treated the presence of
a passing test as an endorsement of the behavior, when the tests say the opposite in their names and
comments.

**What remains, and it is minor.** Pin labelling is *inconsistent* rather than absent — `_PinQ12` uses a
name suffix, Q7 pins use only a code comment. A uniform marker (e.g. `[Trait("Pin","Q12")]`) would make
them filterable and let a maintainer list every provisional assertion in one command. Worth doing when
convenient; not a process defect.

---

## Part 3 — Deferred upstream bugs (B1–B4): recommendation on each

The prior model deliberately pinned these with tests rather than fixing them, since fixes change
observable behavior. That was the right call procedurally. My recommendations:

### B1 — **P0 — FIX.** `GetFrom`/`GetTo`/`GetCc`/`GetBcc` return display names, not addresses — DMARC is inert

**Location:** [MailTransaction.cs:58-73](CSharp-SMTP-Server/MailTransaction.cs#L58-L73)

```csharp
public string? GetFrom => ParsedMessage.From.Count > 0 ? ParsedMessage.From[0].Name : null;
public IEnumerable<string> GetTo() => ParsedMessage.To.Select(x => x.Name);   // …Cc, Bcc identical
```

MimeKit's `.Name` is the **display name**. For `From: sender@example.com` it is `""`; for
`From: John <j@e.c>` it is `"John"`. The address is `.Address` (on `MailboxAddress`).

**This is a security defect, not a cosmetic one.** The DMARC path depends on it:

- [DmarcValidator.cs:123-131](CSharp-SMTP-Server/Protocol/DMARC/DmarcValidator.cs#L123-L131) — `var from = transaction.GetFrom;` → `ProcessAddress(from, out var fromDomain)` → `fromDomain == null` → **returns `ValidationResult.None`**.
- [TransactionCommands.cs:225-231](CSharp-SMTP-Server/Protocol/Commands/TransactionCommands.cs#L225-L231) — the `if (dmarcValidation == ValidationResult.Fail)` → `554` rejection gate therefore **never fires**.

Net effect: a server configured with `ValidateDMARC: true` performs no effective DMARC enforcement and
emits an `Authentication-Results: ... dmarc=none header.from=(none)` header for every message. Operators
believe they have DMARC protection and do not. The prior model's Phase 4 test pins this end-to-end
(a normal SPF-aligned message yields `None` **with zero DNS queries** — the validator bails before
querying), which is strong evidence.

**Recommendation.** Fix. Return `.Address` for mailbox entries — `ParsedMessage.From.Mailboxes.FirstOrDefault()?.Address`
(`.Mailboxes` correctly flattens groups; `From[0]` may be a `GroupAddress` with no address). Same for
To/Cc/Bcc. This is a **breaking behavior change** for any consumer relying on the current display-name
output, so it belongs in the changelog and a version bump. If display names are wanted, add separate
`GetFromName`-style members. Update `MailTransactionTests` (which currently asserts the buggy output) and
re-point the Phase 4 DMARC tests at the corrected behavior — expect real DNS queries and reachable
`Fail` → 554.

### B2 — **P2 — FIX.** `AddHeader` before first parse duplicates the header

**Location:** [MailTransaction.cs:132-136](CSharp-SMTP-Server/MailTransaction.cs#L132-L136)

```csharp
public void AddHeader(string name, string value)
{
    RawBody = $"{name}: {value}\r\n{RawBody}";   // prepend to raw
    ParsedMessage.Headers.Add(name, value);      // getter parses the *already-modified* RawBody, then adds again
}
```

If `_parsedMessage` has not been materialized yet, the `ParsedMessage` getter lazily parses the
just-modified `RawBody` (which already contains the header), and then `.Headers.Add` appends a second
copy.

**Confirmed reachable in production, on every message.** In the DATA path
([TransactionCommands.cs:180-205](CSharp-SMTP-Server/Protocol/Commands/TransactionCommands.cs#L180-L205)),
`RawBody` is assigned from the `DataBuilder` and the very next transaction access is
`AddHeader("Received", received)` — the first parse. So the duplicate always lands on **`Received:`**
(subsequent `AddHeader` calls hit the already-parsed path and behave correctly). `RawBody` — what most
handlers persist — has one copy; `ParsedMessage.Headers` has two. Any consumer reading headers via
`ParsedMessage` (including hop-counting or loop-detection logic, which counts `Received:` headers) sees
an inflated count.

**Recommendation.** Capture `var parsed = ParsedMessage;` **before** mutating `RawBody`, then prepend and
`parsed.Headers.Add(...)`. Or drop the `.Headers.Add` entirely and invalidate `_parsedMessage` so the next
access re-parses. Verify against the existing pin test.

### B3 — **P2 — FIX.** `Clone()` drops `DMARCValidationResult`

**Location:** [MailTransaction.cs:139-150](CSharp-SMTP-Server/MailTransaction.cs#L139-L150)

The object initializer copies `AuthenticatedUser`, `RawBody`, `_parsedMessage`, `RemoteEndPoint`,
`DeliverTo`, `Encryption` — but **not** `DMARCValidationResult`. Since the clone is what gets handed to
`IMailDelivery` ([TransactionCommands.cs:252](CSharp-SMTP-Server/Protocol/Commands/TransactionCommands.cs#L252)),
every delivered transaction reports `DMARCValidationResult = None` regardless of the real outcome — even
`CheckDisabled` is lost. A consumer making delivery decisions on that field is acting on garbage.

Note `SPFValidationResult` *is* carried (via the constructor), so the asymmetry is clearly an oversight.

**Recommendation.** Add `DMARCValidationResult = DMARCValidationResult` to the initializer. Low risk,
clearly correct. Fix alongside B1 — B1 makes the value meaningful, B3 makes it visible.

### B4 — **P2 — FIX.** `Clone()` shares the `DeliverTo` list by reference

**Location:** [MailTransaction.cs:147](CSharp-SMTP-Server/MailTransaction.cs#L147) — `DeliverTo = DeliverTo,`

The clone and the original share one `List<string>`. A delivery handler that mutates `DeliverTo`
(filtering recipients, deduplicating) mutates the server-side transaction too. Because delivery now runs
**inside** the SMTP session and is awaited (the fork's ACK-gating change), a handler mutating the list
while the session still holds a reference is a live risk — arguably more so than upstream, where delivery
was fire-and-forget.

**Recommendation.** `DeliverTo = new List<string>(DeliverTo)`. Trivial, and defensive copying is the
correct semantic for `ICloneable`.

---

## Part 4 — Pinned quirks (Q1–Q13): recommendation on each

Q7 is covered above as **R1** — it is a fork regression, not a quirk, and is the one item in this group
that is genuinely mislabelled.

| # | Quirk | Recommendation |
|---|---|---|
| **Q1** | No dot-stuffing in DATA | **P1 — FIX.** RFC 5321 §4.5.2 requires the receiver to strip a leading `.` from data lines. Without it, any body line beginning with `.` is silently corrupted, and a line consisting of `..` prematurely… is mis-handled. This is a data-integrity bug affecting real mail, not a stylistic quirk. Worth reclassifying out of "quirk". |
| **Q2** | VRFY returns `252 5.5.1` | **P3 — leave, or change to `2.5.2`.** Mixing a 2xx code with a 5.x.x enhanced status is malformed per RFC 3463 (the first digit must agree). Cosmetic; no client depends on it. Fix opportunistically when touching R1, since the same call site is involved. |
| **Q3** | Authenticated `Received:` omits `from <ip>` | **P2 — FIX.** Loses forensic provenance for authenticated submissions, which is exactly the traffic you most want traceable. Should include the IP regardless of auth state. |
| **Q4** | STARTTLS accepted before EHLO | **P3 — leave.** Technically out of order (RFC 3207 expects EHLO first) but harmless and tolerant; some clients do this. Keep the pin test. |
| **Q5** | RCPT syntax error → bare `501`, no enhanced status | **P3 — leave.** Cosmetic inconsistency with neighbouring responses. Fix opportunistically. |
| **Q6** | Size counter excludes CRLF; at-limit accepted (`>=`) | **P3 — leave, document.** Means the effective limit is slightly above the configured value. Harmless, but the off-by-CRLF should be noted in the `MessageCharactersLimit` XML doc so operators sizing against a hard downstream limit are not surprised. |
| **Q7** | Overload accident | **See R1 — P2.** Correctly observed and accurately described; the cause (the `WriteCode(int,string)` overload added in `50276ad`, on `master`) is worth recording alongside it. Not an RFC violation — the enhanced status still occupies the text slot — so this is a diagnostics/operability regression to clean up, not urgent work. |
| **Q8** | Delivery `CancellationToken` never fires on client disconnect | **P2 — FIX (or document loudly).** The token is passed to `EmailReceivedAsync` and implies cancellability, but nothing polls the socket while parked in `DeliverMessage` — it only fires at `Dispose`, i.e. after the handler returns. Since delivery is now synchronous within the session, a slow handler on a dead connection holds a thread and a socket indefinitely. Either wire real disconnect detection into `_ts`, or add a server-side delivery timeout. At minimum, correct the `IMailDelivery` XML doc, which currently reads "Cancellation token tied to the client connection" — that promise is not kept. |
| **Q9** | No per-line responses during DATA | **P3 — leave.** RFC-conformant; correctly analysed. |
| **Q10** | Windows/SChannel rejects in-memory `CreateSelfSigned()` certs | **P2 — document.** Not a library bug, but it bites real users on Windows generating certs in memory. Add a note + the PFX round-trip workaround to the README near `SetTLSCertificate`. Cheap, high user value. |
| **Q11** | `zabszk.DnsClient` drops multi-string TXT records | **P1 — FIX (upstream dependency).** Real-world SPF/DMARC records exceeding 255 bytes are split into multiple character-strings; the client parses only the first, so long records look like "no record" → SPF `None`, DMARC fallback. This silently disables policy enforcement for exactly the large senders whose records are long. Fix requires patching/forking `zabszk.DnsClient` 1.0.1, or replacing it. Concatenating the strings per RFC 7208 §3.3 / RFC 1035 is the correct behavior. Track as a dependency decision. |
| **Q12** | SPF DNS-error handling deviates from RFC 7208 | **P1 — FIX.** Three problems in [SpfValidator.cs](CSharp-SMTP-Server/Protocol/SPF/SpfValidator.cs). (a) Top-level NXDOMAIN → `Temperror` where RFC 7208 §4.3 requires `none` ([line 84-85](CSharp-SMTP-Server/Protocol/SPF/SpfValidator.cs#L84-L85)). (b) **The serious one — SPF fails *open* on DNS error for `a`/`mx`.** `CheckAddressMatch` correctly returns `Temperror` when the A/AAAA lookup fails ([line 320-321](CSharp-SMTP-Server/Protocol/SPF/SpfValidator.cs#L320-L321)), but the `a` case discards that distinction: `var result = await CheckAddressMatch(...); if (result != ValidationResult.None) return qualifier;` ([lines 177-189](CSharp-SMTP-Server/Protocol/SPF/SpfValidator.cs#L177-L189)). Since `Temperror != None`, a **DNS failure is treated as a match** and returns the mechanism's qualifier — a bare `a` (implicit `+`) therefore returns **`Pass`** during a DNS outage. A transient resolver failure converts SPF from a control into an authorizer; that is a genuine security fail-open, not a conformance nit. Fix by returning `Temperror` when the inner call returns `Temperror`, and only treating a true match as a match. (c) Redirect to a nonexistent domain → `Temperror`; should be `permerror` per §6.1. |
| **Q13** | `redirect=` evaluated positionally, short-circuits later mechanisms | **P2 — FIX.** RFC 7208 §6.1 requires `redirect` to be consulted **only after all mechanisms have failed to match**, and §4.7 makes it the default-result mechanism. Current code evaluates it in place and returns immediately ([lines 275-287](CSharp-SMTP-Server/Protocol/SPF/SpfValidator.cs#L275-L287)). Mitigating factor the prior model did not note: there *is* a guard skipping `redirect` when an `all` mechanism is present, which covers the most common record shape (`redirect` and `all` are mutually exclusive in practice per §6.1). So real-world impact is lower than stated — but records with mechanisms *after* a `redirect` and no `all` are still evaluated wrongly. Fix by deferring `redirect` handling until after the mechanism loop. |

---

## Part 5 — Documentation corrections

1. **Q7 (TEST_PLAN.md, SUMMARY.md §6).** Accurately describes the symptom. Worth adding the cause — the
   `WriteCode(int,string)` overload from `50276ad` — and a note that it is a fixable defect rather than
   permanent behavior, so the "pinned quirk" label is not read as an endorsement.

2. **SUMMARY.md §4 "Known limitation".** The throwing-filter crash (R6) is recorded in passing. Given it
   is the same process-kill class as B5, it deserves a bug ID and a place in the open-defects list, not a
   footnote.

3. **`IMailDelivery` XML doc.** "Cancellation token tied to the client connection" overstates what Q8
   shows the token actually does. Reword to state it fires on connection teardown, not on client
   disconnect during delivery.

4. **SUMMARY.md §2 and §8 — "NOT pushed" is now false.** Both state the branch is unpushed and list
   `git push origin dev` as remaining work item #1. `dev` and `origin/dev` are both at `fb820ac` — it
   *is* pushed. Drop the stale instruction so nobody treats the branch as private/rewritable; anything
   already published must not be force-rewritten to fix the items in this review.

5. **SUMMARY.md §2 — "Fork diff vs baseline: `git diff 2f7386e..HEAD`".** This is what led my own first
   pass to misattribute R1/R4/R5 to the `dev` branch. `2f7386e` is the original upstream import, so that
   diff spans the pre-`dev` fork commits (`50276ad`, `9dce319`, `3a3dd1c`, `516702a`) as well. For
   reviewing *this branch*, the correct range is `master..dev`. Worth stating both, with their meanings.

6. **VersionConsistencyTests summary** documents the drift it should fail on — see R5.

---

## Suggested order of work

Security-affecting items first; within a tier, cheapest first.

**Tier 1 — security and lifecycle correctness. Do these first.**

1. **B1 + B3** (P0/P2) — make DMARC actually function. The single most consequential finding: DMARC is
   default-enabled and silently bypassed today. Fix together.
2. **Q12(b)** (P1) — SPF fail-open on DNS error. Small change; removes a hole where a resolver outage
   turns SPF from a control into an authorizer.
3. **R11** (P1) — connection escaping `Listener.Dispose()`. Completes the `df4636e` fix and stops a
   live connection outliving certificate disposal.
4. **R6** (P1) — close the remaining `async void` process-kill path (throwing filter). Pairs naturally
   with R11, since both concern connection setup.

**Tier 2 — data integrity.**

5. **Q11, Q1** (P1) — split TXT records (dependency decision on `zabszk.DnsClient`) and DATA
   dot-stuffing. Both silently corrupt real mail.

**Tier 3 — cleanup, any order.**

6. **B2, B4, R7, Q3, Q8, Q12(a/c), Q13** (P2) — correctness cleanups.
7. **R1, R2, R4** (P2) — response text + the 53 pinned assertions; AUTH LOGIN malformed initial response;
   status-code validation. Clear Q2/Q5 while touching R1.
8. **Q10** (P2, docs) — Windows cert workaround in README. Cheap, high user value.
9. **R3, R5, R8, R9, R10** (P3) — formatting, version suffix, test-port risk, timing sleeps, pin traits.
   Only if convenient.

---

## Appendix — how to reproduce the two non-obvious findings

**R1 overload binding** — four-overload repro, no dependencies:

```csharp
class T {
  public static string W(ushort c) => "ushort1";
  public static string W(ushort c, string e) => "ushort2";
  public static string W(ushort c, string e, string t) => "ushort3";
  public static string W(int c, string m) => "int2-SANITIZER";
}
// T.W(250, "2.0.0")  →  "int2-SANITIZER"   (literal int wins; no implicit int→ushort)
// T.W(u,   "2.0.0")  →  "ushort2"          (u declared as ushort)
```

**Provenance of any finding** — confirm whether it is `dev`'s work before assigning it:

```bash
git log --oneline master..dev -- <path>     # empty  → not touched by the dev branch
git blame -L <start>,<end> HEAD -- <path>   # commit that actually introduced the line
```
