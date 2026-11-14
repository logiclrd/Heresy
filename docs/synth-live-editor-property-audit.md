# Synth editor property-to-audio audit

Status: **in progress**, PR #12 (draft). This is a source-level audit of
`FmSynthEditorControl`, `FmSynthDocumentEditor`,
`SongPlaybackTransport` and `FmSynthSound`, not a claim that the complete
all-properties live-edit feature has passed UI, device and PCM acceptance.
See `docs/synth-live-parameter-plan.md` for design and the chronological
implementation slices.

## Actual editor/publication path

- The inspector dispatches edits through `UpdateParameter` /
  `FmSynthDocumentEditor.UpdateNode`; semantic mutations increment
  `SongDocument.AudioRevision`. Visual canvas positioning and routing
  hints do not alter the audio revision.
- `FmSynthEditorControl._changed` invokes its optional live-publication
  callback only when `AudioRevision` advances. The transport prepares a
  frozen replacement, publishes it in order, and attempts whole-voice
  `FmSynthSound.TryPrepareRunningState` /
  `CommitPreparedRunningState`. A declined migration retains the old
  voice generation while future notes use the newer definition.
- A successful UI commit does **not** by itself prove a held voice
  adopted the edit. The published graph, voice history and any prepared
  filter/source/constituent resources must pass separate preflight.
- `reserveMultiHeadroom` / `enableLiveMultiExpansion` identifies
  the live audition mode. Ordinary playback/export retains its prior
  exact renderer semantics and uses no audition-only smoothing.

## Property surfaces and current audit disposition

| Real inspector property | Code path / current live behavior | Remaining acceptance |
| --- | --- | --- |
| Constant: Value | Audible Constant edits use a bounded 64-frame final-output fade through stateless Oscillator/Envelope/Noise graphs, compatible direct Sample and Filter states (slices 51–55), and now **unchanged stable Oscillator Multi constituents** (slice 56), preserving each child's phase. Pitch/control-only Constants remain immediate | Broader Multi-contained Source/Filter, already-releasing histories, SourceId replacement and complete UI acceptance remain open |
| Oscillator: Waveform, Curvature, Vmin, Vmax | Recompiled waveform/curvature/range with retained phase. Live auditions fade a directly audible Oscillator shape in **stateless Oscillator/Constant/Operator graphs** for 64 frames from the last audible whole-graph value; repeated edits join the blended output. Purely pitch-modulating controls do not require output fades | Still open: Filter/Source/Multi graphs, full modulation/property matrix and exhaustive physical listening |
| Oscillator: Frequency Hz, Constant, Exponential multiplier | Existing phase integrator continues at newly authored instantaneous rate; frequency-only edits are not subjected to the shape fade | UI/PCM matrix and high-rate modulation acceptance |
| Oscillator: Multiplier input | Graph wiring change | Structural-generation rule applies |
| Source: SourceId (Sample / Instrument / Synth) | Inspector permits reassignment. Renderer can migrate compatible **nested Synth** state. A new narrow identity-only case preserves a held **direct Sample** invocation when a changed SourceId resolves to the **exact same SampleSound instance** already bound to that voice, without changing its PCM/playhead; arbitrary Sample/Instrument replacements still retain the old held generation while new notes use the selection | **Open property gap**: genuinely different Sample/Instrument assets need worker-prepared new invocation, new-asset playhead policy, click-safe bounded transition and Note Off/EOF/release handling. Identity-only alias does not close this row |
| Source: Frequency Hz, Constant, Exponential multiplier | Preserves original child playback position/frequency basis while applying forward rate/mode changes | Verify real inspector with Sample, Instrument and Synth sources across prior pitch automation |
| Source: Multiplier input | Graph wiring change | Structural-generation rule applies |
| Envelope: selected Envelope object; shared curve edit | Graph Envelope nodes remain stateless. **Slice 52:** audible changed Node Envelope curves/references in held, stateless Oscillator/Constant/Operator/Envelope graphs use a 64-frame final-output fade without phase rebase; equivalent frozen wrappers do not fade and pitch-control-only Envelopes remain immediate. Changes during an active release are conservatively rejected. **Slices 46/49:** bounded direct/multi-Source Instrument Sample Volume adoption remains separate | Open: scripted graph Envelopes, release-in-progress migration, Source/Filter/Multi graphs, other Instrument envelope targets, rapid edits and full device matrix |
| Filter: shared definition, per-use stage overrides, Tail Seconds | Existing coefficient/FIR and stage-identity preflights adopt compatible prepared parameter changes and tail updates; graph rewiring and unsupported topology remain conservative. **Slice 55** lets an *independent audible Constant/Operator change* crossfade at the output through an unchanged, still-active compatible Filter chain without resetting its DSP history | Per-stage parameter change combinations, released-note tails, multi-filter and nested Instrument/Multi acceptance and physical click/throughput checks remain open |
| Filter: audio and modulation inputs | Graph wiring change | Structural-generation rule applies |
| Multi: Distribution, Detune, Voice Count, Harmonic/Chord Components, Randomize Phase, Gain Compensation, Cycle Length, presets | Existing prepared ratio/onset/voice growth and state migrations are separate. **Slices 56–58** now smooth downstream audible Constant/Operator edits around a stable Multi whose child is an unchanged graph of Oscillators, Constants, Operators, deterministic Noise and **equivalent frozen Node Envelopes**, preserving every child phase/seed/clock. The user's earlier slice-56 live listening explored the three distributions and other Multi properties without unwanted clicking | Still open: edits *inside* such compound children during the parent fade, child Source/Filter/nested Multi, already-releasing state, headroom interactions and full UI-to-PCM matrix |
| Multi: input selection | Graph wiring change | Structural-generation rule applies |
| Operator: operation (Add / Multiply / Minimum / Maximum) | Compatible mode edits with unchanged wiring use the same audible 64-frame output fade through stateless controls, continuing Sample/Filter and **unchanged Oscillator-only Multi** (slice 56), preserving constituent phases. Pure frequency or Filter-control routes do not require that fade | Unstable Multi onset/voice growth, multi-layer Source/Filter nesting and UI-to-PCM matrix remain open |
| Operator: input node IDs | Graph wiring change | Structural-generation rule applies |
| Noise | Counter-based sample generation needs no evolving RNG state. **Slice 54** admits Noise alongside compatible direct Sample/Envelope/Constant/Operator audible paths for 64-frame gain/mix transitions, preserving seed/node/frame identity | Mixed-state Filter/Multi graphs, fast edits and UI/device acceptance remain open |
| Graph: add/remove/output/wiring | Structural; existing voices may retain old generation, new notes see latest | Structural and overlapping generations remain separate tests |
| Canvas layout/routing | Document-only; must not trigger audio publication | No-op UI notifications |

These are **eligibility/code-path findings**. Rows without dedicated
end-to-end test evidence are not marked complete merely because the
renderer has a compatible branch.

## Concrete current slice: bounded direct oscillator-shape transition

The first prioritized audible gap is a discontinuity when an already
sounding single-output oscillator changes Waveform, Curvature, Vmin or
Vmax. Previously the first sample after publication immediately used
the new transfer shape at the retained phase. The regression was
**red before the implementation**: each of Sine->Square, changed
Curvature and changed output range produced the unsmoothed jump.

When both old and new compiled voices are audition-enabled and have
the single-output oscillator shape, the existing `FmSynthState` captures
its **last audible value** and the current output-frame index at the
atomic publication boundary. The new oscillator still advances its
original phase each frame; its new waveform is blended from that
captured audible sample with a fixed 64-frame smoothstep:

`output(n) = oldAudible * (1 - w(n)) + newWaveform(n) * w(n)`,
where `w(n) = t*t*(3-2*t)` and `t = clamp(n/64, 0, 1)`.

The first new PCM frame is continuous with the preceding sample; the
new transfer curve is fully active at frame +64. Another shape edit
during the transition starts from the **currently blended** PCM
sample, not an obsolete waveform. Frequency-only edits remain on the
original phase/rate policy without a gratuitous fade. This is a
deliberately bounded, audition-only behaviour: normal rendering,
offline export and optimized/generic renderer parity do not change.
The fade neither reconstructs past waveform history nor allocates a
second voice/PCM buffer; all audio callback work is scalar.

Regression coverage includes the three inspector shape families,
multiple rapid shape changes, original oscillator phase after fade,
nonshape FrequencyHz edit, zero managed PCM allocations and a
Headless Avalonia waveform selector test proving document revision,
publication callback and no-op selection behaviour. **The general
graph remains a separate smoothing problem**; do not claim that
every oscillator shape edit is click-free yet.

## Findings to prioritize next

1. **SourceId reassignment for held Samples/Instruments** is an
   explicitly non-wire inspector property that currently falls back
   to the earlier generation rather than producing the selected new
   source. This requires off-PCM preparation and a defined transition,
   not a retroactive attempt to derive an arbitrary Sample's phase.
2. **Shared dependency invalidation through Instruments — transport
   routing implemented (slice 44):** `SongPlaybackTransport.GetDependentSynths`
   now follows the declared tone Source, Volume/Pitch/Panning/Filter
   Envelope and tone Filter references of both data and script
   Instruments, including nested Instruments and Synth parents.
   Child-before-parent Synth publication is enforced even when a
   parent has another direct route to the changed object.
   **UI-to-transport path checked (slice 45):** real Node Envelope,
   Script Envelope, and Filter editor Apply operations emit the shared
   workspace event, which MainWindow routes into an active
   SongPlaybackTransport. Their Instrument-dependent Synths and
   Synth parents are published once per audio revision, without
   reopening the live output. Invalid/no-op edits do not publish.
   **Held DSP now partly proven:** a selected direct Instrument→Sample
   node Volume curve can change during a held note (slice 46, physically
   heard); slice 49 extends that bounded proof to multiple eligible Source
   nodes under Operators, using whole-graph Volume preflight. Instrument
   tone Filters, non-Volume envelopes, compound Multi children and other
   selected tone Source families still require separate migration policies.
   Transport publication alone does not prove those other cases.
3. **Filter and Multi property matrix:** keep distinct tests for
   feasible in-place parameter changes, worker-prepared shape
   changes, and intentionally unadoptable structural cases.
4. **Audible acceptance:** profile sustained CPU/underrun behaviour,
   listen for clicks on physical output, and verify overlapping
   latched note generations and edits during release.

Do not mark the existing all-properties TODO complete until every
nonstructural inspector property has an explicit tested live behaviour.


## Slice 44 — live dependency publication through Instrument tones

The transport's frozen-publication dependency walk previously visited
Synth Source, Envelope and Filter node references plus nested Synth
parents, but stopped at Instrument objects. This dropped edits to a
shared Envelope or Filter referenced only in an Instrument's tone
specification, even when the active preview was an acyclic
`Synth -> Instrument -> Sample`. It also missed a changed child Synth
nested inside an Instrument.

The new control-thread graph walk treats both
`InstrumentDefinition` and `ScriptInstrumentDefinition` as
**intermediate** dependents. It traverses all declared tone
specifications (including currently unselected tones), checking
`SourceId`, all four Envelope IDs and the tone's configured Filter
definition ID. It follows nested Instrument and Synth Source
references transitively. Instruments themselves are **not** published
as Synth definitions; only reachable Synths are enqueued, one per
audio revision, without restarting the active audio session.
The publishing order visits affected child Synth dependencies before
their parents even if a parent also directly references the shared
object. Unrelated Synths and the frozen base session remain unchanged.

Regression tests cover data/script Instrument variants across each
of the four Envelope slots and the tone Filter (ten combinations),
nested Instrument chains, shared objects also referenced directly
by a separate Synth, repeat notifications at the same revision,
a changed nested Synth Source, no-session notification, and a
two-path parent/child ordering edge.

This is **publication routing**, not a relaxation of the
`FmSynthSound` state-adoption proof. A newly prepared Synth definition
may be used for future notes while an already sounding Instrument
Sample with genuinely changed Envelope/Filter state remains on its
old invocation until safe state migration is separately supported.
The all-properties TODO, physical listening and shared-object
UI-to-PCM coverage remain open.


## Slice 45 — actual shared-editor-to-live-transport integration

The earlier document and transport layers already contained the
correct wiring: `EnvelopeDocumentEditor.UpdateNodeEnvelope`,
`UpdateScriptEnvelope` and `FilterDocumentEditor.ApplyStage` emit
`DocumentWorkspace.SharedAudioObjectCommitted` **after** successful
audio-affecting authoring commits. The standalone and embedded
editor controls invoke those helpers. `MainWindow` is subscribed to
the workspace's event and sends the exact committed object ID
through `ILiveSynthEditTransport.PublishSharedObjectEditAsync`.
The existing `SongPlaybackTransport` prepares affected Synths
off-PCM and publishes them in Source-dependency order.

The missing part was an integration regression exercising that chain
from the *real* editor controls through the *real* transport while a
Synth audition was active. `SharedAudioEditLivePublicationTests` now
constructs a headless `MainWindow` with a real
`SongPlaybackTransport`, a detached-preparation recording factory
and a deterministic live output backend (no hardware). It starts a
Synth audition where the audible definition is reached through
an Instrument tone with a shared Volume Envelope and Filter, and
where a parent Synth also references that Instrument-backed Synth.

The node Envelope and shared Filter editors commit changes through
their actual draft/Apply methods. The test requires the prepared
child and parent Synth revisions, in that order; exactly one
publication per Synth per audio revision; updated frozen revision
numbers; no unrelated Synth publication; and **no second audio
session**. Invalid Envelope drafts, unchanged node reapplication,
unchanged Filter reapplication and duplicate notifications remain
no-ops. A separate test exercises the Script Envelope editor's
independent expression-Apply path and verifies a rejected invalid
script does not change the revision or publish audio.

**Boundary:** these tests use a recording audio output, not a physical
sound device, and prove correct editor/transport publication rather
than audible migration of a running Instrument's *changed*
Envelope or Filter DSP state. The existing conservative
`FmSynthSound` adoption preflight still decides which held notes
may change and which retain the original frozen generation. That
migration acceptance, the remaining all-properties audit and
physical listening/underrun tests remain **open**. PR #12 stays
draft and unmerged.


## Slice 46 — bounded held Instrument node Volume adoption

Slice 45's real editor integration proved publication, but the held
Sample continued using the Envelope curve captured in its flattened
`SoundInvocation`. Slice 46 adds the first **actual held-voice DSP
migration**, without reopening the session or retiming the Sample.

For a live-audition Synth with exactly one directly outputting
Instrument Source, preparation now freezes possible **direct terminal
Sample + node Volume curve** candidates ahead of PCM. At publication,
the already-selected data-driven tone slot, same terminal immutable
Sample PCM/timeline, selected tone identity, release policy and absence
of other tone Filter/Pitch/Panning/Filter-envelope state provide an
allocation-free compatibility check. Ineligible Samples, scripted
Instrument mappings, complex graphs and already-retired voices keep
their old captured Volume. The original Sample SoundState and
integrated fractional playhead are never replaced.

A permitted change replaces only the active immutable Volume curve,
then evaluates the old and new gains at the original continuing
envelope age and Note Off origin. These gains crossfade over 64 frames
with cubic smoothstep. Interruption uses the last blended gain instead
of retaining multiple outgoing curves; outgoing release is bounded by
the fade, so editing to a shorter release cannot retroactively erase
the old contribution. Natural Sample EOF continues to bound the sound.

The first regression **failed red** on the old code, then passed with
this change; it checks actual held PCM on a varying Sample waveform,
the original Sample's uninterrupted position, changed sustain level,
zero managed render allocations, ten-frame release and a fresh note
on the new curve. Guard tests cover incompatible changed PCM and
non-audition frozen rendering, and overlapping 10/20-frame releases
with Note Off during the fade. The new behavior is deliberately
**narrower than all Envelope inspector properties**; true
editor→transport→held-PCM integration and general tone Filter/envelope
migrations remain outstanding. PR #12 and the all-properties TODO stay
open.


## Slice 47 accessibility and release distinctions

The Synth Source inspector exposes referenced Instrument Node Volume envelopes
in a modeless shared editor. Editing these shared objects uses the existing
audio-revision publication callback without unmounting the Synth preview.
Latch mode allows the preview note to survive focus changes; Hold mode does
not. This makes the limited single Source / Instrument / direct Sample Volume
audition manually accessible but does not extend the renderer's live migration
to compound graphs or Instrument tones backed by another Synth.

Separately, a Pattern Note Off for an Instrument tone selecting a Synth
can now preserve the Synth's excitation until the Instrument Volume envelope
finishes releasing, rather than cutting it after a one-frame Synth note-off.
That fixes release lifetime, **not** held-note live parameter adoption.

## Slice 48 — recursive Note Off semantics (separate from live edit eligibility)

Definite Note Off at the root releases any selected recursive sound subtree
with an active sustain loop, irrespective of which envelope target owns it;
otherwise it cuts. A releasing ancestor sends Indefinite Off to its children;
non-sustaining children then continue their natural sound rather than being
cut by that forwarded request. All participating release lifecycles and
audible Filter tails matter; permanent-zero *final* Volume may prove earlier
silence only with appropriate downstream/tail proof. See
`docs/note-off-lifecycle.md` for the normative table and the scripted
release-duration compatibility exception. The generalized lifecycle was automatically tested in slice 48; a later
real-device listening matrix also verified the core release semantics (see
[Note Off lifecycle](note-off-lifecycle.md)). Internal retirement and
repeated-Off-origin specifics remain automated-only or unverified as stated
there. These lifecycle checks are distinct from adopting edited held
Instrument envelope curves.

## Slice 49 — multi-Source shared Volume held-state migration

An audition-enabled Synth whose compiled graph includes multiple
Instrument-backed Source nodes under Operators now prepares *each* eligible
direct selected Sample + node Volume curve on the frozen preparation thread.
At an audio publication boundary, the whole set of participating active
Instrument node Volume Sources is preflighted before any curve migration.
All must pass selected tone, immutable Sample PCM/timeline, matching note
policy, no unsupported tone Filter or other envelope state, and continuous
already-playing Sample proof; a single ineligible Source prevents *all*
Volume changes without preventing independently compatible scalar adoption.
Eligible curves keep each source's real fractional Sample position and
original note/release envelope clock, with per-Source 64-frame gain fading.
The transition does not allocate or resolve assets in PCM. Arrangement and
offline playback remain unchanged.

The two-Source Add-Operator regression was red before implementation
([CI #43011714291](https://github.com/logiclrd/Heresy/actions/runs/43011714291)).
The new test also asserts zero PCM allocations, original Note Off release,
and exact oracle gain. A second test checks atomic rejection when one
selected Sample's immutable PCM changed and simultaneously proves an
independent Constant edit still adopts. Existing Multi-onset tests caught
an unsafe broadened child adoption; they were not weakened. The final
bounded implementation leaves compound **Multi-generated child graphs**
on their previous conservative policy pending a coordinated onset/Volume
migration. All regression suites and Linux ARM64 validation passed in
[CI #43012576902](https://github.com/logiclrd/Heresy/actions/runs/43012576902).

The user's **single direct Source** shared-Volume listening acceptance is
recorded in the development plan. The two-Source Add-Operator route has since received **physical listening acceptance**
(see below); more complex compound graphs, Note Off release listening and the
all-properties Synth live-edit milestone remain open. PR #12 stays draft/unmerged.

### Slice 49 manual listening acceptance — distinct Source pitches

The user verified on a real audio device a Synth with **two Instrument
Source nodes connected through an Operator**. Both reference the same
Instrument, whose Tone Definitions are identical across the note range
and share one Volume Envelope. The second Source's base frequency was
set **25% above** the first (ratio 5:4), so both versions of the same
short music-loop Sample were audibly distinct. With the note latched,
opening the shared Volume editor from **either Source** and changing its
curve caused the ongoing audio from **both** Sources to respond
immediately, without interruption. This confirms the visible UI path
and an audible in-place shared-Volume change in a compound graph;
it is not merely a transport publication or a new-note effect.

The report does not certify unrelated controls, nested/compound Multi
children, exact fade math, repeated rapid edits, performance under load
or *all* generalized Definite/Indefinite Note Off edge cases. The later
slice-48 core lifecycle listening pass is recorded in
[note-off-lifecycle.md](note-off-lifecycle.md); internal permanent-zero
retirement and repeated-Off-origin listening remain separate.

## Slice 50 — compound stateless Oscillator output transitions

The direct-Oscillator shape fade (slice 43) did not cover an Oscillator
feeding an Add/Multiply Operator. A live **Waveform, Curvature, Vmin or
Vmax** edit was previously permitted structurally but produced a
discontinuous first frame at the final output. A new red-before-fix
regression observed the immediate jump through **Add and Multiply**
([CI #43033101533](https://github.com/logiclrd/Heresy/actions/runs/43033101533)).

A compiled graph consisting only of reachable **Oscillator, Constant and
Operator** nodes is now eligible for a single 64-frame smoothstep on the
**final audible graph output**, using the actual previously audible
sample as its anchor. The raw per-node `Values` and independent
oscillator phase integrators remain untouched. The final output's
`AudioValues` records the already-blended value so rapid revisions
start continuously. A frequency-only change does not start a shape
fade. There are no PCM allocations, second playback voices or state
reconstructions. Normal playback/export retains the unfaded values.

The test matrix covers Square, Curvature and output-range changes,
Add/Multiply graphs, fade interruption, post-fade uninterrupted phase,
frequency retuning and non-audition isolation. **Sound Source, Filter,
Multi and compound Multi-generated graphs remain excluded** until
their distinct history/onset preflights are coordinated. The user subsequently confirmed that editing a running **Test** note
with two Oscillators under an Operator behaved as expected on their local
audio device. That is **manual acceptance of the supported slice-50
compound shape route**, not of the excluded stateful graph classes.
The slice does not settle the broader all-properties task.

## Slice 51 — stateless Constant and Operator live output changes

Changing a Constant's audible amplitude contribution or changing an
Operator's **Operation** is nonstructural, but immediate adoption could
produce a sharp discontinuity in an already playing Synth. Slice 51
reuses slice 50's 64-frame smoothstep from the last **actual audible**
output, without replacing Oscillator phase state or allocating in PCM.

The prepared graph is eligible only when its reachable nodes are
**Oscillators, Constants and Operators**, with no Sound Source,
Filter or Multi. At compile time, an **audible path bitmap** is
computed by tracing Operator audio inputs backward from the final
output, **stopping at Oscillator outputs**. This distinguishes an
audible Constant or Operator edit from a change made only to an
Oscillator's frequency multiplier. The latter is a *control*
change and must update forward pitch integration **without**
introducing a hold-and-fade on the audible output. An Oscillator
shape change similarly fades only when that Oscillator contributes
directly to the stateless audible output.

A single Constant output is included. The implementation retains
the direct Oscillator shape case within a simple Multi child but
does not broaden transitions into compound Multi-generated, Filter
or Source graphs. Ordinary playback/export retains the exact
existing, unfaded semantics. Repeated output-affecting edits during
an active fade start from the current blended output.

The new test group in
`FmSynthCompoundShapeLiveTests` was **red before implementation**:
[CI #43038824435](https://github.com/logiclrd/Heresy/actions/runs/43038824435)
reported four actual output discontinuities (gain, one-Constant
output, Add→Multiply, a second edit while fading). Positive
acceptance asserts full 64-frame oracle, preserved independent
Oscillator phase and no PCM allocations. Negative cases establish
no artificial fade on a Constant used solely for oscillator rate
and no fade in non-audition playback. Final CI and physical
device listening remain separate qualifications, and the broader
all-nonstructural-property TODO is not complete.


### Slice 51 physical listening and graph-generation boundary

The user reported **passing local Test-note listening** with the suggested
Synth containing an Oscillator and Constant joined through Multiply, with
realtime changes to the audible Constant and Operator mode. This is
manual acceptance of the *supported stateless audio-path* behaviour,
distinct from the numerical fade regressions; other graph classes and
stress testing remain outside its scope.

A separate inadvertent structural check began with a playing
`Multiply[Osc, Osc]` Synth and rewired the Operator to
`Multiply[Osc, Osc, Constant]` while the note continued. The change
had **no audible effect** on the existing voice, consistent with the
specified frozen-generation policy after a wiring/topology edit. The
user subsequently clarified that **new-note adoption was also
tested**. A later screenshot using the built-in output scope shows
a **three-input Maximum Operator**, combining a curved 440 Hz
Sawtooth Oscillator, a curved 560 Hz Sine Oscillator and a Constant
at 0.2. The user observed the rendered waveform's dominant
contribution shift between inputs as their values crossed.
This supports the complementary *new-note uses the revised graph*
acceptance, alongside the already-established *old note remains on
the frozen generation* acceptance. The screenshot's **Maximum**
configuration is a distinct subsequent variant from the original
**Multiply** rewire; these are not represented as an identical
graph or an exact numerical PCM oracle.

## Slice 52 — held audible graph Node Envelope edits

Graph `FmEnvelopeNode` curves are sample-addressable and do not
have local DSP history. An edit to the curve of an existing shared
Node Envelope or a change to the node's selected Envelope object
was already allowed under identical graph wiring. If that Envelope
supplied an **audible amplitude factor** (for example,
`Oscillator × Envelope`), the first revised sample could jump
directly to the new curve's value.

Slice 52 extends the existing audition-only final-output
**64-frame cubic smoothstep** to **changed frozen
`NodeEnvelopeCurve`** values in a stateless graph of Oscillators,
Constants, Operators and Envelope nodes. The graph's Operator
audio-input path identifies the actually audible Envelopes.
An Envelope connected *only* to an Oscillator frequency multiplier
stays a pitch control and does not initiate output smoothing.
Immutable Node curves with the same captured shape are compared
by `HasSameFrozenShapeAs`, avoiding a needless fade when the
frozen-song rebuild produces a distinct equivalent wrapper.

**Lifecycle boundary:** If Note Off has already occurred, a
changed directly audible graph Envelope in this eligible shape
is **declined by whole-voice preflight**, retaining the original
released voice and its deadline. This avoids substituting a new
Envelope whose release endpoint might erase an outgoing tail.
A held note is eligible to adopt the edit, and subsequently
releases using the revised curve at its original note age.
Detailed migration of an in-progress release remains outside
this bounded slice.

Both shared-object curve changes and selected-Envelope-ID changes
are covered. Changes to scripted graph Envelopes are deliberately
not admitted into this **Node Envelope smooth-transition**
guarantee, though their existing stateless evaluation semantics
are otherwise retained. No Source, Filter, Multi or compound
Multi-generated graph is newly admitted; normal playback/export
does not acquire audition-only fading.

The red-first
`FmSynthLiveGraphEnvelopeTests` run
[CI #43043724316](https://github.com/logiclrd/Heresy/actions/runs/43043724316)
had exactly three new failures: same-ID audible edit,
changed Envelope object ID, and changed curve after Note Off.
Negative and positive regressions establish exact 64-frame output,
retained phase, frozen-shape equality, pitch-only control
continuity, no PCM managed allocations, non-audition isolation,
and later release timing. An existing Playback integration
regression was updated to assert the new 64-frame curve-reference
join rather than immediate discontinuous replacement. Physical
listening and full all-property acceptance remain separate.

## Slice 53 — audible controls with retained direct Sample Source state

Slice 53 extends the **same 64-frame audition output transition**
used by stateless Oscillator/Constant/Operator/Node Envelope graphs
to one additional, bounded stateful context: those graphs may
contain directly **Sample-backed Source nodes as audio leaves**.
Changing a Constant value (audible gain) or an Operator operation
no longer has to jump from the last audible Sample output to a
different level on the very next PCM frame. The existing Sample's
playhead, pitch integration, natural EOF and Note Off remain on the
original `SourceVoice` and its `SampleSoundState`; no second
Sample instance or state is constructed on the realtime thread.

The admission check is **not** broad Source migration. For
this additional fade each bound Source must be a direct Sample,
retain the same SourceId and compatible frozen Sample PCM
identity, and not already be finished or releasing. The
`CanSharePreparedOnsetTimelineWith` invariant prevents a changed
PCM asset/loop layout from slipping into a superficial output-only
fade. Instrument/nested Synth Sources, Filter and Multi DSP graphs
are still excluded; arbitrary SourceId replacement remains an
explicitly **open** nonstructural-property gap. The audible-value
path continues to stop at Oscillator/Source leaves, preserving
the separate forward rate-modulation semantics.

New `FmSynthLiveSampleOutputTests` check audible Constant edits
under Multiply/Add, an Operator mode change, mid-fade interruption,
uninterrupted original Sample-frame trajectory, exact output
crossfade frames, natural EOF, zero PCM allocations, and
non-audition isolation. The first test-only CI attempt was
**inconclusive**, not a successful red test, because a missing
`Heresy.Render.Playback` namespace prevented test compilation.
The import was fixed and full CI is used for qualification.
Device listening for slice 53 is still pending; the complete
all-property feature remains unfinished.

### Slice 52 manual acceptance received (2026-11-11)

The user subsequently tested the prescribed held **Test** note with
an audible graph Node Envelope multiplied into an Oscillator and
**switched between different Envelope objects while the note played**.
The reported behaviour passed. This is physical acceptance of the
selected-Node-Envelope live-switch route. Editing shared Envelope
points in place, active-release rejection, and the other edge cases
remain covered by their separate automated evidence unless explicitly
reported as physically tested.

## Slice 54 — Sample/Envelope interaction and counter-based Noise

The user physically confirmed the slice-53 output gain change
in a Synth with a looping **Sample Source**, a graph Envelope
wired into the Source's frequency multiplier, and an audible
Constant multiplied into the output. This is manual acceptance
of the held Sample+Constant gain edit in a mixed control/audio
graph—not of a full Source reassignment or every release/EOF case.

Adding `FmNoiseNode` to a graph with otherwise compatible direct
Sample, Oscillator, Constant, Operator and Node Envelope leaves
used to disqualify audition output smoothing, despite the Noise
implementation being a deterministic counter-based sample function
of the voice seed, node identity and absolute frame. It owns no
per-voice evolving RNG state. Slice 54 therefore allows Noise
nodes in the existing bounded audible output-fade classification
without creating or migrating Noise state. The direct-Sample
frozen-PCM, active playhead and release guards are unchanged;
pitch-only Envelope modifications still advance source position
without a pointless final-output fade.

The red-first four-case regression is
`FmSynthLiveSampleEnvelopeNoiseTests`.
[CI #43051634792](https://github.com/logiclrd/Heresy/actions/runs/43051634792)
reported exactly one intentional first-frame discontinuity:
a changed audible Constant in
`Add[Noise, Multiply[Sample, Constant]]`. The other checks
(already-supported audible Envelope gain edit around a Sample,
Envelope used solely for Sample pitch control, and non-audition
rendering) passed before the fix. Full 64-frame output and
zero-managed-allocation invariants are retained. Filter,
Instrument, Multi and SourceId replacement are still outside
the bounded route; this does not complete the all-property TODO.

### Slice 54 — manual combined-graph property acceptance

The user audited an actual running Synth Test note with two summed
branches: a looping **Sample Source** whose frequency multiplier is
driven by an Envelope (Sample×Constant under one Operator), and
**White Noise×Constant** under another Operator. The user reported
all edits working across selected Envelope definition (#1),
Sample frequency and Exponential Multiplier (#2), Sample gain Constant
(#3), Sample branch Operator mode (#4), final Add Operator mode (#6),
Noise gain Constant (#7), and Noise branch Operator mode (#8).

This is broader real-device UI-to-audio acceptance for a *single
mixed graph* than a standalone gain-only fixture. It does not certify
new Noise Vmin/Vmax properties (those are a separate TODO on `main`),
Source replacement, stateful Filter/Multi DSP, released-note editing,
or every timing and graph shape combination.

## Slice 55 — continuing Filter state under audible output edits

Previously the final-output 64-frame fade excluded a graph containing
a Filter even when the Filter itself remained identical and its
live DSP history could continue unchanged. Slice 55 admits
`FmFilterNode` with a configured, continuing audio input to that
bounded audition route. The compiled audible-path traversal follows
the Filter **audio** input (not its numeric parameter-control
ports), and the existing full-voice Filter-stage compatibility
preflight retains its complete history and prepared live controls.

The additional output-fade guard requires an active Filter chain
with input not yet finished and **no Note Off already in progress**;
it excludes a draining Filter tail or unprepared/absent chain.
It does not change the existing Filter parameter/FIR retune
protocol or authorize structural stage replacement. Direct Sample
leaves retain the same frozen PCM and original playhead, natural
EOF and authored Filter tail. All checks and the output fade
remain realtime-safe, with no new PCM allocations.

`FmSynthLiveFilterOutputTests` was red before implementation:
[CI #43063347321](https://github.com/logiclrd/Heresy/actions/runs/43063347321)
failed three first-frame discontinuity assertions: Filtered
Oscillator gain, Filtered Oscillator downstream Operator-mode edit,
and Filtered finite Sample gain. The existing no-audition
negative test passed. The positive regression expects the
same 64-frame smoothstep from the *last audible filtered output*
to the continuing new graph, unchanged Filter/Source history,
unchanged sample/Filter end, and zero managed PCM allocations.
The bounded Filter listening case remains to be verified locally.

### Slice 55 — device acceptance of downstream edits through a Filter

The user confirmed that the prescribed **One-Pole Filter inserted between the Sample Source and the Multiply Operator** did not prevent a latched Test note from responding correctly to changing the downstream Constant or changing the Operator mode (Multiply/Add). The expected audible behaviour passed. This is a **manual signoff of the slice-55 bounded continuing-Filter route**, not of arbitrary Filter coefficient changes, an already-draining tail, or mixed Multi/Filter scenarios.

## Slice 56 — stable Multi as a continuing audible leaf

The previously established audition output crossfade applied to
compatible stateless/Noise/Envelope, direct Sample and active Filter
graphs, but excluded Multi even if all constituent voices were
unchanged and only an external gain or mixing Operator changed.

Slice 56 treats an **unchanged Multi of a single direct Oscillator
input** as a continuing audible leaf in the parent's routing. The
allowed final-output transition requires unchanged Multi mode,
detuning, Voice Count, components, Randomize Phase, compensation,
Cycle Length and child Oscillator properties. Every authored voice
must already be initialized, still running and stable at its
prepared ratio and mix; no Note Off, pending end, active onset
crossfade or expanded active headroom is admitted. It applies the
standard 64-frame smoothstep to the **last actual whole-graph audio**
without replaying or rephasing constituents. A second edit starts
from the current blended output; ordinary non-audition playback
remains immediate.

The new tests in `FmSynthLiveMultiOutputTests` were red before
implementation: [CI #43066845750](https://github.com/logiclrd/Heresy/actions/runs/43066845750)
failed four audible discontinuity assertions for downstream Constant
and Operator changes and interrupted fades. Test oracles compare
all 64 transition frames and post-transition samples to the same
independent Multi reference; no managed PCM allocations are
allowed. Compound Multi-generated child shape, Filter/Source,
multi-voice onset edits and release migration are **not** thereby
qualified. The wider all-property TODO and draft PR remain open.

### Slice 56 — manual mixed-property Multi listening acceptance

The user auditioned an actual **Oscillator→Multi→Multiply with Constant** (the screenshot has Sine 440 Hz, Curvature 5, Spread seven voices, Constant 0.5) and reported correct realtime behavior **without audible clicks/pops** while editing Oscillator **Waveform, Curvature, Frequency Hz, Vmin, Vmax**; Multi **all three Distributions, Detune, Voice Count, Randomize Phase, Gain Compensation, Harmonic Components and Chord Components**; the downstream **Constant**; and the downstream **Operator Operation**.

Mark these specified UI-to-audio paths as *physically listened to* for this fixture. Distinguish the unchanged-Multi downstream fade policy (slice 56) from the independent existing Multi property/onset transitions exercised during this exploratory test. This does **not** prove all timing combinations, exact crossfade math, release-in-progress behaviour, or compound Multi-contained audio/Filter/Sample migrations.

## Slice 57 — frozen compound Oscillator Multi output controls

A Multi's child Synth can be a compound graph with multiple
independent Oscillators and stateless Operators/Constants. An
unchanged compound Multi was still excluded from the established
64-frame *parent output* transition when an audible Constant
gain or Operator mode changed downstream. Slice 57 now allows
the final output fade for that **frozen, unchanged child**.

The compiled child proof checks equal node topology/order and
all Oscillator, Constant and Operator properties, including
frequency multiplier inputs and Oscillator phase indexing.
The already-running bound child is also compared, while the
pre-existing stable-Multi guard verifies unchanged distribution,
voice count, compensation, cycle length, ratios, initialized
voices and no onset/mix glide or pending Note Off. Thus the
output edit never silently replaces any constituent or resets
any of its multiple phase accumulators.

The test-first
`FmSynthLiveCompoundMultiOutputTests` regression
[CI #43077451059](https://github.com/logiclrd/Heresy/actions/runs/43077451059)
failed four first-frame discontinuities before implementation
(Multiply gain, Add Constant, Operator Operation and a second
mid-transition edit). The ordinary playback negative test
passed. The positive suite checks the exact 64-frame output
oracle, unchanged compound child phase trajectory after fade
and zero PCM allocations. It does not claim compound
Multi *child* shape transitions, Sample/Filter/Instrument
migration, already-releasing notes or unrestricted
nested soundgraphs. The user has not yet reported local
listening for this new compound case.

## Slice 58 — frozen deterministic Noise and Node Envelope Multi children

A frozen compound Multi subgraph containing Noise or a Node Envelope
was previously excluded from the 64-frame **parent output** transition,
even when only an independent downstream gain or mixing Operator
changed. In slice 58, deterministic Noise is admitted because its
value is keyed by the preserved child voice's seed, node ID and frame;
no RNG history is migrated. A Node Envelope is admitted only if its
selected EnvelopeId is unchanged and its frozen curve is a
`NodeEnvelopeCurve` with exactly equivalent captured shape under
`HasSameFrozenShapeAs`. Separately constructed but equivalent
snapshots are safe; changing the curve, choosing another Envelope
or using a scripted curve does **not** meet this outer-fade proof.

The compiled child guard additionally matches the original and
prepared graph topology, node IDs, input indices, Oscillator
phase indices, all Oscillator and Constant properties, and Operator
modes. The currently bound Multi child and all active ratios/mix,
voice count and onset conditions are checked; no new voices or
reseeded Noise streams are created in PCM.

`FmSynthLiveDeterministicMultiOutputTests` was **red before the
implementation**:
[CI #43082607418](https://github.com/logiclrd/Heresy/actions/runs/43082607418)
showed four first-frame discontinuities for downstream gain/mode
and a mid-fade edit. The ordinary non-audition and later Note Off
negative/continuity cases passed. The exact 64-frame oracle
uses matched fixed Noise seeds and equivalent separately prepared
Node Envelopes, and checks old phase/release clocks and zero PCM
managed allocations. Physical audio verification and broader
Source/Filter/Multi migrations remain pending; PR #12 stays
draft and unmerged.

### Slice 58 — local physical acceptance of frozen Envelope/Noise Multi child

The user reports that the suggested **already-playing** Synth test passed: a compound child `Add[Multiply[Oscillator, NodeEnvelope], Multiply[WhiteNoise, Constant(0.15)]]` feeding a Spread Multi, with downstream Constant gain changed **0.8 → 0.1** and downstream Operator changed **Multiply → Add**. The submitted graph screenshot depicts **seven** Spread voices rather than the suggested three, and shows the post-edit `Add[Multi #8, Constant #10 (0.1)]` output. This is real-device acceptance of slice 58's **outer output-control transition around an unchanged frozen child**, additional to mathematical regression evidence. It does not qualify live edits *inside* the child, a changing Envelope curve, Sample/Filter/nested Multi components, or already-releasing voices. The PR is draft/unmerged and the main all-property TODO stays open.


## Slice 59 — continuing direct Sample inside a frozen compound Multi

The 64-frame parent output smoothstep now admits the **unchanged
Sample-backed Source** in a frozen compound Multi child of
Oscillator/Constant/Operator/Noise/Node Envelope nodes (still with
at least one Oscillator). This is an outer Constant/Operator transition,
**not** a Source reassignment or new Sample invocation.

The frozen child comparison requires matching SourceId, unchanged
Source FrequencyHz/Constant/ExponentialMultiplier/modulation input,
compatible immutable Sample PCM and loop geometry, and the same note
policy. Per active Multi constituent, the original SampleSoundState
must remain bound and continuing, with no input-finished, already
ended, releasing or unsupported Source Filter state. All previous
Multi stable-ratio, onset, no-Note-Off, voice-count and frozen child
guards remain. The per-constituent preflight prevents beginning a
parent fade when even one child is ineligible. Audio from the original
Sample playhead advances normally through the 64-output-frame
smoothstep; no new PCM-thread Sample state or replay is introduced.

`FmSynthLiveSampleMultiOutputTests` was committed first in
`21ae1891`. It caused four real discontinuity failures in
[red CI #43113214702](https://github.com/logiclrd/Heresy/actions/runs/43113214702),
while the separate natural EOF and ordinary playback cases already
passed. Implementation `05f1fc57` passed
[CI #43113603788](https://github.com/logiclrd/Heresy/actions/runs/43113603788)
with 3,639 tests and required release/ARM64 checks. Tests cover
the exact audible-output fade, interrupted edits, Sample readhead
and EOF continuity, non-audition isolation and zero PCM allocations.

This does not establish migration of changed Sample/Instrument/nested
Synth Sources or arbitrary compound child stateful processing. A local
hardware listening check of this new fixture remains outstanding.
The all-nonstructural-property TODO and draft PR remain open.


### Slice 59 — real-device Sample-backed Multi acceptance

The user reported **PASS** after setting up and auditioning the prescribed
three-voice Spread Multi of an Add-combined direct looping Sample and
390 Hz Sine Oscillator, followed by a downstream Constant edited from
**0.8 to 0.1** and an output Operator switched **Multiply to Add** on
the latched note. The child and Multi themselves were unchanged.
This verifies the newly supported **outer-output editing path** on their
local audio device, not source replacement, changed Filter history,
post-Note-Off edits or all-property acceptance.


## Slice 60 — frozen, unmodulated OnePole inside Sample-backed Multi

An **unchanged single-stage, unmodulated OnePole Filter** may now sit
between a direct Sample Source and an Add-mixed Oscillator **inside**
the frozen Multi child while an external Constant or Operator mode
changes on an already sounding Test note. The parent transition uses
the existing audition-only 64-output-frame smoothstep, not a fresh
Filter or Sample invocation. The Multi child still requires at
least one Oscillator and the previously supported exact frozen
node configurations.

The Filter proof checks the immutable compiled audio input,
single stage identity and **captured stage generation**, Filter
definition ID, all effective parameters, absence of modulation
bindings, unchanged Filter TailSeconds, and compatible actively
processing Filter history for **each** already initialized and
stable Multi constituent. The original immutable Sample PCM/loop
geometry and fractional readhead are retained. An ended/releasing
child, changing stage/coefficient, changed Sample Source property,
modulated or multistage Filter, or still-moving Multi onset/ratio
does not qualify for this *outer fade*. No Filter state,
Sample playhead or additional voice is allocated/reinitialized in
PCM; normal playback remains immediately responsive without
audition fades.

`FmSynthLiveFilteredMultiOutputTests` was committed before the
fix at `8601687c`. [Red CI #43140007560](https://github.com/logiclrd/Heresy/actions/runs/43140007560)
had **four intentional first-frame output discontinuities**
(two gain cases, output Operator and second mid-fade edit);
the Sample EOF/tail and non-audition cases already passed.
A fifth failure in an existing, unrelated maximum-phase FIR
allocation test is **not** a desired red assertion. The
bounded implementation `18d393ff` passed
[CI #43140366054](https://github.com/logiclrd/Heresy/actions/runs/43140366054)
with **3,645 tests** and ARM64/release validation. Preflight
allocation-avoidance refinement `26c8d1ca` subsequently passed
[CI #43140559227](https://github.com/logiclrd/Heresy/actions/runs/43140559227).
The compiled stage-generation capture `9e639348` is included
in the final source and will be checked at documentation HEAD.
The oracle verifies exact 64-frame blend, continuing bound
readheads and Filter histories beyond the blend, sample EOF,
rapid edits, zero managed PCM rendering allocations, and
ordinary playback isolation.

Physical listening on the filtered-Sample Multi fixture is
**pending**. No new in-Multi Filter parameter edit, arbitrary
Filter-family migration, SourceId reassignment, post-Note-Off
migration, nested Instrument/Multi migration, or all-property
acceptance is claimed. The PR remains draft and the main TODO
remains unchecked.


## Post-slice-60 additive-output listening regression and bounded fix

The user reported audible clicks in the slice-60 local fixture
when **Add** was the final output Operator and a large change
was made to its Constant input, or when switching that Operator
between **Add and Multiply**. The equivalent Constant change
under **Multiply** did not click. The prior 64-frame smoothstep
oracle proved sample continuity at the publication boundary,
but not absence of the high-frequency transient associated
with a rapid DC-bias change.

The eligible audition-only compiled shape
`Multi + Constant → Add/Multiply`, with frozen, active,
stable Multi constituents and all original per-Source/Filter
preflight guards, now uses a **512-frame, phase-coherent affine
gain-and-offset morph** whenever Add is involved (or such
a morph is already active). Unlike the original 64-frame
held-output fade, every transitional frame evaluates the
actual advancing Multi signal, applying interpolated gain
and bias at the final output. Repeated edits join the
previous active mapping; switching from an in-progress
64-frame Multiply fade retains the last audible value.
There are no extra DSP voices or allocations in PCM.
Other graph shapes and Multiply-only changes keep the
original 64-frame contract; regular playback/export
remains immediate.

The new red-first `FmSynthLiveAdditiveMultiTransitionTests`
[run #43173794531](https://github.com/logiclrd/Heresy/actions/runs/43173794531)
had **four expected failures** in Add gain/mode and
interruption cases. Implementation `392b3c9d`
[run #43174461292](https://github.com/logiclrd/Heresy/actions/runs/43174461292)
passed **3,650 tests** including exact moving-signal
512-frame regressions, prior 64-frame unaffected Multiply
cases, no PCM allocations, ordinary playback isolation and
release/ARM64 checks. **Hardware listening of the correction
is not yet accepted**. The all-property milestone remains
open; PR #12 remains draft and unmerged.


## Slice 60 second listening failure and updated candidate (12 November 2026)

The user **failed the second physical slice-60 audition**, hearing repeated Add/Multiply operator-switch pops and smaller Multiply-Constant pops, despite the previous 512-frame Add/Multiply morph. The earlier 10.7 ms Add transition and 1.3 ms whole-output-sample hold for pure Multiply were both insufficient evidence of inaudibility.

Red-first [5d00f50e](https://github.com/logiclrd/Heresy/commit/5d00f50ecf628372f62bc407bd68445fe3adb686) / [CI #43322859134](https://github.com/logiclrd/Heresy/actions/runs/43322859134) produced exactly six expected transition oracle failures across 48 kHz and 96 kHz. Candidate fix [650ba5e](https://github.com/logiclrd/Heresy/commit/650ba5e607db3f7da8c83029bf2451836f3330a2) converts both Add-related and pure Multiply outer Multi+Constant output edits to a **100 ms, sample-rate-scaled moving gain/DC-offset morph**, including interrupted-edit continuity without freezing an audio frame or rebuilding bound child DSP. Existing Multi groups verify the new oracle, unchanged Sample/Filter histories, Note Off, no PCM allocations and normal playback isolation. [CI #43323623298](https://github.com/logiclrd/Heresy/actions/runs/43323623298) passed **3,656 tests** and all build/release checks.

**User hearing confirmation of the 100 ms fix is still pending.** Large authored Add offsets can separately cause output clipping; test a quiet graph and modest constants if pops persist. Keep broader property-audit acceptance and PR #12 open.


## Slice 60, 13 November: persistent Add DC thump and speaker-only compensation

**Physical finding (NOT accepted):** The 100 ms moving affine ramp fixed essentially all Multiply-only Constant popping, but a pronounced pop remained on Add Constant edits and Add↔Multiply mode switches. Making the upstream graph much quieter by multiplying its Sample by the low-amplitude Oscillator did not reduce the Add pop. The final Add Constant injects a fixed DC bias regardless of upstream amplitude; AC-coupled monitoring can convert a changing DC level into a physical thump.

**Important regression boundary:** First preview DC rejection inside FmSynthSound.Render violated two older, legitimate tests asserting a published additive scalar must remain in raw Synth PCM (red [CI #43373010155](https://github.com/logiclrd/Heresy/actions/runs/43373010155)). Therefore ordinary FmSynthSound.Render and all playback/export PCM retain authored DC unchanged. Only explicitly flagged **Synth Test** StartNoteCommands take the separate **RenderForAuditionMonitoring** path through PlaybackSession/PlaybackVoice. That path renders raw audio normally and subtracts the matching static or moving Add DC bias at the voice-monitor boundary, before voice filters/mix. Sample Test and ordinary note starts retain normal rendering. Existing raw-output tests are not weakened; targeted monitor-output tests and a live-session marked/unmarked integration regression cover the new behavior.

Red-first [c36bb803](https://github.com/logiclrd/Heresy/commit/c36bb80357601b30ea228c48a58e8857548810d5) / [CI #43372673123](https://github.com/logiclrd/Heresy/actions/runs/43372673123) failed exactly two DC-safe-preview assertions and passed the raw-negative assertion. Correct boundary commits: [ee2eab3e](https://github.com/logiclrd/Heresy/commit/ee2eab3e9e8150a95d8bdf4d18e3717a12f85c7c), [d71653de](https://github.com/logiclrd/Heresy/commit/d71653de22844db4edad21be4eab8ec1c65b39dd), [272ed477](https://github.com/logiclrd/Heresy/commit/272ed477f977fc1e1b75cf3f4d66f6655022e845), and [ab8ad40a](https://github.com/logiclrd/Heresy/commit/ab8ad40a19b3d2fb1defd9f0608eeb300d50ed26). CI and **the physical retest of this correction remain outstanding** at this checkpoint. Keep the feature branch, PR #12 draft, and all-properties TODO open.


### Final virtual-channel monitor routing, automated pass (13 November 2026)

The real PlaybackSession integration test [ab8ad40a](https://github.com/logiclrd/Heresy/commit/ab8ad40a19b3d2fb1defd9f0608eeb300d50ed26) exposed a missing flag handoff: PlaybackSession.StartVirtualNote was constructing a PlaybackVoice without passing StartNoteCommand.MonitorFinalSynthDc, although its physical-note counterpart did pass the flag. The original result was a full +0.85 output DC in a marked preview [CI #43374338210](https://github.com/logiclrd/Heresy/actions/runs/43374338210). [59ea2a98](https://github.com/logiclrd/Heresy/commit/59ea2a9876d3b88e6b1f3280106fdfd12da950ce) fixes virtual-note propagation. [CI #43374659683](https://github.com/logiclrd/Heresy/actions/runs/43374659683) passed **3,660 tests**, Release and Linux ARM64. The user's third physical test diagnosed the Add DC issue but **has not yet verified the new monitor-boundary correction**. Keep the slice-60 hardware result pending and PR #12 draft.


## SourceId reassignment — identity-only Sample alias, initial bounded slice

**Invariant and scope:** A SourceId is an editable scalar reference, not a graph wire. If two IDs resolve to the **same actual SampleSound object** already held by a running direct Source node, replacing the ID has not changed its PCM, loop, channel geometry, note configuration, frequency basis or state. Keeping the original SampleSound/Invocation/PitchTrajectory is exact and no output fade is needed. A mismatched object or changed decoded PCM remains **unadoptable**; the next new note still selects the revised SourceId. In particular, no arbitrary Sample/Instrument retargeting is implied and the reference-identity check must not be treated as a PCM-content equality algorithm.

**Red-first regressions:** `FmSynthLiveSourceIdTests` checks A → alias B → A under the same live Sample object with exact PCM/history preservation and no PCM allocations, and the negative case of a different Sample object with different PCM. The rejected case must leave the live old voice unchanged, while a new note uses the selected new Sample. Scope is deliberately direct Sample only, not a transitive Instrument tone selection or new Synth generation.

**Still needed for different SourceIds:** a per-invocation off-PCM **prepared replacement SourceVoice** bound to the newly selected asset; a deliberate local onset and timeline policy (do not reinterpret a different PCM as the old asset's playhead); bounded 100 ms old/new audible blend with output and state/EOF/release accounting; latest-wins cancellation and cut/Note Off tests; no callback allocation; whole-voice preflight for multiple simultaneous Source nodes; and device hearing tests. These broader semantics are not implemented by the identity-only slice. PR #12 stays draft, all-property TODO open.
