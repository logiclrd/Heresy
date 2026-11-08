# Realtime Synth parameter audition — implementation plan and audit

**Status:** Foundation work in progress, not an implemented live-edit feature. The authoritative open acceptance item is [todo.md](todo.md), requiring **every non-structural editable property**, not a first phase limited to Frequency Hz, Waveform, Curvature or filter CutoffHz.

## Existing implementation boundaries discovered

- `FmSynthDocumentEditor.UpdateNode` replaces the immutable graph and increments the document AudioRevision on semantic edits; inspector text commits occur on Enter or focus loss, selection/toggles commit immediately. Node position/visual routing edits are document-only.
- The renderer compiles `FmSynthSound` from a frozen `FmSynthGraph`. Its ordered `CompiledNode[]` captures immutable node definitions and prepared resources (oscillator curvature lookup tables, Filter processors and prepared child sources). Each `FmSynthState` owns mutable oscillator phases, filter state, nested source invocations, Multi constituent states and per-voice playback history. Parameter updates must never mutate document graph objects or expensive prepared resources on the audio thread.
- `PlaybackSessionAudioSource` already queues live NoteCommands for adoption on the audio block boundary, and `PlaybackVoice` already has ongoing pitch trajectory retuning for note-level effects. Those are useful patterns, but not the complete infrastructure for parameter snapshots.
- **Critical transport dependency:** `SongPlaybackTransport.EnsureLiveAuditionCoreAsync` currently re-creates its *entire playback session* when the next `StartNoteCommand` follows an AudioRevision change. That can discard voices previously latched/held across edits. A successful live-parameter implementation must remove/replace this revision-triggered session teardown for compatible voice generations; it cannot solve the requirement solely by updating a mutable oscillator field. The *next note* must use current graph parameters while *older notes* retain identity/phase and either receive compatible updates or stay on the earlier generation for structural changes. Multi-version prepared source binding or an equivalent safe voice-local mechanism is needed.
- Source resolver and shared Filters/Envelopes can be prepared from frozen song snapshots; live controls must not recompile scripts, decode assets, rebuild expensive FIR or resolve song objects inside the PCM callback.

## Second foundation slice: versioned new-note definitions

**Implemented on the PR branch; not the completion of realtime parameter
audition.** On a direct Synth `StartNoteCommand` after a document audio
revision, `SongPlaybackTransport` now takes the cautious *same-session*
path when the active source factory provides prepared Synth publication.
It captures a frozen snapshot, asks the dedicated playback worker to prepare
an immutable replacement `FmSynthSound` and all nested dependencies,
then enqueues the result through the source's **same FIFO control stream**
as NoteOn and NoteOff. On the PCM producer, a `PublishedLiveSynthResolver`
overlays the original frozen resolver for subsequent direct Synth starts.
The old `PlaybackVoice` objects own their original sounds and states,
so they continue independently across arbitrary later graph revisions.
One original, one parameter-updated and one structurally different graph
can consequently sound simultaneously without restarting the output session.

- Publication is **per Synth source ID and audio revision**, avoiding the
  mistaken assumption that updating one Synth makes the original entire
  song snapshot current. The session-wide frozen-base revision remains
  unchanged. Unsupported source factories retain prior full-session-restart
  behavior. A non-Synth NoteOn after an audio edit also retains that
  conservative behavior. Ordinary arrangement/export sessions are untouched.
- The current `PreparedIncrementalAudioSource` event transform still captures
  its **initial** song's recursive Pattern/Instrument lookup and dynamic
  bindings. Only **direct Synth** NoteOn is opted into the nonrestarting
  path; recursive Pattern/Sequence or Instrument note starts are not.
- This is a lifetime and preparation prerequisite, **not yet the requested
  parameter edit behavior**: notes already sounding do **not** receive new
  waveform/oscillator/filter/etc. parameters. The subsequent phase must
  prepare and publish compatible per-voice parameter updates *when edits
  occur*, without requiring another NoteOn.
- Re-preparation is currently triggered by a future Synth NoteOn and may
  require significant control-worker time for complex songs. Later work
  should push prepared revisions proactively (and coalesce obsolete edits)
  while protecting note event order and GUI responsiveness.
- Check correctness of frozen-document preparation, source generations,
  per-source caching, audio routing, note release, and acoustic overlap in
  tests. The live PCM producer alone may adopt and mutate the resolver;
  the background worker only enqueues fully prepared immutable sounds.

## Property-by-property editor audit

An edit being a parameter change does **not** imply that existing renderer state may be blindly reused. It may require a prepared-resource replacement and *migration*. The full property scope is as follows:

| Node/editor surface | Editable properties | Required live behavior / migration consideration |
| --- | --- | --- |
| Constant | Numeric Value | Republish scalar, retain all running notes |
| Oscillator | Waveform; Curvature; Frequency Hz; Constant; Vmin; Vmax; Exponential multiplier | Update transfer/wave shape/rate/scale on existing phase integrators; prebuild curvature tables; preserve envelope and voice position |
| Oscillator | Multiplier input node selection | Graph connection edit: structural wiring exception |
| Source | Assigned Source (Sample/Instrument/Synth); Frequency Hz; Constant; Exponential multiplier | Parameter/source binding updates may require preparing a new child invocation and migrating/crossfading its history; merely changing SourceId is *not by itself* a graph-wire change |
| Source | Multiplier input node selection | Graph connection edit: structural wiring exception |
| Envelope | Selected/shared Envelope object and its editable node/script envelope parameters | Rebind or update prepared envelope evaluation without restarting voice, preserving playback and release progress when meaningful; adding/removing referenced graph wires is a separate structural operation |
| Filter | Per-use Filter stage parameter overrides (all valid parameter types, not just CutoffHz); TailSeconds | Reprepare filter coefficients/FIR and adopt with appropriate history migration/crossfade; avoid producer underruns |
| Filter | Shared Filter selection and shared Filter definition editing | Distinguish same-stage-identity parameter changes (live) from stage add/remove/reorder/type/generation changes requiring a processing-state transition; do not assume selecting another Filter is automatically a graph-wire change |
| Filter | Audio input and stage modulation connections | Graph rewiring: structural exception |
| Multi | Distribution, Detune, VoiceCount, Harmonic/Chord components, Randomize Phase, Gain Compensation, Cycle override/length and presets | Apply all non-wire controls; voice-count/distribution/component edits may change *internal constituent state count*, so support prepared resize/retirement/migration without tearing down the enclosing preview voice; define deterministic state transition rules |
| Multi | Input node selection | Graph connection edit: structural exception |
| Operator | Operation (Add/Multiply/Minimum/Maximum) | Republish calculation semantics for running voices |
| Operator | Input node IDs | Graph rewiring: structural exception |
| Noise | No editable sound properties | Noise seed/continuity still matter during neighboring parameter updates |
| Graph controls | Add/remove node, output node selection, connect/disconnect/modulation wires | Structural graph change; previously sounding notes may remain on previous graph definition while new notes use new one |
| Canvas | Node positions and routing waypoints | Presentation-only; no audio publication should occur |

**Scope principle:** all property rows not explicitly labeled graph rewiring must be addressed. A property that changes the number/type of nested voices or DSP stages is not silently dropped from the feature: it requires explicit compatibility/migration policy and user-audible behavior. Cross-object changes to shared Envelope/Filter/Sound sources require dependency invalidation, not just a `FmSynthGraph` reference-equality check.

## Planned architecture

1. **Classify graph wiring** by output node, IDs/types and input port identities; this is a *necessary but insufficient* prerequisite. The initial `FmSynthLiveGraphShape.HasSameWiring` helper performs precisely this narrow comparison, allowing all property edits through without claiming DSP state reuse. Separately analyze prepared state shape and resource bindings; this must not be replaced by a comparison of AudioRevision.
2. Track each active preview voice's **prepared Synth graph generation** and its original note/press identity. Versioned property publications apply to compatible generations. Structural edits leave old generations intact. Later parameter edits must never accidentally patch voices from an incompatible earlier graph generation.
3. Produce validated immutable **prepared parameter bundles** off the audio callback, including curvature tables, filter coefficients/FIR snapshots, child source/envelope bindings and Multi constituent plans. Publish as an ordered control message at the audio frame/block boundary. Use bounded latest-wins coalescing where safe without losing note/pitch/voice lifetime messages. No allocation, script compilation, asset decode or Filter redesign in per-sample rendering.
4. Have each active voice adopt new prepared references or migrate per-node state, without resetting oscillator phases, release/envelope positions, child/sample playback position or filter history unless a specific parameter's semantics require a declared transition. Smooth discontinuities where needed; correctly handle nested Multi graphs and recursive ISound children.
5. **Modify live audition snapshot lifetime** so an AudioRevision doesn't force a global teardown on the next Note On. Existing notes continue in their rightful generation; the next note resolves current frozen parameters. Keep song export/ordinary playback snapshot semantics unchanged.
6. Test all properties above via controlled PCM and UI event tests; include multi-note overlap, script/envelope changes, incomplete release tails, incompatible generations and edits-after-incompatibility, note starts after revision, lifecycle/close/F8, filter FIR preparation, ordering and underrun instrumentation. Physical listening is required before completion.
7. Only after this milestone, generalize MIDI pitch control using the same audio boundary/control foundation, but target MIDI channel-owned virtual notes instead of changing authored Synth graph definitions.

## Incremental implementation rule

Early foundation commits (graph wiring identity comparison, renderer dependency inventory and tests) **must not** imply that parameter publication or live sound changes work. Keep the TODO open until end-to-end realtime behavior and *all nonstructural properties* are verified.

## Third slice: committed edits to eligible already-running voices

**Initial end-to-end slice (still incomplete overall):** The Synth editor now
notifies an optional transport after committed audio revisions, including
shared Envelope/Filter edits surfaced in the editor. Canvas-only changes do
not publish. The transport serializes frozen snapshots with NoteOn/Off;
the control worker prepares the replacement and queues it before the PCM
producer attempts active-voice adoption, then updates future-note resolution.

Adoption requires identical *complete* authored graph wiring (including
unreachable nodes), equal compiled state slots and only reachable Constant,
Oscillator, Operator and Noise nodes. Their old phase, note/release and noise
state are preserved while new compiled parameter resources are used.
Filter, Envelope, Source and Multi graphs deliberately retain old sound
definitions for already-playing voices until dedicated state migration exists.
Structural generations stay independent: subsequent edits only update voices
holding the immediately prior published sound, not historical generations.
Released/displaced voices are included; detached anti-click tails cannot
be resurrected. One deterministic PCM test covers the active scalar change,
a structural revision and compatible edits to the new generation.

**Still open:** stateful property migration, cross-object dependency
notifications outside the active editor, all-property UI-to-PCM coverage,
continuous smoothing/crossfade, stress/rapid-edit qualification and physical
listening. The authoritative all-properties TODO remains open.
