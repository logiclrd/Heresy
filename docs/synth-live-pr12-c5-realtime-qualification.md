# PR #12 — C5 realtime qualification and overload recovery

**Status:** C5(a) initial deterministic ring-boundary qualification. **C5 OPEN** pending C5(b–f), target-machine measurements and agreed bounds. This document is subordinate to the [PR closing audit](synth-live-pr12-closing-audit.md). C1–C4 are accepted; C6 physical listening, C7 authorization and the [all-nonstructural Synth TODO](todo.md) are open. PR #12 is draft, unmerged.

## Acceptance boundary

The buffered audio architecture has **two distinct execution deadlines**: the PCM **producer** renders source/DSP blocks on a dedicated worker, and the OS/device **callback** copies already-prepared samples from a bounded ring. The off-PCM FIR/Multi preparation scheduler is a third distinct worker. A quick callback does **not** imply the producer met its deadline, nor that the buffer never ran dry. Conversely an intentionally over-budget producer block does not mean a callback deadline was missed when the ring had enough headroom.

Treat the following as different observations:

| Measurement | Already available in code | Scope |
| --- | --- | --- |
| Callback rendering duration, scheduling jitter, late callback starts, callback allocations | Existing `PlaybackRequestAudioSourceFactoryTests.PacedCallbacksProfileSustainedMultiExpansionWithoutHardwareDeadlineAssumptions` | *Simulated* 192 × 256-frame callbacks at 48 kHz; shared-runner scheduling diagnostic, not SDL/device measurement |
| Buffer underruns, number of buffered frames and recovery | `BufferedAudioOutputSource.UnderrunCount`, `BufferedFrames`; C5(a) controlled-stall test | One increment per short callback, not one per missing frame; no musical-clock advance on silence padding |
| Producer block count, output frames, sum/peak block time, blocks over render quantum budget | `BufferedAudioOutputSource` when constructed with `profileProducerRender:true` | Wall-time observations include OS preemption; counters disabled for normal playback; no hard guaranteed WCET |
| Off-PCM worker queue peak, completed/cancelled Multi expansions, number of prepared children, preparation time and allocations | `FirPreparationScheduler` diagnostics exposed by `BufferedAudioOutputSource` | Counter snapshots only; no guarantee that all candidate revisions finish before an audio callback |
| DSP throughput and source render allocations | `SynthProducerScalingProfileTests`, `SynthPairedRenderProfileTests`, `FmMixedFilterRealtimeQualificationTests` | Warmed/test-host diagnostics, not proof of actual device scheduling, ring level or click-free listening |
| Actual device callback underruns, recovery under 3→17→3 and worker saturation | **C5(e)** target-machine run required | Must record sample rate, buffer size, host CPU, backend, load and observed recovery; no inferred green result from CI |

### C5(a) — deterministic boundary recovery proof

`Heresy.Tests.Render.BufferedAudioDeadlineRecoveryTests` provides two new tests:

1. **Forced producer stall:** hold the *actual dedicated ring producer* inside its underlying `source.Render` while two device-facing callbacks request four frames each. Both callbacks return all-zero silence and produce precisely two underrun events. The callback makes **zero managed allocations** (measured after an initial warm call), does not call the source and consumes **no musical time**. The intentionally blocked 48-kHz four-frame producer block reports at least one over-budget block. After unblocking, the ring contains 1–16, then 17–32 original samples without skip/replay, missing frames, buffer-reset, session recreation or an additional underrun. The measured callback and producer elapsed times are printed, **never used as CI device deadlines**.
2. **Profiling isolation:** profile-enabled and ordinary 48-kHz buffered sources emit exactly the same sequence and each has zero underruns when prefilled; the ordinary source leaves producer timing counters at zero, while the opt-in profiled source reports actual rendered blocks and frames. Timing collection must not change PCM content.

This is **not** a stress reproduction of C5(c)'s 3→17→3 Multi state. Earlier renderer `FmSynthMultiRetirementTests.ThreeToSeventeenToThreeStopsAdvancingFourteenRetiredChildren` already verifies 14 excess children stop advancing after the transition and no steady PCM managed allocation; C5(c) must now connect that to actual producer throughput/recovery under *deliberate concurrent load and queued worker activity* instead of repeating the child-frame check alone.

### Existing baseline profiles (do not duplicate)

The existing `PlaybackRequestAudioSourceFactoryTests.PacedCallbacksProfileSustainedMultiExpansionWithoutHardwareDeadlineAssumptions` runs a one-second mono 48-kHz, **8-note 2→64-Multi**, 192-callback paced simulation. It collects actual producer render-block and worker counters, callback jitter/cost/allocations, CPU, underruns and buffered-ring minimum; zero callback allocations and finite PCM are correctness requirements, whereas elapsed times and underruns are host-dependent **observations**. `BufferedLiveMultiBurstReportsWorkerCostsWithoutCallbackUnderruns` is a separate prebuffered controlled 6-voice 2→64 burst, and its zero underrun result is valid **only because each callback waits for enough already-written frames**. Other available performance evidence is in [profiling Synth graph CPU cost](synth-render-profiling.md) and [mixed Filter realtime qualification](filter-realtime-qualification.md), whose nine-stage/long-FIR cases **already miss their nominal 256-frame deadline on a shared runner**. No claim that heavy FIR, high voice counts or overbudget work can always render in realtime is permitted.

## Reproducibility and follow-on slices

- **C5(b):** freeze/queue/cancel/supersede FIR and Multi jobs under saturation; test bounded worker memory, latest-wins and recovery with exact old/new PCM and accurate status. Require no off-PCM allocations on the device-facing callback.
- **C5(c):** reproduce **Multi 3→17→3** with a held voice on the buffered producer and a distinct fresh note. Compare counters, retired child frame clocks and restored producer throughput after the 100-ms transition. A deliberate over-budget interval may underrun; the reduced configuration must stop doing the 14 retired constituents' expensive work.
- **C5(d):** mixed concurrent load (multiple notes, Filters and nested Multi), rapid edits, worker saturation and interruptions, checking no stale revisions, stuck older flags or unrecoverable silence.
- **C5(e):** repeat representative runs on a **real** Linux and (when practical) Windows audio backend, with source/worker queue/underrun records and callback/deadline observations. Do not equate an automated CI audio-source mock with a physical callback.
- **C5(f):** record supported and unsupported loading envelopes and concrete observed recovery; keep C5 unchecked until the real machine has supplied the required evidence. Hardware performance is workload/host-specific; never invent a universal safe voice count.

### Command lines

```sh
dotnet test Heresy.Tests.Render/Heresy.Tests.Render.csproj -c Release \
  --filter FullyQualifiedName~BufferedAudioDeadlineRecoveryTests \
  --logger 'console;verbosity=detailed'

dotnet test Heresy.Tests.Playback/Heresy.Tests.Playback.csproj -c Release \
  --filter 'FullyQualifiedName~PacedCallbacksProfileSustainedMultiExpansionWithoutHardwareDeadlineAssumptions|FullyQualifiedName~BufferedLiveMultiBurstReportsWorkerCostsWithoutCallbackUnderruns' \
  --logger 'console;verbosity=detailed'
```

The latter paced test runs for roughly one second of simulated callback audio; it **does not** require a sound device. Run longer/CPU-profiling experiments separately from paced measurements. Add sample rate, voice count, rendered blocks, buffer capacity, callback period, host CPU, OS/.NET version and observed underrun/queue data to every eventual C5(e) hardware result.

**C5(a) acceptance gate:** exact-commit Release test suite, desktop package smoke, Linux ARM64 and above two deterministic tests green. These tests should establish baseline invariants, **not** certify C5 completion.
