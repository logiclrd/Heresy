# Pattern editor — source tints, flattened-range shadows and overlap diagnostics

**Status:** Design and TODO capture, 13 October 2026. **Not implemented.** See [todo.md](todo.md) for separately closable tasks. The feature visualizes already-existing recursive source semantics without changing their scheduling, rendering or new-note actions.

## Source colour ownership and persistence

Every playable source (Sample, Synth, Instrument, Pattern, Sequence) has a stable **per-source** colour, not a per-note randomized choice. For a newly created source or a loaded legacy source lacking the field, independently draw a random Hue uniformly in HSV, use **S = 75%**, **V = 100%**, convert to RGB and set **A = 20%** (~51/255). Persist the colour as a stable value in the song's current v1 representation. A loaded legacy object receives an in-memory value at **load time**; the next save persists that assignment. Opening the editor, recreating playback snapshots or returning from a different Pattern must not create a fresh colour. Import/copy semantics should preserve or intentionally reassign identity in a deterministic, tested way. Colour is **visual metadata**, not a Filter, amplitude or other sound parameter. Store a platform-neutral value in Core; convert to Avalonia brushes only in UI.

## Note-cell highlight

Render a translucent highlight for a note start based on its **effective resolved source**. A Pattern cell may have separate note and Source columns, and source selection can be inherited; the tint must follow the source that actually plays. Leave tracker note text, note focus border, selection and effect overlays visible. A source-less, invalid or non-start note must not appear to have a valid source colour.

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
