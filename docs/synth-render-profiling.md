# Profiling Synth graph CPU cost

PR #12's PCM producer has two separate problems to measure: deadline
scheduling/underruns at the buffered device boundary and the actual
single-threaded work needed to render Synth voices. The first belongs
to the existing paced callback regression in
`Heresy.Tests.Playback`. For the second, use the diagnostic
`SynthPairedRenderProfileTests` in `Heresy.Tests.Render`.

## What the paired test controls

The test compiles independent Synth sounds and creates four independent
voices per arm, each with 64 running Multi constituents. It warms up both
arms, then renders the same 48-kHz PCM frames in alternating A/B,
B/A order for seven rounds (or a larger explicit local count). Each
arm uses its own persistent DSP state and exactly the same frame
boundaries and seed identities. Every paired PCM buffer is compared
**exactly**; this is a correctness invariant, not a timing assertion.

Three comparisons run in one process:

- Sine versus the *same* Sine graph: a control for scheduling/order
  and host noise; a ratio away from 1 shows the experiment is not
  completely isolated.
- One unmodulated Sine oscillator versus that oscillator followed
  by a Constant(1) and an identity Multiply operator.
- The equivalent Square waveform comparison. Its cheaper native
  waveform helps expose general node-dispatch overhead.

In the latter comparisons both arms produce the same PCM, but the
reference has **one additional authored graph node and the Operator**.
Consequently, B/A is a *graph complexity comparison*, **not** the
measured speedup of the fast-path optimization in isolation. For an
exact optimization A/B, build a separately versioned generic renderer
with identical graph structure or add a well-reviewed profiling-only
control, avoiding any new per-sample production overhead or global
state.

The report records median microseconds per output frame for each arm,
median paired ratio, the range of individual paired ratios, actual
runtime/OS/logical CPU count and rendering-thread managed allocations.
It does not measure thread-specific CPU cycles, cache misses or
allocation sizes elsewhere in the audio pipeline. There are **no**
hardware-dependent timing thresholds or underrun assertions.

## Reproduce the timing comparison

Build in Release, then run the one test fixture directly:

```sh
dotnet test Heresy.Tests.Render/Heresy.Tests.Render.csproj \
  --configuration Release \
  --filter FullyQualifiedName~SynthPairedRenderProfileTests \
  --logger 'console;verbosity=detailed'
```

For longer recordings on Linux/macOS:

```sh
HERESY_SYNTH_PROFILE_ROUNDS=200 dotnet test \
  Heresy.Tests.Render/Heresy.Tests.Render.csproj \
  -c Release --filter FullyQualifiedName~SynthPairedRenderProfileTests \
  --logger 'console;verbosity=detailed'
```

For Windows PowerShell:

```powershell
$env:HERESY_SYNTH_PROFILE_ROUNDS = '200'
dotnet test Heresy.Tests.Render/Heresy.Tests.Render.csproj -c Release --filter FullyQualifiedName~SynthPairedRenderProfileTests --logger 'console;verbosity=detailed'
Remove-Item Env:HERESY_SYNTH_PROFILE_ROUNDS
```

The environment variable is deliberately limited to 7–512 rounds;
unset it for the short CI default. Run at least two independent
local trials when interpreting small differences, and keep the
same .NET version, architecture, sample count and power/CPU
governor when comparing revisions. Do not co-run the paced realtime
stress test during controlled timing.

## Capture CPU samples, separately from timing runs

On Linux with `perf` installed and suitable performance-event
permissions, prefer an independent Release run long enough for a
call-stack sample profile:

```sh
dotnet build Heresy.Tests.Render/Heresy.Tests.Render.csproj -c Release
HERESY_SYNTH_PROFILE_ROUNDS=200 \
DOTNET_PerfMapEnabled=3 DOTNET_EnableWriteXorExecute=0 \
  perf record --no-buildid-mmap -e cycles:u -F 99 \
  --call-graph dwarf -o heresy-synth.perf.data \
  -- dotnet test Heresy.Tests.Render/Heresy.Tests.Render.csproj \
  -c Release --no-build \
  --filter FullyQualifiedName~SynthPairedRenderProfileTests
perf --no-pager report -i heresy-synth.perf.data \
  --stdio --stdio-color=never --no-children -g none --sort symbol \
  > heresy-synth-report.txt
```

This example captures userspace cycles and uses textual .NET JIT
perf maps. On Ubuntu 26.04/.NET 10, the default JIT double mapping
was observed to leave samples as `memfd:doublemapper (deleted)`
hex addresses; the **profiling-only** `DOTNET_EnableWriteXorExecute=0`
environment setting, combined with `DOTNET_PerfMapEnabled=3`,
resolved Heresy's actual method names. Disabling the JIT's W^X
memory-protection mode is not recommended for regular application
execution or benchmark throughput comparisons. It is used here
only to resolve CPU hotspots in a temporary diagnostic subprocess.

On machines reporting `perf_event_paranoid=4`, unprivileged perf
sampling can be blocked. With permission to administer the machine,
temporarily setting `sudo sysctl -w kernel.perf_event_paranoid=2`
may permit this userspace-only run; first record the original value
from `/proc/sys/kernel/perf_event_paranoid` and **restore it immediately
after recording**. This sysctl affects the entire host while changed.
Do not make a permanent system-wide relaxation merely to run these
tests.

`--no-buildid-mmap` suppresses a Linux-kernel build-ID warning on
affected systems. `--no-pager` and `--stdio-color=never` produce
plain-text reports, avoiding terminal escape sequences in `less`.
The flat report (`-g none`) prioritizes exclusive hotspots;
drop `-g none` for caller-stack detail. JIT helper inlining
means that exclusive samples attributed to a managed method may
also represent inlined callees. Keep instrumentation and timing
measurements separate.

For Windows, run the longer test in one terminal and attach a
CPU sampling profiler to the **actual testhost process** (not the
parent `dotnet test` launcher). `dotnet-trace ps` can enumerate
diagnostic processes, and `dotnet-trace collect --process-id <PID>
--profile cpu-sampling` can sample a selected testhost. Open the
trace in a suitable trace viewer. Since sampling introduces its own
overhead, do not report profiled elapsed times as an uninstrumented
throughput measurement.

Inspect inclusive and exclusive samples under
`FmSynthSound.EvaluateFrame`, `EvaluateOscillator`,
`FmOscillatorWaveformEvaluator.EvaluateNormalized`,
`EvaluateMulti`, `RenderMultiChildFrame`,
`GetBaseEndFrameExclusive`, `GetEndFrameExclusive`,
`PitchTrajectory.GetMultiplier` and
`Spatializer.CalculateGain`. If a method is inlined, look at its
caller/assembly mapping rather than concluding it costs zero.
Classify every result by waveform type and rendered constituent count.

## Isolate the graph shape before interpreting method percentages

The ordinary paired test runs three comparisons in one NUnit
testhost. A flat `perf` report of that process mixes direct Sine,
Sine with identity Multiply, and Square with identity Multiply.
It cannot tell which path owns the sampled calls.

To isolate **one shape per test process**, set
`HERESY_SYNTH_PROFILE_CASE` to one of:

- `sine-direct`: single-oscillator Sine children, no Operator.
- `sine-generic`: equivalent Sine children followed by Constant(1)
  and identity Multiply.
- `square-direct`: single-oscillator Square children.
- `square-generic`: equivalent Square children with identity Multiply.

An unset selector retains the complete ordinary paired PCM test and
all of its exact-output assertions. A nonempty unrecognized selector
fails immediately rather than silently profiling the wrong case.
The isolated mode warms a single graph/voice set and renders its
sequential blocks; its printed duration is diagnostic, not a CI
performance threshold. The same test-filter command selects the
fixture in both modes.

**After checking out the desired PR commit**, build its Release testhost:

```sh
dotnet build Heresy.Tests.Render/Heresy.Tests.Render.csproj -c Release
```

Then, for a Linux userspace-only CPU profile of each case (after
restoring any host `perf_event_paranoid` changes you no longer need):

```sh
mkdir -p synth-perf
for shape in sine-direct sine-generic square-direct square-generic; do
  HERESY_SYNTH_PROFILE_CASE="$shape" \
  HERESY_SYNTH_PROFILE_ROUNDS=512 \
  DOTNET_PerfMapEnabled=3 \
  DOTNET_EnableWriteXorExecute=0 \
    perf record --no-buildid-mmap -e cycles:u -F 99 \
      --call-graph dwarf -o "synth-perf/$shape.perf.data" \
      -- dotnet test Heresy.Tests.Render/Heresy.Tests.Render.csproj \
      -c Release --no-build \
      --filter FullyQualifiedName~SynthPairedRenderProfileTests

  perf --no-pager report -i "synth-perf/$shape.perf.data" \
    --stdio --stdio-color=never --no-children -g none \
    --sort symbol > "synth-perf/$shape.txt"
done
```

The loop yields four separately symbolized profiles, one from each
NUnit testhost invocation. Do **not** regard their percentages as
absolute cycle counts or compare report totals without considering
the `perf` sample count and the duration of the measured phase.
The profiler records startup, JIT, and test work too, although the
long single-case render should dominate. Sampling and diagnostic
JIT mapping differ from uninstrumented performance conditions.

First compare `GetBaseEndFrameExclusive`,
`RenderMultiChildFrame`, `EvaluateOscillator`, and
`EvaluateOperator` in the **Sine direct** vs **Sine generic**
reports. If most end-check samples belong to the generic reference,
that explains the surprising overall movement when optimization
made direct oscillator children cheaper. Then check Square's two
graphs to separate waveform expense from lifecycle dispatch.
Only if a path still needs explanation should you next inspect its
individual instructions with `perf annotate`. Do not add a
generalized lifetime shortcut solely on the mixed-workload samples.

## Repeated long-form JIT instruction samples

The first per-shape Linux profiles on a Ryzen 7 7840HS attributed
`GetBaseEndFrameExclusive` to **34.19%** of exclusive samples for
`sine-direct`, **31.78%** for `square-direct`, but only 1.98%
and 2.53% for the generic identity-Multiply shapes. The first
`sine-direct` JIT annotation placed about **600 samples** in
`GetBaseEndFrameExclusive`, of which 60.16% were attributed to a
nullable stack copy and 32.84% to two nearby conditional tests.
The other graph-traversal instructions had no samples. This
corresponds to the method's *held/no-Source early return*, rather
than traversing the graph. However, sampled instruction pointers
can skid and correlated CPU samples are not independent
observations; these percentages are not actual per-instruction
cycle accounting.

To increase sample yield and test reproducibility, the opt-in
**isolated** mode now allows up to **65,536 rounds**. Normal
paired exact-PCM tests keep their **512-round cap** and default
seven-round CI workload. The isolated mode still uses one shape
per process, persistent voice state and the same waveform graph;
it neither asserts an execution-time threshold nor changes the
production renderer.

Use the **same JIT dump settings** that already produced usable
annotation on this laptop, and keep the sampling rate at 499 Hz.
Start with one 8,192-round `sine-direct` calibration:

```sh
mkdir -p synth-perf/long

HERESY_SYNTH_PROFILE_CASE=sine-direct \
HERESY_SYNTH_PROFILE_ROUNDS=8192 \
DOTNET_PerfMapEnabled=2 \
DOTNET_PerfMapStubGranularity=4 \
DOTNET_EnableWriteXorExecute=0 \
perf record -k mono --no-buildid-mmap -e cycles:u -F 499 \
  --call-graph dwarf \
  -o synth-perf/long/sine-direct-calibration.perf.data \
  -- dotnet test Heresy.Tests.Render/Heresy.Tests.Render.csproj \
  -c Release --no-build \
  --filter FullyQualifiedName~SynthPairedRenderProfileTests

perf inject --jit \
  -i synth-perf/long/sine-direct-calibration.perf.data \
  -o synth-perf/long/sine-direct-calibration-injected.perf.data

perf --no-pager annotate \
  -i synth-perf/long/sine-direct-calibration-injected.perf.data \
  --stdio --stdio-color=never --no-source --percent-limit=1 \
  > synth-perf/long/sine-direct-calibration-annotate.txt
```

Inspect the `GetBaseEndFrameExclusive` annotation's
`(N samples, percent: local period)` header. The earlier
512-round run had about 600 samples *in that method*; 8,192
rounds may produce several thousand samples, though startup,
tiering, profiler overhead and differing CPU load prevent
reliable linear scaling. If fewer than approximately 5,000
samples land in the method, increase the isolated rounds to
16,384 or 32,768 for subsequent recordings (not `-F`).
If the count is at least roughly 5,000, keep **8,192 rounds**
for the repeated comparison.

Next, capture **three fresh, independent testhost recordings**
of both `sine-direct` and `sine-generic`, using distinct
`*.perf.data` outputs, identical rounds/sampling rate, and
the same JIT settings. Collect a flat `perf report` and
`perf annotate` for each run with the commands above, replacing
the output basename. Compare the independent within-method
hot-instruction proportions before pooling counts; the generic
recordings also show whether its short lifetime path receives
samples. An optional third independent `square-direct`
set can test whether the pattern is waveform-dependent,
but Sine is the first priority.

Aiming for 5,000–10,000 per-method samples per run is a
pragmatic first target, not a statistical guarantee. Additional
samples reduce random uncertainty but **do not correct**
nonrepresentative workloads, instruction-pointer skid, JIT
tier transitions, or inlining differences. Therefore record
`perf` capture sample counts, the per-method annotation counts,
and each diagnostic `Isolated Synth renderer:` timing line.
Only consider a larger 50,000–60,000 sample study if the
independent runs disagree or instruction-level distinction
materially affects a proposed optimization.

## Interpretation and remaining acceptance

A strongly consistent paired B/A difference above the same-vs-same
control's noise establishes an interesting cost difference between
the two graph shapes on that host. It does not prove which exact
instruction accounts for the difference. In particular, additional
Operator dispatch, a second compiled node and audio-value routing
all contribute to the generic reference's overhead.

Do not derive a maximum supported number of realtime voices from
single-thread throughput alone. The device ring, GUI, FIR/Multi worker,
OS scheduling, release tails and other application activity also
consume resources. Physical listening/device callback tests are
still necessary, as are complete non-structural live-parameter
migrations and pending crossfade behavior.

Record observations in `docs/synth-live-parameter-plan.md`. Keep
PR #12 draft and the all-properties Synth TODO open.
