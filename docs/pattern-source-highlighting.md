# Pattern editor — source tints, flattened-range shadows and overlap diagnostics

**Status:** Source-colour persistence, effective-source Note-start resolution, whole-cell source tinting, nonserialized bounds caching and **finite flattened Pattern/Sequence rectangle overlays** are implemented as of 13 October 2026. The separate malformed-overlap red warning remains open. See [todo.md](todo.md) for separately closable tasks. The feature visualizes already-existing recursive source semantics without changing their scheduling, rendering or new-note actions.

## Source colour ownership and persistence

Every playable source (Sample, Synth, Instrument, Pattern, Sequence) has a stable **per-source** colour, not a per-note randomized choice. For a newly created source or a loaded legacy source lacking the field, independently draw a random Hue uniformly in HSV, use **S = 75%**, **V = 100%**, convert to RGB and set **A = 20%** (~51/255). Persist the colour as a stable value in the song's current v1 representation. A loaded legacy object receives an in-memory value at **load time**; the next save persists that assignment. Opening the editor, recreating playback snapshots or returning from a different Pattern must not create a fresh colour. Import/copy semantics should preserve or intentionally reassign identity in a deterministic, tested way. Colour is **visual metadata**, not a Filter, amplitude or other sound parameter. Store a platform-neutral value in Core; convert to Avalonia brushes only in UI.

## Note-cell highlight

Render a translucent highlight for a note start based on its **effective resolved source**. A Pattern cell may have separate Note, Source and Volume fields, and source selection can be inherited; the tint must follow the source that actually plays. **The 20%-alpha colour fills the background of the entire logical row/channel grid cell** (behind the Note, Source and Volume subfields), not only the Note field. Effect stickers/overlays are drawn above the cell tint and continue to use **their own independent effect colours**. Keep note/text, cursor, focus, selection, row/beat/playback shades, and effect overlays legible and functional: this is a cell background layer, not a recolouring of child controls or effects. A source-less, invalid or non-start note must not appear to have a valid source colour. The original 13 October Note-field-only implementation is an intermediate milestone, not the intended final paint coverage.

## Cache and flattened range

A Data Pattern keeps a nonserialized cache of the **last row and last channel that contain note events**. The cached indexes are inclusive; a source whose last note is at (row 7, channel 2) has a base visualization footprint of **8 rows × 3 channels**, irrespective of trailing empty rows/columns and trailing effect-only cells. Refresh cached bounds at Pattern data load, when leaving its editor and when switching among Patterns while editing a Sequence; mutations that would make it incorrect must also invalidate or refresh it safely before use. Empty data patterns have a sensible minimum 1×1 indication without inventing musical notes. Keep the cache out of JSON; derived bounds are not state the song author must maintain manually.

When an outer note **flattens** a referenced Pattern/Sequence, extend its source tint into the rectangular host region affected by that merge, starting at the actual outer note's row/channel. For a nested Pattern, use its cached footprint; for a Sequence, derive the finite composite footprint from its Pattern entries and their placement. Clamp the overlay to the **remaining rows and channels** of the receiving Pattern from the starting cell, and respect segment boundaries in a Sequence-oriented editor. A 1×1 flattened source still gets an outline. A private **mixdown** retains the source tint on the note itself but must not depict flattened host-channel occupation.

Use the source's 20%-alpha colour for the interior and an outside edge/border of the same RGB with **twice that alpha** (~40%, or 102/255), without replacing cursor focus borders. The full rectangular shadow is a compositional indicator independent of note data already entered into the underlying cells; painting it must not delete, normalize or conceal those notes.

## Conflicts and priority

If a later note start lies inside a previous note's already depicted region, **the later note's own highlight is exactly `0x80FF0000` (ARGB)** instead of its normal source tint. Keep the previous flattening range and stronger border **present underneath/around that cell**. This marks an existing malformed overlap for the editor user, without reinterpreting renderer precedence. Use deterministic ordering by ascending row, then ascending channel (later row beats earlier; within one row later channel beats earlier), including multiple overlapping nested regions. Clarify any deliberately different playback-level collision precedence separately; a painting rule must not silently change actual note processing.

## Required coverage

- HSV generator: hue range, S = .75, V = 1, A = 20%; stable JSON/package roundtrip; legacy missing-field assignment exactly once; no fresh colours on repeated editor open or snapshot.
- Bounds: note events only, not effects or Source-only cells; add/remove, insert/delete, resize, on-load and on-editor-switch; repeated Pattern occurrences.
- Rectangles: 1×1 outline, upper and right edges, row/channel clipping, sequence entry bounds, missing source, nested source, private mixdown.
- Overlap: occupied shadow with later conflicting note painted red `0x80FF0000`, older flattened outline retained, stable row/column priority, multiple intersections and unchanged song/sound data.
- Avalonia headless render: theme/selection/focus contrast, effects overlay coexistence, per-cell refresh without excessive full-grid redraw.

### Relationship to observed Filter behavior

The 13 October manual test confirmed that a **flattened** nested Pattern's channel Filter persists on the **real host** after the instigator stops, whereas the same child played as a **private mixdown** does not affect later outer-channel notes. This is a backend processing distinction. The requested range shading must represent that distinction without altering it. See [filter-physical-acceptance.md](filter-physical-acceptance.md).

## Implemented source-colour persistence (13 October 2026)

The Core helper `SongSourceColour` uses a cryptographically uniform discrete
HSV hue in the interval [0, 360), with saturation **0.75**, value **1.0**
and alpha **0x33** (51/255, 20%). RGB components are rounded to bytes;
the resulting visual metadata is a platform-neutral `uint` encoded as
**0xAARRGGBB**. `SongObject.SourceColourArgb` is nullable and deliberately
exists only on playable source kinds: Samples, data/script Instruments,
FM Synths, data/script Patterns, and data/script Sequences. Filters and
Envelopes have no source tint. No UI-specific brush or theme colour is
stored in Core.

Each source initializes its tint when constructed. Serializers write an
optional `sourceColourArgb` integer alongside the object's other v1 JSON
fields; existing documents without the field initialize a random tint
during load. The next save writes that assigned value, and all later loads,
playback snapshots and editor openings reuse the exact serialized value.
Malformed alpha values are rejected rather than silently treated as a
different source tint. Neither the audio revision nor document revision
advances as a side effect of load/snapshot colour migration. The source
colour is not read by audio playback or used as a PRNG seed for notes;
it does not participate in any song sound state or routing decisions.

**Copy/import identity policy:** source-copy operations that create a new
ObjectId but represent a copy of an existing musical source **retain
the original source's visual tint**, preserving cross-song visual
recognition. A new, independently authored sound source draws a fresh
hue. `SampleDefinition.CreateImportedCopy` preserves the tint;
`FmSynthDocumentEditor.CloneSynth` preserves it for simple and recursive
graph imports; recursively imported data and script Instruments also
preserve it. Imports retain their existing normal song/document audio
revision changes for new objects, with no extra audio revision merely
for copying colour metadata. No ID is reused or derived from the colour.

Core tests in `SongSourceColourTests` verify primary and secondary HSV
hues, alpha, saturation/value across randomized samples, playable versus
non-source kinds, full v1 JSON and packed `.hm` round trips, legacy absent
fields assigned once and then persisted, immutable snapshot preservation,
non-audio revision behavior, and invalid fields. The UI import regressions
cover both standalone and nested graph imports with fresh ObjectIds
while preserving the source, Instrument and Sample tints.

**Not part of this milestone:** attaching a translucent brush to the
Pattern editor's effective note-start Source cells, theme-dependent brush
construction, cached flattened bounds, range shadows, or red overlap
indicators. Those remain the following independent TODOs.

## Implemented — base effective-source resolution and initial Note-field painting (13 October 2026)

`PatternSourceTintResolver.Analyze(document, context)` projects **the
persisted `SongObject.SourceColourArgb` of the effective note-start
source**, indexed by displayed Pattern row and channel. It uses the
same `PatternSourceSelection.ResolveSourceSelection` precedence shared
with existing flattened-source effect warnings: explicit Source-column
value first, inline `StartPatternNote.SourceId` second, then the
channel's remembered Source from preceding displayed rows. A Source
selection without a note still updates memory. Only actual
`StartPatternNote` cells with a live playable-source ID receive a tint:
`PatternNoteOff`, `PatternNoteCut`, effect-only, volume-only,
source-only, invalid and tombstoned sources are not coloured.

For a Sequence editor, the projection processes its known, editable
data-Pattern segments in display order, so two occurrences of the
same Pattern can inherit *different* preceding Sources without
changing that Pattern's stored cells. It does not invent Source
memory across Script/unavailable segments or following Bxx/Cxx
flow-control transfers: those reset the known projection until the
next explicit selection. A source inherited from an unseen caller
or skipped prefix is treated as unknown, not guessed from object IDs.
The analyzer does not change note routing, flow, playback or persisted
song data.

**Interim implementation before the whole-cell correction:**
`PatternEditorControl` draws a `SolidColorBrush` representing the
existing 20%-alpha ARGB tint **only behind the Note text** within the
Note-field `Border`. Its parent cell still renders the selected,
playback-follow or minor/major beat row shade; Source, Volume and the
interactive right-hand effect stickers are untouched. Note cursor
and outer selection borders remain on top. Brushes are cached by
their portable ARGB values and produced in Avalonia only at the
presentation boundary.

On an ordinary cell edit, the renderer recomputes the lightweight
visual Source-memory projection and updates **only Note fields whose
resolved tint changed**; it never reconstructs the grid just to
replace a brush. This covers a Source-only edit whose effect is
felt by later note starts, and a reused Pattern visible in several
Sequence order occurrences. Full grid/navigation refreshes project
the current context; no audio or document revision is incremented by
visual refresh. Alpha compositing is independent of the light/dark
theme, with the row background still visible through it.

`PatternSourceTintTests` covers direct/source-only and inline starts,
per-channel memory, OFF/CUT/FX omissions, Source-column precedence,
multi-occurrence Sequence inheritance, scripted/missing orders,
uncertain flow, deleted references, and headless Avalonia field
alpha, refresh, focus/row layering and revision invariance. The
**flattened source range/cache/overlap** implementation is explicitly
not part of this milestone.

## Implemented — whole-cell paint-area correction (13 October 2026)

The user clarified that effective-source colour must tint the **entire logical
tracker cell** (Note, Source, Volume and the edge under effect stickers),
not only the Note field. The earlier Note-field-only milestone below remains
documented as an intermediate implementation; its **source resolution** and
Sequence memory rules were correct and remain in use unchanged.

`PatternEditorControl.BuildCell` now puts the persisted 20%-alpha source
brush on the **inner `Grid` content background**. The outer cell `Border`
continues to own row/beat/selection/playback-follow background and the
cursor outline, beneath the translucent tint. The Note, Source and Volume
field Borders remain transparent so the tint covers those areas without
obscuring their text, focus indicators, text hit testing, or edit controls.
The existing `PatternEffectStripControl` is painted last on this Grid;
individual effect tabs keep their independent opaque colours from the
effect palette, even while their surrounding cell background is tinted.
No note tint is propagated into any effect sticker's own background.

A separate `_cellTintLayers` dictionary maps displayed (row, channel)
coordinates to the live inner Grids. `RefreshSourceTints` analyzes the
unchanged `PatternSourceTintResolver` projection and changes only those
Grid backgrounds whose effective source colour was added, removed or
changed. It does not replace cell controls or brushes that already match;
the parent shade and child focus/sticker state are not modified.
Repeated Sequence occurrences of one underlying Pattern retain distinct
inherited hues and update only when their respective effective Source
memory changes. OFF, CUT and cells without valid Note starts stay untinted.
This is presentation-only: no song or audio revisions, note routing,
flatten/mixdown semantics or persisted schema changes.

Headless Avalonia tests in `PatternSourceTintTests` cover full cell
coverage, transparent field backgrounds, retained row shade and focus,
opaque independent effect stickers, in-place upstream source changes
and multiple occurrences of one Pattern in a Sequence. The preceding bounds-cache milestone and subsequent finite range overlay are implemented. The remaining independent visual task is malformed-overlap diagnostics.

## Implemented — finite flattened Pattern/Sequence range shading (13 October 2026)

`FlattenedSourceRangeProjection.Analyze(document, context)` is a pure,
nonserialized editor projection. It uses the shared effective note-source
resolver (`PatternSourceTintResolver.ResolveNoteStarts`) rather than
reimplementing Source-column/inline/inherited precedence. For each real
`StartPatternNote` with `Mixdown == false` and a live colour-bearing data
Pattern or data Sequence source, it produces a rectangle in **display-row
and host-channel coordinates**. A private mixdown gets only its ordinary
whole-cell Note-start tint. Script/unknown sources are not given invented
finite bounds.

Data Pattern footprints read the already implemented, cheaply queried
`PatternNoteEventBounds`: each inclusive final Note-event row/channel is
extended by one for visual dimensions, and empty Patterns retain a 1×1
footprint. For a data Sequence, the projector advances through the
**played row counts** of its constituent data Pattern entries, honoring
each `SequenceEntry.StartRow`. The final row/channel derive from actual
Note events within the played portion of each entry; a suffix skipped by
`StartRow` cannot contribute a trailing note or channel. Unknown,
missing, scripted entries, unsafe flow-control jumps/breaks and
overflowing/unbounded structures suppress the misleading rectangle;
arbitrary script schedules are never eagerly evaluated or estimated.

The origin is the **actual displayed Note start**, not the beginning of
the referenced Pattern. The far sides clamp independently to the
available host Pattern rows and channels, and in a Sequence editor also
to that entry's displayed segment. Thus repeated occurrences of one
Pattern can have different effective Source identities, while shading
never leaks across intervening/unrelated Sequence orders.

For each clipped rectangle, the projection materializes its participating
cells once, each with the source's persisted ARGB **0x33** interior tint
and an exterior-edge-only ARGB **0x66** border (the same RGB and double
alpha). The outer stroke is two pixels wide; a 1×1 range has all four
edges. The origin already has its baseline source tint, so its range
fragment omits duplicate fill while **retaining its stronger outline**.
Multiple simultaneous ranges contribute separate ordered fragments;
their intersections remain visible, pending the later red diagnostic.

`PatternEditorControl` keeps per-cell noninteractive Grid layers
(`_rangeOverlayLayers`) beneath the Note, Source, Volume and effect
controls, above the ordinary row/beat/selection background. Effect
stickers remain opaque and independently palette-coloured; cursor
focus and field borders are never replaced by range borders. The
projection is refreshed after edits and on navigation/rebuild, and
only cells with changed fragment lists reconstruct their overlay
Borders. Range decoration does not rewrite Notes, change playback,
trigger DSP work or change the document/audio revision.

`FlattenedSourceRangeProjectionTests` covers Pattern event extents,
trailing-effects exclusions, both clipping dimensions, 1×1 outlines,
Sequence duration/skip calculations, no invented Script/flow bounds,
distinct repeated host occurrences, live bounds edits, mixdown
negative controls, and headless Avalonia placement/colour/focus tests.

The **next independent milestone** is the pre-specified red
`0x80FF0000` malformed-overlap indication on later Note starts,
while preserving earlier range fills and outlines.
