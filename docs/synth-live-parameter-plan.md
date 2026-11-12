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


## Thirty-sixth slice: longer exact variable-rate Sample rewind, bounded PCM transfer

Slice 35 established an exact 128-frame recent-history rewind for direct bare
Sample children of eligible held/latched audition Multi nodes. A phase reset
from a longer randomized skip was still refused even when the integrated
Sample position had **actually been rendered** earlier in the same live
Source history. This slice widens that deterministic, non-speculative
lookback without making realtime publication work proportional to the
total history retained.

**Separate, fixed capture and publication budgets:**

- Only the audition-opted-in `SampleSoundState` now retains the last
  **1,024** exact `double` integrated pitch positions, consuming
  **8 KiB per eligible Sample invocation**. The ring is allocated as
  part of initial/prepared voice construction, never during audio
  production. Ordinary arrangement playback/export remains unchanged
  and does not allocate it.
- An eligible changed Multi onset may rewind to **any recorded target
  position within that window**, with exactly the same Sample identity,
  Source-control, voice-lifetime, held-note, constant-ratio and full-tree
  preflight gates as slice 35. No old audio or historical rate stream
  is replayed to obtain the position.
- Copying history during atomic PCM publication remains bounded to
  **at most 128 recorded positions**, into the detached replacement's
  already-allocated ring. Thus enlarging the retained lookback does
  **not** turn a 1,024-frame edit into an O(1,024) PCM copy. The
  replacement inherits an exact target position and the immediately
  preceding 128-frame prefix only; any still-earlier prefix not
  copied is intentionally unavailable for another later rephase
  until newly rendered positions establish a usable window.
- This distinction matters: being within 1,024 frames of the
  **currently rendered Source frame** is not sufficient by itself
  if the proposed target was never recorded (for example, a
  randomized first-onset frame skipped past it with no replay).
  Missing history, changes before the recorded beginning, and
  offsets exceeding 1,024 continue to fall back immediately.
  Independent scalar edits remain live and no impossible worker
  preparation is queued.
- Positive variable-rate skips are still not eligible merely because
  the past is retained. Their target position lies in the unobserved
  **future** of that specific invocation and may depend on pitch
  edits or modulation yet to occur. Substituting
  `lastRate * skipDelta` is not an exact historical integral.
  A future feature must define a separate causal/forward policy or
  a truly immutable prepared trajectory, rather than treating
  a current-rate extrapolation as already-known history.

**Red-before-fix acceptance:** Multi ID 2 with seed 137 and 800-frame
cycle starts two randomized bare Sample constituents with skips 725
and 735. Parent rate changes from 1 to 1.3 at **outer frame 200**.
At outer frame **800**, removing randomization requires their
previously rendered **local frame 800**: both Sample histories have
already integrated that position at rate 1, well before the later
parent rate change reaches either displaced Source clock. The
recorded integral is precisely **800**, whereas projecting
backwards 725 or 735 frames at the *current* 1.3 rate gives the
wrong answer. The new regression demands worker preparation and
checks every frame of the 64-frame outgoing/incoming PCM crossfade
against an independently interpolated Sample oracle starting at
position 800 and then advancing at 1.3. It also asserts first-frame
parity, all-sample finite PCM, and **zero managed allocations** in
the edited render. It fails against the previous 128-frame window.

The existing out-of-window/non-starvation test was retained as
the same desired invariant, moving its randomized cycle to
5,000 frames: the deterministic old skips are 3,125 and
3,735, both exceeding the new 1,024-frame retained lookback.
The independent Constant still commits while the phase onset
remains unchanged. Existing direct-Sample EOF/Note Off/fade
lifetime, late-revision supersession, and the original
constant-rate O(1) forward/backward skip behavior are unchanged.

**Still outstanding:** exact variable-rate **future** advances
without an assumed rate, rewinds outside 1,024 already-recorded
frames, arbitrary seeks, Instrument-selected Sample/Envelope/panning
and tone-Filter migration, changed Source assignments, nested
stateful graphs, all-property inspector audit, and physical
listening/throughput acceptance. The whole all-properties TODO
stays open and PR #12 stays **draft and unmerged**.


## Thirty-seventh slice: continuity-first Sample Source live edits (supersedes 31–36 direct-Sample rephase policy)

**Decision:** a live Synth edit changes what an already-playing note
does **from this point forward**. It does not retroactively rewrite the
note's pitch or Sample phase history to match a hypothetical fresh note.
For direct bare Sample Sources, the already-integrated fractional
playback position and playback offset are authoritative. The selected
Sample asset/configuration and its own release/EOF state must still be
compatible; an unrelated Source replacement is not eligible for this
fast path.

This decision **supersedes the direct-Sample onset displacement and
exact-history reconstruction contract in the earlier slices (31–36)**.
Those sections remain as development history, not current requirements.
In particular the 1,024-frame Sample history buffer, 128-frame history
copy cap, `FmSourcePitchCurve.HasConstantRateHistory` gate, historic
rate reconstruction and `lastRate * skipDelta` projection have been
**removed**. Ordinary playback/export already had no history ring;
live audition now has none either.

**Active Sample-only Multi edits:** changing `RandomizePhase` or
`CycleLength` on a Multi whose active input is only bare direct
Samples (optionally joined with stateless Constants/Operators and
compatible stateful OnePole processing) preserves its initialized
children's original `SkipFrames` and active `SoundState`. This edit
does not need the worker, an added playhead or an onset crossfade.
Subsequent new note onsets still use the *revised* phase policy.
Changing the direct Sample's `FrequencyHz`, parent pitch/speed,
or another compatible scalar property changes only future playback
rate; it never rescales already-integrated Sample positions.

**Mixed oscillator + Sample children:** where a compatible oscillator
needs to adopt the revised Multi phase, the existing off-PCM worker
still constructs detached whole-child replacements and an atomic
64-output-frame onset crossfade. Each Sample Source in those children
copies its **current integrated position** at the publication
boundary from `oldState.PitchTrajectory.GetPosition(oldFrame)`
directly to `newState.PitchTrajectory.SeedPreparedStreamingPosition(
newFrame, position)`, with **no skip delta**. The original Sample
`PitchMultiplier`, speed, `PlaybackOffset`, `SourceFrameOffset`,
position, Note Off and `OriginalFrequencyHz` basis are retained
into the new invocation. `FmSourcePitchCurve.SeedBeforeFrame` keeps
only the last positive finite rate needed for the next chronological
frame, not a proof of constant historical rate. The revised
frequency and modulation apply from the first revised frame onward.
Oscillators independently keep their established phase-shift
semantics; unbound OnePole histories are still copied into
worker-preallocated states. During the fade the two Sample branches
start from the same physical position, so an unchanged Sample
contribution is independent of the oscillator rephase.

**Whole-voice eligibility and bounded work:** the prior
same-Sample-asset/PCM-identity, source control compatibility,
unmodulated Source and no unrelated Envelope/tone-Filter history
restrictions stay in place for the mixed prepared transition.
Noncontinuing/finished Sample histories are refused for this
specific mixed transition without speculative restart. Note Off
and natural Sample EOF remain attached to the original current
playhead; no newly randomized Sample position can resurrect or
prematurely truncate a held Source. No Source seek, audio replay or
managed allocation belongs inside PCM. New constructed replacements
and Filter histories stay on the worker.

**Acceptance:** regression tests express continuity rather than
historical rephase equality. For Sample-only Multi phase edits,
PCM before and after a running-note edit matches an unedited held
reference **bit for bit**, including fractional Sample interpolation,
prior variable-rate pitch changes, large randomized skips,
cross-wrapper frozen PCM snapshots, combined FrequencyHz retune,
correct EOF and Note Off boundaries, and zero managed PCM allocations.
A separate mixed oscillator+Sample regression compares every
frame against an independent oracle: the original full mixed output
plus the old/new isolated oscillator phase delta weighted by the
64-frame transition. Its Sample part must not jump even when
the oscillator phase does.

**Scope still open:** actual device listening/click tests, sustained
CPU/underrun profiling with dense Multi voices, broader FM-modulated
Source/Instrument/Envelope/filter state migration and arbitrary
structural Source reassignments, full property-inspector-to-audio
audit, and the remaining all-properties goal. The PR stays
**draft and unmerged** until explicit review/merge authorization.


## Thirty-eighth slice: connected and audio-rate modulated Sample Source continuity

The continuity-first rule introduced in slice 37 now extends to
**direct Sample Sources with a connected Synth graph multiplier input**
during worker-prepared Multi oscillator phase changes. A Sample's
original fractional playback position is authoritative even if its
rate has been driven by an oscillator on every PCM frame.

**Compatibility / semantics**

- `CanRephaseDirectSampleSource` no longer requires the original and
  revised `MultiplierNodeId` to be null. Instead, the old and revised
  node must retain the **same binding**, which also must pass
  `CanRephaseOscillatorGraph`'s existing whole-graph checks on
  stable node IDs, exact input index wiring and individually eligible
  upstream Oscillator/Constant/Operator/OnePole stages.
- A direct Sample still requires the **same frozen Sample PCM/definition
  identity**, positive finite source frequencies and their ratio, and
  unchanged authored `Constant` and `ExponentialMultiplier` flags.
  A changed binding, changed Source identity, unsupported modulation
  graph structure or untransferable Filter/Envelope/Instrument history
  does not gain eligibility from this slice.
- A rephased oscillator used as a **Source multiplier** adopts its
  newly phased oscillator state in the prepared incoming child.
  From the publication boundary onward, that oscillator may change
  the Sample's **future playback rate**. Its pre-publication modulation
  history never causes a Sample seek or retrospective re-integration.
- As in slice 37, the incoming Source's `SampleSoundState` seeds
  `PitchTrajectory` at its new local frame with the actual old
  `PitchTrajectory.GetPosition(oldFrame)`, without compensating for
  the changed Multi `SkipFrames`. `OriginalFrequencyHz`, playback
  offsets, released state, and forward-only adjacent `FmSourcePitchCurve`
  rate are carried over in constant time. If the connection is
  stateless, a Sample-only phase-policy change still requires no
  replacement. If the connected multiplier is an oscillator, its
  phase **does** change through a worker-prepared transition, while
  the Sample's physical playback position remains continuous.
- The new support is for **already sounding, continuing direct Sample
  voices**. Natural EOF or Note Off, crossing nontrivial child
  Instrument tone filters/envelopes, reassigning Source IDs, changing
  graph wiring, or attempting a nested stateful replacement still
  retain their conservative existing eligibility or fallback paths.
  Atomic per-voice preflight, 64-frame smoothstep crossfade for the
  rephased oscillators and worker-prepared destinations remain.

**Tests**

Two regressions exercise both a connected stateless scalar multiplier
and a truly dynamic oscillator-controlled rate:

1. A Sample with a connected 1.2× scalar multiplier is mixed with a
   separate oscillator. The Sample retains every old fractional
   position while the oscillator changes its Multi phase. Every
   crossfade frame is checked against the old full mixture plus the
   separately rendered oscillator phase delta times the established
   smoothstep weight. The publication frame exactly equals the old
   PCM, and the edited PCM render allocates no managed memory.
2. A Source driven by a 550-Hz positive-range oscillator has variable
   integrated rate even before a parent pitch change. The oscillator
   changes its own phase on a live Multi phase edit, but the Sample
   starts from its existing position at the boundary. The first
   crossfade output is exactly the previous held note's PCM; later
   output differs audibly as the **new forward modulation** changes
   its rate. Test verifies finite PCM, no managed allocations and
   successful detached worker preparation without replaying the old
   modulation history.

**Remaining scope**: replacing the selected Sample Source itself,
Instrument-selected Sample envelopes/release state, non-OnePole
Filters, feedback/stateful nested modulation, physical listening and
sustained voice-load performance, and the full inspector all-properties
end-to-end acceptance remain open. PR #12 stays draft and unmerged.


## Thirty-ninth slice: matched Instrument-selected Sample Volume release continuity

This slice adds **one deliberately bounded Instrument case** to the
continuity-first Multi onset migration developed in slices 37–38:
a frozen `InstrumentSound` resolving to the same terminal Sample
with a compatible stateless **Volume envelope and its Note Off release**.

### Semantics and eligibility

- `InstrumentSound.CreateInvocation` already *flattens* the selected
  Instrument tone to its terminal `SampleSound` and
  `SampleSoundState`. Its tone-selected Volume envelope is retained
  in `SoundInvocation.Configuration.Envelopes.Volume`; the terminal
  Sample carries `ReleaseIsControlledByVolumeEnvelope`, so ordinary
  Sample Note Off must not truncate its enveloped release.
- The mixed Multi oscillator-rephase path now admits this shape only
  when old/new `FmSoundSourceNode` retain their source binding,
  positive finite frequency semantics and **the same frozen
  `InstrumentSound` wrapper object**. At publication, the
  actual old/new terminal `SampleSound` instances must still share
  the immutable Sample/PCM timeline identity. The invocation's full
  `NoteConfigurationSnapshot` must compare equal, both terminal
  `SampleSoundState.ReleaseIsControlledByVolumeEnvelope` policies
  must agree, and any Volume envelope must be the matched
  Instrument-controlled release path.
- Unchanged non-Volume controls, without a new bound pitch Source
  identity, still follow the earlier compatibility gates. This
  slice **does not transfer** Instrument tone Filters, pitch or
  panning envelopes, Filter envelopes, stateful Instrument-selected
  alternatives, changed tone mappings, independently resolved
  Instrument wrapper instances, or nested arbitrary Source graphs.
  These remain excluded until their own semantics and bounded
  preparation are established.
- The running Sample's integrated fractional playhead, initial
  `OriginalFrequencyHz` basis, playback offset, native source
  frame offset, spatial position and Note Off origin carry into
  the detached worker-prepared replacement as in slice 37.
  No historical rate replay or second Sample seek occurs.

### Preserving the Instrument's original envelope clock

A changed Multi `RandomizePhase` or `CycleLength` may assign the
incoming child a different local `SkipFrames` even though the Sample
itself must **not** rephase. Preserving only the Sample's accumulated
`PitchTrajectory` would leave its Volume envelope at the wrong
position on its release curve.

Each Sample `SourceVoice` therefore also carries an
`EnvelopeFrameOffset`, initially zero. An eligible oscillator onset
replacement inherits the old envelope clock using the exact
bounded scalar relation:

`incoming.EnvelopeFrameOffset = old.EnvelopeFrameOffset
    + oldLocalFrame - newLocalFrame`

The new Sample's **actual** integrated playhead is seeded unchanged,
while any invocation-owned Volume envelope evaluates at
`newLocalFrame + EnvelopeFrameOffset`. Both the envelope
`GetValue` release origin and the predicted envelope
`GetEndActiveFrameExclusiveAfterNoteOff` end must use the
**same translated clock**. In particular, the enclosing Multi
already displaces the child Note Off by its phase skip. Thus
the effective envelope Note Off is
`childNoteOffFrame + EnvelopeFrameOffset`, and the predicted
silence end is mapped back from the envelope clock into the
new Source local clock. Translating only the envelope's active
frame, without its release origin, is incorrect: it extends or
truncates the Instrument release at the wrong frame.

This scalar transfer and lookups are allocation-free. The
replacement's Sample and oscillator histories remain independently
prepared before publication; the existing 64-frame onset crossfade
and whole-child atomic preflight are unchanged. No retroactive
parameter rewrite or post-edit Sample replay is attempted.

### Red-before-green acceptance

The regression uses a single tone Instrument selecting a long
mono Sample with a ten-frame Volume-release curve. A second,
independent oscillator is intentionally muted by a stateless zero
Multiply: its phase still requires the worker-prepared Multi
replacement, while the audible PCM isolates only the continuing
Instrument Sample. Removing/adding randomized Multi phase at
outer frame 128 must not alter the audible Sample stream.

The test checks identical PCM against an uninterrupted reference,
zero managed allocations in pre- and post-Note Off PCM,
and the **precise original 10-frame release**, both when Note Off
occurs **during** the 64-frame crossfade (outer frame 160)
and **after** it (frame 200). The Sample must still produce audio
during its ten-frame release and become exactly silent at
frame 170 or 210 respectively, with the same exclusive note end.
The initial red test reproduced the previously unsupported
Instrument migration. A subsequent implementation run exposed
that the replacement's Volume release origin also required the
clock translation; this additional invariant is covered by both
timings.

**Still open:** independent Instrument wrapper equivalence,
changing tone selection or Sample assignment, Instrument tone
Filters and pitch/panning/Filter envelopes, nested Instrument
and feedback/stateful modulation, end-to-end property audit,
physical listening/click testing and high-density Multi
throughput acceptance. The all-properties live-editing TODO
remains open. PR #12 stays **draft and unmerged**.


## Fortieth slice: equivalent frozen Instrument bindings across revision wrappers

**Goal:** permit the continuity-first Instrument-selected Sample transfer
from slice 39 when a revised Synth was compiled with a *different*
`InstrumentSound` instance and independent frozen data-driven
`InstrumentDefinition` copy, rather than requiring the identical
InstrumentSound wrapper object. The selected Sample's physical playhead
and Instrument Volume-release clock remain untouched by the Multi
oscillator phase edit.

### Immutable, allocation-free early eligibility

A data-driven `InstrumentSound` constructed from
`InstrumentDefinition` captures a **frozen tone-selection map** at
construction, before any realtime audio processing:

- Stable Instrument ID, tone-table divisions and offset
- Complete tone-table index mapping and ordered tone specifications
- Each tone's selected Source ID, pitch multiplier, explicit NNA and
  envelope IDs (Volume/Pitch/Panning/Filter)
- Instrument NNA override/fade duration and each tone's Filter
  configuration reference

Two distinct data-driven wrappers qualify as **selection-equivalent**
only if those frozen snapshots match completely. This is a deliberately
conservative proof: even a change to an unselected tone or NNA map
refuses the *phase rebase* for this slice. Identical wrapper instances
continue to use the previous slice 39 eligibility. Arbitrary
script-selected `Func<double, ToneSpecification?>` wrappers have
no safe general equivalence proof and are **not** compared between
distinct instances. The comparison performs no song lookups,
tone selection, PCM decoding, state construction or managed allocation
on the producer/PCM path.

### Verify the *actual* selected binding, not just its tone map

Matching tone-table IDs cannot prove that two separately frozen song
resolvers supply the same **decoded Sample** or immutable Volume
curve. The existing off-thread prepared Multi replacement therefore
still resolves each selected invocation and enforces the full
whole-voice `CanTransferPreparedDirectSample` preflight:

- Both selected terminals must be `SampleSound` with the **same
  immutable Sample PCM/timeline identity**, including its loop,
  sample rate, channel layout and geometry. Distinct
  `SampleSound` wrappers sharing one immutable `SamplePcmData`
  are allowed.
- Captured `NoteConfigurationSnapshot` equality, Volume envelope
  object and `ReleaseIsControlledByVolumeEnvelope` policy remain
  mandatory; changed tone Filters, pitch/panning/Filter envelope
  histories, different selected tone pitch and rebinding are excluded.
- The original fractional Sample position, original Source frequency
  basis and the Instrument's original envelope active-frame **and
  Note Off** release origin move to the prepared child exactly as
  in slice 39. No history replay/seek, retroactive pitch rescale,
  unprepared Volume-state allocation or modified crossfade length.

If the frozen tone-map signatures match but the **worker-prepared
terminal binding does not**, the completed incompatible phase
replacement is discarded for that revision. This check runs over
already-prepared immutable child/source configurations; it performs
no tone lookup or DSP work. The existing held Sample and oscillator
retain their phase, while subsequent independently compatible
scalar parameter revisions can still publish. It does not repeatedly
attempt an impossible worker transfer or starve the live edit.

### Acceptance and regression boundaries

The prior Instrument release test now covers a matrix of:

- Same-wrapper or **distinct InstrumentSound and data-driven
  InstrumentDefinition wrappers**; the latter also uses distinct
  `SampleSound` wrappers and Source resolvers sharing an explicitly
  verified immutable decoded `SamplePcmData`.
- Note Off at frame **160** (inside the 64-frame oscillator crossfade,
  exclusive release end 170) and frame **200** (after fade, end 210).
  All cases compare every affected PCM frame against an uninterrupted
  held Sample, require the original ten-frame Volume release, exact
  final silence and **zero managed PCM allocations**.

Negative tests require that distinct frozen definitions with changed
tone pitches do *not* qualify for a new oscillator phase, but leave
an independently compatible Constant update live. A separate
same-tone-map/different-resolved-PCM case verifies the worker detects
the stronger binding mismatch and falls back to scalar adoption
without restarting or jumping the existing Sample.

**Still outstanding:** semantically equivalent *different* immutable
Envelope curve objects (without shared reference), selected-tone-only
rather than whole-map compatibility, scripted Instrument selector
equivalence, changed tone assignments/PCM selection, nested
Instrument chains, Instrument tone Filter and pitch/panning/Filter
envelope history, physical click/throughput acceptance, and the
all-properties Synth inspector-to-audio test. These are not
silently inferred from matching IDs. The all-properties TODO remains
open; PR #12 remains **draft and unmerged**.


## Forty-first slice: selected-tone-only frozen Instrument compatibility

Slice 40 admitted separate frozen data-driven Instrument wrappers only
when their **entire** tone table and ordered tone specifications were
identical. That was a correct but unnecessarily restrictive proof:
editing another key's tone prevented the already-held note from
participating in a compatible oscillator Multi phase change, even
though the current note's selected Sample and Volume release were
unchanged.

**New distinction:** a held Instrument note's selected tone was bound
at Note On, and the running Sample must preserve that selection.
Unrelated tone-table slots or tone specifications may change for
*future* notes. The existing note is never reselected by applying
its current (possibly retuned) pitch to the new map on PCM.

### One-time bound-slot capture

When `FmSynthSound.CreateBoundState` constructs a `SourceVoice`
outside realtime, it captures the data-driven Instrument's
**tone-table index** using the same log2/divisions/offset and
away-from-zero rounding as normal tone binding. This immutable
`BoundInstrumentToneSlot` accompanies each existing SourceVoice
and each independently worker-prepared replacement. The binding is
performed only during normal note/worker construction, not during
an audio callback or a later live parameter edit.

The frozen `InstrumentSound` snapshot from slice 40 still holds the
Instrument ID, pitch-selection divisions/offset, global NNA settings,
tone-table slots and snapshots of each tone's Source ID, pitch
multiplier, explicit NNA, envelope IDs and Filter identity. Its
new comparison has two stages:

1. **Static geometry:** both independently frozen Instrument wrappers
   must agree on Instrument ID, note-to-slot divisions/offset and
   global NNA override/fade semantics. Script-selected Instruments
   have no proof of equivalence between different wrapper objects
   and remain ineligible; identical wrapper references still pass.
2. **Already-bound tone:** for **every active Multi child** and every
   Instrument Source, compare the captured original table index's
   *effective selected tone* in both frozen maps. The list may have
   changed length or been reordered, and other table slots may
   select entirely different tones. The particular table slot
   corresponding to this bound note must still be valid and select
   an identical Source ID, pitch multiplier, tone NNA, Volume/Pitch/
   Panning/Filter envelope IDs, and identical tone Filter identity.
   This is a fixed index/value comparison: **no pitch-to-tone
   recalculation, Source resolution, SoundInvocation construction,
   allocation or historical Sample rendering** happens during
   live adoption.

The worker then constructs the new child normally, and the existing
whole-voice preflight enforces matching **selected table slot**,
terminal immutable Sample PCM/timeline, NoteConfigurationSnapshot,
release policy, and allowable tone Filter/Envelope shape before any
state is transferred. A fresh note may select one of the newly
edited tones; the already-held note keeps its original Sample
playhead and original Instrument Volume-release clock. Compatible
oscillators alone undergo the established 64-output-frame onset
crossfade, with no managed PCM allocations.

**Conservative exclusions and fallback:** a changed tone at the
bound slot, global pitch-selection geometry, selected PCM or selected
release configuration cannot be declared equivalent merely because
the two Instrument IDs match. A changed *selected* tone refuses
the phase rebase before scheduling worker preparation when possible.
If the map matches but a different terminal PCM is discovered only
after worker construction, the prepared rebase is discarded and
independent compatible scalar updates remain live (slice 40).
Original note speed/frequency edits do not retroactively reselect
the Instrument tone.

### Red-before-green acceptance

The existing Instrument Sample/ten-frame Volume-release test is
extended to check the old and new independently frozen wrappers
with **one modified, unselected tone specification**, including
different values of the unselected tone's pitch multiplier.
The running note is bound once at C4 **or at C5 (tone slot 12)**,
to prove no hardcoded slot-zero assumption. Both release timings
remain covered: Note Off at frame 160 during the 64-frame crossfade
(exclusive end 170), and frame 200 after it (end 210).
The resulting PCM must match an uninterrupted held Sample exactly,
including the original release envelope, with **zero** managed
allocations in edited PCM renders.

The test-only change fails under slice 40's whole-map gate. The
selected-tone gate makes these legitimate unrelated-tone edits
eligible. Negative tests also cover changed tone pitch **at the
held note's own bound slot**, including C5, which must not rephase
the old Sample or starve an independent Constant edit. The previous
worker-resolved PCM mismatch fallback continues to apply.

**Still open:** tone-selection changes that *do* affect the held
note, changing its selected Sample asset, independently instantiated
but semantically equal release curves, arbitrary scripted/nested
Instrument selection, Instrument tone Filters and pitch/panning/
Filter envelope state, listening/click and dense-Multi throughput
checks, plus the all-properties Synth inspector-to-audio acceptance.
The all-properties TODO remains open, and PR #12 stays
**draft and unmerged**.


## Forty-second slice: frozen node Volume envelope equivalence without recursive Synth cycles

**Architecture clarification.** A Synth cannot modify a nested Instrument
by recursively selecting itself through one of that Instrument's tones.
`FmSoundSourceReferences.Validate` traverses **all** declared
`InstrumentDefinition` and `ScriptInstrumentDefinition` tones, as
well as every nested Synth Source edge. Cycles reject before the
candidate Synth graph is committed, even if a particular note would
have selected a different, terminating tone. Such pitch-dependent
conditional recursion is not a supported exemption.

The actual production reason for independently prepared nested
`InstrumentSound` and `NodeEnvelopeCurve` wrappers is simpler:
a direct Synth preview edit republishes a **new frozen song snapshot**.
`PlaybackSnapshotSoundResolver` independently prepares the
Instrument and Node envelope renderer objects in each generation.
Those objects are different even when the Instrument's selected
tone and the Volume envelope's authored node shape are untouched.
A nested `Synth A -> Instrument B -> Sample C + Envelope D`
is acyclic and common; editing Synth A's Multi phase can require
migration through the newly compiled versions of B and D. An
identity-only Volume curve check would spuriously refuse this valid
transition.

**Bounded new compatibility proof.**

- `NodeEnvelopeCurve.HasSameFrozenShapeAs` compares every copied node
  time and scalar value, captured sustain-start and sustain-end tick,
  last-node tick and sustain-loop mode **bit for bit**. It never consults
  mutable `NodeEnvelopeDefinition`, hashes text, calls a song
  resolver, constructs a new curve or allocates on PCM. Identity still
  short-circuits for reused curve objects.
- `FmSynthSound.CanSharePreparedVolumeConfiguration` permits a
  separately frozen Node Volume curve with identical captured shape
  in both the whole-child Sample transfer preflight and the early
  prepared-Instrument-binding fallback check. The complete
  `NewNotePolicy`, note/new-note fade durations, and all Pitch,
  Panning and Filter envelope object references must remain
  unchanged. Script envelopes or other non-node implementations
  remain **identity-only**; no equivalence is guessed from Envelope ID.
- As in slices 37–41, Instrument tone identity, immutable Sample PCM,
  original playhead, original Volume-release clock, translated Note
  Off origin, worker-prepared replacement and the 64-frame bounded
  oscillator crossfade remain mandatory. The newly compiled Volume
  curve is mathematically the same function, so substituting it
  cannot change the held note's release. Truly edited Volume shapes
  must retain the old held curve and decline that oscillator rephase,
  while allowing independent compatible scalar edits.

**Acceptance.** New tests build two independent
`NodeEnvelopeDefinition` instances and two distinct
`NodeEnvelopeCurve` objects with equal points and single-node
sustain. The Instrument wrappers are also distinct. Note Off at
outer frame 160 (during oscillator onset crossfade) and 200 (after
it) must each reproduce the continuous old Sample's PCM, original
Volume-release end and zero managed allocations. The same test
also changes the new curve's release endpoint as a negative
scenario: it cannot rewrite the running note's release.

A second, **audible oscillator oracle** is necessary because Sample
PCM equality alone cannot distinguish a successful oscillator phase
transition from the intentional conservative fallback. It renders
the old mixed output plus the isolated new-minus-old oscillator
signal weighted by the 64-frame smoothstep, and checks each frame
against the edited output, including first-frame parity and zero
first/sustained PCM allocations. A measurable post-fade difference
from the old oscillator confirms rephase really occurred. The
initial Sample-only regression did not prove this and was
strengthened accordingly.

This is **not** support for Synth->Instrument->same-Synth recursion,
editing an Instrument's tone map *from inside* the Synth graph,
scripted-envelope equivalence, changed selected Sample or Volume
curve, arbitrary Instrument tone DSP, or the full all-properties
inspector acceptance. The last remains open. PR #12 stays
**draft/unmerged**.


## Forty-third slice: real editor property audit and audition-only waveform transition

The authoritative **property/control audit** now lives in
[`docs/synth-live-editor-property-audit.md`](synth-live-editor-property-audit.md).
It maps the controls actually exposed by `FmSynthEditorControl` to
the document revision/publication path, current running-state
eligibility, test evidence and known remaining gaps. The original
all-properties TODO stays open. In particular, assigning another
Sample/Instrument SourceId remains an unsupported held-note
reassignment (compatible nested Synth bindings are a narrower
exception); some shared object dependency paths through Instruments
still need transitive publication review. Do not mistake a successful
inspector commit or AudioRevision increment for a successful live
voice adoption.

**First audible implementation gap fixed:** Single-output Synth
oscillators previously replaced their Waveform, Curvature, Vmin or
Vmax output immediately upon compatible live publication. Depending
on the oscillator phase, the output could jump by a large fraction of
full scale in one sample. All three actual inspector shape families
failed new red regressions before the fix.

For audition-enabled `FmSynthSound` voices only (identified by
`reserveMultiHeadroom` / `enableLiveMultiExpansion`), the
`CommitPreparedRunningState` boundary now captures the existing
voice's **last audible oscillator value** and output-frame index
when a single-output oscillator's transfer shape or range changes.
The first revised output frame reuses that value. During the next
64 output frames, the existing oscillator phase integrator advances
normally while its new output is blended from the captured value
using the established cubic smoothstep. From frame +64 onward,
the revised waveform/range is fully active. A second shape edit
while the first fade is still underway captures the **current
blended** output as the new fade origin, without a second phase
trajectory, PCM buffer, allocation or retroactive replay.
Frequency-only/mode edits retain their prior phase-continuity
semantics and are not unnecessarily shape-faded.

The smoothing is **not** applied to ordinary playback/export
renderers: those preserve existing exact arithmetic and optimized
vs. generic graph parity. The single-output audition shape is the
only implemented smoothing scope in this slice; waveform changes
within generic operator/Multi/modulation graphs still require
their own compatibility/click rules and listening tests.

Acceptance includes:

- A failing-before-fix 3-case renderer matrix for Sine->Square,
  nonzero Curvature and Vmin/Vmax changes, checked frame by frame
  against the explicit 64-frame smoothstep oracle.
- Repeated shape edits at frames 128 and 144, preserving the last
  actually audible intermediate value and resuming the new
  waveform at the original oscillator phase after the fade.
- Frequency-only retune retaining phase without shape smoothing.
- Zero managed allocation in the edited PCM render; all previous
  ordinary renderer equivalence and realtime tests still pass.
- Headless Avalonia test driving the **actual waveform selector**,
  checking committed document audio revision, publication callback
  and no new revision for a no-op re-selection.

**Remaining acceptance:** generalized oscillator transfer smoothing
in compound graphs, complete property-by-property UI-to-audio
regressions, source reassignment, selected Filter and nested
Instrument DSP migration, dense-voice throughput and physical
click/underrun listening. The all-properties TODO remains open;
PR #12 stays **draft and unmerged**.


## Forty-fourth slice: transitive shared-object publication through Instrument tone references

This slice addresses a **real editor-to-transport notification gap**
identified by the property audit, without expanding historical
Sample, Envelope or Filter DSP state migration.

Previously, `SongPlaybackTransport.PublishSharedObjectEditAsync`
found Synths with direct Envelope/Filter graph nodes and their
transitive Synth Source parents, but did not traverse an
`InstrumentDefinition` or `ScriptInstrumentDefinition` referenced
by a Synth Source. Editing a shared node/script Envelope or Filter
used exclusively by an Instrument tone could leave that Synth's
current frozen publication overlay stale. Likewise,
`PublishSynthEditAsync` could miss parent Synths when the edited
child Synth appeared only inside an Instrument tone.

**New reverse-dependency traversal (control thread, not PCM):**

- For each changed ObjectId, follow a declared Synth Source,
  Envelope or Filter reference as before, plus **all** tone
  specifications on data or scripted Instruments. Every tone
  `SourceId`, `VolumeEnvelopeId`, `PitchEnvelopeId`,
  `PanningEnvelopeId`, `FilterEnvelopeId` and per-tone
  `FilterConfiguration.DefinitionId` can connect the dependency.
  Unselected tones remain part of this conservative authoring
  dependency analysis; dynamic script selectors do not need
  to execute to establish the declared references.
- Recursively follow nested Instruments and Synth Sources, with a
  visited set to avoid repeated traversal. Instrument wrappers
  are intermediate nodes and never treated as publishable Synths.
  Only *reachable* Synth definitions are prepared/enqueued.
- Discovery order is not always valid publication order: if a
  parent Synth directly references the changed shared Envelope
  while also sourcing a child Synth through an Instrument, a
  breadth-first walk could find the parent before the child.
  A second traversal therefore visits only affected upstream
  Source dependencies **before appending each Synth**. This
  preserves child-before-parent overlay publication even when
  the edited object has multiple distinct dependency paths.
- Existing per-`AudioRevision` deduplication, one frozen
  `SongDocumentSnapshot` per notification, ordered control
  publication, no-session no-op behaviour, unrelated Synth
  exclusion and no audio-session restart remain unchanged.

**Acceptance tests:** a matrix of data vs. scripted Instruments
and every one of the four tone Envelope fields plus the tone
Filter field (10 scenarios). Each uses a shared object reachable
through two nested Instruments and a Synth parent, plus a separate
direct Synth consumer and an unrelated Synth. Tests require the
direct Synth, Instrument-sourcing Synth and its parent to be
published in dependency order, exactly once per revision, with
no session restart; no audio session is opened merely to handle
an edit. Additional cases cover editing a child Synth under
an Instrument and a parent with **both** direct and
Instrument-mediated dependency paths, where naive breadth-first
publication would put the parent first.

**Important limitation:** this slice fixes *which Synth revisions
are prepared and published*, not whether an existing
Instrument-selected Sample note may safely replace its already
captured Volume/Pitch/Panning/Filter histories after an actual
shared-object edit. Incompatible existing notes remain on their
old generation, and freshly started notes see the new frozen
dependency graph. Safe held-state migration, full UI-to-PCM
acceptance, Filter parameter coverage and hardware listening
remain open. Do not close the all-properties TODO or merge
draft PR #12 on the strength of routing coverage alone.


## Forty-fifth slice: shared editor commits reach active Instrument-backed Synth audition

The slice 44 authoring dependency walk is implemented, but an
end-to-end test must also prove that the *actual editors* notify
`SongPlaybackTransport` with the changed shared object ID while
a Synth audition is active. Inspecting only the transport's
`GetDependentSynths` helper would miss a broken UI callback.

**Tracing the production chain** showed that the necessary
wiring was already present:

1. Standalone/embedded `NodeEnvelopeEditorControl`,
   `ScriptEnvelopeEditorControl` and `FilterEditorControl`
   commit through their respective framework-independent
   `EnvelopeDocumentEditor` and `FilterDocumentEditor` methods.
2. After successful **audio-affecting** mutations, those methods
   increment `SongDocument.AudioRevision` and call
   `DocumentWorkspace.NotifySharedAudioObjectCommitted` with the
   exact changed Envelope/Filter ID. Invalid edits, no-op drafts
   and display-only envelope bounds do not emit an audio event.
3. `MainWindow` subscribes to `SharedAudioObjectCommitted`,
   checks the current workspace/document and whether its transport
   supports `ILiveSynthEditTransport`, then schedules
   `PublishSharedObjectEditAsync(document, objectId)`. This
   does not open audio if no audition is active.
4. The real `SongPlaybackTransport` reuses the running audition
   session, prepares affected frozen Synth revisions off-PCM
   through the ordered background queue, and publishes reachable
   Synth parents in dependency order, now including Instrument
   tone edges from slice 44.

**New headless integration acceptance:** tests construct a real
`MainWindow` and `SongPlaybackTransport`, with a deterministic
recording live-output backend and detached-preparation factory
(no physical sound device). They start an actual live *Synth*
note whose graph includes an Instrument tone that references both
a shared Volume Envelope and a shared Filter, plus an upstream
Synth parent. Using the actual node Envelope and Filter editor
Apply paths, the test requires one publication of the child and
then its parent for each changed audio revision, publication
ordered after the initial Note On, immutable snapshot revisions
equal to the committed document revision, no unrelated Synth
publications and exactly **one audio session** across both edits.
Invalid/no-op node drafts and unchanged Filter drafts produce no
additional publication. A second test uses the *actual script
Envelope editor* and confirms successful expression changes
publish the Instrument-dependent Synth while invalid C# expression
drafts do not alter document revisions or audio.

**No new production callback was necessary**; this slice closes
the previously untested UI-to-transport integration boundary and
validates the slice 44 routing with actual authoring controls.
It does **not** prove or enable migration of genuinely changed
Instrument-owned Filter or Envelope DSP history into an already
playing Sample voice. Existing note compatibility remains
conservative; later notes use the updated frozen snapshot.
SourceId reassignment, compound waveform smoothing,
remaining inspector-to-audio acceptance, realtime throughput
and physical listening remain **open**. Keep PR #12 draft.


## Forty-sixth slice: held Instrument Sample adopts a changed node Volume envelope

**First bounded real held-DSP migration after the UI/publication slices.**
A live Synth consisting of a **single direct Sound Source node** that
binds an ordinary data-driven Instrument's selected **terminal Sample**
can now adopt a genuinely changed shared **node-based Volume envelope**
without retriggering the Sample or rebuilding its state. This does not
retroactively rephase its PCM playhead: the original integrated Sample
position, Source frequency basis, Note Off event and envelope frame
offset remain owned by the existing invocation.

**Frozen candidate preparation, not PCM-time resolution:**

- During compilation of a live-audition Synth with a single direct
  Instrument Source, the Instrument pre-resolves candidate direct
  Sample/Node Volume pairs for its frozen data-driven tone specifications
  outside the audio callback. The tone-to-table-slot mapping stays
  immutable. Ordinary rendering/export does not perform this additional
  audition preparation.
- At the atomic audio publication boundary, the original selected tone
  slot is checked against the revised frozen Instrument mapping.
  Eligibility requires the same selected Source ID, tone pitch/NNA,
  envelope references and Filter absence, **the same terminal Sample
  identity/timeline** (including independently frozen immutable PCM),
  active Note Off/release control by a Volume envelope, no other selected
  envelopes or tone DSP state, and a continuing (not retired) Source.
  Both the prior and revised active curves must be node-based.
- An unsupported selected Sample/PCM, Filter, scripted selection or
  compound graph is not replaced with a speculative state. Existing
  Sample history is preserved; other compatible graph scalar updates
  remain eligible. This slice deliberately does **not** enable arbitrary
  Instrument/SourceId replacement, tone Filter migrations, or Multi
  descendant Volume updates.
- The running SourceVoice owns a mutable **immutable-curve reference**,
  separately from its initial SoundInvocation.Configuration. The only
  PCM-boundary mutation is adoption of an already-frozen curve and
  scalar transition metadata. No Sample state construction, lookup,
  envelope compilation, array allocation or audio replay happens there.

**Audible interpretation:** the existing Sample keeps playing at its
actual position. The old and new Volume curves are both evaluated at
the **same continuing envelope frame and Note Off origin**, and their
gains blend for **64 output frames** using the already-established cubic
smoothstep. An edit midway through a fade restarts from the actual
last blended gain without stacking outgoing histories. The next
freshly started note independently uses the newly frozen Instrument.

**Release accounting:** while the gain transition is active, the
outgoing curve's release contribution is bounded to at most its
64-frame fade boundary and must not be erased merely because the
new curve has a shorter release. The new curve may lengthen or shorten
the remaining tail; the effective audible bound is the maximum of the
new release end and the outgoing release clipped to the fade boundary,
still limited by the original Sample's natural EOF. A conclusively
retired voice is not resurrected. The original Note Off and
SourceVoice.EnvelopeFrameOffset remain untouched.

**Red-before-green evidence:** the new renderer regression originally
failed on a measured held PCM discrepancy (~0.000217) at commit
`55f9a03` despite correctly published graph changes. The implementation
commits `94405df` and `6934c72` made that red test pass in
GitHub Actions [#42964893697](https://github.com/logiclrd/Heresy/actions/runs/42964893697).
The fixture uses a varying Sample waveform, an actual independently
frozen old/new Instrument, and a shared node Volume curve whose
held level changes from 1.0 to 0.25. Its per-frame oracle verifies
the 64-frame gain blend and unaltered Sample position; it also verifies
the ten-frame original-clock release (exclusive end 330 after Note Off
at frame 320), a genuinely new note using the edited curve, and zero
managed PCM render allocations.

Additional guards require that changing terminal Sample PCM without
changing the selected Source ID does **not** steal the held Sample's
state, and that non-audition Synth rendering retains its previously
frozen behaviour. Release-overlap cases exercise both directions of
ten- versus twenty-frame release changes with Note Off **during** the
64-frame gain transition, including audible PCM oracle and exclusive
end 180 for Note Off at 160. These are separate from the prior
editor-to-transport publication tests.

**Still open:** an actual editor-through-transport-through-recorded-PCM
integration (the earlier real-editor test stops at publication);
scripted/complex/nested/Multi Instrument state, Pitch/Panning/Filter
envelopes and tone Filter history migration; SourceId reassignment;
rapid-edit stress beyond the bounded fade rule; physical listening
and sustained underrun/CPU acceptance. This is a scoped first migration
case, **not** the full all-property acceptance milestone. Keep the
main TODO unchecked and PR #12 draft/unmerged.


## Slice 47 — Instrument release lifetime, selection and accessible shared-Volume audition

**Instrument tone release:** A sustaining Instrument Volume Envelope correctly
leaves its loop on Note Off, but a referenced Synth with no internal release
could stop its oscillators after only one frame. The outer envelope advanced
while its source had already become silent. A new prepared Pattern PCM
regression first failed for both Forward and PingPong sustain loops
([red run #42968779027](https://github.com/logiclrd/Heresy/actions/runs/42968779027)).
A Synth hosted by an Instrument with a Volume Envelope now preserves internal
independent audio excitation through the external envelope's release deadline,
with no PCM allocation or audio replay. The regular composed voice lifetime
still bounds final output. The fix passed
[run #42968889454](https://github.com/logiclrd/Heresy/actions/runs/42968889454).
This is ordinary Pattern Note Off semantics, **not** held Volume-curve
migration for a nested Synth tone.

**Instrument grid:** Shift-click was vulnerable to ListBox focus/selection
events replacing its anchor. Capture the original anchor at the ListBox tunnel
stage, before the cell/container focus events, and restore it at pointer
release. Ctrl+A (also Meta+A) selects all projected tone rows, excluding the
reserved entry draft. These selection changes never alter audio revisions;
model and headless pointer/key regression tests exercise the actual behavior.

**Preview workflow:** Slice 46's single-Source live Instrument Volume migration
was not manually accessible: leaving the Synth editor stops its preview, and
the Instrument's shared Volume curve was editable only elsewhere. A Source
node selecting a data-driven Instrument now exposes its referenced Node
Volume envelopes in a **modeless shared editor** in the Source inspector.
Multiple referenced Volume envelopes can be selected by name/ID. Shared
edits go through the existing document revision publication path, while the
Synth editor and preview remain mounted. A **latched** note continues when
focus moves to the auxiliary window; Hold-mode keys release on focus loss.
Leaving the Synth view still stops its preview voices and closes its
auxiliary envelope windows.

**Listening fixture:** A one-node Synth graph consisting of a Sound Source,
referencing an Instrument with a tone selecting **directly a looping or long
Sample** and a Node Volume Envelope. Latch a note, open that Source inspector's
shared Volume envelope, and edit its gain. Slice 46 should crossfade the
already-sounding Sample without rewinding its playhead. A tone selecting
another Synth, or a graph with multiple Sources and Operators, remains outside
this migration. The UI interaction test does not replace physical listening
or complete editor-to-held-PCM integration.

PR #12 remains draft and unmerged; the all-property TODO remains open.

## Slice 48 — recursive Definite/Indefinite Note Off lifecycle

The source-lifetime bug isolated in slice 47 exposed a general distinction:
Note Off is **Definite** at the Pattern/preview root, but becomes
**Indefinite** below an ancestor with a sustain-release lifecycle. The
lifecycle is present if **any active/reachable, selected envelope sustain
loop** exists in the recursively bound sound subtree, regardless of whether
the envelope controls Volume, Pitch, Filter, Panning or graph modulation.
A transparent Instrument delegates the lifecycle to the bound child.

The complete normative event table, natural EOF and independent tail rules,
multiple release endpoints, early permanent-zero final-Volume retirement,
and the script-envelope compatibility decision are specified in
[docs/note-off-lifecycle.md](note-off-lifecycle.md). The old special-case
outer-Volume-to-Synth lifetime extension and Sample release policy were
removed. Graph and Instrument voice state now propagate Off disposition
without PCM-time source re-resolution or playhead reconstruction.

Red-before-green acceptance initially established that a non-looped Pitch
envelope must **not** extend an unsustained note. Automated tests now include
nested Instrument→Synth Pitch-only release and Synth→Instrument Volume/Pitch
release, including completed Volume0 versus terminal Volume10%, plus the
existing Multi, shared Source, filter-tail and live gain transition suites.

This is a source lifecycle refactor, not completion of all-property live Synth
editing. Physical device listening and full interactive migration remain
open; PR #12 stays draft and the main TODO stays unchecked.

### Manual audio verification — direct Instrument Sample Volume editing (2026-11-11)

User verified the slice 46/47 editor-to-playback fixture in a local build:
a Synth with a single Source node referring to an Instrument whose selected
Tone Definition has a Volume Envelope. The Source inspector's **Edit Shared
Volume Envelope** control appeared; changing the envelope while a preview
note was **latched** produced an immediate and smooth audible change without
stopping the note. This closes the *manual listening* check for this narrowly
supported direct Instrument-backed Sample Volume path only. It does not verify
multi-Source/Operator, nested Synth tone migration, other parameter families,
or the separate Definite/Indefinite Note Off lifecycle behavior. The all-property
acceptance TODO and PR #12 draft status remain unchanged.

## Slice 49 — atomic shared Volume adoption in compound Source/Operator graphs

The previous slice 46 held-Volume migration was limited to a Synth whose
compiled graph had one direct Instrument-backed Sample Source. In particular,
two already-sounding Instrument Source nodes joined by an Operator could
receive a newly published shared Envelope definition while both continuing
voices remained on their old curves.

**Bounded extension:** Live-audition preparation now freezes direct
Instrument→Sample node Volume candidates for every reachable compiled Source
node, rather than only when the entire Synth contains one node. A compound
Synth can therefore update multiple still-playing Instrument Samples through
Operators without restarting their states. Each continuing source retains
its selected frozen tone slot, PCM identity, integrated fractional Sample
position, original pitch and envelope clock, Note Off origin, and the existing
64-output-frame smoothstep gain transition. The 64-frame transition is applied
to each branch's gain, not globally to the Operator or to arbitrary controls.

**Atomic eligibility:** Before changing any Source Volume reference, the PCM
owner preflights *all* participating active Instrument-backed node Volume
sources against their individually prepared immutable candidates. The
selected tone, unchanged Sample PCM/timeline, New Note policy, absence of
unsupported tone Filters or other tone envelopes, and live state must match.
If one participant cannot be proved compatible (for example, its selected
Sample PCM changed behind an unchanged SourceId), **none** of those Source
Volume curves are adopted. This conservative rejection does not prevent
independently compatible graph scalar edits from adopting. A second, bounded
nonallocating pass commits only actually changed curves after the full
preflight succeeds. All preparation, tone selection and Sample resolution
remain off PCM. Ordinary arrangement/export retains frozen-voice semantics.

**Multi boundary:** The change initially exposed two preexisting
`IndependentFrozenNodeVolumeCurvesKeepInstrumentSampleRelease` cases:
a Multi onset rebase rejected by its worker must not also migrate an edited
child Instrument release as a side effect. The implementation preserves the
old allowance for a Multi constituent that consists of one direct Source,
but does **not** broaden Volume migration to *compound* Multi-generated
subgraphs. Their phase/onset and Volume transactions require a separate
coordinated design. Both original tests were retained unchanged.

**Red/green evidence:** The newly added two-Source Add-Operator held-PCM test
failed on the original code in [red CI #43011714291](https://github.com/logiclrd/Heresy/actions/runs/43011714291);
it confirms both already-playing voices adopt the edited shared node curve,
with original Sample continuity, 64-frame smoothstep, Note Off deadline and
zero managed render allocations. A second regression uses two distinct
Instrument Sources, with an incompatible terminal PCM for one, and proves
all-or-none Volume rejection while an independent Constant edit still applies.
The first broad implementation exposed the existing Multi-onset test failures;
the corrected scope is [green CI #43012576902](https://github.com/logiclrd/Heresy/actions/runs/43012576902),
with **1,034 render**, **355 playback**, **1,149 UI**, and other passing suites
of **910, 105 and 29**, plus successful Linux ARM64 codec validation.

**Still open:** physical-device listening of the two-Source fixture and
recursive Note Off semantics; live Volume edits in compound Multi children,
arbitrary Instrument-selected Synths, tone Filters, scripted selectors, other
envelope types, selected SourceId reassignment, rapid concurrent edits and
real hardware throughput. The user-verified *single* direct Source audition
from slices 46–48 remains recorded separately. PR #12 remains **draft and
unmerged**, and the all-nonstructural-properties TODO stays **unchecked**.

### Manual audio verification — two Instrument Sources under Add Operator (2026-11-11)

User pulled the slice 49 build and listened to a Synth containing **two
Source nodes referring to the same Instrument**, combined by an Operator.
That Instrument's Tone Definitions are identical across the note range and
all refer to the **same shared node Volume envelope**. To make the branches
independently audible, the user set the second Source's authored base
frequency to **1.25 times** the first (a just-intonation major-third 5:4
ratio). The material was a short music-loop Sample rather than a single
pitched instrumental note, yielding an unusual but clearly distinguishable
two-pitch result.

With a note latched and both Sources sounding, a live shared Volume envelope
edit through the modeless editor opened from **either** Source immediately
affected **both constituent sounds**, without interrupting their playback.
The user reported that the realtime changes worked perfectly. This is
**physical listening acceptance of the bounded multi-Source/Operator,
Instrument→Sample shared-Volume edit path**, in addition to the previously
accepted single direct Source path. The distinct Source frequencies make
this stronger evidence of independently active Source branches than two
identically configured Source nodes.

This observation does **not** independently establish the underlying
64-frame numerical transition oracle, allocation profile, exact Note Off
release semantics, complex Multi-generated child adoption, arbitrary Source
replacement, scripted/nested tone histories, or other inspector parameters.
Those remain covered only to their separately documented extent. At the time of this two-Source Volume-edit report,
manual listening for slice 48's generalized Definite/Indefinite Note Off
lifecycle was still outstanding; **subsequent listening acceptance is
recorded immediately below**. PR #12
remains draft/unmerged; the broad all-nonstructural-properties TODO is open.


## Manual listening acceptance — slice 48 recursive Off lifecycle (2026-11-11)

The user constructed audible **Synth preview and Pattern** cases covering
the slice-48 Definite/Indefinite Note Off rules. See the full per-fixture
results and remaining limits in [note-off-lifecycle.md](note-off-lifecycle.md).

**Observed passes:**
- **Parent-only release:** An Instrument has a sustain-loop Volume Envelope;
  its selected child Synth has none. Release works for Oscillator, one Sample
  Source and two Sample Sources under Add.
- **Child-only release:** An Instrument with no envelope selects a Synth whose
  Sample Source's *pitch multiplier* is driven by a sustain-loop Envelope;
  the same case was checked with an Oscillator and Volume Multiply. An
  external Note Off releases the nested child instead of cutting it.
- **No sustain anywhere:** Bare Oscillator and Sample Synths and an Instrument
  selecting a no-lifecycle Synth cut immediately on Definite Off.
- **Non-Volume release:** The pitch-multiplier sustain/release is audible
  with both Sample and Oscillator, without any sustaining Volume Envelope.
- **Independent concurrent releases:** Sample-backed Synth with a roughly
  1-second Volume release ending near full Volume and a separate, roughly
  3-second spiky pitch-multiplier release plays correctly in Synth Test and
  Pattern Note Off. Neither release prematurely ends the other.
- **Cut after Off:** Explicit Pattern CUT immediately stops an in-progress
  release.
- **Early Off:** An OFF one row after Note On plays the envelope through
  its sustain-loop region once, without repeated looping.
- **Nested Sample EOF:** `layer3-continuous-mono.mp3` (under 0.5 seconds)
  ends naturally during a 1.3-second Instrument Volume release.
- **Filter tail:** Comb with 2 seconds authored tail and maximum feedback
  continues to ring after a short Instrument Volume Envelope reaches zero,
  including after Pattern OFF.
- **Two child envelopes:** A Synth with two Oscillator branches and
  differently timed Pitch sustain/release curves lets the slower branch
  complete; early OFF runs both curves once without looping.

**Not a valid manual retirement test:** Whether a finished final Volume
Envelope at **exactly zero** stops internal voice processing cannot be
distinguished by hearing silence. Leave this to automated render/state
tests; do not mark it physically tested. A separate repeated-OFF-origin
test was also not performed.

**Conclusion:** Meaningful physical-device listening acceptance for the
**core recursive Note Off lifecycle** is now recorded, beyond the previous
one Instrument→Synth case. This closes the broad *core lifecycle listening*
gap while preserving exact untested subcases and performance qualifications.
It does not authorize merging PR #12 or completing the independent
all-nonstructural-Synth-property live-update TODO.

## Slice 50 — audition-only Oscillator shape smoothing through stateless Operators

The earlier direct one-Oscillator shape smoothing (Waveform, Curvature,
Vmin/Vmax) worked only when that Oscillator was the Synth's entire output.
The same nonstructural shape edit routed through an **Add or Multiply
Operator** could abruptly change an already sounding note even though
its oscillator phase and the rest of the graph were compatible.

**Bounded adoption:** A live audition with a compiled graph containing
only reachable **Oscillator, Constant and Operator** nodes now applies
the same 64-output-frame cubic smoothstep to the **audible final graph
output** when any contained Oscillator's Waveform, Curvature, Minimum or
Maximum changes. Each Oscillator keeps its original independent phase
integrator and continues advancing its new waveform normally. The
outgoing value is the *last actual audible value of the entire graph*,
not the first child Oscillator or a sum reconstructed from stale graph
definitions. Once the fade completes, the exact new authored graph
output is heard at its uninterrupted phase. A second edit during the
fade starts from the currently blended final output. The compiled
`Values` remain raw for control/modulation correctness; only the final
`AudioValues` output is smoothed.

**Scope/guards:** This is audition-only. The classifies-graph test is
computed on the non-PCM compilation thread; publication performs a
bounded scalar comparison and render adds no allocations or child
states. Frequency-only edits do not fade. Non-audition playback/export
retains the existing immediate sample values. Graphs involving **Sound
Sources, Filters, Multi or compound Multi-generated children** are
deliberately excluded: their continuity and onset transactions require
separate prepared state-specific migration rather than treating their
output history as a stateless oscillator.

**Regression evidence:** New
`FmSynthCompoundShapeLiveTests` exercises Add/Square,
Multiply/Curvature and Add/Vmin-Vmax shape changes, each comparing all
64 fading frames against the exact graph-level smoothstep oracle and
checking post-fade phase equivalence and zero managed PCM allocations.
A rapid Square→Sawtooth revision checks interruption against the
last *blended* output; a frequency-only retune checks the absence of
a gratuitous fade; ordinary non-audition rendering has its own
negative-scope invariant.

The first regression commit **failed red** as intended in
[CI #43033101533](https://github.com/logiclrd/Heresy/actions/runs/43033101533):
four new compound shape cases observed an immediate output jump, with
the existing render tests unchanged. Implementation commits followed.
Final CI qualification and real-device listening are separate from
the red/green reasoning; the wider all-property live-edit milestone
and draft/unmerged PR status remain unchanged.

### Manual listening — slice 50 compound Oscillator edit (2026-11-11)

The user tested a Synth with **two Oscillators** playing together through
an Operator, using the live **Test** note and changing Oscillator
properties as suggested after slice 50. The already-sounding Test note
responded as expected. This constitutes **local physical listening
acceptance** of the bounded compound Oscillator/Operator shape-edit
route, in addition to its render/phase/zero-allocation regressions.
The report does not certify other graph node classes, every Operator,
all possible property combinations or long-running hardware throughput.
The all-nonstructural-property milestone remains open.


## Slice 51 — audition-only Constant/Operator output edits

Slice 50 made the output of a compound stateless Oscillator graph
click-safe when an Oscillator's waveform, Curvature or output range
changed. Two other **nonstructural** Synth inspector properties already
published into a held note, but could produce an immediate PCM jump:
**Constant.Value** on an audible gain/mix path, and
**FmOperatorNode.Operation** (Add/Multiply/Minimum/Maximum) on the
audible path.

**Audible-path criterion:** During immutable graph compilation,
an eligible graph consists entirely of reachable **Oscillators,
Constants and Operators**. The compiled output node is traced
backward through **Operator audio inputs**; an Oscillator is a leaf
even if it has a separate multiplier/modulation input. A changed
Constant or Operator that lies on this reachable **audible-value
path** may start a fade. A changed Constant/Operator that exists
*only* upstream of an Oscillator's frequency multiplier is a
**control edit** and must **not** hold or crossfade the graph's
output; it affects the forward phase rate immediately. An Oscillator
shape edit follows the same audible-path condition. Compilation
constructs the path bitmap off the PCM thread; no graph search,
asset resolution or allocation occurs during rendering.

**Audible output transition:** For a compatible running audition,
a changed audible Constant value or Operator operation starts
the same 64-output-frame cubic smoothstep used in slice 50:
`out(n) = lastAudible * (1 - w(n)) + newGraph(n) * w(n)`,
`w(n) = t² (3 - 2t), t = clamp(n/64, 0, 1)`.
The previous *whole-graph output* is captured at the publication
boundary; untouched independent Oscillators keep their real
integrated phases. A mid-fade parameter edit captures the
currently blended output and begins a new bounded transition,
not a chain of old generation buffers. The raw graph `Values`
used by modulation/control remain unaffected. **A one-Constant
output graph** is eligible, as are larger stateless graphs.

**Boundaries:** Audio-only fade is enabled only for explicitly
live/audition-enabled Synths. Ordinary playback/export has
immediate authored values, and frequency-only parameter changes
keep their old continuous pitch policy. Source, Filter and Multi
graphs, including compound Multi-generated subgraphs, remain
outside this stateless-output transaction. No stateful DSP history,
Source playhead, Multi onset or release origin is reconstructed.
The previously supported single Oscillator shape transition
remains available even within a simple Multi constituent.

**Red-first acceptance:** A new test group extending
`FmSynthCompoundShapeLiveTests` first failed four actual PCM
output invariants on the old implementation:
(1) Constant gain in Oscillator × Constant,
(2) one-Constant output,
(3) Add→Multiply operation, and
(4) a new operation during an already-running Constant gain
transition. [Red run #43038824435](https://github.com/logiclrd/Heresy/actions/runs/43038824435).
Positive tests assert the exact 64-frame oracle, original
post-fade phase and zero PCM managed allocations. Negative
tests ensure a Constant connected only to an Oscillator pitch
multiplier **does not** freeze sound, and ordinary non-audition
rendering retains immediate scalar/operation semantics.
CI qualification and real-device listening are recorded
separately; the complete all-property Synth live-update TODO
remains open and PR #12 stays draft/unmerged.


### Manual listening — slice 51 audible parameter changes and structural isolation (2026-11-11)

The user performed the suggested physical-device **Test** listening check
for slice 51 and reported **pass**. The suggested fixture had an
Oscillator multiplied by a Constant, with live Constant gain changes
and an Operator mode change (Multiply→Add) while the Test note was
ongoing. The result provides manual audio acceptance of the narrow
stateless **audible Constant/Operator edit** route in addition to the
automated 64-frame PCM and phase regressions. This observation does not
establish every scalar/mode combination or long-running device
throughput.

During the same session the user inadvertently exercised an explicitly
**structural graph edit**: an already-sounding
`Multiply[Oscillator, Oscillator]` was rewired into
`Multiply[Oscillator, Oscillator, Constant]`. There was **no audible
change** to the still-playing note, as intended: adding a connection
changes the processing topology, and existing voices may remain on
their previous frozen generation. This is independent manual
confirmation of *old-voice structural isolation*. The user
subsequently clarified that the **new-note side was also physically
tested**, so the structural-generation scenario has both halves:
the existing note remained on the former graph, while a new Test note
rendered from the revised three-input topology.

The user supplied a screenshot of a later, clearly identified
three-input example with **Maximum[Oscillator, Oscillator, Constant]**,
using a curved Sawtooth at **440 Hz**, a curved Sine at **560 Hz**
and a Constant of **0.2**. The output scope shows the result of the
Maximum operation, and the user reports being able to observe the
dominant input switching as the shaped oscillator values cross.
This scoped example supports audible/output-scope confirmation that
newly triggered notes use the current three-input graph, including
the added Constant input. The screenshot itself shows **Maximum**,
not the earlier **Multiply** rewire; retain those as distinct fixtures.
No precise numeric PCM curve measurement is claimed from the screenshot.

The slice-51 bounded live-edit listening fixture is accepted, with
the general all-nonstructural-properties TODO and draft/unmerged PR #12
status unchanged.


## Slice 52 — bounded live graph Node Envelope output changes

The stateless Synth graph audition now supports **audible edits to frozen
node-based Envelopes**, expanding the prior Oscillator/Constant/Operator
output fade. A changed `FmEnvelopeNode.EnvelopeId`, or changes to the
shared Node Envelope object's frozen curve under an unchanged ID,
previously passed the wire-shape preflight and used the revised curve
immediately at the original note clock. When the Envelope was used
as **Volume** (for example, `Multiply[Oscillator, Envelope]`), this
could produce a large first-frame jump even though the running
Oscillator phase did not rewind.

**Bounded listening contract:** When a held live-audition graph is
composed entirely of **Oscillator, Constant, Operator and Envelope**
nodes, and a directly audible frozen `NodeEnvelopeCurve` changes,
fade the *final audible graph output* using the already established
**64 output-frame cubic smoothstep** from the previous actual audible
sample. Each existing Oscillator phase continues uninterrupted; the
new curve evaluates on the note's original frame/time basis. A second
edit mid-fade starts from the currently blended value. The prepared
graph's **Operator audio-input traversal** distinguishes Envelopes
that are audible themselves from Envelopes feeding only an Oscillator
frequency multiplier: pitch-only edits retain immediate forward
frequency integration without an unnecessary audible fade.

**Frozen-curve equivalence:** A full-song compilation often produces
a new `NodeEnvelopeCurve` *object* representing the same authored
points, sustain indices, loop mode and timings. Do not start a fade
merely because object identities differ; compare via the existing
`HasSameFrozenShapeAs` invariant. Scripted curve implementations
remain outside this specific fade policy (identity-only equivalence);
they are not silently treated as interchangeable with Node curves.
Switching between separately bound Node Envelope IDs is handled
when both resolved curves are valid.

**Already-releasing note:** A live voice whose **Note Off has already
started** may have an active release timeline and a downstream
completion bound. Until an explicit dual-release state policy is
designed, an edit changing an **audible** frozen Envelope in an
otherwise eligible stateless graph is **rejected by whole-voice
preflight** while Note Off is active. The old voice continues
on its original curve/release origin; a future Note On uses the
new revision. Unchanged equivalent curves and non-audible pitch
controls are not rejected for this reason. An edit made while held
can be adopted and then released later on the revised curve; edits
during a just-started fade and the exact release-tail handoff remain
distinct cases for future deeper testing.

**Strict isolation:** The extra fade remains *audition only*. Normal
Pattern playback/export uses original immediate authored values.
No Source, Filter, Multi or compound Multi-generated graph is
newly admitted, and no Sample playhead, Filter/Envelope state or
extra PCM buffer is reconstructed in the realtime callback.
Only frozen curve snapshots and scalar PCM state are examined.

**Red-first regression:** New
`FmSynthLiveGraphEnvelopeTests` demonstrated three failures before
the fix in [CI #43043724316](https://github.com/logiclrd/Heresy/actions/runs/43043724316):
(1) changed same-ID node Volume, (2) changed selected Node Envelope ID,
and (3) rejection of an edited audible curve during a previously
started release. Non-failing negative cases checked equivalent
frozen wrappers, an Envelope used solely for Oscillator pitch
control, and ordinary playback. Exact 64-frame render oracles,
retained note-clock/oscillator-phase expectations and no managed PCM
allocations cover the positive behaviour. CI green and real-device
listening are separate qualifications. This slice does not complete
the full live-property milestone; PR #12 remains draft.


### Manual listening — slice 52 Node Envelope switching (2026-11-11)

The user physically tested the suggested running Synth **Test** note
with an audible Node Envelope on a Multiply Operator, changing the
Envelope node's selected Envelope object while the note continued.
The user reported the live switching **passed**: the note responded
as expected as the different Envelope objects were selected.
This is **physical listening acceptance of the held-note
selected-Envelope-object path**, alongside the CI math and phase
regressions for the 64-frame transition. It does not independently
establish manual testing of editing shared node points in place,
pitch-only Envelopes, active-release rejection, or every nested
Source/Filter/Multi topology. Those narrower cases remain verified
only to the extent documented by automated tests.


## Slice 53 — bounded audible output transitions around continuing direct Sample Sources

Slice 51's audible Constant and Operator 64-frame transitions were
initially limited to entirely stateless Synth graphs. A common audible
case remained abrupt even though it did not require replacing any DSP
history: a held Synth with an existing, directly Sample-backed Source
feeding an Operator and a Constant (for example
`Multiply[Source(Sample), Constant]`). Editing the **Constant.Value**
or the **Operator.Operation** adopted the revised graph values
immediately, without moving the Sample playhead, but could jump from
one PCM frame to the next.

**Bounded extension:** Allow the established **64-output-frame cubic
smoothstep** around *continuing* directly Sample-backed Source leaves,
when the rest of the compiled graph consists of Oscillators, Constants,
Operators and Node Envelope controls. No nested Synth, Instrument,
Filter or Multi child is admitted by this slice. The Sample itself
remains bound to the original `SourceVoice.Sound` and
`SampleSoundState`; its relative playback position, integrated
frequency trajectory, EOF, note clock, SourceId and Note Off semantics
are **not** reconstructed, reset or exchanged. As in slices 50–52,
only changed nodes on the actual output-value path can trigger the
fade. Controls used only to modulate the Source's frequency do not
acquire an unrelated audible hold.

**Frozen Sample preflight:** Before starting an output fade, every
compiled Source in the eligible graph must still point to a directly
Sample-backed child with the **same SourceId**. The old compiled
Sample, newly compiled Sample and already-bound Sample must share
the same immutable PCM identity, loop/channel/timeline geometry
(or exactly the same SampleSound object), proven by existing
`CanSharePreparedOnsetTimelineWith`. Any ended/finishing Source,
Source with tone-Filter histories or changing Sample data, or an
already-Note-Off voice, is **ineligible for this new audible
crossfade**. These conditions are checked using prepared and
already-bound arrays, with no Sample lookup, copying, fresh child
state or managed allocation inside PCM. This is an **output-control
smoothing rule**, explicitly **not** a solution to nonstructural
SourceId reassignment or arbitrary Sample/Instrument replacement.

**Tests:** New `FmSynthLiveSampleOutputTests` covers an already
running Sample feeding Multiply and Add with changed Constant
values; an Add↔Multiply operation edit; a second gain edit during
the first fade; exact 64-frame graph-level oracles, continued Sample
playhead, non-audition playback isolation, zero managed PCM
allocations and finite natural EOF. An initial test-only CI run
[CI #43047797715](https://github.com/logiclrd/Heresy/actions/runs/43047797715)
was **not** a valid red regression run: the new test file had a
missing namespace import and the compiler stopped before the
new invariants could execute. Do not cite it as red-before-green.
The import was corrected and the implemented behaviour is being
qualified in subsequent CI. Physical listening remains pending.

**Out of scope:** SourceId replacement, Instrument or nested Synth
selection, source-specific Envelope/Filter reconfiguration,
Filter/Multi onset histories, overlapping released revisions,
Sample data replacement and comprehensive high-load UI tests
remain separate PR #12 work. PR #12 stays draft/unmerged and
the all-nonstructural-properties TODO remains open.


### Slice 53 — manual device acceptance (2026-11-11)

The user physically tested an existing Synth whose **looping Sample
Source** was augmented with a **Multiply Operator and editable
Constant**. The graph in the supplied screenshot has an **Envelope
node connected to the Sample Source's multiplier/modulation input**;
the Sample Source output is then multiplied by **Constant 0.1**.
The user reports that editing the Constant while the Test note
continues **applies as expected**, confirming local listening of the
slice-53 gain-control route with a modulated looping Sample.

Scope of the report: the user did not separately report a
Multiply→Add operation edit or a finite/nonlooping Sample EOF check.
Those cases retain automated regression coverage, rather than
being marked manually listened to. This is acceptance of the
specific audible Constant edit, not SourceId reassignment or
Instrument/Filter/Multi migration.


## Slice 54 — Sample/Envelope interaction and deterministic Noise mixing

The slice-53 listening fixture combined a **looping Sample-backed
Source**, a graph Envelope connected to that Source's **frequency
multiplier**, and an output Multiply Operator with an editable
Constant. The user reported a successful live Constant gain change.
That listening observation is retained separately from the numerical
Source-frame and phase tests.

**Next compatibility boundary:** A graph consisting of an audible
Sample-backed Source, scalar/Envelope controls, and a **Noise node**
could previously lose the otherwise-eligible 64-frame output fade
when its audible Constant or Operator was edited. This was not a
Noise-state incompatibility: `FmNoiseNode` computes its sample as a
counter-based deterministic function of **voice seed, node ID and
output frame**, without an evolving random generator. No Noise
history has to be transferred between compiled graph generations.

**Implementation:** Admit `FmNoiseNode` to the existing bounded
audition-only output-fade eligibility alongside Oscillator,
Constant, Operator, Envelope and directly Sample-backed Source
leaves. All earlier actual-output-path tests, bound Sample PCM
identity preflight, retained Sample streaming position/EOF and
already-releasing-note guards remain in place. A changed audible
Constant or Operator in `Add[Noise, Multiply[Sample, Constant]]`
can now join the preceding audible graph output with the same
**64-frame cubic smoothstep**, preserving each Noise sample's
deterministic frame identity and the Sample's original playhead.
The raw graph values driving Source pitch remain unchanged.

**Test-first results:** The added
`FmSynthLiveSampleEnvelopeNoiseTests` matrix covers (1) an audible
Node Envelope controlling gain of a continuing Sample Source,
(2) an Envelope used only to control the Sample's forward pitch
rate, (3) a changed audible Constant in a Sample+Noise graph,
and (4) non-audition Sample+Noise isolation. In the initial
[red CI #43051634792](https://github.com/logiclrd/Heresy/actions/runs/43051634792),
only the Sample+Noise gain case failed as intended: the first
post-edit output differed from the previous audible sample;
the other three cases passed against the existing slice-53
implementation. A single compiler eligibility change implements
the bounded Noise extension. Tests assert the full 64-frame
oracle, streaming/forward pitch continuity and zero managed PCM
allocations. **The Sample-backed audible Envelope edit was already
supported; this test establishes coverage rather than claiming
a redundant new feature.**

**Still separate:** arbitrary SourceId replacement and instrument
tone selection, Filter/Multi histories, changes to ongoing release,
scripted Envelope editing and complete device/stress acceptance
are not solved by this deterministic-Noise extension. No
ordinary playback/export crossfade has been introduced. PR #12
remains draft/unmerged; the all-nonstructural-property TODO
remains open. Device listening for this Sample+Noise case is
not yet reported.

### Slice 54 — manual multi-property Sample and Noise audition (2026-11-11)

The user extended the previously accepted looping Sample/Envelope/Constant
Synth with an **attenuated White Noise** branch. The observed graph is:

`Add[ Multiply[Source(Sample, Envelope-controlled multiplier), Constant],
       Multiply[WhiteNoise, Constant] ]`

The user tested **seven different editor changes** on the running
Synth and reported that **all applied as expected**:

1. **#1 Envelope:** change selected Envelope definition.
2. **#2 Sample Source:** change base Frequency Hz.
3. **#2 Sample Source:** toggle Exponential Multiplier on/off.
4. **#3 Constant:** change its value (Sample branch gain).
5. **#4 Operator:** change its Operation (Sample branch).
6. **#6 Operator:** change its Operation (final output mixer).
7. **#7 Constant:** change its value (attenuated Noise branch).
8. **#8 Operator:** change its Operation (Noise branch).

The eight operations above span **seven distinct editable nodes**;
the Source contributes two independent property checks. The screenshot
shows the Envelope→Sample modulation connection and the two separately
attenuated Sample and Noise branches leading to the output Operator.
This is **physical listening acceptance of slice 54's combined
Sample/Noise graph and these property paths**, in addition to the
earlier exact-PCM automated regression. It does **not** establish
SourceId reassignment, live Filter/Multi state transfer, Note Off
during these edits, or every possible nested graph.


## Slice 55 — audible output smoothing across compatible Filter histories

The previous bounded final-output 64-frame crossfade covered stateless
Oscillator, Constant, Operator, Node Envelope and counter-based Noise
nodes, plus continuing direct Sample-backed Source leaves. A common
further graph shape had the *same* audible output discontinuity despite
a fully compatible unchanged DSP history: an audible
`Multiply[Filter[Oscillator], Constant]` or
`Multiply[Filter[Source(Sample)], Constant]`. The Filter did not
change configuration or lose its history; nevertheless, its
presence disqualified the otherwise safe gain/Operator transition.

**Strictly bounded Filter admission:** Compilation marks a configured
`FmFilterNode` with an **audio input** as eligible for the final-output
transition when all other nodes also satisfy the previously supported
Oscillator/Noise/Envelope/Constant/Operator/direct Sample rules. The
audible-path traversal descends through the Filter's **audio input**
and through all Operator audio inputs. It **does not** descend through
a Filter's separate numeric parameter-control ports or an Oscillator
or Sample Source's pitch-multiplier input, because those are control
signals rather than an additive or multiplicative output contribution.

**State continuity:** The entire voice must first pass its existing
Filter stage-identity/configuration/wiring preflight. The fade is
allowed only while each existing `FmFilterNodeProcessor.State.Chain`
is present, still accepting input, and the note has **not already
entered Note Off**. This excludes already-draining Filter tails from
a scalar held-voice transition. The existing chain, its IIR/FIR
histories, the original Sample's bound PCM playhead/pitch trajectory,
and all authored Filter tail budgets survive; **no new Filter chain,
Sample invocation, second renderer, or PCM buffer is constructed in
the realtime callback**. Changes to a Filter's own parameters use
their existing dedicated coefficient/FIR preparation and migration
policy; this slice does not replace it or grant new structural Filter
stage changes. As before, normal non-audition playback/export
does not get a live output fade.

**Test-first evidence:** New `FmSynthLiveFilterOutputTests` first
failed three audible-discontinuity cases in
[CI #43063347321](https://github.com/logiclrd/Heresy/actions/runs/43063347321):
(1) Constant gain after an otherwise unchanged active One-Pole
Filter driven by an Oscillator, (2) Multiply→Add operation after
that Filter, and (3) Constant gain after the same Filter fed by a
continuing finite Sample with an authored tail. Non-audition
rendering remained unchanged. Positive regressions compare every
frame against the 64-frame last-audible-output smoothstep oracle,
verify the original Filter/Sample histories converge to an
independently rendered revised graph after the fade, require
zero managed PCM allocations, and check that the Sample's natural
EOF and downstream Filter tail do not move.

**Boundaries still open:** This does not claim live editing of
arbitrary Filter stage topology, replacing a Sample/Instrument
SourceId, transferring a nested Instrument Filter's tone state,
compound Multi onset histories, Filter edits during an already-
releasing note, or the complete all-property UI/device matrix.
The Filter-output smoothing fixture awaits physical listening;
PR #12 remains draft/unmerged and the broader all-nonstructural-
property TODO remains open.

### Slice 55 — local listening of Filter-contained output edits (2026-11-11)

The user tested the suggested real-device **latched Test note** by inserting a **One-Pole Filter** between the continuing Sample Source and the Sample-side Operator, then changing its downstream Constant gain and switching that Operator's mode. Both changes produced the expected live result. This is **manual audio acceptance** of slice 55's bounded *Filter history preserved while audible downstream controls change* scenario. It does not constitute a separate listening test of changes to the Filter's own coefficients, Note Off during an active Filter tail, or every stateful topology.


## Slice 56 — stable Multi output controls without constituent re-onset

The preceding slice 55 listening test is now manually accepted for
a continuing Sample passing through a One-Pole Filter, with edits to
the downstream Constant gain and Operator operation.

**Gap:** A live Synth can play multiple independently phased Oscillator
constituents through a Multi while a downstream Constant or Operator
changes. The revision already keeps all existing Multi voice states,
but the old final output changed from the previous gain/mix to the
new value in a single PCM frame. The bounded 64-frame audition output
fade excluded any Multi node.

**New strictly scoped compatibility:** The parent graph may contain a
**Multi with a single direct Oscillator input** when the Multi itself
is unchanged and all authored constituent settings (Distribution,
Detune, Voice Count, harmonic/chord component expressions, phase
randomization, gain compensation, cycle length) match. The child
Oscillator's waveform, frequency, range, Curvature and multiplier
flags must also be identical. Each currently authored child must
already be initialized and active, with no Note Off, known retirement,
active onset replacement, or in-progress ratio/mix glide; dormant
reserved headroom must remain dormant. The old and next compiled
frequency ratios must be identical.

Under these constraints, the **existing** Multi child invocations
can continue at their original phases while a changed **downstream
audible Constant Value or Operator mode** starts the same one-scalar
64-output-frame cubic smoothstep from the previously audible *whole
graph* output to the revised complete Multi mix. Repeated downstream
edits can restart from the currently blended output. The Multi node
acts as an *audible leaf* in the parent's control routing: its own
constituent graph is not traversed for this outer crossfade, and
the earlier exclusion of compound Multi-generated child waveform
migration is preserved. There is no second Multi mix, new constituent
voice, rephasing or managed memory allocation during PCM.

**Test-first verification:** New `FmSynthLiveMultiOutputTests`
was **red before the implementation** in
[CI #43066845750](https://github.com/logiclrd/Heresy/actions/runs/43066845750):
four first-frame discontinuities — downstream Multiply gain, Add
Constant, downstream Multiply→Add Operation, and a second change
while an initial fade was still in progress. Ordinary non-audition
playback retained its immediate semantics. The positive regressions
assert exact 64-frame output transitions, complete agreement with
the unedited independently rendered Multi trajectory once the fade
ends, continued oscillator-phase/ratio identity and zero PCM
allocations. The implementation remains bounded to *stable*
Oscillator Multi constituents, rather than treating complex
Multi-generated Source/Filter voices or onset growth as safe.

**Remaining:** Multi distribution/voice-count/onset edits retain
their separate prepared migration and mix-glide logic. Multi
containing Sample, Instrument, nested Synth, Filter or compound
Oscillator graphs still needs coordinated acceptance before
participating in this outer fade. Filters mixed with stable Multi,
release-in-progress, SourceId replacement, and the complete
all-nonstructural-property audit remain open. Device listening for
slice 56 is pending; PR #12 remains draft/unmerged.

### Slice 56 — local audition acceptance: Oscillator, Multi and downstream controls (2026-11-11)

The user built the prescribed Synth **Oscillator #1 → Multi #2 → Multiply Operator #4**, with **Constant #3** as the other Operator input, and performed extensive local Test-note listening. The screenshot shows a 440 Hz Sine Oscillator with Curvature **5**, Vmin **-1**, Vmax **1**, and a seven-voice Spread Multi, with a downstream Constant at **0.5** at the moment captured.

During an ongoing audition, the user reported that **all tested edits updated in realtime without unwanted clicking or popping**:

- **Oscillator #1:** Waveform, Curvature, Frequency Hz, Vmin, Vmax.
- **Multi #2:** all **three Distributions** (Spread, Harmonic, Chord), Detune, Voice Count, Randomize Phase, Gain Compensation, Harmonic Components and Chord Components.
- **Constant #3:** Value.
- **Operator #4:** Operation.

This is strong physical UI-to-audio acceptance for the tested changes and their ordinary combinations. It also goes beyond the *strictly unchanged-Multi* prerequisite of slice 56's **downstream output fade**: changing Multi's own settings exercises the separate existing constituent reconfiguration, ratio glide and prepared onset mechanisms. The user's lack of audible clicks is meaningful listening evidence, but not a numerical proof of every 64-frame transition, overlapping rapid-edits/release timeline, or the equivalence of compound Multi child graphs. Those boundaries remain separate.


## Slice 57 — downstream output edits with frozen compound Oscillator Multi children

Slice 56 qualified the bounded final-output 64-frame fade for a
**stable Multi containing one direct Oscillator** when only a
downstream Constant or Operator changed. Although that protects the
phase state of multiple Multi voices, each constituent can itself
contain more than one Oscillator in a *compound stateless graph*.
The unchanged compound subgraph was previously excluded solely
because it was not the single-Oscillator fast path, leaving
downstream audible gain/mix edits discontinuous.

**Bounded extension:** A Multi whose frozen input graph consists
of **one or more Oscillators, Constants and Operators** is now an
eligible audible leaf for the same *parent-only* 64-output-frame
cubic smoothstep. Each Multi child retains all of its independently
integrated Oscillator phases, unchanged mixer controls, and
original per-voice ratio. There is no child onset rebuild or
second child voice. The parent graph captures the last actual
audible output and fades to the revised full Multi mix; repeated
edits restart from the last blended sample. This is **not** a
license to use the output fade for changes inside the compound
Multi subgraph.

**Exact frozen-child proof:** The old and next child must have
identical compiled node counts, output node index, oscillator
count, note policy, node IDs/types/compiled input indices and
Oscillator phase indices. Every Oscillator must match all
authored frequency, Waveform, Curvature, Vmin/Vmax, Constant
and multiplier properties. Every Constant must match its
numeric Value; every Operator must match Operation. No Node
Envelope, Noise, Source, Filter, nested Multi or graph
rewiring is admitted in this first compound-child extension.
Each *actually bound* Multi child's sound is compared against
the old frozen subgraph as well, guarding against stale
generations. The outer Multi's Distribution, ratio definitions,
Voice Count, Detune, Randomize Phase, Gain Compensation, cycle
length, active mix ratios and initialized constituent states
must pass the pre-existing stable-Multi guard, with no
already-releasing note, dormant headroom activation, or
onset/ratio glide. Checks run on precompiled arrays without
allocating or rebuilding DSP histories.

**Red-before-fix regression:** New
`FmSynthLiveCompoundMultiOutputTests` uses a compound
`Add[Sine 390 Hz (curved), Triangle 515 Hz]` child feeding
a three-voice Spread Multi, then a Constant and parent
Multiply/Add Operator. In
[CI #43077451059](https://github.com/logiclrd/Heresy/actions/runs/43077451059),
the first commit produced **four intentional discontinuity
failures**: downstream Multiply gain, downstream Add Constant,
Operator mode, and a second edit during the first fade.
The ordinary non-audition rendering negative case passed.
Positive cases check the complete 64-frame smoothstep, exact
post-fade agreement with an independently advanced reference
(including *both* phase integrators per constituent), repeated
edits, and zero PCM managed allocations.

**Scope still open:** Edits to constituent shapes and controls,
Multi distribution/voice growth/onset, compound child
Source/Filter/Envelope/Noise and release-in-progress still
have separate safety contracts. Physical listening on this
compound-child fixture remains outstanding; this slice
does not close the all-nonstructural-property TODO or change
PR #12's draft/unmerged status.


## Slice 58 — frozen Noise and Node Envelope in compound Multi output transitions

Slice 57 allowed the 64-output-frame audition-only smoothstep for a
**downstream Constant or Operator edit** when an unchanged, stable
Multi had a compound Oscillator/Constant/Operator child. A child graph
that added Noise or an audible Node Envelope still disqualified this
*outer output fade*, even when the newly prepared child was otherwise
frozen-identical and its incumbent voices could continue without
losing any state.

**Bounded extension:** A Multi's frozen compound child may now
contain **Oscillators, Constants, Operators, Noise and Node Envelopes**
provided it still has at least one Oscillator and all other existing
stable-Multi/parent conditions pass. Deterministic Noise requires no
new evolving state: the child retains the same bound voice seed,
Noise node ID and absolute frame, and no extra random stream is
created. Each Node Envelope remains on the original child note
clock. Independently prepared NodeEnvelopeCurve wrapper objects
are considered the *same* frozen curve only when their entire
captured sample-addressable shape matches.

**Exact frozen-state guard:** Compare the old/new child graph's
output, node count/order/IDs/types, compiled input indices and
Oscillator phase indices. Match every Oscillator waveform,
frequency, Curvature, Vmin/Vmax, constant and multiplier property,
each Constant value, and each Operator operation. Noise is admitted
only with the same node identity, as its signal depends on that
identity, the bound seed and frame. An Envelope must have the
**same selected EnvelopeId** and both old and new resolutions
must be **NodeEnvelopeCurve** instances with
`HasSameFrozenShapeAs`. A different Envelope selection or
changed curve is **not** a frozen child and cannot piggyback on
this *outer* fade; scripted or unresolved curves remain excluded.
The currently bound child also must match its old compiled
definition. The Multi's distribution, voice count, detuning,
compensation, all ratios/mix, phase/onset and release preflight
rules from slice 56 remain unchanged. Releasing or retiring
children, changing constituent properties, and any nested
Source/Filter/Multi remain outside this bounded transaction.
All checks operate on already compiled snapshots without
PCM allocations or extra child voices.

**Red-first regression:** New
`FmSynthLiveDeterministicMultiOutputTests` models an audible
child `Add[Multiply[Sine, NodeEnvelope],
Multiply[WhiteNoise, Constant]]` feeding a three-voice
Spread Multi, followed by a downstream Constant and
Multiply/Add Operator. Each separately prepared revised
graph contains a newly allocated but **equivalent frozen
NodeEnvelopeCurve**. In
[CI #43082607418](https://github.com/logiclrd/Heresy/actions/runs/43082607418),
four cases **failed red as intended** before implementation:
two downstream gain edits, one downstream Operator change
and a second edit during the first output transition.
Ordinary non-audition rendering and the later-release regression
passed before the fix. The positive oracle compares the complete
64-frame smoothstep to an independently rendered graph with the
**same explicit Noise seed**, preserving every constituent's
Oscillator phase, Noise stream and Envelope clock. It asserts
zero managed PCM allocations and correct subsequent Note Off
behaviour after a finished fade.

**Not claimed:** This does not add a new realtime migration
for *changed* Multi child Envelopes or Noise implementations,
Sources, Filters, nested Multi, arbitrary SourceId swaps, or
in-progress releases; it only smooths compatible *downstream*
controls around frozen child state. The separate UI TODO
for Noise Vmin/Vmax and range-link buttons on `main` is
not implemented by this renderer change. Real-device listening
of this specific combined child is still pending, PR #12
remains **draft/unmerged** and the all-property milestone
remains open.

### Slice 58 — manual audition acceptance for frozen Envelope/Noise compound Multi (2026-11-11)

The user completed the prescribed local **latched Test-note** procedure for the slice-58 graph and reported **PASS**. The requested fixture was:

```text
Add[
  Multiply[Oscillator, Node Envelope],
  Multiply[White Noise, Constant(0.15)]
]
  -> Multi (Spread; instructions specified 3 voices)
  -> outer Operator [Multi, Constant(0.8)]
```

The instructions were to change the **outer Constant from 0.8 to 0.1** and then switch the **outer Operator from Multiply to Add**, while leaving the compound Multi child unchanged. The actual user screenshot (after edits) shows Oscillator #1 (Sine 440 Hz), Envelope #2, Multiply #3, White Noise #4, Constant #5 (0.15), Multiply #6, Add #7, **Multi #8 Spread with 7 voices**, output Operator #9 **Add**, and outer Constant #10 **0.1**. Thus the screenshot is a seven-voice variation of the suggested three-voice fixture, and visibly captures the final configured state, not an audio waveform or intermediate state.

The user explicitly reported the instructed listening test **passed**. Record **manual acceptance for downstream Constant and Operator changes around a frozen compound Oscillator/Envelope/Noise Multi child**; the stronger seven-voice variation is meaningful. Do **not** infer that internal Envelope selection/curve editing, Noise identity or seed changes, Multi voice-count/distribution edits, release-in-progress, or all rapid-transition timing combinations were tested in this session. Automated tests separately cover exact 64-frame smoothstep, same-seed Noise, equivalent frozen Node Envelope, subsequent Note Off release clock and zero PCM allocations. The broader all-nonstructural-property milestone remains open, and PR #12 remains draft/unmerged.


## Slice 59 — frozen direct Sample Sources in compound Multi output transitions

Slice 58's unchanged compound Multi children supported Oscillator, Constant,
Operator, deterministic Noise and equivalent Node Envelope curves. A
**direct Sample Source inside the Multi child** still disabled the parent
64-output-frame audition fade, even if all the caller changed was a
downstream Constant or Operator, and the Sample's existing integrated
fractional playhead could safely continue.

**New bounded eligibility:** The frozen Multi child may additionally
contain a directly bound **Sample** Source, alongside the previously
admitted node types and **at least one Oscillator**. The Sample itself
is an audible leaf, not a newly instantiated or remapped playback
voice. The old, prepared and currently bound child graphs must retain
matching topology, output, node identity/order/input indices and all
previously required frozen node properties. For each direct Sample,
the old/new authored SourceId, FrequencyHz, Constant,
ExponentialMultiplier and multiplier input must all be identical.
The prepared Samples must have matching NewNotePolicy and pass
`CanSharePreparedOnsetTimelineWith` (immutable PCM, sample rate,
channel placement and loop geometry). Comparing names or IDs alone
is not sufficient to substitute a changed Sample asset.

**Every active Multi constituent is checked independently before
starting any parent output fade.** In addition to the prior stable
distribution, detune, ratios, mix, phase/onset, voice count,
gain-compensation, no-Note-Off and no-activated-headroom conditions,
its bound Sample must still match both the current and next prepared
child. The bound SampleSoundState and SourceVoice must be continuing,
not input-finished, expired, releasing, or dependent on unadmitted
Source-local Filter history. A single ineligible child disqualifies
this **outer** fade rather than partially admitting a subset of
constituents. This adds no Source creation, PCM allocation, sample
seek, historical replay, or extra sample-rate conversion in PCM.
The 64-frame smoothstep joins the last audible **whole-parent**
output to the naturally advancing revised parent output.

**Desired-invariant regression first:** The newly added
`FmSynthLiveSampleMultiOutputTests` uses
`Multi[Add[direct Sample Source, Sine Oscillator]]`, with a
three-voice Spread configuration and an external Constant and
Multiply/Add Operator. The test-only commit
`21ae1891ec4c` produced **four genuine first-frame
discontinuities** on
[CI #43113214702](https://github.com/logiclrd/Heresy/actions/runs/43113214702):
two gain cases, Operator mode and an interrupted fade.
The natural-Sample-EOF case and non-audition isolation case already
passed without the fix. The implementation commit
`05f1fc571f79` then passed
[CI #43113603788](https://github.com/logiclrd/Heresy/actions/runs/43113603788),
including **3,639 tests** and Linux ARM64, native-host and release
validation. Positive tests assert the complete 64-frame fade against
an independently advanced Sample+Oscillator reference, uninterrupted
EOF and the simultaneous Oscillator, interrupted-fade continuity,
ordinary non-audition semantics and zero PCM managed allocations.

**Explicitly not added:** Reassigning SourceId, updating the
Sample PCM/loop or its Source pitch controls *inside* this child
during an outer fade; Instrument or nested Synth Sources, Filter
or nested Multi nodes inside this child; already releasing or
retiring constituent histories; changed inner Envelope/Noise;
arbitrary nested migrations and performance stress. The previous
separate live-control migration mechanisms remain in force where
independently compatible. Real-device listening for this new
Sample-in-Multi downstream-edit fixture is **not yet reported**.
PR #12 stays draft/unmerged, and the all-nonstructural-properties
TODO on main stays open.


### Slice 59 — local listening acceptance (2026-11-11)

The user built the prescribed **direct looping Sample Source plus Sine Oscillator
under an Add Operator, into a Spread Multi (3 voices, around 17 cents detune,
Randomize Phase off, Gain Compensation on)**, followed by an outer
Constant and Multiply/Add Operator. With a latched Test note playing, they
changed the external Constant **0.8 → 0.1** and the external Operator
**Multiply → Add**, leaving the Sample, Oscillator and Multi settings
unchanged. They explicitly reported that the test **passed**.

This is **physical listening acceptance for the newly admitted downstream
output-control fade around a frozen, continuing Sample-backed Multi
child**. It does not establish edited/changed SourceId, rewritten PCM,
changed Source rate, changed Filter or Instrument child processing,
already-releasing Sample migration, exhaustive hardware stress or exact
numerical fade equality; those remain separate from the automated
64-frame, no-allocation and EOF assertions.


## Slice 60 — frozen single-OnePole Filter and Sample inside compound Multi output transitions

Slice 59 allowed an unchanged compound Multi child containing a direct Sample
Source and at least one Oscillator to participate in the outer 64-frame
audition-only Constant/Operator output fade. Putting an otherwise **unchanged
OnePole Filter** on that direct Sample branch still disqualified the whole
outer fade, although each Multi constituent owns a prepared continuing
Filter chain and an integrated Sample playhead.

**Bounded extension:** A Multi's frozen compound child may also contain a
Filter node whose configuration is **exactly one unmodulated OnePole
stage**, with an audio input, as well as the previously admitted
Oscillator, direct Sample Source, Constant, Operator, deterministic Noise
and equivalent Node Envelope nodes. At least one Oscillator remains
required. The old, next, and bound child graphs must have the same
compiled shape, output, node IDs/order, inputs and phase indices, with
all existing exact frozen-node conditions. The Filter must have the
same audio input, unchanged TailSeconds, same definition ID and stage
ID, unchanged **stage generation as captured at preparation**, the same
parameter descriptors and each identical effective Filter value. A
modulated OnePole, another stage family, additional stages, or a
changed Filter cutoff/tail is not eligible to masquerade as a frozen
OnePole for this *parent* output fade.

**Per-constituent acceptance:** The already-started bound child must
agree with the original frozen child, and the originally prepared
child must agree with the revised one. For every active Multi voice
the existing Filter chain must still be present, with input accepting
audio rather than drained, and no Note Off. The continuing direct
Sample still passes the slice-59 immutable PCM/loop identity, source
property and original readhead checks, and the original stable
Multi ratios, mix, onset, voice count and headroom guards remain.
The parent fade begins only if **all** constituents pass. No Filter
integrator, Sample invocation, or Multi child is rebuilt in PCM;
the previously audible complete graph output is the fade anchor,
and the original Filter histories and Source positions advance
normally throughout the 64 output frames. The Filter's frozen
parameter comparison uses indexed descriptors rather than a
captured iterator, and stage-generation metadata is snapshotted
off PCM, not rediscovered from mutable authoring objects.

**Desired-invariant regression first:** The new
`FmSynthLiveFilteredMultiOutputTests` graph is
`Multi[Add[OnePole[direct Sample], Sine(390Hz)]]`, using three
Spread voices with a downstream Constant and Multiply/Add Operator.
The test-only commit `8601687c3b33` produced **four real
first-frame discontinuity failures** in
[CI #43140007560](https://github.com/logiclrd/Heresy/actions/runs/43140007560):
two downstream gains, the Operator edit and a second edit
while the first fade was ongoing. The Sample-EOF/Filter-tail
continuity and ordinary non-audition tests passed before the
fix. That red run also saw a **separate, non-target**
`FmRealtimeMaximumFirPublicationTests` allocation assertion
(8200 vs <4096 bytes), so its five total failures must not all
be reported as intentional. The first code implementation
`18d393ff6ec1` passed
[CI #43140366054](https://github.com/logiclrd/Heresy/actions/runs/43140366054)
with **3,645 passing tests**, Linux ARM64 and release/native
validation. A subsequent allocation-avoidance preflight refinement
`26c8d1ca6e1f` also passed
[CI #43140559227](https://github.com/logiclrd/Heresy/actions/runs/43140559227).
The final code refinement `9e639348e031` captures stage
generations before the PCM boundary. Final documentation-HEAD CI
is separately required.

**Exact tests:** complete 64-output-frame smoothstep against an
independently rendered same-Sample/same-Filter three-voice reference,
consecutive gain/mode changes before a fade finishes, retained
per-constituent Filter history, integrated Sample position and
end-of-file behavior with existing Filter tails, the parallel
Oscillator continuing after the Sample, zero managed allocations
while rendering PCM, and exact immediate output behavior outside
audition.

**Not implemented by this slice:** changing the Filter cutoff,
stage, TailSeconds or modulation ports **inside** the Multi while
an outer fade is active; additional Filter families/stages,
Instrument or nested Synth Sources, nested Multi, SourceId
reassignment, released/retiring Filter tails, stale or altered
Samples, or any kind of all-properties signoff. Independently
supported Filter live retunes remain distinct, not implicit
proof that the Filter is frozen for a parent fade. No physical
listening has yet been reported for this new Filter-in-Multi
fixture. PR #12 stays draft/unmerged and the all-nonstructural-
property TODO on main remains open.


## Post-slice-60 regression fix — additive output click on live filtered Multi

**User hardware report (2026-11-12):** The user heard a slight
regression on the prescribed slice-60 latched Sample → OnePole +
Sine → Add → Multi → outer Operator fixture. A large edit to the
**outer Constant while the final Operator was Add** produced a
click; changing that Operator **between Add and Multiply** also
usually clicked. The same Constant changes with the final Operator
set to **Multiply** did not click. This is a real-device *failure
report against the previous code*, not a listening pass of the fix.

**Diagnosis:** The previously proven 64-output-frame smoothstep
ensured the first new PCM frame exactly repeated the last *audible
whole-graph output*, but then faded that held value to the new
moving graph over ~1.33 ms at 48 kHz. The exact 64-frame PCM
oracle did not establish inaudibility of a large additive DC
change. An abrupt DC offset or switching Add ↔ Multiply alters
both the gain and bias; even a smooth 1.33-ms output ramp can
produce an audible high-frequency transient. This explanation
is grounded in the DSP transition shape; additional actual
audio-device listening is still required to determine whether
it fully accounts for the user's reported sound. Source/filter
retirement or a dropped fade was not established as the cause.

**Bounded correction:** For an eligible *top-level three-node
compiled output graph*, where an unchanged stable **Multi**
and one **Constant** feed a final binary Add or Multiply,
a parameter edit affecting that final Constant/Operator now
uses a separate **512-frame (~10.67 ms at 48 kHz) smoothstep
of the operator's affine mapping** instead of the generic
64-frame hold-and-fade.

Let `s(f)` be the **current**, still-moving and fully evaluated
Multi output at frame `f`. Multiply with Constant `c`
maps to `c*s(f) + 0`; Add maps to `1*s(f) + c`.
With `w(f) = smoothstep((f-S)/512)`, the final audible output
becomes

`s(f) * ((1-w)*oldGain + w*newGain)
+ ((1-w)*oldOffset + w*newOffset)`.

This morphs the old and revised gain **and DC offset**, while
rendering Sample readheads, independent Oscillator phases,
Filter histories, Noise/Envelope clocks and Multi ratios only
once, forward, as before. It **does not freeze or replay a
single old output sample for 512 frames**, construct a second
child graph, reconfigure any child Source/Filter, or allocate
in PCM. The previous whole-tree atomic preflight, same
unchanged/frozen Multi child admission and audition isolation
still apply. A second additive edit derives its starting
gain and bias from the *currently audible moving mapping*;
a switch from an in-progress legacy Multiply-only 64-frame
output hold joins its last actual blended value with a
calibrated bias. Ordinary playback/export has no morph.
Changing away from Add/Multiply falls back to the prior
generic behavior. **Multiply-only Constant edits retain the
original 64-frame output fade**; other graph shapes do too.

**Red-first evidence:** `FmSynthLiveAdditiveMultiTransitionTests`
was committed independently as `d86cfeb6` and
[CI #43173794531](https://github.com/logiclrd/Heresy/actions/runs/43173794531)
failed **four expected numerical transition cases**:
large additive offset, two directions of Add/Multiply
switching, and a second additive edit before the first
completed. The normal-playback negative case passed.
This catches the old 64-frame behavior without creating
a test that endorses the audible click.

**Green implementation:** `392b3c9d` updates the renderer
and strengthens the existing Multi output regression groups
(slices 56–60), retaining their independent 64-frame Multiply
assertions and replacing the old Add-mode held-sample oracle
with the full 512-frame moving-signal, gain/offset oracle.
[CI #43174461292](https://github.com/logiclrd/Heresy/actions/runs/43174461292)
passed **3,650 tests** (Render 1,102, Playback 355,
UI 1,149, other 910/105/29), Linux ARM64, native/Windows
checks and release-bundle validation. New tests also assert
zero managed PCM allocations, overlapping edit continuity
and unchanged ordinary playback.

**Boundaries:** This is not a blanket 512-frame smoothing
of every Constant or every FM control port. It applies only
to the eligible final `Multi + Constant → Add/Multiply`
audition output shape. Complex parent Operators, other
control-node paths and unadmitted Multi child migrations
keep their existing contracts. It does not prevent clipping
if authored Add output exceeds the output device's range,
remove the intentional DC offset introduced by Add, or
certify hardware inaudibility. The user's local physical
listening **retest is pending**. PR #12 remains draft/unmerged,
and the all-nonstructural-property TODO remains open.


## Slice 60 follow-up: second failed listening retest; 100 ms moving output transition (12 November 2026)

**Audible status: NOT YET ACCEPTED.** The user reloaded the same slice-60 test file and again heard repeatable pops switching final Add/Multiply and smaller but detectable pops changing the Multiply Constant. The earlier 512-frame (~10.7 ms at 48 kHz) moving Add transition may have reduced the sound but demonstrably did not eliminate it. Multiply-only edits still used a 64-frame (~1.3 ms) hold of the last output sample before fading into the advancing waveform.

**Red-first:** Commit [5d00f50e](https://github.com/logiclrd/Heresy/commit/5d00f50ecf628372f62bc407bd68445fe3adb686) introduced six new 48/96-kHz invariants requiring a 100 ms moving gain-and-bias transition: a large Add Constant step, both mode directions, and Multiply-only Constant changes. [CI #43322859134](https://github.com/logiclrd/Heresy/actions/runs/43322859134) failed precisely these six new cases; the other suites passed.

**Implemented candidate:** [650ba5e](https://github.com/logiclrd/Heresy/commit/650ba5e607db3f7da8c83029bf2451836f3330a2) applies a sample-rate-scaled 100 ms moving affine gain/DC-bias ramp to **all** changes of the eligible final Multi + Constant → Add/Multiply audition shape, including pure Multiply. At 48 kHz this is 4800 frames; at 96 kHz, 9600 frames. The original Multi waveform keeps advancing with its Sample readheads, Filter histories, oscillator phases and envelope/noise clocks. Interrupted edits inherit the previously audible **moving gain/offset map**, not a frozen old sample; the existing whole-tree admission guards and ordinary-playback/export isolation remain. Existing Multi output tests now assert the longer oracle, interrupted transitions, Note Off, tail/EOF retention and no per-PCM managed allocations. [CI #43323623298](https://github.com/logiclrd/Heresy/actions/runs/43323623298) passed **all 3,656 tests**, plus Linux ARM64, release, Windows and native checks.

**Remaining:** Despite automated green, the new 100 ms candidate has **not** passed hardware listening. The original user's failure remains tracked separately from CI. A large additive DC bias may also drive the authored output above full scale, creating clipping that no transition smoother can fix without changing the intended sound. Include an additional low-level/low-DC repeat test to distinguish clipping from transition faults if audible pops persist. Keep PR #12 draft/unmerged and the all-nonstructural-properties TODO open.
