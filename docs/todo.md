# Heresy — outstanding work

Only unfinished, independently actionable work belongs here. Completed
architecture and regression history are documented in the
[documentation index](README.md). The original-design programmability
features, including scripted Patterns, Sequences, Envelopes, Instruments,
virtual-voice controls, and compiled parametric effects, are implemented.

## Reusable FIR/IIR filter system

Design source: [filter-design.md](filter-design.md) (incremental implementation). Each checkbox is intended to be a separately closable, tested implementation slice. In particular, filter *definitions*, configured *instances*, and mutable *DSP processing state* are different things. Do not replace existing IT/tracker-compatible voice filters as an accidental side effect. Preserve the recursive incremental renderer, private mixdown isolation, and current native-effect ordering.

### Core model and authoring

- [ ] **Add reusable Filter song definitions, named stages and independent configured instances.** Make ordered, nonempty serial stage definitions with unique case-insensitive names and stable stage IDs; allow independent per-use parameter overrides and ordered filter-instance chains for nested uses. Test stage rename/reorder/type changes, overrides and atomic rejection of invalid combinations.
- [ ] **Persist, clone, snapshot and import Filter objects and references.** Serialize definition stages, instance overrides and chains in .hm.json/.hm, maintain immutable frozen audio snapshots, object-ID remapping, missing references/tombstones and copy-on-write import semantics. Retain version 1 if loading older forms remains unambiguous; test round-trips and no-op/audio revisions.
- [ ] **Expand the document tree to a sixth Filters root with a four-column grid.** Sequences and Patterns each span two columns in the first row; Patches, Filters, Envelopes and Instruments use one column each in the second. Add compatible four-/five-root loading and six-root restoration without incorrect object moves. Cover tree folders, section-local reorganization, activation and layout tests.
- [ ] **Build the standalone Filter definition editor.** Support named serial stages, add/remove/reorder/rename, type dropdown with disabled section headers, per-type parameter defaults and validation, and shared editing/revision-conflict behaviour. Add headless Avalonia tests.
- [ ] **Build the reusable Filter *instance* parameter editor.** Drive controls and contextual assistance from parameter descriptors, separate configured per-use values from shared defaults, prevent cross-editor draft loss, and show reasonable units/ranges/structural choices. Verify that reconfiguring one use does not mutate another.

### Reusable rendering engine and filter families

- [ ] **Introduce the reusable streaming filter-chain runtime and state/transition contract.** Support per-invocation, per-stage, per-output-channel histories, serial processing, sample-rate-aware preparation, atomic updates, compatible-history retention, implicit reset on incompatible structural updates, deterministic seek reconstruction and a bounded silence/tail policy. Begin with an identity/bypass stage; test block partitioning and state isolation.
- [ ] **Implement the One-Pole (6 dB/octave) type.** Include validated cutoff/response configuration, expected impulse/frequency response, parameter updates, history preservation and denormal-safe tails.
- [ ] **Implement all seven Audio EQ Biquad types.** Low/high pass, low/high shelf, peaking/bell, band-pass and notch, with validated Q/bandwidth/gain controls where applicable, coefficient stability and modulation/smoothing tests.
- [ ] **Implement the four SVF types on a shared stable SVF engine.** Low/high/band-pass and notch are distinct selector options, backed by a topology-preserving implementation; test frequency response and rapid modulation against stability and finite-output criteria.
- [ ] **Implement Ladder (linear).** Define resonance/cutoff, state and stability boundaries; verify modulation, impulse response and numerical safety separately from the nonlinear variant.
- [ ] **Implement Ladder (non-linear/saturated).** Define drive/saturation (and bounded oversampling if needed), validate processing cost and nonlinearity, and test deterministic results across render block boundaries.
- [ ] **Implement All-Pass (Phase Shift).** Specify order/frequency controls, check near-unity magnitude with the intended phase shift, and cover transitions/tails.
- [ ] **Implement Comb (feedback) as an IIR filter.** Specify stable feedback/delay limits and check bounded ringing, discrete delay changes, reset transitions and tails.
- [ ] **Implement Standard Arbitrary FIR response types with Linear phase.** Low/high/band-pass and notch, bounded taps/windows/transition widths, sample-rate-aware coefficient design, delay/latency accounting, expected frequency response and finite delay tails.
- [ ] **Add Minimum and Maximum phase to Standard Arbitrary FIR.** Prepare coefficients only at configuration/structural changes; test phase-dependent response, impulse timing, reconstruction and automatic state replacement when histories become incompatible.
- [ ] **Implement Comb (feedforward) as FIR.** Include parameterized delay/taps and finite impulse tail, and distinguish its response/ownership from feedback comb.

### FM graphs, Instruments and per-note uses

- [ ] **Add FM Filter-node authoring, stable dynamic ports and graph persistence.** Provide Input plus ports for continuous numeric stage parameters only, static structural controls, definition dropdown with italic New..., and **Edit Definition...**. Update node height, validate/rebuild connections on definition edits, reject graph cycles, and test saved/reopened graph topology.
- [ ] **Render FM Filter nodes with per-note histories and explicit Tail.** Evaluate modulation ports before filtering, reconstruct state on replay/seek, and extend finite computed FM note life by the **maximum** reachable node Tail in seconds while ending new excitation at the base boundary. Test gated, ungated, frequency-only, serial/filter-dry-path and indeterminate-envelope cases.
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
