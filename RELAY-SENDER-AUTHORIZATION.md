# Restricting who may send to a customer relay address

Working notes for the journal/customer relay at `@ourdomain.com`: what controls are available,
what each one actually proves, and how to turn on sender authentication without bouncing
legitimate customer mail during the transition.

Companion documents: [WHITELIST.md](WHITELIST.md) for the filter hooks themselves, and
[TENANT-CRYPTO-AUTH.md](TENANT-CRYPTO-AUTH.md) for per-tenant cryptographic identity.

---

## The problem

The original plan was to allowlist Exchange Online IP ranges and use that as the authorization
control — only Microsoft may connect, therefore only our customers may send. DMARC aggregate
reports for `ourdomain.com` show why that does not hold. Mail forging our domain arrived from
genuine Exchange Online IPv6 egress hosts:

```text
2a01:111:f403:c405::2
2a01:111:f403:c407::1
2a01:111:f403:c407::3
```

Those addresses are legitimate Microsoft infrastructure. They are also shared by every tenant on
the platform, including whoever sent that mail. An IP allowlist covering Exchange Online would
have admitted all of it.

The distinction to hold onto:

| Control | Question it answers |
|---|---|
| IP / CIDR allowlist | Which network did this connection come from? |
| SPF / DKIM / DMARC | Which domain is this sender authorized to use? |
| mTLS client certificate | Which *customer* is this, cryptographically? |

On dedicated infrastructure the first two coincide, which is why an IP allowlist feels sufficient
until the sender is a hyperscaler. On shared infrastructure the first question is nearly
uninformative.

## What the allowlist is still worth

Keep it. Restricting TCP 25 to Exchange Online CIDRs removes the entire long tail of botnet and
direct-to-MX traffic before the greeting. It is a good coarse filter. It is not an authorization
control, and it should not be the only thing standing between the internet and the relay.

Endpoint set 10 from the Microsoft 365 endpoint feed is the source for those CIDRs — see
[TENANT-CRYPTO-AUTH.md](TENANT-CRYPTO-AUTH.md) for the feed URLs and refresh approach.

---

## Two separate DMARC decisions

These get conflated and they carry completely different risk.

**1. Publishing a DMARC policy for `ourdomain.com`.** This is a DNS record. It tells *other*
receivers what to do with mail claiming to be us. `p=none` with a `rua=` address changes no
delivery behaviour anywhere — it only produces the aggregate reports we are already reading.
There is no bounce risk. This is the control that addresses the spoofing seen in the reports.

**2. Enforcing SPF/DMARC on inbound mail at our relay.** This is `ServerOptions` in this library.
It decides whether *we* refuse mail. This is the one that can bounce a customer.

The spoofing in the reports is a (1) problem. The relay ingress question is a (2) problem. Turning
on (2) does not fix the spoofing, and publishing (1) costs nothing.

---

## Current enforcement behaviour in this library

Both checks default to **enabled**, and both reject unconditionally. Verified in source, not
assumed:

**SPF** — [TransactionCommands.cs:86-90](CSharp-SMTP-Server/Protocol/Commands/TransactionCommands.cs#L86-L90).
On `ValidationResult.Fail` the server writes `554 5.7.23` and returns at `MAIL FROM`. This happens
*before* `IsAllowedSenderSpfVerified` is invoked, so a filter never sees the failing case and
cannot override it.

**DMARC** — [TransactionCommands.cs:353-359](CSharp-SMTP-Server/Protocol/Commands/TransactionCommands.cs#L353-L359).
On `Fail` the server calls `DiscardTransaction()` and writes `554 5.7.1` at the terminating dot,
again before `CanProcessTransaction` runs.

`ValidateSPF` and `ValidateDMARC` both default to `true`.

### The DNS resolver default

The property setters for `ValidateSPF` / `ValidateDMARC` throw if the flag is turned on while
`DnsServerEndpoint` is null — but the **constructor assigns the backing fields directly**, bypassing
those setters, so that guard does not apply on the normal path. Instead the constructor substitutes
a default: if either check is enabled and no endpoint was supplied, `DnsServerEndpoint` becomes
`1.1.1.1:53` — Cloudflare's public resolver
([ServerOptions.cs:131-141](CSharp-SMTP-Server/ServerOptions.cs#L131-L141)).

The setter guard can therefore only fire in one narrow case: constructing with both checks off, then
enabling one later.

The consequence of the default matters more than the guard. A deployment that never chose a resolver
sends **every SPF and DMARC lookup to Cloudflare** — which means the sending domains of all inbound
mail, and the query volume, leave the network to a third party. For a journaling product with
compliance obligations that is worth an explicit decision rather than a silent default.

Set `DnsServerEndpoint` explicitly to keep resolution on infrastructure you control:

```cs
var options = new ServerOptions(
    validateSPF: true,
    validateDMARC: true,
    dnsServerEndpoint: new IPEndPoint(IPAddress.Parse("10.0.0.53"), 53));
```

`ServerOptions.DnsServerEndpointIsDefault` reports whether the fallback was applied, and
`SMTPServer` logs a warning through `ILogger` at startup when it was. The fallback is kept rather
than made a hard error so existing callers keep working — the fix is visibility, not a breaking
change.

### There is no built-in observe-only mode

This is the practical constraint on any cautious rollout. Because both rejections `return` before
the corresponding filter hook, a filter **cannot** be used to downgrade a `Fail` to a log line.
The options are:

- **Enforce**, accepting the false-positive risk, or
- **Disable** the built-in check (`ValidateSPF = false`) and run the validator from a filter hook,
  logging the verdict and returning `Success` regardless.

The second gives a genuine observe-only mode and is the recommended way to measure real customer
traffic before enforcing. It means calling the SPF validator directly rather than relying on the
transaction pipeline.

Adding a first-class observe-only option to `ServerOptions` — evaluate, annotate
`Authentication-Results`, do not reject — would be a small change at both sites and would remove
the need for that workaround. Worth considering if this rollout is going to be repeated per
customer.

### DKIM is not implemented

Per [KNOWN_ISSUES.md](KNOWN_ISSUES.md): the server does not verify DKIM. Consequences for a
journal relay:

- Mail that would pass DMARC via DKIM alignment — notably anything forwarded, where SPF breaks by
  design — has no second chance. The DMARC pass rate at our relay will be strictly worse than at a
  receiver that checks DKIM.
- For null reverse-path messages the checked identity is the HELO domain, per RFC 7208 §2.4.

This raises the false-positive risk of enforcement above what it would be on a DKIM-capable
receiver, and is a strong argument for measuring before enforcing.

SPF is therefore the only authentication mechanism available to DMARC here. An earlier revision of
this document made that claim while the implementation did not actually honour it — for ordinary
mail DMARC required no SPF result at all, which is the defect described next. The statement is true
of the shipped code as of this revision.

### DMARC requires an authenticated aligned identifier (fixed)

**Status: fixed.** Previously DMARC could return `Pass` with nothing authenticated at all; the
paragraphs below describe the shipped behaviour. Found by adversarial review, confirmed by test,
and now pinned by `DmarcValidatorTests.AlignedFrom_WithoutSpfPass_MustNotPass`.

#### What the defect was

`DmarcValidator.ProcessRecord` gated on the SPF result **only for null reverse-path messages**. For
ordinary mail — anything with a non-null `MAIL FROM`, the overwhelming majority — `SPFValidationResult`
was never consulted, so the method proceeded straight to the alignment comparison and returned
`Pass` on a match.

Alignment compares the header-`From` domain against the envelope-sender domain, and both are
supplied by the connecting client. Making them match required no authorization — the sender simply
wrote the same domain in both places. `TransactionCommands` refuses only SPF `Fail`, so `None`,
`Neutral`, `Softfail`, `Temperror`, `Permerror` and `CheckDisabled` all reached DMARC and all
returned `Pass` against a `p=reject` domain. A domain publishing `p=reject` but no SPF record could
be spoofed through this server, and a DNS outage silently downgraded DMARC from enforcing to
passing rather than failing closed.

#### What it does now

RFC 7489 §4.1 requires at least one *authenticated* identifier to align. With DKIM verification
unimplemented (see [KNOWN_ISSUES.md](KNOWN_ISSUES.md)) SPF is the only mechanism that can supply
one, so an aligned SPF `Pass` is now mandatory for **every** message, not only for bounces:

| SPF result | DMARC result | Effect at DATA |
|---|---|---|
| `Pass` | alignment is evaluated as before | `Pass`, or the policy verdict when unaligned |
| `Temperror` | `Temperror` | `451 4.7.1`, deferred — sender retries |
| `None`, `Neutral`, `Softfail`, `Permerror`, `CheckDisabled` | `None` | delivered, **not** labelled `dmarc=pass` |

The identity SPF checked and the identity DMARC aligns are the same name by construction:
`TransactionCommands` checks `postmaster@<HELO domain>` for a null reverse-path (RFC 7208 §2.4) and
the `MAIL FROM` domain otherwise, which is exactly how `ProcessRecord` picks its envelope domain. A
`Pass` is therefore always a pass *for the domain being aligned*, never for an unrelated one.

#### Why unauthenticated mail answers `None` rather than `Fail`

This is the deliberate part, and it is a deployment decision as much as a specification one.

RFC 7489 §4.1 makes DMARC a statement about authenticated identifiers; with no authenticated
identity there is no determination to report, which is what `None` means. Failing closed instead
would refuse every DSN from a bouncing MTA (which greets with its own hostname, routinely unrelated
to the `From` domain of the notification it carries) and every customer domain that publishes no
usable SPF record — the permanent, unrecoverable mail loss this relay exists to prevent.

The consequence to be explicit about: **a domain publishing `p=reject` that has no SPF record is
still not protected by this relay.** DMARC no longer *claims* it is — the message is delivered
without a `dmarc=pass` label rather than with one — but the message is delivered. Closing that gap
needs a second authentication mechanism (DKIM) or per-customer mTLS
([TENANT-CRYPTO-AUTH.md](TENANT-CRYPTO-AUTH.md)), not a stricter reading of this code path. Until
then, the filter hooks ([WHITELIST.md](WHITELIST.md)) are where a policy stricter than "no
determination" belongs, because they can distinguish a known customer domain from an unknown one.

Spoofing is still caught wherever DMARC can actually speak: an attacker who authenticates their own
domain and forges a victim's `From` header fails alignment and is refused under `p=reject`.

#### `Temperror` is deferred, not accepted

A DNS failure during SPF is not evidence either way. Accepting would let a resolver outage silently
disable DMARC for every domain at once — precisely the window an attacker would choose — while
permanently rejecting would turn a transient hiccup into bounced mail. RFC 7489 §6.6.3 permits a
temporary-failure response, so the message is deferred with `451 4.7.1` and the sender retries.

Two supporting fixes were required to make this safe:

- **DMARC record lookups** no longer collapse transient DNS errors into "no record". `NXDOMAIN` is
  definitive (no policy); any other error code now yields `Temperror` rather than `None`, so a
  resolver outage cannot disable enforcement by making every domain look policy-free.
- **SPF `NXDOMAIN` now returns `None`, not `Temperror`** (RFC 7208 §4.3; this closes the deviation
  formerly recorded as Q12a, and Q12c with it). Without this, mail from any non-existent domain
  would have been deferred forever instead of treated as the unauthenticated mail it is.

#### Configuration note

Enabling `ValidateDMARC` while `ValidateSPF` is off leaves DMARC with no authentication mechanism
at all: it can never return `Pass`. `SMTPServer` now warns about this at startup rather than
presenting an enabled-looking but inert control.

#### Remaining exposure

Requiring aligned SPF `Pass` is what RFC 7489 §4.1 calls for, but with no DKIM it rejects forwarded
mail that a DKIM-capable receiver would accept — SPF breaks by design on forwarding and there is no
second chance. That is an argument for the observe-only rollout below, not against the fix.

## DNS resolution and caching (design)

### The problem: no caching anywhere

The current resolver, `zabszk.DnsClient` 1.0.1, has **no response cache**. Its `DnsClientOptions`
exposes `Timeout`, `MaxAttempts`, `TimeoutInnerDelay`, `UseTCPForTruncated`, `TCPEndpointOverride`
and `ErrorLogging` — and nothing else. Every lookup goes to the wire.

The only caching in the library is `SpfResultsCache`
([ClientProcessor.cs:45](CSharp-SMTP-Server/Networking/ClientProcessor.cs#L45)), a `Dictionary` of
final SPF verdicts keyed by domain and scoped to **a single connection**. It is discarded when the
connection closes, and it caches verdicts rather than DNS records, so it does nothing for the
lookups underneath.

The cost per message is not one query. SPF evaluation issues TXT
([SpfValidator.cs:82](CSharp-SMTP-Server/Protocol/SPF/SpfValidator.cs#L82)), MX
([line 208](CSharp-SMTP-Server/Protocol/SPF/SpfValidator.cs#L208)) and A/AAAA
([line 337](CSharp-SMTP-Server/Protocol/SPF/SpfValidator.cs#L337)) queries, recursing through
`include:` and `redirect=` up to the RFC 7208 limit of 10 DNS-consuming terms. DMARC adds a TXT
lookup ([DmarcValidator.cs:148](CSharp-SMTP-Server/Protocol/DMARC/DmarcValidator.cs#L148)).

For a journal relay taking sustained traffic from a small set of customer domains, this re-resolves
the same Exchange Online `include:` chain on every message. That chain fans out several levels.

### Decision: replace the resolver rather than build a cache

An earlier version of this note proposed writing a TTL-aware cache in front of the existing client.
That is the wrong order of work. `DnsClient.NET` (package id `DnsClient`, MichaConrad) already
provides it, is far more widely deployed than the current dependency, and ships `netstandard2.1`
and `net8.0` targets — both compatible with this project's `netstandard2.1;net10.0`.

Verified against `DnsClient` 1.8.0's shipped XML documentation:

| Capability | Member | Notes |
|---|---|---|
| Response cache, TTL-aware | `DnsQueryOptions.UseCache` | Honours record TTL |
| TTL floor | `LookupClientOptions.MinimumCacheTimeout` | Default null; guards zero-TTL records |
| TTL ceiling | `LookupClientOptions.MaximumCacheTimeout` | Default null |
| Negative caching | `DnsQueryOptions.CacheFailedResults` | With `FailedResultsCacheDuration` |
| System name servers | `LookupClientOptions.AutoResolveNameServers` | Default **true** |
| TCP fallback | `DnsQueryOptions.UseTcpFallback` | |
| Retries | `DnsQueryOptions.Retries` | |

`AutoResolveNameServers` also settles the "use the OS resolver" question raised earlier. The
concern was that .NET exposes no managed API for TXT/MX through the platform stub resolver, making
an OS mode a P/Invoke exercise. `DnsClient.NET` resolves the *system-configured name servers* and
queries them directly — which delivers the intent (use the network's own resolvers, no hardcoded
public IP) without native interop. Note the distinction: this uses the OS's configured servers, not
the OS's cache. Caching happens in-process, via `UseCache`.

### Proposed resolver modes

Three modes on `ServerOptions`, replacing the current single nullable endpoint:

1. **System** *(proposed default)* — `AutoResolveNameServers = true`. Uses the network's configured
   resolvers. Nothing leaves for a third party that was not already the machine's resolver.
2. **Explicit** — caller supplies one or more endpoints. Current behaviour, minus the silent
   Cloudflare substitution.
3. **Disabled** — no validation, no resolver.

This makes the Cloudflare fallback unnecessary rather than merely loud. The
`DnsServerEndpointIsDefault` flag and startup warning added alongside this document are the interim
mitigation; under mode 1 the default becomes "the resolver this machine already uses", which is the
correct default for a compliance-sensitive deployment.

### Migration considerations

- **`SpfValidator` has four public constructors** taking `DnsClient.DnsClient`, `EndPoint`,
  `IPAddress`, and `string` ([SpfValidator.cs:29-65](CSharp-SMTP-Server/Protocol/SPF/SpfValidator.cs#L29-L65)).
  These are public API on a published NuGet package; swapping the resolver type is a **breaking
  change** for anyone constructing a validator directly. The package is already at
  `2.0.0-krugertech.1`, so a major-version boundary is available.
- `DnsLogger` adapts the current client's `IErrorLogging`; `DnsClient.NET` uses
  `Microsoft.Extensions.Logging`, so that shim needs rewriting or dropping.
- Record types differ (`DnsClient.Data.Records` vs `DnsClient.Protocol`), touching both validators.
- The test `DnsStub` serves wire-format DNS over UDP, so it should work against either client — but
  cache behaviour must be tested explicitly, since a stub that counts queries is the only way to
  prove a cache hit actually avoided the wire.
- Caching changes failure semantics in a useful way: with `CacheFailedResults` and a modest
  `FailedResultsCacheDuration`, a resolver hiccup stops translating into a burst of SPF
  `TemporaryFail` responses.

### Sizing the cache

`DnsClient.NET`'s cache is keyed by query and is process-wide. Sending domains are chosen by
whoever connects, so cache keys are attacker-influenced — worth confirming what bound, if any, the
implementation places on cache size before relying on it under hostile load. `MaximumCacheTimeout`
limits entry lifetime but not entry count.

---

## Recommended sequence

0. ~~**Fix the DMARC authentication gap first.**~~ **Done.** DMARC now requires an authenticated,
   aligned identifier; see [the section above](#dmarc-requires-an-authenticated-aligned-identifier-fixed).
   Note what this does and does not buy: an aligned spoof from a domain with no SPF record is no
   longer labelled `dmarc=pass`, but it is still *delivered* (`None` = no determination), so the
   observe-and-enforce sequence below still carries the weight for actual sender authorization.
1. **Publish `p=none` with `rua=` for `ourdomain.com`** if not already. No delivery risk; keeps the
   reports coming.
2. **Keep the Exchange Online CIDR allowlist** as a coarse ingress filter on TCP 25.
3. **Run observe-only at the relay** for several weeks — SPF evaluated, verdict logged, nothing
   rejected. Identify which customers would fail and why. Expect forwarding-related SPF failures
   given no DKIM.
4. **Fix what the observation surfaces**, working with the affected customers.
5. **Enforce with `TemporaryFail` first.** A `4.7.1` makes a conforming MTA retry rather than
   hard-bounce, so a misconfiguration is a delay to notice and fix, not lost mail. Move to
   `5.7.1` only once the failure rate is understood and stable.
6. **Consider per-customer mTLS** for the customers that matter most.

Step 5 is the direct answer to the false-positive fear: the choice is not enforce-or-not, it is
which failure code to enforce with.

---

## The control that actually answers the question

For a relay with a known, enumerable customer list, domain-based authentication is an
approximation. SPF/DMARC tells us a message is authorized for a domain; it does not tell us the
message is from customer X, and it inherits the customer's DNS hygiene as a failure mode.

Per-customer mTLS client certificates, or a dedicated per-customer relay, give cryptographic
tenant identity with no dependence on the customer's SPF record being correct. Higher setup cost
per customer, no false positives from third-party misconfiguration.
[TENANT-CRYPTO-AUTH.md](TENANT-CRYPTO-AUTH.md) covers the architecture.

For a small number of high-value journal customers this is likely the better destination, with
SPF/DMARC as the fallback for everyone else.

---

## Open questions

- Should `ServerOptions` gain a first-class observe-only / report-only mode for SPF and DMARC?
- Is DKIM verification worth prioritising, given it is currently the missing half of DMARC?
- Which customers are candidates for mTLS versus domain-based authentication?
- Which resolver should the relay actually use? The Cloudflare fallback is now logged rather than
  silent, but the deployment still needs to make the choice deliberately — and resolver mode 1
  would make the question mostly go away.
- Should the constructor honour the same DNS-endpoint invariant its property setters enforce? The
  two paths currently disagree, which is what made the fallback easy to miss.
- Is the `SpfValidator` public-constructor break acceptable at `2.0.0`, or do the existing
  overloads need shims to keep external callers compiling?
- What bounds does `DnsClient.NET`'s cache place on entry count, given cache keys are
  attacker-influenced?
