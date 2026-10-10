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
  **Scoped memory lifecycle implemented:** a single shared
  `ScopedSequencingChannelMemory` owns scope 0 directly and lazily maps all
  nested scopes by never-reused 64-bit IDs. Producers retire their maps
  when their entire invocation subtree/order completes or is cancelled;
  the renderer retires the matching per-scope physical channel entries
  after outstanding voices, NNA voices and anti-click tails finish.
  Repeating recursive music must retain state proportional to live
  invocations/voices, not the cumulative number of source starts.
  **Instigating-note effect classification implemented:** direct
  non-mixdown flattened starts ignore single-voice commands *before*
  tracker effect memory and tick-operation scheduling. This includes
  retrigger, tone portamento, glissando, sample offset, pitch/vibrato
  and other direct-voice effects; global Tempo and channel/volume
  controls remain meaningful. Playback emits rate-capped HRSEQ003/004
  warnings while leaving stored Pattern effects untouched; the data
  Pattern editor shows a nonblocking warning on qualifying effects.
  **Step 53: live instigating-note volume semantics supersede step 52's
  whole-effect suppression.** Every flattened invocation now has an
  independent renderer-side source-note volume controller. At source start
  the caller remembers the explicit/recalled volume, which initializes the
  controller, and all descendant voices hold live references to every
  enclosing controller. Per-sample multiplication composes arbitrarily
  nested values; volume zero can be raised by later effects. Dxx, native
  note-volume slide/adjustments and volume-column A-D remain active; Kxx
  and Lxx keep their *note-volume* slide while discarding and diagnosing
  only the vibrato/portamento component. Effects entered on later rows of
  the still-active instigating logical channel also control the source.
  Mxx/Nxx continue affecting the separate live overall-channel-volume
  ancestry, not the physical hosts into which child notes splay.
  Recalled Sources can still reclassify Gxx/Lxx at execution time.
  Retired scope controller lookups are removed, while sounding descendant
  voices retain their referenced controller objects until completion.
  **Step 54: flattened-source note lifecycle implemented.** An active
  source instigator owns a logical note even though its children are
  hosted in independent renderer channels. Note Cut and Note Off on
  its logical channel immediately end future child production and
  send Cut/Off to all existing descendant voices (including nested,
  virtual and NNA-migrated voices) without touching siblings or
  unrelated voices sharing the physical hosts. Natural source
  completion remains non-destructive to already sounding voices.
  Replacing the instigator obeys S73-S76 NNA: Cut (default when no
  single instrument supplies an NNA policy), Continue (old producer
  keeps generating), Off or Fade (future notes stop, existing voices
  release/fade). A later row's volume still controls a releasing
  source until replaced or cut. Explicit subtree cancellation now
  cuts matching physical/virtual descendant voices and clears scope
  state; passive retirement remains separate.
  **Step 55: inherited data-Pattern editor warnings implemented.**
  Voice-specific effects on later rows of a locally known active
  flattened instigator now display the nonblocking warning; Source
  selection without a start, Note Off tails, Cut, and mixdown/new-note
  displacement are distinguished. Runtime diagnostics remain the
  authority for dynamically selected/cross-Pattern Sources.
  **Step 56: same-event source lifecycle ordering implemented.**
  One raw event may start a flattened source then Cut/Off or displace
  it with S74 Continue at the very same output frame. Scope retirement
  now reaches the renderer after the complete emitted command group,
  never before its Begin controller registration. Exact-frame PCM and
  bounded controller lifetime are regression-tested.
  **Step 57: cross-producer pending-event ownership implemented.**
  External Sequence/Pattern cancellation reports actual Pattern cursor
  IDs separately from recursive frame IDs. A canceled producer's
  prefetched note is invalidated directly without an accumulating
  tombstone set, and only its scoped virtual channels are cut.
  Independent roots with numerically colliding frame/cursor IDs
  survive; same-frame PCM and Core ownership regressions are green.
  **Step 58: scoped virtual NNA and past-note ownership implemented.**
  Repeating a scoped or directly targeted virtual note now applies its
  old voice's own Cut/Continue/Off/Fade policy instead of unconditionally
  cutting it. Migrated NNA voices retain their original virtual channel
  and, where applicable, Pattern cursor owner. Canceling a cursor cuts
  its current and displaced scoped virtual voices without touching
  sibling virtual IDs. Physical-channel S70-S72 cannot mistake virtual
  NNA voices for physical host voices. Explicit virtual S73-S76
  overrides and S70-S72 past-note controls use the same original
  target identity. AllVirtual reaches displaced voices whereas
  AllVirtualInScope remains limited to current scoped channels;
  same-frame starts remain ineligible for broadcasts by design.
  Red-to-green renderer regressions cover NNA Continue, cutoff,
  owner isolation, flattened-scope Cut, S70/S74, and exact-frame
  Off/Fade.
  **Step 59: indirect private-mixdown lifecycle propagation implemented.**
  Instrument-selected private Pattern/Sequence tones, even through
  multiple Instrument layers, now retain each flattened ancestor scope
  in their tracked ownership. Flattened Off/Cut/Fade and explicit
  subtree cancellation forward lifecycle actions to their private
  coroutines, independent of overlapping physical host indexes.
  Private virtual notes track their original cursor/ID across NNA
  Continue/Off/Fade, with S70-S72 and AllVirtual reaching the proper
  displaced private voices but not sibling scopes. Resolved virtual
  NNA and past-note commands now pass the shared-tick validator at
  supported musical-row deadlines. Source-level Fade stops future
  private note generation without imposing Off or Cut on existing
  fading voices; pending one-event lookahead and scope retirements
  are discarded/settled safely. Red-to-green PCM, private InputEnded,
  source end-frame, fade-controller, and sibling-isolation tests cover
  direct mixdowns, nested Instrument chains and private Sequences.
  **Step 60: source Fade duration and release ancestry verified.**
  Flattened S76/Fade now applies the selected descendant voice's
  new-note fade duration, rather than its ordinary note-fade duration,
  consistently across physical, scoped virtual and displaced NNA voices.
  Private recursive playback distinguishes S76/NNA Fade from ordinary
  S72 past-note Fade, preserving their independently configured durations
  while stopping future private note generation. Releasing descendants
  retain live source-note and overall-channel volume references even after
  their producer scope retires; a later replacement note takes over the
  instigating channel without stealing or re-targeting those references.
  Four new regression cases verify fade boundary frames, PCM curves,
  continued release automation and replacement-note isolation.
  **Still outstanding:** inherited/dynamically selected editor source
  indications and downstream Note Off/Cut; advanced mixed release
  envelope/indefinite-script stress coverage, and separate timing/seek
  compatibility work.
  The incremental merger admits zero-offset Mxx and native channel-volume
  commands alongside flattened starts.
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
