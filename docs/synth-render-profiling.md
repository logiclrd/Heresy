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
