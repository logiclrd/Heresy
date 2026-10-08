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

## Proposed next interfaces and migration

1. Introduce a **real incremental Pattern processor** that consumes raw
   steps up to the current musical instant and emits resolved sequencing
   operations without preparing future shared channel state. Keep raw
   producer steps, scheduled actions and playback NoteEvents distinct.
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
