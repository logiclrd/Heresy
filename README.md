# Heresy

Heresy is an experimental tracker architecture in which any sound-producing song
object can be used as a note source, including patterns and sequences.

## Solution layout

The repository is intentionally split by concern.

- `Heresy.Core` — persistent song model, IDs, timing primitives, sequencing
  contracts, patterns, sequences, samples, instruments and script source.
- `Heresy.Render` — abstract PCM generation, playback voices/channels,
  spatialization, sample rendering, effect processing and the common renderer.
- `Heresy.Render.SDL` *(planned)* — SDL3-CS real-time sink and buffering.
- `Heresy.Render.File` *(planned)* — FLAC, WAV and MP3 sinks.
- `Heresy.UserInterface` *(planned)* — Avalonia single-document tracker UI.
- A Roslyn-backed restricted-C# compiler assembly will also be added separately
  once the Core/Render contracts have been exercised.

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
- Data-driven patterns use a mutable row/channel grid whose cells contain a
  semantic note column (start/off/cut) and semantic effects. Tracker-specific
  notation and effect-memory behavior will be layered on top of this grid model.
- Sequences are finite and entries can specify a `StartRow`.
- Script object references are persisted in restricted-C# source as `_O(id)` and
  can later be projected by the editor as atomic named tokens.
- Continuous effects are evaluated at output-sample resolution. Traditional
  tracker ticks are compatibility anchors for timing/amplitude semantics, not
  processing steps: an effect must agree with its tracker counterpart at the
  corresponding tick states while using an appropriate continuous curve between
  them. Discrete operations such as note delay/retrigger remain discrete.
- Native source-frame seeking is a semantic capability, not a performance
  promise. A seekable sound reports either direct or replay-required cost.
  Replay-required seeks remain fully supported and exact; a future editor may
  optionally highlight such rows as potentially expensive for realtime playback
  (with user-configurable suppression) rather than forbidding them. Flattened
  Pattern/Sequence playback need not expose source-frame seeking because it has
  no single cooked PCM timeline, while mixdown forms may expose replay-required
  seeking.

## Toolchain note

Heresy targets **.NET 10.0**. GitHub Actions builds the solution and runs the
full test suite on pushes to `main`.
