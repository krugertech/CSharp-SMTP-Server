# Load benchmark harness

Out-of-process load harness for measuring **server** throughput, as distinct from the in-test
harness under `CSharp-SMTP-Server.Tests/Load`, which measures the driver and the server together.

## Why this exists

The in-test harness is a good correctness test and a poor benchmark. Three properties make its
numbers unattributable to the server:

1. **The generator shares a process with the server.** Pinning "the server" to a core also pins the
   client feeding it, so a core-scaling curve describes the pair.
2. **Its delivery handler is not a no-op.** It copies each transaction into a `MemoryStream`, calls
   `ToArray`, extracts a second array, and computes SHA-256 — all inside the ACK-gated path, so all
   of it is charged to server completion time while being described as the library's ceiling.
3. **Its wall clock spans setup and teardown** — connect, greeting, EHLO, QUIT, disposal, and
   client-side digest computation — while the numerator counts only accepted payload bytes.

It also sends one flushed write per body line (thousands per message), and its concurrency ladder
grows total work with concurrency, so a flat MB/s curve across rungs says nothing about scaling.

## What this harness does differently

| Concern | In-test harness | Here |
|---|---|---|
| Process topology | Generator and server co-located | Separate processes, independent affinity |
| Delivery handler | Drains + hashes every message | `noop`, `drain` (default), or `hashing` |
| Timed region | Includes connect/EHLO/QUIT/teardown | Steady state only; connections pre-established |
| Per message | One flushed write per line | One pre-framed buffer, one write |
| Workload per rung | Grows with concurrency | Fixed total bytes (strong scaling) |
| Repeats | Single sample | Warm-up + discarded first trial + N trials |
| Build | Debug | Release |
| Attribution | Wall clock only | Per-process CPU and core utilisation for both sides |

`drain` is the default and the honest floor: it reads each body once and discards it. The pure
`noop` handler never reads the body at all, so the server buffers and discards, leaving the
ingestion path only half-exercised.

## Usage

```bash
dotnet build CSharp-SMTP-Server.Bench/CSharp-SMTP-Server.Bench.csproj -c Release

# Server on one P-core, generator on a disjoint set
dotnet CSharp-SMTP-Server.Bench/bin/Release/net10.0/CSharp-SMTP-Server.Bench.dll run \
  --server-affinity 1 --client-affinity f000 \
  --messages 900 --trials 3 --concurrency 1,4,16,64 \
  --handler drain --label server-1core --out results/server-1core.json
```

### Choosing affinity masks — physical cores, not logical CPUs

Affinity masks are hex over **logical** CPUs, and popcount is therefore a count of hardware threads,
not cores. On an SMT part, `0x3` is both threads of one physical core, not two cores. Getting this
wrong silently halves every "cores" label on the x-axis.

Enumerate `RelationProcessorCore` via `GetLogicalProcessorInformationEx` and pick **one logical CPU
per distinct physical core**. On the i7-12650H used below:

| Physical core | Logical CPUs | Class |
|---|---|---|
| 0–5 | (0,1) (2,3) (4,5) (6,7) (8,9) (10,11) | P (SMT pairs) |
| 6–9 | 12, 13, 14, 15 | E (no SMT) |

So one-thread-per-core P masks are `0x1`, `0x5`, `0x15`, `0x55`, `0x155`, `0x555` for 1–6 physical
cores. Keep the generator on P-cores too (`0xf00` = physical cores 4–5); putting it on E-cores makes
the client an unequal partner and confounds the comparison.

### Options

| Option | Default | Meaning |
|---|---|---|
| `--server-affinity` | `1` | Hex affinity mask for the server process |
| `--client-affinity` | `ff00` | Hex affinity mask for the generator process |
| `--messages` | `120` | Total messages per trial, held constant across the ladder |
| `--trials` | `3` | Recorded trials per rung (one extra is run and discarded) |
| `--concurrency` | `1,2,4,8,16,32,64` | Connection-count ladder |
| `--handler` | `drain` | `noop`, `drain`, or `hashing` |
| `--out` | — | Path for the JSON report |

## Validity checks

Every report carries `Valid` and `IntegrityErrors`. **A run with `Valid: false` must not be quoted.**
The gate fails on duplicate deliveries, messages with no id header, a distinct-id count that does not
equal the delivered count, fewer delivered than accepted, or a child runtime whose
`Environment.ProcessorCount` does not match its pinned core count. Without this, a regression that
truncates or drops bodies while still answering `250` shows up as a *faster* benchmark.

Each trial also carries `ServerCoreUtilisation` and `ClientCoreUtilisation`, sampled over that
trial's timed region only. **If the client figure approaches 1.0 the generator was the constraint**
and the server number is a floor, not a ceiling; widen `--client-affinity` and rerun.

Screen for external interference before aggregating: within one run, if the slowest trial's wall
clock exceeds roughly twice the fastest, something else on the machine interfered and the whole run
should be discarded rather than averaged in. One round in the series below tripped this at 3.9x.

## Results (2026-09-02, KAT6, i7-12650H, .NET 10.0.400, Release)

Server pinned to N **physical** P-cores (masks `0x1`/`0x5`/`0x15`), generator on physical P-cores 4–5
(`0xf00`), 1500 messages/trial, ~433 KB mean, concurrency 64, `drain` handler. Three rounds in
randomised core-count order, four recorded trials each, warm-up and one discarded trial per rung.
All runs passed the integrity gate.

| Physical cores | median MB/s | min | max | N | speedup |
|---:|---:|---:|---:|---:|---:|
| 1 (hot) | 281 | 233 | 311 | 8 | 1.00x |
| 1 (cold) | 309 | 124 | 340 | 16 | — |
| 2 | 588 | 398 | 708 | 12 | 1.90x |
| 3 | 687 | 612 | 851 | 12 | 2.22x |

The 1-core (hot) row excludes one contaminated round (3.9x internal wall-clock spread). Speedups are
taken against the **cold** baseline; see below for why.

### Thermal and warm-up effects on this machine (read before trusting any ratio)

This is a 45 W laptop part, and two opposing effects distort core-scaling ratios. Both were measured,
not assumed.

**Hot baseline, across runs.** A 1-core configuration measured immediately after multi-core rounds
reads ~10% slower than the same configuration measured after 60 s of idle (281 vs 309 MB/s median).
The cause is the PL1/PL2 turbo budget: once the long-turbo window and its tau are consumed, the chip
grants only short turbo until the budget resets. Measuring the baseline hot and the multi-core rows
against it produced **105% efficiency at 2 cores** — physically impossible, and the tell that the
reference was throttled. Against the cold baseline the artefact disappears (1.90x / 95%).

**Warm-up ramp, within a run.** Trial position matters more than expected: median by position was
187 / 294 / 322 / 327 MB/s for trials 1–4. JIT tiering, thread-pool growth and GC heap settling are
still resolving well past the single discarded trial this harness performs. Note this runs *opposite*
to turbo decay — within a run the numbers climb — so the two effects partly mask each other and must
be handled separately.

Practical consequence: insert an idle gap before each configuration, randomise core-count order,
discard 2–3 warm-up trials rather than 1, and prefer measuring **every** configuration cold so no
ratio is taken against a throttled reference.

### What this does and does not show

**It shows** that throughput scales substantially with physical cores — 2.22x on three cores against
a cold baseline — and that the earlier "poor scaling" conclusion was an artefact of the previous
harness. It also shows that the in-test harness under-reports single-core throughput by roughly 30x
(8.7 MB/s versus ~281–309 MB/s here), because its per-line flushed writes, in-path hashing and
setup-inclusive wall clock all land in the measured region.

**It does not show** where the remaining ceiling is. One further caveat:

- Per-trial CPU is sampled with `Process.TotalProcessorTime`, whose ~15.6 ms granularity is coarse
  relative to 1–3 s trials; observed values for identical work varied several-fold. **Utilisation
  figures from this harness are indicative only** and are not a sound basis for claiming the server
  is or is not CPU-bound. Establishing that needs ETW or PerfView sampling, plus memory-bandwidth
  counters, neither of which this harness collects.

No claim is made here about *what* limits throughput. Ruling out the client (doubling its cores did
not raise throughput) and observing that the `hashing` handler roughly halves throughput are two data
points; they do not establish loopback bandwidth or ACK round-trip latency as the binding constraint.

**Do not convert these into CI thresholds.** They are loopback numbers on one machine; retain the
JSON with its commit SHA for deliberate comparison instead.
