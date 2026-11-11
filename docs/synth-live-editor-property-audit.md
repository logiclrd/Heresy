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
| Envelope: selected Envelope object; shared curve edit | Reachable Envelope node uses stateless prepared evaluation on existing time axis; separate Instrument Volume-envelope equivalence has limited shape migration. Changed selected envelope is not automatically a stateful curve replacement | Scripted/nested-envelope revision acceptance, release timing, user edits in connected dependencies |
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
   **Still open:** whether a held Instrument-backed voice can adopt
   a genuinely changed Envelope/Filter DSP state; transport
   notification alone is not proof of in-place sound migration.
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
