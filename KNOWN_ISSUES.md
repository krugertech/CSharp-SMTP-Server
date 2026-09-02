# Known issues and pending work

This document is the current backlog for the Krugertech fork. Completed changes belong in
[`CHANGELOG.md`](CHANGELOG.md); implementation details belong in
[`ARCHITECTURE.md`](ARCHITECTURE.md).

The identifiers in parentheses are retained because regression tests and older commits use them.

## Production decisions

### Graceful shutdown and duplicate delivery

`SMTPServer.Dispose()` stops listeners and terminates active sessions; it does not drain them. With
ACK-gated delivery, shutdown can occur after a handler commits a message but before the sender
receives `250`. The sender will retry, so delivery storage must be idempotent or deduplicate messages.

Pending decision: implement a drain mode that stops accepting, waits for in-flight sessions within
the Kubernetes termination grace period, and then disposes the server. The host must call
`Dispose()` during `SIGTERM` handling even if idempotency is considered sufficient.

### Delivery cancellation does not detect an idle peer disconnect (Q8)

The token passed to `EmailReceivedAsync` is cancelled when the server tears down the connection.
While the handler is running, the receive loop is awaiting that handler and does not poll the socket,
so a remote disconnect alone may not cancel the token until the handler returns and the response
write fails.

Pending work: add independent disconnect detection or a configurable delivery timeout. Handlers
should enforce their own timeout until then.

### No DKIM verification

The server preserves DATA bytes and reverses SMTP dot-stuffing, so a downstream archive can verify
DKIM signatures. The server itself does not verify DKIM. This also means SPF is the only
authentication mechanism available to DMARC for null reverse-path messages.

The unfinished upstream `dkim` branch was deliberately not merged. Treat DKIM verification as a
future feature, not an advertised capability.

`Integrity/DkimSurvivalTests` signs messages with a test key and verifies them after delivery. That
is a test fixture and does not change the above: nothing in `CSharp-SMTP-Server` signs, verifies, or
references DKIM, and the `IDkimPublicKeyLocator` those tests use lives in the test assembly.

### Archived DKIM signatures cannot necessarily be re-verified later

The server preserves signed octets, so a signature that arrived valid is still valid in the archive.
Verifying it *later* is a separate problem: DKIM verification needs the signing domain's public key,
fetched from DNS at the selector named in the signature. Selectors are rotated and retired, so a key
looked up months after receipt may differ from the one that signed the message, or be gone.

This matters wherever DKIM is the evidence of who sent a message — its value in a dispute depends on
being able to re-verify, not merely on having verified once. Nothing in the library addresses it,
and `Integrity/DkimSurvivalTests` does not cover it: those tests verify at delivery time with the key
in memory.

Pending decision, for the deployment rather than the library: capture the verification result and/or
the public key at receipt, alongside the message. This is a consumer concern — the delivery handler
sees the message and can resolve the key while it is still published — so it may not warrant a
library change at all.

## SPF and DMARC

These items matter only when SPF or DMARC validation is enabled. The Office 365 journaling profile in
the README disables both.

### Split TXT records dropped by the DNS dependency (Q11) — fixed

`zabszk.DnsClient` 1.0.1 did not concatenate the character-strings in a multi-string TXT record, so
a long SPF or DMARC record appeared absent — SPF `None`, or no DMARC policy found. Since real SPF
records exceeding 255 bytes are always published this way, this hid exactly the provider
include-chains this relay sees most.

Fixed by the DnsClient.NET replacement, which concatenates per RFC 7208 §3.3. Covered by
`SpfValidatorTests.MultiStringTxtRecord_IsConcatenated_AndEvaluated`.

### The DNS dependency is unmaintained and cannot honor record TTLs — fixed

`zabszk.DnsClient` 1.0.1 has been replaced by DnsClient.NET 1.8.0. It had no cache at all
(`DnsClientOptions` exposed no cache or TTL setting), dropped multi-string TXT records, and its last
published target was `net7.0`, resolved by roll-forward since the `net10.0` retarget.

Resolution: SPF and DMARC now resolve through `IDnsResolver` (`Protocol/Dns/`), backed by
DnsClient.NET with a TTL-aware in-process cache. `DnsResolverCacheTests` proves the cache by query
count against the stub — a repeated lookup within TTL issues no second wire query, and an SPF
include chain resolves once across repeated evaluations rather than per message.

### The positive DNS response cache has no entry-count bound

DnsClient.NET's own cache is keyed by query and bounded only in *time*. Sending domains are chosen by
whoever connects, so cache keys are attacker-influenced.

Verified rather than assumed, against the package's shipped `DnsClient.xml`: the entire cache
configuration surface is `UseCache`, `MinimumCacheTimeout`, `MaximumCacheTimeout`,
`CacheFailedResults`, `FailedResultsCacheDuration`, and a read-only `QueryCache()` lookup. Every one
is a duration or a boolean — there is no count, size, or eviction setting, and the backing store is a
`ConcurrentDictionary`. `MaximumCacheTimeout`'s own documentation confirms its scope: it "can override
the TTL of a resource record in case the TTL of the record is higher than this maximum value", which
clamps retention and says nothing about entry count.

Confirmed by test as well: after 6000 distinct resolving names, the first entry is still cached.
Nothing evicts.

This applies to **positive** answers only, and exploiting it takes more than junk traffic: an
attacker needs each made-up name to actually resolve, which means controlling a zone with wildcard
records. A flood of nonexistent domains produces negative answers, and those go to the resolver's own
negative cache, which *is* hard-bounded at 4096 entries.

`DnsClientResolver` caps `MaximumCacheTimeout` at 5 minutes so the positive working set is bounded by
connection rate rather than by uptime. That is a mitigation, not a fix: the ceiling trades some cache
effectiveness for a bounded window, and a sustained flood of resolvable unique names can still grow
memory within it. A hard bound would need an eviction policy the library does not expose — a bounded
LRU in front of it, or per-connection DNS admission control.

**What has not been measured**, so severity here is reasoned rather than established: the wildcard-zone
attack has not been run end-to-end through SMTP under load, and per-entry memory cost has not been
measured, so the point at which growth becomes operationally painful is unknown. The unbounded-growth
mechanism itself is proven; the exploitability argument above rests on the attacker needing a
resolvable zone, which is a higher bar than sending junk but not a high one.

Negative answers and transient failures are handled by the adapter rather than the library, because
DnsClient.NET lumps them together under `CacheFailedResults` and they need opposite treatment:

- **Transient failures are never cached.** SPF reports `Temperror` and DMARC defers on it, so a cached
  SERVFAIL would keep deferring a sender that retries after resolution recovered.
- **Definitive negatives (NXDOMAIN, NODATA) are cached**, honouring the SOA MINIMUM per RFC 2308 with
  a hard 4096-entry cap. "Publishes no SPF record" is the common case for unauthenticated mail here,
  so re-querying it per message would amplify junk traffic into outbound DNS load.

### SPF result deviations (Q12a/Q12c) — fixed

Both are resolved; the entry is kept because other documents reference these identifiers.

- A top-level TXT lookup returning NXDOMAIN now produces `None`, per RFC 7208 §4.3 (Q12a). It
  previously produced `Temperror`.
- An SPF `redirect=` target returning NXDOMAIN now produces `Permerror` (Q12c), via the existing
  §6.1 `None`→`Permerror` mapping. It was only ever `Temperror` as a consequence of Q12a.

Fixing these became load-bearing when DMARC began deferring on `Temperror` (`451 4.7.1`): under the
old mapping, mail from any non-existent domain would have been retried indefinitely rather than
handled as unauthenticated. See [RELAY-SENDER-AUTHORIZATION.md](RELAY-SENDER-AUTHORIZATION.md).

The former DNS fail-open for `a` and `mx` mechanisms (Q12b) is fixed and is documented in the
changelog.

### The SPF lookup limit is off by one, and is not enforced across `include:`

Two defects in the RFC 7208 §4.6.4 budget of ten DNS-consuming terms. Both were found by adversarial
review of the test suite rather than by a failing test, and the existing limit tests pass straight
over them.

**Off by one.** Each mechanism checks `requestsMade > 10` *before* incrementing, so an eleventh
consuming term is permitted. Confirmed by probe: 10 terms → `Fail` (correct), **11 terms → `Fail`
where the RFC requires `Permerror`**, 12 terms → `Permerror`. `MoreThanTenLookups_ReturnsPermerrorNotTerminal`
uses twelve terms and therefore passes with the boundary broken; the boundary itself is untested.

**The budget does not survive recursion.** `CheckHost` takes `requestsMade` by value and returns only
a `ValidationResult`, so lookups consumed inside an `include:` or `redirect=` are never added back to
the caller's count. Sibling includes each restart from the parent's total, and a record with several
includes can drive far more than ten lookups in total while every individual branch stays under the
limit. That is the amplification the limit exists to prevent, and the sender's domain is
attacker-chosen.

Also missing: the §4.6.4 "void lookup" limit (two NXDOMAIN/NODATA results) is not implemented or
counted at all, and there is no cycle-termination test for mutually-recursive `include:` chains.

Fixing the boundary is a one-character change (`>=` for `>`), but it will start rejecting records that
currently pass, so it wants the same observe-before-enforce care as the DMARC change. The recursion
fix needs `CheckHost` to return the consumed count alongside the verdict.

### The `exists:` mechanism is unimplemented and silently skipped

`exists:` (RFC 7208 §5.7) is absent from the mechanism switch in `SpfValidator.CheckHost`. Because
that switch has no `default` case, an unmatched term falls out of it and evaluation simply continues
to the next one, so the mechanism is **skipped rather than rejected**.

Confirmed by probe: for `v=spf1 exists:%{i}._spf.example -all` with the client's lookup name present
in DNS, `CheckHost` returns `Fail` — the `exists:` term is ignored and the terminal `-all` applies —
where RFC 7208 requires `Pass`.

The error direction is the concerning part: this **rejects mail it should accept**. A customer whose
provider publishes `exists:` gets a `554 5.7.23` at `MAIL FROM`. It also interacts with the DMARC
work, since an SPF result that should have been `Pass` instead denies DMARC its only authenticated
identifier.

Related: SPF macro expansion (`%{i}`, `%{s}`, `%{d}` …, RFC 7208 §7) is not implemented at all, and
`exists:` is rarely useful without it. Implementing one without the other has limited value.

### An unrecognized SPF mechanism is skipped instead of producing `permerror`

The same missing `default` case. RFC 7208 §4.6.1 makes an unknown mechanism a syntax error, and
§4.6/§6.6 require `permerror`; this implementation ignores the term and keeps evaluating.

Confirmed by probe: `v=spf1 bogusmech:xyz -all` returns `Fail` rather than `Permerror`.

That means a malformed record is evaluated as though the bad term were not there, so the verdict
reflects a policy the domain did not publish. Both this and `exists:` above are fixed by giving the
switch a `default` — but the two need opposite handling (`exists:` implemented, unknown terms
rejected), so the `default` must come with `exists:` support rather than before it.

### `redirect=` is evaluated positionally (Q13)

The validator evaluates `redirect=` where it appears and can skip mechanisms that follow it. RFC
7208 treats redirect as the fallback after all mechanisms fail to match. Defer redirect processing
until the mechanism loop completes.

### SPF results can remain stale for a connection

`ClientProcessor.SpfResultsCache` stores results by domain for the lifetime of a connection. It has
no TTL and is not cleared by `RSET` or a repeated greeting, so a previously authorized client can
retain a stale `Pass` after DNS changes.

Transient failures are no longer cached here — a `Temperror` is not stored, because DMARC defers on
it and a stale one would keep deferring a sender that retries after DNS recovered. A stale `Pass` or
`Fail` can still persist for the connection.

The reason for keeping this cache is now weaker: the resolver has a TTL-aware cache of its own, so
clearing per message would re-read from that rather than re-querying the wire. Preferred fix is to
drop this layer entirely and let the resolver cache do the work, which would make SPF verdicts follow
DNS TTLs instead of connection lifetime.

### The public-suffix reference swap has no regression test

`DownloadList` builds each generation of the suffix set off to the side and publishes it by reference
swap, and `GetOrganizationalDomain` captures that reference once per call. That replaced a real race —
the set was previously cleared and repopulated in place while connection threads read it, and a torn
read silently changes DMARC relaxed-alignment verdicts. It surfaced as intermittent suite failures.

The fix is not pinned by a test. An attempt was made and removed rather than kept green: driving
concurrent `GetOrganizationalDomain` calls against repeated `ForceRefreshList` cycles passes just as
happily against a reverted, clear-in-place implementation, so it demonstrates nothing. Two reasons,
both worth knowing before trying again:

- The rebuild window is a few microseconds inside a call otherwise dominated by an HTTP fetch, so
  readers almost never land in it. Padding the list to 20 000 entries widens the window but not
  enough.
- More fundamentally, a torn read has to change the *answer* to be observable, and the probe walk is
  only two iterations deep — `Contains` against a partially filled set usually returns the same
  organizational domain anyway.

A test that actually pins this would need to drive the swap directly rather than through
`ForceRefreshList`, with a probe domain whose answer depends on several suffix entries, and would
still be probabilistic. Until then the fix rests on inspection: the swap is a single reference
assignment under a lock, and readers capture once.

### Public suffix state is process-wide

`DmarcValidator` holds the public suffix set in static state. Multiple servers in one process cannot
safely use different suffix-list sources or force independent refreshes. This is accepted under the
one-server-per-pod deployment model. Revisit it before supporting isolated multi-instance hosts.

## Protocol and API cleanup

### Two-argument response overload drops descriptive text (R1/Q7)

Calls such as `WriteCode(250, "2.0.0")` bind to the `(int, string)` overload intended for a complete
handler-supplied message, rather than `(ushort, string enhancedStatus)`. The resulting response is
legal but contains no human-readable table text. Remove the overload ambiguity and update the exact
wire assertions together.

Related opportunistic cleanups:

- VRFY currently combines SMTP `252` with failure-class enhanced status `5.5.1` (Q2).
- An invalid RCPT address returns bare `501` without an enhanced status (Q5).

### Invalid AUTH LOGIN initial response falls back to another prompt (R2)

An undecodable inline response to `AUTH LOGIN` is treated like an absent username and the server
prompts again. Prefer rejecting malformed Base64 with `501` instead of extending the authentication
exchange.

### AUTH LOGIN case indentation is damaged (R3)

The `case "LOGIN":` block in `AuthenticationCommands.cs` behaves correctly, but its comment has a
stray deep indent and the remaining statements are aligned with the switch rather than the case body.
Reformat the block to match the repository's tab-based Allman style when that command is next touched.

### Custom delivery status codes are not validated (R4)

`SmtpDeliveryResult.Status(int, ...)` accepts values outside the SMTP three-digit range and does not
ensure the enhanced-status class agrees with the SMTP status class. CR/LF injection is already
blocked. Add validation without weakening that response-splitting protection.

### Authenticated `Received` header omits the client address (Q3)

For authenticated sessions, the prepended `Received:` header omits `from <ip>`. Preserve the client
address for forensic traceability regardless of authentication state.

### Minor accepted protocol behavior

- STARTTLS is accepted before EHLO (Q4). This is tolerant but out of the usual command order.
- Despite its historical name, `MessageCharactersLimit` counts stored DATA bytes after
  dot-unstuffing and excludes CRLF (Q6). It is not the exact RFC 1870 wire-octet count, so configure
  headroom.
- DATA receives only the RFC-required final response, not a response per input line (Q9).

## Test-suite maintenance

- `TestPorts.Allocate()` releases its ephemeral listener before the server binds the selected port,
  leaving a theoretical time-of-check/time-of-use race (R8). Test collections remain serial.
- A few lifecycle tests use timing delays (R9). Replace them with observable synchronization when
  those tests are next changed.
- Pin identifiers are represented inconsistently in names, comments, and traits (R10). Standardize
  them only if it improves discoverability; the behavior is already covered.
- The shipped `netstandard2.1` library asset is not exercised by the test suite. The tests target
  `net10.0` and so always resolve the `net10.0` build; the two assets do not share a dependency
  graph, since `netstandard2.1` resolves MimeKit and `zabszk.DnsClient` netstandard builds rather
  than their `net10.0`/`net7.0` ones. This predates the .NET 10 retarget — the previous
  `net7.0;net8.0` test matrix never covered `netstandard2.1` either — so it is a long-standing gap
  rather than a new one. A manual consumer probe on 2026-09-01 confirmed the packed asset restores,
  loads, and resolves its MimeKit- and DnsClient-dependent types on a `netcoreapp3.1` consumer, so
  the artifact is not broken; it is simply unverified by CI. Closing this properly means a
  package-consumer test lane that references the built `.nupkg` from a framework that selects
  `lib/netstandard2.1` and runs the protocol, integrity, SPF, and DMARC suites against it.

### Latent processor-registration ordering smell (not reproduced)

`ClientProcessor` starts `Init()` from its constructor, before `Listener.AddProcessor` registers it.
In theory, a synchronously failed connection filter could dispose the processor before registration,
making `RemoveProcessor` a no-op and allowing a dead processor to be added afterwards.

The proposed leak was not observed across 5,000 connections using instantly rejecting and
synchronously throwing filters: the final processor count was always zero, while the observed peak
represented genuine in-flight connections. The `Task.Run(Init)` hop currently gives registration
time to win the race.

Re-test this ordering first if processor initialization ever starts inline. If it becomes observable,
use two-phase initialization: construct, register, then start `Init()`.
