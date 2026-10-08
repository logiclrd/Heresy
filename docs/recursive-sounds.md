# Recursive Pattern/Sequence sound sources

This document records the *first executable slice* of recursive playback and
the contracts still outstanding. The tracked requirements are in
[docs/todo.md](todo.md); this file does not mark them completed.

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

**This is NOT yet a general unified concurrent row scheduler.** Future child
notes are still compiled eagerly. The queue currently schedules delayed
changes using the child's precalculated absolute wall time. If an
intervening parent tempo change would move that future child's row
boundary, a real tick-domain scheduler must recompute that boundary.
The implementation deliberately rejects detected intervening parent tempo
effects instead of producing silently inconsistent time. Tempo ramps,
child speed changes, nested pattern/fine delays and arbitrary overlapping
effects require further work. Future child effect/source-memory changes
are also currently applied too early by eager child compilation, and need
a comparable deferred event model. Child musical time, not wall time, must
be the final source of truth for all of these.

## Boundaries deliberately NOT complete

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
