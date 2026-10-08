# Incremental sequencing pivot (October 8, 2026)

## Goal and existing contracts

Heresy's existing data and script Pattern producers generate **raw**
`NoteEvent` objects in Pattern-local row coordinates, with `NoteCommand`
objects describing the authored note/effects. `PatternNoteProcessor` then
resolves musical timing, tracker memory and flattened starts into timed note
events. There is not currently a separate public `PatternNoteEntry`
stream contract: `PatternNoteEntry` is the editor's note-column value
representation. Avoid conflating raw `NoteEvent` with a timed playback event.

The historical `IRawPatternNoteGenerator.GenerateRawNotes(context, output,
out rowCount)` and `INoteSequencer.GenerateNotes(..., out duration)`
require the entire invocation to complete. This makes arbitrarily repeating
sources problematic and has produced duplicate eager/chronological scheduling
logic. The architectural target is **one cooperative, lazy producer per
Pattern/Sequence invocation**, merged by a common scheduler sharing musical
time and sequencing state.

## First executable step: data Pattern producer

`IIncrementalRawPatternNoteGenerator.EnumerateRawSteps(context)` exposes
`IEnumerable<RawPatternStep>`. Each enumerator is invocation-local and can
be suspended after each step. Today `DataPatternDefinition` implements it:

- `RawPatternStep.Emit` contains the **unchanged raw `NoteEvent`** in
  row-local coordinates, with global commands preceding channel commands
  as before. Neither tempo conversion nor voice lifetime occurs here.
- `RawPatternStep.Advance(row)` indicates musical progress without
  executing a command or making a sound. After 100 rows without an emitted
  raw event, data Patterns yield such a step. A final advance communicates
  the Pattern endpoint if not already reached by a progress step.
- Translator work for a row happens as the enumerator advances there.
  In particular, legacy non-deferred Source-column state changes do not
  happen merely because the enumerator was created, and need not happen
  before an intervening progress yield.
- `DataPatternDefinition.GenerateRawNotes` **consumes** this iterator
  through a compatibility bridge and forwards only `Emit` values to the
  receiver. `Advance` is excluded from old NoteSchedules. Existing
  Pattern processor, synchronous script generation, flattening and
  rendering are otherwise unchanged.

The first-step iterator reads the live editable Pattern grid when advanced.
Before installing it into realtime playback, its source must be a document
snapshot whose lifetime spans the iterator. Likewise, the eagerly consuming
legacy bridge still cannot play an infinite Pattern: nothing has claimed
to solve that until the *consumer* changes.

## Second executable step: whole-row incremental processor

`Heresy.Core.Patterns.IncrementalPatternNoteProcessor` is the first
consumer that retains a raw `IEnumerable<RawPatternStep>` iterator instead
of exhausting it up front. Its `TryAdvance(out IncrementalPatternRow)`
method processes **exactly one source row**. Returned events are resolved
`NoteEvent` values with absolute offsets from the current invocation's
origin; the result also records source row, row origin, and duration.
`NextRow` and `Elapsed` track cursor progress. `Dispose()` releases the
source enumerator, and the caller can stop well before the Pattern ends.

Each row's raw events are resolved through the **existing**
`PatternNoteProcessor` as a one-row
`IDeferredSourcePatternGenerator` slice, preserving its tracker command,
effect-memory, Source-column and Tempo/Speed behavior. The cursor enables
`ResolvePatternSourcesAtRowTime` before reading raw steps so **lookahead
does not execute future Source changes**; a row's state changes occur only
when `TryAdvance` is called for that row. Other producers must obey that
lookahead contract before they can be used with this incremental consumer.
Changing the shared state between calls can change the subsequent row's
timing, as opposed to an eagerly prepared Pattern whose later rows have
already been resolved.

This is deliberately **a whole-row proof of the consumer seam**, not yet
a fully correct concurrent scheduler. A row is processed as one unit,
including internal fractional events, tempo ramps, delayed effects,
flattened child starts and end-of-row cleanup. The caller currently cannot
interleave a second cursor's operations in the middle of that row. The
prototype explicitly rejects contexts requiring eager flattened expansion,
rather than silently pretending such combinations are correct. It also
does not yet retain cross-row deferred timing commands. We must graduate
from the one-row slices to resumable *within-row* processing and a single
shared-clock merger before replacing actual playback preparation.

Parity/regression coverage checks ordinary raw command ordering and
row timing against the eager processor, external tempo changes before the
next row, skipped Source/Tempo commands, deferred Source memory in future
rows and disposal of a partially consumed silent Pattern.

## Third executable step: suspended within-row cursor merging

`Heresy.Core.Patterns.IncrementalPatternTimeline` now implements a small
generic shared-tick merger over `IIncrementalRawPatternNoteGenerator`
invocations. It belongs to Core and does not know whether the caller's
raw source is a data Pattern, a script, or a future Sequence iterator.

- `Add(generator, rowCount, context, startRow)` registers an independent
  invocation at the **current shared tick** and returns its invocation ID.
  Its context must share the clock (`SequencingState`) and channel-memory
  map of the timeline. `Cancel(id)` terminates and disposes just that
  invocation. A caller observing a nested `StartNoteCommand` may call
  `Add` with `context.FlattenedChild(...)` at the instant of that start.
  Source resolution and invocation-lifetime policy remain the caller's job.
- `TryStep(out IncrementalPatternTimelineStep)` cooperatively returns
  either one **resolved event at its actual time** or a silent row-boundary
  `Advance`. A progress step is not a playback note.
- The scheduler holds each raw enumerator and buffers raw **commands only
  for its current row**, with no future effect resolution. A cursor's
  outstanding fractional event or row-end has an absolute tracker-tick
  deadline. The merger visits the next due cursor in stable channel-base
  and creation order. The one shared wall-time clock integrates tick
  distances using the *current* tempo. A child invoked midway through
  a parent row can change the shared Tempo before the parent's next
  event without recalculating that event's tick.
- Global standalone Tempo/Speed operations, ordinary Start/Off/Cut and
  data Source selections resolve using `PatternNoteProcessor` on the
  **single due raw event**. Script-style fractional Tempo/Speed executes
  at the beginning of its source row, as specified by the established
  timing semantics. A Speed change made at a row boundary can resize
  its own cursor's captured row, not another cursor's existing row.
- Raw steps must be in **nondecreasing row order**. This is an explicit
  causality contract, not something legacy scripts currently guarantee.
  Negative fixed wall-time offsets, tracker-style tempo ramps,
  virtual targets, advanced tracker commands beyond the supported slide
  families, out-of-order scripts and nested compiler expansion are not
  yet supported by this prototype and fail explicitly. Same-tick and
  per-row cooperation budgets prevent non-advancing raw streams
  from starving the scheduler.

This milestone **does not replace song compilation or realtime/offline
playback**. Whole-row `IncrementalPatternNoteProcessor` and production
`ChronologicalDataPatternScheduler` remain unchanged. Per-event
invocation here is valid only for the admitted simple command subset:
the remaining row-scoped effects, tempo ramps, deferred global timing,
sequential Bxx/Cxx flow, note-action lifetime and mixes will require an
incremental version of the original common processor's richer state machine.

## Fourth executable step: row-scoped effect and wall-time lifetimes

The generic tick scheduler can now retain a narrow set of effects that must
outlive the raw note which invoked them. The **established**
`PatternNoteProcessor` still performs the transformation of tracker
commands and their effect-memory state; the incremental timeline holds
only the resulting pending operations and **executes them at their due
musical or wall time**.

- Direct pitch and note-volume slides, tracker Dxy volume slides and Exx/Fxx
  pitch slides are admitted. Continuous channel-volume (Nxx), global-volume
  (Wxx), and panning (Pxx) slides also use the common processor and their
  existing IT byte-memory semantics. Their matching
  `ClearPitchSlideCommand`, `ClearNoteVolumeSlideCommand`,
  `ClearOverallChannelVolumeSlideCommand`,
  `ClearGlobalVolumeSlideCommand`, and
  `ClearSpatialXSlideCommand` operations are **retained per invocation**
  until that cursor's actual row-end tracker tick. Global Tempo changes
  during the row correctly change the *wall time* of those cleanup actions,
  without changing the row's already-captured musical tick deadline.
  Fine/instant slide variants execute once and do not synthesize cleanup.
- Ordinary physical `StartNote`, `NoteOff`, and `NoteCut` with a
  **positive fixed wall-time offset** enter a deadline queue only when
  their musical origin occurs. Their absolute deadline cannot be moved by
  later Tempo changes; they resolve channel Source memory **at that
  deadline**, not when initially queued. The scheduler integrates their
  wall times alongside musical tick deadlines, even if their originating
  Pattern has already completed. It retains the owning invocation identity
  so `Cancel(id)` can discard those pending notes.
- `Cancel(id)` also discards the cursor's pending row cleanup, without
  altering other cursors. Stable same-time ordering and the existing
  operation budgets remain in effect.

The tests compare slide start/clear commands with the eager processor,
check tracker D00/Nxx/Wxx memory, include parent/child channel state
sharing, verify a child's future Tempo change moves another cursor's
slide cleanup, and verify deadline stability, Source resolution at the
deadline, and cancellation.

**Scope remains intentional.** This is still a row/event state machine
*prototype*, not a replacement for the entire `PatternNoteProcessor`.
Negative global Tempo/Speed offsets, tracker tempo slides and other
advanced tracker effects, fine/whole-row pattern delays, virtual-channel
behavior and note-action migration remain unsupported. A fixed wall-time
offset on a *non-timing* command other than ordinary physical
Note/Off/Cut is rejected, rather than executing early or with incorrect
row-boundary semantics. Other source types must also fulfill the
nondecreasing-row contract before integration with this prototype.
Production song compilation, existing chronological scheduling, realtime
playback and offline export are unchanged.

## Fifth executable step: deferred global Tempo/Speed eligibility

A standalone global `SetTempoCommand` or `SetSpeedCommand` may now have
a **positive** fixed `TimeOffset` in the incremental shared-clock timeline.
It obeys the same boundary semantics as the eager
`PatternNoteProcessor`:

- At the beginning of the source row, ignore the fractional part of
  `RowOffset` and establish an *eligibility wall-clock instant* at
  `rowStart + TimeOffset`. **Neither the source row nor the eligibility
  deadline immediately changes Tempo or Speed**.
- Each cursor retains its own pending commands. At the beginning of
  each **later row belonging to that invocation**, check which requests
  are eligible at that row's *actual* wall time, considering any
  intervening shared Tempo changes. Execute eligible timing operations
  through the existing common `PatternNoteProcessor`, ordered by
  eligibility time and their original emission order.
- Zero-offset global timing events enter this **same row-boundary
  mechanism**, so future offsets and ordinary immediate Tempo/Speed
  share the ordering model. A Speed change updates the issuing cursor's
  newly captured row duration, but not another cursor's already-started
  row. A global Tempo change alters future wall-time integration from
  the actual boundary.
- Requests waiting beyond the final available source row never apply;
  a timing event exactly at `RowCount` has no source row to execute.
  Cancelling an invocation disposes all its pending timing commands.
  Positive wall-time physical Note/Off/Cut remain on the separate
  **exact wall-deadline** queue and are not delayed to row boundaries.

Regression tests compare deferred timing against eager output, cover
Speed without retroactive parent-row resizing, multiple pending Tempo
changes ordered by their eligibility, requests skipped across several
boundaries, fractional timing positions, changes to a cursor's row
wall-time by another cursor, cancellation, and final-row endpoints.
Mixed unsupported command combinations and negative fixed offsets
still fail explicitly.

This is an **experimental incremental timeline**, not production
playback. Timing effects embedded in mixed-command notes, variable-length
tracker rows, and full cross-cursor same-instant priority reconciliation
still require additional parity work before general use.

## Sixth executable step: tracker Txx continuous tempo ramps

The incremental shared-clock prototype now handles an **isolated tracker
`ApplyTrackerTempoCommand`** on a physical channel. The existing common
`PatternNoteProcessor` remains authoritative for tracker Txx memory
(`T00`), whole-byte recall, T20–TFF immediate sets, T0x/T1x slides,
32/255 clamping, and the conversion of a slide into a single
`SetTempoRampCommand`. The incremental wrapper temporarily runs the
existing processor for that one command using the cursor's captured row
Speed, then restores the shared timing state: the eager processor normally
advances the shared Tempo to its *future* endpoint while preparing the
full row, which would violate lazy causality.

The shared clock installs the resulting ramp at the command's **eligible
source-row boundary**. Tempo is linear with respect to *tracker ticks*
throughout the ramp:

`tempo(tick) = startingTempo + (endingTempo - startingTempo) * progress`

The timeline integrates its wall-time duration analytically with
`TrackerTimeMap`, and inverts that same integral if an absolute
wall-time deadline falls *inside* the ramp. As it actually advances,
it updates `SequencingState.Tempo` to the current instantaneous Tempo.
An overlapping child's note, Source change, row boundary, or deferred
physical Note/Off/Cut can therefore occur at the correct wall time even
when another cursor is responsible for the active ramp. A later row
starts using the tempo the shared clock has reached by that boundary.

The first tests establish eager parity for T12, the downward T02
clamp and upward T1F clamp, row-end duration and fractional note
times, a child Txx ramp retiming parent events, exact fixed-wall
deadline inversion during the ramp, T00 memory across rows, immediate
TFA followed by T00 recall, and a positive-offset Txx becoming
eligible at a later row boundary.

**Restrictions remain explicit.** Concurrent or interrupting global
timing operations while a ramp is still active currently throw
`NotSupportedException`: physical-channel ordering of multiple
simultaneous Txx commands and two unrelated cursors contributing
overlapping ramps require a separate priority/arbitration design.
Complex cells containing Txx together with other non-timing commands,
S6x/SEy extended rows, tempo-control retriggering, script-generated
out-of-order raw events and NNA/mixdown effects are not implemented
here. This is **not** yet wired to the production song compiler,
realtime playback, or offline export.

## Proposed next interfaces and migration

1. Expand the now-implemented **within-row shared-tick merger** beyond
   the supported slide families, deferred timing and isolated tracker
   Txx ramps. Extract a fully resumable common processor with complete
   row/event semantics, including priority arbitration for concurrent
   and interrupting Tempo ramps, fine/whole-row pattern delays, pattern
   flow control, complex mixed-command effects, virtual channels and
   zero-time cooperation guards before production adoption.
2. Add **script invocation-local iterators** through Roslyn syntax
   rewriting. The existing loop `Checkpoint()` instrumentation for
   `for`/`while`/`do` is the starting point, but the generated
   `ExecuteScript()` currently returns `void` and its `Note/Off/Cut`
   helpers append to an eager receiver. The transformation must suspend
   and resume without sacrificing diagnostics or deterministic Random().
   A computational cooperation step is distinct from musical `Advance`:
   it yields CPU control but does **not** advance musical time.
3. Introduce **Sequence cursors** that begin Pattern invocations lazily and
   retain their progress. A common clock orders all active cursors by
   musical deadline, with stable same-time tie breaking, and handles
   cancellation and migrated/releasing note lifetimes.
4. Move realtime and offline compilation consumers across behind tests,
   then remove duplicate eager/chronological engines once semantics match.

## Correctness constraints

- **Causality:** looking ahead for the next step must not apply future
  Tempo/Speed, Source or tracker-effect memory to shared state.
- **Ordering:** currently Pattern scripts can call `Note(10,...)` before
  `Note(2,...)`. The new stream cannot automatically assume script
  execution order equals musical order; choose a documented policy or
  safe buffering strategy before migrating scripts.
- **Runaway protection:** a cursor may emit indefinitely if musical time
  advances. An infinite loop yielding cooperation steps but no musical
  progress, or producing infinitely many zero-time events, still needs
  a per-instant work budget and bounded active-cursor growth.
- **Clock semantics:** a progress marker carries a row/tick position, not
  an absolute wall timestamp computed before future tempo changes.
- **Stop/release:** terminating or migrating a nested invocation must
  preserve Note Off, Cut, Continue, Fade, virtual channels and NNA
  contracts. Dispose iterators on true termination, not on every release.
- **Export:** offline finite exports need an explicit end bound; lazy
  enumeration by itself does not determine the end of an infinite song.
- **Parity:** existing compilation paths stay authoritative until a
  streaming integration passes their test suites, including mixed data
  and scripted Sequences and flattened versus mixdown sources.
