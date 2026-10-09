# Heresy TODO

This file tracks the remaining requirements recovered from the original design
and the current repository audit (2026-10-08). Work should be implemented
test-first with a red checkpoint, green GitHub Actions, and focused documentation.
Items that were already completed remain in Git history, not in this checklist.

## Recursive sound sources — architecture priority

- [ ] **Pivot to cooperative lazy sequencing**; see
  [incremental-sequencing.md](incremental-sequencing.md). First slice:
  `DataPatternDefinition` implements
  `IIncrementalRawPatternNoteGenerator.EnumerateRawSteps`, emitting
  raw `NoteEvent` steps and non-executing musical progress after up to
  100 silent rows. The legacy `GenerateRawNotes` path eagerly consumes
  the stream for full compatibility.
  `IncrementalPatternNoteProcessor` retains a raw iterator and resolves
  one requested row through the existing common processor, preserving
  row-time Source memory and shared Tempo/Speed changes between rows.
  The next `IncrementalPatternTimeline` prototype merges independently
  suspended **within-row raw cursors** on one shared tick clock. It allows
  fractional notes, simple global Tempo/Speed commands, physical Source
  selection, dynamic child invocation via Add and explicit Cancel, including
  changing shared tempo midway through another Pattern row. A note-less
  row boundary returns a cooperative Advance; unsupported effects and
  unbounded same-row streams fail explicitly.
  The prototype now retains **row-scoped slide cleanup** for direct pitch/
  volume, tracker Dxy/Exx/Fxx, Nxx/Wxx channel/global volume and Pxx
  panning slides. Effect-memory transformations stay in the common
  PatternNoteProcessor, but cleanup executes only at the issuing cursor's
  real row end, even when another cursor changes Tempo. Ordinary positive
  fixed wall-time Note/Off/Cut commands use absolute deadlines established
  at their musical origin, resolve Source memory only when due, and can
  be discarded with their invocation's Cancel operation.
  Deferred standalone global Tempo/Speed events now retain an
  eligibility wall-time established at the beginning of their nominal
  source row and execute at the **first eligible future row boundary of
  that invocation**. Zero-offset global timing shares the same boundary
  handling; effect commands stay in eligibility/emission order, preserve
  other cursors' already-started row durations and are dropped if the
  originating Pattern ends or is cancelled before an eligible boundary.
  Tracker Txx effects reuse SequencingChannelState for T00 memory
  and PatternNoteProcessor's shared per-tick clamp logic for T0x/T1x
  slides and immediate T20-TFF Tempo sets;
  the shared tick merger integrates each ramp analytically (and inverts
  it for fixed wall deadlines), so overlapping child/parent cursors
  observe the actual evolving Tempo rather than its future endpoint.
  Concurrent Txx commands on compatible row spans now compose a single
  ramp with each legacy tick's clamp applied in mapped physical-channel
  order, independent of cursor creation order. New standalone global Tempo
  and later Txx commands interrupt an active ramp at the instantaneous
  shared-clock Tempo, not its future endpoint; Speed-only changes leave
  the current ramp running. Txx byte memory still resolves when due.
  Mixed-command Txx cells now separate their ordinary physical note
  commands from the shared-boundary Txx request. Same-row compatible
  Txx requests still compose one ramp, preserve T00 effect memory,
  and leave positive wall-time note deadlines independent. Simultaneous
  Txx on different captured row-speed spans now compose **piecewise
  shared-clock Tempo ramps**. Each T0x/T1x uses its own captured
  tick span and legacy per-tick delta; the combined Tempo trajectory
  changes slope when a shorter slide finishes. The scheduler emits
  the next Tempo ramp at that tick (not early), preserves fixed wall
  deadlines across the transition, and cancels all pending segments
  on an interrupting Tempo command. SEy-repeated Txx in one Pattern
  invocation now uses successive causally activated compatibility-row
  Tempo spans, matching eager T0x/T1x slides and repeating T20–TFF
  immediate sets; the legacy eager processor also recognizes SEy
  when combined with Txx in one tracker cell. Competing *independent*
  SEy/Txx invocations now compose with independent per-invocation
  captured row spans and repeat counts. The shared piecewise Tempo
  plan has owner-aware contributions: canceling one Pattern removes
  only its remaining repetitions and recalculates from the actually
  reached shared Tempo. Simultaneous immediate Tempo sets remain
  in mapped physical-channel order; no future Pattern iterator is
  consumed during planning. At exact SEy repeat boundaries, the
  scheduler now prepares all newly due Pattern rows *before*
  arbitrating the repeat with incoming Txx. Immediate Tempo sets
  follow mapped physical-channel order and the combined future
  ramp is emitted once, with no prematurely emitted stale ramp.
  Per-source absolute origins preserve different repeat starts.
  The incremental timeline now recognizes **S6x and SEy variable-length
  row spans**: physical-channel S6x ticks accumulate, lowest mapped
  channel SEy wins, ordinary notes execute only once, and admitted
  continuous/fine effects repeat at the correct ticks (including
  fractional offsets), clearing at the final delayed row end.
  S6x reuses the common processor's TicksPerRow override and extends
  isolated tracker Txx ramps. SEy combined with Txx is now handled
  for independent flattened Pattern invocations as well, including
  different captured spans and owner-aware cancellation. Other
  unported tick/repeat effects remain explicitly unsupported.
  The next incremental-timeline slice now uses invocation-local
  tracker-tick deadlines for SCx note cut, SDx delayed atomic note setup,
  and Qxy retrigger. SC0/SC1 and SD0/SD1 share first-post-start-tick
  semantics; S6x extends the tick eligibility window; SEy repeats SDx
  starts but not ordinary cuts and extends the Qxy countdown. Repeated
  SDx notes preserve mapped child physical targets; Q00 retains effect
  memory/countdown across mapped Pattern invocations. Pending tick actions
  respond to live shared Tempo and are discarded with Cancel().
  SDx combined with Qxy is now supported in the incremental
  shared-tick timeline: the delayed note executes first and Qxy
  retrigger timing begins from that same eligible SDx tick. At a shared
  tick, repeated delayed note setup precedes retrigger and SCx cut
  according to eager synthetic ordering. Q00 effect memory and
  retrigger countdown remain mapped-channel state across rows and
  independent Pattern visits; SEy and S6x use the captured span.
  Out-of-span SDx prevents audible note setup/retrigger while leaving
  valid Qxx memory. Positive fixed wall offsets are now supported on
  SCx/SDx/Qxy cells: the tracker tick resolves against live Tempo, then
  the resulting audible command is independently deferred by wall
  time. The synthetic operation must execute before its original row
  ends; late operations are dropped, and cancellation clears their
  deadlines. Negative fixed offsets remain unsupported.
  SBx now revisits raw source rows lazily with per-mapped-channel loop
  markers and repeat counters, resolving shared tracker state anew
  on every visit. Backward visits restart only explicitly replay-safe raw
  sources (data Patterns; scripts must provide a separate coroutine-safe
  model). Bxx/Cxx terminate their original source row and produce a
  `PatternFlowControl` step for the future Sequence cursor rather than
  jumping Pattern rows. Same-tick effect recall/cleanup during SBx
  wrap is kept in eager source-emission order.
  The experimental `IncrementalSequenceCursor` now executes
  **data-Sequence orders lazily**, consuming Bxx/Cxx `Flow` results,
  applying Cxx's one-invocation StartRow override, and visiting Bxx
  targets with fresh Pattern cursors on the same shared timeline.
  Shared Tempo/Speed and channel effect memory survive order visits,
  and a prior Pattern's delayed physical notes can overlap the
  next order. Missing/zero-row entries skip without changing musical
  time; per-step and same-tick cooperation budgets prevent non-progressing
  traversal without imposing a lifetime limit on legitimately advancing
  Bxx playback. The existing order-jump observer
  can terminate Bxx loops without preexpanding them. Resolved
  Patterns must support the incremental interface; scripted/eager-only
  sources explicitly fail.
  The experimental `IncrementalRecursiveTimeline` now composes
  **flattened nested data Patterns and data Sequences** through one
  shared Pattern timeline. A newly due StartNote that resolves to a
  nested data source starts a child at that tick using additive mapped
  physical channels; unrelated commands survive the event. Bxx/Cxx
  order flow, independent child lifetimes, child Tempo changes affecting
  parent timing, sibling overlap, parent-subtree cancellation and
  in-flight delayed notes all operate on the same clock. Recursive
  active-source cycles and non-progressing chains are bounded, while
  advancing Bxx loops have no artificial lifetime cap. Mixdown starts
  remain renderer-owned; unsupported transformed flattened and
  eager-only scripted sources fail explicitly.
  The first **resumable Roslyn Pattern** producer proof now exists as
  `ScriptCompiler.CompileIncrementalPattern`: direct Note/Off/Cut/
  Tempo/Speed statements suspend after each raw event, and loops yield
  `RawPatternStep.Cooperate` after every 128 iterations without
  musical progress. Raw notes emitted earlier than the last accepted
  musical row are now **silently discarded in playback** in both
  streaming and eager Pattern paths, while equal positions preserve
  emission order. A bounded per-context runtime diagnostic queue posts
  `HRSEQ001` for each of the first 32 dropped notes, followed by one
  `HRSEQ002` suppression notice. Subsequent violations increment
  counters but cannot flood the log or interrupt playback; flattened
  and mixdown children share the parent's cap.
  Fixed wall offsets are separate deadlines, not the comparison key.
  The shared-tick `IncrementalPatternTimeline` now exposes a distinct
  CPU-only `IncrementalPatternTimelineStep.Cooperate` at unchanged Tick and
  Elapsed. It suspends partial-row raw collection and retains buffered
  commands until generation resumes, without prematurely executing Tempo,
  Speed or Source effects. Same-instant and per-row work budgets remain
  effective; silent loops can be cancelled and their enumerators disposed.
  The recursive shared-tick coordinator admits resumable scripted
  Patterns through an optional Core script compiler bridge; production
  recursive playback/export are not yet migrated. **Scripted Sequences
  now use a single per-visit `GetSequenceEntry(absoluteIndex, sequenceIndex,
  previousSequenceIndex)` contract** in production and experimental
  processors. `absoluteIndex` increments on every lookup, Bxx revisits
  re-evaluate the function even for the same `sequenceIndex`, and
  `previousSequenceIndex` identifies the prior requested order (-1 on
  first call). Returning null naturally terminates the Sequence.
  Data Sequences implement the lookup by ordinary indexed access.
  Roslyn Sequence script bodies return `Play(_O(id), startRow)` or null
  instead of appending `Play` statements. Invocation-local Random persists,
  local variables reset per call, and each lookup retains CPU runaway
  protection. The older yield-based Sequence iterator, CPU-only
  Sequence checkpoints and generated-entry cache were removed. The
  production chronological scheduler now obtains eligible scripted
  entries on demand without executing scripts for static preflight;
  ineligible arrangements retain per-visit eager note compilation.
  Experimental recursive scripts now also have **snapshot-owned,
  prepared compilation** via `PreparedRoslynIncrementalScriptSources`.
  This object privately snapshots a supplied `SongDocumentSnapshot`,
  compiles every scripted Pattern and Sequence once before playback,
  caches immutable factories by object ID, and creates a fresh timeline
  bound to the same source graph. Compilation failures are surfaced
  during preparation; external authoring/snapshot edits cannot change
  the prepared definitions or Pattern dimensions. Roslyn does not run
  in the audio callback, and new invocations have independent script
  and RNG state. This remains explicitly opt-in; production
  playback/export use their existing scheduling paths.
  Next: establish realtime/offline integration with preprepared
  snapshots and full deterministic effect integration. The Pattern
  ordering contract is settled: earlier raw musical-row events are
  silently dropped at the consumption boundary; no arbitrary sorting
  or lookahead is required. Continue incompatible Tempo spans,
  negative/future-interrupted wall-offset tracker effect corner
  cases, isolated multichannel mixdown, advanced effects, and
  end-to-end deterministic recursive playback/export integration
  before production recursive-clock migration.
  Direct **virtual and broadcast note targets** now pass through the
  experimental shared-tick and recursive coordinators: Virtual(id),
  AllVirtualInScope and AllVirtual preserve their target identities,
  fractional timing, positive wall deadlines and per-Pattern
  InvocationId. Flattened siblings can reuse a virtual ID without
  losing separate invocation ownership. Physical-only tracker memory
  effects remain rejected on virtual targets. A known nested Mixdown=true
  start remains renderer-owned instead of being flattened into the
  parent's clock. Renderer scoped-broadcast eligibility, live virtual
  voice lifecycle now has a first opt-in **realtime renderer bridge**:
  PlaybackSessionAudioSource accepts EnqueueScopedEvent(owner, target,
  commands) and EnqueueCancelScope(owner). PlaybackSession keys audible
  virtual voices by (Pattern invocation ID, local virtual channel ID),
  preserving sibling identity, applies scoped/global broadcasts only to
  voices started strictly before the command frame, and includes
  owner-scoped voices in multichannel rendering, normal release,
  cancellation, indefinite-tail cleanup and anti-click handling.
  Global virtual broadcasts also include unscoped preview and
  displaced NNA virtual voices. The first **sample-accurate**
  prepared recursive consumer now exists as the opt-in
  `Heresy.Playback.PreparedIncrementalAudioSource`: a non-audio
  producer calls PrepareThrough(exclusiveEnd) to stage chronological
  `IncrementalRecursiveTimeline` events and invocation identities.
  Render requires prepared coverage, never executes a source script,
  and splits audio output at `FrameTime.Ceiling` exact event frames,
  applying each event to the existing scoped voice renderer before
  continuing. Uneven block sizes, fractional frames, shared Tempo
  and same-ID independent virtual voices have PCM regressions.
  The earlier EnqueueScopedEvent live-preview interface still uses
  block boundaries by design. A production snapshot/asset factory,
  asynchronous lookahead worker, full cancellation/tail propagation,
  broader effects and private multichannel mixdown clocks remain
  separate integration work; do not switch production paths yet.
  Require test parity, chronological raw-emission filtering, and
  realtime/offline determinism before retiring eager scheduling.
  Replace the restricted chronological scheduler rather than adding
  more special cases; dropped stale note events must never mutate
  shared timing or effect state.
- [ ] Resolve data/script **Patterns and Sequences as playable note sources**
  through the concrete playback sound resolver, including from instruments and
  other nested source graphs. They must render in realtime and offline rather
  than resolve silently.
- [ ] Preserve the original distinction between **flattened** nested invocation
  (mapped child channels, shared parent sequencing state where appropriate)
  and **mixdown** (private child sequencing state and a cooked multichannel
  signal exposed as one parent playback voice). A playback-channel mixdown must
  not collapse physical speaker feeds to mono.
- [ ] Complete the **generalized concurrent flattened row scheduler**.
  Independently advancing shared-tick cursors now handle compatible data
  Patterns, scripted note/cut/off at fractional positions, and standalone
  scripted global Tempo/Speed commands at row starts (fractional timing
  portions ignored as in PatternNoteProcessor), including mixed
  data/script hierarchies and data-sequence arrangements. Scripted Speed
  updates the issuing cursor's row length, without resizing another
  cursor's already-started row. Nonnegative fixed wall-time offsets on
  scripted Note/Off/Cut now generate absolute deadlines on reaching their
  musical position, and nested sources invoked at those deadlines retain
  independent cursors across sequence orders. Eligible scripted Sequences
  also dispatch their ordered Play entries through these shared cursors,
  retaining stable sequence-order positions and one script execution on
  chronological-to-legacy fallback.
  SequenceEntry.StartRow / scripted Play(..., startRow) now enter the
  actual source row without executing earlier effects or Source memory;
  sufficiently delayed skipped-row script events can survive the new
  origin as wall deadlines. Extend to nonzero compile start order/row
  for chronological sequences, pattern control jumps,
  non-unit pitch/speed transforms, negative fixed wall-time offsets,
  fixed offsets on global/advanced effect commands, tracker tempo ramps,
  other scripted/global effects,
  overlapping tempo slides and pattern/fine delays. Preserve the existing
  processor's semantic rules and fail explicitly for unsupported
  combinations rather than render an invalid shared timeline.
- [ ] Extend **chronologically deferred channel state** across advanced
  scripted commands and effects. Source and tracker memory execute at actual
  child-row boundaries in the data-pattern cursor path; eligible scripted
  Start/NoteOff/Cut events execute at fractional timestamps and standalone
  global Tempo/Speed commands at their row starts, without applying
  future commands early. Supported delayed Note/Off/Cut commands likewise
  resolve only at their actual wall deadline. Other scripted/effect
  combinations still use eager preparation and can mutate future shared channel state
  prematurely. Add same-time and overlapping-command regressions.
- [ ] Propagate pitch, playback-speed multiplier, origin timing, Note Off/Cut,
  envelope/release behavior and new-note actions through nested sources.
  Preserve snapshot isolation, deterministic random/effect memory, script
  compilation safety, event caps, and finite/infinite lifetime semantics.
- [ ] Implement mixdown **native source-frame seeking**, including accurate
  ReplayRequired seeking and Oxx/retrigger behavior without silently dropping
  expensive but legal offsets. Add tests for nested patterns, sequences,
  instruments, repeated invocation, notes/releases, seek, and offline parity.
- [ ] Detect or safely bound recursive object-reference cycles and
  unbounded event expansion without hanging playback or export.

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

- [x] Surface bounded `HRSEQ001`/`HRSEQ002` warning reports from
  realtime playback **source compilation** in the UI. The playback
  factory drains the request's `SequencingContext.Diagnostics` after
  preparation; the playback transport and lazy wrapper publish an
  optional runtime-diagnostics event outside the audio callback.
  MainWindow shows a status-bar warning indicator and a bounded
  **View → Runtime Diagnostics** window, marshalled to the UI thread.
  Clearing displayed messages does not reset Core rate limits.
- [ ] Route offline render diagnostics and any future streaming-time
  sequencing warnings to the same UI history without dispatching
  callbacks from the audio thread. Maintain the existing 32-message
  per-sequencing-context suppression limit and bounded 500-entry UI history.
- [ ] Measure/report actual realtime audio underruns. Late audio produces a
  temporary dropout without skipping the musical timeline or stopping playback.
  The UI underrun indicator must be **hidden until the first underrun**.
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

- [ ] Reconcile stale README descriptions of WAVE-only realtime sample
  decoding with the implemented **load/import-time** WAVE/FLAC/MP3/OGG/AIFF
  decoding and immutable, shared in-memory PCM playback architecture. Treat
  docs/sample-storage.md and current code as authoritative.
- [ ] Add format-version migration tooling **only when** actual documents
  require schema evolution; intentionally retain format version 1 during
  pre-release development.

Completed implementation history is preserved in Git, while stable architecture
belongs in the README and focused documentation.

### Prepared recursive cancellation checkpoint (milestone 32)

The opt-in prepared adapter now supports explicit current-frame subtree
cancellation of renderer-owned scoped virtual voices. Natural completion
does not cut voice tails, and duplicate virtual IDs in sibling invocations
remain isolated. Cancellation is admitted only at the prepared playback
frontier with producer and Render serialized; future lookahead invalidation,
asynchronous cancellation delivery, parent physical lifecycle parity, and
production scheduler migration remain unchecked work.

### Prepared snapshot-backed recursive playback checkpoint (milestone 33)

The opt-in `PreparedIncrementalPlaybackFactory` now creates a usable
`PreparedIncrementalPlaybackPlan` (captured revision/snapshot, compiled
restricted Pattern/Sequence Roslyn factories, fresh shared-clock recursive
timeline, empty-schedule renderer session, sample-accurate prepared adapter).
Direct Sample/Instrument/FM synth sounds and envelope curves are resolved
before the callback. Immutable decoded sample PCM remains shared; both
script state and renderer metadata are insulated from caller mutations.
Producer-side note validation explicitly rejects nested Pattern/Sequence
`Mixdown=true` starts until private multichannel mixdown clocks exist.
Real decoded PCM from scripted Sequence -> data Pattern -> scripted
Pattern has an end-to-end regression, alongside sample provider, invalid
script, root-source and mixdown guards.

**Still unchecked:** streaming/background lookahead and underrun policy,
audio-thread-safe cancellation during published lookahead, private nested
mixdown clocks with native frame seeking and preserved speaker feeds,
parent physical channel note lifecycle and advanced effects, full
realtime/export PCM parity, playback transport/diagnostics integration,
and migration of production scheduling.

### Bounded asynchronous preparation checkpoint (milestone 34)

The **experimental** `AsyncPreparedIncrementalAudioSource` starts a
dedicated bounded-lookahead worker via
`PreparedIncrementalPlaybackPlan.StartLookahead(lookaheadFrames)`.
The producer prepares only up to a moving, exact output-frame horizon,
rather than eagerly enumerating an infinite Sequence. Only the single
worker calls `TryStep` / Roslyn; the callback never waits on it.
Insufficient coverage produces an entire silent callback block with an
atomic underrun count but leaves the **musical playback head unchanged**,
so later prepared audio resumes at the missed musical frame.
Producer exceptions are exposed as `PreparationError` outside the callback;
plan disposal stops and joins the worker. Deterministic tests cover
blocked-producer underrun and recovery, maximum-ahead frame accounting,
producer faults, and worker lifetime.

**Remaining unchecked:** production transport/UI wiring of underrun
measurement/status (indicator hidden until first real underrun); a
separate byte/event-count buffering policy; asynchronous cancellation
while lookahead is published; runtime error/diagnostic transport;
realtime end-of-input and tail policy; lifecycle, mixdown and export
parity; migration of the production scheduler. The existing TODO item
for realtime underrun reporting is **not** marked complete by this
experimental adapter.

### Private-clock recursive mixdown checkpoint (milestone 35)

The experimental prepared recursive factory can now start a
`Mixdown=true` data/script Pattern or Sequence as an independent
invocation-local **private recursive clock**. Each start owns its own
`IncrementalRecursiveTimeline`, `SequencingContext`, prepared adapter,
and native speaker-channel PCM session. The producer pre-renders
private frames before publishing parent coverage; parent PCM callbacks
only read immutable, concurrently published chunks, using a unique
transient sound ID per invocation. Recursive mixdowns are allowed;
source cycles fail at preparation; direct stereo speaker feeds are
preserved without collapsing to a single mono playback channel.
Separate starts of the same child have separate clock/voice state,
and private Tempo changes do not retime the parent. Disposal follows
the nested plan.

**Still unchecked before cutover:** producer-aware native source-frame
seeking (especially offsets beyond lookahead and backwards replay),
memory-bounded private PCM buffer retention/backpressure, full parent
Off/Cut/Fade/Continue/NNA propagation into child voices and private
clocks, mixed instrument-owned recursive sources, transformations of
nested pitch/playback speed, realtime/offline end handling and parity,
and production transport/export migration. These are structural
remaining requirements, not reasons to add an opt-in production switch.

### Parent-to-private voice lifecycle checkpoint (milestone 36)

The experimental recursive PCM producer now forwards parent Note Off,
Cut, Fade, ordinary replacement/NNA displacement and tracker S70–S76
past/new-note actions to the affected private mixdown voice **at the
quantized child-frame boundary**, before publishing its audio horizon.
Release stops executing future child Pattern/Sequence notes and ends
child input at that frame, allowing finite child tails to drain.
Continue retains an independent old private source; Cut terminates
it; Fade requests normal child-voice fade as well as retaining the
parent's own voice fade behavior. Invocation-scoped virtual note
controls and previously eligible scoped/global broadcasts also
forward their lifecycle events. The shared tick merger admits
the tracker NNA/past-note control commands and uses the common
PatternNoteProcessor to resolve them. All child timeline and PCM
work remains off the audio callback. Tests cover producer-side
input release, current/continued voices, displaced past-note
cut and NNA Off.

**Still unchecked before scheduler cutover:** complete NNA policy
inheritance for indirect instrument recursive tones and configured
per-note fade durations, broader indirect recursive source graphs,
byte-bounded private PCM storage and native producer-coordinated seek,
asynchronous cancellation through published lookahead, and realtime/
export transport and finite-lifetime parity. Do not add an opt-in
production scheduler: retire the legacy route when these structural
requirements are satisfied.
