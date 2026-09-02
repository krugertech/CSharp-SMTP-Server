# Handover: DMARC authentication gap and DNS client replacement

> **STATUS: BOTH TASKS COMPLETE.** Retained as the record of what was asked for and why; the
> reasoning behind each decision now lives in the code comments and in the documents below.
>
> | Task | Commit | Outcome |
> |---|---|---|
> | 1 — DMARC authentication gap | `c32d844`, `768d521` | DMARC requires an aligned SPF `Pass`; `Temperror` defers with `451 4.7.1` |
> | 2 — DNS client replacement | `727f97f`, `7ec04b8` | DnsClient.NET 1.8.0 behind `IDnsResolver`, caching proven by query count |
>
> Suite: **517/517** (baseline was 490).
>
> Shipped behaviour is documented in
> [RELAY-SENDER-AUTHORIZATION.md](RELAY-SENDER-AUTHORIZATION.md) and
> [CHANGELOG.md](CHANGELOG.md); what remains open is in [KNOWN_ISSUES.md](KNOWN_ISSUES.md).
>
> **Decisions taken that the text below left open:**
>
> - *Unauthenticated mail answers `None`, not `Fail`.* Per the requirement that DSNs and SPF-less
>   customer mail keep flowing. The consequence is stated plainly rather than glossed: a `p=reject`
>   domain with no SPF record is still not protected by this relay — DMARC just no longer claims it
>   is. Closing that needs DKIM or per-customer mTLS.
> - *`Temperror` defers (`451 4.7.1`)* rather than failing open or closed, per RFC 7489 §6.6.3.
> - *External consumers: clean break at the 2.0 prerelease boundary.* No compatibility shims; the
>   migration table is in CHANGELOG.md.
> - *Resolver default: `System`* — the machine's own name servers, retiring the Cloudflare fallback
>   rather than warning about it.
>
> **Found and fixed along the way, beyond the original scope** — each because it directly undermined
> the task it touched:
>
> - SPF returned `Temperror` for NXDOMAIN instead of `None` (RFC 7208 §4.3, deviation Q12a, and Q12c
>   with it). Left alone, every non-existent sender domain would have been deferred indefinitely once
>   DMARC began deferring on `Temperror`.
> - A `Temperror` cached in the connection-scoped `SpfResultsCache` pinned one SERVFAIL for the whole
>   session, so a retrying sender kept being deferred after DNS recovered.
> - The DMARC public suffix list was cleared and repopulated in place while connection threads read
>   it, and set its "loaded" latch before the download began — a torn read silently changes
>   relaxed-alignment verdicts.
> - Two `DnsStub` wire-format defects, invisible to the old client: a byte-swapped RR class, and the
>   response echoing an EDNS0 OPT record into the question section.
> - Definitive DNS negatives were not cached at all, because DnsClient.NET classifies them with
>   transient failures under one flag. Caught by adversarial review, which contradicted a claim made
>   in these docs; testing confirmed the review was right.

---


Two pieces of work, in priority order. Task 1 is a confirmed security defect in committed code.
Task 2 is a dependency replacement that Task 1 does not depend on — they can be done by different
people, but read the shared context first.

Background and rationale: [RELAY-SENDER-AUTHORIZATION.md](RELAY-SENDER-AUTHORIZATION.md).
Filter-hook reference: [WHITELIST.md](WHITELIST.md).

---

## Shared context

The deployment is a journal/customer SMTP relay receiving mail from customers on Exchange Online.
The original plan was to authorize senders by allowlisting Microsoft IP ranges; DMARC aggregate
reports showed spoofed mail arriving from genuine Exchange Online IPv6 hosts, because those ranges
are shared by every tenant on the platform. Sender authorization therefore has to come from
SPF/DMARC (or, better, per-customer mTLS), not from an IP allowlist.

That makes the correctness of the DMARC implementation load-bearing, which is how Task 1 was found.

### State of the working tree at handover

Uncommitted, not staged:

```
 M CSharp-SMTP-Server/ServerOptions.cs        # DnsServerEndpointIsDefault flag + XML doc warnings
 M CSharp-SMTP-Server/SMTPServer.cs           # startup warning when Cloudflare fallback applied
 M CSharp-SMTP-Server.Tests/ServerOptionsTests.cs  # 3 tests for the flag
?? RELAY-SENDER-AUTHORIZATION.md              # design/analysis doc
?? WHITELIST.md                               # filter-hook reference
?? HANDOVER-DMARC-DNS.md                      # this file
```

Full suite passed at 490/490 with those changes in place. Nothing is committed.

An adversarial review (Codex) found the startup warning is **ineffective on the default path**:
`ILogger` defaults to null and `ServerOptions` defaults to validation-enabled, so the riskiest
configuration — `new SMTPServer(params, null, delivery)` — emits nothing. That is unresolved and
is folded into Task 2, since resolver mode 1 removes the need for the fallback entirely.

---

## Task 1 — DMARC passes without authentication (security defect)

### The defect

[DmarcValidator.cs:217](CSharp-SMTP-Server/Protocol/DMARC/DmarcValidator.cs#L217) gates on SPF for
null reverse-path messages only:

```cs
if (transaction.IsNullReversePath && transaction.SPFValidationResult != ValidationResult.Pass)
    return ValidationResult.None;
```

For ordinary mail (non-null `MAIL FROM` — nearly all traffic) `SPFValidationResult` is never read.
Execution reaches the alignment comparison and returns `Pass` on a match
([DmarcValidator.cs:234](CSharp-SMTP-Server/Protocol/DMARC/DmarcValidator.cs#L234)).

Alignment compares header-`From` domain to envelope-sender domain — both attacker-supplied. Making
them match is free.

`TransactionCommands` refuses only SPF `Fail`
([TransactionCommands.cs:86-90](CSharp-SMTP-Server/Protocol/Commands/TransactionCommands.cs#L86-L90)),
so `None`, `Neutral`, `Softfail`, `Temperror`, `Permerror` and `CheckDisabled` all reach DMARC.

**Result:** a domain publishing `p=reject` with no SPF record can be spoofed through this server.
So can one whose SPF soft-fails or whose DNS times out — a resolver outage downgrades DMARC to
passing rather than failing closed.

### Reproduce it first

This probe was run and confirmed, then reverted. Recreate it as the starting point — it should
fail after the fix. Append inside `DmarcValidatorTests` (uses the existing `Env` harness):

```cs
[Theory]
[InlineData(ValidationResult.None)]
[InlineData(ValidationResult.Neutral)]
[InlineData(ValidationResult.Softfail)]
[InlineData(ValidationResult.Temperror)]
[InlineData(ValidationResult.CheckDisabled)]
public async Task AlignedFrom_WithoutSpfPass_MustNotPass(ValidationResult spf)
{
    using var env = new Env();
    env.Stub.AddTxt("_dmarc.victim.example", "v=DMARC1; p=reject");

    var tx = new MailTransaction("attacker@victim.example", "victim.example", spf)
    {
        RawBody = "From: ceo@victim.example\r\nTo: r@example.com\r\nSubject: t\r\n\r\nbody"
    };

    Assert.NotEqual(ValidationResult.Pass, await env.Validator.ValidateTransaction(tx));
}
```

All five currently return `Pass`.

### The fix

Per RFC 7489 §4.1, DMARC passes only when at least one *authenticated* identifier aligns. With no
DKIM (see below), that means an aligned SPF `Pass`. Extend the existing gate to all mail rather
than only the null-reverse-path case.

Do not simply delete `IsNullReversePath &&` without thinking it through — read the comment block at
[DmarcValidator.cs:206-216](CSharp-SMTP-Server/Protocol/DMARC/DmarcValidator.cs#L206-L216) first.
It explains why that gate exists and why returning `None` (not `Fail`) is correct when there is no
authenticated identity: a bouncing MTA greets with its own hostname, so a naive comparison would
reject legitimate DSNs from the very domain publishing `p=reject`. Preserve that reasoning.

Specific decisions to make deliberately:

- **`Temperror`.** RFC 7489 §6.6.3 permits a temporary-failure response. Returning `None` (accept)
  fails open and lets a DNS outage disable DMARC; returning `Fail` fails closed and turns a
  resolver hiccup into rejected mail. A `TemporaryFail`/`4.7.1` path is the middle option and fits
  the caution this deployment needs. Currently there is no distinct handling.
- **`CheckDisabled`.** If SPF is off, DMARC has no authenticated identifier at all. It should not
  report `Pass`. Consider whether enabling DMARC without SPF should be rejected at configuration
  time instead.
- **`UserAuthenticated`.** Authenticated sessions bypass SPF at
  [TransactionCommands.cs:61-62](CSharp-SMTP-Server/Protocol/Commands/TransactionCommands.cs#L61-L62)
  and DMARC at [line 346](CSharp-SMTP-Server/Protocol/Commands/TransactionCommands.cs#L346).
  Confirm that stays correct.

### Expected fallout

Tightening this **will** reject mail that currently passes — that is the point, but it lands on
legitimate senders too:

- Any customer domain without a usable SPF record.
- Forwarded mail: SPF breaks by design on forwarding, and there is no DKIM to fall back on.

Per [KNOWN_ISSUES.md](KNOWN_ISSUES.md) DKIM verification is unimplemented, so DMARC here has
exactly one mechanism. This is why [RELAY-SENDER-AUTHORIZATION.md](RELAY-SENDER-AUTHORIZATION.md)
recommends observe-only measurement before enforcement, and `TemporaryFail` before `PermanentFail`.

Check whether existing tests encode the current behaviour as correct — some may need updating, and
each such update deserves scrutiny rather than a blanket fix.

### Done when

- The probe theory above fails on all five inputs (no `Pass`).
- Aligned + SPF `Pass` still returns `Pass`.
- Null-reverse-path/DSN behaviour is unchanged; existing bounce tests still pass.
- `Temperror` handling is a documented, deliberate choice.
- Full suite green (baseline 490).
- The open-defect section in RELAY-SENDER-AUTHORIZATION.md is updated to describe shipped
  behaviour.

---

## Task 2 — Replace zabszk.DnsClient with DnsClient.NET

### Why

`zabszk.DnsClient` 1.0.1 (June 2023) has **no response cache**. `DnsClientOptions` exposes only
`Timeout`, `MaxAttempts`, `TimeoutInnerDelay`, `UseTCPForTruncated`, `TCPEndpointOverride`,
`ErrorLogging`. Every lookup hits the wire.

The only caching is `SpfResultsCache`
([ClientProcessor.cs:45](CSharp-SMTP-Server/Networking/ClientProcessor.cs#L45)) — final SPF
verdicts, keyed by domain, scoped to a single connection, discarded on close. It caches verdicts,
not DNS records.

Per message, SPF issues TXT ([SpfValidator.cs:82](CSharp-SMTP-Server/Protocol/SPF/SpfValidator.cs#L82)),
MX ([208](CSharp-SMTP-Server/Protocol/SPF/SpfValidator.cs#L208)) and A/AAAA
([337](CSharp-SMTP-Server/Protocol/SPF/SpfValidator.cs#L337)) queries, recursing through `include:`
and `redirect=` up to RFC 7208's limit of 10 DNS-consuming terms; DMARC adds a TXT lookup
([DmarcValidator.cs:148](CSharp-SMTP-Server/Protocol/DMARC/DmarcValidator.cs#L148)). Sustained
traffic from a few customer domains re-resolves the same Exchange Online include chain every time.

### Target

`DnsClient` 1.8.0 (MichaConrad) — verified against the package's shipped XML docs, not from memory.
Ships `netstandard2.0`, `netstandard2.1`, `net472`, `net6.0`, `net8.0`; this project targets
`netstandard2.1;net10.0`, so it fits.

| Need | Member |
|---|---|
| TTL-aware cache | `DnsQueryOptions.UseCache` |
| TTL floor / ceiling | `LookupClientOptions.MinimumCacheTimeout` / `MaximumCacheTimeout` |
| Negative caching | `DnsQueryOptions.CacheFailedResults`, `FailedResultsCacheDuration` |
| System name servers | `LookupClientOptions.AutoResolveNameServers` (default true) |
| TCP fallback / retries | `DnsQueryOptions.UseTcpFallback`, `Retries` |

`AutoResolveNameServers` resolves the **system-configured name servers** and queries them directly.
It does *not* use the OS cache — caching is in-process via `UseCache`. This still delivers the
intent (use the network's own resolvers, no hardcoded public IP) without the P/Invoke that reading
TXT/MX through the platform stub resolver would otherwise require.

### Public API breakage — plan before coding

This is the part an earlier draft understated. [SpfValidator.cs](CSharp-SMTP-Server/Protocol/SPF/SpfValidator.cs)
exposes, all public:

- `public readonly DnsClient.DnsClient DnsClient;` (line 22) — a **field** typed on the concrete
  third-party client. Source *and* binary break.
- Four constructors (lines 29-65) taking `DnsClient.DnsClient`, `EndPoint`, `IPAddress`, `string`,
  several with `DnsClientOptions`.
- [DnsLogger.cs](CSharp-SMTP-Server/Protocol/DnsLogger.cs) implements the old client's
  `IErrorLogging`. DnsClient.NET uses `Microsoft.Extensions.Logging`, so this shim is rewritten or
  dropped — it is public API too.
- `SMTPServer.DnsClient` and record types (`DnsClient.Data.Records` vs `DnsClient.Protocol`),
  touching both validators.

Recommended order, per the review:

1. Introduce an **internal resolver abstraction** (query name + type → records with TTLs).
2. Port `SpfValidator` and `DmarcValidator` onto it.
3. Swap the implementation behind it.
4. Keep endpoint-based overloads working; deprecate concrete-client surface with a documented
   migration path.

The package is `2.0.0-krugertech.1`, so a major boundary is available — but "we're pre-2.0" is a
version number, not a migration plan. Decide explicitly whether external consumers exist.

### Resolver modes

Replace the single nullable endpoint with three modes:

1. **System** *(proposed default)* — `AutoResolveNameServers = true`.
2. **Explicit** — caller-supplied endpoint(s); current behaviour minus the silent substitution.
3. **Disabled** — no validation, no resolver.

This retires the Cloudflare fallback rather than merely logging it, and resolves the outstanding
review finding that the startup warning is invisible when `ILogger` is null. It also removes the
constructor/setter invariant asymmetry described below, since resolver selection stops being a
nullable field with two contradictory validation paths.

### Also fix here

**Constructor/setter contradiction** in [ServerOptions.cs](CSharp-SMTP-Server/ServerOptions.cs).
The constructor writes `_validateSPF`/`_validateDMARC` directly and invents an endpoint; the
property setters throw for the same enabled-with-null-endpoint state. Because `DnsServerEndpoint`
is `readonly`, an instance created with validation disabled can never enable it later. Identical
logical configuration succeeds or fails based only on construction order. The three tests added in
`ServerOptionsTests` currently **pin** this contradiction — they will need revisiting.

**Cache bounds.** DnsClient.NET's cache is process-wide and keyed by query; sending domains are
chosen by whoever connects, so keys are attacker-influenced. `MaximumCacheTimeout` bounds entry
lifetime, not entry count. Confirm what bound exists before relying on it under hostile load.

### Testing

`DnsStub` (test project) serves wire-format DNS over UDP and should work against either client. Add
a query-counting assertion — a cache is only proven by showing the second lookup did **not** reach
the wire. Also cover TTL expiry and negative-cache behaviour.

### Done when

- SPF/DMARC resolve through the abstraction, backed by DnsClient.NET with caching on.
- A repeated lookup within TTL issues no second wire query (proven by stub query count).
- Resolver modes implemented; no silent third-party default on any path, logger present or not.
- `ServerOptions` invariants consistent across constructor and setters.
- Public API changes documented with a migration note in [CHANGELOG.md](CHANGELOG.md).
- Full suite green.

---

## Open questions for whoever picks this up

- Is DKIM verification worth prioritising? It is the missing half of DMARC and directly bounds how
  well Task 1 can work. The unfinished upstream `dkim` branch was deliberately not merged
  ([KNOWN_ISSUES.md](KNOWN_ISSUES.md)).
- Should `ServerOptions` gain a first-class observe-only mode for SPF/DMARC? Both currently reject
  before the filter hooks run, so consumers cannot measure without disabling the checks entirely.
- Do external consumers of the NuGet package exist? Determines how much compatibility shimming
  Task 2 needs.
- Which customers move to per-customer mTLS ([TENANT-CRYPTO-AUTH.md](TENANT-CRYPTO-AUTH.md)) rather
  than domain-based authentication?
