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
  Isolated tracker Txx effects now use PatternNoteProcessor for
  T00 memory, immediate T20-TFF Tempo sets and T0x/T1x continuous ramps;
  the shared tick merger integrates each ramp analytically (and inverts
  it for fixed wall deadlines), so overlapping child/parent cursors
  observe the actual evolving Tempo rather than its future endpoint.
  Concurrent Txx commands on compatible row spans now compose a single
  ramp with each legacy tick's clamp applied in mapped physical-channel
  order, independent of cursor creation order. New standalone global Tempo
  and later Txx commands interrupt an active ramp at the instantaneous
  shared-clock Tempo, not its future endpoint; Speed-only changes leave
  the current ramp running. Txx byte memory still resolves when due.
  Mixed-command Txx cells and simultaneous Txx on different captured
  row-speed spans remain explicitly unsupported.
  Next, move **remaining tracker row/effect semantics** into incremental
  resolution, including incompatible-span tempo arbitration, fine and
  whole-row delays, virtual targets, and user script coroutine
  instrumentation with distinct CPU-only checkpoints. Wire generic
  invocation-lifetime management for data and scripted Sequences.
  Preserve out-of-order scripted-event semantics or explicitly resolve
  their ordering policy before migrating playback. Replace the existing
  restricted chronological scheduler rather than adding more special
  cases. Require test parity and realtime/offline determinism before
  retiring eager scheduling.
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
