# Testing

The test project targets .NET 7 and uses xUnit. Most protocol tests start a real SMTP listener on
loopback and communicate with it using raw SMTP commands.

## Run the suite

```powershell
$env:DOTNET_ROLL_FORWARD = "Major"
dotnet test CSharp-SMTP-Server.Tests/CSharp-SMTP-Server.Tests.csproj
```

`DOTNET_ROLL_FORWARD` is needed when the machine has a newer SDK/runtime but not the .NET 7 runtime.
It can be omitted when .NET 7 is installed.

Test collections intentionally run serially. Integration tests allocate loopback ports by briefly
binding port zero and then reopening the assigned port; parallel collections would increase the
chance of another process taking that port between those operations.

## Load and integrity tests

Fast load and Office 365 relay checks run with the normal suite. To run only the load category:

```powershell
$env:DOTNET_ROLL_FORWARD = "Major"
dotnet test CSharp-SMTP-Server.Tests/CSharp-SMTP-Server.Tests.csproj --filter "Category=Load"
```

The heavy tier is opt-in because it includes high concurrency, sustained traffic, a 150 MB message,
and concurrent large messages:

```powershell
$env:DOTNET_ROLL_FORWARD = "Major"
$env:SMTP_LOADTEST = "1"
dotnet test CSharp-SMTP-Server.Tests/CSharp-SMTP-Server.Tests.csproj --filter "Category=Load"
```

Load metrics are written to `load-metrics.json` beside the test assembly. Set
`SMTP_LOADTEST_OUT` to choose another output directory. Throughput and latency are reported, not
asserted; correctness checks cover accepted-message counts, duplicate delivery, connection failures,
payload integrity, and server recovery.

## Historical load baseline

Keep this baseline to detect severe degradation. It is not a portable performance promise: loopback,
Debug builds, runtime version, CPU, storage, and message corpus all materially affect the result.

The throughput baseline was recorded on 2026-09-01 at commit `570c56e` using KAT6 (16 logical cores),
.NET 7.0.20, a Debug build, loopback networking, and a no-op delivery handler. The corpus averaged
approximately 433 KB per message.

This throughput snapshot predates the streaming implementation at `a33966f`. Use it as a historical
severe-regression reference, not for fine percentage comparisons with current code. Before tuning
performance, capture a fresh baseline on the same host and retain both results.

| Scenario | Concurrency | Messages | Failures | msgs/sec | MB/s | Volume | p95 |
|---|---:|---:|---:|---:|---:|---:|---:|
| ladder-conc-500 | 500 | 1,000 | 0 | 58 | 24.6 | 423 MB | 1,099 ms |
| ladder-conc-200 | 200 | 400 | 0 | 52 | 21.9 | 169 MB | 1,573 ms |
| sustained-1000 | 50 | 1,000 | 0 | 45 | 18.8 | 423 MB | 1,511 ms |
| ladder-conc-100 | 100 | 200 | 0 | 47 | 19.9 | 84 MB | 1,502 ms |
| max-receive-rate | 200 | 200 | 0 | 31 | 13.0 | 84 MB | 1,800 ms |
| pipelined-single-conn | 1 | 25 | 0 | 19 | 7.6 | 10 MB | 133 ms |

The concurrent runs were byte-bound at roughly 15–25 MB/s; single-connection throughput was about
7.6 MB/s. Zero failures and zero payload corruption were observed at every level. An earlier, much
smaller corpus produced 300–870 msgs/sec but only about 8–19 MB/s, which is why MB/s must accompany
message rate when comparing runs.

The large-message memory baseline was recorded after the streaming DATA implementation at commit
`a33966f`:

| Scenario | Before streaming | Streaming baseline |
|---|---:|---:|
| One 150 MB message | ~1,900 MB peak working set | ~0 ± 10 MB measured growth; ~114 MB isolated whole-process peak |
| Four concurrent 50 MB messages | ~1,900 MB peak working set | ~0 MB measured growth |

The 150 MB message completed in approximately 1.5 seconds. Before streaming, memory grew by roughly
12 times the message size and multiplied with concurrent large messages. The regression tests
`LargeMessage_150MB_IsAcceptedIntact` and `ConcurrentLargeMessages_DoNotMultiplyMemory` now assert
that growth remains below the message size rather than asserting a machine-specific megabyte value.

### Comparing future runs

- Compare the same commit configuration, runtime, build mode, corpus, and hardware whenever possible.
- Retain `load-metrics.json` with the commit SHA and environment description for any new release
  baseline.
- Use MB/s and byte volume alongside msgs/sec; message rate alone changes dramatically with corpus
  size.
- Treat any correctness failure, renewed memory growth proportional to message size or concurrency,
  or a large same-environment throughput collapse as a severe regression requiring investigation.
- Do not convert the historical throughput values into fixed CI thresholds. Correctness and the
  structural memory bound belong in assertions; performance comparisons belong in recorded runs.

## Test areas

- `AckGatingTests` and `AckGatingAdditionsTests`: delivery ordering and failure mapping.
- `GreetingAndFilterTests`, `EhloHeloTests`, and `CommandSequencingTests`: greeting and SMTP state.
- `MailFromTests`, `RcptToTests`, and `DataAndMessageTests`: transaction behavior.
- `AuthProtocolTests` and `AuthLoginInitialResponseTests`: AUTH LOGIN/PLAIN.
- `TlsStartTlsTests`: implicit TLS and STARTTLS.
- `SpfValidatorTests`, `DmarcValidatorTests`, and `SpfDmarcIntegrationTests`: offline validation using
  the loopback `DnsStub`.
- `LifecycleAndRobustnessTests`: shutdown, listener behavior, malformed input, and concurrency.
- `StreamingBodyTests`: stream-backed storage, byte preservation, lifetime, and dot-unstuffing.
- `Load/`: load, message integrity, large-message memory behavior, and Office 365 relay assumptions.

## Conventions

- Reproduce a suspected bug with an exact-behavior test before changing it.
- Keep a bug fix and its regression test in the same change.
- Prefer raw-TCP integration tests for wire behavior and exact SMTP response assertions.
- Keep throughput thresholds out of assertions; they vary by machine and build configuration.
- SPF/DMARC tests must use `DnsStub`, not the public internet.

## Review lessons

### Read the normative specification section

Do not base an RFC-conformance decision on a summary, remembered rule, or nearby section. Read the
exact normative text governing the edge case and cite that section in the code or regression test.

This mattered for null reverse-path DMARC handling. An initial review concluded that RFC 7489 did not
authorize use of the HELO identity. RFC 7489 §3.1.2 explicitly provides that exception when HELO is
needed to stand in for a null reverse-path. The empirical evidence was still valuable—a legitimate
bounce test failed under unconditional alignment—and it led to the correct SPF-`Pass` gate. The error
was allowing an unchecked specification summary to carry the conclusion.

The durable workflow is therefore:

1. Reproduce the behavior empirically.
2. Read and cite the exact normative section, including exceptions.
3. Design the fix from both pieces of evidence.
4. Re-review the conclusion, not only the implementation.

### Preserve actionable non-reproductions

A suspected processor-registration leak was not reproduced across 5,000 connections, but it has a
specific re-test trigger: initialization changing to run inline. The evidence and two-phase-init
fallback are retained in
[`KNOWN_ISSUES.md`](KNOWN_ISSUES.md#latent-processor-registration-ordering-smell-not-reproduced).

Open and deliberately accepted behaviors are tracked in
[`KNOWN_ISSUES.md`](KNOWN_ISSUES.md).
