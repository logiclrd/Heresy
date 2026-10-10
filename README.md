# Heresy

Heresy is an experimental tracker architecture in which any sound-producing song
object can be used as a note source, including patterns and sequences.

## Solution layout

The repository is intentionally split by concern.

- `Heresy.Core` — persistent song model, IDs, timing primitives, sequencing
  contracts, patterns, sequences, samples, instruments and script source.
- `Heresy.Render` — abstract PCM generation, playback voices/channels,
  spatialization, sample rendering, effect processing and the common renderer.
- `Heresy.Render.SDL` — SDL3-CS realtime audio-output backend implementing the common float-PCM sink contract.
- `Heresy.Render.File` — deterministic streaming offline PCM and FLAC, MP3,
  or 16-bit RIFF/WAVE file sinks, with FLAC the default export format.
- `Heresy.Playback` — shared, snapshot-bound incremental recursive
  Pattern/Sequence composition for realtime playback and file export.
- `Heresy.UserInterface` — Avalonia single-document tracker UI with the
  current five panes (Sequences, Patterns, Patches, Envelopes, Instruments),
  sample import/editing, data/script editors, FM synth graphs, Instruments
  and ADSR envelopes. Graphical ADSR editing and a redesigned Instrument
  tone grid are **planned**, not yet built.
  See [UI redesign](docs/ui-editor-redesign.md).
- `Heresy.Scripting` — Roslyn-backed restricted-C# analysis/compiler boundary.
  Semantic object-reference analysis/projection and the first executable
  pattern/sequence compiler are implemented while Roslyn remains entirely
  outside `Heresy.Core`.

Detailed planned authoring, playback and tracker-workflow items are tracked in
[docs/todo.md](docs/todo.md).

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
- Data-driven Sequence order lists are finite and entries can specify a
  `StartRow`; scripts and flow can repeat indefinitely, so production
  playback and export enumerate them lazily.
- Script object references are persisted in restricted-C# source as `_O(id)`.
  Roslyn semantic analysis ignores comment/string lookalikes and shadowing user
  declarations, reports malformed intrinsic calls, and projects real references
  through live objects, tombstones or raw-ID fallback.
- Native effects use wall-clock time. Tracker compatibility effects are
  row-scoped operators whose independent parameter deltas are composed with
  persistent baseline state on every output frame. Persistent operators commit
  their final delta at row end; transient modulators disappear without changing
  the baseline. Legacy tick-processing artifacts are not preserved when the
  musical operation is naturally continuous; instead the row's total legacy
  change is spread across continuous row time. See
  [Effect operators and tracker compatibility](docs/effect-operators.md).
- Native source-frame seeking is a semantic capability, not a performance
  promise. Seekable sounds declare direct or replay-required cost. Oxx/Qxy
  warnings and an optional editor suppression preference now exist, without
  changing exact source-frame semantics. A private Pattern/Sequence mixdown
  reconstructs its generator on backward seek; a flattened source need not
  expose one seekable PCM timeline. See
  [source-seek hints](docs/source-seek-hints.md).

## Song persistence and external assets

**Decode at load/import time, never on the audio worker.** Supported
WAVE, FLAC, MP3, OGG Vorbis and AIFF/AIFC encoded sample files are
decoded via `SampleAudioCodec` into immutable interleaved `SamplePcmData`
owned by the live `SampleDefinition`. Playback snapshots share this
immutable PCM. `InMemorySampleDataProvider` exposes it without accessing
any file/ZIP, hashing a source, or invoking an audio codec during rendering.

An imported sample owns an immutable pending copy of its *encoded* payload
until that payload is saved into the current document. Already-persisted
samples retain only their encoded storage identity/signature, not a
duplicate resident encoded copy. Saving verifies/reuses existing encoded
data or commits pending bytes as appropriate. Decoded PCM is retained
throughout. For full lifecycle contracts see
[sample storage](docs/sample-storage.md).

The authoring model keeps persisted encoded-asset identities as
**full paths in memory**. Ordinary
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
- Loading `.hm` immediately decodes samples into immutable PCM and
  retains their persisted encoded identity within the current module.
  Importing another sample copies and decodes the source immediately,
  rather than depending on its external location after import. The next
  successful save writes the pending bytes to the current song and
  replaces the pending payload with a persisted encoded identity.
- Saving an archive-backed document as bare `.hm.json` extracts its bundled
  assets beside the JSON using the same hierarchy they occupied in the archive,
  and retargets the in-memory references to those extracted files.
- When loading bare JSON, a path beginning with `/` or containing `:` in its
  first component is treated as absolute. Absolute paths must match the host
  OS convention and relative paths cannot escape the JSON directory. Missing
  or unreadable encoded assets fail at **load time**, not at first playback;
  interactive asset recovery remains a TODO. Successfully loaded PCM does
  not depend on the continued presence of the original files.

The current persisted schema is format version **1**. During initial pre-release buildout, breaking schema changes intentionally remain version 1 because there are no real-world Heresy documents to migrate yet. Format-version bumps and migrations will begin once the format is in actual use.

## Offline file rendering boundary

The production `OfflineSongRenderPlanFactory` snapshots the song and
current output configuration and builds the **same**
`PreparedIncrementalPlaybackFactory` /
`IncrementalRecursiveTimeline` /
`PreparedIncrementalAudioSource` graph used by realtime playback.
The root Sequence, script-generated orders and nested Pattern/Sequence
invocations are consumed only as the musical clock advances. There is
no eager full-song `NoteSchedule`, second recursive engine, replay-event
journal or cooked private PCM cache.

`OfflinePlaybackRenderer` streams PCM blocks from that incremental
source to an `IAudioFileSink`; it discovers the arrangement's actual
end during rendering. It then issues end-of-input, preserves finite
release envelopes and anti-click tails, and cuts voices without
a deterministic post-Note-Off end. Explicit logical-body and tail
frame limits bound unending scripts. The older finite-`PlaybackSession`
render overload remains available for ordinary callers but is not
the production song-compilation path.

The export-only Bxx loop policy suppresses a jump on its **third
encounter** with the same Pattern/row/target, after rendering the
encountered row; realtime does not have that limit.
FLAC is the default; MP3 and 16-bit PCM RIFF/WAVE are also available.
`WaveFileSink` streams and patches file sizes, while MP3/FLAC use
the `NAudio.SoundFile`/libsndfile native codec boundary.
The worker writes to a temporary sibling path and atomically replaces
the destination only after successful completion; cancellation or
failure preserves the old output file.

Export progress is reported after completed PCM blocks, with logical
body and release-tail phases and cooperative cancellation. An
incremental script may have no known end, so production export
normally shows **indeterminate** progress and rendered musical time
rather than a fabricated percentage or ETA. The bounded sequencing
warnings from parent and private-child invocations flow to the same
UI diagnostics history, including at cancellation/failure.

See [export workflow](docs/export-workflow.md),
[incremental sequencing](docs/incremental-sequencing.md) and
[audio output](docs/audio-output.md).

## Realtime audio boundary

`Heresy.Render.Realtime` exposes the backend-neutral PCM source,
output backend and output session contracts. The desktop
`SongPlaybackTransport` accepts F5 root-Sequence, F6 repeating
Pattern, F7 Sequence/Pattern start-location and F8 stop commands,
plus ad-hoc row/note audition and held preview. Each request captures
an immutable `SongDocumentSnapshot` and its output configuration;
the SDL backend remains lazily initialized until playback is needed.

The production `PlaybackRequestAudioSourceFactory` creates the
**same coroutine-based recursive source** as offline export,
not a complete eagerly compiled `NoteSchedule`. The
`IncrementalRecursiveTimeline` advances data or scripted
Pattern/Sequence invocations cooperatively at sample-exact
musical frames. Small ad-hoc audition schedules are wrapped as
temporary raw Patterns and use the same pipeline.

Flattened Pattern/Sequence sources share their parent clock and
represent one logical instigating note for live source-volume effects,
Note Off, Cut and new-note displacement, while child voices retain
independent channel memory, nesting scope and release tails.
Private nested mixdowns (including Instrument-selected recursive
tones) use independent incremental generators, output-speaker feeds,
pitch/playback-speed multipliers and lifecycle state. Forward and
backward Oxx/Qxy native seeks are handled deterministically;
rewinds reconstruct nested generator state, without retaining
event histories or precooked PCM. Remaining unusual advanced
Tempo/effect combinations are tracked in the TODO rather than
treated as undone core architecture.

**The SDL callback never runs song scripts, sequencing or PCM
synthesis.** One dedicated `BufferedAudioOutputSource` worker
runs the entire incremental source and nested mixes, supplying
a bounded single-producer/single-consumer interleaved float-PCM
ring. The SDL audio-stream callback only reads that ring. On
underrun it outputs silence without fast-forwarding producer
musical time; worker failures are captured and exposed as
runtime diagnostics instead of propagating through native callbacks.

Realtime and export share sample/FM/instrument rendering, the
`PlaybackSession` voice/effect model, and the configured
mono/stereo/5.1/7.1 speaker output with positioning and per-speaker
None/LowPass/HighPass filtering. The final speaker filter stage
runs once after the complete mix; private submixes bypass it.
The UI distinguishes unsaved document changes from the freshness
of the immutable audio snapshot used for current playback.

Pattern playback-follow is enabled by the **Follow** checkbox in the
tracker toolbar. It keeps the highlighted playback row near the
vertical middle of the visible grid, using measured row locations
to handle Sequence separators and repeated Pattern occurrences.
Disabling Follow leaves playback highlighting active without
playback-driven scrolling. Physical **NumPad Period/Decimal**,
**Ctrl+F**, and **Scroll Lock** all toggle the same flag. The main
Period key retains its tracker editing role, and **backtick continues
to enter Note Off**. The follow feature leaves the edit cursor and
horizontal scrolling unchanged.

The Pattern editor auditions a note with top-row 4, a row with
top-row 8, and held notes with Caps Lock plus tracker piano keys;
releases queue Note Off into the same worker-owned playback state.
Neither these previews nor general realtime playback decode
encoded assets at render time.

See [audio output](docs/audio-output.md),
[playback snapshots](docs/playback-snapshots.md),
[incremental sequencing](docs/incremental-sequencing.md) and
[source-seek hints](docs/source-seek-hints.md).

## Documentation history

The older [recursive-sounds design record](docs/recursive-sounds.md)
and early chapters of [incremental sequencing](docs/incremental-sequencing.md)
describe intermediate prototypes. Their claims about eager scheduling,
replay journals, unit-speed-only private mixdowns, incomplete Note Off/
Cut propagation, or unisolated flattened channel memory are
**historical**, not current production limitations. The source,
regression tests, [sample storage](docs/sample-storage.md)
and [remaining TODOs](docs/todo.md) are authoritative.

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

The document tree has **five** fixed top-level sections, persisted in
visual row-major order: Sequences, Patterns, Patches, Envelopes and
Instruments. The top row has two equal-width panes (Sequences,
Patterns); the bottom has three (Patches, Envelopes, Instruments).
The shared six-column grid spans three tracks for each top pane and
two for each bottom pane. Pane action buttons wrap rather than overflow.
The fixed root nodes are not shown within their panes because each
pane already represents one section subtree.

**Patches** is the general UI label for the section containing both
decoded PCM Samples and FM synthesizers. The Core section enum remains
`SongTreeSection.Samples` for compatibility. UI labels tied strictly
to PCM data (sample import, sample file formats, sample waveform and
loop editing) still say **Sample**. New Envelopes receive canonical
placements in the first-class Envelopes section rather than under
Instruments. Folders can be reorganized within a section, not moved
between sections.

Legacy version-1 `.hm.json` and `.hm` documents with the former
four-section tree (Sequences, Patterns, Instruments, Samples) are
upgraded **during load**, without changing the song format version.
Envelope object/tombstone placements are extracted from the old
Instruments subtree, preserving their nested folder paths, object IDs
and references; non-envelope placements remain in Instruments and the
old Samples section is renamed Patches. Saving writes the new
five-section tree; migration alone does not increment audio or
document revision. See [document panes](docs/document-panes.md).

FM synth graph connections are drawn as orthogonal routes from producer to
consumer, with a small arrowhead **only at the consuming/input end**. Hovering
over a node border reveals connection ports: green output handles on the
right, blue input handles on the left, and an additional gold input handle on
operators for appending an input. Dragging between an output and a compatible
input (in either direction) creates the semantic connection; dropping elsewhere
does not change the graph. An oscillator has one replaceable multiplier input,
while operators can replace a selected input or append another ordered input.
Cycle detection and all other graph validation occur before committing, and
invalid drags leave the graph, revisions and routing hints unchanged.

Removing a non-output FM node disconnects every consumer atomically: oscillator
multiplier inputs become unset while retaining the oscillator settings, and
all matching operands are removed from operators (including repeated inputs).
An operator with no remaining inputs is converted into a zero-valued constant
with the same node ID, preserving its location, output selection and any
downstream dependencies without relaxing the Core requirement that operators
have at least one input. Surviving connection waypoints are remapped to their
new operator input indices. The deletion advances the audio/document revision
once. The selected output node still cannot be removed until another output
is selected.

Connection routes terminate at the exact input-port positions, including the
individual inputs of an operator; backward connections travel around an
exterior lane to reach the fixed port sides. Arrowheads follow the last
nonzero segment and scale down for short segments. Explicit routing waypoints
are retained and the route finishes with a short horizontal approach to the
consumer. Editor-only node positions and routing hints never alter the
semantic audio graph.

Connection waypoints are edited directly on the canvas. Pointer hit-testing
uses a three-physical-pixel tolerance on either side of the rendered wire,
with the display scaling accounted for. Dragging a wire creates a waypoint at
the clicked segment's position, inserted before or after existing waypoints
according to the location along the actual orthogonal route. A small gold
handle marks every waypoint; drag it to move it, or double-click it to delete
it. The path is previewed live during drag and persisted only on release,
and a simple click without moving does not add a waypoint. Deleting the final
waypoint clears the routing hint, restoring automatic routing. The old raw
text waypoint editor and its buttons have been removed; these layout-only
edits never advance the audio revision.

The FM editor has a focusable `Test` audition area. With keyboard focus there,
the same layout-independent tracker piano keys as the pattern editor
(`Z S X D C` and higher rows) start notes on independent live virtual voices
without requiring Caps Lock. Repeated physical key-downs do not retrigger;
releasing each key sends Note Off to its own voice. The physical keypad
`*` and `/` keys adjust audition octave (initially 4, clamped to 0–8),
reusing the pattern editor's octave-shortcut rules. Moving focus away,
deactivating the window or leaving the editor releases all held notes.
The live audition uses the FM synth as its sound source and never changes
the song merely by previewing a note. New note starts after semantic audio
edits refresh the live playback snapshot; note releases and layout-only
changes do not restart an existing playback session.

FM node-parameter fields commit individually on Enter or loss of keyboard
focus; dropdowns and checkboxes commit as soon as their selection changes.
There is no node-parameter Apply button. Each text field tracks its own latest
successfully committed value. Escape restores only the current uncommitted
draft: editing a field, tabbing/clicking elsewhere to commit, then returning
and pressing Escape does **not** undo that committed edit. Invalid values
report a validation error without changing the graph; invalid drafts are
reverted on focus loss. Parameter edits use the current immutable node value
when constructing the replacement, so editing another parameter does not
overwrite unrelated committed settings. Ordinary commits refresh the graph
without rebuilding the inspector or disrupting focus.


The desktop File menu offers New (`Ctrl+N`), Open (`Ctrl+O`), Save
(`Ctrl+S`) and Exit (`Ctrl+Q`). These shortcuts are handled by the window
only after the focused control has had an opportunity to handle its key event,
so pattern-editor key bindings take precedence on conflicts. File -> Exit uses
the normal window-close path: when the document is dirty, the existing
save/discard/cancel confirmation can prevent closing, just as it does for New
and Open.

Dialog action rows are anchored at the lower-right of their windows using a
flexible content area and a bottom button row, including in resizable import
and sample dialogs. Standard accept/confirm buttons are the Enter defaults,
while Cancel (or the Sample editor's Close button) responds to Escape. In the
unsaved-changes confirmation, buttons are arranged **Yes / No / Cancel**,
with Alt+Y / Alt+N / Alt+C access keys. Yes is the Enter default,
Cancel is the Escape action, and No explicitly discards unsaved changes.
File → Open now opens its picker **before** prompting: canceling the
picker does not prompt to save; a chosen usable path is only loaded
after the existing save/discard/cancel guard permits replacement.
A failed load preserves the current authoring document and save mode.

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

There is only one tracker-pattern editor implementation. When opened for a
single `DataPatternDefinition`, its display-row context maps directly to that
pattern. When opened from a data-sequence order, the same `PatternEditorControl`
is instead given a sequence context plus the order that should receive initial
focus. That context flattens the sequence's declared data-pattern entries into
adjacent display rows, respecting each entry's `StartRow`, while every display
row resolves back to the original live `(DataPatternDefinition, local row)`.
Scrolling or cursor paint-down can therefore cross a pattern boundary directly;
editing never creates sequence-local copies. Repeated references to the same
pattern are multiple projections of the same object, so an edit is refreshed in
every visible occurrence. Script-pattern and missing-pattern orders remain
visible as non-editable segment separators rather than being silently hidden.
The sequence projection follows declared arrangement order; it does not attempt
to predict runtime Bxx/Cxx jumps or pattern-break control flow, which remains a
Core sequencing concern. The Back control becomes **← Sequence** and returns to
the arrangement list.

Script patterns and script sequences can also be created directly from their
respective document panes and are edited by one shared main-workspace script
source editor. Script-pattern authoring exposes the same row/channel and
minor/major-highlight layout values as other pattern definitions; script
sequences can be explicitly selected as the song root. Opening a script-pattern
order from a data-sequence arrangement opens that script source editor with
**← Sequence** navigation rather than creating a second tracker representation.

Persisted script source remains ordinary restricted-C# text. Script editing now
uses AvaloniaEdit, and its `TextDocument` always contains the canonical source
including literal `_O(id)` expressions. Roslyn semantic analysis supplies the
raw source spans for actual Heresy object lookups; comments, string lookalikes
and shadowed helper calls do not become references. A custom
`VisualLineElementGenerator` replaces each semantic lookup only in the visual
line with one named `FormattedTextElement`. That visual element has visual
length 1 while consuming the complete raw `_O(id)` document span, so the object
name behaves as one editor token without introducing a second editable string
or a second coordinate system. AvaloniaEdit therefore keeps native caret,
selection, clipboard and undo behavior against valid raw restricted C#.
Current live names are projected without rewriting source, tombstones retain
their last-known names, and unresolved references display their canonical raw
ID.

Live authoring analysis also retains an incremental Roslyn syntax snapshot.
Each text change uses `SyntaxTree.WithChangedText`, allowing Roslyn to reuse
unchanged syntax while still returning an error-tolerant tree and diagnostics
for incomplete code. Valid semantic references and highlighting elsewhere in
the document remain available while another expression is being typed.

The script editor now builds syntax-highlighting spans directly from that same
Roslyn tree. Keywords (including contextual keyword spellings), string/character
and raw-string literals, interpolated string text, numeric literals, comments,
disabled/preprocessor text, and semantic object references receive distinct
highlight kinds. Comment/directive and object-reference spans take precedence
over nested lexical spans. In particular, the complete raw `_O(id)` source span
is emitted as one `ObjectReference` highlight rather than separate identifier,
punctuation and number spans. A `DocumentColorizingTransformer` clips those raw
spans to each `DocumentLine` and applies them with `ChangeLinePart` after
AvaloniaEdit has run the object-reference element generator. Consequently a
generated one-column object-name element is styled through the exact Roslyn
source span that produced it, while ordinary source remains normal
`VisualLineText`. Multi-line comments/raw strings are naturally colored a line
at a time.

Compiler diagnostics now use the same raw-coordinate model for in-editor
adornments. Diagnostic spans are normalized to the current document before
rendering; non-empty spans retain their source range, while zero-length Roslyn
diagnostics (such as an expected token at end-of-file) expand to a visible
one-character anchor when the document is non-empty. An AvaloniaEdit
`IBackgroundRenderer` asks `BackgroundGeometryBuilder` for the visual rectangles
corresponding to each raw span and draws severity-colored wavy underlines on the
text layer. Because the geometry builder resolves through the completed visual
line, a diagnostic covering a raw `_O(id)` span automatically underlines the
single generated object-name element rather than assuming one visual column per
source character. Error, warning and informational diagnostics use distinct
marker colors.

Diagnostic markers also drive hover help and navigation without invoking Roslyn
again. Pointer movement is converted by AvaloniaEdit from the rendered editor
position back to a raw `TextLocation`/document offset, then the current marker
catalog is queried for diagnostics covering that offset. The lookup is tolerant
of the end boundary of a diagnostic span so both halves of a generated
one-column object-reference element map back to the same hover. Overlapping
diagnostics are aggregated in severity order and shown in a pointer-positioned
tooltip containing severity, diagnostic code and message. The tooltip is cached
while the same diagnostic set remains under the pointer and is cleared
immediately when analysis refreshes or the pointer leaves the editor.

The persistent diagnostics list below the editor renders each diagnostic as a
clickable row. Activating one maps its *original* Roslyn span back into the
current AvaloniaEdit document, selects that range, moves the caret to its start,
scrolls the source location into view and focuses the editor. Navigation
deliberately uses the original span rather than the normalized visual-marker
span: a zero-width diagnostic at end-of-file therefore navigates to EOF even
though its squiggle is anchored to the preceding character for visibility.
Out-of-range stale spans are clamped to the current document. Object-reference
summaries remain non-interactive informational rows beneath the diagnostics.
The shared script editor continues to run the full restricted-C# compiler
validation without assembly emission/loading.

Core remains Roslyn-free through `IScriptObjectReferenceAnalyzer`. With no
analyzer, `SongReferenceAnalyzer` retains the conservative opaque-script
fallback. The authoring workspace supplies the Roslyn adapter for Save/Save As,
so persistence can retain tombstones referenced by real `_O(id)` expressions
while pruning tombstones mentioned only in comments or strings. If analysis is
unreliable because of syntax/reference diagnostics, the opaque safety fallback
remains active and tombstones are conservatively retained.

Script Pattern and Sequence definitions use the same incremental recursive
timeline as data-driven objects. Restricted-C# scripts are compiled through
the Roslyn scripting boundary into invocation-local cooperative iterators,
not eagerly evaluated into a full-song `NoteSchedule`. Pattern scripts
produce raw `NoteEvent` steps consumed by the common timing/effect processor;
Sequence scripts select the next order via `GetSequenceEntry(absoluteIndex,
sequenceIndex, previousSequenceIndex)` and may continue indefinitely.
The helper surfaces include `_O(id)`, `Note`, `Off`, `Cut`, `Tempo`,
`Speed`, `Play` (for Sequences), and deterministic `Random`.
`System.Math` is permitted; disallowed framework access, allocation,
async/exception control and other unsupported language features are
rejected with scripting diagnostics. Script loops yield cooperative
progress checkpoints and retain resource/iteration budgets so an
unbounded source need not block rendering. Generated state and seeded
randomness are scoped to an invocation, allowing deterministic
reconstruction when private sources are rewound.

The current song execution bridge is
`PreparedIncrementalPlaybackFactory`, not the removed eager
`SongScheduleCompiler`. It supports root or explicitly requested
Pattern/Sequence invocations with order/row starting positions, and
prepares restricted-C# generators for lazy invocation. A script is
evaluated only when musical progression reaches it; the resulting
event stream stays incremental throughout realtime playback and
export. Errors from invoked scripts remain surfaced through the
compiler/runtime diagnostic paths, and script layout/source edits
remain audio-affecting.

The Instruments pane can create an `InstrumentDefinition` and open it in a
main-workspace tone-table editor. Divisions and offset remain the pitch-to-index
lookup parameters from Core. Tone specifications are edited independently from
the tone table: each reusable specification selects any live sound-producing
song object (sample, instrument, pattern or sequence), composes a positive pitch
multiplier, and can independently override volume, pitch, panning and filter
envelope references. The tone table then maps each integer tone index to a
specification or to `-1` for silence. Removing a specification deliberately
silences entries that referenced it and decrements later specification indices
so remaining mappings continue to identify the same definitions. The editor
projects existing envelope objects, including unresolved references via
tombstone/raw-ID fallback.

ADSR envelopes are first-class objects in the **Envelopes** pane. Its
editor now combines numeric fields with the **graphical ADSR editor**:
drag Attack, Decay or Release endpoint handles (separately identifiable
even when segment durations are zero) or the horizontal Sustain line.
The graph shows the right-edge labels **Note Volume**, **Sustain** and
**0**. Attack, decay and release are non-negative physical-time
durations stored as `TimeSpan`; numeric controls show seconds.
The graph's 0..1 Y range is a visual reference, not a Core constraint.
Negative or above-unity Sustain values remain valid; the graph clips
them visually, labels the actual scalar, and permits out-of-bounds
pointer dragging to set such values. A completed drag commits through
the same audio-affecting document editor/revision path as numeric
Apply. Both surfaces remain synchronized, and incomplete drags can
be canceled. See [graphical ADSR editing](docs/envelope-graph.md).
The FM synth editor also embeds the **same graphical control** when
one of its Envelope nodes has a live ADSR Envelope selected.
Creating a new FM Envelope node leaves its reference unassigned
(`ObjectId.None`), even if Envelopes already exist. Its dropdown
starts blank, with an italicized **New...** entry first, followed by
**(None)** and existing Envelopes. New... creates a shared Envelope
in the document's Envelopes section and assigns it without
leaving the FM editor. Clearing a reference does not delete that
Envelope. Unassigned FM Envelope nodes serialize as ID 0 and
evaluate to silence; FM import skips their nonexistent dependency.
See [graphical ADSR editing](docs/envelope-graph.md) and the
[UI redesign](docs/ui-editor-redesign.md).

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
backtick enters note off. The tracker note-entry **skip value** starts at 1.
Press Alt+0 through Alt+9 (the physical top-row digits) to choose 0–9 rows
to advance after every recognized note/cut/off entry. Skip 0 leaves the
cursor on the same row; other values move it just as pressing Down that many
times would, clamping at the final row and crossing projected sequence-pattern
boundaries. Chord entry uses the same skip value, and the chosen value is
shown in the pattern toolbar and retained when switching standalone patterns.
With Caps Lock *not* pressed, physical key auto-repeat continues inserting
notes in successive rows using this skip; with Caps Lock pressed, repeats
remain preview-only. Replacing an existing start note changes its pitch
but preserves its playback-speed multiplier and mixdown flag; Enter remains
available for the detailed semantic note dialog.

Source is a separate tracker column and may be omitted. An explicit Source value
updates the remembered source for that mapped physical channel even on a row
without a note. A Start note whose Source field is empty uses that remembered
source; if the channel has never had a source, the note remains valid pattern
data but generates no playback start. Sequencing source memory survives later
pattern invocations in the same sequencing context, while mixdown children
naturally receive independent memory with their independent channel-state map.
During song compilation, Source selections are resolved as each row executes,
not precomputed for the entire pattern. A flattened Pattern or Sequence may
therefore change the mapped channel's remembered source before a later parent
row, even when the child merely selects a source without starting a note.
Source changes in skipped rows do not execute; standalone raw-note generator
callers retain their previous eager resolution behavior.
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
and Enter collapses in place. Native effects occupy one whole-tab keyboard stop
and ignore direct tracker typing. Enter on a selected native effect, double-
clicking its tab, or choosing **Edit Parameters...** from that tab's context menu
all open the same generic native-effect parameter dialog. The dialog is driven
by a framework-independent semantic edit model rather than effect-specific
Avalonia code: all current native effect shapes project named fields, reconstruct
the same concrete Core effect type, and rely on the Core constructors for value
validation. Applying a change replaces the selected stack member through the
same stack-editing command path used by the tracker UI.

The same dialog also creates native effects. Creation adds a type selector backed
by the native-effect catalog, seeds a valid default instance for the selected
type, then renders exactly the same semantic parameter fields used for editing.
All eleven current native effect shapes are available. `Alt+N` inserts a native
effect before the selected stack member (or into an empty effect column), while
`Alt+Shift+N` inserts after it. A tab's context menu exposes **Insert Native
Effect Before...** and **Insert Native Effect After...**; an empty effect strip
offers **Insert Native Effect...**. Native insertion is audio-affecting and
selects the inserted native tab immediately, including when it is the only effect
in the cell. The sequence-integrated tracker uses the same mapped cursor path,
so insertion edits the underlying shared pattern rather than sequence-local data.

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
also be dragged to reorder them. A tab's context menu labels tracker insertion
explicitly as **Insert Tracker Slot Before/After**, alongside native insertion
and **Delete**; all operations route through the same framework-independent
stack-editing commands.

Effect copy/paste treats the complete ordered effect list of one pattern cell as
the clipboard unit; note, Source and Volume data are never included or replaced.
`Ctrl+C` (or the platform Meta modifier) while the tracker cursor is in either
effect field copies the stack, and `Ctrl+V` replaces only the destination
cell's effects. The same commands are available as **Copy Effect Stack** and
**Paste Effect Stack** in effect-strip context menus; an empty strip still
offers paste so a copied stack can be placed into a blank cell. The clipboard
payload is plain text with a Heresy pattern-effects/version header followed by
the canonical polymorphic `PatternEffect` JSON representation used by document
persistence. Mixed native/tracker stacks, ordering and inert `...` parameters
therefore round-trip without a parallel clipboard schema. Copying an empty stack
and pasting it clears destination effects while preserving any note, Source or
Volume in that cell. A stack replacement is a no-op when values are identical;
changes involving only inert tracker placeholders advance document revision but
not audio revision. In sequence context, paste resolves through the same mapped
cursor and modifies the underlying shared pattern object.
