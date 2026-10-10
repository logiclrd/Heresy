# Instrument tone-grid model — implementation milestone

Status: **framework-independent model completed**. The Avalonia
`InstrumentEditorControl` still uses the existing dual
ToneSpecifications/ToneTable editor controls. This milestone does
**not** claim the visual nine-column redesign, note names, direct
cell editing, or new keyboard interactions are complete.

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
and optional Volume, Pitch, Panning and Filter envelope IDs.
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
   same full Source/Pitch/Envelope combination. Editing one member
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
The existing explicit ToneSpecifications editor remains present
until the future UI cutover.

`SongDocumentSnapshot.Create` continues to isolate playback
from subsequent tone-grid edits. The test suite
`InstrumentToneGridModelTests` covers lookup bounds, fractional
Divisions, out-of-range rendering metadata, drafts,
entry-row overwrites, shared mapping copy-on-write,
deduplication, deletion, validation, revision counts and
snapshot isolation.

## Next work

Connect the projected rows and entry-row focus-loss/Delete events
to the Avalonia Instrument Editor, replacing the separate
specification list and tone table. Implement exactly nine columns;
closest musical note +/- decoration; logarithmic pitch offset;
pitch-adjusted closest-note dropdown with its +/-0.3 option range;
and asymmetric selection snapping. See
[ui-editor-redesign.md](ui-editor-redesign.md) and [todo.md](todo.md).
