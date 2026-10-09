# Heresy TODO

This checklist tracks **remaining** work as of 2026-10-09. Completed
implementation milestones are retained in Git history, not repeated here.
Design history and detailed sequencing semantics are documented in
[incremental-sequencing.md](incremental-sequencing.md); its earlier
experimental milestones are historical, not current production status.

## Recursive playback — remaining compatibility and performance work

**Production cutover completed:** F5/F6/F7, ad-hoc note audition and
offline export now use the same coroutine-based
`IncrementalRecursiveTimeline` / `PreparedIncrementalAudioSource` engine.
`SongScheduleCompiler`, the old greedy flattened expander and compiled
nested mixdown sound have been removed. A single PCM worker generates notes,
executes scripts and recursively renders private mixdown sessions. The SDL
callback only consumes a bounded PCM ring; no event journal, preparation
queue or cooked private PCM cache remains. Offline rendering discovers
the finite arrangement end incrementally, applies the third-Bxx-encounter
policy and drains release tails, with explicit bounds for endless songs.
Indirect Instrument tones can also select Pattern/Sequence sources,
including nested Instrument chains; selected tones receive independent
private mixdown voices, preserve tone-envelope overlays, and check cycles.
Sample/FM-only tones still use the normal cached resolver. Retired
private mixdown sound registrations, Instrument-bound transient IDs and
nested ownership graphs are reclaimed after the final active voice detaches,
preserving audible release and already-captured anti-click tails. These are
**implemented invariants**, not outstanding TODOs.

- [ ] **Complete flattened-source instigating-note effect handling and
  ownership edge cases.** The first logical-channel isolation milestone is
  **implemented**: the shared clock now supports independent Source/effect
  memory per flattened invocation, stable logical render-channel ownership
  over overlapping physical hosts, separate NNA/cut/off/retrigger voice
  state, remembered note volume updates on an explicit flattening start,
  omitted-volume recall from the live renderer, multiplicative child
  source gain, and live instigating-channel overall-volume ancestry.
  Nested sources, sibling hosts and repeated Sequence orders have regression
  coverage. The original shared-channel-memory design and step 47's
  no-caller-memory claim were **superseded** by the October 9 clarification.
  **Still outstanding:** meaningful-effect classification on a flattening
  start before per-note effects update local tracker memory; ignore
  nonsensical single-voice controls (retrigger, glissando, portamento,
  sample offset and similar) and post bounded playback warnings; add
  nonblocking authoring-UI warnings while preserving valid stored data;
  test deeper nested cancellation, virtual/NNA displacement, source-volume
  curves and unusual same-frame starts. Raw incremental Patterns still do
  not admit direct SetOverallChannelVolumeCommand: overall-volume ancestry
  is currently covered through supported live channel-volume controls.
  The detailed target remains in step 48 below.

  **Original architectural requirement (now partially implemented):** A flattened
  Pattern/Sequence owns independent logical channel memory (selected Source,
  note volume, effect parameters, retrigger/portamento, channel automation
  and current voices) that persists across its own Sequence orders but
  never leaks into the parent or sibling invocations. It merely **uses**
  parent physical channel indexes as playback hosts. A host must not lend
  note volume/effect memory to its guest or be changed by that guest.
  Only the instigating note updates its caller's remembered note volume.
  Its **explicit or recalled** volume is captured as an initial source-gain
  multiplier for the child's notes; its instigating channel's **overall**
  channel volume is a live parent multiplier for all splayed child voices,
  regardless of which physical host they use. Child logical channel note/
  overall volumes remain independent and are multiplicative with this gain.
  Nested flattening retains independent memories and composes gains;
  sequences reuse their invocation-local map between order Patterns.
  No double-application from a host's remembered note volume.
  Implement stable logical render-channel identities through end-to-end
  PCM, private mixdown ownership, virtual/NNA lifecycle and cancellation;
  provide tests for physical-host collisions, parent memory persistence,
  omitted-volume recall, nested/sibling and multichannel splaying.
  Classify effects on the *instigating flattening note*: global Tempo and
  meaningful channel/volume changes still apply, but individual-voice
  operations such as retrigger, glissando, sample offset or portamento
  are **ignored before their effect memory changes**, with rate-limited
  runtime diagnostics and non-blocking UI warnings. Preserve all effects
  in the stored data (do not reject/erase them). A note *inside* the child
  can still use those effects normally on its own logical voice.
  Important: do not consider source-volume step 47 fully correct until
  both remembered caller volume and private child channel ownership pass.
- [ ] Complete recursive **dynamic pitch trajectories** and advanced
  cross-rate tracker-effect parity. Initial recursive source volume is now
  inherited by flattened Pattern/Sequence descendants as a per-voice
  multiplicative gain, separate from shared tracker channel-volume memory.
  Nested initial source volumes compose; gains survive note slides, fades
  and virtual-voice displacement without affecting unrelated parent notes.
  Private mixdowns retain their renderer-owned parent note-volume semantics
  and inherit a flattened ancestor's source gain on their outer voice.
  Initial pitch and private/flattened playback-speed multipliers are already
  implemented. A single scaled Txx slide is supported, but simultaneous
  cross-rate slides and scaled Txx-with-SEy remain explicitly unsupported
  pending complete multi-rate arbitration. Preserve native multichannel
  speaker feeds, cancellation and deterministic source-frame seeking.
- [ ] Finish unsupported advanced tracker/script effect combinations in
  the shared-tick coordinator. In particular, verify negative fixed
  wall-time offsets, advanced/global effect deadlines, incompatible
  simultaneous Tempo spans, delayed/overlapping command memory, and
  remaining tick/repeat behaviors. Do not apply future Source, Tempo or
  effect state prematurely; reject unsupported combinations explicitly.
- [ ] Extend lifecycle and cancellation parity for complex recursive
  graphs: indirect instrument-owned voices, displaced Continue/Off/Fade,
  S7x past-note controls, per-note fade durations, virtual broadcasts,
  cancellation during delayed operations, and nested end-of-input tails.
  Add targeted mixed data/script and multichannel regressions.
- [ ] Complete edge-case **native source-frame seeking** (Oxx and Qxy
  retrigger) across nested Pattern, Sequence and instrument graphs, including
  offsets crossing lifecycle boundaries and repeated invocations. Backward
  seeks already reconstruct deterministic private generators without
  retaining event/PCM history; retain that architecture and verify parity.
- [ ] Optionally optimize expensive long-distance private seek/reconstruction
  using deterministic bounded checkpoints **only if** realtime ring-buffer
  underruns warrant it. Do not reintroduce unbounded event journals or
  schedule/PCM pre-rendering.
- [ ] Expand end-to-end tests for indefinite scripted sequences, recursive
  cancellation and instrument cycles, realtime/offline PCM parity, and finite
  export-body/tail limits. Basic cycle protection, cooperation budgets and
  export limits are already implemented.

## Output audio configuration and physical speaker processing

- [ ] Apply configured per-output-channel filtering to the **final speaker
  feeds**, independently of the existing per-voice tracker resonant filter;
  cover None, LowPass and HighPass and continuity across render blocks.
- [ ] Expose configuration of output channel count/layout (including 5.1 and
  7.1), speaker positions, positional importance, optional speaker filters
  and cutoff frequencies, and sample rate. Wire the chosen configuration to
  realtime and offline paths, preserving the same PCM engine and proper
  distinctions between playback channels and speaker outputs.
- [ ] Test spatial and filtered output routing, including arbitrary output
  layouts, stable multi-block rendering, and configured channel ordering.

## Export workflow

- [ ] Show offline rendering progress in **rendered musical time** against
  the logical length (not guessed wall-clock ETA).
- [ ] Add cooperative cancellation at safe render blocks, preserving the
  atomic temporary-output-file behavior and leaving existing exports intact.
- [ ] Support user-selectable output configuration for export and additional
  WAV depths beyond default 16-bit PCM. Retain FLAC as default and MP3/WAV.
- [ ] Ensure export and realtime render identically for equal snapshots and
  render configurations, aside from intentionally different end-of-song
  handling and output encoding.

## Asset portability

- [ ] Add an interactive recovery flow for missing/unreadable sample assets
  when opening an existing project: select substitute files or search folders,
  validate loaded encoded data, and retry without mutating the current
  document on failure. Preserve in-memory decoded PCM and archive semantics.

## Playback state and authoring feedback

- [ ] Route **offline export** sequencing diagnostics to the same bounded
  runtime-diagnostics UI history now used by streaming realtime playback.
  Realtime HRSEQ001/HRSEQ002 reports are already delivered from the PCM
  worker via the transport; do not dispatch UI callbacks from that thread.
  Preserve the 32-message per-context cap and 500-entry UI history.
- [ ] Surface the PCM ring's existing underrun count and worker faults
  through playback transport/UI. Late audio already produces silence without
  skipping unrendered musical frames or blocking SDL; the UI underrun
  indicator must be **hidden until the first underrun**.
- [ ] Show playback-affecting changes since the most recent playback snapshot,
  separately from unsaved-file status, using document/audio revision tracking.
- [ ] Highlight Oxx/offset operations on ReplayRequired mixdown sources when
  the runtime capability indicates expensive realtime seeking. Explain in a
  tooltip that export remains correct; provide an option to suppress warnings.
  Do not prohibit these operations.

## Startup branding and application identity

- [ ] Use `Heresy.UserInterface/Images/Icon.ico` as the main window icon
  so the application has its own icon in the title bar, taskbar, Alt-Tab,
  and other window-manager UI on supported platforms.
- [ ] Embed `Heresy.UserInterface/Images/Icon.ico` as the Windows executable
  icon in the Windows build, including the native apphost/stub `.exe` file,
  while preserving cross-platform builds.
- [ ] On startup show the supplied `Heresy.UserInterface/Images/Logo.axaml`
  control in a separate chromeless splash window in front of the main window.
  Dismiss it on any keyboard key, any pointer click, or automatically after
  four seconds, whichever happens first. Do not delay or block main-window
  initialization or leave the splash open when the main window closes.

## Documentation and later maintenance

- [ ] Update README descriptions of load/import-time WAVE/FLAC/MP3/OGG/AIFF
  decoding, immutable shared PCM assets and the **production coroutine
  playback/export cutover**. Review historical sections of
  docs/incremental-sequencing.md to make their superseded statements about
  eager production scheduling unambiguously historical; treat current
  code and docs/sample-storage.md as authoritative.
- [ ] Add format-version migration tooling **only when** actual documents
  require schema evolution; intentionally retain format version 1 during
  pre-release development.
