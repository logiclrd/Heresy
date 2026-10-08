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

**Remaining correctness work:** Data Pattern raw-note generation currently
reads/resolves remembered source IDs for the entire grid before the row loop
starts. That means a child changing `CurrentSourceId` cannot yet influence a
later source-omitted parent note within the same parent pattern. Moreover,
child tempo events that start partway through a parent row do not yet reshape
that already-computed parent row's timeline; virtual channel scopes and
parent-to-child effects, release and pitch/speed propagation remain open.
These cases require moving more of data-note resolution into the live
row-processing timeline and adding tests for simultaneous operations.

## Boundaries deliberately NOT complete

- Ordinary `Mixdown: false` nested Pattern/Sequence notes are now
  compiled at their parent's active sequencing row, sharing tracker effect
  memory and later-row timing changes. Full flattening still requires
  source-selection memory at row-generation time, exact within-row tempo
  interplay, virtual-channel scoping, and parent-to-child effects/note
  actions. No separate child session is used for flattened notes.
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
