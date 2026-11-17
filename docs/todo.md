# Heresy — outstanding work

This file contains **only open, independently actionable work**. Current
architecture and implemented behavior belong in the [documentation index](README.md)
and linked design contracts; completed milestones remain available in Git history.
Do not reintroduce completed-task checklists here.

## Desktop and listening acceptance

### Pattern editing

- [ ] **Finish physical Pattern row Insert/Delete acceptance on Windows and Linux.**
  On both desktops, exercise Insert/Delete and Alt+Insert/Delete near the
  beginning/end of long populated, multi-channel Patterns and reused Patterns
  in Sequences. Check selection, cursor/focus, scroll position, continued
  realtime playback and underruns. Distinguish Pattern row actions from
  Alt+Insert/Delete in the effect stack, and record any remaining lag or
  platform-specific defects. Linux testing already found major improvement:
  current-channel row edits keep up with normal keyboard repeat, while
  all-channel edits sustain about 4–5 updates/s with some latency; this is
  **not** Windows/device/functional acceptance. Record observations in
  [Pattern row mutation performance](pattern-row-mutation-performance.md).

- [ ] **Complete broader physical acceptance of flattened-source Gxx and
  instantaneous GFF.** **Manually verified (limited):** a single-channel
  Pattern used as an instrument inside another Pattern responded correctly
  to both `G01` (slow continuous portamento) and `GFF` (instant tone
  change). This verifies the core use case, **not** the complexities below.
  **Not yet explicitly manually verified:** multi-channel nested Patterns,
  nested Sequence sources, multiple levels of flattening, simultaneous
  unrelated channels sharing physical hosts, displaced/NNA and virtual
  descendant voices, `G00` target/speed memory, `Lxx` combined pitch and
  volume effects, future Instrument-tone selection after retuning,
  interactions with vibrato, envelopes and glissando, release and native
  sample-position continuity, and `GFF` on ordinary Samples, Instruments,
  Synths and private mixdowns. Recheck the Pattern editor's note/`?` and
  unsupported-effect warning presentation, and verify no inadvertent
  source restarts, altered child-row timing, clicks or underruns. Test on
  Windows and Linux where practicable and record results per case rather
  than treating the one-channel test as blanket acceptance. Automated
  PCM, NNA, nesting and producer tests passed, but do not replace these
  physical listening/UI checks. See [effect operators](effect-operators.md)
  and [UI workflow](user-interface-workflows.md).

### Filters

- [ ] **Finish physical filter-feature listening and desktop acceptance.**
  Complete manual case **11**, per-speaker final-output filter choices,
  independence and preference persistence; and case **12**, filtered
  realtime-versus-export parity, release/tail behavior, actual Windows/Linux
  audio devices and underrun diagnostics. Exercise expensive configurations
  when practical and distinguish missed realtime deadlines from correct but
  slower-than-realtime offline exports. Cases **1–10** have already passed
  their documented manual scopes; do not repeat them without a new regression.
  See [filter physical acceptance](filter-physical-acceptance.md) and
  [realtime filter qualification](filter-realtime-qualification.md).
  CI/PCM equivalence alone cannot close hardware listening checks.

### Node envelopes and FM release

- [ ] **Physically validate node-envelope editing and audible release tails.**
  On a real Windows desktop, recheck the final five-pixel guide hit targets,
  node-versus-guide priority, guide hover cursors, outward and crossing
  drags, sustain-boundary guides, single-node sustain, Match, drag previews,
  keyboard focus, lost-capture cancellation and the fixed display domain
  with its sticky/right-edge refit control. Confirm edits synchronize between
  standalone Envelope and FM inspectors, survive save/reload, and preserve
  unsaved numeric drafts appropriately. Audition held, Note Off, release and
  retirement for Instruments and FM Test notes with **Forward, PingPong
  and no-sustain** modes. Recheck the formerly reported FM modulation-only
  finite Envelope retirement and the sticky-domain/release patch on actual
  audio; the original problematic patch values were not captured.
  Prior manual Windows/Linux editing and headless PCM tests do **not**
  establish these outstanding audible and hands-on results. See
  [interactive envelope graph](envelope-graph.md) and
  [node envelope design](node-envelope-design.md).

### Multichannel output

- [ ] **Validate surround playback and encoded multichannel output on real
  hardware.** Verify SDL speaker routing on actual 5.1/7.1 devices and
  FLAC/MP3 encoder-specific supported layouts with playable outputs.
  Automated PCM, WAV speaker-mask and software routing tests already exist;
  physical-device and codec evidence is still required. See
  [audio output](audio-output.md) and [export workflow](export-workflow.md).

## Main window — object management

- [ ] **Implement graph-aware Synth import planning and dependency resolution.**
  Replace the existing Import Synths flow with the editable TreeDataGrid
  plan, stable canonical/alias rows, per-object/per-use mapping choices,
  exact-match highlighting with manual provenance, collision-avoiding
  suggested names, live internal validity and summary, separate song-wide
  validation at Import click, and atomic commit. Include the present
  Source+Multi import refusal and generic zero-import diagnostic as
  regression coverage for the new engine, rather than a standalone
  throwaway fix. See the complete approved [Synth import design](synth-import-plan.md).
  This is separate from the future **Duplicate Object** feature.
  **Implementation progress:** draft PR #34 creates the read-only discovery
  graph and semantic connection/use identities, including the original
  Source+Multi combination, nested Instruments/Synths, shared
  Envelopes/Filters, inactive Filter wires, missing-reference diagnostics,
  and disconnected graph roots. The old import command remains unchanged.
  A stacked draft slice now adds pure editable import-plan decisions,
  per-use overrides, stable canonical ownership, manual/automatic provenance,
  collision-avoiding editable names and multi-document identity handling,
  with dedicated regression tests. Neither slice changes the old importer.
  Effective-plan liveness and Filter rewiring, strict match proofs,
  TreeDataGrid, two-stage validation, Sample asset handling and atomic
  commit remain open.

- [ ] **Add Duplicate Object to every main-window object list.**
  Provide a consistent **Duplicate Object** action in each object list,
  creating a new independently identifiable object of the same kind with
  the selected object's content and settings copied according to that
  object's existing reference/ownership semantics. Keep the original
  unchanged, give the copy a usable name and selection, and maintain valid
  internal IDs, references, document persistence and undo/redo where
  applicable. Audit each list rather than implementing just one object
  type; check both complex objects and objects referenced elsewhere.
  See [UI workflow](user-interface-workflows.md).

## Synth editor — UI defects

## Synth editor — live parameter audition

- [x] **Apply *all non-structural Synth property changes* to ongoing preview
  notes in realtime, without restarting them.** Completed under the bounded
  compatibility/structural-refusal contract of PR #12 (C1–C7, 2026-11-15),
  explicitly accepted for merge by the owner after the 28-surface inspector
  audit, 3,826 .NET / five Python tests, Linux device listening and measured
  overload/recovery, and frozen offline/export parity checks. Windows
  physical playback was unavailable and untested. See the
  [C7 signoff](synth-live-pr12-c7-final-signoff.md) for accepted deferrals,
  evidence, and the precise limits of the claim.
  The requirement covers every editable property whose change leaves the Synth
  graph structurally compatible, **not merely a selected set of parameters**.
  Oscillator Frequency Hz, Waveform and Curvature, and Filter CutoffHz are
  representative examples, **not a minimum viable subset or exhaustive list**.
  Audit the full Synth inspector's editable properties (across node types,
  including nested/source, filter and Multi parameters) and ensure each
  non-structural property has realtime-update support and regression coverage.
  A property must not be excluded just because applying it requires
  recomputing prepared resources, rebuilding DSP coefficients, or migrating
  compatible per-voice state.

  Introduce versioned, immutable, preprepared parameter updates from
  committed Synth edits to all active held and latched preview voices.
  Preserve continuity of oscillator phase, note/release envelopes,
  child-source playback position and stateful DSP histories wherever
  semantically possible; define explicit, click-safe state migration or
  transition behavior when a non-structural parameter changes the processing
  configuration. Graph **structural** changes (such as adding/removing nodes,
  changing node identity/type, rewiring connections or changing effective
  processing topology) may leave existing voices on the earlier captured
  definition while new notes use the edited graph. Decide structural
  compatibility from actual graph/runtime structure, never merely from
  AudioRevision or whether a parameter has a prepared implementation.
  Preserve graph-generation isolation for subsequent edits. Prepare costly
  resources (including curvature tables and filter/FIR redesign) outside the
  audio callback and publish safely at an audio block boundary.

  Acceptance requires a complete property-by-property audit, automated
  realtime/PCM tests of **every non-structural editable property**, simultaneous
  held and latched notes, rapid successive edits, consistent voice/release
  lifetimes, filter/state transitions, structural-change isolation, latency,
  underruns and physical listening; do not claim the milestone complete after
  testing only the illustrative properties above. Keep live MIDI pitch bend
  as an *independent per-voice/per-MIDI-channel control*, not an authored
  graph mutation, while allowing it to reuse the realtime control/publication
  infrastructure. See [Synth sound source](synth-sound-source.md),
  [UI workflow](user-interface-workflows.md) and [audio output](audio-output.md).

## MIDI keyboard input (deferred until Synth live-parameter infrastructure)

- [ ] **Implement cross-platform MIDI keyboard input, Program Change Source
  control, and continuous microtonal preview pitch bend.** Preserve all
  decisions, routing, device management, acceptance cases and unresolved
  hardware expectations in the [MIDI keyboard design](midi-keyboard-input-design.md).
  Provide the Pattern Editor's mutually exclusive **Stop, Pause, Record,
  Play+Record** modes and adjacent exclusive **P/S** traversal selector
  (P default). Stop auditions notes without document edits, but still
  allows Program Change Source navigation; Pause inserts notes with Skip.
  Record/Play+Record advance a Note-column-locked record head at the
  **actual effective tempo and speed** (including muted Record-only rows),
  ignoring Skip so repeated incoming notes overwrite the same row under
  the existing edit mask. Play+Record plays **all channels** of each row.
  P wraps within the same Pattern; S advances the Sequence and stops at
  its end. Ctrl+Space selects Play+Record; Ctrl+Shift+Space selects Record.
  Entering either record-head mode **remembers the current Stop/Pause state**;
  switching Record ↔ Play+Record does not change this memory. Escape in a
  recording mode **restores that remembered state** (Stop or Pause), including
  after repeated start/stop cycles. In Stop or Pause, Escape switches to
  the other resting state and updates the memory used by the next recording
  session. Clicking a depressed Play/Record half also returns to the
  remembered resting state.
  Include a **Velocity** toggle for MIDI and the physical keyboard piano:
  off uses current-channel volume for preview without writing it; on
  uses translated MIDI velocity (or keyboard current-channel volume) for
  both preview and Volume insertion, with edit-mask precedence. Preserve
  Note/Volume/Source/effect mask semantics, source navigation and playback
  scheduling. Numeric MIDI velocity mapping and a few boundary policies
  remain for explicit implementation decisions in the design.
  Program Change navigation uses raw base program **100**, sends reset
  Program Change back to the keyboard, moves Source by signed delta with
  **clamping**, and is gated by an independent, default-checked
  **MIDI Control** checkbox even in Stop mode. MIDI Synth/Sample audition
  is independent of computer-keyboard Hold/Latch, always Hold, and
  updates ongoing MIDI voices per channel for pitch bend. Capture
  arbitrary bent pitch multipliers in Pattern notes without recording
  continuous bend automation. Use the completed Synth realtime-parameter
  audition infrastructure without conflating authored graph parameters
  with per-voice MIDI pitch. Implement synthetic and cross-platform
  virtual-port tests before physical hardware acceptance; the MIDIPLUS
  EK490+ input response and program-counter reset semantics remain
  **unverified**.

## Conditional improvements

These are contingent on evidence; they are **not** authorization to
preemptively alter the production architecture.

- [ ] **Consider bounded seek-reconstruction checkpoints only if measured
  underruns justify them.** Profile long-distance Oxx/Qxy private-source
  seeking with the existing underrun instrumentation. Introduce
  deterministic, memory-bounded checkpoints only if measurements demonstrate
  realtime PCM-ring underruns. Preserve native-frame accuracy and the
  coroutine renderer; do not restore eager event journals or PCM caching.
  See [source-seek hints](source-seek-hints.md).

- [ ] **Consider determinate export progress only with a proven finite
  duration.** A percentage must use a logically established total without
  pre-expanding arbitrary scripted sequences. Otherwise continue reporting
  rendered musical time and indeterminate progress. See
  [export workflow](export-workflow.md).

## Deferred compatibility

- [ ] **Add persisted-format migrations only when a genuine schema change
  requires them.** The current `.hm.json`/`.hm` format remains version 1;
  do not invent version 2 preemptively. Existing four-/five-/six-pane
  compatibility, sample metadata tolerance and stable IDs remain supported.
  See [document panes](document-panes.md) and
  [sample storage](sample-storage.md).

## Maintenance policy

New tasks require a reproducible defect or concrete feature, testable
acceptance criteria and the relevant design-contract link. On completion,
update the reference documentation, add appropriate regression coverage,
verify CI on the final commit, then **remove the completed entry** from
this file. Git history preserves the implementation chronology.
