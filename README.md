# Heresy

Heresy is an experimental tracker architecture in which any sound-producing song
object can be used as a note source, including patterns and sequences.

## Solution layout

The repository is intentionally split by concern.

- `Heresy.Core` — persistent song model, IDs, timing primitives, sequencing
  contracts, patterns, sequences, samples, instruments and script source.
- `Heresy.Render` — abstract PCM generation, playback voices/channels,
  spatialization, sample rendering, effect processing and the common renderer.
- `Heresy.Render.SDL` *(planned)* — SDL3-CS real-time sink and buffering.
- `Heresy.Render.File` *(planned)* — FLAC, WAV and MP3 sinks.
- `Heresy.UserInterface` — Avalonia single-document tracker UI. The current
  document view projects the four fixed song-tree sections into Sequences,
  Patterns, Samples and Instruments panes, with sample import/editing and
  external-asset diagnostics; realtime transport remains separate.
- A Roslyn-backed restricted-C# compiler assembly will also be added separately
  once the Core/Render contracts have been exercised.

## Current architectural rules captured in Core

- Song objects use monotonically allocated 32-bit IDs and are referenced by ID,
  never by tree ownership.
- Deleted objects can leave tombstones containing their last name and kind.
- `MusicalTime` carries both a `TimeSpan` component and a row component.
- Tracker tempo is expressed as ticks per 2.5-second **diachron**; speed is ticks
  per row.
- `SequencingState` is shared by flattened sequencers; mixdown children clone it
  on entry, so their tempo/speed changes remain local.
- Sequencing randomness uses a framework-independent deterministic PRNG.
- One sequencer invocation may emit at most 1,000,000 note events.
- Raw pattern generation is separated from the common timing/state processor;
  data-driven and scripted patterns are two front ends to the same raw-event
  model.
- Data-driven patterns use a mutable row/channel grid whose cells contain a
  semantic note column (start/off/cut) and semantic effects. Tracker-specific
  notation and effect-memory behavior will be layered on top of this grid model.
- Sequences are finite and entries can specify a `StartRow`.
- Script object references are persisted in restricted-C# source as `_O(id)` and
  can later be projected by the editor as atomic named tokens.
- Native effects use wall-clock time. Tracker compatibility effects are
  row-scoped operators whose independent parameter deltas are composed with
  persistent baseline state on every output frame. Persistent operators commit
  their final delta at row end; transient modulators disappear without changing
  the baseline. Legacy tick-processing artifacts are not preserved when the
  musical operation is naturally continuous; instead the row's total legacy
  change is spread across continuous row time. See
  [Effect operators and tracker compatibility](docs/effect-operators.md).
- Native source-frame seeking is a semantic capability, not a performance
  promise. A seekable sound reports either direct or replay-required cost.
  Replay-required seeks remain fully supported and exact; a future editor may
  optionally highlight such rows as potentially expensive for realtime playback
  (with user-configurable suppression) rather than forbidding them. Flattened
  Pattern/Sequence playback need not expose source-frame seeking because it has
  no single cooked PCM timeline, while mixdown forms may expose replay-required
  seeking.

## Song persistence and external assets

The authoring model keeps asset locations as **full paths in memory**. Ordinary
external assets use absolute filesystem paths. Assets loaded from a consolidated
`.hm` module use directory-like synthetic paths whose `.hm` component names
the ZIP archive, for example `C:\\Music\\song.hm\\pcm\\kick.wav` on Windows or
`/home/user/Music/song.hm/pcm/kick.wav` on Unix-like systems. The asset layer
can open and hash either form transparently.

Persistence translates those paths rather than making the editable document
relative to its current filename:

- `.hm.json` is the transparent JSON representation. Asset paths written to
  JSON are relative to the JSON file's containing directory; loading resolves
  them back to absolute in-memory paths. Saving the JSON somewhere else therefore
  does not reinterpret the document's existing asset locations.
- `.hm` is a ZIP-based consolidated representation. It contains one root-level
  `.hm.json` manifest and every asset referenced by that manifest. Paths inside
  the archive always use `/`, may not contain `\\`, and may not escape the
  archive with `..`.
- When an external asset is bundled into `.hm`, a source already below the
  module's containing directory keeps that relative subdirectory hierarchy.
  Otherwise it is placed under `pcm/`. A literal backslash in a Unix filename
  is replaced by `_` when converted to an archive entry name.
- Loading `.hm` and importing another sample leaves the new sample pointing at
  its actual external full path until the next save. Saving the module then
  copies it into the archive and retargets the in-memory reference to the new
  synthetic `.hm` path.
- Saving an archive-backed document as bare `.hm.json` extracts its bundled
  assets beside the JSON using the same hierarchy they occupied in the archive,
  and retargets the in-memory references to those extracted files.

The current persisted schema is format version **3**.

## Toolchain note

Heresy targets **.NET 10.0**. GitHub Actions builds the solution and runs the
full test suite on pushes to `main`.

## User-interface boundary

The UI is a projection/editor of the semantic Core model rather than a second
song model. `DocumentWorkspace` owns the one active `SongDocument` plus its
save baseline, while `SongTreeItemViewModel` resolves tree object IDs to live
names and falls back through tombstones to raw IDs for broken references.
Tree-only reorganization uses `SongTreeEditor` and advances `DocumentRevision`
without advancing `AudioRevision`; moving a node never changes its ObjectId.

The document tree has four fixed top-level sections, persisted in version 3 in
clockwise document-view order: Sequences, Patterns, Instruments and Samples.
The Avalonia document mode projects those section subtrees as four panes:
Sequences top-left, Patterns top-right, Samples bottom-left and Instruments
bottom-right. The fixed section nodes themselves are not shown because each
pane is the visual root of its subtree. New objects receive one canonical tree
placement from `SongDocument.Add`; envelopes are grouped with Instruments.
Nodes may be reorganized within a section but not moved between sections.
