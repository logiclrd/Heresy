# Script programmability — original-design completion

**Status (October 10, 2026):** current contract for implemented
Pattern-script virtual channels and script-defined Envelopes, alongside
implemented script Instruments and native parametric effects. This is a **current design and
acceptance contract**, not a historical implementation log. Track
outstanding work in [todo.md](todo.md).

## Shared principles and existing seams

The original Heresy design allows data-defined *and* program-defined
musical objects. The production architecture already provides restricted
Roslyn Pattern/Sequence programs, abstract sound/envelope execution,
recursive/flattened invocation ownership, snapshot-backed playback,
and independently composed parameter operators. Those original-design programmable authoring gaps now have
persisted, tested, restricted compilation and editor surfaces.

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

### Second slice: virtual controls — implemented

Pattern scripts may now invoke the following **typed, direct-statement**
helpers, with one raw `NoteEvent` per call and mandatory yielding from
incremental Roslyn iterators:

```csharp
VolumeVirtual(row, virtualId, volume, timeOffsetSeconds: 0.0);
SourceFrameVirtual(row, virtualId, sourceFrameOffset, timeOffsetSeconds: 0.0);
DisplacementVirtual(row, virtualId, NoteDisplacementAction.Continue,
    timeOffsetSeconds: 0.0);
PastVirtual(row, virtualId, TrackerPastNoteAction.Cut,
    timeOffsetSeconds: 0.0);
```

- **Volume:** Normalized finite [0,1] value via `SetNoteVolumeCommand`.
  It modifies the current voice and is remembered on **that scoped
  virtual channel** for a later note; it does not modify another
  invocation's same-numbered virtual ID.
- **Native offset:** `SourceFrameVirtual` takes a nonnegative `long`
  in **source frames**, not the wall seconds used by the existing
  physical-script `Seek` helper. At the actual event deadline it seeks
  the voice then current *only when that bound sound implements*
  `ISourceFrameSeekableSound`; otherwise it does nothing, as does
  physical Oxx. Direct and replay-required capabilities retain
  identical frame semantics. Nonzero replay-required virtual seeks
  emit the existing rate-limited cost warning under
  `Script virtual native offset`. No decoded buffers or journal are
  cached to emulate seeking.
- **Displacement:** The strongly typed `NoteDisplacementAction`
  accepts `Cut`, `Continue`, `Off`, or `Fade`. It overrides
  the **current** voice's behavior on the next start for the same
  scoped ID; it does not set a permanent policy for replacements.
- **Past voices:** `TrackerPastNoteAction` accepts `Cut`, `Off`,
  or `Fade`. It acts only on displaced NNA voices that originated
  from the same (Pattern-cursor, virtual-ID) pair, never the current
  replacement or a sibling cursor's voices. Release/fade semantics
  remain owned by each displaced voice.

Only these two enums' *named constant fields* are newly permitted
as member accesses by the restricted-C# analyzer; arbitrary
framework/object member access is still rejected. Runtime validation
rejects invalid enum casts, negative native offsets, invalid volume,
rows outside the Pattern, and unrepresentable wall offsets **before**
emission. Positive fixed-wall offsets execute on the voice present
at the actual deadline, not when the script merely yields the event.

These controls target `Virtual(id)` only. The existing playback
broadcast surface supports Note Off/Cut and note volume on eligible
earlier-started voices. **It does not support** native offsets,
displacement policy, past-note actions, tracker channel memory, or
source starts on `AllVirtualInScope` or `AllVirtual`. The
incremental validation layer rejects those raw broadcast commands
instead of deferring a renderer exception; no extra broadcast script
helpers are introduced.

Regression coverage:
- `ScriptVirtualControlTests`: eager versus incremental command
  identity, all action enumerators, argument errors, direct-statement
  restrictions, coroutine yielding and rejected raw target combinations;
- `ScriptVirtualControlPlaybackTests`: actual PCM for scoped volume,
  NNA migration, past-note Cut, native seek applied to a later
  replacement at its real wall deadline, chunk invariance and
  cancellation before a deferred operation;
- `SourceFrameSeekCapabilityTests`: seekable/nonseekable virtual
  sounds, direct and replay-required cost hints and zero-offset behavior.
  Existing physical source-frame seek tests remain unchanged.

### Acceptance and regression matrix

- Restricted-C# analysis/compilation for eager and incremental sources.
- Exact `ChannelTarget`, command payloads, row/wall offsets,
  input validation, lazy emission and stable same-time order.
- Root/sibling flattened invocations with reused IDs and
  `AllVirtualInScope` versus `AllVirtual` isolation.
- Production `PreparedIncrementalPlaybackFactory` PCM, cancel,
  Note Off/Cut and realtime/offline parity in unequal block sizes.
- No altered physical `Note/Off/Cut` or Sequence script semantics.

## 2. Script-defined Envelopes — implemented

`ScriptEnvelopeDefinition` is a persisted first-class
`EnvelopeDefinition` subtype (`type: scriptEnvelope`). It
contains a restricted-C# `Source` **expression**, not a
Pattern-style statement program, and an optional
`TimeSpan? ReleaseDuration`. Both values survive `.hm.json`,
zip-packaged `.hm`, song snapshots and editor revisions without
changing version 1 of the document format.

### Scalar expression contract

Expressions evaluate to `double`, with two numeric inputs:

- `elapsedSeconds`: the total **active** Envelope time since its
  note began, including time before Note Off; disabled Envelope time
  is excluded.
- `releaseSeconds`: exactly `-1.0` before Note Off; after Note
  Off, nonnegative active time since the Note Off event.

For example, `releaseSeconds < 0 ? 0.7 : 0.7 *
Math.Max(0, 1 - releaseSeconds / 0.2)` sustains at 0.7 and
fades for 0.2 active seconds after Note Off. For a guaranteed
finite end, also set `ReleaseDuration` to 200 milliseconds.

The source must be a **single expression** of at most 2,048
characters and at most 256 parsed syntax nodes. The same
restricted-C# analyzer used by Pattern scripts rejects object
creation, statements, framework access, I/O and unapproved
calls. Pure `System.Math` functions/constants and the two
numeric parameters are available. The code is compiled once
using Roslyn in `Heresy.Scripting`; its stateless
`IScriptEnvelopeEvaluator` is called by the
`ScriptEnvelopeCurve` in `Heresy.Render`. No compilation,
scripts with loops or file operations run inside PCM callbacks.

Values are arbitrary **finite scalars**, not clipped to [0,1]:
negative values may invert a volume signal, and pitch or filter
Envelope consumers interpret the scalar in their own units.
Nonfinite results fail deterministically instead of poisoning
the PCM buffer. This is a deliberately bounded **expression**
contract, not unrestricted scripted per-sample C#.

### Release duration and ownership

`ReleaseDuration` is an explicit **enforced** release bound in
the active Envelope domain, not a duration inferred by sampling
the expression. The curve returns 0 after the first frame at
that bound and advertises
`GetEndActiveFrameExclusiveAfterNoteOff` to the existing
`EnvelopePlaybackState` and voice-retirement policy.
A zero-duration release is immediate. A null duration means
the expression has **no promised finite end**; voice retirement
must not be guessed even if individual evaluated frames equal
zero. Existing indefinite-source end-input safeguards apply.

Disabled Envelopes hold their value without consuming active
release time. Note Off while paused records the correct active
origin. Retrospective value queries, split PCM rendering and
snapshot ownership use the same existing Envelope timeline
semantics as ADSR; a Note Off cannot cut a volume Envelope's
finite release tail prematurely.

### Integration and authoring

`PlaybackSnapshotSoundResolver` compiles script Envelopes into
immutable curves while preparing direct sources **before**
producing PCM. Invalid expressions fail playback preparation
with compiler diagnostics; they never create partly initialized
audio sources. Instrument tone Volume/Pitch/Panning/Filter
Envelope assignments and FM `FmEnvelopeNode` lookups reuse
the common `IEnvelopeCurveResolver`, without a separate
rendering path or implicit ADSR conversion.

The Envelopes pane offers **+ Envelope** for graphical ADSR
and **+ Script** for expression Envelopes. The dedicated
`ScriptEnvelopeEditorControl` edits the expression and an
optional release duration in seconds (blank = unbounded);
`EnvelopeDocumentEditor` validates all edits *before*
changing either definition or document revision. Failed edits
preserve the playing snapshot and current authoring document.
The graphical ADSR control is still ADSR-only.

Tests: `ScriptEnvelopeCompilationTests` (restrictions,
pure expression determinism), `ScriptEnvelopeCurveTests`
(boundaries, arbitrary finite values, pause/resume and
nonfinite rejection), `ScriptEnvelopePersistenceTests` (JSON,
`.hm` and isolated snapshots),
`ScriptEnvelopePlaybackTests` (instrument release tails,
PCM chunk invariance, FM output, frozen playback revisions
and compilation failure), and
`EnvelopeDocumentEditorTests` (transactional UI mutation).

## 3. Script-defined Instruments — implemented

A `ScriptInstrumentDefinition` is a first-class persisted
`SongObjectKind.Instrument`, separate from the data-driven
`InstrumentDefinition`. The existing nine-column tone grid,
`Divisions`/`Offset` lookup and round-away-from-zero behavior
remain unchanged.

### Per-invocation selection and pitch

The scripted Instrument has two restricted-C# **expressions**:

- **`Source`** (default `-1`): returns a signed **integer tone
  specification index**, chosen from the Instrument's ordered
  `ToneSpecifications` collection; `-1` selects silence. Any
  other out-of-range index is a descriptive runtime error.
- **`PitchExpression`** (default `1.0`): returns a `double`
  scaling the chosen specification's positive, fixed
  `PitchMultiplier`. The resulting ratio must be positive and finite,
  including after multiplication; otherwise it fails deterministically.

Both receive **`pitchMultiplier`**: the invoked note's positive
frequency ratio relative to C-4 (`1.0` is C-4). Inherited recursive
pitch is sampled at the note boundary for selection as in the
data-driven Instrument path. One expression may choose which of the
declared sounds to invoke while the other may transform the note's
pitch continuously or piecewise as a function of its invocation pitch.

For example, a selector
`pitchMultiplier < 2.0 ? 0 : 1` picks declared tone 0 below one
octave up and tone 1 at or above it; a pitch expression of
`pitchMultiplier < 2.0 ? 1.0 : 0.5` applies a different ratio
depending on the same incoming pitch. This does **not** create two
simultaneous sounds or eagerly expand future note events.

Each `ToneSpecification` holds its stable source `ObjectId`,
base pitch multiplier and optional Volume, Pitch, Panning and Filter
Envelope `ObjectId` overrides. These fields form the actual tone
output of selection. The script does not return an arbitrary object
ID: dynamic lookup is **limited to this declared list**, so
`SongReferenceAnalyzer` discovers every potential source/Envelope
edge, even for branches the script never selects. Tombstone retention,
import remapping, serialization and snapshot isolation therefore
need no opaque dynamic-reference guesses.

### Compilation, binding and source ownership

Both expressions share the bounded single-expression restricted-C#
profile used by script Envelopes: at most 2,048 characters and
256 syntax nodes; no statements, allocations, I/O or unrestricted
framework calls. Approved `Math` operations and the numeric input
are allowed. Compilation occurs once for each expression in
`PlaybackSnapshotSoundResolver.PrepareDirectSources` from the
**frozen playback song**; changing the live source does not mutate
an existing playback plan. Invalid scripts fail preparation with
compiler diagnostics rather than supplying a partially initialized
sound. Individual expression evaluations occur only when invoking
a note, not once per PCM frame.

`InstrumentSound` accepts the same pure tone-selector delegate
used by table Instruments, then composes pitch and resolves the
selected child via `ISound.CreateInvocation`. This preserves the
existing Envelope overlays, sample volume-tail behavior, New Note
Action and inherited configurations. The incremental production
factory uses the same selector to preflight **only the selected
branch** for nested Instrument/Pattern/Sequence sources, and builds
independent private mixdowns for selected recursive tones. Cycles
on the selected path fail explicitly; unselected branches are inert.

### Persistence and desktop authoring

The existing version-one JSON format adds object type
`scriptInstrument`, `source`, `pitchExpression` and
`toneSpecifications`. An older scripted Instrument record lacking
the optional pitch expression loads with neutral multiplier `1.0`.
Both `.hm.json` and consolidated `.hm` retain the definitions;
`SongDocumentSnapshot` freezes them using the normal clone path.

The Instruments pane offers **+ Instrument** (unchanged table grid)
and **+ Script**. `ScriptInstrumentEditorControl` contains both
expressions and a declared tone list with source selection, fixed
pitch ratio and four optional Envelope assignments.
`ScriptInstrumentDocumentEditor` validates both expressions before
committing a revision and reuses the standard tone specification's
live source/Envelope validation. Rejected edits never partially
change the live song or interrupt a playing snapshot.

Regression coverage: `ScriptInstrumentCompilationTests` (bounded
restricted scripts, pitch-aware selection and dynamic scales);
`ScriptInstrumentPersistenceTests` (JSON/`.hm`, declared refs,
legacy default and snapshots);
`ScriptInstrumentPlaybackTests` (direct source selection,
actual resampled pitch, private recursive Pattern binding, inherited
Envelope volume, chunk invariance, selected cycles and bad indices);
`ScriptInstrumentDocumentEditorTests` (transactional authoring
and live reference validation). Existing tone-grid and direct
Instrument tests remain unchanged.

## 4. Authorable compiled parametric effects — implemented

`ParametricPatternEffect` is a first-class native semantic effect
in a data-driven Pattern's effect stack; it persists its
`ParametricEffectParameter`, `TimeSpan Duration`, C# `Expression`,
and `CommitOnExpire`. The same `NativePatternEffectEditorDialog`
now creates, validates and edits it with a destination selector,
duration expressed in seconds, expression field and commit toggle.
The effect participates in normal Pattern clipboard, editing and
version-one JSON/`.hm` serialization with no new schema version.

### Source and supported additive destinations

Expressions are **restricted C# scalar expressions** returning
`double`, with `elapsedSeconds` (wall time since the effect
started) and optional `rowTime` (continuous tracker ticks since
the same event). An expression such as
`-0.2 * Math.Min(elapsedSeconds / 0.5, 1.0)` fades an additive
volume coordinate over half a second. Wall-time duration **never**
means a count of tracker ticks. In particular, tempo changes may
alter `rowTime` but cannot stretch the half-second lifetime.
`rowTime` is a query coordinate, not ownership of the timing map.

The supported `ParametricEffectParameter` values are:

- `GlobalVolume` — playback-session global volume, requiring
  a `ChannelTarget.Global` event;
- `OverallChannelVolume` and `SpatialX` — physical host channel
  volume and spatial-X baseline, respectively;
- `NoteVolume` and `PitchLinearUnits` — the *current voice*,
  following that voice into the NNA displaced pool if necessary.

Voice-specific effects on a flattened invocation with no single
terminal voice are filtered using the same flattened-source effect
policy as other voice-specific commands. The native effect cannot
target the standalone tick-clock `Tempo` coordinate: that would
also require changing the analytic tempo/time map, a separate
architectural task rather than an undocumented side effect.

### Compilation and execution

`ScriptCompiler.CompileParametric` and
`AnalyzeParametricSource` reuse the single-expression restricted
C# contract: maximum 2,048 source characters and 256 syntax nodes,
no arbitrary framework calls, I/O, loops or allocations. The
frozen `SongDocument` is scanned for every data-driven Pattern
parametric expression, including potential private child sources,
and each distinct expression is compiled once in
`PlaybackSnapshotSoundResolver.PrepareDirectSources`. Playback
event preparation only looks up precompiled delegates: missing
preparation fails rather than invoking Roslyn in rendering.

The runtime command `StartParametricEffectCommand` is a transient
semantic NoteCommand and carries the compiled delegate only
**after** snapshot preparation; the persisted Pattern effect
contains *source text*, not executable delegates. The renderer
wraps the function in a `FunctionalRowPlaybackOperator` with an
independent `PlaybackParameterDeltas` contribution; separate
effects do not replace or mutate each other's state.

`ActiveParametricEffect` binds an absolute start frame and an
exclusive end frame derived from the declared wall duration.
At each output frame it evaluates the scalar using the
effect-relative wall time and current continuous tracker-tick
position. At the exact exclusive deadline, the operator evaluates
its final endpoint then either:

- commits its final delta to its own coordinate's clamped baseline,
  once, when `CommitOnExpire` is true; or
- disappears, without modifying baseline, when false.

The renderer rejects a nonfinite function output with a
deterministic `InvalidOperationException` rather than allowing
NaN/infinity into PCM. There is no retroactive arbitrary-script
recovery or silent fallback that might mask malformed projects.
Expired effects do not allocate per-sample delta buffers.

### Voice lifecycle and cancellation

Channel operators live in `PlaybackChannelState`; pitch/note-volume
operators belong to `PlaybackVoice` and continue to affect that
voice if it is migrated by NNA. Global operators live in
`PlaybackSession`. Natural Pattern completion does not
truncate an effect's declared duration. Explicit cancellation
removes that Pattern cursor's unexpired operators **without**
committing their terminal deltas; an unrelated sibling cursor
keeps its own voices/operators. Physical channel volume and
position are resynchronized immediately after removal so a
surviving sibling does not retain a canceled contribution.

All native effect evaluation and PCM generation use the existing
prepared coroutine playback and offline pipeline. There is no
parallel scheduler or journal, no cache of entire future effects,
and no work is added to SDL's callback.

### Regression coverage

- `ParametricExpressionCompilerTests`: inputs, restricted syntax
  and deterministic compilation;
- `ParametricPatternPlaybackTests`: transient/persistent global,
  channel and voice effects, additive overlap, exact frame
  deadlines, dynamic pitch, JSON and frozen playback, invalid
  scripts, chunk invariance and explicit scope cancellation;
- `PlaybackOperatorIntegrationTests`: independent spatial-X
  baseline commit, transient global contribution and renderer
  state restoration;
- `NativePatternEffectEditorTests` and
  `NativePatternEffectInsertionTests`: type creation,
  round-trip editing, invalid-expression rejection and parameter
  control defaults;
- `PlaybackExportPcmParityTests`: mono and 5.1
  realtime/offline expression modulation.

## Recommended sequence

1. Pattern virtual note lifecycle helpers (bounded first slice).
2. Pattern virtual voice-local controls.
3. Script Envelope model/compiler and runtime integration (complete).
4. Script Instrument model/compiler and source binding (complete).
5. Authorable parametric effect expressions (complete).

Steps 3–5 share compilation and snapshotting principles, but
should remain separate deliverables with specific red/green
regressions. The original design required programmable
musical behavior; it did not settle an unrestricted C# runtime
policy or solve arbitrary-script finite-duration proofs.
