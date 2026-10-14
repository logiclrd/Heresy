# Pattern editor row Insert/Delete — Release performance qualification

**Date:** 14 October 2026
**Scope:** native Pattern editor's plain Insert/Delete (current channel)
and Alt+Insert/Delete (all channels). Existing Effect-column Alt+Insert/Delete
remains the separate *effect stack* operation.

## Root cause and measured before/after

The Pattern Grid itself already shifts references in place with stable
dimensions, reuses cell subscriptions, and records one audio revision on a
non-empty edit. The old UI handler nevertheless called `RefreshGrid()`
after every changed row shift. This constructs a fresh Avalonia `Grid`,
every row header, every cell, Note/Source/Volume text, effect sticker/strip,
cursor decoration, source tint and range overlay. It also re-runs the
source-range/Filter-warning projections and resets focus. The
document mutation is not the main contributor to the perceived lag:
**full control creation and layout/materialization is.**

Release CI run
[`38614434155`](https://github.com/logiclrd/Heresy/actions/runs/38614434155)
used an explicit headless benchmark: a populated **128-row × 8-channel**
Pattern, a single-channel Insert/Delete starting at **row 104**, with
two repeats of the **previous** (`PatternDocumentEditor.Insert/DeleteRow`
+ `RefreshGrid()`) path and three repeats of the new
`ApplyRowMutation` path. Captured diagnostic stdout:

| Path | Recorded runs (ms) | Mean (ms) |
| --- | ---: | ---: |
| Previous full-grid rebuild | 1531.033, 1676.081 | **1603.557** |
| Localized update | 12.264, 121.088, 16.599 | **49.984** |

**Observed speedup: 32.08×.** Only **24 of 1,024 existing cell
controls** were repainted; none were reconstructed. The localized run's
121-ms high outlier illustrates CI noise: the test reports actual timings
without a brittle wall-clock performance threshold. The deterministic
regression contract instead asserts the number of repainted cells,
identity preservation for existing controls, and exact shifted cell data.
These figures are a single Release runner observation, **not a guaranteed
interactive latency, sustained throughput, or a Windows/Linux device
measurement**.

## Implementation

`PatternEditorControl.ApplyRowMutation` times the data shift and four
distinct stages, producing a transient `PatternRowMutationTiming`:
`DocumentMutation`, `CellRefresh`, `Decorations`,
`CursorAndFocus`, and `Total`, plus `CellsRepainted` and
`RealizedCells`. Timing and perf data are never stored in the song.

On a changed mutation, the editor:

1. Mutates the live `PatternGrid` exactly once (existing implementation
   and audio revision rules) and invalidates an expanded effect selection
   that could reference a moved/discarded cell.
2. For each displayed occurrence of the edited Pattern, updates only
   rows at/after the inserted/deleted row, and either the current channel
   or all channels based on the shortcut. Pattern row counts and
   `PatternEditorContext` mappings stay unchanged.
3. Recomputes global annotations *once*, after local text/effect refresh:
   source-memory tints, flattened source ranges, flattened Filter warnings
   and replay-required-seek indications. These can depend on earlier
   source/effect instructions and later/repeated Pattern occurrences.
4. Updates the effect hint and restores focus without walking every
   cell again merely to repaint unchanged cursor borders.

No existing `Grid` or `Border` is recreated. Existing horizontal and
vertical scroll offsets, keyboard focus targets, cursor, selected range,
independent sibling channels, live cell references, effect collections,
source ownership, and source-note subscriptions remain intact. The
source/Filter global analyses still have a cost proportional to the
displayed Pattern; further optimization requires data-flow-specific
invalidation proofs, not stale UI decorations.

**Effect-stack Insert/Delete** already uses `FinishStackMutation` →
`RefreshUnderlyingCell`, without the full `RefreshGrid()` call
diagnosed here. It was not the culprit in this row-shift path and was
not unnecessarily rewritten.

## Automated verification and remaining manual acceptance

Headless Avalonia scale regressions include:

- Populated 96×8 single-channel Insert and Delete preserving unrelated
  channels, shifted cell **object identities**, audio revision count,
  cursor field, old `Grid` instance and unaffected cell controls.
  Exactly **16 cells** are repainted per operation.
- Two occurrences of a reused 8×3 Pattern separated by an unrelated
  5×2 Pattern in a Sequence. An all-channel row Insert correctly shifts
  both occurrences' displayed content without rebuilding either, changes
  the original Pattern once, and leaves the intervening Pattern untouched.
- The Release 128×8 benchmark comparing full rebuild with the localized
  codepath using the **actual editor control**, with source and effect
  stickers populated and explicit instrumentation.

CI: **3,277 tests passed; zero build warnings and errors**. Both the
Release bundle smoke check and Linux ARM64 packaging succeeded.

**Not claimed:** live mouse/key timing on a physical Windows or Linux
desktop, perceived responsiveness with GPU/compositor load, or sound-device
underrun measurements. Those require physical smoke testing and remain
tracked separately in `todo.md`.
