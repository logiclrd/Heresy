# Synth graph ISound source — architecture and staged implementation

**Status (13 October 2026):** source **reference/data-model foundation** implemented.
The prepared child-invocation PCM adapter, public graph editor controls and
cross-song import are **not yet implemented**. The UI must not offer creation
of this node until the render path exists. The current renderer fails
explicitly **during preparation** for a reachable `FmSoundSourceNode`, never
silently produces plausible but incorrect PCM. Do not mark the overall
feature complete based only on persistence tests.

## Persistent graph contract

`FmSoundSourceNode : FmSynthNode` has immutable `SourceId` (stable
`ObjectId`, with `None` meaning unassigned), strictly positive finite
`FrequencyHz`, an optional `MultiplierNodeId` numeric input,
`ExponentialMultiplier`, and `Constant`. These last two settings exactly
mirror the existing oscillator's control vocabulary: linear multiplier or
`2^(modulation/12)` (semitone units), and parent-note-relative or
wall-time-constant pitch. An input edge participates in existing node-ID
validation and topological sorting, so numeric graph feedback remains illegal.
JSON uses the v1 node type `"soundSource"`, `sourceId`,
`frequencyHz`, optional `multiplierNodeId`, `exponentialMultiplier`
and `constant`. The existing `FmSynthDefinition` object schema and kind
remain unchanged. Song snapshots deep-copy this graph via normal JSON.

Core `FmSoundSourceReferences.Validate(document, ownerId, candidateGraph)`
checks **inter-object** cycles in addition to the numeric DAG. It walks
references from Synth source nodes and both data/script Instrument tone
specifications, using the proposed graph for the edited owner and the
document's other current graphs. Cycles through any combination of Synths
and Instruments reject *before* publishing a graph/audio revision.
Samples terminate traversal. `ObjectId.None` and deleted/unresolved IDs
are preserved, not rebound to arbitrary objects; the eventual adapter should
report them at preparation and produce silence. Objects that cannot be
directly invoked as `ISound` (such as Pattern/Sequence definitions without
a producer-prepared private mixdown) reject explicitly. Core's
`SongReferenceAnalyzer` includes `FmSynthSoundSource` edges, preserving
tombstones that are only referenced from Synth graphs.

## Frequency and child note semantics

A Synth node's **Frequency Hz** expresses the *requested musical note*
at a neutral graph pitch, not the PCM sample rate and not a pitch scale
relative to whichever file or nested Instrument happens to be selected.
Heresy's existing note convention treats neutral pitch multiplier **1**
as middle C (C4). The prepared child note's neutral pitch multiplier is
therefore `FrequencyHz / C4Hz` (C4Hz =
`440 * 2^(-9/12)`, approximately 261.6255653 Hz).
This ratio is supplied to the existing `ISound.CreateInvocation` boundary;
it does **not** introduce the previously discarded mutable per-source
`ReferenceFrequencyHz` setting.

When `Constant == false`, the outer note's pitch and speed trajectory
multiply the frequency. `Constant == true` excludes *outer pitch* (not
the explicitly connected modulation), just as it does for an oscillator.
The modulation is applied in the same graph topological order.
For a nested Instrument, **tone selection and child policy snapshots happen
once per spawned Synth voice** at the initial requested pitch; a connected
audio-rate multiplier must not repeatedly reselect an Instrument tone or
compile scripts while processing PCM. Subsequent pitch modulation changes
the playback position/phase of the **selected terminal source**, not the
persistent song graph. The implementation must explicitly decide zero or
non-positive linear modulation handling (freezing or muting without invalid
`SoundState.PitchMultiplier`) and test it; do not silently force it
through `CreateInvocation` where pitch must be positive.

## Execution, release, end and mono projection (remaining work)

- Build an immutable prepared source reference table from the **frozen song**
  before the audio callback. No live library lookup, imports, asset decoding,
  tone selection, script compilation or allocation for each PCM sample.
  Treat the node as part of the containing Synth voice, not a global
  independently playing timeline.
- Each Synth invocation owns **independent child SoundInvocation/state** for
  every reachable source node. Honor a nested Instrument's
  `CreateInvocation` flattening, its selected volume/pitch/pan/Filter
  envelopes and tone Filters, as well as SampleSound's source offset,
  loops and natural end. Unassigned/missing source is silent.
- Render the child through an isolated mono-output context (or equivalent
  properly normalized channel fold), with neutral child positioning, so the
  node returns **one graph-domain scalar sample**. The parent Synth's
  spatializer is applied only at the graph output; never spatialize the
  child into the final mix a second time. Define stereo-to-mono folding
  unambiguously and cover multisource gain and cancellation in tests.
- Child note release is tied to the containing Synth voice's actual
  Note Off. Do **not** replace a release with a cut or discard child PCM
  until its selected envelope and Filter tail finish. Natural sample end,
  cyclic sample loops, nested-Synth release and undefined/infinite tails
  all affect the parent `GetEndFrameExclusive` and quiescence correctly.
- Modulation is evaluated at PCM-frame granularity using an **integrated,
  replayable** source position/time warp for sounds whose `Render`
  uses the `PitchTrajectory` integral; simply writing a new pitch
  multiplier each frame is insufficient for SampleSound. Bound memory,
  avoid steady-state heap work, and have the same output under whole,
  split, backward seek/replay and parallel independent voices. Replay
  must reset/reconstruct child states and recursive envelope/filter
  histories deterministically.
- During cross-song import, traverse and import or remap all nested
  Synth/Instrument/Sample dependencies with stable identity, including
  their referenced Filter/Envelope and assets. Never silently reuse an
  unrelated target-song ObjectId. Until this is implemented, the current
  Synth import path deliberately refuses unsupported source nodes.
  UI source pickers must filter out the graph's owner, potential cycles,
  unsupported kinds and unavailable direct-resolution sources.

## Independently closable implementation slices

1. **Reference foundation — complete:** immutable node schema,
   JSON/snapshot/tombstone coverage, numeric and recursive reference cycle
   preflight, and fail-fast render rejection while not yet executable.
2. **Prepared recursive binding and mono adapter:** resolve eligible source
   once per frozen graph preparation, flatten child Instrument invocation,
   capture release/effect policies and produce scalar graph-domain output.
3. **Sample-accurate pitch, note release and seek:** implement integrated
   frequency modulation, end/tail ownership, chunking/replay determinism,
   multichannel normalization and no-alloc steady rendering.
4. **Editor and safe cross-song import:** source picker, Frequency Hz,
   numeric input/ports and connections, inline diagnostics, mutation rollback,
   dependency-safe import/remap and replacement/deletion/persistence tests.
5. **Full Pattern/transport integration:** real sample, Instrument, recursive
   Synth, scripted Instrument and unsupported Pattern/Sequence scenarios
   across snapshot playback, FLAC export, note release and realtime preparation.

Each slice needs its own tests/CI acceptance; **do not expose the node as a
usable editor feature until slices 2–3 pass**.
