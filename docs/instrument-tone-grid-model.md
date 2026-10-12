# Instrument tone-grid model — implementation milestone

Status: **visual eleven-column grid with independent tone NNA and Filter
selection, per-use Filter override editor, and framework-independent model implemented**. The Avalonia `InstrumentEditorControl` now
projects the same `InstrumentToneGridModel` used by regression tests;
the old dual ToneSpecifications/ToneTable editor controls were removed.
The editor commits populated cell choices as they change; its Pitch
TextBox commits on Enter or loss of focus. The reserved blank entry
row migrates when focus leaves the *whole row*; Delete on a selected
grid row removes its mapped/draft state. A desktop smoke test remains
useful for focus behavior, overflow/virtualization and keyboard
navigation under real window-manager conditions.

## Projection and exact regular range

`InstrumentToneGridProjection.Project` builds a reserved first
entry row with a blank index followed by tone indices in descending
order. The standard range comprises all **nonnegative integer** indices
`i` for which both:

- `i < 10 * Divisions` (strict ten-octave maximum), and
- `i <= Round(7 * Divisions + Offset, AwayFromZero)` (C-11 included).

The renderer's pitch origin is **C-4**, represented by multiplier
`1`. C-11 lies seven octaves higher (multiplier `2^7`);
`InstrumentSound.SelectTone` rounds the continuous index using
`MidpointRounding.AwayFromZero`. Matching that lookup is essential
to avoid a visual/render disagreement. The double-valued
`Divisions` is **never coerced to an integer**: the exclusive
10-octave bound is evaluated as floating-point and its integer
range is expressed with `Ceiling(10 * Divisions)`.

Examples:

| Divisions | Offset | Regular indices | Notes |
|---:|---:|---:|---|
| 12 | 0 | 0–84 inclusive | C-11 has index 84 |
| 48 | 144 | 0–479 inclusive | ten-octave cap excludes C-11 at index 480 |
| 2.5 | 0 | 0–18 inclusive | C-11 continuous index 17.5 rounds to 18 |
| 12 | -100 | *(none)* | C-11 is below index 0 |

Above-range assigned mappings are inserted **between** the top
entry row and standard rows, sorted highest first. Their row
background is the requested ARGB `40FF0000`. Silent, unmapped
ToneTable indices beyond the standard range do not generate empty
extra rows. Existing assignments are not truncated on load or
Divisions changes. For UI responsiveness, the model explicitly
rejects projections or tone-table allocations requiring more than
100,000 rows rather than silently dropping entries.

## Editor-local draft lifecycle

`InstrumentToneGridCells` carries Source, positive pitch multiplier,
optional Volume, Pitch, Panning and Filter envelope IDs, and an optional
independently configured `FilterInstance` (distinct from the legacy
`FilterEnvelopeId`).
New/empty cells use neutral pitch multiplier **1**, never the C#
record-struct default zero.

`InstrumentToneGridModel` is created once per Instrument editor
lifetime. It holds only editor-local draft rows and reserved entry
input separately from the song:

- `Rows` projects the **current Core assignments** plus pending
  draft overrides, including any out-of-range draft index.
- `SetEntryDraft(index, cells)` writes the blank top input.
  `CommitEntry()` migrates its contents to the matching tone index
  only for a populated, nonnegative index, **overwriting** that
  row. It resets the first row for the next insertion.
- `OnDivisionsChanged()` resets the first entry row and recalculates
  the regular range using the current Instrument definition, while
  retaining draft cells for the lifetime of the editor.
- `SetRow(index, cells)` with **no Source** retains the other cells
  only in this editor instance and clears an existing saved mapping;
  an unassigned row has no persisted `ToneSpecification`.
  Setting a valid Source creates/updates the persisted mapping.
- `DeleteRow(index)` removes a mapping and any editor-only draft;
  it is called only for actual indexed rows, not the top entry row.
  Unused out-of-range visual rows then disappear.
- Reconstructing a fresh `InstrumentToneGridModel` from the song
  does **not** resurrect unassigned drafts. Normal refreshes on the
  same instance retain them.

## Core mapping semantics and revisions

Core continues to serialize `InstrumentDefinition.ToneTable`
(integer indices or -1 for silent) plus a list of
`ToneSpecifications`. In the new editor model:

1. Read all mapped tone indices into independent `InstrumentToneGridCells`.
   Reject dangling specification-list indices rather than reassigning
   them silently.
2. Apply a row edit to the resulting index-keyed mapping. A Source
   and envelope selection must reference live song objects; an
   assigned pitch multiplier must be positive and finite.
3. Rebuild compact Core specification/mapping lists transactionally,
   **interning equivalent specs** when more than one row has the
   same full Source/Pitch/Envelope/Filter-configuration combination.
   The Filter identity is its stable shared definition ID plus explicit
   typed parameter overrides, rather than the mutable instance's reference
   or revision number. Editing one member
   of an old shared specification therefore copy-on-writes only
   that row and leaves other mapped rows unchanged. Clearing or
   deleting a row correctly renumbers later references.
4. Mark exactly **one audio-affecting document revision** when
   effective saved mapping data changes. A no-op edit and every
   unassigned draft edit must not dirty the persisted song.
   Failed validation leaves both the song and editor-local drafts
   unchanged. Preserve original table length and extend when
   newly saved high indices require it, with silence in gaps.

Old, unreferenced ToneSpecifications become ineligible for the new
single-grid presentation and are removed as part of the next effective
mapped edit. Simply viewing the instrument does not mutate it.
The Core dual-list representation remains a persistence detail,
not a user-facing second editor.

`SongDocumentSnapshot.Create` continues to isolate playback
from subsequent tone-grid edits. The test suite
`InstrumentToneGridModelTests` covers lookup bounds, fractional
Divisions, out-of-range rendering metadata, drafts,
entry-row overwrites, shared mapping copy-on-write,
deduplication, deletion, validation, revision counts and
snapshot isolation.

## Current UI integration

The ten columns (no-heading index and nominal note, Source, log₂
Pitch, no-heading pitch-adjusted note, Volume/Pitch/Panning/Filter
envelopes, plus **Tone Filter**) are implemented in `InstrumentEditorControl`.
The final column contains the shared Filter-definition selector (including
None) and an adjacent “...” editor for that tone's independent typed
parameter overrides, enabled only when a Filter is selected. This editor
reuses the validated `FilterInstanceEditSession` and embeddable
`FilterInstanceEditorControl`, never modifying the shared Filter's defaults.
The indexed rows are read-only in their index column; the blank
first entry row accepts a new index, Source, Pitch and envelope
values and has an intentionally blank nearest-note column.
All source/envelope dropdowns have clearing entries; the
pitch-adjusted note dropdown has no blank choice.

`InstrumentToneNoteNotation` chooses the nearest standard
12-note chromatic pitch for each row, breaking exact halfway
ties toward the lower note. It counts the difference from that
chromatic pitch's nominal instrument index in **individual steps**
(`+`/`-`), for arbitrary positive finite Divisions and any
Offset. The 48-division illustrative sequence has 47 provided
labels; its final omitted index is `C-2-` (not another B).
`InstrumentToneNoteNotationTests` cover several divisions,
fractional divisions, offset and the complete octave boundary.

Pitch editing uses `log₂(multiplier)`. The adjusted-note selector
offers exact grid pitches within ±0.3 in that logarithmic
coordinate; changing text picks the nearest listed option
**without snapping**, while actively selecting an option
writes its exact multiplier and updates the text. Viewpoint
refreshes do not discard model drafts. Shared specification
copy-on-write, revisions and playback snapshot isolation remain
covered by `InstrumentToneGridModelTests`.

The grid is implemented and covered by automated regressions.
Avalonia's virtualized `ListBox` can temporarily invoke its row template
with a null item while recycling a container during scrolling. The
`InstrumentEditorControl` template returns no control for that null case,
without calling `BuildRow`. A regression invokes the actual data template
with a null item, then with the entry and normal tone rows. A desktop
smoke test is still useful for focus routing, row overflow, scrolling
and keyboard navigation. Log any *observed* defect
as a new scoped item in [todo.md](todo.md).


## Optional tone Filter persistence and snapshot behavior

`ToneSpecification.FilterConfiguration` is nullable, separate from the
existing tracker-specific `FilterEnvelopeId`. An assigned tone references
a live `FilterDefinition` from the same document, and owns a cloned use
with explicit typed stage overrides. Equivalent configurations are interned
when grid rows are saved; changing one shared row is copy-on-write. Changing
a Filter selector starts a fresh default instance, while opening “...” uses
the existing per-use override editor. Unassigned grid rows keep selected
filters as editor-local drafts only and do not dirty persisted audio.

Both data and scripted Instrument tone arrays now round-trip through
`SongDocumentJson`'s typed Filter-instance codec, matching the existing FM
serialization format (`definitionId`, stage IDs/types/generations and
explicit parameter overrides). Older version-one songs without
`filterConfiguration` remain valid and load as bypass; a missing/deleted
Filter reference or tampered overrides fail instead of silently rebinding.
Snapshots restore independent use copies bound to the cloned shared
definition. Reference analysis registers `InstrumentToneFilter` links
for both Instrument kinds. Composed tone Filter chains now render in the dedicated per-invocation
DSP path, inner Instrument first and outer Instrument last, after the
already-selected terminal sound. The editor milestone itself remains
restricted to authoring, while the subsequent renderer milestone
implements the audio processing and preserves the old FilterEnvelopeId
behaviour. Details and regression coverage are in
[filter-design.md](filter-design.md).

## Script Instrument editor is separate

`ScriptInstrumentDefinition` offers compiled expression-based selection
from an explicit list of `ToneSpecifications`, with an optional
pitch-dependent scale expression. It shares the existing sound/Envelope
binding semantics but is **not** represented by the ten-column tone grid:
the Envelopes/pitch-lookup layout described above applies to
data-driven `InstrumentDefinition` only. Script Instrument authoring
uses `ScriptInstrumentEditorControl` to manage expressions and
the declared, stable-ID tone list. Each declared tone also has a
Filter selector and `Edit Filter...` instance override editor; changes
to a selected tone use atomic publication and revision tracking. See [script programmability](script-programmability.md).


## Agreed next-stage interaction specification — 12 October 2026

Hierarchical NNA is now implemented through the authoring views described
below. Rectangular selection, multi-row propagation, revised Pitch display and
row-density adjustments remain **approved but unimplemented** separate items
in [todo.md](todo.md).

### Hierarchical NNA

Both data-table and Script Instruments use one note-start NNA resolver
contract, potentially evaluated by a script with the same input as
the tone selector. Each selected `ToneSpecification` has Inherit as
its default, or an explicit Cut, Continue, Off or Fade value (and a
positive Fade duration). A data-driven Instrument has an outer setting
defaulting to **Use Tone NNA**, or an unconditional action that overrides
all selected tones. Script Instruments expose a compiled method using
their tone-selector input and returning Inherit or a concrete NNA.
An inner source's NNA is the first inherited value; an explicit
selected-tone result overrides it; an explicit outer data-Instrument
choice overrides both. The final policy becomes invocation-local
when the new note begins. Tracker S73–S76 commands independently
modify the **currently playing** note's displacement action.
No implicit NNA changes to existing saved Instruments or playing
notes are permitted.

### Rectangular selection and focus

Selection consists of one anchor cell, one moving end cell and
the inclusive rectangular span they determine; it is not the
virtualized ListBox's row selection. Shift+arrow/Shift+click
expand the region. Ordinary arrow movement or a click **outside**
the region starts a new one-cell region; moving focus **inside**
does not collapse the region. This is true for keyboard, mouse
and Tab; no focus mode may invisibly edit a cell outside the
selection. Text controls retain standard cursor, selection and
Shift+Left/Right behavior while inside the text. Up/Down, or an
attempt to move left/right past the respective text boundary,
transfers to grid navigation using the original modifiers.
Specifically, Shift+Left while caret index is zero sets both
the new anchor and end to the cell to the left. Dropdown navigation
remains native while an option popup is open. Selection/focus is
stored independently of virtualized visual containers.

### Multi-row edits, pitch and density

For a field edit inside a selected rectangle, all selected **rows**
receive the changed value in that field, whether or not other
columns lie in the rectangle. Source-less rows remain drafts until
Source is assigned. One validated batch creates at most one song
revision and is fully atomic, including invalid IDs, pitch and
independent Filter overlay clones. Row deletion and the reserved
entry row are not implied by clearing a field. Pitch edits are
**constant across rows**, not adjacent-index pitch ramps: typing
`5` transposes every selected tone by five divisions relative to
its incoming pitch. The displayed Pitch value becomes
`Divisions * log2(PitchMultiplier)`, rather than bare log₂,
so familiar twelve-division scales admit integer offsets; fractional
values are permitted. Present a value within roughly
`1000 * 2^-52` relative tolerance of an integer as that integer
in the UI (the C# `double.Epsilon` constant is instead the
minimum subnormal, not floating-point machine epsilon).
Keep the actual stored multiplier when rounding display labels.
Rows should occupy minimum practical height with at least 1px
above and below all visible control contents, not simply change
padding while leaving a hard 32px minimum.


## Inherited Instrument NNA contract — 12 October 2026

The shared `InstrumentDefinitionBase.ResolveNna(incomingPitch, selectedTone, preparedScriptSelector)` contract now exists for both `InstrumentDefinition` and `ScriptInstrumentDefinition`. Every `ToneSpecification` has a `InstrumentNnaSetting Nna` with one of **Inherit, Cut, Continue, Off or Fade**, and a positive per-tone duration for Fade. Inherit is the exact default for old saved documents and for programmatically constructed tones. A **data Instrument** adds `NnaOverride` defaulting to **UseToneNna**, with concrete Cut/Continue/Off/Fade options and its own positive `NnaFadeDuration` for the blanket Fade case. A **script Instrument** adds restricted C# `NnaExpression` defaulting to `InstrumentNnaAction.Inherit` and a positive `NnaFadeDuration` for a script result of Fade.

At each note start, tone selection uses the caller's positive finite input pitch; recursive binding obtains its child invocation and its already-captured `NewNotePolicy`. The selected tone may override that policy. The data Instrument's non-default global NNA overrides its selected tone; otherwise **UseToneNna** delegates to it. A scripted NNA expression evaluates with that same original input pitch, not the tone's composed/child-transposed pitch, and overrides the tone unless the script returns Inherit. The final concrete `NewNotePolicy`, Fade duration and full Envelope configuration are captured once in `NoteConfigurationSnapshot`, so a previously started voice cannot change when the song's authoring settings or a later note change. Tracker S73–S76 still override the **current** already-playing voice using the existing runtime command pathway, independently of source defaults.

Both styles and all per-tone values are serialized in the unchanged version-one song schema as optional properties. Old documents lacking them load with Inherit/UseToneNna defaults. Invalid policy enums and malformed/nonpositive Fade durations are rejected at decode or bind rather than silently selecting a different voice action. `InstrumentToneGridCells`, shared-tone interning, and the legacy editor API retain these independent NNA values even when the user changes only Source/Pitch/Envelope/Filter fields; a source-less row holds its NNA in an editor-local draft until the row is published. The Avalonia NNA authoring surface is also implemented. Data Instruments show
an explicit Instrument-wide chooser with **Use Tone NNA** (the default),
Cut/Continue/Off/Fade and a Fade-duration field that is visible when Fade is
selected. Clicking **Apply NNA** validates the positive duration and publishes
action plus duration in one audio revision; an unchanged setting is a no-op.
The virtualized grid adds an **NNA / fade (ms)** column alongside Source,
Pitch and Envelopes. Each row can independently select **Inherit** (default),
Cut, Continue, Off, or Fade, with a positive per-tone Fade duration.
Editing the policy of an assigned row publishes one copy-on-write grid change,
retaining all its Source/Pitch/Envelope/Filter fields, whereas an unassigned
row remains a source-less draft and does not change the saved audio document.
The reserved entry row carries its draft NNA through insertion and publication.
An invalid Fade duration shows a diagnostic without committing a change; the
duration input commits on Enter or focus loss. A change to one of two previously
interned tones does not change the other. The Instrument-wide Apply operation
detects concurrent authoring of its NNA policy and refuses a stale overwrite.

Script Instruments expose their prepared restricted **NNA expression** in a
separate editor field beside their Source/Pitch expressions, with an example
of InstrumentNnaAction.Inherit and pitch-dependent Continue. The separately
configured script Fade duration is in milliseconds; **Apply NNA expression**
validates both fields atomically through SetNnaExpression. Each declared
script tone also has a separate Inherit/Cut/Continue/Off/Fade selector and
Fade duration in the selected-tone details, published together on **Add tone**
or **Update selected**. Script tone updates detect conflicting external NNA
edits. Neither interface compiles Roslyn or resolves arbitrary scripts on a
realtime audio callback.

InstrumentNnaEditorControlTests covers both real Avalonia views, positive
and malformed duration handling, independent tone editing, source-less drafts,
shared specification copy-on-write, scripted expression diagnostics, stale
changes and version-one JSON persistence; previous Core/Script/Playback NNA
regressions continue covering recursive precedence and note snapshot behavior.
Physical-device audible NNA acceptance is intentionally still open.
