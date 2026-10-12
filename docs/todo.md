# Heresy — outstanding work

Only unfinished, independently actionable work belongs here. Completed
architecture and regression history are documented in the
[documentation index](README.md). The original-design programmability
features, including scripted Patterns, Sequences, Envelopes, Instruments,
virtual-voice controls, and compiled parametric effects, are implemented.

## Reusable FIR/IIR filter system

Design source: [filter-design.md](filter-design.md) (incremental implementation). Each checkbox is intended to be a separately closable, tested implementation slice. In particular, filter *definitions*, configured *instances*, and mutable *DSP processing state* are different things. Do not replace existing IT/tracker-compatible voice filters as an accidental side effect. Preserve the recursive incremental renderer, private mixdown isolation, and current native-effect ordering.

### Core model and authoring

The reusable Core Filter SongObject, stable named serial stages, independent parameter overlays and filter-chain composition are implemented. The six-root Filters document tree, four-column Avalonia projection and version-1 four-/five-root migration are implemented. Filter definitions and the ID-based instance/chain codec now round-trip through version-one .hm.json/.hm, deep-copy into playback snapshots, and support selected-definition import/remapping. The standalone Filter creation/editing UI is implemented: the Filters pane exposes + Filter; double-click/edit opens a serial-stage editor with validated defaults, hierarchical filter kinds and conflict-safe drafts. The separate descriptor-driven Filter instance editor is also implemented as an embeddable, copy-on-write component with explicit override/inheritance controls; its actual Instrument/FM/note/output hosts will be wired when those persistent owner fields are introduced.


### Reusable rendering engine and filter families

The per-invocation streaming runtime, explicit neutral/bypass factory, immutable sample-rate-dependent preparation contract, atomic transition and state-fork/reset/tail policy are implemented in Heresy.Render. The first real DSP implementation, One-Pole (TPT low/high-pass), is complete with sample-rate/Nyquist checks, history-preserving cutoff updates, structural resets, PCM spectral tests and bounded denormal-safe tails. The built-in streaming factory now executes One-Pole and all seven Audio EQ Biquads (low/high pass, low/high shelf, peaking, band-pass, notch). Biquads use stable normalized RBJ coefficients, per-channel history, sample-rate validation, 32-frame smoothing of numeric changes, bounded denormal-safe tails, and response/modulation regressions. All four State Variable Filter responses now share a topology-preserving two-integrator DSP engine with unit-centre band-pass, notch, independent channel histories, Nyquist/Q validation, 32-frame interpolation of cutoff and Q, finite/denormal safety and rapid-modulation tests. The linear four-pole ladder is also implemented: resonance is mapped strictly below its continuous-time instability boundary, the TPT feedback path is solved algebraically per frame, cutoff/resonance changes preserve four integrator histories with 32-frame smoothing, and impulse/frequency/rapid-modulation/stability/tail regressions are included. The nonlinear/saturated ladder is now also implemented with a tanh-driven implicit zero-delay feedback junction, bounded Newton/bisection solving, real 1x/2x/4x oversampling plus four-pole anti-alias reconstruction, 32-frame numeric smoothing and structural oversampling resets. Response/distortion, harmonic alias suppression, rapid modulation, history isolation, finite-input stability and bounded-tail PCM tests are included. The All-Pass phase shifter is implemented in either first or second order (two cascaded identical first-order sections), with steady-state unity gain and −90°/−180° phase at CutoffHz, 32-frame stable coefficient smoothing, structural Order resets, per-channel histories, and measured impulse/phase/frequency/streaming/tail regressions. The feedback comb IIR is implemented with an independent per-channel circular delay ring, a delayed wet tap and stable ±0.95 feedback; delay time is structural and is quantized deterministically to whole output frames with a maximum one-million-sample ring/channel. Feedback/Mix edits preserve echoes and interpolate over 32 frames; structural delays reset histories. PCM regressions cover geometric positive/negative echoes, real wet/dry mixing, spectral comb peaks and notches, atomic updates, deep-fork and stereo isolation, bounded ringing and finite tails. All four Standard Arbitrary FIR responses (low/high/band-pass/notch) now implement linear-phase windowed-sinc convolution with odd 3–2047 taps, Hann/Hamming/Blackman windows, meaningful structural transition width and Nyquist-band validation, 32-frame history-preserving coefficient transitions, and exact finite convolution tails. Fixed (N−1)/2 group-delay metadata is exposed and serially summed on streaming chains, without automatic dry-path compensation. Minimum/Maximum FIR phase modes now execute bounded configuration-time cepstral spectral factorization against the same windowed-sinc Linear reference, with Maximum the exact reversed Minimum impulse. The authored odd tap count, passband reference gain and finite tail are retained, while impulse energy moves toward the start/end; phase mode remains structural and resets histories. Reported fixed group delay remains (N−1)/2 only for Linear mode (0 fixed-delay metadata for nonlinear-phase modes, not a claim of zero acoustic latency). PCM tests independently compare frequency magnitudes, phase relation, energy centroids, structural replacement, compatible-cutoff smoothing, forks, block determinism and maximum-tap bounds; feedforward comb also now uses real two-tap FIR processing. PCM tests cover impulse symmetry, spectra, group delay, atomic reconfiguration, structural resets, stereo chunk invariance, forks, deterministic replay, bounds and tails. All 21 selectable kinds now have real DSP, including the two-tap feedforward comb with one delayed echo, 32-frame compatible gain smoothing and bounded finite tails. The reusable runtime is now connected to FM Filter graph nodes with invocation-private DSP state; Instrument tone, note-event, physical-channel and final-speaker bindings remain outstanding.


### FM graphs, Instruments and per-note uses

FM Filter-node authoring, persistence **and functional rendering** are implemented: the shared definition selector/New.../Edit Definition..., copy-on-write overrides, stage-ID/generation-keyed numeric ports, per-note streaming DSP histories, bounded validation of connected numeric values, atomic compatible updates, deterministic replay/seek, live pattern-source rendering and manually authored maximum-audio-path Tail. Ordinary source excitation stops at the natural boundary while operators and serial filter states continue draining. The additional audio-rate modulation performance hardening below remains outstanding.

The optimized FM audio-rate numeric path is implemented for **One-Pole, all four SVF responses, all seven Audio EQ Biquads, both Ladder kinds, All-Pass and both Comb kinds** (17 of 21 filter kinds), using preallocated per-note typed coefficient scratch, shared mixed-family all-or-nothing preflight/commit, RBJ equations shared with static preparation and the existing family-specific smoothing and state histories. Unchanged stages are not reconfigured. The compatibility LINQ closure lives outside the allocation-free fast method; `StreamingFilterChain.ProcessMonoFrame` handles graph-scalar samples directly. Tests compare frame-exact PCM to full atomic reconfiguration, reject invalid mixed-family multi-stage batches, check interpolation continuity/chunked replay, and bound allocations across thousands of continuously changing source samples.

The remaining FM Filter modulation performance work is split into **independently closable implementation tasks**, in recommended sequence. Check off each task as soon as its own implementation and tests pass; **do not keep reopening or expanding a completed family task** to absorb the next one. The final system-wide qualification is deliberately its own checkbox. For each accelerated family, the success criteria include no `FilterInstance.Clone`/`StreamingFilterChain.Configure` on changed PCM frames, no per-frame managed allocations after warmup, rejection of invalid values *before* any stage commits, unchanged history and transition semantics, exact PCM comparison with the existing full-preparation reference for valid updates, and deterministic chunk/backward-seek tests. Family-specific requirements follow.

- [x] **FM modulation — linear Ladder (completed 11 October 2026).** Prevalidated, allocation-free `CutoffHz`/`Resonance` coefficient updates for `LadderLinear` now retain the exact TPT algebraic feedback, all four integrator histories and 32-frame interpolation, and participate atomically with One-Pole/SVF/Biquad updates. Regression coverage compares cutoff-only/resonance-only/dual-port FM PCM frame-exactly against full reconfiguration, tests invalid Nyquist and resonance mixed-stage rollback, finite output under rapid high-resonance modulation, allocation bounds, mixed-family equivalence, chunking and backward seek. No changes to the nonlinear/saturated Ladder.
- [x] **FM modulation — saturated Ladder (completed 11 October 2026).** Prevalidated allocation-free `LadderSaturated` `CutoffHz`/`Resonance`/`Drive` updates preserve the bounded nonlinear feedback solver, four ladder integrators, four reconstruction memories, previous-input history and 32-frame numeric interpolation at 1×/2×/4× oversampling. `Oversampling` remains structural and does not change or allocate during PCM processing. Tests compare each oversampling factor frame-exactly with full atomic reconfiguration under audio-rate control, validate drive-only/default-field retention, extreme legal resonance/drive, invalid mixed TPT/Biquad/linear/saturated rollback, mixed valid-update equivalence, allocation bounds and chunk/seek replay.
- [x] **FM modulation — All-Pass (completed 11 October 2026).** First- and second-order `AllPass` now accept allocation-free, prevalidated `CutoffHz` updates, preserving the exact 32-frame stable reflection-coefficient interpolation and both section histories. `Order` remains structural and is never modulated. Tests check frame-exact equivalence with full `Configure` for both orders, untouched independent serial stages, valid and invalid mixed-family atomic batches, audio-rate allocation bounds, finite PCM, and split-block/backward-seek determinism; existing All-Pass response/phase tests still cover unity magnitude and phase rotation.
- [x] **FM modulation — feedback Comb (completed 11 October 2026).** `CombFeedback` `Feedback` and `Mix` now retune through allocation-free atomic scalar preflight (stable ±0.95 feedback, wet/dry mix in [0, 1]), retaining the existing 32-frame numeric transition, invocation-private circular delay memory and current ring position. `DelaySeconds` stays structural and is never rounded, resized or cloned per PCM frame. Tests cover positive/negative feedback, feedback-only/mix-only and simultaneous FM, short/long structural ring lengths, exact full-`Configure` PCM equivalence, unrelated-stage history preservation, invalid mixed-family rollback, successful multi-family commits, allocated-bytes bounds, finite PCM, and deterministic chunk/backward-seek replay.
- [x] **FM modulation — feedforward Comb (completed 11 October 2026).** `CombFeedforward` `Feedforward` and `Mix` now use allocation-free, atomically prevalidated numeric updates, retaining the two-tap impulse response, 32-frame coefficient interpolation, circular delay-ring history and write position. `DelaySeconds` remains structural; no ring resizing, cloning or delay quantization occurs during PCM rendering. Tests cover positive/negative and zero two-tap impulses, changing one or both numeric controls, multiple delay lengths, valid and invalid mixed-family batches, independent note histories, exact full-`Configure` PCM equality, per-frame allocation bounds, chunking and backward-seek determinism.
- [x] **FM modulation — bounded FIR control preparation policy (completed 11 October 2026).** Added invocation-owned, allocation-free, latest-wins capture of up to **four** FIR numeric-stage proposals at once. Before accepting a batch, a fixed-memory preflight checks stage identity, finite 1–20,000 Hz cutoff/edge descriptors, sample-rate Nyquist, coupled band edges and transition-width overlap, odd 3–2,047 tap count and structural phase. Admission is conservatively capped at **131,072 deterministic preparation work units** and **2 MiB estimated temporary memory per batch**, with no unbounded journal. The explicit `TryPreparePending` control-side API builds all kernels only after budget admission and publishes an immutable candidate batch only if every preparation succeeds; it never changes active DSP itself. The live PCM evaluator does **not** call it: until the separate phase-response tasks install candidate consumption, changed FIR ports retain their prior validated kernel and block the entire simultaneously changed mixed-family batch. Excess-budget, invalid and unready updates likewise retain previous histories. Tests cover linear 2,047-tap and small minimum/maximum-phase admission, worst-case phase rejection, atomic coupled validation/preparation failures, repeated coalescing, over-capacity rejection, zero per-frame allocations, deterministic FM fallback/seek and mixed-IIR rollback. This closes **policy infrastructure only**, not the four FIR response implementations or final real-time qualification.
- [ ] **FM modulation — linear-phase FIR responses.** Use the bounded FIR preparation mechanism for `FirLowPass`, `FirHighPass`, `FirBandPass` and `FirNotch` in `PhaseResponse=Linear`. Modulate only connected numeric cutoff/band-edge ports; retain authored tap count, transition width, window and phase selection, the convolution ring and 32-frame coefficient crossfade. Verify cross-parameter/Nyquist rejection, 3–2,047-tap limits, latency/tails, stereo/seek determinism, and exact reference PCM whenever a prepared change is admitted; verify the specified bounded behavior when it is not.
- [ ] **FM modulation — minimum-phase FIR responses.** Support `PhaseResponse=Minimum` for the four FIR response kinds through the bounded preparation mechanism, never invoking cepstral spectral factorization in the live PCM loop. Preserve tap count, phase-specific latency metadata, convolution histories, the 32-frame coefficient transition, finite tails and gain normalization. Test phase/magnitude against the reference, accepted-update PCM equivalence, overloaded control input and deterministic replay.
- [ ] **FM modulation — maximum-phase FIR responses.** Support `PhaseResponse=Maximum` for the four FIR response kinds with preprepared coefficients and no spectral factorization in the live PCM loop. Verify exact reversal of the corresponding minimum-phase impulse, proper metadata/tails, 32-frame transitions, bounded admission behavior, reference-equivalent accepted updates and deterministic replay. Close independently of the minimum-phase implementation.
- [ ] **FM modulation — final mixed-family real-time qualification.** After all preceding family tasks are checked off, run and document worst-case CPU/work and managed-allocation budgets for rapid audio-rate controls, multiple serial Filter stages (including mixed IIR/FIR), 2,047-tap FIR, invalid/NaN/out-of-Nyquist recovery, note concurrency, chunking and backward seek. Assert cross-family all-or-nothing updates and no unexpected coefficient preparation or structural state rebuild in live PCM. Record platform/test configuration and any explicit preparation-budget fallback. Only this task closes the overall hard-real-time performance qualification.

- [ ] **Add Filter selection/instance editing to Instrument tone specifications.** Extend ToneSpecification, table and scripted tone UIs; add the tenth tone-grid column and adjacent instance “...” editor, and include instance configuration in tone-specification interning/copy-on-write and persistence tests.
- [ ] **Apply composed recursive Instrument tone filter chains during rendering.** Preserve per-layer histories through nested Instruments and flattened Pattern/Sequence descendant voices; do not fabricate private mixdowns or disturb note release, seek or source-selection behaviour. Cover multi-voice nesting, direct and indirect sources, and snapshot isolation.
- [ ] **Add optional filter configuration to note starts and scripted note events.** A new note with no event-level Filter starts with no event-level filter; prior note state must not leak. Compose any event filter after inherited tone layers, and cover same-frame command order, flattened logical notes and private mixdowns.
- [ ] **Implement SetNoteFilterCommand and SetNoteFilterParametersCommand.** Select/clear the *current note-event layer*, apply typed atomic partial parameter updates (including structural resets), preserve inner Instrument layers, and emit rate-limited diagnostics on invalid/no-note targets. Ensure existing released/NNA voices are not retroactively reconfigured.

### Physical channels, commands, envelopes and UI helpers

- [ ] **Introduce persistent physical/virtual channel-output filter processing.** Add a distinct channel filter slot after the note/Instrument chain and before session speaker summation; one slot per real host channel regardless of flattened invocation scopes, isolated inside private mixdowns. Test combined-voice processing and persistence across note changes and child cancellation.
- [ ] **Implement SetChannelFilterCommand and SetChannelFilterParametersCommand.** Select/clear with fresh state, atomically set any subset of typed parameters (including structural choices), preserve compatible history, and route flattened child commands to the actual hosting channel without scope rollback. Diagnose invalid targets without partial application.
- [ ] **Implement stage-aware multiline assignment parsing and assistant/wizard rewrites.** Accept # comments and typed values, reject duplicate/invalid paths atomically, and make type-aware assistance available in native effect configuration. Rewriting stage2 removes all its prior assignments and # stage2: annotations but preserves other stages/comments (including stage20); test idempotent repeat rewrites and visible validation errors.
- [ ] **Implement SetNoteFilterParameterEnvelopeCommand and note-level bindings.** Attach/replace/detach Node/Script Envelopes to numeric nonstructural parameters, absolute values in descriptor units, independent active clocks, base/override separation, Note Off and flattened-descendant ownership. Test finite/nonfinite outputs and invalid bindings.
- [ ] **Implement SetChannelFilterParameterEnvelopeCommand and persistent bindings.** Attach/replace/detach on host channel stage parameters, persist across flattened-child termination/new notes, avoid automatic Note Off from unrelated notes, preserve deterministic modulation and bound diagnostic spam.
- [ ] **Clone channel filter histories/configuration when NNA displaces a voice.** Fork full channel filter DSP and active envelope state to its dynamic virtual host at the displacement frame while leaving the physical host unchanged; carry note-local chains with their voices. Verify continuity on the first virtual frame and divergence after independent changes.
- [ ] **Replace final-speaker None/LowPass/HighPass processing with the reusable filter engine.** Keep exactly one post-master-mix pass and independent speaker histories; provide an explicit settings/UI transition for existing cutoff choices, including the equivalence of the previous one-pole response. Ensure private mixdowns remain unfiltered at this final-speaker stage.
- [ ] **Perform physical filter-feature listening and desktop acceptance on Windows and Linux.** Once production integration is complete, audition filter automation, nested tones, Note Off/tails, NNA continuity, live graph edits and updated speaker filters; record actual audible outcomes separately from automated PCM/CI assertions. Confirm final-commit CI and update adjacent audio/effects/pane/tone docs as affected.

## Node-envelope redesign

The [node-envelope design contract](node-envelope-design.md) records the
requested model, sustain timeline semantics, and exact graphical gestures.
The validated node model, immutable renderer, JSON persistence and
production authoring cutover are implemented for both Instruments and FM
graphs. The four-scalar ADSR definition, renderer, and graphical editor have
been removed without a legacy-song migration. The shared interactive graph now renders the curve and node guides,
supports 4 px single-node selection, dragging, line insertion and node
deletion; the node-list editor stays available for precise values.
Sustain-guide boundary moves, bidirectional region selection, single-node
sustain by guide double-click, and selected-endpoint Match are now
implemented. Headless/shared-editor synchronization, numeric draft
conflict protection, navigation and direct FM/Instrument render lifecycle
checks are automated. The remaining node-envelope milestone is desktop
hardware and actual audible playback verification:

- [ ] **Physically validate node-envelope editing and audible release tails.**
  Windows desktop feedback identified inadequate sustain shading,
  ambiguous outward guide drags, missing fixed display bounds, missing
  drag preview, no-sustain mode, and keyboard interactions. The first
  corrections are implemented and regression-tested. Subsequent
  feedback clarified five-pixel guide hit targets, guide hover cursors,
  crossing boundaries, interior-guide drags, and thicker boundary
  lines, now covered by further regression tests. A fresh hands-on
  Windows check is still required before closure.
  Automated regression coverage exercises shared standalone/FM editor
  synchronization, unsaved-draft conflict prevention, navigation,
  simulated headless pointer dragging, and chunk-invariant held/released/
  retired rendering for both direct FM and sample-backed Instruments.
  On a real desktop, check node and guide priority, mouse/touchpad
  drags in both directions, double-click insertion/deletion and single-
  node sustain, lost-capture cancellation, and Match button state.
  Verify that edits made in the FM inspector appear in the standalone
  editor and survive reopening the song. Audition actual Instrument
  notes and the FM editor's Test notes through held, Note Off, and
  final silence, using both Forward and PingPong sustain.
  Windows and Linux desktop editing have been exercised manually;
  the sticky time-domain control and FM release changes still require
  listening and hands-on verification with the reported patches.
  CI/headless tests cannot certify the audible outcome.

## FM release and envelope zoom follow-ups

The graph's horizontal time domain stays expanded after shortening the
last node, and grows automatically when that node moves beyond the right
edge. The right-aligned **↔** button appears with more than one pixel
of unused horizontal domain, and refits without changing song data.

FM voices with a finite Envelope used only for frequency modulation now
retire after its release tail, instead of stopping immediately in live
preview or potentially lingering in a Pattern. Ungated FM voices with no
envelope stop at Note Off; unbounded scripted envelope lifetimes remain
unknown. Automated live and Pattern regressions cover the principal
release paths. Recheck actual audio playback and the reported patch:
the original problematic parameter values were not captured.

## Physical validation

- [ ] **Validate surround playback and encoded multichannel output on real
  hardware.** Verify SDL speaker routing on actual 5.1/7.1 devices and
  FLAC/MP3 encoder-specific supported layouts with playable outputs.
  Automated PCM, WAV speaker-mask and software routing tests are already
  in place. Completion requires physical-device/codec evidence, not only
  synthetic tests. See [audio output](audio-output.md) and
  [export workflow](export-workflow.md).

## Conditional improvements

- [ ] **Consider bounded seek-reconstruction checkpoints only if measured
  underruns justify them.** Profile long-distance Oxx/Qxy private-source
  seeking with the existing underrun instrumentation. Introduce
  deterministic, memory-bounded checkpoints only when measurements
  demonstrate real-time PCM-ring underruns. Preserve native-frame
  accuracy and the coroutine renderer; do not restore eager event
  journals or PCM caching. See [source-seek hints](source-seek-hints.md).

- [ ] **Consider determinate export progress only with a proven finite
  duration.** A percentage must be based on a logically established
  total without pre-expanding arbitrary scripted sequences. Continue
  reporting rendered musical time and indeterminate progress otherwise.
  See [export workflow](export-workflow.md).

## Deferred compatibility work

- [ ] **Add persisted-format migrations when a genuine schema change
  requires them.** The current `.hm.json` / `.hm` format remains at
  version 1. Do not preemptively invent version 2. Existing five-pane
  migration, sample metadata tolerance and stable-ID behavior remain
  supported. See [document panes](document-panes.md) and
  [sample storage](sample-storage.md).

## Maintenance policy

New tasks need a reproducible defect or concrete feature, testable
acceptance criteria, and a link to the relevant design contract.
When completing a task, update its documentation, add regression
coverage where appropriate, and verify CI on the final commit.
Do not re-add historical completed milestones as open work.
