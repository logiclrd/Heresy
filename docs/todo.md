# Heresy TODO

This checklist tracks **remaining** work as of 2026-10-10 and summarizes
completed architectural contracts where they affect open tasks. Historical
milestones and red-to-green corrections are recorded in
[incremental-sequencing.md](incremental-sequencing.md). The early exploration
in [recursive-sounds.md](recursive-sounds.md) is historical, not a current
implementation inventory. The production code and regression tests are
authoritative whenever an earlier design sketch differs.

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

**Flattened-source instigating-note ownership is complete (step 62).**
The cross-order/scoped memory, live volume-ancestry, NNA and virtual
channels, recursive private Instrument sources, Cut/Off/Fade,
diagnostics/UI indications, cancellation, and bounded retirement audits
from steps 47–62 are implemented and regression-tested. The detailed
design and red-to-green corrections are in
[incremental-sequencing.md](incremental-sequencing.md).
Remaining advanced timing, seeking, export-stress and unusual mixed
script/effect compatibility are tracked separately below, not as an
unfinished flattened-source ownership requirement.

- [ ] Investigate **remaining recursive pitch-modulation compatibility**
  with a reproducible note-level or PCM counterexample before changing
  semantics. `SoundState.PitchTrajectory`, native sample pitch slides,
  modulation curves, recursive initial pitch transposition and private/
  flattened playback-speed multipliers already exist. Critically, the
  flattened *instigating note* intentionally suppresses voice-specific
  portamento, vibrato, retrigger and pitch effects; this is a completed
  design rule, not an unimplemented feature to bypass. Audit independent
  child-note automation and pitch-modulated Instrument/private sound
  seeking separately, without conflating them with live source-volume
  ancestry or tracker Tempo.
- [ ] Extend **shared-clock cross-rate Tempo arbitration** only where
  the coordinator currently rejects it. Single scaled Txx slides,
  same-rate simultaneous Txx, SEy repetition across independent
  unscaled invocations, interruption, and piecewise Tempo ramps already
  have tests. The concrete unsupported cases are simultaneous
  *different-rate* Txx slides and scaled flattened Txx combined with
  SEy repeats; preserve each source's captured tick span, channel-order
  clamping, deferred deadlines and cancelation without ever eagerly
  enumerating future orders.
- [ ] Finish unsupported advanced tracker/script effect combinations in
  the shared-tick coordinator. In particular, verify negative fixed
  wall-time offsets, advanced/global effect deadlines, incompatible
  simultaneous Tempo spans, delayed/overlapping command memory, and
  remaining tick/repeat behaviors. Do not apply future Source, Tempo or
  effect state prematurely; reject unsupported combinations explicitly.
- [ ] Extend **advanced** lifecycle compatibility beyond the completed
  flattened-source ownership model, especially interactions between delayed
  tracker effects, mixed data/script timing, multichannel routing, and
  unusual instrument release envelopes. Existing indirect Instrument,
  S7x, NNA, virtual broadcast, Cut/Off/Fade and private-tail ownership are
  implemented and tested; avoid reopening those milestones without a
  demonstrated counterexample.
- [ ] Complete edge-case **native source-frame seeking** (Oxx and Qxy
  retrigger) across nested Pattern, Sequence and instrument graphs, including
  offsets crossing lifecycle boundaries and repeated invocations. Backward
  seeks already reconstruct deterministic private generators without
  retaining event/PCM history; retain that architecture and verify parity.
- [ ] Optionally optimize expensive long-distance private seek/reconstruction
  using deterministic bounded checkpoints **only if** realtime ring-buffer
  underruns warrant it. Do not reintroduce unbounded event journals or
  schedule/PCM pre-rendering.
- [ ] Expand end-to-end **export/realtime parity** and advanced script
  compatibility tests: additional nonterminating script-Pattern cases,
  complex flow with musical-time adjustments, finite export-body and tail
  limits, and multi-speaker parity. Step 62 already stress-tests 160
  indefinitely scripted Sequence orders, deep cancellation, chunk-invariant
  recursive PCM, overlapping NNA tails and registration reclamation;
  the existing cycle protection, cooperation budgets and export limits
  remain implemented.

## Output audio configuration and physical speaker processing

**Final-speaker filtering implemented:** `OutputChannelConfiguration`
already modeled None/LowPass/HighPass and cutoff. The final
`PlaybackSession` now applies independent stateful one-pole speaker
filters **once after the complete mix and global volume**, preserving
continuity across PCM blocks. Private recursive Pattern/Sequence mixer
sessions intentionally bypass this output stage to avoid filtering their
sound a second time. Offline quiescence includes the filters' residual
tails, which decay to digital silence. Mono/stereo response, speaker
independence, nested private mixes and chunk invariance are tested.
See [audio-output.md](audio-output.md).

**User-configurable output implemented:** Options → Audio Output
now selects mono, stereo, 5.1 or 7.1, sample rate, individual speaker
position (X/Y/Z), positional importance, and each speaker's None,
LowPass or HighPass filter with cutoff. The ordered outputs are explicit;
the LFE speaker feed is **not** automatic bass management. Changes
stop the existing SDL transport before the next PCM format is selected.
The application shares one thread-safe, immutable-per-render
`AudioOutputSettings` snapshot with realtime and export factories.
A running export retains its starting format even when UI preferences
change; configuration is session-scoped, not yet persisted to disk.
Presets, dynamic factory capture, WAV sample rate/channel headers,
5.1/7.1 speaker ordering and multi-block filtering have regressions.
See [audio-output.md](audio-output.md).

- [ ] Expand **device-specific** 5.1/7.1 end-to-end coverage:
  verify SDL hardware mapping and encoder-specific multichannel support
  with supported physical devices and exported FLAC/MP3 layouts.
  Engine-side 5.1/7.1 speaker ordering and WAV mono/stereo headers are
  already tested. Preserve the distinction between ordinary speaker
  feeds and true bass management.
- [ ] Optionally persist output preferences between application launches,
  with versioned validation; settings currently live for the desktop
  session and do not alter song files.

## Export workflow

**Export progress and cooperative cancellation implemented.**
`OfflinePlaybackRenderer` reports progress after written PCM blocks in
logical musical frames and distinct release-tail frames. A fixed-duration
session may expose its known logical total; the production coroutine
renderer deliberately reports **no total** until its ending is discovered,
never eagerly enumerating future Sequence orders or inventing a completion
percentage/ETA. The UI displays an indeterminate progress indicator and
elapsed rendered musical time, switching to a release-tail indication.
Cancellation checks the token at safe PCM-block boundaries, before
finalizing the sink and immediately before atomic file replacement.
Canceled exports dispose their private playback plans and temporary files,
preserving any existing destination. The UI prevents concurrent exports
and cancels when the progress window or owner closes.
See [export-workflow.md](export-workflow.md).

- [ ] Optionally add a **determinate** export progress percentage only when
  a separately proven finite total is available without unrolling an
  unbounded script; the default coroutine path must stay indeterminate.
- [ ] Support **additional WAV bit depths** beyond default 16-bit PCM,
  and check encoder-specific validity for user-selected output rates and
  channel layouts. Realtime and export already share the chosen sample
  rate, speaker count, positions and filter configuration; retain FLAC
  as the default format with MP3/WAV available.
- [ ] Ensure export and realtime render identically for equal snapshots and
  render configurations, aside from intentionally different end-of-song
  handling and output encoding.

## Asset portability

- [ ] Add an interactive recovery flow for missing/unreadable sample assets
  when opening an existing project: select substitute files or search folders,
  validate loaded encoded data, and retry without mutating the current
  document on failure. Preserve in-memory decoded PCM and archive semantics.

## Playback state and authoring feedback

**Offline export runtime warnings integrated.** Both realtime and
export now deliver `SequencingDiagnostic` reports through the same
bounded runtime Warnings history. The export service drains its
shared, per-plan diagnostic log after completed PCM blocks and in a
`finally` path, including on cancellation and failure. Explicit
private Pattern/Sequence mixdowns reuse the root plan's log, rather
than isolating their warnings. UI notifications are posted to the
Avalonia dispatcher; export entries use an `[Export]` prefix and
the existing 500-entry history/clear action. HRSEQ001/002 and
HRSEQ003/004 retain their existing 32-individual-message-per-kind
suppression caps. No script/UI callbacks run in the audio worker.
See [export-workflow.md](export-workflow.md).
**Realtime PCM health surfaced.** The SDL session exposes the PCM ring's
atomic underrun count and its already-captured worker/device faults through
a read-only health contract. The transport samples it on the existing
non-audio timer, resets status when a playback session is replaced/stopped,
and reports faults once per session to the existing bounded Warnings UI.
An underrun is recoverable: the status-bar count remains **hidden until
the first underrun**, while SDL inserts silence without advancing or
skipping future musical frames. Live audition and completed pattern-position
tracking keep status monitoring active. See [audio-output.md](audio-output.md).
**Playback snapshot freshness implemented.** Realtime song/Pattern/Sequence
and live audition sessions now publish the exact frozen `AudioRevision`
and source-document identity after a successful start. The status bar
separately marks playback whose audio has changed since its snapshot,
or playback continuing from a replaced song document; layout-only
edits, saving the file, and dirty-document status do not imply stale audio.
The indicator clears on restart/stop, and live note releases keep their
existing snapshot until a new note requires refresh. Lazy transport and
UI-dispatched edit events preserve the single-worker audio model.
See [playback-snapshots.md](playback-snapshots.md).
**Expensive native-source seek hints implemented.** The Pattern editor now
identifies Oxx starts with nonzero effective source-frame offsets and Qxy
retriggers that may target ReplayRequired private mixdowns, including Source
recall, repeated Sequence-order occurrences and conditional Instrument or
script/flow cases. Tooltips explain possible realtime replay costs while
offline export remains correct. The renderer reports actual ReplayRequired
bound-voice offsets/retriggers through the shared bounded HRSEQ005/006
runtime diagnostics sink, including nested private mixdowns. Options →
Show expensive source seek hints suppresses editor decorations and future
UI warning display for these hints only; it never changes Oxx/Qxy execution
or stored effects. See [source-seek-hints.md](source-seek-hints.md).

## Startup branding and application identity

**Application icon wired into the desktop window and Windows apphost.**
`Heresy.UserInterface/Images/Icon.ico` is explicitly packaged as an
Avalonia resource and loaded into `MainWindow.Icon` without relying on
an installed file path. The executable project sets `ApplicationIcon`
to that same source file, allowing the .NET SDK to embed the multi-size
icon in the native Windows `.exe` stub. CI tests the Avalonia resource
and cross-publishes `win-x64`, inspecting the native PE executable
for both RT_ICON and RT_GROUP_ICON resources. Linux/macOS builds
retain their standard platform application hosts. See
[application-branding.md](application-branding.md).
**Startup splash implemented.** The existing `Images/Logo.axaml`
control is displayed in an owned, centered, chromeless, nontaskbar window
above the already-opened main window. The one-shot four-second
`DispatcherTimer` begins when the splash opens; any keypress, pointer
click or owner-window close dismisses it. The dismissal guard stops the
timer on every close path and prevents double close/reentrancy. Splash
creation is dispatcher-posted after main-window opening, never stalls the
main window or initializes audio. See
[application-branding.md](application-branding.md).

## UI polish and requested editor redesign — newly planned

The detailed requirements and acceptance contracts are recorded in
[ui-editor-redesign.md](ui-editor-redesign.md). These items are **not**
implemented simply because they are listed. In particular, keep existing
Core identity/revision behavior and avoid treating visual grid drafts as
persisted notes.

- [ ] Add **Follow** playback in the Pattern editor: center the active
  playback row where scrolling permits, gated by a `Follow` checkbox;
  toggle via backtick, Ctrl+F or Scroll Lock. Respect Sequence-projected
  display rows and manual scrolling when Follow is off.
- [ ] Promote **Envelopes** to a first-class section/pane: two panes on
  the upper row (Sequences, Patterns) and three below (Samples/Patches,
  Envelopes, Instruments); handle tree-root persistence and placement.
- [ ] Change UI labels **Sample/Samples** to **Patch/Patches** only where
  the objects represented may also be FM synth generators. Keep
  sample-specific waveform/file terminology.
- [ ] Add a reusable **graphical ADSR envelope editor** with right-edge
  0/Note Volume/Sustain annotations, draggable Attack/Decay/Release
  vertical boundaries and Sustain horizontal segment. On coincident
  zero-duration handles, drag the **second** line to increase duration.
  Retain valid unrestricted finite sustain-scalar semantics.
- [ ] In the FM editor, allow creating an Envelope node with no existing
  envelopes. Its selection starts blank, with an italic top **New...**
  option that creates an envelope. Embed the same live graphical ADSR
  editor in the selected Envelope node's detail pane.
**Save-changes dialog and picker-first File → Open implemented.**
The confirmation buttons appear left-to-right as Yes, No, Cancel, with
Alt+Y / Alt+N / Alt+C access keys; Yes has IsDefault and Cancel has
IsCancel. File → Open selects a usable local path before invoking the
existing unsaved-change guard, so picker cancellation never triggers a
save prompt. A canceled/failed guard prevents loading; document and JSON
save mode are replaced only after a successful load/validation.
New and Exit retain their original guard behavior. A headless-testable
action factory and open-flow coordinator cover button roles, choice
mapping, event ordering, cancellation and failure preservation.
See [ui-editor-redesign.md](ui-editor-redesign.md).
- [ ] Redesign the **Instrument tone-table editor** as one grid with
  implicitly managed specifications. Build the blank top row and
  reverse-index, ten-octave/C-11-with-Offset rows on load/Divisions
  change; preserve assigned out-of-range rows in descending order with
  ARGB `40FF0000` highlighting; support Delete and blank-row
  focus-loss migration/overwrite.
- [ ] Implement all **nine tone-grid columns** with the specified
  heading/blank/readonly/editable and dropdown semantics, including
  48-divisions-per-octave closest-note `+`/`-` notation, logarithmic
  pitch offset and the pitch-adjusted closest-note selection range ±0.3.
  Update closest-note automatically on multiplier edits without
  snapping the entered value; snap multiplier only when the user
  actively chooses a note.
- [ ] Apply Instrument grid changes to Core automatically. Rows with
  no Source must remain as editor-local drafts (even with other data)
  and are not persisted; adding/removing a Source publishes/removes
  its underlying tone assignment without losing draft UI state.
  Keep shared tone-specification indices and audio revisions correct.

## Documentation and later maintenance

**Documentation reconciliation completed.** The README now describes
load/import-time WAVE/FLAC/MP3/OGG/AIFF decoding, immutable PCM shared
with playback snapshots, and the single production coroutine engine
for both realtime playback and export. Historical milestones in
[incremental-sequencing.md](incremental-sequencing.md) and
[recursive-sounds.md](recursive-sounds.md) explicitly mark obsolete
eager scheduler, event-journal, callback rendering, unit-speed
mixdown and incomplete flattened-source ownership claims as superseded.
The current source, [sample-storage.md](sample-storage.md), regression
tests and the remaining TODOs are authoritative.
- [ ] Add format-version migration tooling **only when** actual documents
  require schema evolution; intentionally retain format version 1 during
  pre-release development.
