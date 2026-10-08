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
  Negative fixed wall-time offsets, simultaneous/interrupting Tempo ramps,
  virtual targets, advanced tracker commands beyond the supported slide
  families, out-of-order scripts and nested compiler expansion are not
  yet supported by this prototype and fail explicitly. Same-tick and
  per-row cooperation budgets prevent non-advancing raw streams
  from starving the scheduler.

This milestone **does not replace song compilation or realtime/offline
playback**. Whole-row `IncrementalPatternNoteProcessor` and production
`ChronologicalDataPatternScheduler` remain unchanged. Per-event
invocation here is valid only for the admitted simple command subset:
the remaining row-scoped effects, overlapping Tempo ramps, pattern
delays, sequential Bxx/Cxx flow, note-action lifetime and mixes require an
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
Negative global Tempo/Speed offsets, overlapping tracker Tempo slides
and other advanced tracker effects, fine/whole-row pattern delays, virtual-channel
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

The sixth step established isolated physical-channel tracker
`ApplyTrackerTempoCommand` execution with continuous Tempo ramps, using
the common `PatternNoteProcessor` to translate individual effects without
applying their future endpoint prematurely. **The seventh step below has
since replaced that isolated-event adapter with a generalized simultaneous
Txx arbiter.** It resolves T00/whole-byte memory through the existing
`SequencingChannelState` and shares the eager processor's
`ResolveTrackerTempoAtTick` clamp arithmetic for T0x/T1x transitions.
T20–TFF sets and the resulting `SetTempoRampCommand` are emitted only
at the shared clock's actual eligible row boundary.

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

**Remaining restrictions:** Complex cells containing Txx together with
other non-timing commands,
SEy repeating Tempo ramps, tempo-control retriggering, script-generated
out-of-order raw events and NNA/mixdown effects are not implemented
here. This is **not** yet wired to the production song compiler,
realtime playback, or offline export.

## Seventh executable step: concurrent Tempo arbitration

The experimental shared clock now arbitrates **simultaneous Txx commands**
and **interruptions of running Tempo ramps**, rather than rejecting all
overlaps.

- Every cursor beginning a new row at the same tracker tick prepares its
  raw steps before any Txx is interpreted. Standalone global Tempo/Speed
  changes at that boundary execute before the physical-channel tracker
  effects, regardless of cursor creation order.
- The Txx requests ready at that tick are collected together and sorted
  by eligibility time, mapped physical-channel number, invocation order
  and per-invocation emission order. The associated
  `SequencingChannelState` resolves byte-memory `T00` **only when
  the command executes**. The common `PatternNoteProcessor`'s
  `ResolveTrackerTempoAtTick` routine is reused to apply each legacy
  per-tick T0x/T1x adjustment and its clamp in physical-channel order.
  Compatible slide requests combine into **one global ramp**, not
  multiple ramps overwriting each other at the same instant.
- A simultaneous Txx immediate set applies in physical-channel order;
  subsequent slides start from the resulting Tempo. A standalone global
  Tempo command at the same boundary is applied first. A **later**
  Txx request or direct global Tempo set interrupts the old ramp at the
  instantaneous Tempo actually reached by the shared tick clock. It
  never uses the interrupted ramp's future endpoint. A Speed change
  alone does not interrupt the ramp.
- The common analytic `TrackerTimeMap` integration remains in use,
  including inverse tick mapping for exact wall-time events that occur
  during the ramp. Same-time output events are queued in deterministic
  order, and silent/empty Txx memory recalls do not create a ramp.

**Remaining supported-subset boundary:** simultaneous Txx invocations
whose already-captured row Speed differs are rejected explicitly
until competing ramp-span policies are specified. Mixed-command Txx
cells, combined SEy/Txx repeated Tempo ramps, advanced tracker controls, virtual
channels and recursive invocation lifetimes are still outside this
prototype. The old eager Pattern processor remains the production
authority for full tracker-effect semantics, and song compilation,
realtime playback and offline export have not switched to this timeline.

Tests compare same-boundary Txx channel-order clamping directly with
the eager Pattern processor, verify reversed cursor creation order,
multiple simultaneous slides composing into one ramp, Txx immediate
set plus slide, a later slide interrupting from the current Tempo,
direct Tempo interruption and global-boundary priority, and the
non-interruption of a ramp by a Speed-only command.

## Eighth executable step: variable-length S6x/SEy rows

The experimental `IncrementalPatternTimeline` now recognizes a restricted
set of row-delay effects **before** starting the current row:

- `ApplyTrackerFinePatternDelayCommand` (**S6x**) contributes its
  `ExtraTicks` cumulatively across physical channels. Raw events still
  occur at their original fractional position in the base-Speed span;
  additional ticks extend the row's end.
- `ApplyTrackerPatternDelayCommand` (**SEy**) repeats the **effective
  row tick span** `y + 1` times. The lowest mapped physical channel
  wins (source emission order breaks ties). Ordinary notes do not
  retrigger merely because an SEy span repeats.
- Together the row lasts
  `(capturedSpeed + sum(S6x)) * (1 + selectedSEy)` ticks.
  An intervening Tempo change from another active cursor alters the
  row's wall-clock duration, not the stored tick deadline.
- Supported continuous pitch/note-volume/channel-volume/global-volume/
  panning slides and persistent fine adjustments repeat at successive
  SEy span starts, including at the originating fractional offset.
  Existing row-end cleanup occurs at the last extended boundary.
  `PatternNoteProcessor.ApplyRowTickOverride` is shared so S6x
  attaches the original processor's `TicksPerRow` values to resolved
  continuous effects.
- Isolated Txx with S6x generates a longer **single-span** ramp and
  retains the analytic `TrackerTimeMap` integral. SEy combined with
  Txx remains explicitly rejected **before effect-memory changes**
  because the eager engine repeats Tempo processing across spans.

Tests compare supported cases directly with eager `PatternNoteProcessor`,
including cumulative S6x, first-channel SEy selection, combined delays,
Speed changes, fractional effect repetition, row-scoped cleanup and
parent/child Tempo timing.

**Still unsupported:** SEy with Txx, SDx combined with Qxy,
fixed wall-time offsets on SCx/SDx/Qxy, tracker control flow, compound
advanced effects beyond the admitted commands, virtual targets and
full recursive invocation lifetime management.
Production playback and offline export remain unchanged.

## Ninth executable step: tracker SCx/SDx note timing and Qxy retrigger

The incremental shared-clock prototype now also has a **per-invocation
tracker-tick operation queue**. These operations are scheduled only when
their source event is reached, and execute when that cursor's shared
musical tick is due. They are neither eagerly resolved into future wall
times nor confused with an ordinary positive fixed-wall offset.

- **SCx note cut:** the effective tick is `max(1,x)`; a Cut is emitted
  once at that tick, only if `x` fits inside the effective
  `Speed + S6x` row span. SEy does not repeat an SCx cut. Other
  commands in its cell may execute at the original event time.
- **SDx note delay:** the same `max(1,x)` and within-span eligibility
  apply. The **whole ordinary note/setup command list** remains pending
  until its actual delayed tracker tick. Source memory, volume and
  sample-offset transforms therefore resolve atomically at that time,
  rather than starting a note early and applying its properties later.
  With SEy the already-resolved command set is copied to the equivalent
  tick in each repeated span; tracker effect-memory resolution is not
  rerun for every copy.
- **Qxy retrigger:** its whole-byte `Q00` memory resolves through the
  existing mapped `SequencingChannelState` at the source event. Each
  tracker tick decrements the live `RetriggerCountdown`, emitting
  `RetriggerCurrentVoiceCommand` only when due and preserving the volume
  transform nibble. A newly started note initializes the countdown and
  begins checking on the following tick; a Qxy row without a new note
  continues from the previous countdown, including across Pattern
  invocations sharing the mapped physical channel. Zero interval
  retriggers at each eligible active tick. S6x adds eligible ticks,
  and SEy continues the countdown through repeated spans.
- **Shared time and cancellation:** these operations retain tracker-tick
  deadlines, so a concurrent child's instantaneous or continuous Tempo
  changes retime their wall timestamps correctly. Cancelling a cursor
  drops its pending cut, delayed note and retrigger tick operations.
  For repeated SDx notes, the already mapped physical target is preserved
  and not mapped twice.

The tests compare the supported SCx/SDx/Qxy cases with the eager
`PatternNoteProcessor`, including SC0/SC1/SD0/SD1, beyond-span
suppression, S6x eligibility, SEy repeat rules, atomic sample offsets,
Q00 effect memory and countdown continuity, zero-interval retrigger,
mapped flattened-child channels, Tempo changes during pending ticks,
and cancellation.

**Scope limits:** combining SDx with Qxy is still explicitly rejected
before effect memory is changed. Fixed wall-time offsets on SCx/SDx/Qxy,
other advanced composite tracker effects, physical note lifetimes and
unported row controls require further work. This is an **experimental
engine**, not yet substituted for the production realtime or offline
compiler.

## Tenth executable step: lazy SBx row revisits and Bxx/Cxx flow

The experimental incremental Pattern cursor now handles **SBx** by
revisiting source rows *without expanding the Pattern into a complete
schedule*. Each invocation maintains its own physical-channel loop
markers, remaining repeat counts, and current source-row index. At a
completed row boundary:

- **SB0** marks the current source row as the loop beginning for that
  channel. Without a marker, the loop begins at the invocation's
  `startRow` (not at a skipped earlier row). A positive SBx count
  repeats the marked range that many **additional** times. Each
  revisit independently resolves Tempo, Speed, Source-column memory,
  effects, variable-length rows, delayed notes, and row-end cleanup
  against state as it stands on that visit.
- Loop decisions are processed in ascending **mapped physical-channel**
  order, with stable source emission order for ties. A loop end on a
  lower channel wins the global row revisit; later channel loop
  controls retain their own per-channel counters. During an active
  backward loop, Cxx on that loop's channel or a higher channel is
  suppressed on that pass, following the eager expansion's rule.
- Backward visits restart raw enumeration at the beginning and skip
  raw steps until the selected row. This is legal only for an
  `IReplayableRawPatternNoteGenerator`: a generator that can replay
  **raw notes without committing tracker state or other side effects**.
  `DataPatternDefinition` implements that contract. Scripts do not;
  a nonreplayable source attempting a backward SBx jump fails
  explicitly. The source enumerator is disposed on each restart.
  State-bearing scripts will need a genuine resumable/replay policy
  rather than silently rerunning arbitrary script code.
- A visit whose last row contains **Bxx/Cxx** now ends at that row
  boundary and yields an `IncrementalPatternTimelineStep.Flow`
  carrying its invocation ID and `PatternFlowControl` (OrderJump,
  BreakRow, and original SourceRow). Bxx and Cxx on the same
  terminating row compose. This is a **Sequence-level request**;
  the Pattern timeline does not interpret Bxx as a Pattern-local jump,
  and does not yet instantiate the next Sequence order.
- At a backward SBx boundary, an earlier raw source row may have an
  effect start at exactly the same tick as the previous row's
  cleanup. Eager output sorts those commands by **original source
  emission order**, not just by the visit order. The incremental
  cursor retains previous-row cleanup across the wrap to preserve
  that ordering for tick-zero effects.

The regressions compare against the eager `PatternNoteProcessor`:
SB0/SB2, SBx without a marker, retained tracker effect memory on
revisited rows, per-channel competing loop markers, skipped start rows,
SEy pattern delays within a loop, Bxx/Cxx composition and termination,
earlier control rows, and the SBx/Cxx suppression rule. They also
test replay-safe enumeration restart and rejection of non-replayable
sources.

**Limits:** SBx is still bounded by the tracker's nibble repeat
count. An indefinitely repeating **Sequence** requires Sequence
cursors that consume the new `Flow` steps and re-invoke Patterns
as needed, not an infinite preexpanded Pattern buffer. Continuous
cross-invocation note migration, virtual channels, advanced tracker
commands and script coroutine generation are still separate tasks.
The production scheduler has not been replaced.

## Eleventh executable step: lazy data-Sequence order cursor

`IncrementalSequenceCursor` now owns an **experimental,
single-Sequence order cursor** over the same long-lived
`IncrementalPatternTimeline`. It accepts a
`DataSequenceDefinition` or an `IReadOnlyList<SequenceEntry>`,
the existing `ISequencePatternResolver`, a shared
`SequencingContext`, and optional initial order and row.
The entries are snapshotted on construction, while Pattern generators
are resolved **only when their order is first visited**.

- When a Pattern's **source rows finish**, the cursor advances to the
  next order. It does **not** wait for delayed physical Note/Off/Cut
  deadlines from that invocation: those remain on the *same shared
  timeline* and can interleave chronologically with the next Pattern's
  notes. Tempo, Speed, mapped channel state, Txx/Qxx memory and timing
  effects are retained across orders.
- `IncrementalPatternTimelineStep.Flow` is returned to the caller
  at the completed Bxx/Cxx row boundary, before the next order begins.
  On the following step, `OrderJump` selects the new order, or
  sequential progression is used; `BreakRow` overrides the next
  entry's `StartRow` for **one invocation only**. A Bxx jump to
  an earlier order constructs a new incremental Pattern invocation.
  No future loop iterations are generated speculatively.
- The optional `shouldFollowOrderJump` callback receives the same
  `SequenceOrderJumpEncounter(PatternId, SourceRow, Order)` as the
  eager `SequenceNoteProcessor`, and may stop on a particular
  encounter (useful for bounded offline exports). A caller can also
  advance an indefinitely repeating Bxx Sequence cooperatively
  by calling `TryStep` one step at a time.
- Unresolved Pattern references and zero-row Patterns are skipped
  without advancing musical time. A resolved reference **must**
  be an `IIncrementalRawPatternNoteGenerator` with a
  `PatternDefinition.RowCount`; eager-only/script Patterns are
  rejected, not silently expanded. A maximum of 8192 internal
  operations per `TryStep` prevents a long run of missing or empty
  entries from starving the caller. **There is deliberately no
  lifetime Pattern-visit cap:** an advancing Bxx loop must be able to
  run indefinitely. The existing within-row and same-tick operation
  budgets still protect against non-advancing sources.
- The invocation's `TimelineOrigin` is supplied during its raw
  iteration and temporarily restored afterward. `Dispose()` owns
  and releases the shared timeline and outstanding raw enumerators.
  This is a **sequential data-Sequence cursor**, not yet a common
  scheduler for recursively flattened or scripted Sequences.

Parity tests cover ordinary order progression, Bxx skips, combined
Bxx/Cxx jumps with row overrides, one-invocation Cxx overrides,
initial `startOrder`/`startRow`, missing and zero-row Patterns,
Tempo and effect memory across orders, B00 observation and
termination, reporting Flow before entering the next order,
delayed notes crossing an order boundary, truly lazy Pattern
enumeration, and explicit rejection of eager-only generators.

**Production playback and offline export are unchanged.**
This milestone adds an executable order-level primitive for
future composition with recursive Pattern/Sequence cursors. We still
need genuine Roslyn iterator compilation (with CPU-only checkpoints),
shared invocation-lifetime management, flattening/mixdown, and
the remaining complex tracker-effect parity before replacing the
existing chronological data-Pattern scheduler.

## Twelfth executable step: recursive flattened invocation ownership

The new experimental `IncrementalRecursiveTimeline` composes **nested
data Patterns and data Sequences through one shared
`IncrementalPatternTimeline`**. It does not instantiate one scheduler
per child: Tempo, Speed, row ticks, deferred events and mapped
physical-channel memory remain common to parent, child, and siblings.

- Source lookup is expressed by `IIncrementalInvocationResolver`, which
  resolves immutable snapshot `SongObject` references by stable ID.
  `AddRoot(sourceId)` begins a Pattern or data Sequence at the
  timeline's current position. `TryStep` returns the existing
  `IncrementalPatternTimelineStep` types and can be advanced
  cooperatively without eager future expansion.
- Each note `Emit` now carries its *originating Pattern invocation
  ID*. When an emitted physical `StartNoteCommand` refers to an
  incremental `PatternDefinition` or `DataSequenceDefinition`
  and `Mixdown` is false, the coordinator consumes **only that
  nested start command**, leaving any unrelated commands in the
  original event. It creates a child context using
  `FlattenedChild(physicalChannelOffset: ...)` and starts the
  child at that **exact shared tick**, including fractional parent
  positions. An unknown or sample/instrument source, or a
  `Mixdown=true` start, passes through to the existing renderer.
- A data Sequence invocation snapshots its order entries, starts
  just one referenced Pattern at a time, consumes that Pattern's
  Bxx/Cxx `Flow`, and starts the requested next order at the
  proper musical boundary. An optional `shouldFollowOrderJump`
  observer retains the existing `SequenceOrderJumpEncounter`
  contract. **B00** can therefore revisit an order forever while
  creating new children only as time advances; no lifetime
  visit-count ceiling is imposed.
- Frames record parent/child relationships independently of source
  row completion. A child may outlive its parent's final row;
  a previous order's delayed physical Note/Off/Cut can outlive that
  order without stopping the next. `HasOutstandingWork` on the
  underlying timeline accounts for delayed deadlines and queued
  synthetic timing events. `Cancel(invocationId)` recursively
  disposes descendant enumerators, pending timing and wall events,
  and future Sequence orders while preserving unrelated siblings.
  Finished frames are pruned when their own work *and all descendants*
  are complete, so repeating Bxx orders do not accumulate historical
  invocation frames indefinitely.
- Recursive source-ID cycles are rejected against the **active
  ancestry path**, not across sibling invocations or separate
  sequential Bxx visits; nesting depth is limited to 128.
  A per-call cooperation budget also rejects non-advancing
  chains of missing/empty Sequence orders.

A flattened child currently requires a **physical parent target**
and unit Pitch/Speed multipliers with no initial Volume override,
matching the existing flattened-expander limitations. Mixdown
requires an independently rendered sound and is explicitly left
unflattened in this shared-clock prototype. Scripted, eager-only
and other unsupported Pattern/Sequence sources are rejected instead
of being executed speculatively.

Regressions cover child Tempo changes retiming later parent notes,
child Sequence Bxx/Cxx, grandchildren and additive channel mapping,
simultaneous siblings, descendant lifetimes/cancellation, active
cycle detection, B00 repeatedly launching child Patterns,
mixed-command retention, unknown-source and mixdown pass-through,
and unsupported transforms.

**Production playback and offline export have not migrated.** The
remaining major architecture item is generating invocation-local
raw steps for Roslyn Pattern/Sequence scripts (distinguishing
CPU checkpoint yields from musical time), together with stronger
mixed effect and mixdown parity before replacing the existing
compiler and chronological scheduler.

## Recursive composition: initial flattened-child primitive

The shared `IncrementalPatternTimeline` now exposes
`AddFlattenedChild(generator, rowCount, parent, physicalChannelOffset,
startRow, pitchMultiplier, playbackSpeedMultiplier)`. This creates a
`SequencingContext.FlattenedChild` with the inherited shared tracker
clock and channel-state map, maps its physical channels relative to the
parent, and registers a separate resumable Pattern invocation at the
current tick. It rejects parents belonging to a different timeline.
The returned invocation ID can be observed using
`HasUnfinishedRows` / `HasOutstandingWork` and cancelled separately.

This is the first explicit composition primitive, **not** recursive
source resolution yet: callers still decide when to start a child
from an emitted note. Sequence-as-child scheduling, automatic nested
source dispatch, invocation ancestry/cancellation, and mixdown's
independent timing domain are subsequent milestones. In particular,
mixdown must not be inserted into the shared-clock merger as if it
were a flattened child. Production playback is unchanged.

## Thirteenth executable step: invocation-local Roslyn Pattern iterator proof

`ScriptCompiler.CompileIncrementalPattern(ScriptPatternDefinition)` is a
**separate, experimental** Roslyn compilation entrypoint. The old
`CompilePattern`, eager `GenerateRawNotes`, and all production song,
playback and export schedulers are unchanged.

- It retains the restricted C# syntax/semantic checks and the existing
  helper validation (`Note`, `Off`, `Cut`, `Tempo`, `Speed`). A new
  syntax pass turns *direct helper statements* into a call followed by
  `yield return EmitPendingStep()`. Each invocation gets its own script
  instance and one-event receiver. Creating the enumerator does not run
  the source, and resuming after a raw `Emit` executes the **next**
  script statement only then. Helpers used outside ordinary direct
  statements (for example, a `for` increment expression) are rejected
  by the streaming entrypoint with HRS2001 instead of silently losing
  their events. The eager compiler still accepts its established input.
- Incremental `for`/`while`/`do` loops inject
  `if (ShouldCooperate()) yield return CpuCheckpoint();` at their body
  head, one yield per 128 iterations. A `RawPatternStep.Cooperate`
  represents **CPU-only cooperation**, never a sound event, tracker
  memory operation, or musical-time increment. Its Row is informational
  (the most recently emitted script row), **not** a progress boundary.
  The existing `RawPatternStep.Advance` continues to mean musical
  progress. Streaming enumeration emits a terminal `Advance(RowCount)`
  only if the script actually completes; an infinite CPU-only loop does
  not fabricate progression. Disposal ends that invocation's iterator.
- The producer enforces **nondecreasing note row positions** as it
  emits them; out-of-order scripted calls throw rather than being
  silently sorted. This is a deliberately narrower contract than the
  legacy eager script compiler: an earlier yielded event may already
  have reached a caller before later out-of-order output is detected.
  It is **not safe to install this producer into active playback**
  without deciding whether to statically validate/buffer the script
  or otherwise preserve legacy out-of-order semantics.
- The existing `SequencingContext.Random` and script helper budget
  remain invocation-local in the same runtime base. The tests cover
  lazy execution before a later invalid command, indefinite silent
  CPU cooperation, out-of-order rejection, and sandbox diagnostics.

**Explicit next prerequisites:** the current
`IncrementalPatternTimeline` buffers one musical row and does not
yet suspend that partial row to return `Cooperate` steps to callers.
It must handle checkpoints on a fixed musical instant with a bounded
per-call work budget, without confusing them with `Advance`.
Then prove deterministic random/state parity, extend the same iterator
model to scripted Sequence `Play` and ordering, define replay/seek
semantics, and only then admit scripts in the recursive resolver.
No production scheduler was switched by this proof.

## Fourteenth executable step: CPU-only cooperation in the shared timeline

`IncrementalPatternTimeline.TryStep` now returns
`IncrementalPatternTimelineStep.Cooperate(InvocationId, Tick, Time)`
when a raw producer yields `RawPatternStep.Cooperate`. This returns CPU
control at **unchanged musical and wall time**; it emits no sound and
does not commit Tempo, Speed, Source memory or other tracker commands.

A Cursor now retains a partially prepared row across cooperation
checkpoints. Collected raw events, ordering, row timing requests and
delay-effect candidates remain buffered. The source enumerator resumes
from the same position on the next call. Only when the row has been
completely collected can its commands enter shared-clock arbitration.
The row's starting tick, wall time and captured speed stay fixed while
preparation is suspended. A raw CPU checkpoint's Row is informational,
not a musical progress marker: only `Advance` denotes progress.

Both the same-tick scheduler budget and per-row raw-step limit remain
effective. Infinite silent loops return checkpoints and can be cancelled
without moving the clock; an unbounded number of such returns at one
tick eventually hits the existing same-tick cooperation budget.
Tests cover a paused row with a buffered Tempo command, a fractional
checkpoint with unchanged time, cancellation/disposal of a never-ending
CPU-only producer, and an actual resumable Roslyn loop feeding the
shared timeline.

This **does not switch production playback/export** or add scripted
Pattern/Sequence sources to `IncrementalRecursiveTimeline`.
The experimental timeline still buffers a full musical row for stable
command ordering. The next step is a resumable scripted Sequence
`Play` producer and then safe recursive script admission, with
deterministic Random and legacy out-of-order script semantics resolved
before production migration.

## Fifteenth executable step (historical): lazy Roslyn Sequence Play invocation

**Superseded by the seventeenth step below.** The streamed Play
iterator and generated-entry cache described here have been removed.

A new Core contract, `IIncrementalRawSequenceEntryGenerator`, produces
`IEnumerable<RawSequenceStep>` per invocation. A `RawSequenceStep.Play`
contains one ordinary `SequenceEntry` (`PatternId`, `StartRow`);
`RawSequenceStep.Cooperate` means **CPU-only suspension**, not a tracker
row, elapsed duration, or audio event. The contract does not import Roslyn
into Core.

`ScriptCompiler.CompileIncrementalSequence` is an **experimental,
separate Roslyn entrypoint**. It rewrites direct `Play(...)` statements to
execute the original helper once, then yield exactly that entry. The
`SequenceScriptProgram` instance and its enumerator belong to one
invocation, preserving local variables and deterministic context `Random`
across successive `MoveNext` calls. `for`/`while`/`do` loop heads
cooperate every 128 iterations without advancing musical time. Streaming
`Play` and `Random` do not consume the eager compiler's total-lifetime
script budget, because a valid sequence can run indefinitely as musical
time advances. Existing restricted syntax/semantic/reference validation
and diagnostic codes remain in force. `Play` in an expression context
rather than as a direct statement is rejected by this streaming API with
HRS2001. The old `CompileSequence`/`ExecuteScript` eager preparation
path, including its own resource budget, remains unchanged.

`IncrementalSequenceCursor` now has an overload accepting the raw
Sequence generator. It retains its iterator rather than expanding the
whole Play list. The next `Play` is requested only when its order is
needed, **after the preceding Pattern finishes its source rows**; any
already established delayed physical notes can still overlap following
orders on the shared Pattern timeline. Script CPU checkpoints surface
as `IncrementalPatternTimelineStep.Cooperate(-1, Tick, Elapsed)` (the
sentinel -1 denotes the Sequence script, not a Pattern invocation).
Tick, wall time, and effects remain unchanged. A same-tick guard permits
up to 8192 such returns before explicitly failing a continuously silent,
non-advancing Sequence script. Each `TryStep` also retains its existing
finite zero-progress traversal work budget.

Previously generated entries are cached so Bxx can revisit them without
rerunning the script, changing its `Random` history, or prematurely
executing statements beyond the loop target. Cxx continues to supply a
one-invocation starting row. Forward Bxx jumps can request successive
future Play entries until the target exists or the generator completes.
The shared Pattern cursor remains the authority for actual musical
advancement, Tempo/Speed, timing and physical note ordering.

Tests cover statement-by-statement suspension before an invalid later
Play, infinite CPU-only loop cooperation, `Random` and local counter
reproducibility across independent invocations, streaming-only HRS2001
and restricted-code diagnostics, sequential Pattern timing, B00 cached
visits, checkpoint time invariance, iterator disposal, the 8192 same-tick
guard, and an **actual Roslyn Sequence** consuming two data Patterns on
the shared-tick cursor.

**Important boundaries:** Generated Play entries are currently cached
for possible backward visits; a script producing an unbounded number of
distinct orders can grow this cache indefinitely. Establish a bounded
rollback/history policy before production admission. This cursor still
requires resolved child Patterns implementing both `PatternDefinition`
and `IIncrementalRawPatternNoteGenerator` (data Patterns today); it
does **not** automatically compile scripted Pattern definitions, recurse
flattened grandchildren, or implement independent mixdown clocks.
Script/Sequence source admission to `IncrementalRecursiveTimeline`,
snapshot-safe ownership, general script ordering, voice lifecycles and
full timing parity remain future work. No realtime/offline production
scheduler was changed.

## Sixteenth executable step (historical): opt-in recursive Roslyn admission

**The script admission remains; its Sequence iterator and entry cache were
replaced by per-visit lookup in the seventeenth step below.**

`IncrementalRecursiveTimeline` now admits resumable
`ScriptPatternDefinition` and `ScriptSequenceDefinition` sources alongside
data Patterns and data Sequences. Core remains Roslyn-independent: an
**optional** `IIncrementalScriptSourceCompiler` supplied to the
coordinator compiles scripted definitions to
`IIncrementalRawPatternNoteGenerator` or
`IIncrementalRawSequenceEntryGenerator`. The Scripting assembly provides
`RoslynIncrementalScriptSourceCompiler` as the explicit opt-in bridge;
the previous two-argument constructor continues to run data sources
without compiling scripts. An unsupported or unconfigured scripted
source fails explicitly rather than invoking an eager fallback.

Scripted Pattern invocations use exactly the same
`IncrementalPatternTimeline.Add`, physical-channel mapping, Tempo/Speed
state, delayed-note ownership and recursive ancestry/cycle checks as
data children. Scripted Sequence invocations retain their own suspended
`RawSequenceStep` enumerator and generate `Play` entries only when their
next order is needed. Previously generated entries remain cached for Bxx
revisits, preserving script locals, `Random` consumption and ordering
without executing future script statements. Cxx start-row overrides are
still one-use, and the shared Pattern timeline remains the only musical
clock. A recursive Sequence `Cooperate` step is explicitly CPU-only
and keeps Tick and Elapsed unchanged, with a per-invocation, same-tick
8192-checkpoint guard.

Every active Sequence order now records **the specific newly created
Pattern frame ID**, not the latest historical child. This matters when
a streaming Sequence finishes, skips a missing order or jumps to a
not-yet-generated order: a completed older frame must not be reselected,
preventing final pruning. Cancelling a subtree or disposing the
coordinator releases suspended Sequence and Pattern enumerators while
retaining unrelated sibling invocations.

Regression coverage includes a scripted child changing Tempo before
a parent's next row, CPU cooperation before a scripted Sequence's first
Play, cancellation/disposal, recursive Bxx replay without triggering
later scripted Play instructions, a genuine nested Roslyn Sequence
launching a Roslyn Pattern with mapped channels, and explicit rejection
of out-of-order scripted Pattern notes. The red commit demonstrated
missing script contracts, and the first implementation run exposed the
order-frame lifecycle bug fixed test-first before documenting.

**Not a production migration.** The generic resolver still must return
stable immutable song snapshots; this experimental bridge does not
snapshot mutable document objects itself. Compiling scripts upon
invocation may be expensive, so offline/realtime source preparation and
compiled-program caching need an explicit policy. Distinct future
scripted Play entries are still cached without a bounded-history scheme;
a long-running script that keeps generating new orders can grow memory.
The streaming Pattern API still rejects out-of-order note positions
rather than preserving eager ordering. Mixdown's independent clock,
full virtual-channel/effect parity and export bounds remain outstanding.
Production playback/export, their eager compatibility fallback and
`SongScheduleCompiler` are unchanged.

## Seventeenth executable step: stateless-index, visit-sensitive Sequence lookup

Scripted Sequences no longer yield a stream of `Play` entries. All
three sequencing paths—`SequenceNoteProcessor`, the standalone
`IncrementalSequenceCursor`, and `IncrementalRecursiveTimeline`—now
resolve **exactly one entry per order visit** using:

```csharp
SequenceEntry? GetSequenceEntry(
    int absoluteIndex, int sequenceIndex, int previousSequenceIndex);
```

The values are scoped to one Sequence invocation. `absoluteIndex`
starts at zero and increments for every lookup (including repeated Bxx
visits, missing Patterns and a final `null`). `sequenceIndex` is the
requested order after any Bxx/Cxx flow. `previousSequenceIndex` is the
order passed on the previous lookup, or -1 on the first. A `null`
return ends that Sequence immediately and naturally, even if its
`sequenceIndex` is otherwise valid.

`DataSequenceDefinition` trivially implements this Core contract,
ignoring the visit history and indexing its Entries list. Scripted
Sequences use a generated Roslyn method body with those three arguments
in scope. In scripts `Play(_O(patternId), startRow)` now creates a
`SequenceEntry` **for return**, rather than appending it. The idiomatic
restricted-C# source is:

```csharp
switch (sequenceIndex)
{
    case 0: return Play(_O(17));
    case 1: return Play(_O(18), 2);
    default: return null;
}
```

This is only a convention, not a rule: a script may choose based on
`absoluteIndex`, `previousSequenceIndex`, `Random()` and local
expressions. The function can return a different child for the same
Bxx-targeted order on its next visit. Its local variables reset on each
call, while the `SequencingContext` RNG persists in one invocation.
`ISequenceEntrySourceFactory.Create(context)` binds each compiled
script to a fresh invocation-local `ISequenceEntryProvider`. Script
loops retain a **per-lookup** runaway CPU guard; there is no longer
a Sequence-specific CPU cooperation yield. Musical cooperation and
suspension for **Pattern** scripts remain as before.

No script-entry list or stream iterator is cached. Bxx and Cxx remain
the responsibility of the Sequence scheduler, not the script. The new
model avoids unlimited scripted-order history and makes backward/forward
flow equally cheap at the Sequence-definition layer. It intentionally
changes the restricted script language: earlier `Play(...);` statement
scripts must return an entry or `null` on every control-flow path.
The obsolete `RawSequenceStep` and
`IIncrementalRawSequenceEntryGenerator` types were deleted.

The production `SongScheduleCompiler` now uses the same lookup
contract. For eligible scripted root Sequences, a chronological
scheduler consults each new entry at the preceding root's endpoint,
retaining concurrent nested rows, shared Tempo/Speed, delayed notes,
and positional metadata. It qualifies potential targets by static
script object references rather than executing the script to build
an entry list; a non-eligible arrangement uses the established
eager `SequenceNoteProcessor` with **per-visit lookup**. The recursive
experimental scheduler likewise binds a script provider once per
invocation, preserving Bxx/Cxx, root/child lifetimes, and shared clock.

Tests cover in-place Bxx replacement of its own target Pattern by
visit number, lookups starting from a nonzero requested order,
`previousSequenceIndex` changes, `null` termination, deterministic
invocation-local RNG, per-call runaway handling, and nested timing
parity. Real-time/export migration of the experimental recursive
clock and remaining unsupported advanced effects are still open.

## Eighteenth executable step: snapshot-owned preparation of recursive Roslyn scripts

`PreparedRoslynIncrementalScriptSources` (Scripting) is an **explicit
opt-in** source factory for experimental `IncrementalRecursiveTimeline`.
It implements both Core contracts,
`IIncrementalInvocationResolver` and
`IIncrementalScriptSourceCompiler`, so one prepared object supplies the
same source graph and the corresponding precompiled script factories.
It can create a new experimental coordinator with
`prepared.CreateTimeline(context)`. This does **not** switch production
playback or offline export to the recursive scheduler.

The constructor takes a `SongDocumentSnapshot`, then captures its
**own private document snapshot** before preparing scripts, preventing
later edits to the authoring document *or to the caller's supplied
snapshot* from changing the executing source text or Pattern
RowCount/ChannelCount. The existing snapshot mechanism deep-clones
song objects and shares immutable sample PCM, not new PCM buffers.

At preparation, every scripted Pattern and Sequence in the owned
snapshot is compiled **once** with the existing restricted Roslyn
compiler; failures are surfaced immediately before a timeline starts,
including scripts that are not currently reachable from the root.
Prepared factories are cached by immutable object ID. Each Pattern
enumeration and Sequence `GetSequenceEntry` invocation creates its
own runtime state from a new `SequencingContext`, preserving the
existing deterministic RNG contract and avoiding sharing mutable
program instances. `CompilePattern` and `CompileSequence` reject
definitions not owned by the prepared snapshot, rather than
accepting an authoring object with a colliding ID.

The preparation work, including full Roslyn compilation, **must be
performed off the audio callback**. Only already prepared factories
are used by the experimental timeline. Regression tests prove
source/dimension isolation across both mutable documents, factory
identity across visits, deterministic fresh-lookup RNG, two complete
timeline passes, and fail-fast restricted-script diagnostics.

**Still open:** production uses its existing scheduling and compiled
source resolver; this new entrypoint is not yet wired to the live
playback session or export snapshots. Snapshot ownership/PCM-sharing
tests establish isolation for the opt-in experimental path, not
timing parity or safety of a future production migration. Out-of-order
raw scripted Pattern events, unsupported effects, flattened voice
lifecycles and independent mixdown clocks remain separate gates.

## Nineteenth executable step: silently discard out-of-order raw Pattern events

**Authoritative Pattern ordering policy:** raw note events must be
emitted in nondecreasing **musical-row position**, and a late arrival
whose raw row is earlier than the last accepted position is silently
discarded. This is not a hard error and does not rewind the cursor.
Emissions at the **same** position are valid and retain their
original order. A rejected event cannot change shared Tempo, Speed,
Source, or tracker-effect memory. Discarded events do not update the
last accepted position: after rows 4, 2, 3, 4, 5 are emitted, the
accepted events are 4, 4, 5. The rule applies to both scripted and
data sources at Core consumer boundaries.

The underlying positions are raw tracker **rows**, not final
wall-clock deadlines. A positive fixed `TimeOffset` is resolved
later; earlier wall deadlines on later rows do not cause otherwise
valid raw notes to be dropped. `RawPatternStep.Cooperate` is only
CPU cooperation, not a claim of musical progress. Backwards
`RawPatternStep.Advance` progress remains invalid; earlier
`RawPatternStep.Emit` notes are silently skipped. Infinite streams
of stale notes are still subject to the existing per-row/at-one-instant
no-progress budgets, so the silent-discard policy cannot stall the
audio thread indefinitely.

Eager scripted Patterns now filter raw results as they are appended,
before the schedule is frozen or tracker commands are resolved.
`PatternNoteProcessor` enforces the same contract for general eager
raw generators. `CompiledIncrementalPatternGenerator`,
`IncrementalPatternTimeline`, and the standalone
`IncrementalPatternNoteProcessor` skip stale `Emit` steps instead
of throwing, without reordering or buffering future events.
The former streaming rejection behavior is superseded.

Regression tests verify scripted eager and streaming drop semantics,
stable equal-position ordering, shared-clock timestamps, and a late
global Tempo request that **must not** retime later notes.
**No production scheduler migration** is implied by this policy.

## Proposed next interfaces and migration

1. Extend the implemented **shared-tick recursive Pattern/Sequence
   invocation coordinator** to cover scripting and remaining effect
   semantics. Flattened data children and Sequence orders now execute
   lazily with subtree lifetimes; Bxx/Cxx move between orders and SBx
   revisits replay-safe rows. Finish SEy repeated Tempo, SDx/Qxy
   interactions, advanced effect combinations, virtual channels,
   mixdown clocks, and incompatible Tempo spans before adoption.
2. Add **script invocation-local iterators** through Roslyn syntax
   rewriting. The existing loop `Checkpoint()` instrumentation for
   `for`/`while`/`do` is the starting point, but the generated
   `ExecuteScript()` currently returns `void` and its `Note/Off/Cut`
   helpers append to an eager receiver. The transformation must suspend
   and resume without sacrificing diagnostics or deterministic Random().
   A computational cooperation step is distinct from musical `Advance`:
   it yields CPU control but does **not** advance musical time.
3. Extend the new **recursive Pattern/data-Sequence coordinator**
   to accept resumable scripted invocations and true mixdown semantics.
   The common clock already orders flattened active cursors by musical
   deadline, with stable same-time tie breaking, and handles
   cancellation and migrated/releasing note lifetimes.
4. Move realtime and offline compilation consumers across behind tests,
   then remove duplicate eager/chronological engines once semantics match.

## Correctness constraints

- **Causality:** looking ahead for the next step must not apply future
  Tempo/Speed, Source or tracker-effect memory to shared state.
- **Ordering:** a Pattern source must emit chronological raw musical-row
  positions; out-of-order notes are silently discarded rather than
  buffered, sorted or treated as hard errors. Equal positions preserve
  source order; fixed wall offsets remain independent deadlines.
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
