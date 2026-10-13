# Heresy

Heresy is an experimental **.NET 10** music tracker where any sound-producing
song object can be a note source—including a Pattern or Sequence. A recursive
source can be **flattened** into the parent's clock and logical voice or
rendered as an independently clocked **private mixdown**.

The desktop application provides an Avalonia tracker UI, restricted-C#
scripting, realtime SDL3 audio, and offline FLAC, WAV and MP3 export.
This page is a current overview, not a development diary. Start with
the [documentation index](docs/README.md) for more detail and
[open work](docs/todo.md) for actual remaining tasks.

## Projects

| Assembly | Role |
| --- | --- |
| `Heresy.Core` | Stable song IDs, objects, samples, FM, Instruments, Envelopes, Patterns, Sequences, effects, timing and persistence |
| `Heresy.Scripting` | Restricted Roslyn compilation and source-reference analysis |
| `Heresy.Render` | Sample/FM rendering, voices, recursive private sources, effects, spatial mixing and filters |
| `Heresy.Playback` | Snapshot-owned incremental recursive music and PCM shared by realtime and export |
| `Heresy.Render.SDL` | SDL device output backed by a bounded PCM ring |
| `Heresy.Render.File` | Offline WAV, FLAC and MP3 sinks |
| `Heresy.UserInterface` | Avalonia authoring workspace, transport, file actions, export and diagnostics |
| `Heresy.Tests.*` | Regression suites for the corresponding assemblies |

The solution is `Heresy.slnx`. GitHub Actions builds and tests on `main`,
including native desktop packaging checks.
Release tags `release-A.B.C` also produce versioned, self-contained
Windows, Linux and macOS downloads with a separate end-user README
and exact source-commit identification. See
[release packaging](docs/release-packaging.md).

## Current playback contract

The production pipeline is `PreparedIncrementalPlaybackFactory` →
`IncrementalRecursiveTimeline` → `PreparedIncrementalAudioSource` →
`PlaybackSession` → PCM.

- Data and restricted-C# scripted Patterns/Sequences are evaluated lazily.
  Scripts may be indefinite; resource budgets and cooperative no-ops bound
  processing. There is no eager whole-song schedule or future-event journal.
- Flattened sources share their ancestor clock while maintaining scoped
  effect memory, virtual channels and one logical instigating note for
  Note Off/Cut/NNA/cancellation. Private recursive sources use independent
  clocks, live parent pitch inheritance and deterministic source-seek replay.
- Musical time includes both row-relative tracker coordinates and fixed
  wall-time offsets; effects apply on their own timing domains. The local
  unity musical pitch is C-4 (`261.6255653005986 Hz`).
- Realtime sequencing, scripts, synthesis and filters execute on a dedicated
  PCM worker. The SDL callback **only drains the ring**, outputting silence
  during underruns. Offline export uses the same incremental engine.
- Output layouts include mono/stereo/5.1/7.1 with configurable speaker
  positions and a single final per-speaker None/LowPass/HighPass filter stage.
  Physical hardware/encoder surround validation remains an
  [open task](docs/todo.md).

Current contracts: [recursive sound sources](docs/recursive-sounds.md),
[effect operators](docs/effect-operators.md), [audio output](docs/audio-output.md),
[offline export](docs/export-workflow.md), and
[source-seek hints](docs/source-seek-hints.md).
The [incremental sequencing log](docs/incremental-sequencing.md) is
**historical**, with later corrections superseding prototype milestones.

## Persistence, samples and recovery

Song objects have stable 32-bit IDs, independent of tree placement;
deleted objects can leave name/kind tombstones. Scripts refer to
objects with `_O(id)`. The persisted schema remains **version 1**.

WAVE, FLAC, MP3, OGG Vorbis, and AIFF/AIFC samples decode at **load/import
time** into immutable in-memory PCM. Realtime playback never reads encoded
files, ZIPs or codecs. Imported sample encoding is retained as song-owned
pending bytes until saved; existing encodings retain path/signature identities.

Projects use `.hm.json` (transparent JSON with path-mode rules) or
`.hm` (ZIP package with a root manifest and encoded assets).
File → Open permits **interactive recovery** of missing/unreadable
assets by locating individual replacements or searching folders for
manifest-hash-verified matches. Every replacement must decode, and the
staged document replaces the existing song **only after all assets succeed**.
Recovered bytes remain song-owned and the project is marked modified
until saved. No realtime source depends on the recovered file.

See [sample storage](docs/sample-storage.md).

## Desktop authoring

Six panes organize the document into **Sequences, Patterns, Patches,
Filters, Envelopes and Instruments**. Patches include Samples and Synths;
PCM-specific actions still say Sample. User-facing actions say **Synth**, while the
existing FM-backed internal object type and persisted song representation remain unchanged. Existing four-pane trees migrate
in memory without a format-version bump.

Implemented UI features include tracker note/volume/source/effect columns,
script editing with semantic object-reference highlighting and diagnostics,
FM graph routing, playback-follow, a graphical ADSR envelope editor
(also embedded in the FM inspector), and the unified nine-column Instrument
tone grid with chromatic-note notation based on actual Divisions.

File → Open chooses a path before the dirty-document prompt. The current
document remains intact if opening or asset recovery fails. The UI displays
audio health and bounded sequencing/export diagnostics.

Detailed editing and keyboard contracts were **moved, not discarded**, from
the former oversized README to [UI workflows](docs/user-interface-workflows.md).
Additional references: [editor redesign](docs/ui-editor-redesign.md),
[document panes](docs/document-panes.md),
[envelope graph](docs/envelope-graph.md),
[instrument tone grid](docs/instrument-tone-grid-model.md) and
[playback snapshots](docs/playback-snapshots.md).

## Source of truth

Use [docs/todo.md](docs/todo.md) for the remaining actionable work and
[docs/README.md](docs/README.md) to navigate production contracts.
The early parts of [incremental-sequencing.md](docs/incremental-sequencing.md)
and [recursive-sounds.md](docs/recursive-sounds.md) preserve intermediate
designs **for history**, not as current limitations. Code and regression
tests are authoritative when documentation conflicts.
