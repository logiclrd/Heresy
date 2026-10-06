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
  Patterns, Samples and Instruments panes, with sample import/editing, external-
  asset diagnostics, data-pattern editing and data-sequence arrangement editing;
  realtime transport remains separate.
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
- Data-driven patterns use a mutable row/channel grid whose cells contain
  first-class note (start/off/cut), source, volume and semantic-effect data.
  Source is optional per row and participates in per-physical-channel tracker
  memory; tracker-specific notation and effect-memory behavior are layered on
  top of this semantic grid model.
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

- `.hm.json` is the transparent JSON representation. Its default mode writes
  portable `/`-separated relative paths only for assets inside the JSON file's
  directory subtree. It never emits `..` to walk upward; a relative-mode save
  fails with an informative error if an external asset is outside that subtree.
  Save As also offers **Heresy JSON (absolute paths)**, which writes every asset
  location as an OS-conventional fully qualified path instead. The chosen JSON
  mode is retained by the workspace for subsequent ordinary Save operations.
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
- When loading bare JSON, a path beginning with `/` or containing `:` in its
  first component is treated as absolute. Absolute paths must match the host OS
  convention; relative paths may not escape the JSON directory. For now, loading
  fails if an asset is missing or its absolute-path convention cannot be resolved
  on the current host. A future UI resolution workflow will let the user locate
  replacement files or directories instead.

The current persisted schema is format version **1**. During initial pre-release buildout, breaking schema changes intentionally remain version 1 because there are no real-world Heresy documents to migrate yet. Format-version bumps and migrations will begin once the format is in actual use.

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

The document tree has four fixed top-level sections, persisted in
clockwise document-view order: Sequences, Patterns, Instruments and Samples.
The Avalonia document mode projects those section subtrees as four panes:
Sequences top-left, Patterns top-right, Samples bottom-left and Instruments
bottom-right. The fixed section nodes themselves are not shown because each
pane is the visual root of its subtree. New objects receive one canonical tree
placement from `SongDocument.Add`; envelopes are grouped with Instruments.
Nodes may be reorganized within a section but not moved between sections.

Opening or creating a data pattern switches the main workspace into pattern
mode rather than opening a modal editor. The pattern grid edits semantic note
(empty/start/off/cut), Source, Volume and effect columns directly; Start notes
carry pitch/playback-speed/mixdown while Source is independent pattern-cell data.
The editor exposes row/channel dimensions plus minor/major row-highlight
intervals and warns before shrinking dimensions when populated cells would be
discarded.

Pattern row divisions are presentation rather than typography: major/minor
rows use translucent background bands instead of bold text. The shared
`UserInterfaceConfiguration` currently defaults major rows to ARGB
`80808080` and minor rows to `40808080`, allowing the neutral grey to blend
lighter in dark themes and darker in light themes.

Opening or creating a data sequence likewise switches the main workspace into
an arrangement-list editor. Sequence entries remain ordered `(PatternId,
StartRow)` references rather than duplicating pattern data. The editor can add
live data or script patterns, replace an entry's pattern, edit its non-negative
start row, reorder/remove entries, and explicitly mark the sequence as the
song's `RootSequenceId`. Creating a sequence does not implicitly make it root.
Broken pattern references are preserved and projected through tombstones; their
start row remains editable and they can be repaired by selecting a live pattern.
Opening a live data-pattern entry drills directly into the pattern editor, whose
Back control becomes **← Sequence** and returns to the arrangement instead of
the document browser. Script-sequence and script-pattern source editing remain
future UI work. Existing Bxx/Cxx sequence-control semantics continue to be
handled by the Core sequence processor.

The note field supports direct tracker-keyboard entry. A current toolbar Source
(sample, instrument, pattern or sequence) and base octave are editor state; the
toolbar Source is not implicitly written when a note is entered. The physical-
layout convention is the familiar chromatic tracker piano:
`Z S X D C V G B H N J M` spans the lower octave and
`Q 2 W 3 E R 5 T 6 Y 7 U` the next, continuing through `I 9 O 0 P`.
Tracker piano entry is driven from Avalonia's layout-independent physical-key
codes rather than produced text, so changing the operating-system keyboard
layout does not move the musical keys: the physical QWERTY `Z` position still
enters C even if that key currently produces some other character. Entered
pitches are stored as semantic pitch multipliers relative to Heresy's existing
C4 reference convention, so multiplier 1.0 is displayed as `C-4`; exact
equal-tempered semitone multipliers are projected as tracker note names, while
arbitrary multipliers remain visible numerically. `1` enters note cut and
backtick enters note off. Every recognized note/cut/off entry advances one row,
enabling paint-down entry. Replacing an existing start note changes its pitch
but preserves its playback-speed multiplier and mixdown flag; Enter remains
available for the detailed semantic note dialog.

Source is a separate tracker column and may be omitted. An explicit Source value
updates the remembered source for that mapped physical channel even on a row
without a note. A Start note whose Source field is empty uses that remembered
source; if the channel has never had a source, the note remains valid pattern
data but generates no playback start. Sequencing source memory survives later
pattern invocations in the same sequencing context, while mixdown children
naturally receive independent memory with their independent channel-state map.
The cursor order is **Note → Source → Volume → Effect Command → Effect
Parameter**. Clicking any field moves the cursor there, including blank space.
When Source is already focused, clicking it again opens a field-anchored picker
wide enough to show full source names; `Alt+Down` opens the same picker.
Space copies the Source selected in the pattern toolbar into the field, `.`
clears it, and the Source field's right-click menu also provides **Clear**.

Volume is first-class pattern-cell data, independent of the effect stack. It is
stored as an optional normalized value and presented in the tracker grid as a
decimal `00..64` column (`..` means absent). Two decimal digits set the volume and
advance one row; `.` clears it and advances, so volume values can be painted
down rows even when those rows contain no new notes.

Pattern translation treats that single volume column differently according to
what the row does. For an ordinary `StartPatternNote`, the value is folded
directly into `StartNoteCommand.Volume`, keeping the common note+volume case
atomic and compact. A volume-only row, a note-off row, or a note used only as a
tone-portamento target instead emits `SetNoteVolumeCommand` for the current
note. Note off deliberately keeps the releasing voice attached to its physical
channel, so same-row and subsequent volume-column changes continue to affect
that voice throughout its release phase; more such changes may follow until the
voice ends naturally or is explicitly cut. A note-cut row is different: CUT
ends the current note immediately, so any volume value stored on that same row
is retained as pattern data but ignored during translation and does not alter
the channel's remembered note volume. A direct start volume is applied by
playback only after the source successfully resolves and starts, so a broken
source cannot seed the volume of a later note.

Effects are projected as coloured tabs attached to the right edge of each cell.
Multiple effects remain in semantic application order and collapse into an
overlapping stack with a constant five-pixel reveal between tabs; the compact
effect control occupies only the effect column, so it cannot intercept clicks
intended for the note or volume fields and the cell clips the stack so it can
never bleed into a neighbouring channel. Hovering or tapping a stack expands it
over the cell into equal-width tabs side by side. Expanded tabs are right-aligned
when their combined width is smaller than the cell; when they need the full cell
or overflow it, they use the available width and left/right edge controls scroll
the strip on hover or tap. The expanded strip collapses on a click outside it,
or once the pointer moves more than five row heights beyond that pattern row.
Clicking the note, volume, effect-command or effect-parameter region places the
tracker cursor in that field; clicking a visible effect tab selects its logical
field while expanding the stack.

The tracker cursor treats an IT-style effect as two keyboard fields: command and
parameter byte. Typing a command letter replaces/creates a lone tracker effect
while preserving its existing parameter and advances one row; typing two hex
digits replaces the parameter byte and then advances, while `.` writes `00`
and advances immediately. This deliberately supports tracker paint-down entry
such as repeated `G` commands or repeated `15` parameters across rows whose
effect types differ. A collapsed cell containing multiple effects rejects direct
typing until Enter expands it. In the expanded strip, left/right traverse the
individual fields but clamp at the ends; up/down collapse and move vertically,
and Enter collapses in place. Native effects occupy one whole-tab keyboard stop,
ignore direct typing, and are reserved for a future Enter/double-click parameter
dialog. Native and IT-style tabs use the same visual footprint and may coexist
in one stack.

Effect stacks can be edited without leaving the tracker keyboard flow. `Alt+Insert`
inserts a new IT-style slot before the selected effect; `Alt+Shift+Insert` inserts
after it and selects the new slot. A new slot is persisted as a musically inert
`...` placeholder rather than as a real `xx=00` command, because many tracker
commands use zero as effect memory and therefore are not no-ops. Parameter entry
may precede the command (for example, an empty slot can hold `.15`, then become
`G15` when `G` is entered). `Alt+Delete` removes the selected effect; when only
one effect remains, the expanded stack collapses back to the ordinary single-
effect view. `Alt+Left`/`Alt+Right` reorder the selected effect while expanded,
and `Alt+Home`/`Alt+End` select the first/last stack member. Expanded tabs may
also be dragged to reorder them. Right-clicking a tab first makes it the logical
target, then offers **Insert Before**, **Insert After**, and **Delete**, all routed
through the same stack-editing commands as the keyboard shortcuts. Copy/paste is
intentionally deferred.
