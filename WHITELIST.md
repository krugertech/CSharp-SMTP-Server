# Whitelisting

There is no whitelist (or blocklist) type in this library. There is no allowlist configuration,
no IP-range option on `ServerOptions`, and no built-in domain table. What the library provides is
`IMailFilter` — a set of five decision points a consumer implements, inside which an allowlist of
any shape can be built.

This document records where those hooks fire, what each one can decide, and the behaviour a
whitelist implementation has to account for. Before building one, read
[An IP allowlist is not a substitute for SPF and DMARC](#an-ip-allowlist-is-not-a-substitute-for-spf-and-dmarc)
— allowlisting shared infrastructure such as Microsoft 365 does not stop domain spoofing.

---

## The hook

[`IMailFilter`](CSharp-SMTP-Server/Interfaces/IMailFilter.cs), registered with
`server.SetFilter(new MyFilter())`. Every method returns `SmtpResult`, a struct of an
`SmtpResultType` (`Success`, `TemporaryFail`, `PermanentFail`) and an optional `FailMessage`.

The whole interface is skipped when no filter is registered — every call site is guarded by
`if (Server.Filter != null)`. Registering a filter is therefore opt-in, and a filter must
implement all five methods, returning `Success` from the ones it does not care about.

## Decision points

| Method | Fires at | Whitelist scope |
|---|---|---|
| `IsConnectionAllowed(ep)` | Connection accepted, **before the greeting** | IP / CIDR allowlist |
| `IsAllowedSender(source, ep, username)` | `MAIL FROM`, before SPF | Sender address or sender domain |
| `IsAllowedSenderSpfVerified(source, ep, username, validationResult)` | `MAIL FROM`, after SPF | Same, conditioned on SPF outcome |
| `CanDeliver(source, destination, authenticated, username, ep)` | Each `RCPT TO`, before mailbox lookup | Recipient domain, relay control |
| `CanProcessTransaction(transaction)` | Terminating dot, after DATA | Whole-envelope and content rules |

### Wire codes on rejection

Each site maps the result type onto an enhanced status code and a default text. The reply code
is fixed per site; only the enhanced code varies with `PermanentFail` (`5.7.1`) versus
`TemporaryFail` (`4.7.1`):

| Method | Reply code | Default text |
|---|---|---|
| `IsConnectionAllowed` | `550` | `Delivery not authorized, connection refused` |
| `IsAllowedSender` | `554` | `Delivery not authorized (MAIL FROM address not allowed), message refused` |
| `IsAllowedSenderSpfVerified` | `554` | `Delivery not authorized (mail sender not allowed), message refused` |
| `CanDeliver` | `550` | `Delivery to this recipients is not allowed, message refused` |
| `CanProcessTransaction` | `554` | `Delivery not authorized, message refused` |

A non-empty `FailMessage` replaces the default text. A message that is null, empty, or
**whitespace-only** falls back to the default — the check is `string.IsNullOrWhiteSpace`, so a
`" "` message will not blank the reply. Pinned by `Filter_WhitespaceOnlyFailMessage_FallsBackToDefault`
in [GreetingAndFilterTests.cs](CSharp-SMTP-Server.Tests/GreetingAndFilterTests.cs).

---

## Behaviour a whitelist has to account for

**The connection filter runs before the greeting.** `IsConnectionAllowed` is awaited inside
`Greet()` ([ClientProcessor.cs:273](CSharp-SMTP-Server/Networking/ClientProcessor.cs#L273)), and a
rejection writes `550` and disposes the connection — the client never sees `220`. An IP allowlist
here costs the client one round trip and nothing more. It also means a slow lookup (a blocking DNSBL
query, say) delays the greeting for every connection; keep this path fast.

**The null sender arrives as an empty string.** A bounce (`MAIL FROM:<>`) reaches
`IsAllowedSender` with `source` set to `""`, not null. A domain allowlist written as
`source.Split('@')[1]` throws on it — see [CHANGELOG.md](CHANGELOG.md) on the address-getter
change. Handle the empty sender explicitly, and decide deliberately whether bounces are whitelisted.

**`IsAllowedSenderSpfVerified` is not called on SPF `Fail`.** An SPF failure is refused earlier
with `554 5.7.23`, so the hook never sees that case. The `validationResult` it does receive may be
`CheckDisabled` (SPF off, or no checkable identity), `UserAuthenticated` (authenticated session —
SPF is skipped), or any non-`Fail` outcome. A whitelist that means to override an SPF failure
cannot do it here; that decision has already been made.

**SPF results are cached per session.** The `SpfResultsCache` keyed by domain is consulted before
each lookup, so within one connection a repeated sender domain yields the cached result rather
than a fresh check.

**For a null reverse-path, the checked identity is the HELO domain.** Per RFC 7208 §2.4 the
identity becomes `postmaster@<HELO domain>`, so a sender-domain allowlist that keys on `source`
alone sees nothing for bounces — the relevant name is on `processor.HeloDomain`, reflected in the
`validationResult` rather than in `source`.

**`CanDeliver` fires per recipient, before the mailbox lookup.** It runs ahead of
`DoesUserExist`, so a recipient allowlist here shadows the mailbox check: a rejected recipient
never reaches delivery resolution. `authenticated` is derived from a non-empty username, which is
the same condition that makes a session count as authenticated elsewhere.

**`CanProcessTransaction` discards the transaction on rejection.** `DiscardTransaction()` is
called before the `554` is written, so the envelope is gone and the client must start over with a
new `MAIL FROM`. Everything a content-based whitelist needs is on `MailTransaction` at this point,
including the SPF and DMARC validation results.

---

## Worked shape

[SampleApp/FilterInterface.cs](SampleApp/FilterInterface.cs) implements the interface — its
`CanProcessTransaction` rejects a message containing the word "spam". The same shape holds for an
allowlist; only the predicate and the hook change.

```cs
public Task<SmtpResult> IsConnectionAllowed(EndPoint? ep)
{
    if (ep is not IPEndPoint ip || !_allowedNetworks.Any(n => n.Contains(ip.Address)))
        return Task.FromResult(new SmtpResult(SmtpResultType.PermanentFail, "Not authorized"));

    return Task.FromResult(new SmtpResult(SmtpResultType.Success));
}

public Task<SmtpResult> IsAllowedSender(string source, EndPoint? ep, string? username)
{
    if (string.IsNullOrEmpty(source))            // MAIL FROM:<> — a bounce, not an address
        return Task.FromResult(new SmtpResult(SmtpResultType.Success));

    var at = source.LastIndexOf('@');
    var domain = at < 0 ? null : source[(at + 1)..];

    if (domain == null || !_allowedDomains.Contains(domain))
        return Task.FromResult(new SmtpResult(SmtpResultType.PermanentFail));

    return Task.FromResult(new SmtpResult(SmtpResultType.Success));
}
```

Choose the result type by what the sender should do with it. `PermanentFail` (`5.7.1`) tells a
conforming MTA not to retry — right for an address that will never be on the list. `TemporaryFail`
(`4.7.1`) invites a retry — right when the allowlist source is momentarily unavailable, so a lookup
outage does not turn into permanent rejections.

---

## An IP allowlist is not a substitute for SPF and DMARC

Whitelisting Microsoft-owned address ranges — Exchange Online, Office 365, `outlook.com` — does
**not** stop spoofed mail claiming your domain. Those ranges are shared by every tenant on the
platform, so "the connection came from Microsoft" is not evidence about who sent the message or
which domain they are entitled to use.

This is observable in production DMARC aggregate reports. Forged mail for a domain arrives from
Exchange Online IPv6 sources such as:

```text
2a01:111:f403:c405::2
2a01:111:f403:c407::1
2a01:111:f403:c407::3
```

These are legitimate Microsoft egress hosts. What made the mail fraudulent was the domain in the
`From`/`MAIL FROM`, not the source address — and it was SPF and DMARC that rejected it, because a
domain-authorisation check is the only control that can tell those two cases apart. An
`IsConnectionAllowed` allowlist covering the same CIDRs would have accepted every one of those
messages before the greeting was even sent.

The general rule: an IP allowlist answers *which network the connection came from*. SPF, DKIM and
DMARC answer *which domain the sender is authorised to use*. On shared infrastructure — Microsoft
365, Google Workspace, any large ESP — the first question is nearly uninformative and the second is
the one that matters. On dedicated infrastructure the two coincide, which is why an IP allowlist
looks sufficient right up until the sender is a hyperscaler.

Consequences for a filter implementation:

- Do not use `IsConnectionAllowed` as anti-spoofing or anti-spam control against Microsoft or
  Google ranges. Use it to *narrow* who may connect at all, then let SPF/DMARC decide the domain
  question — the two are complementary, not alternatives.
- Do not disable SPF checking because a range is allowlisted. `IsAllowedSenderSpfVerified` never
  sees an SPF `Fail` (it is refused earlier with `554 5.7.23`), so a filter cannot re-permit a
  spoofed sender there even by accident — but turning SPF off in `ServerOptions` removes the only
  control that was catching this.
- An allowlisted range plus a permissive `CanProcessTransaction` is a relay for anyone with a
  tenant on that platform.
- Where per-tenant identity is actually required, an IP or CIDR check cannot provide it. See
  [TENANT-CRYPTO-AUTH.md](TENANT-CRYPTO-AUTH.md) — the shared-identity limitation and the
  per-customer mTLS approach that does establish it.

---

## Related

- [ARCHITECTURE.md](ARCHITECTURE.md) — session lifecycle and where filters sit in it
- [TESTING.md](TESTING.md) — `GreetingAndFilterTests` covers connection rejection, custom and
  whitespace-only fail messages, and endpoint propagation
- [RELAY-SENDER-AUTHORIZATION.md](RELAY-SENDER-AUTHORIZATION.md) — why an Exchange Online allowlist
  cannot authorize a sender, and the observe-first rollout for SPF/DMARC
