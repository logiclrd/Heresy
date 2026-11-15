# PR #12 — C2 inspector-to-live-PCM acceptance matrix

**Status: C2 IN PROGRESS; bounded C2(a), C2(b) and C2(c) inspector/shared-editor/held-PCM regressions accepted 2026-11-14.** This ledger supplements [the PR closing audit](synth-live-pr12-closing-audit.md); it does not supersede its C1/C3/C4 decisions or authorize completing the overall Synth TODO. Derived from the actual controls in `FmSynthEditorControl`, the committed model in `FmSynthDocumentEditor`, `SongPlaybackTransport`, and existing UI/renderer/session tests. It separates *controls exposed* from *runtime state eligible* and *evidence actually observed*.

## Evidence vocabulary — avoid claiming a full path from one partial test

- **U:** Real headless inspector control exercised, showing its committed `FmSynthNode`/shared definition and semantic `AudioRevision` behavior. Direct calls to `FmSynthDocumentEditor.UpdateNode` alone are **not U**.
- **P:** The inspector's `_changed` handler invoked its live publication callback. That is the **handoff request**, not proof that the production transport worker prepared/published the revised frozen graph.
- **T:** Production `SongPlaybackTransport`/snapshot event preparation and ordered session publication observed; `SongPlaybackTransportTests` has generic direct/parent/shared-dependency cases, but these must not automatically count for an individual inspector control.
- **V:** An *already-playing* `PlaybackVoice` adopts the correct compiled revision or retains a knowingly incompatible older generation with correct status and end; new Note On PCM alone is insufficient.
- **A:** Concrete held-voice output samples, integrated histories and fade/release invariants checked with a deterministic oracle. A standalone `FmSynthSound` regression establishes renderer A, not by itself UI+T+A.
- **H:** Human physical-device listening and callback deadline evidence belong to **C6/C5**, not a prerequisite for declaring this narrower automated matrix row UI-correct. C7 combines the results.

**Acceptance rule:** Every *currently editable, nonstructural* control needs the appropriate **U/P/T/V/A** evidence for its actual eligible runtime cases, or a specific and approved old-generation policy (C1) verified by status/lifecycle tests. Treat absent evidence as **OPEN**, not as a failure or as passing. Structural graph wires and canvas positions are classified separately. A simulated session-publication callback is not labeled T.

## Real inspector surface inventory and first disposition

The matrix below deliberately records code paths and *partial* existing evidence even where a single cross-layer test is missing. Rows are classified as **OPEN** unless explicitly identified as bounded C2(a)/C2(b)/C2(c) evidence.

| Real inspector surface | Known UI/renderer/session evidence | C2 remaining cross-layer acceptance |
| --- | --- | --- |
| **Constant — Value** | Text commit through `AddConstantInspector`/`UpdateParameter`; renderer smoothing in `FmSynthCompoundShapeLiveTests`, `FmSynthLiveFilterOutputTests`, Multi/output cases | **OPEN** U/P/T/V/A from actual Value TextBox, including audible output vs pitch/control-only Constant |
| **Oscillator — Waveform, Curvature, Vmin, Vmax** | `FmSynthSourceEditorTests.OscillatorWaveformInspectorCommitsAndPublishesHeldAuditionRevision` proves waveform **U/P only**; `FmSynthCurvatureInspectorTests.CurvatureAndWaveformShareLeftCellWithReactiveRightHandPreview` proves edit/revision behavior; renderer C3 compound and unified-transition tests | **OPEN** actual control→frozen revision→held PCM and phase/shape transition for every field |
| **Oscillator — Frequency Hz; Constant toggle; Exponential multiplier toggle** | `FmSynthConstantOscillatorInspectorTests.ConstantCheckboxFollowsFrequencyInputAndCommitsOnlyChanges` proves toggle U and no-op revision; renderer frequency/phase controls have direct regressions | **OPEN** all three U/P/T/V/A; demonstrate frequency-only change is not mistaken for shape fade |
| **Oscillator — Multiplier input selector** | `AddOscillatorInspector` edits a node ID/edge | **STRUCTURAL**, not nonstructural PCM-adoption promise; verify old-generation/new-note isolation and no erroneous edit elision |
| **Source — selected Source (None / Sample / Instrument / Synth)** | `FmSynthSourceEditorTests.SourceInspectorPlacesConstantDirectlyUnderFrequencyAndSelectsSample` proves Sample selector U; C4(j)–(m) cover separate SourceId/onset/deferred/None/release PCM; C1 specifies unrelated Instrument/cross-family deferral | **OPEN** real selector P/T/V/A, including same Sample alias, different Sample, None and approved incompatibility/status. Do not claim arbitrary Instrument substitution supported |
| **Source — Frequency Hz; Constant toggle; Exponential multiplier toggle** | Source inspector U for Constant; renderer Source scalar and C4(j)/(m) independent pitch histories | **OPEN** real editor U/P/T/V/A, attached Sample rate history and simultaneous SourceId+mode change |
| **Source — Multiplier input selector** | `AddSoundSourceInspector` edits graph wiring | **STRUCTURAL**: old voice may stay on prior generation |
| **Source — Instrument tone Volume shared-editor action** | Selection and modeless Volume button; shared Volume audition/atomicity in `FmSynthMultiSourceLiveVolumeTests` and transport dependency tests | **OPEN** control/shared editor→T/V/A against unchanged bound Instrument; do not conflate with switching Instruments |
| **Envelope node — Envelope choice (None/Node/Script/New...)** | `FmUnassignedEnvelopeEditorTests` verifies unassigned Node object and authoring; renderer `FmSynthLiveGraphEnvelopeTests` covers frozen audible/Script/parameter-only cases; C4(n) proves changed audible release refusal | **OPEN** dropdown/editor U/P/T/V/A, with a deliberately incompatible already-releasing curve |
| **Shared Node Envelope curve** | Shared editor and dependent Synth publication built; renderer frozen curve and selected Instrument Volume coverage | **OPEN** actual shared editor commit (including no-op) through dependent Synth T and audible held PCM |
| **Shared Script Envelope expression** | **C2(c) U + shared-event + T ordering + V/A**: the real Script editor Apply button commits valid source 0.25→0.75; invalid and identical input cause no audio revision/notification. `SongPlaybackTransport.PublishSharedObjectEditAsync` publishes the referenced child before its parent through its real command gate and frozen document snapshots. The original held physical note adopts a separately compiled Script curve; 801 PCM frames match a 100-ms smoothstep and an independently compiled expression oracle. | **PARTIAL**: T uses a recording audio source and V/A uses a separate real `PlaybackSession`; the test does not claim device-backed PCM from that transport. Shared Script release timing, mixed Filter/Script graphs and unproven equivalence remain open in their respective rows |
| **Filter — shared stage defaults (OnePole CutoffHz)** | **C2(c) U + shared-event + T ordering + V/A**: actual `FilterEditorControl` numeric stage draft, ApplyDraft (the handler used by Apply stage), invalid NaN/no-op suppression, changed 45-Hz cutoff and one audio revision, actual `SongPlaybackTransport` child→parent publication. Held physical voice adopts continuing OnePole state; 1600 PCM frames match an independently authored shared Filter oracle and demonstrably differ from an unedited ongoing voice. | **PARTIAL**: this covers an unchanged OnePole stage plan only; production transport audio backend is a recorder, while V/A uses a distinct real `PlaybackSession`. Other stage families, live modulation, FIR preparation and structural refusal remain separate |
| **Filter — shared definition selected / None / New...** | `FmFilterInspectorTests.FilterSelectorAndInstanceEditorAppearOnlyWhenDefinitionIsSelected` proves UI selection/panel; `FmFilterAuthoringTests` validates persisted configuration | **OPEN** U/P/T/V/A on *equivalent* stage plans; structural stage-kind/count/generation changes retain old generation per C1 |
| **Filter — per-use stage parameter overrides** | `FilterInstanceEditorControl` embedded under inspector, `FmFilterAuthoringTests`; renderer transactional IIR/FIR/filter-port coverage and physically heard SvfNotch/CombFeedback | **OPEN, high risk** actual per-use control commit→Filter preparation→held PCM for representative distinct stage kinds and multi-stage atomicity |
| **Filter — Tail (seconds, nonnegative)** | Actual inspector TextBox and `FmSynthDocumentEditor` semantic graph update; renderer authored-tail lifetime regressions | **OPEN** U/P/T/V/A and original released-note tail ownership |
| **Filter — audio Input and modulating parameter ports** | Connected node/port IDs are graph edges in `FmSynthGraph` | **STRUCTURAL**; numeric parameter edits along *existing* connections remain eligible and covered by separate Filter row |
| **Multi — Presets** | **C2(b) U+P+V+A and real detached worker**, `FmSynthInspectorLivePcmTests.MultiPresetAndCycleInspectorEditsPrepareHeldOnsetsAndMatchAuthoredPcm`: real Unison action switches Randomize Phase false→true on held oscillator Multi, compiles a new definition, worker prepares replacement onset, same voice accepts, PCM matches independent authored reference and differs audibly from unedited session; preset selection resets to its placeholder | **PARTIAL**: actual production transport handoff T remains open; another preset changing *multiple* distinct fields (e.g. spread voice count/distribution/components/gain) is not yet directly exercised |
| **Multi — Distribution, Detune, Voice Count** | Inspector ComboBox/text and `FmMultiNode.With...`; renderer C3 and C4(a)/(c)/(g) supports eligible Multi updates and prepared child expansions | **OPEN, high priority** each UI control U/P/T/V/A, worker completion without PCM construction or stale current voice |
| **Multi — Components (Harmonic and Chord)** | TextBox conditionally edits `HarmonicComponents` or `ChordComponents` depending on Distribution; renderer has ratio/component coverage | **OPEN** U/P/T/V/A for both distributions; do not infer one from the other |
| **Multi — Randomize Phase, Gain Compensation** | Independent toggles; renderer has compatible onset/rephase and gain migration regressions | **OPEN** U/P/T/V/A, with no unintentional Note restart |
| **Multi — Override cycle; Cycle Length (samples at C4 / 48 kHz)** | **C2(b) U+P+V+A and real detached worker**: real checkbox enables default derived cycle override, real Cycle Length TextBox Enter commits **1120**, and checkbox disables override back to null. Each UI action publishes once; actual held voice adopts after off-PCM preparation, with frame-for-frame PCM equality against separately authored Multi revisions | **PARTIAL**: production transport queue/worker handoff T remains open; scripted/rapid overlapping inspector edits remain separate C2/C4 evidence |
| **Multi — input** | Changes the input node edge | **STRUCTURAL** |
| **Operator — Operation (Add/Multiply)** | `AddOperatorInspector` ComboBox and established renderer/physical Add/Multiply smoothing | **OPEN** control U/P/T/V/A for Add/Multiply in an audible compound graph |
| **Operator — Operation (Minimum/Maximum)** | **C2(a) U+P+V+A**: `FmSynthInspectorLivePcmTests.OperatorMinimumMaximumInspectorCommitPublishesAndUpdatesHeldPcm` exercises the real ComboBox twice, checks one semantic revision/callback per change and unchanged revision on reselect, binds its callback to `PlaybackSession.AdoptLiveSynthParameters`, verifies the *existing* physical voice's compiled Sound/older count and all 202 PCM frames against 100-ms smoothsteps. Release CI at [#43664807533](https://github.com/logiclrd/Heresy/actions/runs/43664807533). | **PARTIAL**: the callback is hooked directly to an in-process PlaybackSession, **not** the real background `SongPlaybackTransport` worker (T still OPEN); a mixed/filtered operand graph also remains to be tested before all Operator modes are signed off |
| **Operator — Input node IDs** | Graph wiring edit through inspector TextBox | **STRUCTURAL** |
| **Noise** | `FmNoiseEditorTests.WhiteNoiseEditorIsOutputOnlyWithNoFrequencyControlAndSafeDeletion`; no authored numeric inspector properties currently | **NO NONSTRUCTURAL UI PROPERTY** to audit. Existing deterministic Noise renderer/state integration is separate. Proposed future Vmin/Vmax is an independent TODO, not silently treated as implemented |
| **Graph node/connection/output changes** | `FmSynthDocumentEditorTests` and canvas/UI structural tests | **STRUCTURAL**, verify old-generation status and fresh-note publishing in representative UI workflow |
| **Canvas location/waypoints** | `FmSynthDocumentEditorTests.MovingNodeAndChangingRouteHintsAreDocumentOnlyEdits` proves document-only audio revision invariance | **PRESENTATIONAL**; add an editor callback test showing zero Synth publication for a layout-only edit |

## C2(a) acceptance notes

The first new test is intentionally more demanding than the existing waveform-inspector callback-count test, but **does not claim the actual transport-worker step**. It initializes a real headless `FmSynthEditorControl` over a document with two audible Constants **0.2 and 0.8** feeding an Operator. The session starts a physical note using the original **Minimum** graph at 1 kHz, renders ten frames, then the real operation ComboBox commits **Maximum**. The inspector advances both semantic revisions exactly once and invokes its published callback. The callback compiles a new frozen graph and supplies it to `PlaybackSession.AdoptLiveSynthParameters`; the **same physical PlaybackVoice** must adopt it and `CountOlderAudioVoices()` stay **zero**. All frames **10–110** match the expected **0.2→0.8, 100-ms smoothstep** within 2e-6. Re-selecting Maximum is a no-op. At frame **111** Minimum is selected again; frames **111–211** match the reverse **0.8→0.2** smoothstep with unchanged note identity. Two changed inspector selections cause exactly **two** audio revisions, document revisions, callbacks and no false older status.

This isolates a previously incomplete **UI→publication request→session/PCM** proof. It is not a genuine red-first bug fix: the existing behavior passed its first compiled test run. It is **not** a C6 device listening result and does **not** cover background transport queue worker scheduling. Only this named row is partially accepted; C2 remains unchecked.

## C2(b) — Multi preset and cycle override through actual inspector, detached worker and PCM (2026-11-14)

**Bounded automated acceptance.** The new headless
`FmSynthInspectorLivePcmTests.MultiPresetAndCycleInspectorEditsPrepareHeldOnsetsAndMatchAuthoredPcm`
uses the same actual `FmSynthEditorControl` as C2(a), but unlike the
stateless Operator test explicitly drives the **real
`FirPreparationScheduler`**. Three independently started
`PlaybackSession` histories use the same real Synth graph,
sample rate (1,000 Hz) and one sustained physical oscillator-backed
Spread(2) Multi: the **UI-edited actual**, a **separately authored
oracle**, and a **never-edited control**. The oracle's four frozen
`FmMultiNode` revisions are constructed from the specified expected
parameters, *not* copied from the UI's committed object.

The Multi begins with **Detune 15 cents, 2 voices, randomized phase
off, gain compensation on**, and no cycle override. At frame 12 the
actual **Unison** preset changes randomized onset to on; the
preset action returns its ComboBox to the `Presets` placeholder.
After a 140-frame comparison window, the actual **Override cycle**
checkbox is enabled with the UI's neutral-C4 derived cycle;
after another 140 frames, the actual numeric **Cycle Length**
TextBox commits **1120** by Enter; after another 140 frames, the
checkbox is disabled, returning authored `CycleLength` to null.
Every selected edit must change `AudioRevision` and
`DocumentRevision` exactly once, call the live Synth publication
callback once, and retain the **same physical `PlaybackVoice`**.
While a requested prepared onset is pending, older-audio count stays
zero rather than treating it as a definitively missed revision.

For each of four revisions, the real session and independent oracle
queue detached Multi onset preparation and the test explicitly waits
for **two successful worker completions** (eight total, one per
session and edited revision). Only after PCM-boundary
`AdoptPreparedFirControls` can either existing voice publish its new
frozen Synth. The test verifies the identities and no older-audio
marks, then checks **140 PCM frames per revision**, sample by sample
at `2e-5` tolerance against the independently authored reference.
A strengthened test also compares the first changed held-note
signal against the unedited session and requires an audible,
non-vacuous **maximum absolute difference over 0.05**. No NoteOn,
voice rebuilding or synthetic historical rewind is used for any of
the four changes.

The original test commit `bef25a6` failed CI at a **test-only
namespace import**, and `00c0bc8` failed at a **long/int test
variable type**. These **did not run** the C2(b) assertions and are
not evidence of a renderer defect. Corrected `6285e97` passed
the C2(b) test and the full test suite; `a6a96e8` strengthened
the audible-difference proof. Production DSP/UI source files were
not changed for this slice.

**Evidence classifications: U + P + V + A, including real detached
Multi preparation and PCM-boundary adoption.** The inspector's
callback is a test-controlled, in-process publication to the
real `PlaybackSession`; it does **not** exercise
`SongPlaybackTransport.PublishSynthEditAsync`'s own ordered
background compilation worker (T is still open). This named
**Unison** preset changes the phase policy only because the other
Unison settings already match; it does **not** prove every
multi-field preset's atomic UI behavior. Remaining distribution,
detune, components, voice count, randomize/gain toggles and
actual background-transport T are separate C2 matrix entries.

**C2 remains OPEN**; this slice does not change the C1/C3/C4
checkmarks or C5/C6/C7 and final all-properties TODO.

## C2(c) — real shared Script/Filter editors, dependent transport publication and held PCM (2026-11-14)

**Accepted bounded regression suite:** [`FmSynthSharedEditorLivePcmTests`](../Heresy.Tests.UserInterface/FmSynthSharedEditorLivePcmTests.cs), added by [`63e4422`](https://github.com/logiclrd/Heresy/commit/63e4422190a86c0e115d9130d48246a37ad9ce3d). Both new real-headless-editor regressions passed [full test-head CI #43668497957](https://github.com/logiclrd/Heresy/actions/runs/43668497957): **3,767/3,767 tests** (Core 910; Render.File 29; Render 1,213; Scripting 105; Playback 356; UI 1,154), Release desktop and Windows apphost/release-bundle smoke, Linux ARM64 shared codecs. No production DSP/UI/transport code was changed.

**Actual Script Envelope expression:** `ScriptEnvelopeApplyPublishesDependentSynthsAndCrossfadesHeldPcm` starts a real physical Synth note whose graph output is a shared **Script Envelope** compiled from `0.25` at 8 kHz, plus a document-level parent Synth that references it. The actual `ScriptEnvelopeEditorControl` button is activated through its click event. Submitting a disallowed expression is rejected atomically: no `AudioRevision`, `SharedAudioObjectCommitted` notification or dependent publication. Submitting `0.75` emits **one** precise shared-object event; submitting the same expression again emits none. That event is forwarded through the actual `SongPlaybackTransport.PublishSharedObjectEditAsync` command gate: the *child* is prepared and enqueued before the *parent*, with no unrelated or duplicate publication. In the separate renderer session, the original held `PlaybackVoice` adopts the newly compiled `ScriptEnvelopeCurve`, and every one of **801 PCM frames** agrees both with a separately compiled authored-expression oracle and the explicit **0.25→0.75, 100-ms smoothstep** (2e-5 tolerance). Older-audio count remains zero and the voice is not restarted.

**Actual shared Filter stage default:** `SharedFilterStageEditPublishesDependentsAndRetunesContinuingOnePole` uses `FilterEditorControl`'s real `CutoffHz` TextBox and the editor's `ApplyDraft` handler (the same handler wired to its **Apply stage** button). An invalid `NaN` cannot mutate the shared definition or notify. Committing a **45-Hz** OnePole cutoff emits exactly one audio revision and shared-object notification, which causes the production transport to prepare/enqueue the dependent child before its Synth parent. Reapplying identical parameters emits nothing. The independently continuing physical note adopts the new prepared OnePole coefficients without replacing its voice or discarding its prior filter state. Its **1,600 PCM frames** agree with a separately authored equivalent-stage Filter oracle (3e-5 tolerance), and the edited note differs by more than **0.05** in maximum absolute amplitude from an untouched ongoing reference. This is an audible, non-vacuous result, not merely a revision-count assertion.

**Evidence precision:** These exercises establish real shared-editor **U**, precise workspace notification, and the *production transport's dependency-order T* using frozen snapshots and a **recording audio-output stub**. The PCM check is run in a separate real `PlaybackSession` using the same commit event and actual frozen `FmSynthSound`/Filter state; it establishes **V + A**, but does **not** pretend the recording transport backend emitted PCM or that the MainWindow dispatcher was exercised. This bounded test proves one scripted constant-expression revision and one compatible shared OnePole retune, **not** every Script release behavior, Filter kind/FIR stage plan, nested graph geometry or stage-kind structural refusal. `C2` stays unchecked pending its remaining real controls and those bounded edge policies.

## Next bounded steps (proposed, not preapproved scope expansions)

1. **C2(d): Source selector/frequency/modes and oscillator family** — bind already proven C4/C3 renderer histories to the real inspector and transport paths, including an intentionally retained unrelated Instrument candidate.
2. **C2(e): remaining Multi/Filter/Operator/Constant controls and structural/presentational no-op rules** — use this matrix to record each actual control's disposition and exact proof before C2 closes. Keep C5/C6 measurements and C7 merge authorization separate.

**Gate status:** C1, C3 and C4 are complete; **C2 remains OPEN**, as do C5/C6/C7 and the overarching all-properties Synth TODO. PR #12 remains draft/unmerged.
