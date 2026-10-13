# Synth graph ISound source — architecture and staged implementation

**Status (13 October 2026):** source **reference/data-model foundation**,
**prepared child-invocation/mono-output**, and **streaming frequency modulation** implemented. Graph editing,
cross-song import, full transport end/tail qualification, and deterministic
backward replay remain unimplemented. Both data and scripted Instrument
Filter plans are now producer-prewarmed for frozen playback. The UI
must not offer creation of this node until the remaining rendering slices
are complete. Connected linear/semitone inputs now advance an integrated pitch curve at each
PCM frame; backwards replay of an advanced source voice still fails
explicitly instead of reusing stale DSP history. Do not mark the overall
feature complete based on the playback of constant-frequency source nodes.

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
2. **Prepared recursive binding and mono adapter — complete:** resolve eligible
   source once per frozen graph preparation, flatten child Instrument invocation,
   capture volume and per-tone Filter configuration and produce scalar
   graph-domain output. The adapter has independent per-voice states, a mono
   render context with zero positional importance and arithmetic averaging of
   PCM source channels (so stereo L/R become (L+R)/2). A node with no resolved
   source is silent. It computes the initial child invocation from the
   requested Hz/C4 ratio, parent pitch/speed or Constant isolation, without
   dispatching library lookups from its PCM frame loop. Child Tone Filter
   chains are per-voice; in frozen production playback their immutable static
   Filter/FIR plans are precompiled for the output rate before the producer
   starts, and independent histories are instantiated when voices bind.
   Direct standalone sounds without a prepared-plan provider retain the
   legacy first-render fallback for compatibility.
   The parent Synth applies spatialization only after the mono graph output.
   Existing graphs and renderer constructors without a source remain
   compatible. The public authoring/editor affordance is intentionally absent.
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

### Limitations of the prepared mono slice

The prepared child sound is selected only once for each Synth invocation.
Nested Instrument tone selection and the captured Volume Envelope are
honored; the inner Sample/Synth supplies mono signal to the outer graph.
The neutral frequency uses C4 = 440 * 2^(-9/12) Hz. Constant sources
receive neither parent note pitch nor playback-speed scaling.

This checkpoint is **not** the full musical execution contract. Connected
modulation advances a per-child two-frame streaming `PitchCurve` without
per-frame allocation. `PitchTrajectory` then integrates the prior frames
when SampleSound requests source positions. Linear inputs at or below zero
mute the node and use a tiny positive trajectory multiplier (approximately
stationary; reverse playback is not offered). Exponential connections treat
control values as semitones. Nonfinite multipliers reject explicitly. Source
voices do not yet support backwards render replay or changing output sample
rates on an existing state. Source graphs now derive conservative end bounds when their children or
an audible graph gate have a provable finite end. This includes observed
Sample EOF, Instrument Volume Envelope release ceilings, nested Synth
release and bounded tone Filter drain. Unproven indefinite paths still
report no finite end. Pitch/Filter/Panning envelopes, child FIR realtime scheduling,
post-end/filter tail processing and exact transient release behavior must be
completed and tested before editor exposure. Source resolution occurs while
constructing the frozen renderer. A node's child voice is created when the
parent's `CreateInvocation` runs; `ISound` sounds that flatten recursively
may resolve their already-prepared tone target at that invocation boundary,
not in PCM render. For frozen playback, output-rate mono/stereo child contexts and Filter
histories are constructed during the source voice's initial invocation;
static FIR plans were already prepared before playback. Standalone callers
that do not supply the producer's plan cache retain the old fallback.

### Streaming frequency-modulation checkpoint (13 October 2026)

A reachable `FmSoundSourceNode` now accepts its connected numeric input, in
normal graph topological order. `FmSourcePitchCurve` retains the **current and
previous frame** value, which is enough for sequential calls to the existing
`PitchTrajectory.GetPosition(frame)`; no per-frame `SetMultiplier` segments
or growing pitch journal are allocated. Each outer frame writes the effective
relative rate once, including the parent's current transport trajectory and
`Constant` isolation. This is deliberately a *streaming-only* curve: arbitrary
backward reads require the next task's producer-prepared reset and replay.
Unassigned sources remain silent and stable. Source tone selection is still
performed once on binding; audio-rate input never reselects Instrument tones.
The tests cover output source-frame positions, linear/semitone inputs,
continuous modulation, pitch isolation, mute/freeze policy, chunking and
steady managed-allocation bounds. Completion of note-off/tail propagation and
arbitrary backward replay is separately tracked in `docs/todo.md`.

### Source natural-end and tone-Filter lifecycle checkpoint (13 October 2026)

The outer Synth no longer returns an unconditional indefinite end merely
because its graph contains an ISound node. Every source has a private
invocation and a first-known excitation-end frame. For modulated samples,
`SampleSoundState.NaturalEndFrameExclusive` is discovered during actual PCM
rendering instead of projecting the non-constant pitch curve. Parent Note Off
is forwarded to the child without restarting its release on every render
block. A bare looping Sample becomes finite on Note Off; Instrument-selected
Samples retain their release when a bound Volume Envelope owns that release.
A finite Volume Envelope post-Off end may bound a looping sample or other
indefinite child. Once excitation stops, the child's per-voice tone Filter
chain receives exactly one `FinishInput` and emits zero-input DSP history
through the configured `PreparedTailFrames`. The graph's own Filter-tail
accounting still extends the source end as needed. Graph operator analysis
requires all additive branches to end (or any multiplicative factor to
become silent), and does not prematurely cut a genuinely indefinite input.

This is **not** certification of every inherited effect. In particular,
Instrument Pitch/Panning/Filter envelope routing, worker scheduling of child
FIR preparation, detailed graph-tail gating and realtime/offline lifecycle
acceptance remain separately unchecked in `docs/todo.md`. The independent
backward-seek and replay milestone is also still pending; do not expose
creation of this node in the editor yet.

### Child Instrument control-envelope checkpoint (13 October 2026)

Each prepared nested source voice now reads its captured Instrument Pitch
Envelope in **octaves** and multiplies its streaming `FmSourcePitchCurve` by
`2^octaves`. Parent Note Off passes into the exact child envelope release
frame, including the velocity/frequency modulation already applied by the
source node. This does not re-run Instrument tone selection. Instrument
Panning Envelopes are evaluated on an isolated **stereo child bus** and
collapsed to the graph's scalar input before the outer Synth applies its
only final spatialization. For native stereo Samples the fold averages the
stereo source image; for mono sources the stereo output is normalized by
`sqrt(2)` to retain centred equal-power amplitude. A scalar Synth graph
cannot retain a genuine independent stereo field; this downmix is intentional
and not a second final speaker pan. A child Filter Envelope follows the
tracker legacy cutoff convention (absolute normalized [0,1]), updating
per-voice `ResonantFilterState` after its inherited tone Filter chain and
before the Volume Envelope, so Filter history is independent for each note.

The parent `IFirPreparationSound` now forwards `AdoptPreparedFirControls`
and `QueuePendingFirControls` to its already-bound child `ISound`s, and
explicit `PreparePendingFilterControls` descends into nested Synth
invocations without resolving song objects inside PCM. **Static Instrument tone-Filter plan preparation is now fixed in frozen
playback** by the producer prewarm described below; the fallback for direct
standalone `FmSynthSound` callers is deliberately retained. Scripted Instrument tones are selected from finite, persisted specifications;
all of their Filter combinations are now prewarmed too. There is no existing
API for scripts to generate new Filter parameter sets. Backward replay and
transport acceptance remain open.

### Static inherited tone-Filter prewarm checkpoint (13 October 2026)

`PreparedIncrementalPlaybackFactory` supplies the actual output sample rate
when creating `PlaybackSnapshotSoundResolver`. Its producer-side
`PrepareDirectSources()` scans frozen **data-driven and scripted Instrument** tone
specifications (including unselected scripted branches), prepares all distinct configured Filter stage kernels
(including FFT-based FIR phase conversion), then freezes a read-only
`PreparedToneFilterStageFactory`. Preparation and validation happen before
any PCM producer/callback can render; stage identity, generation, numeric and
structural parameters and sample rate are matched exactly. A stage may appear
at multiple nested Instrument layers while sharing **only** its immutable
plan, never mutable convolution histories. The per-voice Synth invocation
instantiates its own `StreamingFilterChain` from those prepared plans, plus
output-rate mono/stereo child contexts and optional legacy resonant Filter
Envelope state, before its first rendered frame. The first `Render` or
`GetEndFrameExclusive` in frozen playback does not call a Filter stage
factory or design a FIR kernel. Live playback remains behind the existing
`BufferedAudioOutputSource` producer and its independent FIR-control
scheduler; offline Pattern rendering uses the same frozen prewarm route.

Scripted Instruments do not construct Filter configurations dynamically:
`ScriptInstrumentDefinition.Source` selects an integer index from its finite
`ToneSpecifications`, and `PitchExpression` returns a scalar pitch factor.
Both data and scripted tone specifications (including branches never selected
at preparation time) now enter the same frozen prewarm pass. Consequently, the
notional queue for unknown scripted Filter configurations is unnecessary in
the current architecture: no script can produce such a configuration. The
cache still throws for a genuinely unprepared parameter set, protecting the
PCM path if some **future** plugin or API gains dynamic Filter authoring.
That future feature would need its own explicit preparation and latency
contract, not a speculative realtime queue today.
Standalone direct `FmSynthSound` use without the optional prepared factory
retains the earlier first-render fallback for compatibility, and is not a
realtime-prepared invocation. Frozen voices reject output sample-rate changes
and must be recreated from a different sample-rate snapshot to change the
rendering format. Wider end/tail transport acceptance and backward replay
remain distinct open tasks.

### Scripted tone table prewarm checkpoint (13 October 2026)

The script `Source` expression returns only a **zero-based index** into
`ScriptInstrumentDefinition.ToneSpecifications` (or -1 for silence).
`PitchExpression` returns a pitch multiplier. Script evaluation cannot
construct a new `FilterInstance` or change stage parameters; even a
pitch-sensitive selection is constrained to the finite declared table.
During frozen-song preparation, `PlaybackSnapshotSoundResolver` therefore
prewarms every static and scripted tone's `FilterConfiguration` at the
known output sample rate *before* any note rendering or worker startup,
including choices that will be selected only by subsequent notes.
The Filter plan cache is sealed, and a nested Synth's selected tone reuses
only its prepared immutable plan while owning a fresh DSP history. Tests
exercise minimum- and maximum-phase FIRs selected by scripted pitch split,
comparing the actual Pattern output with an independently rendered reference.
This closes the supposedly dynamic scripted-FIR gap **without** an extra
queue, new voice latency or altered song format. If a future scripting API
actually gains dynamic Filter construction, it will require new design and
tests; none of the current code silently performs FFT design in PCM.

### Dynamic source endings and Pattern producer qualification (13 October 2026)

A Synth with ISound source nodes implements `IStreamingFiniteSound`, so the
existing `PlaybackSession` asks for its effective end **again** following
each rendered block. This is necessary for SampleSound whose exact natural
EOF is discovered only while integrating PCM-frame pitch, and for nested
source-Synths which discover their own terminal bounds during a parent
frame. Nested end observations propagate to the parent in the same
source-frame evaluation. The parent refreshes its graph cutoff when a child
first becomes finite inside a block; it no longer waits for an unrelated
future Render call to learn that boundary.

Because a containing Synth can poll a nested sound's finite end once per
PCM frame, `FmSynthState` now owns reusable per-invocation end-analysis
scratch, and the graph's maximum authored Filter-tail allowance is compiled
once from its immutable DAG. End queries no longer build an array/HashSet
per sample; a warmed nested looping-source regression checks bounded
managed allocations across repeated PCM blocks.

Graph-tail accounting preserves the existing explicit FM Filter-node
budget: the **maximum** authored `TailSeconds` among Filter nodes on
reachable **audio** paths, not the sum of serial Filter allowances. Serial
physical DSP tails may be longer than that budget and can intentionally be
truncated; the user controls this maximum. Filter parameter wires and
oscillator frequency controls do not add audible tail paths. Algebraic graph end
bounds remain conservative: Multiply is provably silent when *any* input
becomes silent; Add, Minimum and Maximum require *all* contributing inputs
to have finite silence bounds. A graph Envelope without an actual Note Off
must not use an imaginary release at time zero when estimating these bounds.

Direct regression fixtures check source/oscillator-style indefinite branches,
serial and parallel graph Filter tails, multiply/min/max, unreleased versus
released envelopes and nested Sample → Synth → Synth natural EOF. A frozen
Pattern producer fixture plays a four-frame sample through nested Synths
and verifies that the physical voice is detached within the same 16-frame
render. A parallel fixture with a persistent constant branch correctly
retains its indefinitely sounding voice. These changes do not alter saved
song schemas or the existing file-export interfaces.

**Remaining qualification:** moving transport pitch envelopes, loop-to-release
control interactions, shared oscillator audio/control gates, concurrent NNA
and virtual voices, and explicit offline FLAC versus buffered realtime
comparison still need focused tests and possible fixes. Source-bearing
Synths still reject backward replay and remain unavailable in the editor.
