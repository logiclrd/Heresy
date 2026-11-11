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
| Constant: Value | Replaces reachable scalar and adopts without stored state | Confirm UI-to-PCM for representative graph descendants |
| Oscillator: Waveform, Curvature, Vmin, Vmax | Recompiled waveform/curvature/range with retained phase. In live audition, a **single-output oscillator** now blends from the last audible sample to the new waveform over 64 output frames. A mid-fade edit restarts from the actual blended sample. No second voice or PCM allocation | Extend click-safe treatment through Operators, Multi, Source modulation and other oscillator-containing graphs; physical click tests |
| Oscillator: Frequency Hz, Constant, Exponential multiplier | Existing phase integrator continues at newly authored instantaneous rate; frequency-only edits are not subjected to the shape fade | UI/PCM matrix and high-rate modulation acceptance |
| Oscillator: Multiplier input | Graph wiring change | Structural-generation rule applies |
| Source: SourceId (Sample / Instrument / Synth) | Inspector permits reassignment. Renderer can migrate compatible **nested Synth** state, but arbitrary Sample/Instrument Source swaps remain unsupported for held voices; the next new note receives the new selection | **Open property gap**: prepare replacement Sample/Instrument invocation, define new-asset playhead and click-safe bounded transition, Note Off/EOF/release handling |
| Source: Frequency Hz, Constant, Exponential multiplier | Preserves original child playback position/frequency basis while applying forward rate/mode changes | Verify real inspector with Sample, Instrument and Synth sources across prior pitch automation |
| Source: Multiplier input | Graph wiring change | Structural-generation rule applies |
| Envelope: selected Envelope object; shared curve edit | Graph Envelope nodes are stateless. **Slice 46:** a single direct Instrument→Sample Source adopts changed shared node Volume at its original note clock through a 64-frame gain blend (manually heard). **Slice 49:** multiple eligible Instrument-backed Sample Sources feeding Operators now preflight and adopt their shared Volume edits atomically; compound Multi constituents remain excluded | Open: compound Multi/onset coordination, scripted/nested tone Sources, other Envelope targets, rapid edits, device listening for multi-Source cases |
| Filter: shared definition, per-use stage overrides, Tail Seconds | Existing coefficient/FIR and stage-identity preflights can adopt compatible prepared parameter changes and tail updates; graph rewiring and unsupported stage topology remain conservative | **Open coverage**: every typed stage parameter, Filter selection/structural stage changes, multi-filter tails and audible click/throughput acceptance |
| Filter: audio and modulation inputs | Graph wiring change | Structural-generation rule applies |
| Multi: Distribution, Detune, Voice Count, Harmonic/Chord Components, Randomize Phase, Gain Compensation, Cycle Length, presets | Ratio/mix glide, worker-prepared growth/onset phases, Sample playhead continuity and bounded fades have dedicated renderer coverage; feasibility depends on constituent/state eligibility and 128-voice cap | Audit combinations, nested State/Filter cases, rapid edits, maximum headroom, UI-to-PCM |
| Multi: input selection | Graph wiring change | Structural-generation rule applies |
| Operator: operation (Add / Multiply / Minimum / Maximum) | Existing calculation semantics can update with unchanged wiring and state | UI-to-PCM coverage |
| Operator: input node IDs | Graph wiring change | Structural-generation rule applies |
| Noise | No editable sound parameters; seed history must survive neighboring edits | Nested noise/phase audit |
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
release-duration compatibility exception. The generalized lifecycle was
automatically tested in slice 48 but has not been physically listened to.
It is not equivalent to adopting changed held Instrument envelope curves.

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
recorded in the development plan. The two-Source version still needs
physical listening, and the all-properties Synth live-edit milestone
remains open. PR #12 stays draft/unmerged.
