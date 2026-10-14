# Heresy documentation index

Keep three categories distinct: **open tasks**, **implemented contracts**,
and **historical engineering records**. Production code and regression
tests supersede contradictory old prose.

## Start here

- [Open work](todo.md): outstanding physical desktop/listening acceptance,
  conditional performance/progress improvements, and deferred schema migration.
  **Only unchecked, actionable items appear in that file.**
- [Root README](../README.md): current architecture, projects and UI.
- [Recursive sounds](recursive-sounds.md): **current production contract**
  first; early eager/prototype designs are historical below it.
- [Incremental sequencing](incremental-sequencing.md): detailed
  chronological engineering log with later corrections and regression
  milestones. **Not a list of unfinished work.**

## Original-design programmability

- [Script programmability](script-programmability.md): **implemented**
  Pattern virtual channels, script-defined Instruments and Envelopes,
  and compiled native parametric effect expressions. Their supported
  parameter ranges, restricted-C# contracts and regression tests are
  documented here; unrelated outstanding desktop validation and conditional
  work are listed in [open work](todo.md).

## Current technical references

| Contract | Document |
| --- | --- |
| Samples, encoded-asset ownership and recovery | [Sample storage](sample-storage.md) |
| Tracker/native operators and timing | [Effect operators](effect-operators.md) |
| Reusable FIR/IIR filter catalogue, runtime/authoring contracts and historical integration milestones | [Filter design](filter-design.md) |
| Realtime processing limits and measured filter qualification | [Filter realtime qualification](filter-realtime-qualification.md) |
| Synth graph recursive ISound sources and implementation chronology | [Synth graph sound sources](synth-sound-source.md) |
| Physical Filter audition status and outstanding Windows/Linux acceptance cases | [Filter manual acceptance](filter-physical-acceptance.md) |
| Native seek semantics and replay warnings | [Source-seek hints](source-seek-hints.md) |
| PCM ring, speakers, device settings, filters | [Audio output](audio-output.md) |
| Export, formats, progress, cancellation and parity | [Export workflow](export-workflow.md) |
| Live song vs. frozen playback revisions | [Playback snapshots](playback-snapshots.md) |
| Desktop splash, icon and maximize state | [Application branding](application-branding.md) |

## Authoring/editor references

| Contract | Document |
| --- | --- |
| Comprehensive UI and keyboard behavior (formerly in root README) | [UI workflows](user-interface-workflows.md) |
| Implemented source tints, finite flattened-source overlays and overlap diagnostics | [Pattern source highlighting](pattern-source-highlighting.md) |
| Pattern row Insert/Delete performance, measured Release baseline and localized redraw | [Pattern row mutation performance](pattern-row-mutation-performance.md) |
| October 10 editor redesign **historical snapshot** (ADSR and older pane/grid counts superseded) | [UI editor redesign archive](ui-editor-redesign.md) |
| Six pane roots, legacy four-/five-pane migration | [Document panes](document-panes.md) |
| Implemented node-envelope model, interactive graph and sustain gestures | [Node envelope design](node-envelope-design.md), [Envelope graph](envelope-graph.md) |
| Eleven-column tone grid, chromatic notation and NNA | [Instrument tone grid](instrument-tone-grid-model.md) |

## Documentation maintenance

On completion, **remove the completed task entry entirely**; put durable
implementation contracts in a topical reference and preserve the change
history in Git commits and explicitly historical logs. The TODO contains
open work only. Do not duplicate completion narratives in current indexes
or mislabel superseded design snapshots as active contracts.
