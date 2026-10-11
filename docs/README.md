# Heresy documentation index

Keep three categories distinct: **open tasks**, **implemented contracts**,
and **historical engineering records**. Production code and regression
tests supersede contradictory old prose.

## Start here

- [Open work](todo.md): original-design programmability milestones
  plus conditional hardware, optimization and schema tasks.
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
  documented here; the [remaining TODOs](todo.md) are conditional.

## Current technical references

| Contract | Document |
| --- | --- |
| Samples, encoded-asset ownership and recovery | [Sample storage](sample-storage.md) |
| Tracker/native operators and timing | [Effect operators](effect-operators.md) |
| Native seek semantics and replay warnings | [Source-seek hints](source-seek-hints.md) |
| PCM ring, speakers, device settings, filters | [Audio output](audio-output.md) |
| Export, formats, progress, cancellation and parity | [Export workflow](export-workflow.md) |
| Live song vs. frozen playback revisions | [Playback snapshots](playback-snapshots.md) |
| Desktop splash, icon and maximize state | [Application branding](application-branding.md) |

## Authoring/editor references

| Contract | Document |
| --- | --- |
| Comprehensive UI and keyboard behavior (formerly in root README) | [UI workflows](user-interface-workflows.md) |
| Implemented editor redesign requirements | [UI editor redesign](ui-editor-redesign.md) |
| Five pane roots, legacy four-pane migration | [Document panes](document-panes.md) |
| Node envelope editor and upcoming graph | [Node envelope design](node-envelope-design.md) |
| Nine-column tone grid, chromatic notation | [Instrument tone grid](instrument-tone-grid-model.md) |

## Documentation maintenance

On completion, **remove the TODO checkbox**; put durable implementation
contracts in a topical reference. Use the chronological log for
red-to-green developments and superseded prototype decisions. Avoid
copying long completion narratives into both a TODO list and README.
