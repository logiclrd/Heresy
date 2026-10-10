# Script programmability — original-design completion

**Status (October 10, 2026):** scoped architecture for unfinished
script-defined objects and native expression effects, plus the first
Pattern-script virtual-channel slice. This is a **current design and
acceptance contract**, not a historical implementation log. Track
outstanding work in [todo.md](todo.md).

## Shared principles and existing seams

The original Heresy design allows data-defined *and* program-defined
musical objects. The production architecture already provides restricted
Roslyn Pattern/Sequence programs, abstract sound/envelope execution,
recursive/flattened invocation ownership, snapshot-backed playback,
and independently composed parameter operators. What is missing is
persistence, safe compilation and authoring for the remaining
programmable object kinds.

- **One song model:** `SongDocument` stores definitions, IDs and
  references; live edits never mutate an already-created playback
  snapshot. Compilation belongs outside `Heresy.Core` (in
  `Heresy.Scripting` or a dedicated adapter).
- **Determinism and bounded work:** script output for identical
  invocation inputs and seeded state is repeatable. Scripts have
  syntax/semantic restrictions, no arbitrary framework access, and
  execution budgets appropriate to their use. Validation/compilation
  and asset decoding never execute in the SDL callback.
- **Runtime ownership:** sound selection uses `ISound.CreateInvocation`;
  scalar envelopes use immutable `IEnvelopeCurve`; native effect
  programs contribute through the independent operator model. Avoid
  a second renderer, eager event scheduler, or mutable cross-voice
  shared script state.
- **References:** stable `ObjectId` and `_O(id)` references must
  interact with tombstones, sound/envelope resolution and serialization.
  Dynamic references may require conservative retention or an
  explicit discoverable declaration; never silently delete a
  referenced object.
- **Diagnostics and transactions:** invalid scripts receive
  actionable compiler/editor diagnostics, cannot partially mutate a
  live song on failed authoring operations, and do not corrupt a
  concurrently playing snapshot. Runtime errors use bounded
  diagnostics and documented voice/failure policies.

These principles establish *constraints*, not a promise of unrestricted
C# inside per-sample rendering. Extending the allow-list should be a
deliberate, testable design decision.

## 1. Pattern script access to virtual voices

`ChannelTarget` already supports `Virtual(uint id)`,
`AllVirtualInScope` and `AllVirtual`. The production
`PlaybackSession` scopes a virtual ID by the active Pattern cursor
invocation. Repeated numerical IDs in different flattened siblings
remain distinct. Broadcasts act only on voices already started at a
**strictly earlier** resolved note-start time; same-frame starts are
not retroactively targeted. Scoped broadcast excludes unrelated
invocations; global virtual broadcast may reach all scopes.

### First slice: note lifecycle — implemented

These are *direct statement helpers*, matching existing
`Note/Off/Cut` syntax and defaults:

```csharp
NoteVirtual(row, virtualId, sourceId,
    pitchMultiplier: 1.0, playbackSpeedMultiplier: 1.0,
    mixdown: false, volume: null, timeOffsetSeconds: 0.0);
OffVirtual(row, virtualId, timeOffsetSeconds: 0.0);
CutVirtual(row, virtualId, timeOffsetSeconds: 0.0);
OffVirtualInScope(row, timeOffsetSeconds: 0.0);
CutVirtualInScope(row, timeOffsetSeconds: 0.0);
OffAllVirtual(row, timeOffsetSeconds: 0.0);
CutAllVirtual(row, timeOffsetSeconds: 0.0);
```

The ID is a `uint`, **independent of Pattern.ChannelCount**.
A valid ID of zero is not `ObjectId.None`; it is simply a local
virtual-channel key. Values beyond the 32-bit unsigned domain are
compile-time invalid. Start validates the same source, positive
pitch/speed, and optional [0,1] volume as physical `Note`;
every helper validates inclusive row bounds and a representable
fixed-wall offset. Invalid helpers fail before emitting.

Each call creates exactly one raw `NoteEvent` with the corresponding
target and existing `StartNoteCommand`, `NoteOffCommand` or
`NoteCutCommand`. The eager generator and incremental iterator
must match. In the incremental compiler all emission helpers
**must yield exactly one event** before executing the next
statement. Helper calls in expressions or nested invocations are
rejected rather than secretly bypassing suspension.

In the current incremental engine, events may be chronological and
carry positive fixed-wall deadlines. Out-of-order script emissions
remain subject to the existing silent-drop, rate-capped diagnostic
contract. No changes to renderer virtual ownership, NNA or
broadcast eligibility are implied by exposing these helpers.

The `PatternScriptProgram` virtual note-lifecycle helpers are now
implemented, sharing physical `Note` start validation and emitting
the existing Core commands. The incremental restricted-C# compiler
recognizes their direct statements as mandatory yield points and
rejects nested/statement-expression misuse. No changes to the
`PlaybackSession` target/ownership implementation were needed.

Automated coverage: `ScriptVirtualChannelTests` (eager/incremental
equivalence, validation, laziness, Tempo/wall deadlines),
`ScriptVirtualPlaybackTests` (PCM, same-frame scoped/global
broadcast, ID reuse, cancellation, chunk invariance) and
`PlaybackExportPcmParityTests` (production realtime/offline mono
and 5.1 PCM equivalence).

### Second slice: virtual controls

Once note lifecycle works end-to-end, expose **explicitly supported**
voice-local controls: volume, native frame offset, per-current-voice
NNA/displacement and past-voice action where supported. Decide
individual names/argument types from their existing
`NoteCommand` contracts and prove fixed-wall deferral and
same-frame semantics. Do *not* map physical-only channel memory,
global Tempo or tracker effects onto a virtual target by default.
Document any deliberately unsupported combinations clearly.

### Acceptance and regression matrix

- Restricted-C# analysis/compilation for eager and incremental sources.
- Exact `ChannelTarget`, command payloads, row/wall offsets,
  input validation, lazy emission and stable same-time order.
- Root/sibling flattened invocations with reused IDs and
  `AllVirtualInScope` versus `AllVirtual` isolation.
- Production `PreparedIncrementalPlaybackFactory` PCM, cancel,
  Note Off/Cut and realtime/offline parity in unequal block sizes.
- No altered physical `Note/Off/Cut` or Sequence script semantics.

## 2. Script-defined Envelopes — planned architecture

Existing `EnvelopeDefinition`/ADSR storage and
`IEnvelopeCurve.GetValue(activeFrame, sampleRate,
noteOffActiveFrame)` form the extension seam. Add a persisted
script Envelope definition and a compiled, immutable evaluator
with explicitly defined inputs and scalar output. The *active*
frame domain pauses under tracker S77/S78 controls and Note Off
origin is expressed in that domain, so resumed releases must not
consume paused time. A script may return negative or above-unity
finite values, as ADSR currently does.

A script envelope needs a trustworthy
`GetEndActiveFrameExclusiveAfterNoteOff` policy. Define a
provable finite release bound, or report `null` and handle
indefinite release under existing end-of-input resource safeguards.
Do not infer sound retirement from arbitrary samples of the
function or cut a sample before its owning volume envelope tails.
Compilation and immutable state preparation precede rendering;
per-frame evaluation must be bounded and allocation-conscious.

Integrate via `IEnvelopeCurveResolver`, including Instrument tone
overrides, FM Envelope nodes, persistence, named editor navigation,
diagnostics and reference retention. Confirm split-block/seek
determinism, Note Off repetitions, nested release ownership,
pause/resume and replacement-document atomicity.

## 3. Script-defined Instruments — planned architecture

Keep existing data-driven `InstrumentDefinition` and its
nine-column tone grid unchanged. A script-defined Instrument
definition must select **one invocation-local tone** using a
deterministic compiled function of pitch and documented inputs;
that result contains a sound-source ObjectId, a pitch
multiplier, optional Volume/Pitch/Panning/Filter Envelope
overrides, or a silent result. This is not a script that emits
an eagerly materialized Pattern event schedule.

Use `ISound.CreateInvocation` to compose pitch/speed and bind the
selected terminal sound, retaining nested Instrument and
Pattern/Sequence private-source support. Preserve per-note
selection, finite releases and inherited Envelope overlays.
Reject recursive cycles within existing policy. Decide how
dynamic references are discovered/retained (including tombstones)
before claiming complete persistence. The script runs under a
bounded deterministic contract before binding each note; never
run compilation or uncontrolled side effects on the SDL callback.

Complete JSON/`.hm` round-trips, snapshot isolation, failure
diagnostics and dedicated UI editing alongside the tone grid.

## 4. Compiled authorable parametric effects — planned architecture

`IRowPlaybackOperator` and
`FunctionalRowPlaybackOperator` already represent independent,
absolute delta contributions. They do **not** yet provide a
persisted authorable expression language or compiler pipeline.

A native parametric effect should persist its destination,
scope, source expression, lifetime and terminal commit policy.
Compile to a deterministic, bounded function of **wall time**,
optionally also explicit tracker `row_t` for a chosen compatibility
mode. Its output contributes one independent finite delta; it
cannot overwrite another operator's state. Distinguish a
persistent value committed at expiry from a transient value
removed without baseline change. Do not turn native duration
into tracker ticks implicitly.

Validate additive coordinate/parameter compatibility, nonfinite
result and evaluation failure handling, pause/cancel/NNA owner
lifetime, seek reconstruction and split-render invariance.
Integrate a semantic Pattern effect, serialization, authoring
controls, restricted-source compiler and regression PCM tests
without duplicating the existing effect engine.

## Recommended sequence

1. Pattern virtual note lifecycle helpers (bounded first slice).
2. Pattern virtual voice-local controls.
3. Script Envelope model/compiler and runtime integration.
4. Script Instrument model/compiler and source binding.
5. Authorable parametric effect expressions.

Steps 3–5 share compilation and snapshotting principles, but
should remain separate deliverables with specific red/green
regressions. The original design required programmable
musical behavior; it did not settle an unrestricted C# runtime
policy or solve arbitrary-script finite-duration proofs.
