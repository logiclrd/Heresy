# Heresy TODO

This checklist tracks **remaining** work as of 2026-10-09 and summarizes
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

- [ ] Show offline export **rendered musical-time progress**, without
  inferring a fixed logical total from an indefinitely scripted Sequence.
  Use a determinate ratio only when a trustworthy finite total is known;
  otherwise show an indeterminate elapsed-musical-time status. Never
  pre-expand future song orders merely to produce an ETA.
- [ ] Add cooperative cancellation at safe render blocks, preserving the
  atomic temporary-output-file behavior and leaving existing exports intact.
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
  playback/export cutover**. Mark superseded material in
  docs/incremental-sequencing.md and docs/recursive-sounds.md as historical,
  especially old eager scheduling, unit-speed private mixdown, and
  unimplemented-ownership claims. Treat current code,
  docs/sample-storage.md and tests as authoritative.
- [ ] Add format-version migration tooling **only when** actual documents
  require schema evolution; intentionally retain format version 1 during
  pre-release development.
