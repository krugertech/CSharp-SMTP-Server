# Immediate TODO — handover

**Created:** 2026-09-01
**Context:** This fork is being deployed as a **journaling relay for Office 365** (Exchange Online
relays journaled mail here; we archive it). One server instance per Kubernetes pod, each with its own
IP, scaled horizontally — see `SUMMARY.md` §2a.

**The governing constraint for every item below:** a rejected journal report is a compliance record
that no longer exists anywhere. For ordinary mail a 5xx is the sender's problem; here it is permanent
data loss. Simultaneously, the server is internet-adjacent, so it cannot be made unbounded to achieve
that. Every decision below balances those two.

**State (updated 2026-09-01):** **everything in this document is done.** Items 1, 2a, 3 and 4 landed
first; item 4's result is a 150 MB message going from ~1900 MB of peak working set to no measurable
growth. The final pass closed **Q1 (dot-unstuffing) together with byte-exact DATA**, **5b (quoted
local-parts)** and **5a (the HELO identity)** — with one deliberate deviation from what 5a proposed,
recorded below.

**442/442 tests green**, plus 66/66 on the heavy load tier
(`$env:DOTNET_ROLL_FORWARD="Major"; $env:SMTP_LOADTEST="1"; dotnet test
CSharp-SMTP-Server.Tests/CSharp-SMTP-Server.Tests.csproj`). Load/O365 tests live in
`CSharp-SMTP-Server.Tests/Load/`; the streaming path's own tests are in
`CSharp-SMTP-Server.Tests/StreamingBodyTests.cs`.

---

## 1. Message size limit and the missing SIZE extension

### 1a. Set a finite size limit — not 0

`ServerOptions.MessageCharactersLimit` defaults to **10 MB**, which silently rejects any O365 journal
report above it with `552 5.4.3` — a **permanent** failure, so Exchange does not retry and the record
is lost.

The fix is **not** `0` (unlimited). That would let one hostile or broken client stream unbounded data
at the server until it exhausts memory or the disk bodies spill to. Use a finite ceiling above what
O365 can send:

```csharp
MessageCharactersLimit = 200u * 1024 * 1024   // 200 MB; O365 max message is 150 MB
```

**Verified: a finite limit genuinely bounds storage** — this is what makes it a real DoS defense and
not just policy. `TransactionCommands.ProcessData` counts every line but writes to the body store
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

### 2b. Memory amplification on large messages — **~11× the message size** — **FIXED**

A single 150 MB message drove peak working set to **~1.6–1.9 GB**. Reproduced repeatedly; this was the
most operationally significant finding in this handover. **Fixed by item 4** — the working set no longer measurably grows for such a message.

Related earlier-known items (from `REVIEW.md`): **Q1** no dot-stuffing in DATA (data-integrity,
arguably P1 — pinned by `DotStuffing_IsNotImplemented_PinsQ1`) — **still open**; and **B2** `AddHeader`
duplicating a header before first parse — **fixed as a side effect of item 4**, its pinning test
inverted to assert one copy.

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

## 4. Streaming DATA path — the fix for the memory problem — **DONE 2026-09-01**

**Result: a 150 MB message went from ~1900 MB of peak working set to no measurable working-set
growth; 4 × 50 MB concurrently likewise.**
Both are now asserted by heavy-tier tests that print the figure on every run
(`LargeMessage_150MB_IsAcceptedIntact`, `ConcurrentLargeMessages_DoNotMultiplyMemory`). 407/407 tests
green, including the full heavy load tier. Pod sizing no longer scales with concurrent large messages.

Implementation notes are at the end of this item; the analysis below is kept as the record of why.

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
if possible. ~~Needed~~ — superseded; the streaming path landed, see below.

### What was actually built

- **`MessageBody`** (new, `CSharp-SMTP-Server/MessageBody.cs`) — byte-backed body store. In memory
  below 4 MB, spills to a temp file above it, opened `DeleteOnClose` so a pod killed mid-transaction
  cannot leave the file behind. Prepended headers are *recorded*, not written, and spliced in ahead of
  the body by the read stream, so `AddHeader` on a 150 MB message costs the header rather than a
  rewrite.
- **`MailTransaction`** — `RawBody` is now a lazy property over the store (still works, still returns
  the same text); `GetBodyStream()` and `BodyLength` are the new stream-native API. `Clone()` shares
  the store instead of copying it, and the parsed-`MimeMessage` cache moved onto the store so the
  clone still shares the parse — lazily, without `Clone()` forcing one. `ParsedMessage` now loads from
  the stream, so MimeKit never sees a re-encoded string.
- **`BoundedLineReader`** (new, `Networking/BoundedLineReader.cs`) — replaces `StreamReader` in the
  receive loop and closes the unterminated-line hole described above. 1 MB cap per line, truncate and
  drain past it, stateful UTF-8 decoder so multi-byte characters survive read boundaries.
- **Lifetime** — `ClientProcessor.DiscardTransaction()` is the single abandon path (over-limit, policy
  rejection, RSET, EHLO/HELO reset, connection teardown); the delivery path hands the store to the
  clone, which disposes it in a `finally` so a throwing handler (the 451/retry path) does not leak a
  file per message for the length of an outage. Disposal is deliberately a **no-op for a body that
  never spilled**, so retaining a small transaction past delivery — which consumers and this suite's
  own fakes do — keeps working.

**Two related defects fixed in passing:** B2 (`AddHeader` duplicating a header before first parse) is
gone as a structural consequence — its pinning test is inverted to assert one copy. And a transaction
rejected at the terminating dot is now reset rather than left live on the connection, which was both
wrong and a temp-file leak.

**Follow-up — DONE 2026-09-01.** The DATA path decoded each line to a string before writing it back out
as UTF-8, so a byte-exact round trip was not guaranteed and dot-unstuffing (Q1) was unimplemented.
Both are now fixed; see item 6 below, which explains why they were one change rather than two.

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

## 5. Deferred from the 2026-09-01 adversarial reviews — **BOTH DONE 2026-09-01**

Both found while fixing items 1–3; neither blocked that work, both were real.

> **5a landed with a deliberate deviation.** The SPF half was implemented as specified. The DMARC half
> — "align that domain in `DmarcValidator`" and invert the pinning test to assert `554` — was **not**,
> because doing so destroys legitimate bounces. See the verdict at the end of 5a.

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

#### What was built, and what was not — **verdict 2026-09-01**

**Done: the SPF half.** `ClientProcessor` retains the EHLO/HELO argument (`ParseHeloDomain`, keeping
only a plausible DNS name), `MailTransaction` carries it as the new public `HeloDomain` preserved
across `Clone()`, and the `MAIL FROM` branch runs the §2.4 check against it. A HELO domain publishing
`-all` is now enforced against a null sender — a new `554 5.7.23` path, reachable only with
`ValidateSPF` on. `Authentication-Results` reports `smtp.helo=` rather than an empty `smtp.mailfrom=`,
per RFC 8601 §2.7.2. A client greeting with an address literal has no checkable identity and stays
unchecked, so this is not a new rejection for MTAs that greet that way.

**Not done, on purpose: the DMARC half.** Aligning the HELO domain against `RFC5322.From` is *not*
what DMARC specifies, and doing it destroys ordinary bounces.

- **The spec.** RFC 7489 §4.1: *"DMARC uses the result of SPF authentication of the MAIL FROM
  identity."* For the null case it defers to RFC 7208 §2.4 rather than defining a DMARC behaviour of
  its own. §2.4 substitutes `postmaster@<HELO domain>` for the SPF **check** — that does not make it a
  DMARC **alignment** identity, and nothing in RFC 7489 authorizes treating it as one.
- **The consequence, measured not predicted.** Implementing it as written turned
  `NullSender_WithAlignedFromHeader_UnderDmarcReject_IsDelivered` red: a bounce carrying
  `From: postmaster@example.com` under `example.com`'s own `p=reject` was refused with `554`. A
  bouncing MTA greets with **its own hostname** (`mail-out-3.provider.example`), which routinely
  differs from the From domain of the notification it carries, so requiring the two to align refuses
  legitimate DSNs generally — not a test artifact. That is precisely the permanent, unrecoverable loss
  this whole document is organized around preventing.

So `NullSender_WithSpoofedFrom_UnderDmarcReject_IsAccepted_KnownLimitation` **still asserts `250`** and
was not inverted. What changed is its cause, and its rationale has been rewritten accordingly: the
§2.4 check now constrains *who* may send a null path, but it says nothing about the `From:` header.

**The gap that remains, and what would close it.** An unauthenticated client can still send
`MAIL FROM:<>` with a spoofed `From:` under a `p=reject` domain and be accepted. Closing it needs an
aligned identity DMARC actually recognizes — which means **DKIM**, not implemented in this fork (the
upstream `dkim` branch was deliberately not merged; see `ARCHITECTURE.md` §9). When DKIM lands, that
test should fail and be inverted to assert `554`. Unreachable for this deployment, where DMARC is off.

### 5b. Quoted local-parts containing angle brackets are rejected

Pre-existing, not introduced by items 1–3. `TryGetBracketedPath` (and `ProcessAddress` before it)
treats the first `>` as the path terminator with no awareness of RFC 5321 quoted-strings, so valid
addresses like `<"a>b"@example.com>` and `<"a<b"@example.com>` get a permanent `501`. Shared by
RCPT TO, so it can lose a recipient as well as a sender.

Low priority for this deployment — O365 journal envelopes do not use quoted local-parts — but it is a
permanent rejection of a legitimate address. The fix is a quote- and escape-aware scanner that
recognises the closing bracket only outside a quoted-string.

#### What was built — **DONE 2026-09-01**

There were **two** quote-blind checks, not one. `TryGetBracketedPath` took the first `>` as the
terminator, and `ProcessAddress` separately required exactly one `@` anywhere in the address — so
`<"a@b"@example.com>` was refused even once the bracket scan was fixed. Both are now quote-aware
(`FindPathEnd`, `ContainsUnquoted`, `LastIndexOfUnquoted`), honouring RFC 5321 quoted-pairs so a
backslash-escaped quote does not end the string. `GetAddressDomain` got the same treatment, since
MimeKit hands back a quoted local-part with its quotes intact.

**The security interaction, checked rather than assumed.** This is the anchored path locator that
closes the null-reverse-path smuggling differential (`"AUTH=<> <ceo@victim.example>"` and friends).
Making it quote-aware is exactly the kind of change that could reopen it — if a `>` inside quotes is
ignored, an attacker who opens a quote controls where the parser thinks the path ends. Two rules keep
it shut, and both are pinned: an **unterminated** quoted-string yields no path at all rather than
falling back to the first `>`, and a quoted-string in the **domain** is refused (it is a local-part
construct only, and a trailing quote there could hide the dot the domain check relies on). Every
existing smuggling case is re-asserted directly against the new scanner.

---

---

## 6. Byte-exact DATA and dot-unstuffing (Q1) — **DONE 2026-09-01**

Carried over as item 4's "follow-up not taken". These were **one change, not two**: the same line of
code caused both, and fixing Q1 on the old string-based path would have meant rewriting it immediately
afterwards.

### The shared cause

`BoundedLineReader` decoded every line to a .NET string, and `MessageBody.WriteLine` re-encoded it as
UTF-8 into the body store. That round trip is where both defects lived.

**Byte-exactness.** A message body is an octet stream — any charset, or an unlabelled 8-bit body — and
the transcode replaced every byte that was not valid UTF-8 with U+FFFD, stored as `EF BF BD`. The
archive did not hold what the sender transmitted.

**Dot-unstuffing (Q1).** RFC 5321 §4.5.2 requires the receiver to strip the transparency dot a sending
client prefixes to any body line already beginning with one. A composed line `.text` was archived as
`..text`.

### Why this mattered more than the original REVIEW.md framing suggested

`REVIEW.md` classified Q1 as a data-integrity bug affecting real mail — correct, but it predates the
decision to rely on **DKIM** to prove a message was not tampered with between sender and receiver.
That makes both defects signature-breaking, and reframes Q1 from "corrupts some lines" to "produces an
archive that cannot be verified":

- DKIM signs the body as the **sender** composed it. Dot-stuffing is *transport* encoding applied by
  the sending SMTP client and required to be removed by the receiver — it is not part of the signed
  content. Storing the stuffed form preserved the **wire** bytes, not the sender's, so verification
  fails on any message with a leading-dot body line.
- The UTF-8 transcode fails a signature over any body that is not already valid UTF-8, for the same
  reason: a verifier hashes the octets it is given.

Neither is detectable downstream as anything but tampering.

### What was built

- `BoundedLineReader` is **byte-primary**: lines accumulate as wire bytes and decode to text only when
  a caller asks. `ReadLineBytesAsync` hands back a **borrowed** buffer, so the DATA path does not
  allocate an array per body line — for a 150 MB message that is millions of arrays the streaming work
  exists to avoid.
- The stateful UTF-8 `Decoder` is gone with the char buffer. Decoding per line is equivalent, not a
  regression: a multi-byte sequence can straddle a socket read (which is what a stateful decoder is
  for) but **cannot straddle a line terminator**, since no byte of one can be `0x0A`.
- `ProcessData` takes bytes and strips the stuffing dot as a one-byte offset. The line is counted
  against `MessageCharactersLimit` **after** unstuffing, so the enforced limit measures the message as
  stored rather than as framed — otherwise the effective limit would depend on how many body lines
  happened to start with a dot.
- The terminating dot is matched on **bytes**, so a stuffed `..` stays body content instead of
  truncating the message. Unstuffing before that comparison would have turned every literal `.` line
  into an end-of-message.

### The archive-format boundary

A body line beginning with a dot is now stored unstuffed, so the same message hashes differently
across this change. Reviewed before landing and judged correct: messages archived *before* it are the
ones that do not match what their sender signed. The load corpus deliberately contains no leading-dot
lines, so no existing corpus hash moved.

**Two pinning tests inverted** (`DotStuffing_IsNotSupported_BodyLinesStoredVerbatim`,
`DotStuffing_IsNotImplemented_PinsQ1`), plus new coverage for a stuffed lone dot as body content and
for non-UTF-8 bytes surviving to the delivery handler byte-exact.

---

## Suggested order

1. ~~**Item 3** — configuration only, zero code change, stops the bleeding immediately.~~ **Done.**
2. ~~**Item 1a** — set the finite limit as part of that same config.~~ **Done.**
3. ~~**Item 2a** — null sender; test inverted to assert `250`.~~ **Done.**
4. ~~**Item 1b** — advertise SIZE.~~ **Done.**
5. ~~**Item 4** — streaming DATA path.~~ **Done 2026-09-01** — 150 MB: ~1900 MB → no measurable growth;
   4 × 50 MB concurrent: likewise. Both pinned by heavy-tier tests that assert growth stays below the
   message size itself.

6. ~~**Item 6** — byte-exact DATA + dot-unstuffing (Q1), as one change.~~ **Done 2026-09-01.**
7. ~~**Item 5b** — quoted local-parts.~~ **Done 2026-09-01.**
8. ~~**Item 5a** — HELO identity retained and SPF-checked per RFC 7208 §2.4.~~ **Done 2026-09-01**,
   SPF half only — the DMARC half was deliberately not implemented; see the verdict in 5a.

**Remaining: nothing in this document.** 442/442 green, plus 66/66 on the heavy load tier.

**Carried forward, out of scope here:**

- **DKIM** is the one open thread with a named consequence. It is what the archive's tamper-evidence
  rests on, and it is also the only thing that would close 5a's remaining DMARC gap. Not implemented
  in this fork; the upstream `dkim` branch was deliberately not merged (`ARCHITECTURE.md` §9). Note
  that this server *verifying* DKIM and the archive *being verifiable* downstream are different jobs —
  item 6 was needed for the second regardless of whether the first ever lands.
- The `REVIEW.md` backlog (Q11 split TXT records, Q12(a)/(c), Q13 `redirect=` ordering, Q10 docs) is
  untouched by this document and still open.
