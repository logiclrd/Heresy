# Pattern editor — source tints, flattened-range shadows and overlap diagnostics

**Status:** All planned Pattern source-colour visualization milestones are implemented as of 14 October 2026: persistent source tints, effective-source whole-cell colouring, nonserialized bounds, finite flattened Pattern/Sequence range overlays, malformed-overlap red diagnostics, and correct controlling-channel termination of those overlays. See [todo.md](todo.md) for future independently actionable work. See [todo.md](todo.md) for separately closable tasks. The feature visualizes already-existing recursive source semantics without changing their scheduling, rendering or new-note actions.

## Source colour ownership and persistence

Every playable source (Sample, Synth, Instrument, Pattern, Sequence) has a stable **per-source** colour, not a per-note randomized choice. For a newly created source or a loaded legacy source lacking the field, independently draw a random Hue uniformly in HSV, use **S = 75%**, **V = 100%**, convert to RGB and set **A = 20%** (~51/255). Persist the colour as a stable value in the song's current v1 representation. A loaded legacy object receives an in-memory value at **load time**; the next save persists that assignment. Opening the editor, recreating playback snapshots or returning from a different Pattern must not create a fresh colour. Import/copy semantics should preserve or intentionally reassign identity in a deterministic, tested way. Colour is **visual metadata**, not a Filter, amplitude or other sound parameter. Store a platform-neutral value in Core; convert to Avalonia brushes only in UI.

## Note-cell highlight

Render a translucent highlight for a note start based on its **effective resolved source**. A Pattern cell may have separate Note, Source and Volume fields, and source selection can be inherited; the tint must follow the source that actually plays. **The 20%-alpha colour fills the background of the entire logical row/channel grid cell** (behind the Note, Source and Volume subfields), not only the Note field. Effect stickers/overlays are drawn above the cell tint and continue to use **their own independent effect colours**. Keep note/text, cursor, focus, selection, row/beat/playback shades, and effect overlays legible and functional: this is a cell background layer, not a recolouring of child controls or effects. A source-less, invalid or non-start note must not appear to have a valid source colour. The original 13 October Note-field-only implementation is an intermediate milestone, not the intended final paint coverage.

## Cache and flattened range

A Data Pattern keeps a nonserialized cache of the **last row and last channel that contain note events**. The cached indexes are inclusive; a source whose last note is at (row 7, channel 2) has a base visualization footprint of **8 rows × 3 channels**, irrespective of trailing empty rows/columns and trailing effect-only cells. Refresh cached bounds at Pattern data load, when leaving its editor and when switching among Patterns while editing a Sequence; mutations that would make it incorrect must also invalidate or refresh it safely before use. Empty data patterns have a sensible minimum 1×1 indication without inventing musical notes. Keep the cache out of JSON; derived bounds are not state the song author must maintain manually.

When an outer note **flattens** a referenced Pattern/Sequence, extend its source tint into the rectangular host region affected by that merge, starting at the actual outer note's row/channel. For a nested Pattern, use its cached footprint; for a Sequence, derive the finite composite footprint from its Pattern entries and their placement. Clamp the overlay to the **remaining rows and channels** of the receiving Pattern from the starting cell, and respect segment boundaries in a Sequence-oriented editor. A 1×1 flattened source still gets an outline. A private **mixdown** retains the source tint on the note itself but must not depict flattened host-channel occupation.

Use the source's 20%-alpha colour for the interior and an outside edge/border of the same RGB with **twice that alpha** (~40%, or 102/255), without replacing cursor focus borders. The full rectangular shadow is a compositional indicator independent of note data already entered into the underlying cells; painting it must not delete, normalize or conceal those notes.

## Conflicts and priority

If a later authored Note start lies inside a **still-active** previously projected finite flattened range, **the later note's entire cell background is exactly `0x80FF0000` (ARGB)** instead of its normal source tint. All active preceding range fills and stronger exterior outlines remain present in separate overlays above that background, including under/around the conflicting cell. The diagnostic does not remove the later start's own flattening range when it has one. **A note on the earlier flattened source's controlling channel first terminates that earlier range**, however, so that same earlier source does not cause a spurious red warning. Other live ranges can still cause a valid red warning at the same cell.

Precedence is **ascending displayed row, then ascending channel**: a start only collides with ranges contributed by earlier positions, and never with its own 1×1 origin. Within one row, a larger channel index is later; across rows, the larger row index is later regardless of channel. Multiple still-active earlier ranges may cover the same later Note start; red is still a single exact diagnostic brush and none of those active earlier fills/borders are erased. A range terminated on its original controlling channel is first cropped above that event and cannot contribute either colour or a red conflict below its new bottom edge. A later *authored* `StartPatternNote` is sufficient to warn even if its effective Source is unresolved; that does **not** invent a source tint or imply that the Note will actually play. Source-only, effects-only, Off and Cut cells do not trigger red diagnostics.

This is an **editor visualization of overlapping authoring footprints**, not a mutation of renderer precedence, channel lifetime, Mixdown routing or source selection. It preserves the author's notes/effects, real Source identity and tooltips, selection/focus borders and independent effect-sticker colours.

## Required coverage

- HSV generator: hue range, S = .75, V = 1, A = 20%; stable JSON/package roundtrip; legacy missing-field assignment exactly once; no fresh colours on repeated editor open or snapshot.
- Bounds: note events only, not effects or Source-only cells; add/remove, insert/delete, resize, on-load and on-editor-switch; repeated Pattern occurrences.
- Rectangles: 1×1 outline, upper and right edges, row/channel clipping, sequence entry bounds, missing source, nested source, private mixdown.
- Overlap: occupied **non-controlling** shadow with later conflicting note painted red `0x80FF0000`; source owner Start/Off/Cut crops the old range before that row, retaining a moved bottom edge; concurrent still-active ranges continue to warn; stable row/column priority, multiple intersections and unchanged song/sound data.
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

The subsequent malformed-overlap red diagnostic was implemented on
14 October 2026, as documented below.

## Implemented — malformed-overlap red Note-start diagnostics (14 October 2026)

`FlattenedSourceRangeProjection` now materializes two distinct results
from one **row-major, channel-minor** walk of the displayed Pattern:
(1) the existing ordered independent finite flattened-range fragments,
and (2) `ConflictingNoteStarts`, the set of authored later
`StartPatternNote` addresses already occupied by an *earlier* range
when encountered. Conflict lookup happens **before** appending the
current start's own projected rectangle. An isolated origin (including
a 1×1 footprint) cannot conflict with itself. The scan is deterministic
for same-row/later-column as well as later-row starts, intersecting
several earlier rectangles, and repeated Sequence occurrences.
Uncertain/scripted sources create no guessed range; an authored later
start with an unresolved Source can nevertheless be flagged where a
*known earlier* finite range provides a definite painted conflict.
Non-start events (Off/Cut, Source-only, Volume-only, effects-only) are
never marked.

`ApplyConflictTints` produces a **display-only** copy of the existing
effective-source tint projection, replacing only those conflicting
start addresses with exactly **`0x80FF0000`**. This does not change
`PatternSourceTintResolver.Analyze`, `SongObject.SourceColourArgb`,
inherited/explicit Source precedence, Source-column labels or tooltips.
A later flattened start can be red at its origin and still contribute
its independently colour-coded range. Private mixdown starts can be red
inside a previous shadow but contribute no flattened rectangle.

`PatternEditorControl` builds the current finite-range projection
before constructing the base tint layer. On edit, a **single new
projection** supplies both `RefreshSourceTints(projection)` (only changed
whole-cell backgrounds) and fragment-by-fragment range repainting
(only changed separate outlines/fills). The earlier ranges stay in their
noninteractive layered `Grid`, visibly above the red translucent
background and below text, independently opaque effect stickers and
cursor/focus borders. No authored note is removed or normalized, no
playback/rendering effect is changed, and paint-only refresh never
advances document or audio revision or changes v1 persistence.

`PatternFlattenedOverlapTests` covers exact ARGB colour, stable
row/channel precedence, intersecting multiple older flattened
rectangles, self-origin exclusion, unresolved and mixdown later
starts, non-start exclusions, Source-memory edits and revocation,
different repeated Sequence-order inheritance, and headless Avalonia
compositing, untouched earlier edges/sticker/tooltip identities and
in-place add/remove of the red indication.

The follow-up controlling-channel termination refinement is implemented as described below. Remaining editor tasks are tracked separately in `todo.md`.

## Implemented — stop flattened overlays at controlling-channel end (14 October 2026)

**Playback ownership, not rectangular occupancy, determines when a
flattened shadow ends.** The host `StartPatternNote` has exactly one
controlling channel: the *host channel in which it was authored*.
When a later Start, OFF or CUT is authored in that same channel,
the earlier flattened source's overlay ends at the **end of the
immediately preceding displayed row**. This is an editor-only
projection of the host event's end: playback and audible release
tails retain their existing semantics. A note merely present in one
of the flattened source's *other covered host channels* does not
replace or stop that source. Those non-controlling-channel starts are
still genuine red overlap conflicts when the earlier range is active.

`FlattenedSourceRangeProjection.Analyze` now computes each range's
natural extent from its cached Pattern/data Sequence note-event
bounds, clips it to the current host Pattern/Sequence order segment
and available host channels, **then scans only later displayed rows
of that same segment on the originating controlling channel**.
The first later `StartPatternNote`, `PatternNoteOff` or
`PatternNoteCut` moves the range's inclusive `LastRow` to one
row before that event. Source-only/Volume-only/effect-only changes
never stop its range. If no such event exists, the natural clipped
length is unchanged. Existing rendering logic computes the source
colour fill and doubled-alpha outer perimeter from the revised
rectangle, so its bottom edge moves to the actual termination
row and 1×1 cases still receive all four edges.

Range footprints are shortened **before** building their per-cell
overlay fragments or checking later authored Note starts for red
conflicts. Consequently, the new note in the controlling channel
inherits its own normal source tint (not red from the old source),
while red `0x80FF0000` remains correct if a *different* active
flattened region also covers its cell. Separate sources keep
independently clipped ownership even where their rectangles
overlap. Sequence-entry/StartRow clipping prevents any shadow
spilling into another order or repeated occurrence.

Core projection and headless Avalonia regressions cover a 64-row
drum source replaced in its host channel, OFF and CUT before its
natural end, unchanged Source/FX-only rows, red starts in covered
non-controlling channels, simultaneous active ranges, independently
red replacement under a *different* owner, repeated Sequence
occurrences with `StartRow`, in-place edit refresh, and outline
movement/restoration after deleting the terminating event.
All song data, effects, source-memory attribution, rendering and
audio/document revisions retain their previous behaviour.
