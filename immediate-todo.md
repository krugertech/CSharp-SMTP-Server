# Immediate TODO — handover

**Created:** 2026-09-01
**Context:** This fork is being deployed as a **journaling relay for Office 365** (Exchange Online
relays journaled mail here; we archive it). One server instance per Kubernetes pod, each with its own
IP, scaled horizontally — see `SUMMARY.md` §2a.

**The governing constraint for every item below:** a rejected journal report is a compliance record
that no longer exists anywhere. For ordinary mail a 5xx is the sender's problem; here it is permanent
data loss. Simultaneously, the server is internet-adjacent, so it cannot be made unbounded to achieve
that. Every decision below balances those two.

**State:** 343/343 tests green (`$env:DOTNET_ROLL_FORWARD="Major"; dotnet test
CSharp-SMTP-Server.Tests/CSharp-SMTP-Server.Tests.csproj --no-build`). Load/O365 tests live in
`CSharp-SMTP-Server.Tests/Load/`. Nothing below is committed.

---

## 1. Message size limit and the missing SIZE extension

### 1a. Set a finite size limit — not 0

`ServerOptions.MessageCharactersLimit` defaults to **10 MB**, which silently rejects any O365 journal
report above it with `552 5.4.3` — a **permanent** failure, so Exchange does not retry and the record
is lost.

The fix is **not** `0` (unlimited). That would let one hostile or broken client stream unbounded data
into a `StringBuilder` until the pod is OOM-killed. Use a finite ceiling above what O365 can send:

```csharp
MessageCharactersLimit = 200u * 1024 * 1024   // 200 MB; O365 max message is 150 MB
```

**Verified: a finite limit genuinely bounds memory** — this is what makes it a real DoS defense and
not just policy. `TransactionCommands.ProcessData` counts every line but appends to `DataBuilder`
only while `Counter` is within the limit, so over-limit data is discarded as it arrives.

> Measured: **200 MB sent against a 10 MB limit → peak working set ~126 MB** (not ~2 GB), `552` at the
> terminating dot, and the connection stayed usable. Pinned by
> `OverLimitFlood_IsDiscardedNotBuffered_AndConnectionSurvives`.

**Units matter.** `MessageCharactersLimit` counts **characters, excluding CRLF** — not bytes. For the
ASCII/base64 of MIME transport 1 char == 1 byte, so the numbers align in practice. Headroom above
150 MB covers MIME headers, base64 expansion (~4/3 of the raw attachment), and the server's own
prepended `Received:` header. The field is `uint` (max ~4.29 GB), so 200 MB fits fine.

**Caveat — the limit is enforced late.** The 552 is only sent at the terminating `.`, after the client
has transmitted the whole message. It bounds *memory*, not *bandwidth or time*. A client can still
occupy a connection streaming data that will be discarded. If that matters, an early abort when
`Counter` first exceeds the limit is a separate improvement (it deviates from RFC 5321's
read-until-dot expectation, so weigh it deliberately).

### 1b. No SIZE extension advertised (RFC 1870)

EHLO currently returns only:

```
250-<ServerName> at your service
250 8BITMIME
```

No `250-SIZE <n>`. Consequences:

- A sender cannot learn our limit up front, so Exchange discovers an over-limit message only *after*
  transmitting it in full — wasted bandwidth on both sides.
- With `SIZE` advertised, a well-behaved sender that knows the message is too large fails fast and
  reports a useful error instead of burning a full transmission.

O365's `SIZE=`/`BODY=8BITMIME` parameters on `MAIL FROM` are **parsed correctly today** — ignored, not
rejected, because `ProcessAddress` reads only between `<` and `>`. Pinned by
`MailFrom_WithO365EsmtpParameters_IsAccepted` (4 variants incl. `AUTH=<>`).

Note the server also does **not** pre-reject on the declared `SIZE=` value. For journaling that is
arguably desirable (nothing is refused before the data arrives), so if SIZE is implemented, decide
deliberately whether to act on the declared value or only advertise.

**Where:** `ClientProcessor.cs` ~line 286–292 (the EHLO branch).

---

## 2. Two defects found

### 2a. `MAIL FROM:<>` (null sender) is rejected with `501 5.5.2` — **live defect**

RFC 5321 §4.5.5 **requires** the null reverse-path to be accepted. It is what every DSN/bounce uses,
and what some Exchange system-generated reports use. Rejecting it drops those messages permanently.

**Cause:** `TransactionCommands.ProcessAddress` requires a non-empty address containing exactly one
`@` and a dotted domain. `<>` yields an empty address → returns `null` → the `MAIL FROM` branch
answers `501 5.5.2`.

**Fix:** special-case the empty path in the `MAIL FROM` branch *before* calling `ProcessAddress` —
accept `<>` with a null/empty sender and an empty `FromDomain`, and return `250`. Care needed
downstream: `MailTransaction.FromDomain` is used by SPF and by the `Authentication-Results` header, so
confirm the empty-domain path does not NRE with SPF enabled (it is off for journaling, but the library
is shipped to others).

**Test to invert:** `NullSender_IsCurrentlyRejected_KnownGapForJournaling` asserts today's `501`. When
fixed it **fails by design** — change it to assert `250` and add a delivery assertion.

**Note this is a public behavior change** for all consumers of the library, not just this deployment.

### 2b. Memory amplification on large messages — **~11× the message size**

A single 150 MB message drives peak working set to **~1.6–1.9 GB**. Reproduced repeatedly; see item 4
for the cause and the fix. This is the most operationally significant finding in this handover.

Related earlier-known items (unchanged, from `REVIEW.md`): **Q1** no dot-stuffing in DATA
(data-integrity, arguably P1 — pinned by `DotStuffing_IsNotImplemented_PinsQ1`), and **B2**
`AddHeader` duplicating a header before first parse.

---

## 3. Settings you MUST change — all four defaults lose mail

Every one of these produces a **5xx on a well-formed message** at its default value. For a journaling
relay each is silent, permanent record loss.

```csharp
var options = new ServerOptions(validateSPF: false, validateDMARC: false, null)
{
    ServerName             = "journal.example.com",
    MessageCharactersLimit = 200u * 1024 * 1024,  // NOT 0 — see item 1a
    RecipientsLimit        = 0,                    // unlimited
};
```

| Setting | Default | Required | Failure at default |
|---|---|---|---|
| `MessageCharactersLimit` | 10 MB | **200 MB** (finite) | `552 5.4.3` — **permanent**, Exchange will not retry |
| `RecipientsLimit` | 50 | **0** (unlimited) | `550 5.5.3` on the 51st recipient |
| `ValidateSPF` | on | **off** | `554 5.7.23` before DATA is even sent |
| `ValidateDMARC` | on | **off** | `554 5.7.1` |

**Why SPF/DMARC must be off here specifically:** a journal report's envelope sender is the *journaling
mailbox*, not the original sender, so the original message's SPF alignment is irrelevant and can fail
spuriously. They also add a blocking DNS lookup on the session thread.

**Two more, outside `ServerOptions`:**

- **Any `IMailFilter` must not reject.** Every hook (`IsConnectionAllowed`, `IsAllowedSender`,
  `IsAllowedSenderSpfVerified`, `CanDeliver`, `CanProcessTransaction`) can return a failure that
  becomes a 5xx. Prefer no filter at all for the journaling listener.
- **The `IMailDelivery` handler must throw or return `TemporaryFailure` on a backend outage — never
  `Ok`.** Throwing yields `451 4.3.0` (transient), so Exchange queues and retries and the record
  survives. Returning `Ok` acknowledges a message that was never stored — silent loss. Pinned by
  `DeliveryHandlerThrows_YieldsTemporaryFailure_SoExchangeRetries`.

All of the above are encoded in `CSharp-SMTP-Server.Tests/Load/Office365RelayTests.cs` (13 tests), so
a regression fails the build rather than losing mail in production.

---

## 4. Streaming DATA path — the fix for the memory problem

### The problem

**Peak working set is ~11× message size.** One 150 MB message → ~1.6–1.9 GB. Concurrency multiplies
it: 4 × 50 MB concurrently → ~1.9 GB peak. **Pod memory must be sized per _concurrent_ large message,
not per pod** — a pod on a 2 GB limit taking two 150 MB reports at once will be OOM-killed
mid-transaction, losing both.

### Why it costs 11×

In `TransactionCommands.ProcessData` / `MailTransaction`:

1. `DataBuilder` (`StringBuilder`) accumulates the body — and grows by chunked reallocation, so it
   carries slack.
2. `RawBody = DataBuilder.ToString()` — a **second** full copy.
3. `MailTransaction.Clone()` before delivery copies `RawBody` again — a **third**.
4. .NET strings are **UTF-16: 2 bytes per char**, so each copy of a 150 MB ASCII message is 300 MB.
5. `MimeMessage ParsedMessage` re-encodes `RawBody` to a `byte[]` and parses it — more copies, if the
   delivery handler touches any parsed property.

Several full copies coexist at peak, each doubled by UTF-16.

### Also fix here: the size limit does not bound an unterminated line

Found by adversarial review 2026-09-01, **pre-existing and not introduced by the items above.**

`ClientProcessor.Receive` awaits `_reader.ReadLineAsync()`, which materialises a complete line before
`ProcessData` increments `Counter`. A client that connects and streams without ever sending CRLF —
either as a command line or inside DATA — grows the `StreamReader` buffer unbounded and never reaches
`MessageCharactersLimit`. Unauthenticated, on an internet-facing listener.

This qualifies item 1a's claim that a finite limit "genuinely bounds memory": it bounds *terminated
lines*. The `OverLimitFlood` measurement is real but only covers the many-terminated-lines shape.

The streaming rework already touches this exact read path, so fix it there: read with a bounded line
reader, and once the cap is exceeded stop buffering while draining to the terminator (or drop the
connection with a bounded response). Add a test that opens a connection, streams megabytes with no
CRLF, and asserts working set stays bounded.

### Suggested fix

Replace string accumulation with a stream the consumer reads:

- Buffer DATA to a **`Stream`** rather than a `StringBuilder`: small messages to a
  `MemoryStream`/pooled buffer, spilling to a temp file past a threshold (a few MB). Write **bytes**
  as they arrive — no UTF-16 inflation, no repeated reallocation.
- Expose `MailTransaction.GetBodyStream()` and keep `RawBody` as a **lazy, opt-in** property (or
  `[Obsolete]` it) so a handler that only needs to persist the message never materializes it. Feed
  `MimeMessage.Load(stream)` directly — MimeKit is stream-native and this is the shape it wants.
- Avoid the `Clone()` full-body copy: pass the stream handle / a reference, not the bytes.
- Deleting the temp file must be tied to transaction disposal, including the error and
  client-disconnect paths.

**Expected result:** peak memory becomes roughly *O(buffer size)* instead of *O(11 × message)*,
making 150 MB messages viable on a modest pod and removing concurrency as an OOM trigger.

**This is a breaking public API change** (`RawBody` is public and widely used, incl. by our own tests
and `AddHeader`), so it warrants a major version and a migration note. Sequence it with item 2a, which
is also a public behavior change.

**Interim mitigation until this lands:** keep the finite size limit (item 1a), size pods for
≈ 2 GB × expected concurrent large messages, and bound concurrent large transactions ahead of the pod
if possible.

### Verify with the existing harness

The load harness added 2026-09-01 already measures this — see `SUMMARY.md` §11.

```powershell
$env:DOTNET_ROLL_FORWARD="Major"
$env:SMTP_LOADTEST="1"   # heavy tier: concurrency ladder to 500, 150 MB test, sustained 1000-msg run
dotnet test CSharp-SMTP-Server.Tests/CSharp-SMTP-Server.Tests.csproj --no-build --filter "Category=Load"
```

`LargeMessage_150MB_IsAcceptedIntact` prints peak working set on every run — the number to watch while
implementing streaming. Metrics (msgs/sec, **MB/s**, latency percentiles) are written to
`load-metrics.json` next to the test binary; integrity is asserted (SHA-256 per message, verified
non-vacuous by fault injection), throughput is only reported.

---

## 5. Deferred from the 2026-09-01 adversarial reviews

Both found while fixing items 1–3; neither blocks that work, both are real.

### 5a. A null sender is not authenticated in either direction

Accepting `MAIL FROM:<>` (item 2a) means an unauthenticated client can spoof `From:` under a
`p=reject` domain and be accepted. The server cannot distinguish it from a genuine bounce, because:

- RFC 7208 §2.4 defines the null-path MAIL FROM identity as `postmaster@<HELO domain>`, but the
  EHLO/HELO argument is **discarded** — `ClientProcessor` keeps only `_protocolVersion`.
- DKIM is not implemented, so there is no second aligned mechanism.

Delivering was chosen deliberately: applying the policy destroys legitimate bounces, which for
journaling is unrecoverable, and the gap is unreachable there because DMARC is off. **The fix** is to
retain the EHLO/HELO argument on `ClientProcessor`, thread it into `MailTransaction`, run the §2.4
check against it, and align that domain in `DmarcValidator`. When it lands,
`NullSender_WithSpoofedFrom_UnderDmarcReject_IsAccepted_KnownLimitation` **fails by design** and
should be inverted to assert `554`.

### 5b. Quoted local-parts containing angle brackets are rejected

Pre-existing, not introduced by items 1–3. `TryGetBracketedPath` (and `ProcessAddress` before it)
treats the first `>` as the path terminator with no awareness of RFC 5321 quoted-strings, so valid
addresses like `<"a>b"@example.com>` and `<"a<b"@example.com>` get a permanent `501`. Shared by
RCPT TO, so it can lose a recipient as well as a sender.

Low priority for this deployment — O365 journal envelopes do not use quoted local-parts — but it is a
permanent rejection of a legitimate address. The fix is a quote- and escape-aware scanner that
recognises the closing bracket only outside a quoted-string.

---

## Suggested order

1. **Item 3** — configuration only, zero code change, stops the bleeding immediately.
2. **Item 1a** — set the finite limit as part of that same config.
3. **Item 2a** — null sender; small, well-understood, test already written and waiting to be inverted.
4. **Item 1b** — advertise SIZE; small and independent.
5. **Item 4** — streaming DATA path; the largest change, breaking, and best done deliberately with the
   load harness watching the memory number.
