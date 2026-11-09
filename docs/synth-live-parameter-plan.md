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
