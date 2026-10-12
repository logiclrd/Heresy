# Heresy FM Filter: measured mixed-family real-time qualification

**Measured:** 11 October 2026 (America/Winnipeg; Actions logs dated 12 October UTC).  
**Source workload:** `Heresy.Tests.Render/FmMixedFilterRealtimeQualificationTests.cs`  
**Full-quantum instrumentation:** [GitHub Actions run #38195789865](https://github.com/logiclrd/Heresy/actions/runs/38195789865) (`c9a6639`).  
**First instrumentation/reference:** [GitHub Actions run #38195569999](https://github.com/logiclrd/Heresy/actions/runs/38195569999) (`2967ad9`).

## Scope, operating conditions, and what is measured

Runner: **Ubuntu 24.04.5 LTS, x64, .NET 10.0.12**, four reported logical processors, shared GitHub Actions host, NUnit nonparallel qualification fixture, Release build. Sample rate **48,000 Hz**, source period **3,072 frames per voice**, PCM render block/quantum **256 frames = 5.333 ms of output audio**. A single worker serially advances one or four independent FmSynthSound note states, mirroring the worker's per-voice execution rather than simulating simultaneous DSP threads. Before timing, each invocation renders a 128-frame JIT/preparation warmup.

Each invocation passes audio through **nine serial filter stages**: OnePole, SvfLowPass, BiquadPeaking, LadderLinear, LadderSaturated, AllPass, CombFeedback, CombFeedforward, and FirLowPass. Eight numeric families retune at audio rate, including nonlinear Ladder drive in **7.5–9.5**, resonance in **0.90–0.98**, fixed structural oversampling **4×**, and the other modulated controls. FIR impulse is fixed at 127 or **2,047 taps**, Linear phase, while other controls change per sample. The test captures Stopwatch wall duration, process TotalProcessorTime, individual per-voice 256-frame block ticks, complete *all-voice* quantum ticks, and `GC.GetAllocatedBytesForCurrentThread()` for the warmed render interval. Every PCM output is asserted finite and nonzero.

Theoretical FIR convolution work for this one serial FIR stage is **voices × 3,072 × tap count** multiply-add operations, before other filters and graph evaluation. The saturated Ladder solver has a fixed 12-iteration ceiling for each of four oversampled samples per host frame. This is a conservative deterministic arithmetic/work bound, not measured CPU cycles, an instruction bound for the entire process, or an operating-system scheduling guarantee.

## Actual observed Release CI results

These are observations from the shared GitHub-hosted runner, **not portable thresholds, guaranteed WCET, or physical device measurements**.

| Voices | FIR taps | FIR multiply-adds | Wall time for 64 ms audio | CPU time | Worst full-voice quantum | Worst quantum / 5.333 ms deadline | Managed allocated bytes |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 127 | 390,144 | 39.286 ms | 39.804 ms | 3.3446 ms | 0.627× | **0** |
| 4 | 127 | 1,560,576 | 173.422 ms | 174.259 ms | 25.5987 ms | 4.800× | **0** |
| 1 | 2047 | 6,288,384 | 88.526 ms | 88.729 ms | 7.5456 ms | 1.415× | **0** |
| 4 | 2047 | 25,153,536 | 362.674 ms | 614.443 ms | 34.6475 ms | 6.496× | **0** |

The same stress profiles varied materially between executions; for example, the earlier run #38195569999 measured 39.326 / 230.204 / 92.937 / 728.973 ms (in the above row order). This demonstrates why hosted runner elapsed time is **not a stable worst-case latency bound**. Process CPU time may include managed runtime/other threads; it is diagnostic, not per-DSP thread isolation. The worst *full* quantum is the sum of rendering every selected voice before the output deadline, not just the worst individual voice block.

The single-voice 127-tap profile completed both total audio and worst observed quantum faster than their audio duration on this runner. **The other three stress profiles missed at least the measured 256-frame deadline**, so no blanket hard-realtime claim is warranted. In particular, allocation-free code and bounded loops are necessary but **insufficient** for uninterrupted playback with arbitrary FIR taps, effect depth, oversampling, and voice counts.

## Integrity and overload contracts verified

- All eight accelerated scalar families can coexist in a serial pipeline with the maximum permitted **2,047-tap Linear FIR**; their control/effect states are private to each active voice, and no large FFT or FIR redesign is performed in live PCM.
- Without an explicit prepared candidate, a changed FIR port **holds its last validated coefficients and all simultaneously dirty IIR/Comb stages**, rather than silently advancing only a subset. Maximum-phase **129-tap** pending requests exceed the unchanged **131,072 work-unit** bound (8,192-point FFT; 132,620 units) and cannot publish, whereas 127-tap nonlinear-phase requests meet the single-stage budget.
- The existing **four-stage / 131,072-work-unit / 2-MiB** total preparation policy is conservative and enforced over whole batches. Nonfinite controls (NaN, +/−Infinity), out-of-descriptor, at/over-Nyquist requests reject; later valid proposals are recoverable without stale kernel publication. Mixed-family invalid OnePole prevents preprepared Maximum FIR, Biquad/Ladder/Comb changes and other serial updates from partially applying.
- A successful preprepared 127-tap Maximum FIR update with changing other stages is replayed **exactly** by absolute frame 64 preparation in contiguous, split-chunk, multiple independent voice, and backward-seek schedules. FFT/coefficient preparation happens only in the explicit control-side method between Render blocks; ordinary Render never schedules it automatically.
- The measured steady PCM region allocates **0 managed bytes** for each of the four specified workloads after warmup. `FmSynthSound.Render` uses stack spatial-gain scratch for 1–16 channels, avoiding the earlier per-block heap array; exotic outputs beyond 16 still use an allocation fallback. Initial note state construction, backward-seek reconstruction, and deliberate off-PCM FIR control preparation **may allocate** and must not execute in a device callback.

## Qualification outcome and operational implications

**Result:** Numeric correctness, bounded admission, deterministic replay, mixed-family atomicity, and warmed PCM allocation qualifications pass. The CPU/deadline assessment **fails for high-load configurations**, including one voice of the stated nine-stage 2,047-tap test and four voices even at 127 taps under high resonance/drive. Accordingly this task reports a **measured supported/unsupported envelope**, not general hard-real-time certification.

The production design retains a dedicated PCM rendering worker and a bounded ring buffer; SDL/system callbacks drain rather than preparing filters or owning DSP computation. Real target-machine qualification must account for concurrent voices, IIR cost and oversampling, 2,047-tap FIR O(N) convolution, buffer depth, worker scheduling, output channel count, and other parts of the song renderer. No automatic CPU-resource admission gate or automatic FIR preparation scheduler is implemented by this milestone. Applications hosting these features must set practical voice/effect budgets or fail gracefully with documented underrun/overload behavior rather than promising arbitrary concurrency. Real devices and other OS/CPU families require their own latency/headroom tests.

**Scope boundary:** This closes the discrete measurement/correctness milestone; it does not implement Instrument tone, note-level, channel-output, or master-output Filter bindings, nor guarantee physical hard-real-time playback under all valid filter settings. No release tag was created.

## Note Filter Envelope numeric-control qualification — 12 October 2026

The new `NoteFilterEnvelopeRealtimeControls` engine processes ordinary note-event and recursively flattened inherited Filter parameter Envelopes through the existing atomic two-phase typed scalar kernels. Attachment, release, replacement and detachment rebuild only control-side routing metadata; per-sample code preflights descriptor bounds and mixed stage proposals, retains the source voice's immutable authored base, and uses the same 32-frame compatible state interpolation as full `StreamingFilterChain.Configure`. Separate regression tests compare frame-exact PCM for mixed Feedforward Comb, OnePole and Biquad Peaking, two inherited layers with colliding stage IDs, invalid all-or-nothing batches, delayed FIR plan publication and nonlinear FIR budget fallback. Session-level tests verify explicitly matching FIR control boundary behavior.

**Observed GitHub-hosted Linux x64 Release benchmark** (Ubuntu runner, .NET 10.0.12, four reported processors; 48 kHz, 4,096 frames = 85.33ms audio; three serial stages with continuously changing numeric controls):

| Workload | Observed PCM/retune wall time | Relative to audio duration | Managed PCM/control allocations |
| --- | ---: | ---: | ---: |
| 1 note voice | 6.59 ms | 0.08× | 0 bytes in measured region |
| 4 independent note voices | 30.18 ms | 0.35× | 0 bytes in measured region |
| 4,096 additional scalar-only frame retunes | 5.179 ms | 0.06× | 0 bytes |

These are **test workload observations**, not hardware guarantees. The independently measured more expensive nine-stage/saturated-ladder/2,047-tap cases above may still fail their real-time deadline.

**FIR qualification and host boundary:** The FIR policy maintains the existing at-most-four-stage, 131,072 work-unit/2 MiB bounded preparation policy for an *entire* proposal, including multiple inherited Filter layers; candidate publication matches stage/layer identity and exact requested values. No FIR coefficient work executes inside PCM. `PlaybackSession.PreparePendingNoteFilterEnvelopeControls` is an explicit synchronous off-PCM control boundary; `OfflinePlaybackRenderer` invokes it between export blocks and may spend more wall time than the emitted audio. Real-time playback still lacks a separate asynchronous producer for these requests. A new independently actionable TODO tracks non-PCM-worker scheduling/hand-off, scope lifetime/seek, automated cadence and physical device listening. Until then, continuously moving audio-rate FIR Envelopes may remain at their previous valid coefficients during live playback. Do not claim complete real-time FIR automation or universal hard-realtime certification.
