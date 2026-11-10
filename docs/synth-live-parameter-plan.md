# Realtime Synth parameter audition — implementation plan and audit

**Status:** Compatible live edits already migrate many *existing* held/latched Synth voices, with versioned preparation, whole-tree preflight, Filter/Source/Multi state retention and bounded off-thread growth. **The complete all-property live-edit feature is not finished.** The authoritative open acceptance item is [todo.md](todo.md), requiring **every non-structural editable property**, not only Frequency Hz, Waveform, Curvature or Filter CutoffHz. Read the later slices chronologically; early exclusions document historical implementation stages.

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

## Fourth slice: stateful ancestry and nested Multi adoption

Envelope nodes use frame/note-off-relative immutable curves, so newly
prepared Envelope references and shared curve edits can be adopted without
resetting the voice timeline. A Filter with unchanged stage identity, effective
parameters and controls can retain its existing IIR/FIR state while upstream
parameters or its TailSeconds change. Source nodes can carry their unchanged
prepared child invocation through compatible *parent* edits without losing
nested Sample position or Filter state. Multi retains its individual
constituent states across outer GainCompensation edits and recursively
compatible subgraph edits by updating each child sound binding. All nested
constituents are preflighted before changing any of their bindings.

**Not yet supported:** changed Filter coefficients/FIR plans or shared stage
parameters, Source reference/frequency/constant/modulation-mode changes,
Multi distribution/voice-count/components/cycle/randomized-phase migrations.
Source *dependency* changes remain captured in its old child, even when
the parent itself can be adopted; these still need invalidation and
migration. Work is incremental; the all-properties TODO remains open.

## Source scalar pitch migration (fifth slice)

For an unchanged selected child SourceId, the live Source node can now adopt
FrequencyHz, Constant and ExponentialMultiplier edits without replacing the
child SoundState. The child keeps its *original* pitch/playback-speed scale
and its original baseline frequency. The new authored rate is expressed as
a ratio in the forward-integrated FmSourcePitchCurve. This is important for
SampleSound: multiplying its bound pitch after the note starts would
retroactively rescale its accumulated source position and cause a seek.
Source pitch integration, note-off and child Filter/envelope histories remain
unchanged. New/reassigned child SourceId still requires a prepared
invocation migration/crossfade; shared child-definition updates are not yet
propagated into already-bound child instances.

## Sixth slice: compatible authored Filter coefficients

Committed edits to **one** unmodulated Filter stage per compatible Synth
graph revision can now use the existing allocation-free realtime numeric
retune protocol for OnePole, SVF, Biquad, Ladder, AllPass and both Comb
families. It preflights changed coefficients before committing any of
the stage's history; the voice's own filter state is not reconstructed.
Stage identity, generation, kind, order, and all structural parameters must
remain unchanged. Edits on a Filter that has live connected parameter
modulation are still held on its previous generation until binding-policy
migration exists.

For linear/minimum/maximum-phase FIR stages, the detached revision compiles
a bounded candidate plan on the control worker using
FirControlPreparationPolicy; the PCM producer publishes that immutable plan
through TryApplyRealtimeFirUpdates, preserving the convolution history.
No tap/FFT design occurs at publication. FIR batches exceeding bounded
preparation limits or changes to TapCount, TransitionWidthHz, or PhaseResponse
remain unsupported for live voice adoption. Multiple changed stages in one
Filter are deferred until multi-stage atomic publication is implemented.

A whole Synth revision also rejects more than one changed Filter processor
across the compiled nested graph (not merely within a single Filter object).
This protects the currently one-processor commit path from partially updating
a live voice if a later processor were to reject a publication. Cross-filter
two-phase preflight/commit remains a separate follow-up.

## Seventh slice: full-tree atomic Filter coefficient publication

The single-stage/single-processor admission guard is replaced with an explicit
**two-phase Filter transaction**. Each realtime Filter chain now exposes a
non-mutating numerical/FIR preflight, which validates and fills its existing
invocation-owned coefficient scratch arrays. A separate non-failing commit
consumes those prepared arrays without repeating stage validation or designing
FIR taps. The older single-chain realtime-update method still calls both
phases and retains its original atomic contract.

A Filter processor batches **all changed stages** together rather than
returning after the first changed stage. Preallocated scratch is now sized
for the entire authored stage count, covering multiple stages of the same
family as well as mixed OnePole/SVF/Biquad/Ladder/AllPass/Comb/FIR changes.
Compatible FIR kernels are still prepared on the playback control worker
under bounded admission rules, never within the PCM thread.

The Synth recursively validates the complete graph and then preflights
all Filter processors and every nested Multi constituent. On a rejection
(including a sample-rate-invalid later stage), the producer leaves *all*
histories and sound references unchanged. Only after every constituent
passes does one ordered second traversal commit every prepared Filter
batch and publish compatible Multi child sound definitions. Voice phase,
envelope age, IIR delay state and FIR convolution buffers are retained.
There is no copying/forking of Filter chains during publication.

Regression coverage compares two simultaneously changed serial OnePole
stages with the independent existing FM realtime-coefficient path; verifies
simultaneous retuning of separate Filter processors across nested Multi
constituents; and verifies a later invalid Filter cutoff cannot partially
commit an earlier valid Filter change.

Remaining exclusions: edits to parameters with active FM Filter input
bindings, structural stage edits, child-source rebinding, Multi topology,
and click-safe edit smoothing. Per-voice acceptance remains independent,
and the all-properties Synth TODO remains open.

## Eighth slice: authored controls alongside active FM Filter bindings

Authoring a Filter parameter and driving that parameter through an FM input
have different semantics. A connected numeric port supplies an **absolute**
control value to the DSP; the authored value is its fallback, not an offset.
Consequently, changing a connected port's authored fallback must neither
reset the held voice nor silently overwrite its last accepted modulation.

For live migration, Filter processor comparison now checks connected stage,
parameter and compiled source routing **without comparing authored
InitialValue**. Unchanged routing and stage identities remain mandatory.
Changes to *connected* authored fallback values are DSP no-ops for that
voice; the revised definition still applies to later new notes.

For changed **unconnected** numeric parameters in a Filter with active
connections, the two-phase transaction builds effective coefficient targets
using each bound parameter's existing, accepted invocation-local
`state.Values`, and each unbound parameter's newly authored value. This
preserves live cutoff/Q/resonance/etc. controls and stateful IIR histories
while applying changes to other numeric properties of the same stage.
Subsequent FM changes continue to use the newly compiled static defaults.
The change composes with the prior whole-tree atomic cross-Filter/Multi
preflight: an invalid later target rejects the entire voice's adoption.

Only supported allocation-free numeric IIR families can retune an unbound
parameter on an actively modulated Filter. Unmodulated FIR stages elsewhere
in that Filter can still use off-thread-prepared FIR plans; an FIR stage
with its **own connected controls** cannot yet adopt a change to another
unbound FIR design parameter, because its new kernel would have to be
prepared against the invocation's actual bound-control snapshot rather
than the authored fallback. Connected-only FIR fallback edits are no-ops
and remain compatible. Unsupported slow compatibility-family retunes and
FM port rewiring still conservatively retain the previous sound for
existing notes.

PCM regressions compare authored Q changes with an independent
FM-controlled-Q reference while cutoff remains continuously modulated,
test silent edits to connected fallback and later FM retuning, test shared
Filter definition defaults without changing the Synth graph, and test
whole-voice rollback when an FM-bound later Filter has an invalid
unbound coefficient. The all-properties TODO remains open, including
bound-FIR mixed-parameter kernel preparation and smoothing.

## Ninth slice: per-voice off-thread FIR designs for mixed bound/unbound parameters

The previously deferred case of a **live FM-controlled FIR stage** whose
other non-structural cutoff is edited now uses a bounded, invocation-local
preparation path instead of falling back to the old captured Synth forever.

- A Filter with a partial FIR binding (for example a BandPass whose
  `LowCutoffHz` is driven by FM and whose `HighCutoffHz` is edited)
  captures a complete FIR control request from the voice's **last accepted
  absolute FM values**, plus the new unbound authored values. It never
  substitutes the edited authored *fallback* for a connected port.
- The request is admitted by the established FIR work/memory policy, then
  queued in a dedicated per-voice latest-wins `FirPreparationMailbox` on
  the normal FIR scheduler thread. It is independent of that same Filter's
  existing realtime FM-control mailbox: a pending authoring kernel cannot
  accidentally be applied by ordinary PCM modulation.
- The original voice, including other edited IIR stages and nested Multi
  constituents, **remains on its previous generation** until its complete
  new Synth definition can be atomically preflighted and committed.
  Prepared kernels are checked against the current accepted controls:
  stale plans are rejected, and later revisions supersede unprepared edits.
  If modulation changes while the worker runs, a new bounded snapshot can
  be offered at the next PCM control boundary.
- The already-established two-phase IIR/FIR transaction then commits all
  compatible live changes together without rewinding oscillator phase,
  envelope age, or FIR/IIR histories. FIR tap/window/FFT design, Filter
  cloning, and kernel preparation remain entirely outside the audio loop.
- The existing buffered audio host drives the per-voice work through its
  normal `QueuePendingFirControls` and `AdoptPreparedFirControls`
  boundaries. Standalone/direct output can explicitly drive the same
  `IFirPreparationAudioSource` handoff for deterministic testing.

PCM tests cover an FM-bound FIR BandPass cutoff whose other cutoff changes
while the note is held, no publication before preparation, latest-edit wins,
a connected FM value differing from the edited authored fallback, and a
simultaneous FIR+IIR change whose **entire** update waits for the FIR
worker and then matches independent branch superposition.

The per-voice pending snapshot is bounded; continually moving audio-rate
FM targets may starve a never-matching FIR design rather than allowing a
stale or mismatched kernel through. Profile that case, policy decisions for
resampling/quantizing held controls, further fallback behavior, callbacks,
allocation budgets and physical listening separately before closing the
all-properties milestone. Structural FIR modifications, FM port rewiring,
Source rebinding, Multi topology and click-safe smoothing remain open.

## Tenth slice: referenced Synth Source migration and dependent publications

A Synth Source node's bound child has an immutable original
`SoundInvocation` and mutable, invocation-local `SoundState`. Previously
the Source renderer always called `Invocation.Sound`, so even when a
parent Synth was recompiled, changing the referenced Synth's parameters
could not propagate into its ongoing child note.

- `SourceVoice.Sound` now holds the currently adopted compiled child sound,
  initialized to the invocation's original sound. Rendering, natural-end
  queries, nested FIR handoff and prepared-child end calculations use this
  adopted sound, while preserving the original bound child
  `SoundState`, `SoundInvocation.Configuration`, integrated pitch
  trajectory, note/release age and voice-owned Filter histories.
- When the old and newly prepared Source child are both `FmSynthSound`
  and their effective runtime graphs are state-compatible, the existing
  whole-tree two-phase preflight recursively checks the child before
  publishing either parent or child. FIR authoring preparation requests
  recurse through Source-bound Synths too. Commit publishes the revised
  child `FmSynthSound` only after all parent, Multi and nested Source
  stages have passed; no child invocation is reconstructed.
- This applies to a **same-SourceId child definition edit**, multiple
  levels of nested Synth Sources, and **SourceId reassignment between
  two structurally compatible Synths**. A SourceId reassignment to
  unrelated Sample/Instrument implementations is still rejected for
  existing voices, because their native `SoundState` and envelope/tone
  semantics are not interchangeable. Future notes use the newly
  selected Source. When the SourceId is unchanged but its referenced
  child Synth becomes structurally incompatible, the parent may still
  adopt independent compatible edits; the existing child remains on
  the previous captured generation until it ends.
- `SongPlaybackTransport.PublishSynthEditAsync` discovers transitive
  parent Synths that reference an edited Synth in their Source nodes.
  It prepares all affected revisions from one detached document
  snapshot, publishes child before parents through the existing ordered
  live event queue, and deduplicates each affected Synth by audio
  revision. Unrelated Synths are left alone; the audio session is not
  reopened and the frozen base snapshot's revision is unchanged.
- PCM regressions verify phase-continuous Grandchild -> Middle ->
  Parent adoption with no ancestor graph edit, compatible Synth
  SourceId reassignment, and isolation of an incompatible nested
  child while a parent's scalar gain still changes. Transport
  regression verifies direct/transitive dependent fanout,
  publication ordering, revision deduplication and session continuity.

Still deferred: migration/crossfade of structurally incompatible
Sample/Instrument child formats, adding/removing a bound child invocation,
structural changes inside Multi distributions, automatic notifications
from editors of shared Filter/Envelope objects outside the active
Synth editor, and click-safe smoothing/physical listening. The
all-nonstructural-properties TODO remains open; source dependency
publication is not a claim of generalized cross-object invalidation.

## Eleventh slice: shared Envelope/Filter editor notifications and dependency fanout

Shared Filter and Envelope definitions previously supported many compatible
held-voice parameter changes in the renderer, but edits made from their
**standalone editors** were not automatically sent through the Synth
live-publication transport. Merely incrementing a document revision cannot
identify which Synths need an updated compiled sound.

- Successful audio-affecting commits in `FilterDocumentEditor` and
  `EnvelopeDocumentEditor` now raise a
  `DocumentWorkspace.SharedAudioObjectCommitted` notification containing
  the changed object ID, active document identity and new audio revision.
  This occurs **after** the atomic authoring mutation and revision bump.
  Invalid drafts, repeat no-op edits, and node Envelope Vmin/Vmax-only
  display changes emit **no** shared-audio notification. Creating a
  currently unreferenced definition likewise does not trigger publication.
- `MainWindow` subscribes once to workspace-level committed changes
  (regardless of which editor control initiated them), delegates to the
  optional live Synth transport and unsubscribes on window close.
  Notifications from documents replaced by New/Open are ignored; no
  background audio session is opened just because a shared object changed.
- The transport now exposes
  `ILiveSynthEditTransport.PublishSharedObjectEditAsync`. Its existing
  serialized publication path discovers every Synth graph with an
  `FmEnvelopeNode.EnvelopeId` or
  `FmFilterNode.Configuration.DefinitionId` referring to the changed
  shared object; then it walks transitive `FmSoundSourceNode.SourceId`
  parent Synth dependencies. It publishes each affected Synth in
  child-before-parent order from one detached song snapshot, deduplicating
  per Synth/audio revision and skipping unrelated Synths. Playback session
  and the frozen base revision are not reset. This combines with the
  existing Source/Filter/Envelope state-compatible PCM preflight; structurally
  incompatible generations retain their old running DSP state.
- `LazySongPlaybackTransport` forwards the optional shared-object method
  **only if** the inner transport already exists. Shared edits cannot
  eagerly construct an audio backend.
- The same notification operates when shared Envelopes or Filters are
  edited through an embedded Synth inspector or its Filter popup; the
  prior Synth-local publication may also fire, but transport revision
  deduplication prevents duplicate dependent publications.
- Regression tests cover the framework-independent editor event contract
  (successful vs invalid/no-op/presentation-only edits), direct and
  transitive Envelope/Filter dependency fanout, unrelated Synth isolation,
  audio-session continuity and repeated same-revision deduplication, plus
  lazy-inner transport forwarding. Previous held-note PCM regressions
  continue to validate state-preserving adoption after publication.

**Still outstanding:** changes originating from other authoring paths
that bypass these shared editor mutation APIs, general note/Instrument
shared-object invalidation, incompatible structural changes requiring
migration or crossfade, continuous-FM FIR liveness, smoothing, stress and
physical listening. The full all-properties live-editing TODO remains open.

## Twelfth slice: bounded Multi constituent migration and 64-frame transitions

A compatible Multi node edit now migrates constituent state instead of
rejecting all layout changes other than GainCompensation. The policy is
deliberately **state-preserving and bounded**:

- Preparation precompiles the new Multi frequency-ratio array, before the
  PCM callback. Each existing constituent is identified **by index**
  and keeps its original child invocation, oscillator phase, Filter/FIR
  histories, Sample/Source position, noise seed and release state.
  Changes to Spread Detune, Harmonic or Chord component ratios, or
  Distribution with a compatible compiled child graph adjust those
  *same* constituents' future pitch.
- Ratio changes use a deterministic, allocation-free **64-frame
  smoothstep glide**. Mix gains have the same 64-frame transition.
  This includes GainCompensation changes, reductions in VoiceCount,
  changes in the number of Harmonic/Chord components, and activation
  of previously prepared constituents. The first sample of an edit
  uses the prior gain/ratio; rapid subsequent edits begin at the
  interpolated current value rather than restarting a fade from zero.
- Up to **four dormant child invocations per eligible Multi node** are
  prepared at the original note boundary. Total reserve is capped at
  sixteen across a top-level compiled Synth and limited by the unused
  portion of the 128-voice budget. No additional reserve is recursively
  created inside Multi-subgraph children. A count increase that fits
  the existing constituent array wakes unused voices with a local
  clock starting on the publication frame and a silent-to-target
  mix fade; **no SoundState allocation, child replay, reseeding or
  FIR design occurs inside the PCM edit callback**. Count increases
  exceeding prepared headroom are conservatively refused for the
  held note; new notes always use the new layout.
- Retired constituent histories **continue advancing silently**
  after their gain reaches zero. Thus an edit that reactivates one
  within the original capacity preserves its phase and Sample
  position without a rewind. This continuity policy is bounded by
  the initially prepared Multi capacity, but it intentionally does
  **not** reclaim retired child CPU during an existing note. Profile
  this tradeoff and consider off-worker migration/state fast-forward
  for larger growth and long-lived retired voices.
- RandomizePhase and CycleLength are *initial-onset* controls.
  Existing initialized constituents retain their sampled start
  offsets/cycle and are not forced through a discontinuous phase
  jump; untouched or newly activated constituents and future notes
  use the revised values. Applying a new randomized phase layout
  to already-running children requires a separate prepared
  dual-state crossfade.
- The whole-voice recursive preflight still checks every retained
  nested Multi, Source and Filter processor before **any** commit.
  An incompatible child graph or oversized count rejects the entire
  update; the running voice stays on its previous generation.
  The note's release and audible end handling excludes dormant
  constituents and completed fade-outs, so a reserved child cannot
  indefinitely prolong a finite note.

Regressions verify growth/shrink/revival with exact first-frame and
fully transitioned PCM, distribution/harmonic retune with oscillator
phase continuity, changed onset controls only affecting new notes,
nested Multi retirement and revival, an over-capacity rejection,
and live audition publication on an existing latched note. The
existing gain-compensation regression now verifies its smooth 64-frame
envelope rather than testing an undesirable instantaneous amplitude
jump.

Remaining work includes prepared growth **beyond** the bounded
reserve, new explicit identities/voice matching for arbitrarily
reordered chord components, fully audible re-randomization via
crossfade, efficient retirement of silent child histories, stress/
underrun measurements, smoothing for other Synth families and
physical listening. The all-properties milestone remains open.

## Follow-up: reserve Multi growth capacity only in live audition

Dormant Multi growth capacity is now opt-in rather than a property of
every `FmSynthSound`. The ordinary constructor defaults to
`reserveMultiHeadroom: false`; pattern/sequence playback, ordinary
ad-hoc rendering and offline export therefore allocate **only the
authored Multi constituents**, with no extra dormant child invocations.

`AdHocPlaybackRequest.ReserveSynthMultiHeadroom` defaults to false.
The interactive `SongPlaybackTransport.EnsureLiveAuditionCoreAsync`
opts into this setting when opening a live audition. The flag travels
through `PlaybackRequestAudioSourceFactory.Create`,
`PreparedIncrementalPlaybackFactory.CreateAdHoc` and
`PlaybackSnapshotSoundResolver` to the prepared root `FmSynthSound`.
Revisions built by `PrepareLiveSynthSource` also opt in so that later
audition notes can accept further compatible growth.

The optional headroom remains capped at four dormant child invocations
per eligible Multi, sixteen total per top-level Synth and the remaining
part of the 128-constituent budget. Nested Multi subgraphs do not
recursively reserve extra voices. Dormant state has only its
memory/initialization cost and skips normal per-frame DSP. Once
activated and subsequently retired, a constituent continues advancing
silently for phase-accurate revival.

Regressions distinguish default versus explicitly opted-in capacity at
the sound, playback-source-factory and transport boundaries. The
existing live Multi growth and smoothing tests now opt in rather than
depending on an always-on constructor default. All other compatible
nonstructural edit migrations still work without a reserve.

## Thirteenth slice: off-PCM Multi expansion beyond initial audition reserve

The four-constituent audition reserve is now an optimization for *immediate*
growth, not a permanent hard limit on editable held notes. Larger valid Multi
increases use the existing independent FIR preparation worker's bounded
control handoff, without reconstructing existing constituent histories or
allocating new SoundInvocations inside the audio producer.

- Live-editable Synth invocations (and nested Multi subgraph invocations)
  own one private `MultiExpansionMailbox`. Ordinary frozen playback,
  offline export and non-editable ad-hoc rendering construct **no**
  mailbox and retain their authored-only constituent counts.
- When an otherwise structurally compatible revision needs more children
  than the running invocation prepared, the PCM owner captures the
  **immutable target Synth revision**, original bound pitch/speed,
  deterministic per-voice seed and per-Multi existing capacities. Its
  captured request does **not** expose the running child's phase, PCM,
  Filter, Source, envelope or Sample history to the other thread.
- The same single `FirPreparationScheduler` thread that handles FIR
  kernels also handles immutable Multi expansion requests. It constructs
  **only the missing** child SoundInvocations and completely allocated
  replacement arrays; slots for retained children remain placeholders.
  Worker submissions are latest-wins and versioned, so an old or
  superseded prepared layout cannot publish after a newer revision.
- At a subsequent PCM block boundary, the producer checks the prepared
  result against the proposed Synth identity and original capacity,
  pitch, speed and seed. The complete recursive preflight still validates
  every Filter, Source and nested Multi before any DSP mutation.
  Only then does the producer splice the **same original child
  references** into the preallocated arrays and retune/fade-in new
  constituents through the existing 64-frame smoothstep. Original child
  oscillator phases, integrated Sample/Source positions, release state and
  FIR/IIR histories remain untouched. The newly introduced children
  begin their own local clocks on the actual acceptance frame.
- A pending expansion blocks the **entire** revision, including
  compatible upstream scalar or Filter changes, until all nested
  expansion plans are ready. If an incompatible structural change or
  insufficient capacity makes migration impossible, the held voice
  retains its previous generation; later notes use the new definition.
- Boundaries remain the Synth graph's existing valid 128-constituent
  limit and the runtime voice-state compatibility checks. Expansion
  workload may take multiple blocks; the audio producer never waits
  for the control worker, and no committed note is replayed to catch up.
  A voice can already have other pending FIR updates: both mechanisms
  share the scheduler but each keeps its own invocation-private
  mailbox and the whole Synth state must pass a common preflight.

Direct PCM regressions cover an edit from two to nine constituents,
unchanged output while preparation runs, exact smooth post-publication
output, latest-revision supersession, and recursively prepared nested
Multi growth. Playback regression checks the same deferred two-to-nine
growth on a held live-audition voice. The existing ordinary-versus-
preview tests ensure normal playback incurs no extra capacity cost.

Further work: deterministic stress/underrun and peak-memory profiling
with thousands of concurrent voices, cancellation of jobs for retired
notes, explicit constituent identity matching when a chord is reordered,
audible re-randomization via state crossfades, and physical listening.
This does not yet close the general all-properties live-editing TODO.

## Fourteenth slice: bounded worker demand, obsolete-job cancellation and diagnostics

Live Multi growth now has explicit resource-management semantics rather than
relying only on latest-wins *publication*. A superseded revision or a retired
voice no longer needs to finish constructing every unused child invocation.

- `FirPreparationScheduler` continues as the single non-PCM worker per
  buffered live producer. Its Multi-expansion admission path caps **queued
  Multi requests at 64 total queued work items** (FIR requests also occupy
  the shared queue). No audio producer waits when that queue is full:
  the versioned request remains in the owning invocation mailbox and
  retries at a later audio-block boundary. This is backpressure, **not**
  silent abandonment of a still-live requested change.
- Every `MultiExpansionMailbox` has a cooperative cancellation/version
  mechanism. Newer authoring revisions invalidate an older snapshot
  immediately; workers check that version before every node and before
  constructing each new constituent. A cut, completed, displaced/retired
  or explicitly superseded voice clears its pending and ready plans,
  releases references to frozen obsolete revisions and invalidates
  already-queued jobs. A worker already inside a single child
  construction finishes only that child, then abandons the rest.
  This cancellation does not rewind or mutate other notes' DSP histories.
- Retirement wiring is centralized at the real playback lifecycle:
  `PlaybackChannelState.CutCurrentVoice`, natural end culling,
  NNA Cut, completed NNA Off, displaced-voice retirement, and
  `PlaybackVoice`'s superseded/successfully committed revision
  boundaries. Multi and referenced child Synth state trees are
  traversed recursively; surviving NNA Continue/Off/Fade voices keep
  their work until *their own* eventual retirement.
- Off-thread metrics are readable without allocation from both
  `FirPreparationScheduler` and `BufferedAudioOutputSource`:
  number of prepared/cancelled expansion batches, accepted/abandoned
  constituents, total and peak allocated bytes, cumulative worker
  preparation wall-clock time, and peak queued work items. The existing
  buffered output's underrun count and rendering fault are retained.
  The callbacks remain ring-buffer reads with no preparation activity.
- Deterministic concurrency regressions gate the worker after exactly
  one prepared child and show that cancelling or superseding that
  invocation stops extra work without publishing stale arrays.
  A 96-voice burst verifies the queue limit and eventual completion
  of 12 surviving voices after the other 84 are cancelled.
  The playback-level lifecycle regression cuts a note after its
  queued expansion is captured and verifies no child allocation by the
  worker. A real buffered audition test consumes already-available PCM
  while six held voices request larger Multi expansions and records
  elapsed/CPU time, worker allocations and observed underruns.
- Reference measurement from **one GitHub CI runner** during the
  96-request burst: peak queued work items 64, cancelled queued
  batches 53, completed batches 12, 504 accepted prepared
  constituents, 566,880 worker-allocated bytes, largest accepted
  batch 47,240 bytes and 0.69 ms cumulative worker preparation
  time. The test's entire process consumed about 6.65 ms CPU over
  6.55 ms elapsed during that measured portion. These measurements
  are observations, **not performance thresholds or hardware
  guarantees**; no test should assert machine-specific latency,
  memory or throughput numbers.

Still outstanding: broad performance sampling on real target systems,
true sustained callback-deadline testing under contention (controlled
ring consumption is not the same thing), memory allocation budgets for
complex nested Source/FIR children, cancellation inside an individual
expensive child constructor, priority/fairness between heterogeneous
FIR and Multi jobs, and full physical listening acceptance.
The all-properties live-editing milestone remains open.

## Fifteenth slice: nested-construction cancellation and paced callback instrumentation

### Cancellation within a newly prepared child

The Multi worker previously stopped obsolete jobs between *top-level*
new children. A child may itself instantiate many nested Multi voices or
referenced Synth Sources; waiting for that entire constructor to finish
can waste substantial worker time after the sounding voice has ended.

- Worker-only `FmSynthSound.CreateInvocationForLiveExpansion` now carries
  an invocation-local cancellation predicate into recursive
  `CreateBoundState`. It checks before entering each nested Multi/Source
  node, between nested constituent invocations, after each child
  construction and after prepared DSP/source context initialization.
  It recurses through referenced `FmSynthSound` Source invocations,
  not just through Multi subgraphs.
- An obsolete snapshot aborts by throwing `OperationCanceledException`
  *inside new, private worker construction only*. The enclosing Multi
  expansion mailbox catches it **only when the request really was
  superseded/cancelled**, discards partial state, records abandoned
  allocation costs and lets the latest valid request retry. Ordinary
  sound creation, frozen/offline playback, and the PCM producer never
  enter this cancellation path.
- A deterministic worker-gated regression cancels an expansion while
  it is creating the first of sixty **nested** constituent states.
  No subsequent nested child is constructed, no obsolete plan can
  publish, no live DSP history is changed and no scheduler fault is
  recorded.
- External `ISound` implementations without the specialized Synth
  construction interface remain *atomic* child constructors: we
  check before and after their `CreateInvocation` call but cannot
  interrupt an arbitrary third-party implementation's internals.
  Likewise, any individual blocking native DSP constructor requires
  its own cooperative checkpoints to interrupt mid-operation.

### Sustained simulated callback profile

A one-second, 48-kHz, mono, 256-frame/callback paced regression
stresses eight live Synth notes requesting 64 Multi constituents
each. The observer distinguishes **callback execution duration**
from **host scheduling jitter** and **producer underruns**; it
records the longest callback, callbacks whose render duration
exceeded the 5.333-ms nominal period, callbacks started at least
one period late, actual ring underrun count, minimum buffered
frames, allocation bytes strictly around `Render`, completed
off-worker preparation and CPU/wall times.

The assertions enforce safe control behavior, finite PCM and lack
of rendering failures; **machine-specific timing numbers and
underrun counts are observations rather than portable CI acceptance
thresholds**. This simulation is not a substitute for sustained
hardware tests with real PortAudio/ALSA/CoreAudio/WASAPI device
callbacks and competing workload. For physical acceptance, measure
at several block sizes and CPU contention levels on each available
platform, including audible artifacts and both callback and worker
allocation profiles.

### Next crossfade-only sound transitions

Two classes still need explicit state-migration policy rather than
mistaking sudden state replacement for nonstructural live editing:

1. For a Multi `RandomizePhase` / `CycleLength` edit, an already
   initialized constituent currently retains its original onset.
   To *hear* revised onset parameters on sustained notes, prepare
   a **replacement constituent invocation off-thread** with the new
   phase/cycle state, then transition between old and new rendered
   contributions over a bounded smooth or equal-power window.
   Existing phase history is retained until the crossfade completes;
   a rejected or superseded candidate must leave it untouched.
2. For structurally incompatible Synth-to-Synth Source or
   Sample/Instrument reassignment, retain the old child and allow
   new notes to use the new definition. If the product should
   migrate an existing note, create a compatible, isolated new
   child with well-defined note age/position/release policy and
   crossfade the *audio* without replaying old DSP on the PCM
   producer. This requires explicit semantics per child type.

Crossfade setup and destruction must occur outside the callback,
publication must use the existing FIFO revision ordering, and
multiple overlapping edits must have deterministic latest-wins
handling. Regression acceptance should verify old-generation
continuity, finite PCM, bounded amplitude derivative, release
behavior, nested graph interactions and no on-PCM construction.
**Neither crossfade policy is claimed implemented in this slice.**

### First source-render block timing results (CI runner, diagnostic only)

A follow-up paced regression with opt-in `BufferedAudioOutputSource`
producer timing separated callback copying, OS wake-up jitter, PCM
rendering and detached Multi-state construction. At 48 kHz with
eight simultaneously sounding Synth notes, each edited from 2 to 64
Multi constituents, one successful full-suite CI run (#42562130547)
reported:

- 192 simulated callbacks of 256 frames, spanning approximately
  **1.02 s** of wall time.
- `BufferedAudioOutputSource.Render` maximum callback duration
  **0.013 ms**; **zero allocations** during the callbacks; no
  callback execution exceeded the 5.33-ms nominal block period.
- Host scheduling jitter up to **49.2 ms** and nine callback starts
  delayed by a full block interval. Thus the synthetic callback
  pacing itself is not a faithful low-jitter device host.
- **140 PCM buffer underrun events** while attempting to consume
  one second of output. Only **13,312 frames** were produced across
  52 measured PCM producer blocks in that interval.
- Source `Render` spent **1,006.1 ms** generating those blocks,
  with the longest producer block taking **43.6 ms**. **46 of 52**
  blocks exceeded their own nominal audio duration, so the
  workload was *not* capable of keeping up with nominal sample
  time on this shared CI runner.
- All eight new Multi expansions completed, using **1.695 ms** total
  of the separate preparation worker's measured time, and 521,792
  bytes of worker allocation. The shortage is therefore mainly
  sustained PCM production rather than expansion construction
  or device-facing ring copying.

The exact counts and durations vary with CI host scheduling, CPU
capacity and test contention; these are measurements, **not stable
pass/fail thresholds**. Passing the regression means finite PCM,
allocation-free device-facing callback behavior, live dependency
publication and no renderer failures—not uninterrupted realtime
output under this heavy load. Avoid converting the observed underrun
count into a required invariant. The next performance work should
profile per-constituent rendering, graph traversal, mixing and
state dispatch under controlled single-thread baselines and assess
scaling at realistic live-note/constituent counts. A listening/device
acceptance pass on supported hosts remains necessary.

Opt-in producer render timing is disabled for ordinary playback.
The callback itself still performs no new timing instrumentation
unless the diagnostic test wraps the call.

## Sixteenth slice: steady-state PCM-producer scaling profile (in progress)

The negative paced eight-note, 64-constituent CI result is not a reliable
measure of which inner DSP operation costs the most: it mixes audio work,
worker activity and shared-runner scheduling. A new non-gating regression,
`SynthProducerScalingProfileTests`, runs **the real ad-hoc playback source
on one calling thread** (not the buffered output worker) after warming up
each independent case. It measures eight successive 256-frame render
blocks at 48 kHz for direct Constant and Oscillator graphs, a nested Synth
Source, and two-, eight-, 32- and 64-constituent Multi graphs, with both
single-note and eight-held-note configurations. The 8x64 Multi baseline
is exercised with and without audition-only dormant reserve; Multi
Oscillator cases separate graph/oscillator work from Constant mixing.

The profile reports total elapsed time, microseconds per output frame
and rendering-thread managed allocations. It does **not** assert any
machine-dependent latency, allocation or underrun count, and is not
a substitute for CPU sampling/profiling or hardware deadline testing.
The existing paced test remains the sustained-callback and producer
instrumentation reference. Expected code-level cost centers to investigate
are each Multi child performing its own one-frame `ISound.Render`,
per-call Synth setup/end-frame queries, whole-graph per-sample dispatch,
and nested Source work. These are hypotheses, not profiled conclusions.
Use the CI comparison to select one isolated optimization, preserving
per-voice histories, note release/tails and audition-only headroom.
Do not close the full all-property live-edit milestone.

### Neutral-spatialization fast path and first measured comparison

A code inspection following the scaling matrix found that each one-frame
Multi child render called `Spatializer.CalculateGain` for its mono
output, whose `PositionalImportance` is exactly zero. The spatializer
was nevertheless calculating `Math.Pow(2, 0)` for *every child frame*.
For finite source positions, a zero-importance speaker's gain is provably
1.0 regardless of its position. The renderer now returns that value
before performing the expensive power calculation. Non-finite source
positions still follow the old NaN-producing path rather than being
silently masked; existing neutral-gain and new invalid-position tests
protect this behavior. This optimization is shared by all neutral
output channels but does not change Multi voice allocation, audible
phase/age, or the device-facing ring callback.

Measured comparison from two successful **Release CI** test runs on
9 November 2026 (same scaling test, independent shared-runner runs):

| Steady-state case | Before, µs/output frame (run #42617569499) | After, µs/output frame (run #42618113183) |
| --- | ---: | ---: |
| 8 direct Constant notes | 11.444 | 1.124 |
| 8 direct Oscillator notes | 6.139 | 1.610 |
| 8 nested Synth Source oscillator notes | 13.801 | 3.871 |
| 8 notes × 8 Constant Multi constituents | 37.540 | 4.909 |
| 8 notes × 32 Constant Multi constituents | 134.524 | 16.488 |
| 8 notes × 64 Constant Multi constituents | 258.540 | 32.233 |
| 8 notes × 64 Oscillator Multi constituents | 378.216 | 58.501 |

The 8x64 Constant Multi example improved roughly 8.0-fold. Its live
headroom variant went from 258.339 to 31.971 µs/output frame, confirming
no measurable per-frame headroom penalty *within these observations*.
Render-thread measured allocations remained approximately 117 KB per
2,048 frames with eight notes, independent of constituent count.

The separate paced one-second 8x64 Multi edit showed 178 underruns and
3,584 produced frames in the baseline run, versus 82 underruns and
28,160 produced frames after the fast path. Its callback Render remained
allocation-free (0 bytes) in both. The producer's longest measured block
fell from 121.877 ms to 21.750 ms. **These are shared-CI observations,
not deterministic performance guarantees or a paired scientific
benchmark.** Even after optimization, the 8x64 steady-state Constant
profile requires 32.233 µs to generate an output frame when nominal
48-kHz audio has only 20.833 µs available: sustained worst-case
throughput is still inadequate in that environment. The Oscillator Multi
case remains costlier; real device/OS profiling and additional per-child
render optimizations remain necessary. Do not write an assertion
requiring a particular number of underruns.

The all-properties milestone and crossfade-only live transitions remain
open; no physical hardware acceptance was performed.

## Seventeenth slice: specialized one-frame Multi child rendering

The steady-state scaling data showed that the remaining cost after
neutral-spatializer optimization was strongly concentrated in the
per-constituent recursive render path. A Multi constituent is always an
`FmSynthSound` invocation rendered onto an isolated one-channel,
zero-positional-importance bus. For every constituent/output frame, the
generic `ISound.Render(context, state, frame, 1, span)` route repeated
argument/shape validation, prepared-child setup, `EnsureFrame` (including
an unnecessary end-of-note graph traversal even when already sequential),
gain-array preparation and multi-channel dispatch. These costs scale
with the full live constituent count.

`FmSynthSound.RenderMultiChildFrame` is now a *private* optimized
single-frame route, used only by `EvaluateMulti`. It retains
per-invocation SoundState, Sample/Source pitch integration, oscillator
phase, Filter histories and the original absolute frame and NoteOff
semantics. It still applies the actual mono spatializer gain, including
non-finite diagnostics; checks for sample-rate/replay discontinuities;
calls `PrepareChildRender` and recomputes natural/silent boundaries
when a child discovers its Sample EOF. Only the provably redundant
`EnsureFrame` work for sequential child frames, temporary gain array,
generic output-channel loop and Span dispatch are eliminated. Randomized
start offsets, gaps and replay still traverse the original
`EnsureFrame` warm-up path. The prepared live-revision FIFO,
whole-tree compatibility preflight, off-thread Multi expansion and
audition-only reserve are unchanged.

Two new PCM regressions in `FmSynthMultiTests` compare single-pass and
irregular block-split output, sample for sample, through (a) nested Multi
and randomized oscillator phase with a Note Off boundary and (b) a
randomized Multi wrapping a finite Sample source, including its natural
EOF and integrated position. Existing Multi phase/replay, Source, Filter,
expansion and transport tests still run. Full Release CI for the test
commit **passed** in [run #42619961201](https://github.com/logiclrd/Heresy/actions/runs/42619961201).

The unchanged single-thread 48-kHz/256-frame scaling matrix reported
the following observations. Both columns are separate green Release
CI runners on 9 November 2026, not a controlled same-machine CPU
benchmark:

| Steady-state case | Prior, µs/output frame, #42618113183 | Specialized, µs/output frame, #42619961201 |
| --- | ---: | ---: |
| 8 direct Constant notes | 1.124 | 0.855 |
| 8 direct Oscillator notes | 1.610 | 1.222 |
| 8 nested Synth Source Oscillator notes | 3.871 | 3.060 |
| 8 notes × 8 Constant Multi constituents | 4.909 | 2.349 |
| 8 notes × 32 Constant Multi constituents | 16.488 | 6.919 |
| 8 notes × 64 Constant Multi constituents | 32.233 | 14.677 |
| 8 notes × 64 Oscillator Multi constituents | 58.501 | 33.209 |

The repeated 8x64 Constant profile is about 2.2x faster, and the
Oscillator case about 1.8x faster. The callback-paced 8-note 2→64
Multi edit observed 20 underruns and 44,032 produced frames over
~1.02 s, versus 82 underruns and 28,160 producer frames on the
earlier run; device callback managed allocations remained **0**.
Measured render-thread managed allocation was approximately
117 KB/2,048 frames with eight voices in both scaling runs.
Neither these counts nor the observed durations are assertions:
host scheduling, JIT/cache warm-up and multi-tenant CI contention vary.

At 48 kHz there are nominally 20.833 µs per output frame; the
steady-state 8x64 Constant profile now fits that isolated budget on
this CI run, while the Oscillator profile still exceeds it. The paced
simulation **still underruns** and is not device hardware acceptance.
Further work should profile oscillator/recursive node dispatch,
multi-constituent lifecycle/end checks, concrete UI-to-PCM property
coverage, and the still-missing dual-state crossfade transitions.
Do not mark the full Synth live-edit TODO complete or merge this PR.

## Eighteenth slice: single-oscillator graph dispatch and shared Multi pitch sampling

The prior Multi-child microbenchmark established that oscillator-heavy
graphs can be more expensive than Constant-only graphs, but the absolute
times vary greatly between shared GitHub CI runners. This bounded
follow-up reduces two forms of repeated work without changing oscillator
waveform approximations, phase integration, release lifetimes or
live-edit compatibility.

1. A compiled `FmSynthSound` with **exactly one reachable node**, an
   unmodulated output `FmOscillatorNode`, now takes a private direct
   dispatch inside `EvaluateFrame`. It calls the **same**
   `EvaluateOscillator` function and retains the original pitch validation,
   draining/NoteOff behavior, finite-value validation and raw/audible
   scratch-slot updates. Graphs with connected multiplier nodes,
   Operators, Filters, Source, Multi, or any additional reachable node
   continue through the existing generic dispatch. An authored parameter
   revision selects a matching fresh immutable compiled definition;
   running phases still belong to the unchanged `FmSynthState`.
2. `EvaluateFrame` now fetches its own `PitchTrajectory.GetMultiplier`
   **once per frame** and passes that already-fetched value to
   `EvaluateMulti`. Previously every constituent looked up the same
   outer pitch segment again, even though the parent had already
   evaluated it. The constituent pitch expression retains the exact
   prior multiplication order (`parentPitch * RatioAt * parentCurve`);
   the child's independent pitch curve, speed and state migration remain
   unchanged. The optimization applies to ordinary playback and live
   audition without adding Multi reserves to ordinary rendering.

PCM correctness:
- `SingleOscillatorMatchesGenericGraphAcrossLiveEditsPitchAndRelease`
  compares the direct path against the same oscillator through a
  generic graph with an identity Multiply. It checks all four waveforms,
  constant and relative pitch, nonzero curvature, a piecewise pitch
  trajectory, compatible live waveform/frequency/curvature adoption
  and NoteOff, sample for sample.
- `MultiSharesOuterPitchTrajectoryAcrossConstituentsWithoutChangingPhase`
  compares a two-constituent Multi against twice a standalone
  oscillator over multiple piecewise pitch changes, irregular
  block boundaries and an explicit release, guarding against stale
  pitch samples, changed phase integration and timeline shifts.
- Existing Source/Filter/Multi and callback regressions continue to
  pass. The final combined changes passed the Release suite and ARM64
  codec validation in [CI #42627430163](https://github.com/logiclrd/Heresy/actions/runs/42627430163).

**Performance qualification:** The shared hosted CI measurements are
too noisy to attribute a speedup to either optimization. An earlier
post-Multi-specialization run (#42619961201) measured 8×64 Constant
Multi at 14.677 µs/output frame and Oscillator Multi at
33.209 µs/output frame; the subsequent combined run #42627430163
measured 316.149 and 395.265 µs/output frame respectively, with
large slowdowns in unrelated graphs too. The later paced 8-voice
2→64 stress recorded 171 PCM underruns, 5,376 producer frames in
about one second and **zero callback Render allocations**. These
figures represent materially different shared host contention;
neither a regression nor an improvement can be established by
comparing the raw totals. Do **not** make performance threshold tests
from them.

Next, gather paired within-process A/B render profiles under controlled
CPU load (ideally on the available Linux/Windows development machines),
record CPU sampling data for oscillator evaluation and recursive
GetEndFrameExclusive/lifecycle calls, then choose another hotspot
based on those results. Actual audio hardware and listening tests
remain open. Crossfade-only sound transitions and the complete
all-properties live audition milestone are **not** finished.

## Nineteenth slice: paired in-process profiling and CPU-sampling procedure

The extreme spread in adjacent hosted CI runs after the single-oscillator
fast path made cross-run before/after timing ratios untrustworthy. The
next instrumentation slice adds `SynthPairedRenderProfileTests` in
`Heresy.Tests.Render`, which directly exercises the real
`FmSynthSound.Render` implementation but controls graph shape,
identity, PCM timeline, process and trial order.

- Each arm owns a fresh immutable prepared sound and four independent
  invocation histories, with 64 active Multi children per note at
  48 kHz. Both arms are warmed independently before measuring seven
  256-frame rounds in alternating A/B and B/A order. They generate
  **exactly equal PCM buffers every round**, asserted as a semantic
  invariant. Only diagnostic timing and managed allocation values
  are logged; no machine-specific performance assertion was added.
- A **Sine-versus-identical-Sine** control reveals measurement/order
  noise in the same process. Two comparisons run a fast-path
  unmodulated Sine or Square oscillator graph against the identical
  oscillator with an additional Constant(1) and identity Multiply
  on the audio path. This intentionally forces the general evaluator.
  The reference therefore performs more actual DSP work; the
  observed B/A is a graph-complexity ratio, **not an isolated
  previous-versus-current implementation speedup**.
- Logs report runtime, OS, logical CPU count, absolute median
  microseconds/output-frame, median **paired** B/A ratio, each ratio's
  min/max and managed bytes allocated on the measured render thread.
  A later test-only option `HERESY_SYNTH_PROFILE_ROUNDS` accepts
  7–512 rounds to support sustained local CPU-sampling sessions,
  while portable CI remains seven rounds. No new instrumentation or
  profiling switch was placed in production PCM paths.

### First within-process observations

The initial successfully completed Linux Release CI
[run #42712794635](https://github.com/logiclrd/Heresy/actions/runs/42712794635)
used .NET 10.0.14 on Ubuntu 26.04.1 with four reported logical CPUs.
Values are diagnostic **host-specific measurements**, not acceptance
thresholds:

| Pair | A median µs/output frame | B median µs/output frame | Median paired B/A | Range |
| --- | ---: | ---: | ---: | ---: |
| Sine versus Sine control | 161.558 | 158.833 | 0.924 | 0.572–1.044 |
| Direct Sine versus Sine + identity Multiply | 162.645 | 308.476 | 1.874 | 1.572–1.992 |
| Direct Square versus Square + identity Multiply | 15.279 | 23.013 | 1.553 | 1.506–2.729 |

Each comparison had **zero managed bytes allocated on the rendering
thread** during measured blocks and exact PCM equality. The
same-graph control was not perfectly stable. The consistent separation
between the direct and expanded graphs supports further investigation
of graph dispatch, Operator evaluation and per-node audio-value
routing; it does **not** alone identify a specific hot instruction.

A separate guide, [synth-render-profiling.md](synth-render-profiling.md),
documents Release commands, the optional longer measurement window,
Linux `perf` CPU sampling, Windows testhost process sampling, and
how to distinguish elapsed time from CPU samples. No hardware-specific
CPU profile or device listening was captured during this slice. Next,
collect representative local managed stacks on available hosts, inspect
oscillator graph dispatch and recursive lifecycle/end checks, and
choose a change only when supported by those samples. The
all-properties live-edit TODO, crossfade migration and physical
acceptance remain open. PR #12 stays draft.

## Twentieth slice: oscillator-only Multi child lifecycle fast path

The first extended local CPU profile was supplied from a Linux laptop
(Ryzen 7 7840HS, eight physical cores / 16 logical threads, 32 GiB RAM)
after a **200-round** same-process PCM profile. Its A/B medians were:
Sine/Sine control **1.004** (12.964 and 13.000 µs/output frame);
direct Sine vs identity-Multiply **1.287** (12.872 vs 16.582);
direct Square vs identity-Multiply **1.600** (8.097 vs 12.981).
All compared PCM matched; measured render-thread allocations were zero.
The test's reference has an additional graph node; it is **not**
an exact before/after comparison.

The user then recorded a JIT-symbolized Linux `perf` sampling trace.
The following were *exclusive* proportions for this mixed
Sine/Square and direct/general comparison workload:

| Sampled method | Exclusive CPU share |
| --- | ---: |
| `FmSynthSound.RenderMultiChildFrame` | **35.63%** |
| `FmSynthSound.EvaluateMulti` | 10.87% |
| `FmSynthSound.EvaluateFrame` | 5.19% |
| `FmSynthSound.EvaluateOscillator` | 4.58% |
| `FmSynthSound.EvaluateOperator` | 3.81% |
| `FmSynthSound.GetBaseEndFrameExclusive` | 2.91% |

The JIT can inline callees into a method whose **exclusive** samples
are being counted, so the 35.63% does **not** mean that method's
source-level dispatch alone consumes that share. Nevertheless the
relative concentration justified checking per-child lifecycle work
before changing oscillators, approximations or graph semantics.

### Applied lifecycle specialization

In `FmSynthSound`, only a graph with **one reachable output node,
an unmodulated oscillator**, uses the following shortcuts:

1. `RenderMultiChildFrame` skips `PrepareChildRender`'s generic
   Source traversal / mono-context setup, since the child graph has
   no Sources, Filters or nested Multi nodes. If a prepared Filter
   factory exists, its sample-rate consistency check is retained.
   Nonsequential frames still pass through `EnsureFrame` to replay
   phase history.
2. `GetBaseEndFrameExclusive` directly computes the oscillator's
   release end and updates its sole `SilentFrom[0]` audio gate,
   preserving `GetIndependentAudioReleaseEnd` (the authored
   one-frame anti-click and inherited release extension) without
   clearing and traversing a generic graph array. Unreleased
   single-oscillator graphs retain their prior indefinite behavior.
3. `EvaluateMulti` does not query
   `GetEndFrameExclusive` for **held** single-oscillator children
   without Note Off. No natural Source EOF is possible for this
   shape, so that query could not yield a finite end. On Note Off,
   including inherited release envelopes, the complete end check
   remains. All other graph shapes continue down the existing path.
4. Authored oscillator evaluation, every per-voice phase and pitch
   history, randomized Multi onset, live nonstructural revisions,
   child state reset/seek semantics, sample-rate validation and
   spatialization remain unchanged.

A new reference-PCM test
`OscillatorOnlyMultiMatchesGeneralGraphThroughRandomPhaseLiveEditAndRelease`
compares the pure child path against a graph with Constant(1) and
identity Multiply, across **all four waveforms**, Multi randomized
phase and detune, split rendering, piecewise parent pitch, nonzero
curvature, compatible live revision of waveform/frequency/curvature,
and Note Off. It asserts sample-for-sample equality and final
silence; it does **not** assert timing or any prior undesirable
behavior. Existing downstream-envelope release and sample-backed
replay/EOF tests cover the graph shapes that must retain the
general path.

The implementation and new test passed
[Release/ARM64 CI #42727771453](https://github.com/logiclrd/Heresy/actions/runs/42727771453).
This run's short seven-round *diagnostic* same-process pairs were:
Sine control B/A **0.970**, direct Sine vs identity Multiply
**2.504**, direct Square vs identity Multiply **2.609**;
measured render-thread allocations were **zero**. Different CI
runs exhibit significant CPU contention and do not provide a
controlled before/after speedup estimate. The separate paced
8-voice 2→64 expansion still observed 169 PCM underruns in this
run (device callback managed allocations zero). There is no
hardware listening acceptance claim.

The [Linux/Windows profiling guide](synth-render-profiling.md) now
records the tested .NET 10 Linux JIT-symbolization workaround
(`DOTNET_PerfMapEnabled=3` and
**profiling-only** `DOTNET_EnableWriteXorExecute=0`),
the user-space perf-event permission caution and clean
plain-text reports. To measure the actual benefit, repeat the
200-round paired profile on the same Linux laptop at the updated
branch head under comparable conditions, ideally with a local
baseline checkout or controlled A/B builds. Further live-edit
property coverage, crossfade-only transitions, device listening
and aggregate CPU budgeting remain open. Keep PR #12 draft and
unmerged.

## Twenty-first slice: isolate CPU sampling per oscillator graph shape

The user's second local `perf` run after the single-oscillator
lifecycle specialization showed exclusive samples moving:
`RenderMultiChildFrame` 35.63% → 12.57%,
`GetBaseEndFrameExclusive` 2.91% → 23.76%, and
`EvaluateOscillator` 4.58% → 8.17%.
Percentages alone are not absolute times, but the
oscillator-to-end-check ratio changed from approximately 1.57
to 0.34 (roughly 4.6-fold). A shorter total render time by
itself cannot explain that ratio shift.

**Do not immediately generalize the lifetime shortcut.** The
three ordinary profiler comparisons have different graph paths
and run sequentially in one testhost. The previous specialization
made the direct oscillator path lighter while leaving the
identity-Multiply general child path largely unchanged. A
mixed-workload `perf` report cannot distinguish a true change
in end-check work from this redistribution, JIT inlining or
sampling/host noise.

To isolate the issue, the existing
`SynthPairedRenderProfileTests` now offers an **explicit
diagnostic-only** `HERESY_SYNTH_PROFILE_CASE` selector:
`sine-direct`, `sine-generic`, `square-direct`, or
`square-generic`. Each selected run creates **one graph shape**
and only its own four voices, each with 64 Multi constituents.
It warms up that path, then renders the chosen number of
consecutive 256-frame blocks, logging duration and rendering-thread
allocation. An invalid selector fails; an unset selector preserves
the existing exact PCM paired acceptance in regular CI.
No production audio path or hardware performance invariant changes.

The updated [profiling guide](synth-render-profiling.md)
includes one-process-per-shape Linux `perf` commands and clean
per-shape flat report collection. The next step is a **local**
512-round sample for all four shapes on the same CPU under
comparable conditions. Review direct vs generic Sine first to
identify which shape owns `GetBaseEndFrameExclusive` samples,
then both Square shapes to separate waveform expense. Request
`perf annotate` for a specific method only if the per-shape
profiles remain ambiguous. The all-properties live editing,
crossfade migration and physical-device acceptance still remain
open; PR #12 must remain draft and unmerged.

## Twenty-second slice: calibrate and replicate longer instruction-level sampling

After the four isolated recordings and JIT annotation, the user
challenged the **small absolute sample count**: the annotated
`sine-direct` `GetBaseEndFrameExclusive` method had only about
**600** CPU samples while the fixture rendered
4 × 64 × 512 × 256 = **33,554,432** constituent frames. This is
normal for 499 Hz sampled CPU cycles, since it does not trace
each function call. Nevertheless, 600 observations and particularly
correlated instruction-pointer skid do **not** justify precise
instruction-level cost attribution or an asserted optimization
speedup. The method's 93% combined sample concentration near its
held/no-Source early return is suggestive, not proof of why each
instruction costs cycles.

For more reliable evidence before further source changes,
`SynthPairedRenderProfileTests` now caps
`HERESY_SYNTH_PROFILE_ROUNDS` at **65,536** in the
`HERESY_SYNTH_PROFILE_CASE` isolated test mode only. The regular
paired mode still caps at **512** rounds (seven by default),
keeps exact-PCM comparisons, and remains compatible with normal
CI runtime. The diagnostic longer path does not introduce
production-renderer changes or pass/fail speed thresholds.

The [profiling guide](synth-render-profiling.md) now documents a
two-stage experiment. First, one isolated **8,192-round
`sine-direct`** JIT-dump and `perf inject --jit` recording
with the already validated .NET 10 Linux settings, 499 Hz
userspace sampling, then inspect the
`GetBaseEndFrameExclusive` annotation's local sample count.
If at least roughly **5,000 samples** land in this method,
proceed to three independent records of both
`sine-direct` and `sine-generic` at 8,192 rounds.
If the method count is smaller, calibrate up to 16,384 or
32,768 rounds without raising sampling frequency. Record
sample counts and timings for each run; compare independently
before pooling, and avoid equating percentages with exact cycles
or attributing all skew to source-level computation. No new
lifecycle optimization is approved by these profiles alone.

PR #12 must remain draft and unmerged. Open Synth all-properties
live parameter adoption, graph-incompatible crossfades and
physical audio acceptance remain unaffected by this test-only
measurement change.

## Twenty-third slice: verified redundant held-oscillator lifetime query

The user's independent, extended Ryzen 7 7840HS Linux recordings
delivered the missing replication data: `sine-direct`
`GetBaseEndFrameExclusive` accounted for 69.8%, 63.48%, and
62.77% of exclusive CPU samples across the 8,192-round
calibration and two fresh recordings. Its instruction-level
annotation contained 9,458, 8,229, and 8,092 method samples,
with 93.21%, 94.22%, and 94.52% of those attributed to the
three nearby instructions on the early-return path. In the
independent `sine-generic` reference recordings, only 2.60%,
2.80%, and 3.06% of the total samples were attributed to
the method, versus roughly 33–36% in
`RenderMultiChildFrame`. These figures are attribution,
**not** calibrated absolute instruction-cycle costs, and
the disproportionate JIT-method attribution remains
unexplained. Repeating the same sampling more times cannot
remove instruction-pointer skid.

The direct Sine recordings returned approximately
**11.667–11.756 µs/output frame**, while the three generic
identity-Multiply reference runs returned
**16.630–16.730 µs/output frame**, but these are profiled,
independently run **different graph shapes**, not a controlled
before/after performance estimate. The user supplied the
`*.log`, `*.report.txt` and `*.annotate.txt` records
as an archived dataset. They corroborate that the repeated
early-return work is a worthwhile optimization candidate.

After examining this evidence, the production change is
intentionally one conditional bypass in
`FmSynthSound.RenderMultiChildFrame`: when the immutable
compiled sound is a **single unmodulated oscillator** and
the child has **no Note Off**, do not call
`GetBaseEndFrameExclusive`; assign `baseEnd = null`
directly. The skipped method would return exactly `null`
without mutating the child state. It continues to run for
Note Off (and inherited external release), or any other graph
shape, while nonsequential calls still execute the established
`EnsureFrame` phase catch-up path. This does **not**
generalize the shortcut to Operator/Source/Filter graphs.

The existing
`OscillatorOnlyMultiMatchesGeneralGraphThroughRandomPhaseLiveEditAndRelease`
exact-PCM regression has been expanded to keep both paths
unreleased across several post-publication blocks, skip 14
outer frames (exercising `EnsureFrame`), then set Note Off
and compare the rest of the split render. The
`DownstreamEnvelopeReleaseKeepsWrappedOscillatorAudible`
regression now starts held for three frames before
Note Off, proving that the nested single oscillator inherits
the downstream Multiply envelope's authored release rather
than truncating immediately. All four oscillator waveform
variants, phase randomization, detune, pitch trajectories,
compatible live edits and finite silence checks remain
covered. No test asserts a hardware speed threshold.

The [profiling guide](synth-render-profiling.md) includes
an uninstrumented, same-graph **8,192-round** before/after
protocol with two independent Release builds (prior
`792cea4` versus the PR head), alternating repeated runs
and logging per-run duration and managed render-thread
allocations. The user must run this locally for a meaningful
Ryzen speedup estimate. Short shared-host CI timing and
additional JIT-sampling percentages must not be presented
as a verified speedup.

PR #12 stays draft and unmerged. Broader real-time
Synth property coverage, sound-generation crossfade
transitions, and physical-device/listening acceptance
remain unfinished.

## Twenty-fourth slice: Ryzen before/after disproves a large runtime win

The user ran the prescribed uninstrumented **8192-round, same Sine-direct
graph** benchmark in alternating `before/after/after/before/
before/after/after/before` order, using separate Release builds:
`before` = commit `792cea4` before the
`RenderMultiChildFrame` lifetime-call bypass;
`after` = PR head including the bypass. Every test reported
`render-thread allocated bytes=0` and the **identical**
`PCM probe=-419683.9955374971`. The actual per-frame
durations (microseconds):

| Run | Version | µs/output frame |
| --- | --- | ---: |
| 1 | before | 10.742 |
| 2 | after | 10.715 |
| 3 | after | 10.708 |
| 4 | before | 10.670 |
| 5 | before | 10.644 |
| 6 | after | 10.642 |
| 7 | after | 10.865 |
| 8 | before | 10.776 |

Before: mean **10.708**, median **10.706**, range
**10.644–10.776** µs/frame. After: mean **10.7325**,
median **10.7115**, range **10.642–10.865** µs/frame.
The median after-minus-before difference is
**+0.0055 µs/frame (+0.051%)**; mean difference
**+0.0245 µs/frame (+0.229%)**. Both are smaller
than run-to-run dispersion. **No measurable improvement
or meaningful regression is established.**

This is a particularly important counterexample: preceding
isolated `perf` runs reproducibly attributed roughly
**63–70% of exclusive sampled cycles** to the held
`GetBaseEndFrameExclusive` early-return method, yet removing
that call showed no practical throughput improvement.
The previous high sampling percentages **must not be cited
as a validated time contribution or projected speedup**.
Possible explanations include misleading JIT instruction
attribution, cost migration among memory accesses/generated
instructions, or an incorrect/stale after build (not yet
excluded independently). The identical probe is a
correctness consistency signal, not proof that both intended
binaries were used, nor proof of complete PCM equality.

Next diagnostic, **before changing production code again**:
verify the after checkout SHA and Release rebuild, then capture
one fresh isolated `sine-direct` after JIT-perf recording at
8,192 rounds with exactly the baseline symbolization settings.
Check whether the method's share disappears as predicted.
If it does, the hotspot was removed from the profiler but
not from elapsed time; re-examine overall attribution and
retire any assumption that percentage corresponds to avoidable
runtime. If it remains high, investigate call sites and
whether the expected executable was profiled. Consider
`perf stat` cycles/instructions for both binaries as an
independent counter-based comparison if the result remains
ambiguous. Keep the one-line bypass pending evidence; do
not broaden this shortcut into additional graph shapes
on the assumption of speedup. No speedup threshold belongs
in CI. PR #12 remains draft and unmerged.

## Twenty-fifth slice: after-bypass JIT profile confirms attributed hotspot relocation

The user rebuilt the current feature-branch Release solution after
pulling `9177bac..5f20f3d`. The `dotnet build -c Release`
succeeded, and the isolated 8,192-round `sine-direct`
`.NET 10.0.14` test completed successfully on Ubuntu 26.04
with `render-thread allocated bytes=0` and the same
`PCM probe=-419683.9955374971`. The JIT-symbolized
`perf record` at `-F 499 -e cycles:u` captured
**12,622 samples**; the flat, exclusive report showed:

- **78.88%** attributed to `RenderMultiChildFrame`
  (`OptimizedTier1`), approximately 9,956 samples.
- **4.12%** attributed to `EvaluateMulti` (`OptimizedTier1`).
- **3.27%** attributed to `EvaluateOscillator`
  (`OptimizedTier1`).
- `GetBaseEndFrameExclusive` **did not appear among the
  displayed symbol matches** (consistent with the eliminated
  held-child invocation). Do not claim this proves no calls
  whatsoever from other paths.

Before the bypass, 63–70% of sampled cycles were attributed
to the callee `GetBaseEndFrameExclusive` and roughly 6–7%
to `RenderMultiChildFrame`; after, the dominant hotspot
became the caller. Crucially, the alternating,
**uninstrumented** A/B timing comparison remained
essentially tied (before 10.706 versus after 10.7115
µs/output frame median; same PCM probe, zero managed
render-thread allocations). The fresh Release rebuild
rules out the plausible stale-binary explanation.

**Conclusion:** the source-level redundant call was
successfully bypassed on the held single-oscillator path,
but its earlier method-local `perf` attribution was
a poor predictor of avoidable runtime. The relocation
can arise from JIT-generated/inlined code, sampled
instruction-pointer precision/skid, and the cost of
shared memory accesses or surrounding operations; the
available data do not distinguish those mechanisms.
Do not claim a speedup or describe the before-profile
sample share as literal time saved.

If further diagnosis is useful, **do not rerun the
long benchmark yet**: use the existing
`synth-perf/after-bypass-injected.perf.data` with
`perf annotate --stdio --no-source` and examine the
hot instructions inside `RenderMultiChildFrame`.
This determines whether samples have moved to the
same nullable Note Off check now performed by the
caller or elsewhere in its compiled frame path.
Counter-based `perf stat` comparisons are optional.
The no-speedup special-case remains in the draft PR
pending engineering judgment; avoid generalizing
it to other graphs. No further optimization is
authorized by high exclusive sample percentages alone.

## Twenty-sixth slice: direct nullable presence accessor experiment

The user observed that the JIT-annotated hot path used
GNU/AT&T `vmovups` to copy the entire 16-byte nullable
`TimeSpan?` into a stack temporary before testing
`HasValue`. In the post-bypass `RenderMultiChildFrame`
profile (9,641 local samples), **59.30%** were attributed
to the store at `+0xbd` and another **35.35%** to
the adjacent comparison/branch. This strongly resembles
the sampled instruction distribution previously seen
inside `GetBaseEndFrameExclusive`; it does **not**
prove actual instruction-cycle cost or that misalignment
caused it. Nullable `TimeSpan?` already contains a
separate flag; no sentinel tick value is involved.
Ordinary unaligned moves are not necessarily slower
than aligned moves if they do not cross problematic
boundaries. The user confirmed that the host's
`perf list` includes `ibs_op` and `ibs_fetch`,
offering optional precise sampling later.

To isolate whether copying the nullable value is
avoidable by changing the *access pattern*, this
slice adds an **internal derived property**
`SoundState.HasNoteOffTime => _noteOffTime.HasValue`,
leaving the private nullable field and existing public
`NoteOffTime` getter/setter unchanged. The
single-output-oscillator held-path in
`FmSynthSound.RenderMultiChildFrame` uses
`!child.HasNoteOffTime` in place of
`child.NoteOffTime is null`. No second flag is
stored; validation, release transitions, other graph
paths, and existing PCM behavior remain unchanged.
The JIT is free to inline that accessor and use a
direct one-byte test, but this must be observed rather
than assumed.

Prior exact-PCM regressions include four waveforms,
live compatible edits, held blocks, a 14-frame catch-up
gap, downstream inherited release, and non-null Note Off.
They are preserved. No performance number becomes
a CI assertion. This change has **no measured speedup**
yet. The profiling guide specifies a controlled
uninstrumented `8192`-round, same-Sine-direct
comparison between old checked-nullable bypass
commit **`a925705`** and the candidate, in
alternating independent runs, then a new
`perf record` / `inject --jit` /
`annotate` to inspect whether the 16-byte copy was
removed. AMD IBS can be explored if skid
continues to obscure instruction attribution.
PR #12 remains **draft/unmerged**.

## Twenty-seventh slice: precise IBS Op sampling contradicts ordinary instruction hotspots

The user's Ryzen 7 7840HS exposes `ibs_op`/`ibs_fetch`;
`perf record -e ibs_op//` initially failed because this
Ubuntu kernel requires system-wide/per-CPU monitoring.
The working experiment used `-a -C 4`, a pinned
`taskset -c 4` testhost, userspace software filtering
`-e ibs_op/swfilt=1/u`, `-c 100000`, JIT-dump
symbols and DWARF call graphs. System-wide IBS required
a temporary `kernel.perf_event_paranoid` adjustment;
the original setting should be restored afterward.

The result was an unexpectedly large **~9.7 GB**
`perf.data` recording and an additional ~9.7 GB
injected copy. The archived textual report was ~32 MB,
the annotation ~219 KB, and `tar.xz` compressed the
two to ~215 KB. The report recorded approximately
**1.15 million IBS samples**, with **4,790 lost**
(roughly 0.4%). The IBS flat report differed
dramatically from the earlier ordinary
`cycles:u` JIT-perf run on the accessor build:

| Method | Ordinary `cycles:u` share | Precise IBS Op share |
| --- | ---: | ---: |
| `__sin_fma` | 49.74% | 8.74% |
| `EvaluateOscillator` | 14.11% | 23.06% |
| `RenderMultiChildFrame` | 7.89% | 20.59% |
| `EvaluateFrame` | 10.57% | 18.37% |
| `EvaluateMulti` | 5.62% | 14.46% |

These are **distinct sampling methods** and their
percentages must not be compared as identical measures
of elapsed cycles or instruction latency. IBS precisely
identifies tagged/sampled instructions without ordinary
PMU IP skid; its default IBS Op configuration counts
cycles between selected operations, not necessarily
weighted time lost to each selected operation.
Deterministic fixed-period sampling of a highly
repetitive signal could introduce periodic selection
bias even with accurate IPs.

In the 100,514-sample `libm` annotation for the
region identified by the report as `__sin_fma`,
the earlier apparent ~79% single-instruction
concentration on the `mov` immediately following
`vstmxcsr` **did not reproduce**. Instead,
`vstmxcsr -0x18(%rbp)` had **5.92%** of the
libm-local IBS samples, while the dependent
`mov -0x18(%rbp),%edx` had **2.01%**,
with nearby instructions also receiving samples.
This is consistent with the previous large
post-`vstmxcsr` sample share being affected by
ordinary sampling skid; it does not rule out a
real floating-point-state dependency latency.

In the 236,834-sample JIT `RenderMultiChildFrame`
annotation, the internal
`HasNoteOffTime` check compiled to direct
`cmpb $0x0,0x48(%r13)` (**0.65%** local)
followed by `jne` (**0.68%** local): no
16-byte nullable stack copy. This corroborates
the previous JIT verification. Compared with
the original >90% local clustering around a
nullable copy and branch, IBS sampling distributes
work across the rest of the frame and oscillator
pipeline.

The eight **uninstrumented** same-graph accessor
A/B measurements were also supplied: old checked
nullable bypass: `10.744, 11.006, 10.779, 14.098`
µs/output frame; direct-accessor candidate:
`10.708, 10.685, 10.572, 10.673`.
All reported zero render-thread allocations and
the same PCM probe. Before median was 10.8925,
after median 10.679 (1.96% smaller), but the
14.098 before outlier heavily affects this
estimate. Excluding it, the before median is
10.779 (~0.93% greater than after). This is
**promising but not sufficient** to claim a
reproducible speedup of a specific magnitude.

The major lesson is to prefer **uninstrumented
controlled before/after throughput** for performance
claims and treat ordinary `cycles:u` JIT instruction
attribution as a debugging clue, not a cost breakdown.
IBS reduces skid but sampled-op proportions alone
still are not instruction latency. Further optional
counter work should avoid multi-GB recordings:
increase the IBS period and drop DWARF call graphs
where an exclusive instruction histogram suffices.
No further code changes, optimizations, or merges
are justified solely by this IBS profile.
PR #12 remains draft and unmerged.

## Twenty-eighth slice: preserve authored scalar timing, expand nonstructural PCM acceptance

After concluding the Ryzen profiling investigation, development
returned to the unfinished all-properties Synth audition milestone.
A first prototype attempted 64-frame per-voice smoothstep
transitions for every live `FmConstantNode.Value` change.
It preallocated transition state per node and started rapid
successive ramps at the current interpolated value. That
prototype is **reverted**: CI revealed it violated several
important existing exact-output invariants, including
instantaneous authored Constant gain publication on held
notes, preserved Source child audio at the publication frame,
and exact comparison of authored Filter coefficient edits
against the equivalent realtime-FM retuning of the same
IIR histories. Those are real behavioral contracts, **not**
undesirable assertions to rewrite merely to pass the suite.
The prototype's replacement of existing expected values
was also reverted. No global implicit Constant smoothing
remains in production.

The engineering consequence is that *click-safe smoothing*
should be a separate, explicit output/transition mechanism
with well-defined opt-in/audition semantics, not an implicit
change to all Constant control values (which can route
directly into oscillator FM or Filter coefficients).
The existing 64-frame Multi voice gain/ratio glides retain
their previously specified semantics. Incompatible
Source/Multi onset changes still need dedicated prepared
dual-state crossfades; this slice does **not** claim that
capability.

Instead, this slice extends correctness coverage of the
functional publication path, keeping the already tested
immediate scalar semantics:

- New held Constant regressions exercise two rapid
  compatible revisions on the same voice without NoteOn,
  and verify no old revision leaks into later samples.
- A second regression checks that a Constant change
  propagates to the retained child invocations inside a
  two-voice Multi graph, with publication on the first
  resumed output frame.
- Expanded the single-oscillator-versus-generic-graph
  exact-PCM live-edit regression across changes to the
  *full unbound Oscillator inspector scalar set*: waveform,
  frequency, curvature, Vmin/Vmax, Constant (absolute
  frequency mode), and the unbound Exponential multiplier
  interpretation flag (inert without an FM wire).
  The old/new configurations use the same node wiring,
  including Note Off and held pitch-trajectory history.
- A separate connected FM port regression changes
  Exponential multiplier interpretation to Linear while
  keeping the same connected source, verifying the first
  edited frame's phase continuity and the next sample's
  changed phase increment.

These are functional tests, not a claim that all properties
are implemented or smooth. They do not assert wall-clock
performance or change the native renderer's PCM semantics.
The all-property TODO, explicit click-safe transition
design, Source reassignment and Multi re-randomization
remain outstanding; PR #12 stays draft and unmerged.

## Twenty-ninth slice: first prepared Multi onset crossfade for held oscillators

This slice begins the previously deferred audible adoption of
`FmMultiNode.RandomizePhase` and `CycleLength` on **already initialized
live-audition** constituents. It deliberately limits the first sound
transition to **held** notes containing a **single unmodulated oscillator**
as each Multi child, with **unchanged frequency ratios and identical
oscillator properties** across the edit. It is not yet the general
nested/Sample/Filter child transition architecture.

Previously, an initialized child always retained its old skip/cycle,
while subsequent fresh notes followed the edited onset. That existing
policy is preserved for ordinary playback and for graphs outside this
first eligible live-audition shape. On an eligible edit, the owner now
marks the whole running-voice revision **pending**, instead of claiming
immediate successful migration without updating the onset.

- The existing bounded, per-voice, latest-wins Multi-expansion mailbox
  snapshots whether each Multi needs onset replacements at its PCM
  owner boundary. The worker reads only the **frozen next definition**,
  saved scalar seed/pitch/speed and captured capacity/eligibility
  flags—not live `FmSynthState` or active oscillator arrays.
- The same scheduler's cancellable worker constructs detached child
  `SoundInvocation` instances for the eligible existing constituents
  in addition to any expansion children. This is done **off the PCM
  producer**, without changing the outgoing children. Cancellation,
  seed identity, revision matches and worker admission remain shared
  with normal Multi expansion.
- The nonallocating onset calculation has been extracted into
  `InitializeMultiOnset` so both initial notes and prepared rephase
  candidates use identical cycle rounding, deterministic hash and
  skip-frame/fade rules. Likewise `MultiTransitionWeight` unifies
  the established 64-frame smoothstep used by Multi gain/ratio glides
  and the new dual-state crossfade.
- After the existing whole-tree preflight passes, the PCM owner
  attaches the **already constructed** child invocations at the
  ordered publication boundary. For this identical unmodulated
  oscillator shape only, it derives the replacement phase from the
  outgoing oscillator's next phase and the difference between old/new
  seeded skip offsets. The replacement's `NextFrame` is set to the
  corresponding local frame, so **no unbounded history replay or
  new SoundState construction occurs in the PCM callback**.
- Over the next **64 outer frames**, each affected Multi child
  renders both the outgoing and incoming state, with a bounded
  smoothstep mix. The outgoing child's oscillator phase continues
  advancing independently. The first edited frame uses only the
  outgoing value, then the old state is released from the active
  reference once the fade reaches full weight.
- A subsequent onset edit arriving while an earlier fade is still
  active can be worker-prepared, but is not published until the
  previous 64-frame fade completes. This avoids an unbounded
  incoming/outgoing state chain. A newer authored revision supersedes
  pending worker plans using the existing mailbox versioning.
  Non-onset scalar, ratio, Filter and Source updates retain their
  previous admission behavior unless independently incompatible.
- The initial scope does **not** reinterpret pitch-history changes
  accumulated before the publication frame. The rephase delta is
  defined using the oscillator's instantaneous prepared pitch at
  publication. Rebuilding a full historic pitch/phase trajectory
  off-worker is explicitly deferred to a later, more generalized
  prepared-state transition; active phases and original note/release
  lifetimes are retained wherever this narrow scope permits.

Regressions assert deterministic seeded/cycle onset across split blocks,
worker admission instead of immediate held-note publication, identical
first edited PCM sample, genuinely changed audible phase after the
64-frame window, zero failed worker preparations, and a second
onset revision waiting for an existing fade rather than allocating
a chain. Existing unrelated Multi growth and release tests remain
mandatory invariants; *no* timing/underrun thresholds are asserted.

**Outstanding for the next slices:** complete stateful Sample/Instrument,
Source and Filter child crossfades, arbitrary nested Multi graphs, ratio
and onset changes in the same publication, edited notes already in
release, phase positioning under complex pitch trajectories, prepared
replay semantics for non-oscillator descendants, state lifetime/CPU
profiling under simultaneous transitions, all-property UI-driven tests
and actual physical listening. Keep the full live audition TODO open
and PR #12 **draft/unmerged**.

## Thirtieth slice: Multi crossfade child lifetime and independent oscillator subgraphs

The first prepared onset crossfade (§29) used one unmodulated
output oscillator per Multi constituent. This slice extends the
**same bounded audition-only mechanism** to a first wider set
of safely migratable child graphs: one or more independent,
unmodulated oscillators feeding unchanged stateless
`FmOperatorNode` combinations, optionally through
unchanged `FmConstantNode` values.

An eligibility check, `CanRephaseOscillatorGraph`, compares
the fully compiled child graphs' node order/identities/types,
wiring, output identity, oscillator indices, constants,
operator kinds and **every authored oscillator parameter**.
All oscillator `MultiplierNodeId` ports must be unbound.
It rejects Source, Sample, Filter, Envelope, Noise, nested
Multi and any FM-modulated oscillator child. Such
ineligible graphs retain the established policy of keeping
an initialized constituent's previous onset until the
proper state-replay/crossfade policy is available; new
notes always use the edited definition. This is **not**
a general stateful Source/Filter migration.

For eligible graphs, the already-cancellable off-PCM worker
still creates the detached child invocations. At ordered
publication, `AttachPreparedOscillatorOnset` copies
the outgoing phase of **each oscillator index**, applying
only the new-minus-old deterministic skip-frame delta
at that oscillator's own fixed or note-relative
instantaneous frequency. Stateless Constant/Operator
scratch values are recomputed on the next sample;
nothing is replayed or reconstructed in the PCM loop.
The incoming state reuses the parent's **already prepared
mono RenderContext**: unlike the single-oscillator fast
path, generic oscillator/Operator graphs would otherwise
perform a lazy RenderContext allocation on the first
replaced PCM sample. A regression now checks **zero
managed render-thread allocations** for the complete
prepared crossfade.

Release and state-lifetime bookkeeping also change:
`GetMultiSilenceEnd` takes the maximum of the
still-audible **incoming AND outgoing** child ends
while the 64-frame crossfade is unfinished, rather
than prematurely dropping the old child's end.
If Note Off makes *both* children definitively
silent before the transition window ends, the
outgoing state reference is cleared as soon as this
can be established. The old reference also clears
at normal fade completion; fresh seed/reset clears
stale fade pointers. The outgoing child is never
discarded just because a new phase is prepared,
and neither child can independently lengthen the
original note's audible release without an authored
tail or inherited release envelope.

New tests cover:
- A live rephase on a Multi wrapping two independent
  oscillators, a constant and Add/Multiply operators,
  including exact continuation on the first publication
  frame, audible new onset after 64 frames, and two
  worker-prepared replacement invocations.
- **Zero managed allocations** across the realtime
  PCM Render call during that state transition.
- **Note Off at frame 96 during a fade begun at 80**:
  both original and replacement children end at the
  original exclusive frame 97, even though the
  64-frame crossfade would normally finish at 144.
- A Multi whose oscillator has a connected FM multiplier:
  its initialized held state must remain unchanged
  through the edit, and no unsupported rephase worker
  construction occurs.

These invariants are functional, not performance-time
thresholds. The single-output-oscillator crossfade,
Multi growth/release, and related PCM tests still apply.
The transition still uses the phase-delta approximation
at the instant of publication, not a reconstruction of
historic pitch changes across arbitrary skipped frames.
It does not yet support Filter histories, Envelope
release trajectories, Sample/Source playback position,
nested stateful Multi nodes, simultaneous ratio and
onset rephase, or a released note *starting* a new
transition. Physical listening and the full all-properties
UI/PCM acceptance remain outstanding.

PR #12 stays draft and unmerged. The feature is not
claimed complete.

## Thirty-first slice: worker-prepared OnePole IIR histories during live Multi onset fades

The previous 64-frame Multi onset transition supported only independent
unmodulated oscillator phases, Constants, and stateless Operators. The
first genuinely history-bearing descendant is now supported:
**unmodulated, unchanged OnePole IIR Filter chains**, including
multiple serial OnePole stages, on **held Synth-preview** Multi notes.
Sources, Samples, FIR, SVF, Biquad, Ladder, Comb, Noise, Envelopes,
FM-modulated Filter inputs, nested Multi and any structural changes
are intentionally **not** covered by this migration.

The explicit eligibility guard in `CanRephaseOscillatorGraph`
requires identical child node indices/identities/wiring, oscillator
and Constant parameters, Operator kinds, and Filter tail allowances.
For every Filter it delegates to
`FmFilterNodeProcessor.CanRephasePreparedOnePoleHistoryTo`,
which accepts only unchanged, unbound OnePole chains with matching
shared definition, stage IDs/generations, CutoffHz and structural Mode.
This is a *live onset-policy* edit, not a generic Filter parameter
retune: actual Filter property changes still follow the existing
separate atomic live-edit mechanism.

The bounded latest-wins Multi preparation mailbox now also captures
the live owner's **sample rate** as immutable scalar preparation data,
including it in candidate matches. When the worker builds a detached
replacement child invocation for a rephase, it constructs
the child's entire OnePole Filter `State` and `StreamingFilterChain`
at that sample rate, outside the PCM callback. The worker never reads
active DSP histories.

At the ordered publication boundary, the parent first checks
**every** active child through
`CanTransferPreparedOnsetHistoriesTo`.
For every OnePole chain, the already allocated destination stages
must match topology, type/generation, precise parameters, sample rate,
channel count and remaining lifecycle. The existing
`StreamingFilterChain.CanCopyPreparedOnePoleHistoryFrom` uses
nonallocating direct OnePole parameter lookups rather than LINQ-based
per-stage comparisons. When all child preflights succeed,
`AttachPreparedOscillatorOnset` transfers each independent oscillator
phase as before **and** copies each OnePole stage's active integrator
and realtime coefficient into its own separately allocated stage state.
There is no Filter clone, initial FIR design, history reconstruction or
skipped-frame replay in the PCM callback. Outgoing and incoming
OnePole stages then evolve independently through the same 64-frame
smoothstep crossfade. Per-voice note lifetimes, tail budgets, and
outgoing reference retirement use the §30 two-state logic.

The copy is a conscious boundary-continuation policy: it preserves
the Filter's audible current history, while the *incoming oscillator*
adopts the new phase origin; it does not claim that the Filter had
actually processed a phase-shifted input over all past frames.
Higher-order/non-IIR history and Sample/Source position reconstruction
remain separate work.

Added functional regressions:
- A **one-frame cycle length** makes the old and new phase skips
  exactly equal. With a nonzero accumulated OnePole integrator,
  the worker-prepared crossfade must output **exactly identical PCM**
  to the continuous old Filter across 160 frames. This detects both
  history resets and inadvertent state sharing. The PCM render call
  must allocate **zero managed bytes**.
- A randomized onset with a real offset and a OnePole stage must keep
  the exact old first PCM sample, then become audibly different after
  the 64-frame fade; worker preparation must finish without faults.
- A graph whose OnePole Filter has a bound FM input remains
  *bit-identically unchanged before publication*, whether or not
  preparation is admitted in a future generalized implementation.
  This tests the durable isolation invariant, not the current lack
  of a particular history migration.
- A Note Off inside the crossfade with an authored 96-frame OnePole
  tail must retain the original exclusive end and yield zero output
  after that end; neither incoming nor outgoing history can prolong
  or truncate the shared lifetime.

The zero-PCM-allocation invariant applies to **rendering** the prepared
transition; initial Filter state construction runs only on the worker.
The existing live-publication architecture and held-state safety checks
remain in force. Broader stateful transitions require a separate
prepared replay/snapshot policy for FM histories, FIR buffers,
Source/Sample playback positions and nested Multi. All-property
audition completion and physical listening remain TODOs.

PR #12 stays **draft and unmerged**.

## Thirty-second slice: direct Sample source playheads in live Multi onset crossfades

The first Source/Sample milestone extends the bounded 64-frame
off-thread-prepared Multi onset transition to a tightly specified
**direct Sample-backed Source node**. This is a different kind of
state migration from oscillator phase and OnePole integrators:
SampleSound's position is a sub-frame **integral of its source
pitch curve**, not just the most recently requested frequency.
It cannot be reset, copied as a nominal output-frame index, or
replayed from the beginning inside the PCM callback.

**Eligible shape and assumptions**

- A held, live-audition Multi note with unchanged constituent ratios,
  whose child graph contains identical direct
  `FmSoundSourceNode` Source nodes. Either both revisions resolve
  the **same SampleSound instance**, or their distinct wrappers use
  `IImmutableSamplePcmIdentity` adapters exposing the **same frozen
  SamplePcmData object**. Definition ID, loop, sample rate,
  channel count, frame count and channel positions must also match.
  Arbitrary ISampleData providers are not presumed compatible.
- Source ID, authored frequency, Constant mode, Exponential mode
  and absence of a connected multiplier must match. Mono/stereo
  bare Samples are allowed; Instrument selections, Source envelopes,
  Source tone Filters, and other ISound types remain excluded.
  The graph may still contain compatible independent Oscillators,
  Constants, unbound OnePole IIR stages and stateless Operators.
- Each actively sounding Source pitch curve must have a single,
  **constant positive finite rate over its lifetime**. A history
  with any previously accepted rate change is not approximated
  as a constant. The Source must be sequential, not past a known
  natural EOF, and not released; the parent must remain held.
- The new/old deterministic Multi skip displacement is bounded.
  The same prepared worker mailbox, latest-wins cancellation,
  whole-voice preflight and no-overlapping-fades gates apply.

The production `PlaybackRequestAudioSourceFactory.PrepareLiveSynthSource`
constructs a new `PlaybackSnapshotSoundResolver` for each revision.
That resolver also constructs a new `SampleSound` and
`InMemorySampleDataProvider.Adapter` for the sample. A pure
`ReferenceEquals(SampleSound)` gate would incorrectly reject
real editor-preview revisions. `SongDocumentSnapshot.Create`
shares the immutable `SamplePcmData` object across snapshots,
and `InMemorySampleDataProvider.Adapter` now declares precisely
that data identity. This prevents false matches between custom
providers whose IDs and format metadata happen to agree but whose
readable audio is different.

**State transfer**

`FmSourcePitchCurve` tracks whether its integrated rate has ever
changed (a single flag checked on each chronological `SetFrame`),
and exposes the last accepted source frame/rate. The Sample's
`PitchTrajectory` can seed the cache of its sole existing segment
at a chosen logical frame and accumulated position in O(1),
without allocating another segment. A worker constructs an
entirely independent new Sample invocation and `FmSourcePitchCurve`
before acceptance; the worker never reads playing DSP state.

At the ordered PCM publication boundary, the preflight validates
every active child and its ready Source destination, including its
sequential last frame, one-segment trajectory, original pitch scale,
unmodified note configuration, release and natural-end states.
The already-prepared recipient is positioned at:

`newPosition = oldTrajectory.GetPosition(oldFrame)
               + (newSkip - oldSkip) * constantSourceRate`

The original accumulated position is queried one sequential frame
ahead of its prior cache; the *constant-rate* precondition makes
the offset delta exact in this supported shape. The new Sample
inherits its fixed original pitch/speed scale, source-frame offset,
playback offset and spatial position. Its preallocated trajectory
receives `newPosition`, and its `FmSourcePitchCurve` is seeded
immediately before the revised local frame so future frames
continue integrating normally without reading any old historical
frames. Outgoing and incoming Sample interpolators thereafter
evolve independently through the 64-frame smoothstep fade.
The first publication frame remains 100% the outgoing Sample.

**Stale/unsupported history policy**

A played Source that has already changed its rate or reached
its known end cannot use this O(1) shortcut. Such onsets retain
their captured phase instead of stalling the entire Synth revision
forever: compatible scalar edits can still publish. This is not
a claim that these general Source histories are fundamentally
impossible; they require a prepared historical snapshot/replay
or a different time-warp migration contract.

**Regression contracts**

- A one-frame randomized cycle (equal old/new skips) retains
  **exact PCM** across 160 frames after the worker-prepared
  migration, despite complex interpolated PCM source data.
  The PCM `Render` call allocates **zero managed bytes**.
- Another test constructs distinct frozen SampleDefinition and
  SampleSound wrappers with the same underlying immutable
  SamplePcmData but separate provider adapters; a direct-Sample
  live onset must still prepare and preserve exact PCM.
  The production snapshot resolver uses the same identity contract.
- A second 3-voice Sample case changes a real seeded phase
  offset, is exactly unchanged on the first publication sample,
  becomes audibly different after the 64-frame fade and keeps
  all samples finite.
- A prior parent-pitch change causes a nonconstant Sample
  Source trajectory; a simultaneous, compatible authored
  Constant change outside the Multi must still publish.
  This protects the durable *no starvation of independent
  scalar edits* invariant, not a refusal of future full
  Source-history support.

**Still excluded / next work:** variable-rate integrated Sample
positions and user-controlled seeks, full Instrument-selected
Sample/Envelope/pan histories, Source assignment changes and
crossfading two different source definitions, stereo tone-Filter
histories, Source/Filter combinations with modulation, nested
Multi state, incoming/outgoing Sample EOF during the fade,
complex release tails, and full UI/live listening acceptance.
The broad all-properties TODO remains open; PR #12 remains
**draft and unmerged**.

## Thirty-third slice: Sample EOF and Note Off across a Multi onset fade

This slice completes explicit lifetime accounting for the initial
direct-Sample Multi rephase (§32) and the earlier oscillator/OnePole
onset crossfades. It does **not** widen the eligible Source/Filter
state migrations or alter ordinary/non-preview playback.

**Problem reproduced by a red regression:** a 400-frame Sample
played unrandomized until an onset edit at outer frame 80. The
edited Multi randomized both incoming playheads beyond the asset's
natural EOF on their first frame (deterministic offsets 725 and
735). The outgoing Sample remained audible under the smoothstep
through frame 143, then correctly became silent, but
`GetEndFrameExclusive` returned **80**: retiring the outgoing
history discarded the note's *actual* last audible contribution.
The regression correctly requires exclusive end **144**.

**Lifetime policy:** for a transition starting at frame `S`,
its outgoing branch can contribute no later than `S + 64`,
regardless of the child Sample's natural EOF or any otherwise
infinite authored lifetime. While the fade remains active, the
outgoing upper bound is:

`min(outgoingNaturalEnd ?? (S + 64), S + 64)`

The voice's overall end includes the maximum of this bound
and the incoming branch's known end. When the fade completes
(or both branches have already proved silent), the outgoing
invocation reference is retired; if the incoming end is finite,
the voice **preserves the outgoing branch's bounded audible
end** rather than incorrectly replacing that end with the
incoming Sample's earlier EOF. `GetMultiSilenceEnd` performs
the same bounded reconciliation for lifetime queries made
between render blocks or after the outer renderer has already
stopped evaluating a finished note. On an in-render fade
completion or proven early silence, `ChildEndChanged` is set
so parent `baseEnd` and `finalEnd` recompute within the
block. The end arithmetic saturates at `long.MaxValue`
instead of overflowing a frame counter.

**Regressions:** three exact, deterministic acceptance cases:

- Incoming Sample EOF **before the first edited frame**,
  outgoing Sample naturally audible beyond the fade.
  Randomizing at outer frame 80 must retain the old PCM
  weighted by `1 - smoothstep(frame / 64)`, stay nonzero
  during the fade, become exactly zero from frame **144**,
  and report exclusive end **144** even when queried
  **in the middle of a split-block fade**.
- Outgoing randomized Sample EOF at frame **126**, while
  the new aligned playhead remains audible until frame
  **170**. The fade must not truncate the incoming Sample
  when the outgoing one ends; PCM after the fade must
  equal a continuously playing aligned reference, and
  exclusive end must be **170**.
- A held bare Sample rephase at frame 80 with Note Off
  at frame **96**. Both original and newly displaced
  playheads must be silent from frame 96, **not** extended
  to fade end 144 or to the incoming Sample's offset.
  The first edited PCM sample matches the outgoing
  state and the natural end is **96**.

The base renderer still stops invoking finished children at
their proven end; the transition neither allocates an
additional SoundState nor reconstructs playback history
inside PCM. Outside the eligible held direct-Sample shape,
previous captured-onset behavior is unchanged. Ordinary
note-off/loop support, arbitrary variable-rate Source
history, alternate Sample assignments, Instrument/Envelope
release, nested stateful child graphs and physical listening
remain separate acceptance milestones.

Keep the all-properties TODO open and PR #12 draft/unmerged.


## Thirty-fourth slice: concurrent direct Sample Source retune and Multi onset change

A held or latched audition may change a direct bare Sample Source's
authored **FrequencyHz** in the same Synth revision as its Multi
`RandomizePhase`/`CycleLength` onset policy. The previous narrow onset
migration required the two Source frequencies to be identical, so the
scalar Source retune was accepted while the new phase-onset was silently
retained. The replacement now uses the existing worker-prepared Sample
history transfer and the same bounded 64-output-frame onset crossfade.

The replacement Source invocation is constructed off-PCM using the
**new** authored FrequencyHz, but transferring the old Sample's
`PitchMultiplier`/speed/playhead into it must also retain the
**outgoing voice's original FrequencyHz basis**. The live Source rate
uses `node.FrequencyHz / voice.OriginalFrequencyHz`: retaining the
worker-created *new* denominator after copying the old Sample scale
would incorrectly normalize the edited frequency back to one. A
preflight requires finite, positive old/new frequencies and their
ratio; the PCM commit copies only the previous frequency basis,
integrated position, scalar state and streaming curve into preallocated
destinations. No Sample replay, child construction or managed PCM
allocations are introduced.

The red regression
`FrequencyRetuneAndDirectSampleOnsetEditShareTheOriginalPlayhead`
first demonstrated that the old code accepted the combined revision
without requesting any onset preparation. It compares 160 exact-frame
outputs against two independently rendered references: unchanged
outgoing Sample pitch and an in-place, forward-integrated Source
frequency retune, combined through the established smoothstep at
frames 0–64. The first frame must equal the old voice; after frame 64
the output must match the retuned reference, with zero managed
allocations during the PCM render.

This does **not** yet generalize the migration to previously variable
Source pitch-rate histories, concurrent `Constant` or
`ExponentialMultiplier` mode changes, FM-modulated Source pitch,
Source reassignment, Instrument/Envelope/panning/tone-Filter state,
nested Multi, arbitrary Sample seeks, or physical click/load
acceptance. It retains the preexisting whole-tree worker and
release/EOF guards. All-properties acceptance remains open; PR #12
stays draft and unmerged.


## Thirty-fifth slice: exact bounded variable-rate direct Sample rewind

This slice admits a concrete, previously excluded **variable-rate Source
history** for held/latched Synth audition Multi onset changes. In the
eligible case, the new Multi phase skip is *earlier* than the old one
and its target Sample playhead frame is within a **128-frame retained
window of actual integrated Sample positions**. The integration may
cross any number of authored/parent pitch-rate changes within that
window; the change does not use a current-rate approximation and does
not replay historical audio.

**History capture and transfer**

- Only Sample invocations in the live Synth/Multi audition path opt in
  to a fixed 128-double circular history in `SampleSoundState`. The
  Sample renderer records exactly the `double pitchPosition` it uses
  for each already-rendered Source frame. Ordinary playback/export
  does not allocate the ring; no ring allocations occur in PCM.
- The preflight knows the target deterministic Multi skip **before**
  requesting a worker replacement. The pure skip arithmetic is shared
  by ordinary onset initialization and the eligibility check. A
  variable-rate future skip or rewind older than the retained window
  is not worker-queued or retried indefinitely; independent valid
  scalar edits still adopt normally.
- A detached replacement Sample/Source invocation, Filter preparation,
  and destination history ring are constructed on the worker. Whole
  voice admission still checks same immutable Sample PCM/definition,
  unbound Source, unchanged Source controls except eligible FrequencyHz,
  positive/finite rates, compatible release/state, no EOF, no Source
  envelope or tone Filters, unchanged Multi ratios and the original
  no-overlapping-fades policy.
- At the PCM publication boundary, an admissible variable-rate
  **backward** skip copies the exact previously rendered pitch integral
  at the revised local frame. The existing `PitchTrajectory` and
  `FmSourcePitchCurve` recipients are seeded in O(1), and up to 128
  prior recorded positions are copied into the already allocated
  destination ring so a subsequent compatible rewind can retain
  historical continuity. No source is rebuilt, no previous audio
  rendered and **no managed PCM memory allocated**. The outgoing
  and incoming Samples evolve independently through the existing
  64-output-frame smoothstep.
- Crucially the incoming Source retains the previous
  `OriginalFrequencyHz` basis and the previous *has-ever-varied*
  Source pitch-history classification. Reseeding it must not
  accidentally make a later variable-rate forward skip appear
  constant-rate eligible.

**Red/green regression**

The first regression proves exact integration across a discontinuous
parent pitch change. Multi ID 2, seed 21 and cycle 100 give both
original randomized voices skip 44. Parent rate changes from 1 to
1.3 at outer frame 80, then the onset becomes aligned at outer frame
128. The outgoing child's local frame is 172, the incoming's is 128;
the true historical integral at 128 is
`124 * 1 + 4 * 1.3 = 129.2`, not `128` and not the result of
subtracting 44 frames at today's 1.3 rate. Every frame in the
64-frame crossfade is compared against the outgoing PCM and a
separately computed interpolated Sample oracle using the recorded
position advanced at 1.3 per new frame. The test also checks
old-frame parity, zero managed render-thread allocations and that a
subsequent forward phase edit does not falsely assume a constant
rate. The second regression uses seed 137/cycle 800 to force a
rewind well outside the ring and verifies the independent Constant
edit still publishes without scheduling impossible preparation.
The test-only commit initially ran red because the variable-rate
rewind did not request onset preparation; the out-of-window scalar
fallback was already correct. Implementation and full CI then ran green.

**Limits and next work**

The original O(1) constant-rate projection remains valid for positive
or negative offsets. **Variable-rate migration here is only an
exact, recorded nonforward shift**: positive/future skip, older than
128 frames, an unrecorded history, or a prior changed Source state that
fails whole-voice preflight stays on the previous onset rather than
using a guessed rate. General seeks, longer history transfer,
Instrument-selected Sample/Envelope/panning/filter chains, modified
Source bindings, nested stateful Multi, complex FM graph migration,
and physical click/underrun acceptance are still outstanding.
The all-properties TODO remains open, and PR #12 stays draft/unmerged.
