# Script programmability — original-design completion

**Status (November 24, 2026):** current contract for implemented
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
  Both `_O(uint)` and `_O(int)` are supported. Positive signed IDs
  forward to the UInt32 overload; negative signed IDs are converted
  unchecked to non-zero UInt32 values, then follow normal object-ID
  resolution (normally an unresolved reference, not an exception).
  Zero remains invalid. Valid computed lookups
  such as `int id = 137; _O(id)` are ordinary executable C# source;
  only statically recognizable non-zero numeric
  literal calls (such as `_O(137)`) become named visual reference tokens.
  Computed references are not statically enumerable: graph analysis is
  conservative, and **deep duplication refuses** scripts containing them
  rather than silently omitting an edge. Ordinary shallow duplication
  preserves such expressions verbatim. Never silently delete a referenced
  object because its computed ID was invisible to static analysis.
  In the source editor, Backspace from a token's right edge removes its
  final `)` and Delete from its left edge removes its initial `_`,
  revealing ordinary editable text in both cases. Diagnostic navigation
  buttons display literal underscores without accelerator parsing.
- **Diagnostics and transactions:** invalid scripts receive
  actionable compiler/editor diagnostics, cannot partially mutate a
  live song on failed authoring operations, and do not corrupt a
  concurrently playing snapshot. Runtime errors use bounded
  diagnostics and documented voice/failure policies.

These principles establish *constraints*, not a promise of unrestricted
C# inside per-sample rendering. Extending the allow-list should be a
deliberate, testable design decision.

## Managed data in Script Patterns and Script Sequences (issue #58)

Pattern and Sequence scripts accept ordinary **managed strings, interpolated
strings, arrays, `new` object creation on approved runtime types, collection
initializers, `foreach`, and synchronous `using`**. The generated wrappers
import `System.Collections.Generic`, so `List<T>` and
`Dictionary<TKey,TValue>` are available directly:

```csharp
var rows = new List<int> { 0, 2, 4 };
foreach (int row in rows)
    Note(row, 0, _O(35));
```

Scripts can also use **LINQ-to-Objects**, including `Enumerable.Where`,
`Select`, `OrderBy`, `GroupBy`, `ToLookup`, and `Aggregate`, with
inline lambda expressions:

```csharp
var primes = new List<int> { 2, 3, 5, 7, 11 };
var selected = primes.Where(p => p > 4).OrderBy(p => p).ToArray();
foreach (int p in selected)
    Note(p, 0, _O(35));
```

The policy grants **the public types defined in the exact
`System.Linq.dll` assembly**. This is not a permission for arbitrary
assemblies sharing the `System.Linq` namespace:
`System.Linq.Parallel.dll` (PLINQ), `System.Linq.Queryable.dll`, and
`System.Linq.Expressions.dll` remain unapproved. LINQ-to-Objects
predicates and selectors may run on deferred *synchronous* enumeration;
they do not schedule background callbacks themselves. The runtime
verifier audits lambda bodies and signatures, rejecting forbidden APIs
even when the LINQ iterator is never enumerated. Musical note/control
helpers inside lambdas are not supported in **strict streaming mode**,
preserving the direct-statement and incremental-yield contract.
Method-group delegate conversions of approved non-emitting methods
are supported in both modes; relaxed Patterns also permit musical
emitters as delegate targets. Async lambdas and expression trees remain
restricted.
Lambda-local loops receive synchronous work checkpoints, even in
streaming Pattern execution.

C# operations are checked against the exact
[runtime type/member allow-list](script-runtime-allow-list.txt), bound with
Roslyn **before** loop, streaming and F6 trace rewrites. In particular,
allowing `System.IO.Path` doesn't permit `Path.GetTempFileName()`, which
creates a file; filesystem constructors and other selected operations are
explicitly blocked. The compiler checks all original member accesses in
these two contexts, preserving Heresy's narrow musical helper API and
opaque song-object boundary. A `foreach` is checkpointed to retain the
existing execution-work protections.

### Script Pattern ordering modes (issue #52, PR #69)

A Script Pattern can declare its event-generation mode in its own Source:

- `#order strict` (default if omitted): stream raw events in nondecreasing
  musical-row order. Earlier rows emitted after later ones are dropped
  with a bounded diagnostic. Emitters are direct statements; each call
  suspends the incremental iterator immediately. Lambda bodies and
  method-group targets may compute data but must not emit musical events.
- `#order relaxed`: run the invocation synchronously to completion in a
  private raw-event collector, then sort and replay through the same
  scheduler. Emitters may appear inside synchronous lambdas, method-group
  delegates, and other approved expression positions.

The directive is visible source, saved with the Pattern, highlighted as
a preprocessor directive, and recognized before the first C# statement.
Only one directive is accepted; invalid, duplicate, or late directives
produce diagnostics. Other script types do not accept it. Roslyn sees a
space-masked version retaining original UTF-16 offsets, so object-reference
tokens, diagnostics and F6 metadata still refer to the authored source.

```csharp
#order relaxed
for (int channel = 0; channel < 2; channel++)
    Enumerable.Range(0, 8).ToList().ForEach(row =>
        Note(row, channel, _O(35), Semitone(channel * 7)));
```

For this example, use at least eight rows and two channels, and substitute a
valid Source object ID. In relaxed mode, the second channel's early rows
are retained even though the first channel's later rows were generated
first. Deferred LINQ selectors only execute when enumerated. No additional
worker, asynchronous callback permission or per-emitter yield is introduced.

**Scheduling:** sort by raw musical row and original emission ordinal, not
by resolved wall deadline. Same-row emission order is stable within the
collector; existing scheduler arbitration (global timing, channel order,
fixed offsets, concurrent cursors, and source ownership) still applies.
Fractional rows and offsets retain their original values. A note at a
smaller raw row can sound after a later row due to its wall-time offset.
Tempo/speed are applied by the scheduler, not by precomputation. Starting
at a later row uses normal cursor semantics, and repeated visits generate
fresh events. Flattened and private mixdown invocations select their own
ordering modes; ordinary playback and export use the same scheduler.

**Failure and cost:** no raw events are published until generation and
sorting succeed. A thrown exception, runaway guard or observed cancellation
during collection discards that collection, not other already-committed
invocations. This is not a rollback of random draws or all diagnostic
state, nor of errors during subsequent scheduling. Relaxed mode costs
memory proportional to collected events and can delay playback startup;
strict mode retains streaming. The existing 1,000,000-event/expansion-unit
limits, periodic elapsed-time checks and scheduler per-row guards apply.
There are no new arbitrary managed-allocation quotas. Cancellation is
cooperative at execution checkpoints and the collection/sort boundary;
a library operation that does not reach a checkpoint must return first.
The same root stop signal is shared by flattened and private descendants,
and export cancellation reaches the collector. This is application-level
isolation, not an OS sandbox or universal CLR resource bound.

**Randomness:** authored execution order determines Random() draws;
fresh invocations and identical seeded inputs remain repeatable within a
mode. Moving random draws ahead of nested source creation may change their
interleaving compared with strict streaming, so cross-mode equivalence is
not promised for arbitrary scripts using Random() and nested sources.

**F6 provenance:** use the innermost active authored delegate invocation's
full source span. Restore the enclosing span in finally, including after
exceptions. Without an active invocation, use the direct emitter span
inside a callback, or an unambiguous emitter method-group reference.
Multiple references to the same bound emitter retain original delegate
identity rather than changing equality to manufacture fallback provenance.
Ordinary playback receives no delegate tracing instrumentation. A future
opt-in generation diagnostic trace is tracked separately in
[#68](https://github.com/logiclrd/Heresy/issues/68).

**Restrictions unchanged:** the exact bound-symbol API policy applies to
both modes. User-defined functions/types, async or outliving callbacks,
unsafe code, reflection and exception-control constructs remain
unavailable. Instrument, Envelope and parametric scalar expression
contexts remain restricted. The managed-data feature (#58) was merged
separately in PR #67.

## Shared restricted-script pitch conversion helpers (November 2026)

All eight restricted script compiler wrappers now expose the same pure
12-tone-equal-temperament conversions from the one
`Heresy.Scripting.Runtime.ScriptPitchMath` implementation, imported
unqualified into generated script code:

- `Semitone(double semitones)`: positive frequency ratio
  `2^(semitones / 12)`;
- `Cent(double cents)`: ratio `2^(cents / 1200)`;
- `Octave(double octaves)`: ratio `2^octaves`;
- `SemitoneOffset(double pitchMultiplier)`: signed semitone offset
  `12 * log2(pitchMultiplier)`, for a **positive finite** input ratio.

Fractional and negative offsets are supported, without rounding. Inputs
must be finite; output ratios must be positive and finite, including after
any multiplication at the ordinary Note/tone boundary. Invalid values
throw `ArgumentOutOfRangeException`; underflow to zero and overflow
to infinity are not silently clamped. Custom data Instrument `Divisions`
and absolute tuning frequencies are unrelated to these 12-TET ratios.

For Script Patterns (physical and virtual), a note may use
`pitchMultiplier: Semitone(7)`, `Cent(-25)` or
`Octave(-1) * Semitone(4)`; emitting `Note` itself must still be a
direct statement in strict incremental compilation. For Script Instrument
selectors, the incoming `pitchMultiplier` is a relative C4 frequency
ratio, so `SemitoneOffset(pitchMultiplier) >= 12 ? 1 : 0` compares
against the octave boundary. The Script Instrument `PitchExpression`
returns an **additional** multiplier, so `Semitone(-7)` transposes the
selected tone down seven semitones. NNA, Sequence, Envelope and native
parametric expressions can use the same conversions as pure arithmetic.

The compiler allowlists **only these four static method signatures**
from the shared implementation, not the whole class, namespace, or arbitrary
framework API. The compiled script editor reference provides the exact
author-facing signatures. See [the approved numerical API design](script-pitch-helper-design.md).

## 1. Pattern script access to virtual voices

The internal `ChannelTarget` supports generated `VirtualChannelIdentity`
(slot and generation), legacy generation-zero `Virtual(uint id)`,
`AllVirtualInScope` and `AllVirtual`. The production `PlaybackSession`
scopes the full virtual identity by the active Pattern cursor invocation.
Recycled slots in the same invocation and matching local identities in
flattened siblings or independent private renderers remain distinct. Broadcasts act only on voices already started at a
**strictly earlier** resolved note-start time; same-frame starts are
not retroactively targeted. Scoped broadcast excludes unrelated
invocations; global virtual broadcast may reach all scopes.

### Owned virtual note lifecycle

Script callers no longer select numeric IDs. `NoteVirtual` creates an
invocation-local exclusive logical channel, starts its first note, and
returns a disposable `VirtualNote`:

```csharp
using var note = NoteVirtual(0, _O(17),
    pitchMultiplier: 1.0, playbackSpeedMultiplier: 1.0,
    mixdown: false, volume: null, timeOffsetSeconds: 0.0);
note.Note(4, _O(17), Semitone(7));
note.Off(8);
note.Cut(12);
```

A handle is independent of Pattern.ChannelCount. `Note` on the handle
retriggers the same logical channel, not a new lease. Dispose immediately
releases ownership without emitting commands or cancelling already
scheduled events. A recycled slot gets a distinct generation. Aliases
of a disposed handle cannot emit commands, and misuse is warned.
Use a using declaration or block to guarantee cleanup. Unproven
ownership termination produces a compiler warning; invocation-end
cleanup is a safeguard. Nested Pattern/Sequence sources require
`mixdown: true` on each virtual start/retrigger.

### Owning note groups

`NewNoteGroup()` creates an empty disposable `NoteGroup`. Transfer a
handle using `+=`, including a direct `NoteVirtual` initializer:

```csharp
using var chord = NewNoteGroup();
var lead = NoteVirtual(0, _O(17));
chord += lead;
chord += NoteVirtual(0, _O(17), Semitone(4));
chord += NoteVirtual(0, _O(17), Semitone(7));
chord.Volume(4, 0.5);
chord.Off(8);
```

The original handle and every alias become permanently unusable after
transfer; disposing the old wrapper is harmless. The underlying lease
and already scheduled events remain unchanged. Heresy permits the exact
`NoteGroup += VirtualNote` transfer on a using local, including a using
block, without permitting reassignment or ref/out writes to that local.
Invalid transfers warn without revoking valid ownership. A start evaluated
on the right-hand side before an invalid transfer remains scheduled.

A group forwards `Volume`, `Off`, `Cut`, `SourceFrame`, `Displacement`,
`Past`, `NoteFilter` and `ChannelFilter` to its own members with the same
arguments as the handle. There is no group `Note` method and no broadcast
or separate engine channel behind the group. Dispose releases all leases
without stopping music. Strict streaming yields once per member before
resuming authored statements; relaxed mode collects the same fanout.
The compiler warns conservatively (`HRS2101`) about unproven ownership
termination; invalid ownership operations are suppressed with bounded
`HRSEQ011`/`HRSEQ012` diagnostics. Invocation cleanup covers abandoned
leases, including failure or iterator disposal.

Scope-wide and global broadcasts remain distinct helpers:

```csharp
OffVirtualInScope(row, timeOffsetSeconds: 0.0);
CutVirtualInScope(row, timeOffsetSeconds: 0.0);
OffAllVirtual(row, timeOffsetSeconds: 0.0);
CutAllVirtual(row, timeOffsetSeconds: 0.0);
```

Successful calls emit the same Core note commands as before, targeting
the full invocation/slot/generation identity. Strict compilation
suspends after each direct musical statement, single direct local
initializer or direct `group += NoteVirtual(...)` start, and after each
member emission of a group control; suppressed invalid-handle actions
emit nothing.
Relaxed compilation collects and sorts events before playback.
Row bounds and representable fixed-wall offsets are validated; earlier
strict-mode emissions continue to be dropped with bounded diagnostics.

Automated coverage: `ScriptVirtualChannelTests` (eager/incremental
equivalence, validation, laziness, Tempo/wall deadlines),
`ScriptVirtualPlaybackTests` (PCM, same-frame scoped/global
broadcast, ID reuse, cancellation, chunk invariance) and
`PlaybackExportPcmParityTests` (production realtime/offline mono
and 5.1 PCM equivalence).

### Physical channel control parity

The physical Pattern helpers also expose the following typed controls
(PR #72, slice 2a):

```csharp
Volume(row, channel, volume, timeOffsetSeconds: 0.0);
SourceFrame(row, channel, sourceFrameOffset, timeOffsetSeconds: 0.0);
Displacement(row, channel, NoteDisplacementAction.Continue,
    timeOffsetSeconds: 0.0);
Past(row, channel, TrackerPastNoteAction.Cut,
    timeOffsetSeconds: 0.0);
```

They use the Pattern-local physical channel index and the same typed
commands, validation and authored ordering as the virtual equivalents.
Volume is normalized [0,1], remembered on that logical channel, and
controls the live source gain for a flattened Pattern/Sequence.
SourceFrame uses a nonnegative native frame index on the current
seekable sound, distinct from seconds-based Seek. Displacement
overrides the current logical note's next-note action, including the
existing flattened-source displacement policy. Past presently addresses
displaced rendered voices by physical origin and playback owner, never
the current replacement or explicitly virtual origins. The owner-approved
extension to former flattened logical invocations is a separate,
pending slice; it is not yet part of the implementation.
Positive fixed-wall offsets are evaluated at their resolved deadline.
Generated-only F6 overloads are not authored entrypoints.

### Virtual handle controls

The owned handle exposes typed instance controls; successful calls emit one raw event in strict or relaxed mode:

```csharp
note.Volume(row, volume, timeOffsetSeconds: 0.0);
note.SourceFrame(row, sourceFrameOffset, timeOffsetSeconds: 0.0);
note.Displacement(row, NoteDisplacementAction.Continue,
    timeOffsetSeconds: 0.0);
note.Past(row, TrackerPastNoteAction.Cut,
    timeOffsetSeconds: 0.0);
```

- **Volume:** Normalized finite [0,1] value via `SetNoteVolumeCommand`.
  It modifies the current voice and is remembered on **that scoped
  virtual channel** for a later note; it does not modify another
  invocation's channel or a later generation of a recycled slot.
- **Native offset:** `note.SourceFrame` takes a nonnegative `long`
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
  logical channel; it does not set a permanent policy for replacements.
- **Past voices:** `TrackerPastNoteAction` accepts `Cut`, `Off`,
  or `Fade`. It acts only on displaced NNA voices that originated
  from the same (Pattern-cursor, slot, generation) identity, never the current
  replacement or a sibling cursor's voices. Release/fade semantics
  remain owned by each displaced voice.

Only these two enums' *named constant fields* are newly permitted
as member accesses by the restricted-C# analyzer; arbitrary
framework/object member access is still rejected. Runtime validation
rejects invalid enum casts, negative native offsets, invalid volume,
rows outside the Pattern, and unrepresentable wall offsets **before**
emission. Positive fixed-wall offsets execute on the voice present
at the actual deadline, not when the script merely yields the event.

These controls target the handle's generated identity only. The existing playback
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

`note.NoteFilter(row, definitionId = null, timeOffsetSeconds: 0.0)`
selects or clears the current note's event Filter; `note.ChannelFilter`
with the same arguments selects or clears the persistent logical-channel
output Filter. Pass a Filter object ID with `_O(id)`, or null to clear.
These are distinct controls, not the physical resonant `Filter` helper.

A generation's output Filter can outlive its lease: final-use retirement
waits for queued events, current/displaced voices and DSP/anti-click tails.
Private mixdown rewind reconstructs the timeline and renderer together;
no event history or PCM cache is retained to emulate the seek.

### Acceptance and regression matrix

- Restricted-C# analysis/compilation for eager and incremental sources.
- Exact `ChannelTarget`, command payloads, row/wall offsets,
  input validation, lazy emission and stable same-time order.
- Root/sibling flattened invocations with reused IDs and
  `AllVirtualInScope` versus `AllVirtual` isolation.
- Production `PreparedIncrementalPlaybackFactory` PCM, cancel,
  Note Off/Cut and realtime/offline parity in unequal block sizes.
- Exclusive transfer, using-local lowering, strict group suspension,
  bounded misuse diagnostics, full F6 spans and traced/ordinary PCM.
- Long-running lease recycling and bounded renderer state; nested private
  rewind/cancellation with a non-recycled PCM reference, repeated
  invocations and actual realtime/F6/export PCM parity. See the
  [ownership design and acceptance record](script-virtual-note-ownership-design.md).
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
characters and at most 256 parsed syntax nodes. The Envelope-specific
restricted-C# analysis rejects object creation, statements, framework
access, I/O and unapproved calls; unlike Script Patterns and Sequences,
this scalar-expression contract has **not** been broadened. Pure `System.Math` functions/constants and the two
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
snapshot ownership use the same shared Envelope timeline
semantics as node-defined Envelopes; a Note Off cannot cut a volume
Envelope's finite release tail prematurely.

### Integration and authoring

`PlaybackSnapshotSoundResolver` compiles script Envelopes into
immutable curves while preparing direct sources **before**
producing PCM. Invalid expressions fail playback preparation
with compiler diagnostics; they never create partly initialized
audio sources. Instrument tone Volume/Pitch/Panning/Filter
Envelope assignments and FM `FmEnvelopeNode` lookups reuse
the common `IEnvelopeCurveResolver`, without a separate
rendering path or implicit ADSR conversion.

The Envelopes pane offers **+ Envelope** for interactive
node-based Envelopes and **+ Script** for expression Envelopes.
The standalone and FM inspectors share the current
[node graph](envelope-graph.md); Script Envelopes retain the dedicated
`ScriptEnvelopeEditorControl` for editing expressions and an optional
release duration in seconds (blank = unbounded).
`EnvelopeDocumentEditor` validates edits *before* changing a definition
or document revision. Failed edits preserve the playing snapshot and
the current authoring document. No scalar ADSR-only editor remains.

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
`InstrumentDefinition`. The **eleven-column** data Instrument
tone grid, including per-tone NNA and Filter controls, is described
in [instrument tone grid](instrument-tone-grid-model.md). The
`Divisions`/`Offset` lookup and round-away-from-zero semantics
are unchanged by script-defined Instrument support.

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

1. Pattern virtual note lifecycle helpers (complete; owned handles in #54).
2. Pattern virtual voice-local controls (complete; owning groups in #54).
3. Script Envelope model/compiler and runtime integration (complete).
4. Script Instrument model/compiler and source binding (complete).
5. Authorable parametric effect expressions (complete).

Steps 3–5 share compilation and snapshotting principles, but
should remain separate deliverables with specific red/green
regressions. The original design required programmable
musical behavior; it did not settle an unrestricted C# runtime
policy or solve arbitrary-script finite-duration proofs.


## Script Instrument note-start NNA expressions — 12 October 2026

A scripted Instrument can now supply an independent restricted C# `NnaExpression` evaluated from the same positive finite `pitchMultiplier` as its source and pitch expressions. Its compiled `IScriptInstrumentNnaEvaluator.SelectNna(double pitchMultiplier)` returns `InstrumentNnaAction.Inherit`, `.Cut`, `.Continue`, `.Off`, or `.Fade`. For example:

```csharp
pitchMultiplier < 1.5 ? InstrumentNnaAction.Continue : InstrumentNnaAction.Inherit
```

Scripts have no unrestricted object allocation or host framework access: only the five literal `InstrumentNnaAction` enum members were added to the restricted semantic allowlist. The expression is compiled once during playback-source preparation, alongside the tone selector and pitch scaler; no Roslyn compilation occurs in realtime PCM processing. An explicit script result supersedes the selected tone's NNA; Inherit delegates to the selected tone, which can in turn inherit the child source's policy. Fade uses the script Instrument's separately authored positive `NnaFadeDuration`. The resolved policy and duration are captured in the newly created note invocation. Expression and duration are stored as optional version-one Instrument fields; absence means Inherit and a standard positive Fade duration. Invalid expressions fail preparation with diagnostics before sound processing. Script UI editing controls are now implemented: ScriptInstrumentEditorControl
has a distinct NNA expression input, independently authored Fade milliseconds,
an **Apply NNA expression** action that runs restricted compilation analysis
and preserves the song on failure, and Inherit/Cut/Continue/Off/Fade with
per-tone Fade duration in the selected-tone form. Script tone creation and
updates publish this value together with Source, Pitch, Envelopes and Filter;
an Inherit result delegates to the selected tone as described above.
The view refuses stale NNA expression or selected-tone authoring rather than
silently overwriting an external change. Headless Avalonia tests, version-one
JSON round-trips and prior prepared-playback regressions cover these controls
and their non-realtime execution boundary.
