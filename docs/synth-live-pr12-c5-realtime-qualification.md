# PR #12 — C5 realtime qualification and overload recovery

**Status (2026-11-15): C5(a) and C5(b) AUTOMATED ACCEPTED. C5 OPEN** pending C5(c–f), target-machine measurements, producer recovery and supported load limits. This document is subordinate to the [PR closing audit](synth-live-pr12-closing-audit.md). C1–C4 are accepted; C6 physical listening, C7 authorization and the [all-nonstructural Synth TODO](todo.md) are open. PR #12 is draft, unmerged.

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

### C5(b) — bounded FIR/Multi worker saturation, latest-wins and retiring-voice cancellation (2026-11-15)

**Automated ACCEPTED at test-only implementation HEAD** [`b07c876`](https://github.com/logiclrd/Heresy/commit/b07c876172b6ff5084a0e0c019ebd9c32bbce348). [**GitHub Actions #43735932634**](https://github.com/logiclrd/Heresy/actions/runs/43735932634) **COMPLETED SUCCESS: 3,818/3,818 tests** — Render **1,218**, Playback **357**, Core 911, Render.File 29, Scripting 105, UI 1,198; full Release, Windows apphost/icon, versioned release smoke and Linux ARM64. No renderer, transport, device callback or scheduler implementation change was necessary in this slice.

**Three distinct deterministic gates, not a 300-request CPU benchmark:**

1. **Worker admission cap and deferred retry.** `FirPreparationSchedulerTests.SaturatedMultiQueueRefusesExcessAdmissionButRecoversWhenDrained` parks the single dedicated preparation worker on a deliberately blocked first item, then admits exactly `FirPreparationScheduler.MaximumQueuedMultiExpansions = 64` pending Multi items. A further item is refused immediately while all queue slots are occupied. After unblocking, all admitted items complete and the rejected item can be enqueued and completed at the next producer-side opportunity. **Peak queue = 64; every candidate completes once, zero scheduler faults.** The queue cap limits *waiting* work and excludes the single already-dequeued active item; it is not a global per-note or voice-count ceiling. The test uses tiny tagged work items to prove **admission/retry semantics**, not to pretend they exercise real Multi DSP construction.
2. **FIR latest-wins across 12 distinct frozen mailboxes.** `FirPreparationSchedulerTests.ManyFirMailboxesCoalesceRapidSupersessionIntoLatestFrozenSnapshots` blocks the worker while twelve real FIR Filter-chain mailboxes each receive **25 newer numeric cutoff proposals**. No additional worker slot is enqueued per superseding revision; `TakeReady` is null until work completes, and each of the twelve eventually yields exactly one prepared kernel that matches its *last* frozen proposal, rejecting both its initial and immediately preceding cutoff. **Peak queued = 11** (the twelfth mailbox was already dequeued), **12 final preparations**, no faults. All cutoff snapshots are validated by `PreparedFirControlBatch.Matches`. This tests latest-wins and identity without inferring a device scheduling bound.
3. **Real held Synth retirement with a saturated queue.** `FmSynthPreparationSaturationTests.RetiredHeldNoteCancelsQueuedExpansionBehindSaturatedMultiWorker` starts an actual note with two Constant-backed Multi constituents, publishes a frozen **2→9** voice-count revision (past the four-voice audition reserve), verifies old **0.03125** PCM while its worker preparation is pending, and blocks that *actual* Multi request. It then fills all **64 waiting slots**, cuts the held note before the worker may construct children, and resumes the worker. The retired expansion is cancelled **once**, with **zero prepared Multi children** and no worker fault; all filler work drains, the rejected extra item retries, and a separately started new note produces actual latest nine-constituent PCM **0.140625** rather than resurrecting the retired note. **Peak queue = 64, cancellations = 1, abandoned prepared children = 0, completed filler/retry work = 65.** The retired voice's original PCM is not rebuilt on the worker; a new note is entitled to the current authored graph.

These gates are independent of CI-runner wall-time jitter: they coordinate on `ManualResetEventSlim` and assert completions, exact queue membership, frozen snapshot identity, original/new PCM and error/cancellation counts, **not** a millisecond threshold. The tests use `SpinWait` timeouts only to detect a stuck worker. The device callback remains the preexisting copy-only `BufferedAudioOutputSource.Render` path; C5(a) separately verified its zero managed allocation under an intentional producer stall. Off-PCM Multi construction may allocate; the assertion here is that cancelled obsolete work does **not** keep prepared child states alive, not that all worker construction is allocation-free.

**Bounded status:** C5(b) closes scheduler-admission, cross-mailbox FIR supersession and real cancelled-on-retirement queue saturation. It does **not** certify fairness under unlimited FIR traffic, real producer callback rate under 17 heavy Multi children, worker starvation under unbounded host load, or physical audio recovery. **C5(c)** must now measure same-held-note **3→17→3** retirement **and producer throughput recovery** (including a fresh-note comparison) under a deliberately overbudget interval; **C5(d)** combines mixed concurrent graphs, while C5(e/f) require device evidence and load-envelope decisions. **C5 stays unchecked**; C6 physical listening, C7 signoff, overall Synth TODO and PR #12 merge remain open.

### Existing baseline profiles (do not duplicate)

The existing `PlaybackRequestAudioSourceFactoryTests.PacedCallbacksProfileSustainedMultiExpansionWithoutHardwareDeadlineAssumptions` runs a one-second mono 48-kHz, **8-note 2→64-Multi**, 192-callback paced simulation. It collects actual producer render-block and worker counters, callback jitter/cost/allocations, CPU, underruns and buffered-ring minimum; zero callback allocations and finite PCM are correctness requirements, whereas elapsed times and underruns are host-dependent **observations**. `BufferedLiveMultiBurstReportsWorkerCostsWithoutCallbackUnderruns` is a separate prebuffered controlled 6-voice 2→64 burst, and its zero underrun result is valid **only because each callback waits for enough already-written frames**. Other available performance evidence is in [profiling Synth graph CPU cost](synth-render-profiling.md) and [mixed Filter realtime qualification](filter-realtime-qualification.md), whose nine-stage/long-FIR cases **already miss their nominal 256-frame deadline on a shared runner**. No claim that heavy FIR, high voice counts or overbudget work can always render in realtime is permitted.

## Reproducibility and follow-on slices

- **C5(b), ACCEPTED:** worker admission bound at 64 waiting Multi items, deferred retry, 12 concurrently superseded FIR mailboxes each resolving latest of 25 proposals, and a real held 2→9 Multi whose queued preparation is retired behind 64 pending items. No stale prepared child, and fresh Note On uses the edited graph; detailed verified results above. No physical deadline claim.
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

For C5(b)'s deterministic worker gates (no audio device necessary):

```sh
dotnet test Heresy.Tests.Render/Heresy.Tests.Render.csproj -c Release \
  --filter 'FullyQualifiedName~SaturatedMultiQueueRefusesExcessAdmissionButRecoversWhenDrained|FullyQualifiedName~ManyFirMailboxesCoalesceRapidSupersessionIntoLatestFrozenSnapshots'

dotnet test Heresy.Tests.Playback/Heresy.Tests.Playback.csproj -c Release \
  --filter 'FullyQualifiedName~RetiredHeldNoteCancelsQueuedExpansionBehindSaturatedMultiWorker'
```

The latter paced test runs for roughly one second of simulated callback audio; it **does not** require a sound device. Run longer/CPU-profiling experiments separately from paced measurements. Add sample rate, voice count, rendered blocks, buffer capacity, callback period, host CPU, OS/.NET version and observed underrun/queue data to every eventual C5(e) hardware result.

**C5(a) acceptance gate:** exact-commit Release test suite, desktop package smoke, Linux ARM64 and above two deterministic tests green. These tests should establish baseline invariants, **not** certify C5 completion.
