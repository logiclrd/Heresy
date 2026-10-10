# Recursive Pattern/Sequence sound sources

> **Historical prototype record (October 8, 2026):** Everything below
> the current-state summary captures *earlier implementation stages*.
> The first-slice compiler/resolver, eager `SongScheduleCompiler`,
> `CompiledNestedMixdownSound`, unit-pitch/speed restriction, Note Off
> limitations and shared flattened child channel-memory descriptions
> have been **superseded**. They are neither the production architecture
> nor the current TODO inventory. See [incremental sequencing]
> (incremental-sequencing.md), [sample storage](sample-storage.md),
> and [remaining TODOs](todo.md).

## Current production contract (post-cutover)

- Both realtime and offline export instantiate the snapshot-owned,
  coroutine-based `PreparedIncrementalPlaybackFactory` and
  `IncrementalRecursiveTimeline`; future orders/events are **not**
  compiled into an eager complete song schedule.
- Data/script Patterns and Sequences generate cooperatively, on the
  same PCM rendering worker in realtime (or export worker for files).
  SDL only reads a bounded PCM ring. Nested mixdown sound instances
  render their own generators/PCM recursively on the same worker;
  deterministic rewind rebuilds their invocation state, not a replay
  event journal or cooked PCM cache.
- Flattened recursive sources share their parent clock but keep
  invocation/scoped channel memory and one logical instigating note
  whose live volume, Cut/Off/Fade, NNA, cancellation and release-tail
  semantics propagate to descendant voices. Independently clocked
  private mixdowns and Instrument-selected recursive tones support
  initial pitch/playback-speed composition and deterministic seeking.
- Advanced mixed-rate tracker timing, complex script interactions and
  unusual native seeks remain separate compatibility TODOs. The
  original flattened-source ownership work and baseline recursive
  playback are **complete**. The bounded runtime/editor hints for
  costly ReplayRequired Oxx/Qxy seeks are also implemented.
  See [source-seek hints](source-seek-hints.md).

---

**The sections below are preserved only as the chronological account
of the initial prototype and must not be read as current behavior.**

## Original musical model

Patterns and sequences are sources just as samples, instruments and FM synths
are. A nested invocation can be **flattened** into the parent's sequencing
context or can be a **mixdown** whose children render into one parent voice
while preserving the configured multichannel speaker feeds. Channel-state
scope, pitch/speed, Note Off, release tails, random/effect memory and
source-frame seeking must be deterministic for each invocation. A recursive
reference must be safely rejected or bounded.

This is **not** the same distinction as mono versus stereo: a mixed-down
nested source can still output stereo, 5.1 or another speaker configuration.

## First executable slice (October 8, 2026)

- The playback snapshot resolver now keys its sound cache by **(ObjectId,
  Mixdown)** rather than ObjectId alone; a source may eventually have distinct
  flattened/mixdown representations.
- For a Pattern or Sequence requested as `Mixdown: true`, the resolver uses the
  existing common data/script schedule compiler and returns a
  `CompiledNestedMixdownSound`. Each started note creates independent
  `PlaybackSession` state and renders directly into the parent's configured
  interleaved output channels. It does not eagerly cook an entire PCM buffer.
- Offset seeking is reported as `ReplayRequired`; its native source-frame
  offset is reached by deterministically replaying the child schedule from
  frame zero. Seeking backwards also recreates/replays the session.
- At the compiled logical end the child session receives `EndInput`, followed
  by the same indefinite-voice cleanup policy used by offline output; finite
  tails are allowed to continue. A rendered instance stops after quiescence.
- Active per-thread nested render object IDs reject cyclic invocation paths
  instead of infinitely recursing through `PlaybackSession.Render`.
- Integration tests start nested data Patterns and Sequences as sources from
  outer data Patterns, and check audible PCM using the actual snapshot
  playback source factory. Further tests cover native source-frame offsets,
  multiple independent nested invocations and cycle protection.

## First flattened schedule-preparation slice (October 8, 2026)

- Before creating the realtime or offline `PlaybackSession`, the playback
  preparation layer now expands **simple** Pattern/Sequence note starts with
  `Mixdown: false` into child `NoteEvent`s at the corresponding parent
  musical time. These events share the **same** parent `PlaybackSession`,
  rather than allocating one mixed-down sound voice or cooking PCM.
- Physical child channel `n` is mapped to the parent note's physical
  channel plus `n` using the Core `SequencingContext.FlattenedChild`
  mapping. The child compiler reuses the existing state and mapped-channel
  APIs; multiple expanded child starts use one expansion context.
- The expanded events are stably ordered by resolved time and capped by
  `NoteScheduleBuilder.MaximumGeneratedNotes`. Cyclic flattened references
  are rejected during expansion, before audio rendering; nested mixdown
  references continue to use the separate runtime cycle guard.
- Pattern and Sequence playback, repeating pattern playback, ad-hoc playback
  and root offline export use the same pre-expansion operation. The expanded
  logical duration includes child patterns that outlast their containing
  pattern, so export isn't cut short at the parent row boundary.
- Integration tests cover audible data Pattern/Sequence nesting, channel
  mapping, delayed child notes, duration extension, and recursive-reference
  rejection. Unsupported parent pitch/speed/initial-volume transformations
  fail **explicitly** rather than rendering incorrect notes.

**This was a limited preparation-stage integration.** The newer
in-context generation stage described below supersedes post-compilation
expansion for ordinary song and pattern playback. The remaining source-memory,
within-row timing, parent Note Off/Cut, NNA, virtual-channel and transform
contracts are still tracked in the TODO.

## Flattened child generation in active parent context (October 8, 2026)

The compiler now installs a Core-facing
`IFlattenedNoteSourceExpander` on its `SequencingContext` and invokes it when
`PatternNoteProcessor` resolves a parent note start **inside the row
processing loop**, rather than after freezing the entire parent schedule.
The expander compiles child Patterns/Sequences on
`SequencingContext.FlattenedChild` instances, which share both parent
`SequencingState` and mapped `SequencingChannelStateMap`. The script and data
compilers use the same pathway.

- Child tracker effects are resolved while parent state is active, and can
  update effect memory before subsequent parent rows. Regression tests cover
  a child tracker volume slide remembered by a later parent `D00`.
- Child tempo changes affect subsequent parent sequence-pattern timing.
  `SequenceNoteProcessor` tracks the absolute origin of each sequence entry
  without changing the local offsets emitted by `PatternNoteProcessor`.
- Flattened note events are inserted directly into the parent's schedule
  with their proper relative offsets and mapped physical channels. The
  outer compiler's returned logical duration includes nested child tails,
  without making later sequence orders wait for those tails.
- Cyclic flattened references are rejected as they are encountered during
  recursive compilation, and both realtime song playback and offline export
  consume the already-expanded compiler schedule. A separate legacy expander
  remains for arbitrary ad-hoc raw schedules that bypass normal compilation.

## Row-time data-pattern Source-column resolution (October 8, 2026)

Data Pattern raw-note generation now emits compiler-only
`SelectPatternSourceCommand` events when a Source column is explicitly set,
and unresolved note starts/portamento targets when Source is omitted.
`PatternNoteProcessor` resolves these against the mapped channel's
`CurrentSourceId` **as each row executes**. A flattened child can therefore
change the source selected by a later parent note, and that change also
affects later pattern invocations on the same mapped channel.

- Source-only rows update sequencing memory without creating playback notes.
  Skipped rows do not execute their Source changes. Missing remembered sources
  suppress playback starts but retain the original valid pattern data.
- Explicit Source selections on a row take effect before that row's note.
  Note-volume and tone-portamento semantics remain intact, including when
  source memory is absent. Raw `GenerateRawNotes` callers retain their
  previous eager resolution behavior for compatibility; the song compiler
  opts into row-time semantics explicitly.
- Deferred command interpretation applies only to data-pattern generation,
  not arbitrary scripted `StartNoteCommand` events. Selection commands are
  removed during Core resolution and never reach the renderer.
- Regression tests cover flattened source-only child rows, no retroactive
  selection, independent mapped channels, skipped source rows, and
  source-omitted tone-portamento targets.

**Remaining correctness work:** Child tempo events that start partway through
a parent row do not yet reshape the already-computed parent row timeline.
Simultaneous parent/child operations, virtual-channel scopes and
parent-to-child effects, release and pitch/speed propagation remain open.
Those cases need separate red-test coverage and potentially deeper
row/event interleaving.

## Within-row immediate flattened tempo updates (October 8, 2026)

The parent pattern processor now sorts ordinary events by their initial
wall-time position, then by mapped physical channel and emission order.
When a flattened child **sets tempo at the instant its note is invoked**,
the parent row's remaining tracker-tick timeline is reconstructed with a
piecewise-constant tempo map. This works for invocations at row start and
at fractional row offsets; parent notes and subsequent rows use the
updated timing and parent row-scoped cleanup commands end at the recalculated
boundary. Equal-time events are sorted stably: independently compiled
children can have identical emission orders, and mapped physical-channel
ordering determines which simultaneous tempo setting wins.

A prior test expecting a child tempo set at the **same** row-start boundary
to affect *only* the following row was corrected: under this timeline
contract an immediate child set affects the current row too. Existing
non-flattened tracker timing commands retain their original rule that
timing changes occur only at row boundaries (fractional RowOffset ignored).

**Remaining scope:** A flattened child whose tempo changes at some *later
child row* currently cannot be safely interleaved with a concurrently
advancing parent. Such delayed tempo changes and child tempo ramps now
fail explicitly rather than silently backdating the parent's shared
tempo state. Concurrent parent tempo ramps or pattern/fine delays together
with an in-row child tempo set also fail explicitly. The next step requires
a unified, event-driven parent/child timing scheduler and collision tests,
not another eager child-schedule shortcut.

## Deferred child-row tempo queue — initial concurrency milestone (October 8, 2026)

A new `DeferredTempoEventQueue` is attached to the flattened sequencing
context. When a flattened child has a future **SetTempo** in a later child row,
the parent records the child's absolute wall-time instead of allowing
eager child compilation to apply the change to the parent's present tempo.
The parent pattern consumes queued tempo sets in chronological order while
walking its rows; the tick map is spliced at a due change, including changes
partway through a parent row. The queue is stable for equal timestamps and
retains events across successive patterns within a parent sequence.

A delayed child row can therefore change the duration of a *concurrent*
parent row. The first supported integration case is a unit-speed/normal
tracker-tempo sequence with no intervening unrelated tempo effects.
Regression tests cover:
- a child tempo change in its second row modifying the second half of
  the parent's current row, without backdating earlier parent notes;
- a child invoked in one sequence order modifying the next order's tempo;
- strict ordering and argument validation for the shared tempo queue.

**The queue is a compatibility-path milestone, not a general scheduler.**
Its future events use precalculated wall time, so interfering parent tempo
changes may be rejected. The newer independent data-pattern row cursors
described below resolve the main shared-tick and state-memory limitations
for their supported subset. Scripted, mixed and advanced tracker sources
still require a generalized cursor scheduler.

## Independent tick-driven data-pattern row cursors (October 8, 2026)

A new `ChronologicalDataPatternScheduler` has been integrated into
`SongScheduleCompiler.CompilePattern` and `CompileSequence` for eligible
**data-only** arrangements containing flattened data-pattern sources. It is
the first actual independently advancing row scheduler, rather than an eager
child compiler corrected with deferred wall-clock effects.

- Every root or nested data Pattern invocation has its own `RowCursor`,
  containing its raw events, mapped `SequencingContext`, ancestry for cycle
  protection, and its next absolute tracker-tick boundary.
- All active cursors share `SequencingState` and the physical-channel state
  map. The scheduler advances directly to the next cursor boundary; wall time
  is integrated from the current shared tempo over the elapsed tracker ticks.
  A parent tempo change therefore moves a future child boundary naturally,
  without knowing the child's future wall-clock times ahead of execution.
- Each cursor compiles **only its current row** through the existing
  `PatternNoteProcessor`; a data-row marker retains live Source-column
  lookup and existing tracker effect-memory resolution. Future child Source
  changes and tracker memory mutations no longer execute early in this path.
- Flattened note starts create mapped child cursors at that same tick.
  Independent child cursors can outlive the root Pattern or cross into later
  data-sequence orders. Row-scoped tracker volume-slide cleanup is emitted
  at each cursor's actual row boundary; logical duration includes the
  longest active cursor.
- The regular data-pattern generator retains its public raw-note behavior,
  while the compiler selects the cursor path only for compatible data
  patterns with supported effects. Root playback positions follow the new
  row clock. Cycle detection and the established million-event cap remain.
- New regressions cover Future-Source selection not leaking before a child
  row, deferred effect memory, tempo changes of a parent moving child row
  boundaries, and source selection across data-sequence order boundaries.

**Scope of the first milestone:** The initial cursor path accepted data
Patterns with row-start tempo/speed, tracker volume slides, and ordinary
note/source events. The scripted subset described below extends the path to
fractional script note/cut/off events, but pattern-loop/break/jump control,
fine and whole-row delays, nested tempo ramps, and advanced row-scoped
effects are still outside its eligibility checks.
These inputs retain the existing general compiler path and its explicit
unsupported-combination checks where appropriate. The older
`DeferredTempoEventQueue` remains for that compatibility path; this
data-pattern cursor pathway no longer needs to precalculate a child's
future wall-clock events.

## Scripted Pattern cursors and fractional events (October 8, 2026)

The first tick-driven cursor generalization now supports **eligible
`ScriptPatternDefinition` sources** as roots, flattened children, and
entries of ordinary `DataSequenceDefinition` arrangements. Their Roslyn
programs still generate raw `NoteEvent` values through the existing script
compiler; the cursor scheduler does not reinterpret user source text or
introduce another scripting language.

- Script events are divided into independently scheduled, per-event cursor
  operations at their original (potentially fractional) row offsets. Their
  offset within a row is converted to shared tracker ticks using that
  cursor's speed at the beginning of the row. The scheduler advances the
  shared clock to the next data row, script event, or pattern boundary.
- A later parent or child tempo change adjusts **wall-clock time** along the
  shared tick timeline without moving already-established musical positions.
  An event at row 1.5 consequently follows the actual tempo history, even
  when a flattened child changes it partway through the containing row.
- Source starts from script events create independent mapped child cursors
  at their exact event tick. A child may outlive its initiating scripted
  pattern and continue through a later sequence order.
- Script raw-note generation remains invocation-scoped. The compiled
  Roslyn generator is reused across repeated invocations of the same
  script ID within one schedule; its generated raw events are not
  shared between invocations.
- The selector conservatively admits script patterns whose raw events use
  physical channels for Start/NoteOff/Cut, or the global target for standalone
  SetTempo/SetSpeed commands, with zero fixed wall-time offsets and
  nonnegative row offsets **up to and including** the final pattern boundary.
  The final row owns events exactly at RowCount and runs them at its
  RowEndTick before cursor retirement;
  these events can launch additional flattened child cursors. More complex
  script commands retain the legacy compiler. Data-pattern support
  continues alongside scripts with shared row-time Source and effect memory.
- Regressions check fractional scripted parents, fractional scripted child
  notes, tempo changes between child creation and a future script event,
  script invocations extending across data-sequence order boundaries,
  and endpoint notes/flattened invocations whose exact final tick follows
  intervening parent tempo changes. An endpoint child can outlive its
  initiating parent; the compiled logical duration includes its tail.

### Standalone scripted Tempo events on the shared clock

The restricted scheduler also supports script-generated **global
`SetTempoCommand`** events (the script `Tempo(row, value)` helper). For
compatibility with `PatternNoteProcessor`, **timing commands apply at their
row boundary**: `Tempo(1.5, 250)` takes effect at the start of row 1, not
halfway through it. Notes, Off and Cut retain their exact fractional
positions. A tempo command at `RowCount` applies at the final endpoint.

The cursor orders these effective row-start operations with ordinary events
without preparing future child state early. The shared tempo integrates
elapsed wall time between tick positions only after **all** simultaneous
operations have run. Across cursors, the stable precedence is mapped
physical-channel base followed by cursor creation order; within one script
cursor, commands at the same due tick are ordered by target and emission
order. This permits competing parent and child script tempo sets and
scripted siblings on different mapped channels without precalculated
child wall-time events.

Regressions include fractional scripted `Tempo` snapping to its row start,
competing parent/child tempo changes at a common tick, and equal-tick
sibling-script tempo changes ordered by mapped channels. This is **not**
support for tempo ramps, arbitrary global commands beyond Tempo/Speed,
or complex row/effect operations.

### Scripted Speed and independent row-length capture

Eligible scripted global `SetSpeedCommand` events (the
`Speed(row, ticksPerRow)` helper) now use the same boundary rule as
`Tempo`: the fractional portion of `row` is ignored. A scripted
`Speed(1.5, 3)` executes at the beginning of row 1, **before** that
cursor's row duration is captured. Its subsequent fractional note/cut/off
events are scheduled at positions calculated from the new row speed.

Each cursor's `RowEndTick` is captured independently. A Speed command
changes its **own** current row's tick span, and sets the shared speed for
future rows/orders and other cursors that have not started their row.
It does **not** retroactively resize another cursor's already-started
row, even if the other cursor's boundary is at the same tick. At an exact
final `RowCount` endpoint, Speed affects future work without resizing
the completed row. As with scripted Tempo, same-time operations are
resolved by mapped physical-channel base and stable cursor creation
sequence; within a script row, global timing commands precede ordinary
fractional note events.

Regressions cover fractional Speed taking effect at row start, an
in-flight data parent's preserved row length, simultaneous parent/child
Speed conflicts, carryover across data-sequence orders, and a Speed event
at a final endpoint. This milestone is about the script **timing command**,
not the playback-speed multiplier of nested sounds, which remains
explicitly unsupported for flattened recursive cursors.

### Fixed wall-time scripted note events

The restricted script helpers `Note`, `Off` and `Cut` now have an optional
numeric `timeOffsetSeconds` argument (zero by default). For example:

```csharp
Note(0.5, 0, _O(17), timeOffsetSeconds: 0.09);
Off(1.0, 0, timeOffsetSeconds: 0.025);
Cut(1.5, 0, timeOffsetSeconds: 0.05);
```

A fixed offset is added to the **actual wall time when the note's musical
row position is reached**, after all intervening shared-tempo changes.
For supported **nonnegative** offsets, the chronological scheduler
records the resulting absolute deadline and interleaves pending events
with ongoing tracker-tick cursors. Later tempo changes cannot retime an
already-established wall deadline. Crucially, command resolution and
nested flattened-child cursor creation happen only when the deadline
arrives; nothing executes early during script generation or when the
base musical position is reached.

Delayed Note/Off/Cut commands may cross sequence-order boundaries or
outlive their invoking parent. A delayed flattened child begins at the
deadline, advances on its own tick-driven cursor and contributes its
logical duration. At exact shared wall times, pending commands compete
with ordinary cursor events using mapped channel base and stable cursor
creation order. Delayed operations count against the same one-million
sequencing work cap.

This extension is deliberately narrow: negative offsets, wall offsets
for scripted `Tempo`/`Speed`, arbitrary global/advanced effect commands
and virtual-channel targets are *not* admitted to the chronological
cursor path. They retain the general compilation behavior, including
its explicit unsupported-combination checks where applicable. The
scripting API itself validates finite, representable numeric offsets.

### Scripted Sequence lookup on the shared clock

Both data and scripted Sequences expose a logical
`GetSequenceEntry(int absoluteIndex, int sequenceIndex,
int previousSequenceIndex)` returning a nullable `SequenceEntry`.
`absoluteIndex` begins at zero and increments on **every entry lookup**,
even when Bxx revisits the same order; `sequenceIndex` is the requested
order, and `previousSequenceIndex` is the previous requested order (or
-1 on the first visit). Returning `null` ends the Sequence naturally.

A data Sequence ignores the visit history and returns its indexed entry,
or `null` out of range. A restricted-C# scripted Sequence implements the
*body* of the lookup function; for example:

```csharp
switch (sequenceIndex)
{
    case 0: return Play(_O(17));
    case 1: return Play(_O(18), 2);
    default: return null;
}
```

`Play(id, startRow)` now **constructs and returns** a `SequenceEntry`;
it does not enqueue one. Scripts may instead inspect `absoluteIndex`,
`previousSequenceIndex`, and invocation-local `Random()` to choose
different Patterns for the same order on successive visits. Ordinary
local variables are fresh on each call; the RNG is shared by calls in
one Sequence invocation. Bxx changes the next requested index, and
Cxx supplies a one-invocation start row. No list of generated scripted
entries is cached and no iterator is rewound.

For eligible scripted root Sequences, the chronological shared-row
scheduler consults the function **only when the preceding root Pattern
finishes**. Static object-reference analysis decides whether that
scheduler supports all potential Pattern targets without executing the
script or consuming its RNG. Unqualified source graphs use the general
`SequenceNoteProcessor`, which also calls the same lookup function per
visit; it does not pre-expand the script. Both processors therefore
preserve Bxx/Cxx ordering and deterministic invocation-local Random.

On the supported chronological subset, nested child cursors and pending
wall-time commands can outlive the root Pattern that started them.
Global Tempo/Speed, fractional events, Source memory and wall-time
deadlines remain shared across the arrangement.
`CompiledPatternPlaybackPosition.SequenceEntryIndex` identifies the
requested order index; repeated visits can now select different Patterns
while retaining the same order index.

### Chronological Sequence entry StartRow

An eligible data-Sequence `SequenceEntry(patternId, startRow)` and scripted
Sequence `Play(patternId, startRow)` now enter that Pattern at the specified
**source row** rather than running its skipped rows. The root cursor begins
with `Row = min(startRow, RowCount)`; its first playback-position record uses
that source row. Earlier Source-column selections, Tempo/Speed changes,
notes, and effects do **not** execute or contribute their skipped time.
Each remaining row still advances by the shared tracker speed, with
subsequent nested child operations ordered by the concurrent clock.

As defined by `SequenceEntry`, skipped row durations are evaluated from
the SequencingState in effect **on entry**, without running those rows. A
script event originating in a skipped row with a sufficiently positive fixed
wall-time offset can nevertheless land after the new entry origin. The
scheduler preserves such an event by converting its remaining offset
using the entry speed/tempo and queuing an absolute wall deadline.
Events wholly before the new origin are dropped. Entries whose StartRow
is at or beyond RowCount finish immediately and advance to the next
order without synthesizing skipped row positions.

Regressions cover skipped Tempo/Speed, retained absolute source row and
order indices, Source memory not mutated by skipped data rows, fractional
script events after `Play(..., startRow)`, delayed pre-origin script events
that survive the skipped duration, and out-of-range entries.
Compile-level `startOrder` and `startRow` overrides remain on the
established general compiler path; this milestone concerns the
individual Sequence entries.

Deliberately excluded from this path: nonzero compile start order/row,
missing or unsupported Pattern sources,
pattern jumps/breaks, and otherwise incompatible nested source graphs.
They use the general compiler until corresponding scheduler tests and
logic exist.

**Not yet universal:** Negative wall-time script
offsets, fixed offsets on global/advanced effect commands, tracker
tempo ramps, other scripted global/effect commands, complex virtual-channel events,
pattern control and delayed note commands still need distinct red tests and
scheduler integration. The older compiler remains for those cases.

## Historical first-slice boundaries (superseded)

The following bullets reproduce the early prototype's unfinished-work
list. Later milestones **completed or replaced** these assumptions;
consult the current production contract above and [todo.md](todo.md)
rather than attempting to implement them a second time.

- Ordinary `Mixdown: false` nested Pattern/Sequence notes are now
  compiled at their parent's active sequencing row, sharing tracker effect
  and Source memory. Immediate child tempo changes and the first class of
  delayed child-row tempo sets are incorporated into the parent timeline.
  A general tick-domain concurrent scheduler, deferred child channel state,
  tempo ramps, virtual-channel scoping and parent-to-child effects/note
  actions remain open. No separate child session is used for flattened notes.
- The first mixdown implementation supports unit initial pitch/speed only. It
  **rejects** other initial pitch/playback-speed multipliers rather than playing
  incorrect audio. Child pitch trajectory/time-warp and parent row-time
  effects need a precise mapping.
- An early parent Note Off currently bounds the mixdown voice; fully
  propagating Note Off into active nested child voices and allowing their
  individual finite releases is unfinished.
- Scripted children, nested reference graph expansion and sound compilation
  should eventually be **prepared outside the realtime audio callback**.
  The present on-demand resolver path does not yet guarantee that; avoid
  relying on expensive scripted nested sources for realtime smoothness.
- ReplayRequired source seeking is correct for the supported unit-speed
  private-session slice, but mixing/flattening, pitch transforms, more complex
  effects and source timing need broader tests and implementation.
- Cyclic scheduled/rendered source graphs have a render-time cycle guard, but
  a full structural/cost/resource policy remains open.

Continue red-test-first, preserving the existing sample/instrument/FM
renderers and avoiding duplicate sequencing semantics in a separate engine.
