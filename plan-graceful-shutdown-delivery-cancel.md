# Plan: delivery timeout

## Scope

One task: an opt-in deadline after which a delivery is answered `451` instead of being accepted.
Roughly 30 lines, one call site, one new option field, default off.

Two items that were originally in this plan have been **dropped and are not to be revived here**:

| Dropped | Why |
| --- | --- |
| Graceful shutdown drain | Out of scope for this deployment. Would have required splitting `Listener.Dispose()` — the most race-sensitive code in the repository — and fixing two latent concurrency defects to make it safe. |
| Peer-disconnect socket poll | `Poll(SelectRead) && Available == 0` is unsound: unreliable under TLS, produces false positives on a half-close, defeated by pipelining, and races the reader. Green tests would not have made it correct. |

**Do not extend this work into `Listener.cs` or `SMTPServer.Dispose()`.** They are deliberately
untouched.

Q8 in [`KNOWN_ISSUES.md`](KNOWN_ISSUES.md) is **mitigated** by this timeout, not fixed. A real fix
needs TLS-aware stream-level EOF handling with tests for `close_notify`, half-close, buffered
pipelining, FIN and RST — a much larger piece of work, not currently justified.

## Prerequisites — check both before building

**1. The delivery handler must observe its cancellation token.** A handler that ignores it will not
return any sooner, and the session still blocks. Confirm the target `IMailDelivery` implementation's
write path is cancellation-aware; if it is not, this option buys nothing and the work should not
start.

**2. Delivery storage must be idempotent or deduplicating.** This is a hard prerequisite for
*enabling* `DeliveryTimeout`, not a nicety. A handler can durably commit the message, observe
cancellation only afterwards, and return `Ok` — which section 3 deliberately discards in favour of
`451`. The sender then retries a message that is already stored. The deadline cannot distinguish
"committed then cancelled" from "never committed", so enabling this option **introduces a
commit/ACK ambiguity that only the consumer can resolve**.

The same requirement is already recorded independently in
[`KNOWN_ISSUES.md`](KNOWN_ISSUES.md) under graceful shutdown. This deployment's ingestor
deduplicates, so the prerequisite is met — but it must be stated, because a deployment without it
would be made *worse* by this feature, not better.

## Verified starting point

Read from the tree at `e97aad9`, not from the docs:

- Delivery is awaited at exactly **one** call site,
  [`TransactionCommands.cs:412`](CSharp-SMTP-Server/Protocol/Commands/TransactionCommands.cs#L412):
  `await processor.Server.DeliverMessage(delivery, processor.ConnectionToken)`, wrapped in
  `try/catch/finally`. The `catch` maps a throwing handler to `451 4.3.0`; the `finally` disposes
  `delivery.Body`. `SMTPServer.DeliverMessage` is a one-line pass-through to
  `MailDeliveryInterface.EmailReceivedAsync`.
- The `finally` closes at line 429; the success response is written at
  [line 431](CSharp-SMTP-Server/Protocol/Commands/TransactionCommands.cs#L431), *after* it. Disposing
  a linked source in that `finally` is therefore safe — it happens after the catch-path writes and
  before the success write, and it does not touch `_ts`.
- `ClientProcessor` owns `_ts` (`CancellationTokenSource`) and exposes `_t` as `ConnectionToken`.
  `Dispose(bool dontRemove, bool reset)` cancels `_ts` and then closes `_stream`, `_innerStream` and
  `_client`.
- [`ClientProcessor.WriteText`](CSharp-SMTP-Server/Networking/ClientProcessor.cs#L399-L418) does
  `await _stream.WriteAsync(encoded, _t)` — the **connection token**. A response written after `_ts`
  is cancelled throws `OperationCanceledException`, which is *not* caught by its `catch (IOException)`;
  it falls to the generic handler and is logged as a client-write fault. It also swallows
  `IOException`, which is what makes a best-effort write to a vanished peer harmless.
- The receive loop runs the DATA path inline, so it does not return until the delivery handler
  completes. A peer FIN/RST is **not observed** while delivery is awaited.
- `ServerOptions` is a public-field options class. Adding a field matches its existing style.

Baseline: `dotnet build` clean (21 warnings, all in the test project); `dotnet test` **541 passed,
0 failed**.

---

## 1. Add the option

```cs
/// <summary>
/// Deadline for the delivery handler. When it expires, the handler's cancellation token is
/// cancelled and the message is answered <c>451 4.4.7</c> regardless of what the handler
/// subsequently returns.
/// <para>
/// This bounds whether the message is <i>accepted</i>, not how long the session lasts: a handler
/// that does not observe its cancellation token is still awaited to completion, because the
/// message body must stay alive until it returns.
/// </para>
/// <see cref="TimeSpan.Zero"/> disables the deadline.
/// Default: <see cref="TimeSpan.Zero"/> (disabled).
/// </summary>
public TimeSpan DeliveryTimeout = TimeSpan.Zero;
```

Reject a negative value at the point of use. Document the cooperative-token requirement here and on
`IMailDelivery`.

## 2. Link the token at the call site

- **`DeliveryTimeout == Zero`** → pass `processor.ConnectionToken` unchanged. No linked source, no
  new classification, no allocation. This path must remain today's code exactly.
- **Otherwise** → `CancellationTokenSource.CreateLinkedTokenSource(processor.ConnectionToken)`, then
  `CancelAfter(DeliveryTimeout)`, pass `linked.Token`, and dispose the linked source in the existing
  `finally`.

## 3. The deadline is authoritative over the handler's result

The important correctness point, and the one an obvious implementation gets wrong.

A cancellation token is **advisory**. A handler may observe cancellation and still return normally —
`IMailDelivery` explicitly permits returning `TemporaryFailure`, and a handler may equally catch the
cancellation and return `Ok`, or simply finish late. If the timeout is inferred only from a thrown
`OperationCanceledException`, that returned result is written at line 431 and **a `250` is sent after
the deadline expired**. The option would then silently not do what its name says.

So record the deadline as a fact rather than inferring it:

1. **Snapshot the configured timeout once** into a local, and compute a monotonic deadline from
   `Stopwatch.GetTimestamp()` (never `DateTime.Now`, which moves under clock changes). The field is
   public and mutable, so re-reading it later could compare against a different value than the one
   `CancelAfter` was given.
2. **Continue awaiting the handler.** Do not abandon the await: `delivery.Body` is disposed in the
   `finally`, and returning early would dispose it while the handler may still be reading. This is
   the reason the deadline cannot bound wall-clock session time.
3. **After the await returns — and before the `finally` disposes the linked source — settle the
   classification** into a plain local variable, in this order: `ConnectionToken` cancelled first,
   then the deadline. Decide from the elapsed monotonic time against the snapshotted deadline, not
   from `linked.Token.IsCancellationRequested` alone.
4. **Use the settled classification, not the handler's result.** If the deadline won, answer
   `451 4.4.7` and discard whatever the handler returned, including `Ok`.

**Do not use `linked.Token.Register(...)` to set a flag.** Cancellation callbacks may run
synchronously, and ordering between the handler's own registration and ours is not guaranteed: the
handler's callback can complete its task and resume the await before our callback runs, leaving the
flag false and letting an `Ok` through to line 431. It is also not a wall-clock authority —
`CancelAfter` can fire late under scheduler starvation. Classification must be settled by inspecting
state after the await, never by a callback racing it.

Resulting behavior, all gated behind `DeliveryTimeout != Zero`:

| Situation | Response |
| --- | --- |
| Handler completes before the deadline | Its own result (unchanged) |
| Deadline fired — handler threw, or returned any result including `Ok` | `451 4.4.7`, logged as a delivery timeout |
| `ConnectionToken` cancelled (local teardown observed) | No write — the socket is closed |
| Any other exception | `451 4.3.0` (unchanged) |

Check `ConnectionToken` first: when both fire, local teardown is the more specific truth.

`451` is deliberate — a temporary failure invites a retry, which is the correct direction for a
compliance record.

## 4. The `451` is best-effort

`_ts` being uncancelled proves only that *this server* has not torn the connection down. It does not
prove the peer is still there — a remote FIN/RST is not observable while delivery is awaited. So the
`451 4.4.7` write may target a peer that has already gone.

That is acceptable and needs no detection: `WriteText` swallows `IOException`, so the write fails
harmlessly. Document the response as best-effort, and **do not** assert in tests that no write is
attempted after a remote disconnect — assert that no unhandled failure results. Only *locally
observed* teardown (`ConnectionToken` cancelled) takes the no-write branch.

## Tests

- Slow handler + short deadline → `451 4.4.7`; handler's token observed cancelled.
- **Handler catches cancellation and returns `Ok` after the deadline → `451 4.4.7`, not `250`.**
  The regression test for section 3; without it the defect is invisible.
- **Handler whose own cancellation callback completes its task inline and returns `Ok` → still
  `451 4.4.7`.** Pins the callback-ordering race specifically: a synchronous cancellation callback
  must not be able to resume the await ahead of classification. Make it deterministic by having the
  handler register a callback that completes its `TaskCompletionSource` inside the callback itself.
- Handler returns `TemporaryFailure` after the deadline → `451 4.4.7`.
- Handler completes inside the deadline → its own result, no cancellation, no linked-source leak.
- `DeliveryTimeout = Zero` + slow handler → completes, `250`. Pins the opt-in default.
- `DeliveryTimeout = Zero` + connection cancelled → today's behavior exactly (logged, `451 4.3.0`
  attempted). Guards the compatibility claim in section 2.
- Remote disconnect during a slow delivery → no unhandled exception on the `_ = Receive()` task.
  Do **not** assert that no write occurred.
- Local teardown during delivery → no write attempted.
- Handler that ignores cancellation → assert the observed behavior, documenting that the session is
  not bounded.
- Negative `DeliveryTimeout` → rejected.

## Risk

Low, and deliberately so:

- One call site, one field, no new types, no threads, no locks, no concurrency.
- `DeliveryTimeout == Zero` is today's code path, so the existing 541 tests are a genuine regression
  net.
- `Listener.cs` and `SMTPServer.Dispose()` are untouched.

The worst realistic failure is **a duplicate, not a harmless retry**: a handler that commits and
*then* observes cancellation returns `Ok`, the deadline discards it, `451` goes out, and the sender
resends a message already in the archive. Nothing is lost — the failure direction is still safe for a
compliance system — but the duplicate is real and is absorbed by the consumer's deduplication, which
is why prerequisite 2 is mandatory rather than advisory.

The known limitation — a handler ignoring its token is still awaited — is inherent to keeping the
message body alive, not a defect to be fixed later. Document it; do not engineer around it.

## Sequencing

1. Confirm the prerequisite above (handler observes cancellation).
2. Sections 1–4 + tests. Full `dotnet test` green.
3. Documentation: `DeliveryTimeout` in the README journaling profile; `KNOWN_ISSUES.md` — record Q8
   as mitigated-by-timeout rather than fixed.

## Out of scope

- Any change to `Listener.cs` or `SMTPServer.Dispose()`.
- Bounding wall-clock session time (see section 3, step 2).
- Retry or queueing inside the server. It is a receiver; the sender retries.
- Any change to the SPF/DMARC paths — disabled in the journaling profile.

## Separately: two defects worth filing

Surfaced by review of the dropped drain design. Both are real in the code today and **independent of
this plan**. They belong in [`KNOWN_ISSUES.md`](KNOWN_ISSUES.md) as their own entries, not in this
work:

- **Concurrent processor disposal — reachable today, not latent.**
  [`ClientProcessor.Dispose`](CSharp-SMTP-Server/Networking/ClientProcessor.cs#L602-L603) guards with
  a non-atomic `if (_dispose) return; _dispose = true;`. Two callers can both pass it: the receive
  path calls `Dispose(false, true)` after a write `IOException`
  ([`ClientProcessor.cs:409`](CSharp-SMTP-Server/Networking/ClientProcessor.cs#L409)) while
  `Listener.Dispose` is iterating its snapshot of the same processor. One can then set `LingerState`
  or touch the streams after the other has closed them, throwing `ObjectDisposedException`; because
  the listener's `foreach` does not isolate per-processor failures, that throw aborts teardown of
  every remaining session. **Concrete trigger: a remote disconnect during delivery coinciding with a
  deployment shutdown** — routine in a Kubernetes rollout. An earlier draft of this plan called it
  unreachable; that was wrong.
  Fix: `Interlocked.Exchange` for disposal ownership, plus a try/catch around each processor in the
  listener's teardown loop.
- **Unsynchronized listener list.**
  [`SMTPServer.AddListener`](CSharp-SMTP-Server/SMTPServer.cs#L208-L215) mutates the `_listeners`
  `List` while `Dispose` enumerates it.
