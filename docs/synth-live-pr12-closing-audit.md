# PR #12 — closing audit and finite acceptance plan

**Audit date:** 2026-11-14. **Audited branch:** `feat/synth-live-parameter-foundation` at `ec2e1b324c5d1ee757fcf5e4684b78986ee466e8`. **State:** draft, unmerged. This is a **source/test inventory and closing plan**, not new DSP implementation, not a passing UI/device property matrix, and not authorization to merge. The current automated baseline is [CI #43610633937](https://github.com/logiclrd/Heresy/actions/runs/43610633937), 3,706 passing tests, Release, release-bundle smoke and ARM64 checks. The authoritative completion requirement remains [todo.md](todo.md), “Apply *all non-structural Synth property changes* to ongoing preview notes in realtime.”

## Evidence standard and scope

- **Implemented:** the renderer/editor contains a compatible publication or a deliberately safe old-generation branch.
- **Automated verified:** a particular UI/PCM regression confirms a stated invariant (not every arrangement of those nodes).
- **Physically verified:** an explicitly reported device listening test for that arrangement. Passing CI alone does not establish inaudibility or real-device deadline compliance.
- **Unresolved probe:** neither confirmed supported nor confirmed broken in the specified compound/runtime circumstance. Reproduce red-first **only if** the behavior actually violates an accepted invariant. Do not write tests requiring a known undesirable click or an arbitrary shape whitelist.
- **Documented incompatibility:** a runtime-topology/state incompatibility permits the held voice to retain its prior generation, with new Note Ons using the current definition and appropriate `Playback uses older audio` reporting. Such a refusal must be deterministic, nonmutating, and **explicitly agreed**; mere absence of a preflight implementation does not automatically make a nonstructural property a deliberate exception.

**Baseline boundaries agreed for this closure:** Support for a Pattern or Sequence object as a *new* Synth Source type is **out of PR #12 scope**. Existing Synth Source selectors accept Sample, Instrument (including scripted Instrument), and Synth. Comprehensive live `Sample ↔ Instrument`, `Instrument ↔ Synth`, and unrelated `Instrument → Instrument` invocation replacement is **deferred** rather than made a merge prerequisite; this does not remove the requirement to support independently compatible parameter edits on an *unchanged bound Instrument*. A future Pattern/Sequence Source feature must integrate with the completed live-edit contract. A changed graph node/wire or effective processor topology (Filter stage add/remove/kind/generation, for example) may retain the earlier voice; changing a Filter *parameter* within an unchanged stage is different. No all-source-family blanket promise.

### Architectural coverage confirmed in source

1. `FmSynthEditorControl` commits inspector semantic changes through `UpdateParameter`/`FmSynthDocumentEditor.UpdateNode`; graph-only presentation does not publish audio. Inspector surfaces: Constant Value; Oscillator Waveform/Curvature/Frequency/Constant/Vmin/Vmax/Multiplier input/Exponential multiplier; Source selected object/Frequency/Constant/Multiplier input/Exponential multiplier; Envelope reference/shared Node or Script Envelope editor; Filter selected shared definition, stage overrides and TailSeconds, audio/modulation ports; Multi Presets/Distribution/Detune/VoiceCount/Components/RandomizePhase/GainCompensation/Cycle override and length; Operator kind/inputs; Noise currently no numeric inspector properties. Node/wire/output edits are structural; canvas positions/waypoints are presentational.
2. Prepared revisions are published in ordered transport events, the prior voice's `FmSynthState` retains phase, Source playhead, Filter histories, Multi constituent history and release age when state compatibility permits. `CanAdoptRunningState` preflights *the entire graph*, followed by off-PCM preparation and atomic commit where necessary. Arbitrary incompatible replacement is never implemented as a silent `SoundState` type cast.
3. Sample SourceId replacements are prepared off PCM and crossfaded **at each Source output** for 100 ms before any downstream Operator, fan-out or Filter. Graph-wide wiring/state preflight supersedes the former Add/OnePole whitelist. The same Source cannot start another replacement during its current fade; released voices and simultaneous changed Sample SourceId+frequency-basis edits still decline.
4. Compatible *nested Synth* sound sources can recursively migrate their `FmSynthSound` instances and active histories, including SourceId reassignment between compatible Synth definitions. An **Instrument** selects a terminal tone/source when its `SoundInvocation` is first constructed, and its note configuration can include Volume/Pitch/Panning/Filter envelopes, tone Filter chains and release/NNA settings. Some held Instrument-backed Volume-envelope updates are supported, but replacing an arbitrary Instrument invocation is *not*. Pattern/Sequence are not direct `ISound` graph Source choices.
5. Numeric Filter stage retunes (IIR and prepared FIR) can use two-phase transactional adoption across multiple stages/nodes; changed graph or effective processing topology remains a different category. Exact ongoing FM-bound parameter values matter when worker-preparing FIR. Scripted Envelope equivalence cannot always be proven across revisions. The existing log of slice-specific restrictions is **historical**, not an authoritative current whitelist.
6. Audition-only smooth transitions and DC-monitor policy are distinct from raw Synth rendering and ordinary playback/export. Live output transitions depend on whether a change affects an audible signal, a modulation control, a Filter history, or a stable Multi child. There is **no proved universal single crossfade** for arbitrary nested signal graphs.

### Property-matrix audit (existing proof vs outstanding evidence)

| Surface / edit | Current supported route and specific regression evidence | Close-out gap |
| --- | --- | --- |
| Constant Value | Compatible scalar publication; output transitions in `FmSynthCompoundShapeLiveTests`, `FmSynthLiveSampleOutputTests`, `FmSynthLiveFilterOutputTests`, stable Multi and filtered/sample Multi tests | Probe *upstream* Constant edits in evolving Multi/Source/Filter compound children, plus rapid overlapping revisions and release |
| Oscillator Waveform, Curvature, Vmin/Vmax | Retains phase; bounded audible shape transition in `FmSynthCompoundShapeLiveTests`; `FmSynthUnifiedTransitionTests` covers common 100-ms clock | Probe *inside* a live Multi child and through mixed Filter/Source operators; verify no click without resetting child phase |
| Oscillator Frequency Hz, Constant, Exponential multiplier | Existing phase/rate migration, logarithmic frequency glide and control-only distinction; `FmSynthUnifiedTransitionTests`, `FmSynthChildFrequencyModulationTests` | Real inspector/PCM test matrix for rapid audio-rate modulation and mixed child ancestry |
| Source selected Sample; Source scalar Frequency/Constant/Exponential | `FmSynthLiveSourceIdTests` verifies independent Sample fades, multiple Sources, short EOF, graph-wide mixed Filters/Operators/fan-out, modulated input, atomic refusal; pre-existing forward-integrated pitch controls | Probe SourceId+frequency in one publication (currently declines), a SourceId switch inside a Multi child, overlapping edit during an active 100-ms fade, and Source `None`/silent selection policy |
| Source selected nested Synth | `FmSynthNestedSourceMigrationTests` covers compatible child edits under Multi(3)→OnePole, two compatible Synth SourceId selections, held-sibling isolation, sticky old-audio status on retained incompatible children, and nested worker supersession by newer parent+child revisions | Still probe distinct simultaneous parent/child commits with complex Filter/Source histories, deeper Instrument/Script Envelope combinations, and interrupted/released nested voices (C4 boundary) |
| Source selected Instrument | `FmSynthMultiSourceLiveVolumeTests` now covers compatible selected-Sample Instrument Volume revisions across two Sources under a shared Filter and both nonoscillator and oscillator-bearing Multi(3), including source-local 100-ms gain smoothing, preserved oscillator phase/IIR history, worker-resolved incompatible rephase, rejection of changed release timelines, and all-or-none incompatible-tone refusal | **Approved deferral:** comprehensive cross-family/unrelated Instrument *substitution*. C2 still owns scripted Instrument Volume tones and UI publication; C4 owns active onset-fade interruption, Note Off/release changes and lifecycle transitions |
| Envelope selected/shared Node and Script Envelope | Frozen Node curve migration, plus red-first-fixed audible Script Envelope smoothing inside Multi(3)→OnePole and no-fade Script pitch-only control in `FmSynthLiveGraphEnvelopeTests`; shared-object publication and Instrument Volume paths have separate regressions | C2 must verify Script editor commit/publication for every live field and `None ↔ reference`; C4 owns active Note Off and release-duration changes (audible releasing curve conservatively retained) |
| Filter selected definition; overrides / TailSeconds / shared edit | Atomic multi-processor prepared IIR/FIR retunes and FM-bound controls, `FmSynthLiveFilterOutputTests`, `FmFilterNodeRenderingTests`, multiple Filter realtime tests; user physically edited a three-stage Filter containing **SvfNotch** and **CombFeedback** and heard ongoing changes | Probe each stage-parameter *family*, more than one edited processor per publication, connected modulated FIR, rapid retune, release/tail, nested Multi/Instrument and **Filter definition selection with equivalent stage shape**. Stage add/remove/kind/generation is *effective topology*, not automatically guaranteed |
| Multi Presets/Distribution/Detune/VoiceCount/Components/RandomizePhase/GainCompensation/Cycle | Worker-prepared expansion/retirement/revival and nested worker supersession in `FmSynthMultiRetirementTests` / `FmSynthNestedSourceMigrationTests`; `FmSynthLiveFilteredMultiOutputTests` now verifies simultaneous child Sample-rate + IIR Filter retune under a parent 100-ms moving gain, including oscillator sibling and three independent histories | Deeper Multi with changed child Instrument/Script Envelope, topology-effective onset changes and interruption/release (C4), plus CPU deadline/recovery (C5) |
| Operator Add/Multiply/Minimum/Maximum | Compatible state adoption and smooth output mapping in `FmSynthCompoundShapeLiveTests` and sample/Filter/Multi output tests; user listened to Add/Multiply; final stable Multi + Constant affine transition / monitor-only DC behavior separately tested | Probe `Minimum ↔ Maximum` in an audible compound path, nested unstable Multi, release and non-audition parity; physical listening of high-DC final Add is not evidence that raw output may suppress authored DC |
| Noise | No currently editable numeric Synth inspector properties; deterministic counter-based stream tested with envelope/sample/Multi combinations | Verify unaffected seed/clock across revisions. New Noise Vmin/Vmax is a **separate open main TODO**: when implemented, its live editing must meet the baseline |
| Graph wiring, add/remove/reorder nodes, output, Filter modulation ports, Multi input, Operator inputs, Source/Oscillator multiplier input | Explicit structural classification via `FmSynthLiveGraphShape.HasSameWiring`, graph/UI regression tests | Ensure structural change -> existing voice old generation, fresh Note On new generation, and later compatible edits do not falsely rewrite incompatible old history |
| Canvas positions/waypoints | Document-only, no semantic audio revision | One headless editor no-notification test suffices; not part of live DSP transition |

**Specific source/test anchors:** `Heresy.Render/FmSynthesis/FmSynthSound.cs` (`CanAdoptRunningState`, `NeedsPreparedSampleReplacement`, `CommitPreparedRunningState`, `EvaluateSource`, `PrepareBoundSourceLifetime`); `Heresy.Render/Instruments/InstrumentSound.cs` (`CreateInvocation`, `FrozenToneMap`); `Heresy.UserInterface/Views/FmSynthEditorControl.cs` and `Heresy.UserInterface/Documents/FmSynthDocumentEditor.cs`; tests named above plus `PlaybackLiveSynthCurrencyTests`, `FmSynthNnaVirtualLifecycleTests`, `FmSynthLiveAdditiveMultiTransitionTests`, `FmSynthLiveFilteredMultiOutputTests` and `FmSynthLiveDeterministicMultiOutputTests`. These tests establish their *named cases*, not every Cartesian product of node types and edit paths.

## C1 decision record — 2026-11-14

**Contract, not blanket whitelist:** An inspector field is “nonstructural” in the *authoring* graph when it leaves graph IDs/connections unchanged, but it may change the runtime invocation or stage configuration. The renderer must make an explicit decision about whether it can retain already-running state. A refusal never tears down the note or tries to reuse a different `SoundState` implementation; the new definition is still used for subsequent notes. Pending worker preparation is **not** a definitive refusal.

| Changed property/runtime condition | C1 policy | Reason and acceptance obligation |
| --- | --- | --- |
| Oscillator/Source frequency, exponential or Constant mode **without changing selected source**, ordinary Constant/Operator/Filter numeric changes | **Adopt if complete preflight succeeds** | Preserve clocks, Sample position, Filter state and ordered events; smooth audible changes where necessary |
| Same resolved Sample selected through another SourceId | **Adopt in place** | No new state or Source fade; retain integrated playhead and compatible independent retune |
| Different Sample SourceId, source frequency basis unchanged, note active, no Source fade running | **Worker-prepared Source-local 100-ms fade** | Full graph/child/Filter preflight, all participants atomically ready, downstream graph state continuous; this is *already implemented* |
| Two compatible nested Synths selected through a SourceId edit, or an unchanged nested Synth whose own compatible definition changed | **Adopt recursively** | Preserve child state under whole-tree preflight; independently incompatible descendant stays in its prior generation and never receives unsafe later patch |
| **Different Sample SourceId AND changed frequency/Constant/exponential basis in one authored revision** | **Unfinished eligible edit; C3/C4 regression and decision needed, not an approved permanent refusal** | A new independently prepared Sample can start with its new pitch, but the outgoing Sample needs an explicitly preserved prior pitch trajectory during the crossfade; merely removing the equality guard would risk retuning the outgoing audio unexpectedly |
| **Unassigned `None` ↔ an eligible Sample source** | **Unfinished eligible edit; test and define explicit silence/new-invocation transition** | Authoring wiring is unchanged but an actual voice is created/destroyed. Validate note age, Note Off, fade-in/out, and new-note behavior before claiming support; do not classify it as graph rewiring |
| A second different SourceId while the same Source's 100-ms old/new fade is unfinished | **Temporarily retain the prior generation; C4 still requires explicit interrupted-edit acceptance** | Current `FadeFrom` stores only one outgoing state. Replacing midfade without a bounded 3-way/flattened transition can lose audible history. The immediately requested revision still applies to future notes; do not claim held-note latest-wins until a concrete overlap policy is implemented and verified |
| SourceId reassignment **after Note Off has begun** | **Retain prior generation until release/EOF/tail retirement** | A replacement would introduce a fresh onset in a note whose release semantics are already committed. New notes take the new source; original note must never lose its previous tail |
| A changed **audible Node Envelope curve after Note Off** | **Retain original releasing curve/lifetime until proven migratable** | The old curve may be audible after the new curve ends; replacing it without dual-curve end bookkeeping truncates release. Purely unrelated/matched envelope changes still follow ordinary preflight; revisit only with a concrete release-migration design |
| Source swap involving unrelated Instrument invocations; Sample↔Instrument, Instrument↔Synth | **Approved feature deferral: retain prior generation** | Tone selection, envelopes, tone Filters and lifecycle state are independent types. Same-bound-Instrument *compatible property edits* are **not** blanket-exempt from the audit |
| Sample↔Synth unrelated runtime state kinds | **Retain prior generation unless a separate verified state-conversion/crossfade design exists** | Do not cast/reconstruct a running state on PCM. Reevaluate under a future generalized terminal-ISound replacement effort |
| Filter stage insertion/removal/reordering/kind/generation change, change of node IDs/type/output or actual graph/audio/modulation wiring | **Retain old generation (effective processing topology)** | This is an explicit structural exception. Unchanged stage kinds with changed *numeric parameters* remain eligible, even in multi-stage/modulated Filters |
| Filter definition selection with an *equivalent* stage plan and unchanged graph wires | **Probe under C2/C3; don't infer topology just from ObjectId change** | Identity/version and owned histories must be tested; approve adoption when stage-kind/generation/port and state invariants actually hold, else document the precise incompatible state |
| Script Envelope revision or other authored control that changes compiled processing resources without changing wiring | **Probe, not assumed structural** | If safe stateless control migration cannot be proved, record exactly why and retain older generation; dedicated audible tests determine whether smoothing is required |
| A revision definitively refused by one running note, while another note/new Note On is current | **Sticky per-note older-generation status until that note ends** | Compatible later edits must not pretend the missed generation was retroactively adopted; pending asynchronous preparation alone must not show the warning |

**C1 acceptance contract:** In all refusal cases, no partial Filter/Source/Multi updates occur, the old voice continues at its original clocks, the new definition is available to fresh Note Ons, and the status reflects actual retained voices. Unsupported Source kinds (Pattern/Sequence) and the approved Instrument-substitution deferral are future features, **not tests that certify an eligible property as adopted**. This table resolves disposition *policy*, not the outstanding implementation/proof work for eligible `None` and combined-frequency cases or interrupted fades.

## Finite proposed closing work, priority-ordered

Do not turn this into arbitrary “slice 61, 62, ...” development. Each item begins with a reproducible gap probe, a classified decision, or a bounded stress/acceptance case. **Status at audit: all closure items below open** (regardless of earlier individual-feature tests).

- [x] **C1 — Adopt/retain/refuse policy contract (small, prerequisite; decision record above).** Record agreed deferrals: cross-family/unrelated Instrument SourceId replacements and Pattern/Sequence Source support; runtime-topology Filter stage changes; structural graph connections; no fake continuity after a missed revision. Decide remaining boundary cases **individually**, especially SourceId+base-frequency, `None` ↔ assigned, simultaneous changes to a referenced nested Synth, active Note Off, and reassignments during a fade. For each, require an explicit rationale, new-note behavior, old-generation status, and atomicity; if a nonstructural case can reasonably be supported without new processor topology, treat it as a functionality gap rather than assuming refusal is acceptable.
- [ ] **C2 — Inspector-to-PCM property matrix (medium).** Build a compact auditable matrix across all actual inspector fields and the exposed shared Filter/Envelope editors, with at least one whole-path UI commit/revision/publication test per family and a PCM invariant proving the selected held voice actually adopted the edit. Include presets/cycle, multiple Filter stage kinds, Script Envelope, and Operator Minimum/Maximum; distinguish “UI publishes” from “held voice adopts.” Mark each cell *verified, intentionally retained, or red-first failing* with exact test and device observations.
- [x] **C3 — Compound/nested migration probes (likely highest functional risk).** Use four focused repeatable fixtures: (a) Oscillator waveform/range and Node/Script Envelope edits **inside** a sounding Multi with a Filter or Source sibling; (b) SourceId change **inside** Multi and independently within a referenced child Synth, including held siblings; (c) simultaneous upstream Source/Filter controls and downstream Operator changes through a mixed multi-stage Filter; (d) Filter with connected FM cutoff plus an unconnected coefficient or prepared FIR change during parent Multi output smoothing. Demand no state replay, PCM allocation, missing audible transition, or partial-graph commit. Implement only demonstrated violations; preserve existing invariants. **Disposition:** C3's named deterministic fixtures and compound migration contracts are automated-verified as of 2026-11-14; physical acceptance remains C6 and active interrupted/releasing behavior remains C4.
- [ ] **C4 — Interruption and release/EOF closure (medium/high).** Exercise edit-before-fade-end, latest-wins preparation cancellation, simultaneous multiple held/latched notes, Note Off during the Source fade, active Node Envelope releases, short Sample EOF plus Filter tail, NNA-displaced and scoped virtual notes. If a candidate must stay old, it must keep sounding correctly until retirement and show the correct older-audio status. Include event order and nested child Source notes; do not invent a new release clock.
- [ ] **C5 — Realtime deadline/stress qualification (medium, separate from feature completeness).** Sustained live audition on real hardware with worker queue saturation and Multi voice-count excursions (including 3→17→3) while monitoring underruns, callback duration and recovery. Compare fresh vs already-running notes; once CPU overload causes a retained oversized constituent graph to keep processing silently, document or fix the actual retention/reclamation cause—do not certify realtime simply from zero managed allocations or median offline renderer speed. Keep offline/export semantics unchanged.
- [ ] **C6 — Physical listening and editor acceptance matrix (medium).** Short Linux and Windows (where available) sessions covering every node family, representative compound fixture, two latched notes with distinct generations, rapid changes, Note Off, deliberate incompatible SourceId and structural edits, and audible Filter tails. Record tested graph, change, expected and observed result. Confirm no erroneous `Playback uses older audio` on fully compatible notes. The user's newest passed three-stage `SvfNotch` + `CombFeedback` audition is positive evidence for Filter coefficient editing, but **not proof** that inserting a third Filter stage migrated the old graph generation.
- [ ] **C7 — Final sign-off / bounded merge (small).** Update this matrix with observed disposition for every row, attach test names and device results, confirm full project CI/Release/ARM64 and parity, check no unmanaged UI field or undocumented arbitrary whitelist remains, and only then mark the main [all-property TODO](todo.md) complete and make PR #12 merge-ready **with the user's approval**. Document deferred Instrument Source replacement and future Pattern/Sequence integration as independent follow-ups.

## C3 initial probes — evidence and disposition (2026-11-14)

**C3(b), SourceId inside stable Multi: automatically ACCEPTED.** A new `FmSynthLiveSourceIdTests.SampleReplacementInsideMultiFadesEachVoiceBeforeContinuingParentFilter` regression exercises a three-constituent, equal-rate Multi whose selected direct Sample changes while one parent OnePole continues. The expected 100-ms Source-local fade is baked into a separate reference Source before its own Multi→Filter, with frame-by-frame PCM comparison and zero managed allocations in the measured running segment. [CI #43614484639](https://github.com/logiclrd/Heresy/actions/runs/43614484639) **SUCCESS**, 3,707 passing tests, Release/ARM64 green. This is not evidence for every arbitrary nested Multi/Instrument topology or for physical listening of this exact fixture.

**C3(a), compound waveform within Multi: concrete missing behavior reproduced red-first.** `FmSynthCompoundShapeLiveTests.CompoundOscillatorShapeInsideMultiFadesWithoutResettingChildPhases` builds `Oscillator × Constant → Multi`, where the child is stateless and uses three equal-phase voices. A Sine→Square change previously committed without child-level smoothing even though the identical non-Multi compound graph already had a 100-ms output transition. [CI #43614627614](https://github.com/logiclrd/Heresy/actions/runs/43614627614) failed the sole intended new test: expected first transitioned PCM `0.14334717`, found `0.4`; the other 3,707 tests passed. The old `_isMultiInputSubgraph` guard blocked the existing child output fade.

**Narrow correction committed:** `FmSynthSound` now precomputes whether a Multi child graph is **purely stateless** (Oscillator, Constant, Operator, Noise and Envelope only), and permits its existing 100-ms audition fade at that child's output **only for audible Oscillator waveform/curvature/range and Node Envelope curve changes** after complete state preflight. Children with Sources, Filters or nested Multis are **not** admitted by this new exception: their playheads, IIR/FIR and onset behavior require separate contracts, and must not be frozen by a generic output hold. No PCM-time shape classification/allocation, new child invocation, phase rebase or altered raw non-audition playback. Added `OrdinaryCompoundMultiChildShapeEditDoesNotInheritAuditionFade` to enforce the negative invariant.

**Existing regression defended the narrower gate.** [Implementation CI #43614769030](https://github.com/logiclrd/Heresy/actions/runs/43614769030) and [ordinary-playback test CI #43614802281](https://github.com/logiclrd/Heresy/actions/runs/43614802281) each failed exactly `OffThreadMultiExpansionAddsMissingChildrenWithoutRewindingHeldState`: an unrelated Constant edit inside a growing Multi had incorrectly acquired the output fade (expected first frame 0.8, got 0.5). The older invariant is correct; do **not** weaken it. Commit [`175b3602`](https://github.com/logiclrd/Heresy/commit/175b36027fb82656b8058f2d093363585d4c00ff) preserves immediate Constant/Operator child edits and enables the additional fade only for the targeted shape/curve cases. [Corrective CI #43614963374](https://github.com/logiclrd/Heresy/actions/runs/43614963374) **SUCCESS**: **3,709 / 3,709** automated tests (Render 1,159), Release and Linux ARM64 green. The new compound-child fade test, ordinary-playback no-fade test, pre-existing `OffThreadMultiExpansionAddsMissingChildrenWithoutRewindingHeldState` and Sample replacement inside Multi all passed. **Physical audition of the new waveform-inside-Multi fixture is still pending.**

**Still open:** Inner-Multi edits with live Sample/Filter histories, nested Synth/Instrument cases, C3(c)/(d) simultaneous controls and FM-bound FIR scenarios, C4 overlap/release cases, and real device listening of changed waveform inside Multi. Keep C3 open; these are named evidence gaps, not assumed failures.

## C3(c)/(d) mixed Source, Filter and Operator fixture results (2026-11-14)

**C3(c), simultaneous Source frequency + three-stage Filter retune + downstream Operator: ACCEPTED by automated PCM.**
Regression `FmSynthLiveFilterOutputTests.SimultaneousSourceFrequencyThreeStageFilterAndOperatorEditIsContinuous` edits one existing held Sample Source's base Frequency Hz (without replacing its asset), multiple parameters in **OnePole → SvfNotch → CombFeedback**, and its final Multiply→Add Operator, all in a single prepared/committed Synth revision. An independent Add-output reference receives exactly the same Source rate and Filter changes but *no changed Operator*; its running upstream state is therefore an oracle for the edited Filter/Source history. The complete primary PCM trace matches a 100-ms smoothstep from its last genuinely audible old sample to the moving reference signal, including beyond the transition, with **zero measured PCM managed allocations**. No renderer change was necessary. [CI #43615695970](https://github.com/logiclrd/Heresy/actions/runs/43615695970) **SUCCESS: 3,710 tests**, Release and ARM64.

**C3(d), FM-bound frozen Multi Filter during moving outer gain: RED FIRST, then FIXED and AUTOMATED ACCEPTED.**
New `FmSynthLiveFilteredMultiOutputTests.FrozenModulatedTwoStageMultiFilterKeepsOuterFadeDuringInnerRetune` uses `Oscillator → [OnePole (FM-bound CutoffHz), SvfNotch (unbound CutoffHz/Q)] → Multi(3) → Multiply(Constant)`. First edit the outer Constant from 0.85 to 0.20 and verify that the signal continues with the expected moving **100-ms** gain transition, not an immediate jump. At frame 192, retune the unbound notch while the outer gain is still in flight; the reference graph retains unity gain, but has the same independent Filter and oscillator history. The PCM oracle checks the *entire remaining* outer gain clock while the independently prepared inner Filter state changes, again with **zero measured PCM allocations**.

- The initial red test used invalid whitespace in stage identifiers and was corrected in `547d97a5`; that setup rejection was **not** the DSP red evidence.
- Corrected [CI #43616025244](https://github.com/logiclrd/Heresy/actions/runs/43616025244) failed **only the new intended invariant** (expected -0.33019418, got -0.07769275 at the first output edit). All other 3,710 tests passed. Root cause: the frozen Multi-child output guard only recognized a single *unmodulated* OnePole, even when a connected multi-stage Filter remained entirely unchanged.
- Fix `2f39c63b`/`28c1b29e`: `FmFilterNodeProcessor.HasSameFrozenConfigurationAs` compares complete immutable stage identity/kind/generation, all effective numeric/choice parameters, exact bound port topology and initial controls, realtime numeric mode, and prepared sample-rate conditions. The parent admission rule now uses this **strict frozen equivalence** rather than an arbitrary OnePole whitelist. A changed Filter coefficient or topology is **not** reclassified as frozen; it still requires its own actual state-migration preflight. Children continue forward from existing oscillator phases and Filter histories.
- [CI #43616214415](https://github.com/logiclrd/Heresy/actions/runs/43616214415) **SUCCESS: 3,711 / 3,711 tests** (Render 1,161), Release, bundle/native checks, Linux ARM64. Both new C3 regressions passed, alongside all prior Multi-growth/retirement and standalone Filter regressions.

**Outstanding:** The C3(c) and bounded C3(d) cases above are tested, but **full C3 remains open**: bound-FIR worker redesign while a parent gain is transitioning, stateful inner Multi shape edits, nested Source/Instrument/Synth variants, fast repeated/overlapping edits, and **physical listening of these precise mixed fixtures**. These are evidence gaps, not automatically proven defects. C2 and C4–C7 are not signed off by this CI.

## Manual acceptance update — two-stage modulated Filter inside Multi (2026-11-14)

**User-confirmed physical listening pass** for the human-reproducible portions of the preceding C3(d) fixture: with a latched Synth Test note, the outer gain edit 0.85→0.20 sounds smooth; after that 100-ms transition has finished, independent SvfNotch cutoff/Q edits affect the ongoing audio without audible interruption or unwanted clicks. This confirmation is specifically for these two separate actions, **not** for a manual edit during the 100-ms interval (which is physically impractical). The sub-100-ms overlap at 64 audio frames apart is covered by `FrozenModulatedTwoStageMultiFilterKeepsOuterFadeDuringInnerRetune` and successful CI #43616214415. Do not overinterpret the listening pass as generalized nested Multi/FIR or actual rapid-edit device acceptance.

## C3(d) follow-up — bound FIR inside Multi during moving outer gain (2026-11-14)

**Automated ACCEPTED, with no renderer change needed.** The new
`FmSynthLiveFilteredMultiOutputTests.BoundFirInsideMultiWaitsForWorkerThenRetunesWithoutInterruptingOuterGain` fixture renders
`Oscillator → FirBandPass (FM-bound LowCutoffHz, unbound HighCutoffHz) → Multi(3) → Multiply(Constant)`.
An already-sounding note begins a 100-ms outer Constant/gain transition; while the gain is still moving, the authored unconnected high cutoff changes from 3900 to 8100 Hz. The connected low cutoff remains based on each **actual captured, voice-specific FM control**, not its fallback configuration. A separate unity-gain voice supplies the continuously advancing oscillator/FIR oracle.

The regression insists that **before** the detached FIR kernels are available, the complete edit cannot preflight/commit and the output keeps its original FIR state and moving outer gain. Six independent child FIR preparations (three constituents in each of the two voices) complete via `FirPreparationScheduler`; the owner adopts prepared kernels and then atomically publishes the revisions. Every frame across and beyond the remaining outer 100-ms gain transition matches the unity-gain reference scaled by the uninterrupted original gain ramp, and the measured PCM rendering allocates **zero managed bytes**.

Test committed in `3f44a0ca`; first CI #43618589057 failed to compile **only because the test omitted the `Heresy.Render.Filters` import**, corrected in `ced6a8a4`. This is not a DSP red-first failure. [Corrected CI #43618785504](https://github.com/logiclrd/Heresy/actions/runs/43618785504) **SUCCESS: 3,712 / 3,712 tests** (Render 1,162), Release, native/bundle checks and Linux ARM64 successful. No DSP implementation changes were necessary.

**Human timing boundary:** No user listening of edits during the 100-ms overlap is requested. Two separately human-reproducible checks from the previous Multi+IIR fixture were already reported successful. This new success is an automated PCM/worker preparation acceptance, not physical device certification of every FIR configuration.

**Still open:** C3 remains open for simultaneous structurally *compatible* nested Synth/Source edits, more complex and interrupted inner-Multi shape/voice-count cases, scripted Envelope and Instrument-backed paths and the wider C2/C4–C7 closing gates. The above success does not prove universal overbudget-FIR or stale-worker acceptance; separate existing worker tests cover their named cases only. PR #12 remains draft/unmerged.

## C3 nested Synth/Source under parent Multi/Filter (2026-11-14)

**Compatible child edit: AUTOMATED ACCEPTED without a DSP change.** The new
`FmSynthNestedSourceMigrationTests.CompatibleNestedConstantEditInsideMultiPreservesParentFilterHistory`
holds `Source(referenced child Synth: Constant 0.20) → Multi(3) → OnePole → Output`
at 48 kHz, edits the *referenced child* Constant to 0.60, and keeps the same parent
Multi constituents and OnePole state. All child invocations recursively adopt
the new definition. A separate parent using a baked Sample whose value follows
the 100-ms smoothstep supplies an independent, continuously rendered reference;
every frame agrees within 5e-5, and the measured PCM segment allocates **zero
managed bytes**. The test passed **before** the associated status-accounting fix.

**Incompatible child edit: RED FIRST, then FIXED.** A parent
`Source(child Synth) → OnePole` can correctly adopt its own compatible graph
revision while a structurally changed referenced child Synth remains bound to
its *old* sound. Initially the held voice continued playing that old child,
but `PlaybackSession.CountOlderAudioVoices()` incorrectly returned **0**.
`FmSynthNestedSourceMigrationTests.IncompatibleNestedChildRetainsSoundAndMarksOnlyHeldParentOlder`
reproduced that bug on test-only commit `f67993a`: [CI #43620521906](https://github.com/logiclrd/Heresy/actions/runs/43620521906)
had **1,163 of 1,164 Render tests passing**, with the expected 1-vs-0 failure.
The earlier first test-only commit `7769ac3` failed at compilation due only
to a missing `Heresy.Core.Sequencing` import; it was *not* behavioral evidence.

Commit `ee0b8e7` adds a frozen-state recursive check for a truly retained
incompatible nested Synth. Both immediate and worker-completed live publication
paths mark the existing voice's **sticky older-audio flag** only after their
preflight succeeds; a worker-pending revision is not treated as refusal.
This retains the previous child audio and parent Filter history and keeps
an unaffected new note current. Its old-audio count clears after the older
note is Cut. The change does **not** reinstantiate child audio or broaden
arbitrary Instrument/Source replacement.

[CI #43620816545](https://github.com/logiclrd/Heresy/actions/runs/43620816545)
**SUCCESS: 3,714 / 3,714 tests** (Core 910; Render.File 29; Render 1,164;
Scripting 105; Playback 356; User Interface 1,150), including Release,
native/apphost, release bundle smoke test and Linux ARM64 shared codecs.

**Scope:** These are deterministic PCM and note-currency tests, not a claimed
physical audition of nested edits. C3 remains **OPEN** for selecting between
two compatible nested Synth SourceIds, simultaneous parent/child/held-sibling
updates, nested pending worker generations and more complex Instrument/Script
Envelope cases. C4 interruption/Note Off/EOF and C2 property-matrix closure
also remain open; no sign-off or PR merge is implied.

## C3 compatible nested Synth SourceId selection and held siblings (2026-11-14)

**Automated PCM and transport acceptance; no renderer change required.** The added
`FmSynthNestedSourceMigrationTests.SelectingCompatibleSynthSourceIdKeepsIndependentMultiAndFilterHistories`
selects between two distinct referenced Synth objects (193 Hz and 431 Hz
Sine Oscillators with matching graph layout) behind parent
`Source(child Synth) → Multi(3) → OnePole`. The old and new SourceIds
differ, but the bound child invocation is structurally compatible. Two
running voices at different local ages (211 and 67 frames) each preserve
their own oscillator phase, Multi state, and parent IIR history. Across
4,928 continuing frames, both PCM streams agree within 5e-5 with a
separately migrated same-SourceId child-definition-edit oracle.
Measured PCM rendering allocates **zero managed bytes**; after the
100-ms logarithmic frequency glide, output demonstrably differs from an
unmodified 193-Hz child. Distinct voice ages still produce distinct PCM.

`FmSynthNestedSourceMigrationTests.CompatibleSynthSourceIdSelectionKeepsTwoHeldSiblingVoicesCurrent`
exercises the **PlaybackSession** adoption path with two separate virtual
channels started at different times. Both adopt the parent SourceId
selection without an older-audio mark; a third newly started note uses
the latest selected Synth. Its complete 5,024-frame mixed PCM trace
matches an independently constructed same-ID child-edit session.
Cutting the oldest note does not disrupt the remaining sibling histories
or introduce a currency warning. This is a status-and-PCM regression,
not an assertion that the child-editor UI allows simultaneous editing.

Initial test commit `38128ae` failed to **compile** only because two
float-valued comparison thresholds used double literals. This test
fixture mistake is not DSP red-first evidence; corrected in
[`e9605bd`](https://github.com/logiclrd/Heresy/commit/e9605bd1450621abc4ac01b977776f914cc5b655).
[Corrected CI #43622895976](https://github.com/logiclrd/Heresy/actions/runs/43622895976):
full automated test-suite step succeeded; Release packaging and ARM64
status should be checked independently at the exact audited HEAD.

**Physical acceptance boundary:** The user manually configured a
referenced child Synth under a parent
`Source → Multi(3) → OnePole` and confirmed that changing the
**parent Source node's base Frequency Hz** affected the latched note
without audible clicks, pops, or discontinuities. Opening the child
Synth editor replaces the audition context, so live editing of the
child's *internal* parameters while its parent is latched is not an
exposed workflow, and is **not** a required physical test for PR #12.
The recursive child-definition revisions above are automated safety
coverage, not a claim of such manual playback acceptance.

**Still open:** C3 other simultaneous/overlapping parent-and-child
publications, nested worker/pending-generation isolation and complex
Instrument/Script Envelope cases; C4 interruption/Note Off/EOF and C2
matrix closure. This bounded acceptance does not mark the PR complete,
check the all-properties TODO, or authorize merging.

## C3 nested Multi worker supersession during parent Filter edit (2026-11-14)

**Automated ACCEPTED; no DSP change required.** The parameterized
`FmSynthNestedSourceMigrationTests.SupersededNestedMultiWorkerCannotApplyObsoleteParentRevision`
regression holds a parent `Source(child Synth) → OnePole` at 48 kHz.
The child Synth is `Constant(0.10) → Multi(1)` initially. A first
authored revision would grow the child to four voices, change its
Constant to 0.14, and retune the **parent** OnePole from 1200 to
1700 Hz. Before that revision can be adopted, a second authored
revision supersedes it with `Constant(0.18) → Multi(3)` and parent
OnePole at **2600 Hz**.

Two deterministic worker conditions are exercised:
(1) the obsolete Multi expansion has constructed a constituent and is
held *in flight* on a test-only scheduler gate when the newer edit arrives;
(2) the obsolete expansion has **fully completed**, but has not yet been
adopted by the PCM owner. In either condition the held note stays audible
with its original complete parent/child state during the pending interval.
Supersession cancels the stale candidate without marking the note as
`Playback uses older audio`. The latest child expansion is then
worker-prepared and the parent publishes its Filter and child together.

The complete 5,000-frame resulting two-note session matches, within
7e-5 per sample, an independently constructed session that received
**only the final revision at the same PCM frame**. This comparison
detects obsolete intermediate DSP, Filter-history reset, or partial
parent publication; the final output is separately asserted to have
changed audibly. A new Note On uses the latest selected definition.
The in-flight variant verifies that obsolete work was actually
cancelled; neither worker records an exception.

[Test-only commit `5471eb5`](https://github.com/logiclrd/Heresy/commit/5471eb5a0dc10b8fc8e2f690b197fd96f10e07a0).
[CI #43623932059](https://github.com/logiclrd/Heresy/actions/runs/43623932059):
both regression variants and the full Release test suite **PASS**, with
Linux ARM64 successful. Exact-head final workflow conclusion is
reported separately when available.

**Boundary:** This proves two worker-queue orderings for an actively
held nested Multi and simultaneously changing parent OnePole, not
universal interruptible 100-ms Source crossfades, arbitrary nested
Instrument substitution or active Note Off/EOF. Those belong to the
remaining C3 cases or C4 as specified. PR #12 stays draft/unmerged.

## C3 stateful Sample/Filter Multi child plus simultaneous outer gain (2026-11-14)

**RED FIRST — concrete missing 100-ms moving gain transition.**
`FmSynthLiveFilteredMultiOutputTests.SimultaneousSourceRateFilterRetuneAndOuterGainInsideMultiPreservesMovingAudio`
uses a genuinely stateful Multi child: `Sample Source → OnePole`,
combined with an independent 709-Hz Sine oscillator through Add, then
`Multi(3) → Multiply(Constant)`. One authored revision simultaneously
retunes the continuing Sample Source's base frequency from C4 to
**1.35 × C4**, the OnePole cutoff from **1200 to 3600 Hz**, and the
outer gain from **0.85 to 0.20**. Each constituent owns its own Sample
playhead, continuing IIR history and oscillator phase; no SourceId,
Multi count/distribution/onset, graph wiring or stage topology changes.

A separately maintained **unity-gain** reference receives identical
inner Source and Filter revisions at the same frame (128). Its continuing
PCM is the signal oracle: the primary voice must equal that moving
signal multiplied by the established **100-ms smoothstep** of the
outer gain for all **4,896** post-edit frames, with zero measured
managed allocations during rendering. This requires a genuinely moving
current signal—not a held prior output sample.

[Test-only `1bd891f`](https://github.com/logiclrd/Heresy/commit/1bd891f352f68ed449d91f8c5d73dcedef94b757)
produced the expected **behavioral red** in
[CI #43624563534](https://github.com/logiclrd/Heresy/actions/runs/43624563534):
at the very first edited frame, expected **0.17897737**, actual
**0.04211232**, matching an immediate change to the new outer gain
instead of starting at the old one. All other **1,168 Render tests**
passed. This was not a test-fixture compile error.

**Narrow fix:** [`c159e00`](https://github.com/logiclrd/Heresy/commit/c159e005d7f1e301dae955e46cdc1ad535483450)
adds `FmSynthSound.HasCompatibleRetunedSampleFilterMultiGraphAs`.
Only the dedicated moving **Add/Multiply affine output** transition
may use this new admission path. It requires a stable Multi layout
and fully preflighted child graphs, the same compatible Sample
identity/Source mode, an unchanged oscillator shape and stateless
arithmetic, continuing Sample/Filter histories, and compatible Filter
topology. The source frequency and Filter numeric coefficients may
change together. Nested Multi, selected Sample replacement, finished
children, arbitrary oscillator shape edits and unprepared/incompatible
Filter changes remain excluded. The **general stateless held-output
fade** continues calling the original frozen-child-only guard,
preserving existing growth/onset/ordinary-playback invariants.

[Corrective CI #43624822034](https://github.com/logiclrd/Heresy/actions/runs/43624822034)
**SUCCESS: 3,719 / 3,719 tests** (Core 910, Render.File 29,
Render 1,169, Scripting 105, Playback 356, User Interface 1,150).
Both Release/native/apphost/bundle checks and Linux ARM64 passed.
The originally red PCM test now passes, and there were no other test
regressions.

**Boundary:** This is one concrete compatible Sample/OnePole/oscillator
compound edit under a constant-layout Multi. It does not generalize
arbitrary nested Instrument or Script Envelope mutation, switching
Samples while fading, editing past Note Off, or changing child topology.
Those remaining cases are individually C2/C3 audits or C4 lifecycle
work; no physically impossible multi-control edit during 100 ms is
required to accept this deterministic regression. C3 and the all-field
milestone remain open; PR #12 stays draft and unmerged.

## C3 Script Envelope and Instrument Volume in compound graphs (2026-11-14)

**Script Envelope — RED FIRST, FIXED.** The regression
`FmSynthLiveGraphEnvelopeTests.ScriptEnvelopeChangedInsideMultiFadesBeforePreservingParentFilterHistory`
uses a compiled Script Envelope to multiply a sounding Sine oscillator
inside `Multi(3) → OnePole`. At frame 128 the Script level changes
from **0.80 to 0.12**. A separately baked Sample through the equivalent
unchanged Multi and parent OnePole supplies the expected **100-ms
child-local smoothstep**, integrated by the continuing parent Filter
for 4,896 subsequent frames, with zero measured PCM allocations.

The first Script fixture revisions had an `ISoundResolver` import
error (**not** DSP red evidence). [Corrected test-only `920de5f`](https://github.com/logiclrd/Heresy/commit/920de5f9dfa04d7c6fdaf9b684105ca208bfbb92)
produced a true behavioral red in
[CI #43625819179](https://github.com/logiclrd/Heresy/actions/runs/43625819179):
expected **-0.3667959**, actual **-0.34838054** at the
first checked output frame. The other **1,170 Render tests** passed,
including the separate simple Instrument Filter test.

[Fix `4dc1881`](https://github.com/logiclrd/Heresy/commit/4dc188152210f9bdf995e9c0b38e1a1a0bf89233)
uses the existing `HasSameFrozenEnvelope` test for both Node and
Script curves when selecting an **audible** stateless output fade.
Frozen Node curves compare by actual shape; Script curves can prove
identity only by the same compiled instance. This does not fade a
Script Envelope connected *only* to pitch/FM control, and ordinary
non-audition PCM remains unchanged. [CI #43626005388](https://github.com/logiclrd/Heresy/actions/runs/43626005388)
**SUCCESS: 3,721/3,721 tests** (Render 1,171), Release, native/bundle and
Linux ARM64. An extra Script pitch-only no-fade variant was added in
[`c03f2e6`](https://github.com/logiclrd/Heresy/commit/c03f2e6ac5e8a1b70c32f30ed004ffcfad059b89)
and passed [CI #43626421612](https://github.com/logiclrd/Heresy/actions/runs/43626421612)
(**3,723 tests**).

**Instrument Volume — accepted parent Filter, then RED FIRST inside
stable Multi, FIXED.** A direct matched-Tone, Sample-backed Instrument
Volume edit on **two** already-running Sources retains their own Sample
playheads and fades each Volume curve **before** a continuing parent
OnePole. A baked source-input oracle verifies all 4,896 frames with
zero PCM allocations. This simple compound case passed in the
corrected Script red run above.

The parameterized
`FmSynthMultiSourceLiveVolumeTests.SharedInstrumentVolumeRetuneThroughTwoSourcesAndParentFilterKeepsIirHistory`
then places the same pair of Sources and the Filter **inside Multi(3)**,
with detune=0 and normalized output. [Test-only `76d1cbe`](https://github.com/logiclrd/Heresy/commit/76d1cbe4818bc591dc35b78c2f53905d263b5de8)
reproduced a real retained-old-gain bug:
[CI #43626200646](https://github.com/logiclrd/Heresy/actions/runs/43626200646)
failed only the new Multi variant, expected **0.5999498** and got
**0.6000000** at frame 163; **1,171 other Render tests** passed.
Its culprit was the prior blanket exclusion of all compound
Instrument-backed Multi child graphs.

[Fix `3ce6d98`](https://github.com/logiclrd/Heresy/commit/3ce6d98dcad59e3cb8e45d7beb26b8e517182d65)
allows the existing all-or-none prepared selected-Sample/Volume
transaction inside compound Multi children only when their graph
has **no oscillators or nested Multis**. Such a child cannot require
the separate oscillator-onset/rephase state transaction. The
unchanged Sample identity, selected Tone, active Volume node curve,
unchanged note lifecycle, and fully preflighted Source and Filter
history checks still apply to **every participating Source**; no tone
selection or Sample invocation reconstruction occurs on PCM.
[CI #43626411359](https://github.com/logiclrd/Heresy/actions/runs/43626411359)
**SUCCESS: 3,722/3,722 tests** (Render 1,172), Release, native/bundle
and Linux ARM64. Both parent-Filter and Multi variants passed.
The existing separate incompatible-Source atomicity test was extended
to inside Multi in [`58f92af`](https://github.com/logiclrd/Heresy/commit/58f92af2da65a5094a415991dae6046db459888d);
see [CI #43626620323](https://github.com/logiclrd/Heresy/actions/runs/43626620323)
for its independent final result.

**C3 versus C2/C4 boundary:** The above deterministic Synth renderer
tests establish these two **audible, compatible compound-graph
migrations**; they do *not* prove that every Script Envelope editor
commit produces a new compiled sound or that every scripted Instrument
Tone/Volume definition is migratable. The inspector-to-renderer
publication matrix and explicit unsupported Script Instrument cases
belong to **C2**. Oscillator-bearing Instrument/Multi compound
onset changes remain outside this newly admitted safe branch.
Changing a selected Instrument invocation across source families
remains the **approved deferral**, not a new C3 obligation. Note Off,
release deadline, instrument tails and overlapping interrupted fades
remain **C4**. No physically impossible human 100-ms multitouch edit
is required to accept the deterministic PCM regressions. C3 is not
automatically checked, and PR #12 remains draft/unmerged.

## C3 closing decision — oscillator-bearing Instrument Volume migration (2026-11-14)

**C3 bounded automated acceptance complete.** The last open C3 compound boundary
was a two-Instrument-Source `Source + Source → OnePole` child accompanied by
a real, independently advancing 367-Hz Sine sibling under `Multi(3)`.
Each Instrument Source is bound to a 15,000-frame Sample and its own
Node Volume envelope. The authored edit changes both Volume levels from
**1.0 to 0.25**, while keeping the selected Samples, authored node wiring,
source/Filter lifetimes and envelope node times unchanged. The expected
PCM is produced by an independently baked **source-local 100-ms Volume
smoothstep** before each continuing OnePole, plus the independently
integrating Sine sibling through the same normalized Multi. The
4,896-frame test asserts a 5e-5 PCM tolerance and **zero render-thread
managed allocations**.

[Test-only `09b3f91`](https://github.com/logiclrd/Heresy/commit/09b3f91a2a1a0731c4115d88d4df88d08e46d653)
first reproduced a **behavioral red** in
[CI #43629503699](https://github.com/logiclrd/Heresy/actions/runs/43629503699):
the stable-oscillator variant expected **0.6899251**, actual
**0.6899753** at frame 163, because the blanket oscillator gate
left the Instrument Volume at its old level. The phase-setting
variant of that initial fixture had not yet prepared the worker;
its preflight failure was **not** evidence of wrong DSP. Corrected
[test `da5c02a`](https://github.com/logiclrd/Heresy/commit/da5c02a921e5b8a3fa92fe3392a92b1bfc6a5ec0)
explicitly requests, waits for and adopts the worker-prepared onset
candidate before retesting. [CI #43629569858](https://github.com/logiclrd/Heresy/actions/runs/43629569858)
was then red on **both** sound-output cases, with the other **1,174
Render tests** passing.

[Initial correction `af8c1ba`](https://github.com/logiclrd/Heresy/commit/af8c1ba5ddb52b9c710f03d48b2451506b0e2aeb)
removed the blanket oscillator gate while retaining the nested Multi
exclusion and the existing preflight of *every* selected Instrument
Tone, terminal Sample and Filter. Both new oscillator cases passed,
including the simultaneous `Randomize Phase` edit: its prepared
onset is refused when its replacement's Volume configuration is
incompatible; the already-running oscillator retains its phase
and its original Sample playheads while the compatible gain values
migrate. This is not arbitrary Instrument replacement or a claim
that a changed phase setting always forces a held note to rephase.

**Established release regression defended the stricter boundary.**
The first broad correction caused two pre-existing
`FmSynthMultiTests.IndependentFrozenNodeVolumeCurvesKeepInstrumentSampleRelease`
cases to fail: an edited **release node time**, combined with a refused
oscillator rephase, had incorrectly modified the held Sample release
deadline. Those assertions are correct and were retained unchanged.
[`NodeEnvelopeCurve.HasSameFrozenTimelineAs` in `f9795c0`](https://github.com/logiclrd/Heresy/commit/f9795c081d357068b5d5e45ef8fe2d6104a6748a)
compares frozen node times, sustain-loop mode/points and the final
zero-completion policy independently of intermediate amplitude values.
[Final guard `fd43a63`](https://github.com/logiclrd/Heresy/commit/fd43a63337e3b69c8e600c36e02bc2b4caf537ab)
requires that same timeline for a compatible gain change in an
oscillator-bearing Multi child; an active Note Off or altered release
timeline is conservatively excluded in this case. A changed release
duration cannot slip through merely because the Instrument's
Sample and SourceId remain the same.

[Negative regression `9c5f991`](https://github.com/logiclrd/Heresy/commit/9c5f9917715e4605fa275019f8a27432d60d4db7)
also adds the oscillator-bearing Multi case to
`IncompatibleSecondSampleRejectsEveryVolumeEditButNotIndependentScalarChanges`.
When one bound Tone's terminal Sample is different, no shared Volume
update partially migrates, but the independently eligible final
scalar still applies. [Corrected CI #43630025276](https://github.com/logiclrd/Heresy/actions/runs/43630025276)
is the code-bearing acceptance run. Both the new positive cases and
the pre-existing release and negative-tone invariants must pass;
the audit-head workflow result is separately linked after the audit commit.

**Finite C3 exit:** The four originally named compound fixture families
now have deterministic evidence in the C3 sections above: (a) audible
oscillator shape and Node/Script Envelope changes in Multi; (b) direct
and nested Synth/Sample SourceId changes, including held siblings; (c)
combined Source/Filter/Operator and multi-stage migrations; and (d)
modulated IIR and worker-prepared FIR retunes during an outer Multi
transition. This final selected-tone case closes the remaining
oscillator-bearing Instrument Volume question for **gain-only**
compatible timelines. The C3 checklist is therefore checked as
**automated scope completed**, *not* a blanket Cartesian-product proof.

**Explicit boundaries outside C3:** Script Envelope and Instrument
inspector publication and all property-by-property paths are C2.
An already-active Multi onset fade interrupted by another change,
changed Instrument release timelines, Note Off/EOF, and interrupted
Sample fades remain C4. CPU deadline recovery and hardware audition
remain C5/C6. Unrelated Instrument/source-family selection remains the
agreed deferral. C3 closure does **not** check the overarching
all-nonstructural-properties TODO or authorize merging PR #12.

## C4(a) — superseded Multi onset preparation during an active 100-ms fade (2026-11-14)

**Automated accepted; no renderer change required.**
`FmSynthMultiTests.SupersedingPreparedRephaseDuringActiveMultiOnsetFadePublishesOnlyLatest`
strengthens the earlier
`SecondMultiRephaseWaitsForPriorFadeWithoutLosingPreparedWork` test
from finite-output acceptance to an exact, independent **latest-only
PCM oracle**.

A sounding `Sine(173 Hz) → Multi(3)` at 48 kHz starts in
`RandomizePhase=false / CycleLength=120`. At frame **96**, the first
worker-prepared revision changes onset to
`RandomizePhase=true / CycleLength=820` and begins its established
**4,800-frame (100-ms)** smoothstep between outgoing and new phases.
After **320 frames**, a second revision proposing
`false / 120` is worker-prepared, but the PCM owner correctly refuses
to adopt another onset while an outgoing phase is still audible.
An additional **320 frames** later, that already-prepared candidate
is explicitly **cancelled** and superseded by the latest proposal,
`true / 1350`, which is also worker-prepared. A second independent
held-state oracle sees the first revision and **only** the final
proposal; a third baseline hears only the first revision.
All three share the same noise seed and initial active-frame clock.

During the remaining first fade, the primary and latest-only oracle
must agree *sample for sample*. The newest candidate remains pending
even though its worker result is available; no obsolete prepared phase
is published. After rendering the old fade's last sample at frame
**4,896**, both held states must be able to commit the final prepared
candidate at frame **4,897** without building DSP on PCM. The 5,024-frame
second fade must be exactly equal between the primary and latest-only
oracle. Its first frame equals the baseline's last unchanged phase;
after the second 100-ms boundary the final phase must differ
measurably from the never-updated baseline. The long render segments
verify **zero managed PCM allocations** and finite output. This
detects premature interruption, stalled latest revision, stale
worker adoption, hidden phase restart and silent/no-op completion,
not merely a successful `TryPrepareRunningState` return.

[Test commit `450fa63`](https://github.com/logiclrd/Heresy/commit/450fa63b1cdbab4f3dcd4048a4ce99bc376c7b4f)
initially had an assertion method-group-to-expression-tree **test
compile error**, not a DSP failure. The fixture-only follow-up
[`a301088`](https://github.com/logiclrd/Heresy/commit/a301088e93d1d6cb6de8111f0b6a00394b127328)
used a predicate lambda; [CI #43630917174](https://github.com/logiclrd/Heresy/actions/runs/43630917174)
passed the whole test-suite step with the new regression, plus ARM64
validation. The complete workflow conclusion is independently verified.
No renderer change was required in this slice.

**Remaining C4 cases (not covered by this acceptance):** Note Off in
the middle of Source or Multi onset crossfades, active Node Envelope
release deadlines, short-Sample EOF versus outgoing and Filter tails,
worker cancellation across **multiple** held siblings and nested
Sources, NNA-displaced notes and scoped virtual-channel voice
retirement. Older-audio generation status must remain truthful for
any actual refusal. This regression does not certify these other
event-order/lifecycle cases, nor does it justify checking the main
all-nonstructural-properties TODO or merging PR #12.

## C4(b) — Note Off during a Sample Source fade with release and EOF (2026-11-14)

**Automated PASS; no renderer change required.**
`FmSynthLiveSourceIdTests.NoteOffDuringSampleReplacementKeepsBothReleaseContributionsAndFilterTail`
is parameterized for a direct Sample Source and for three independent
equal-pitch Sample Source invocations inside `Multi(3)`. Both graphs
feed a common sustained Node Envelope and a continuing OnePole Filter
with an authored **3-ms (144-frame)** tail. The test uses one long
outgoing constant-valued Sample (**0.25**), one shorter incoming Sample
(**0.75**, 2,000 frames), and a Node Envelope with sustain at unity
followed by an 11-ms release to permanent zero.

At global frame **128**, the held Synth commits a prepared SourceId
replacement and begins its **100-ms (4,800-frame)** source-local
smoothstep. Both old and new Sample invocations contribute to the
Filter input. At frame **1900**, with the fade still only partially
complete, the test delivers **Note Off** *after* rendering the preceding
1,772 faded frames, so this is a genuinely in-progress interruption,
not just a Note Off timestamp set in advance. The incoming Sample
naturally reaches EOF at global frame **2128**, *during* the subsequent
release. The outgoing Sample must remain available through the parent
Envelope's release even after the incoming EOF, without restarting
either readhead. The Node Envelope's own release ends at exclusive
frame **2429** and the authored downstream OnePole tail ends at
exclusive frame **2573**. There must be no artificial extension to
the full source-fade endpoint at frame 4928.

The independently baked PCM oracle is a *single* Sample carrying the
mathematically correct outgoing/incoming source-local mix at every
frame, through the same Envelope and continuing Filter graph. For
both source layouts, every frame from the edit through Note Off and
release is compared at **5e-5** tolerance. Assertions explicitly
verify: both Sample contributions after Note Off; the outgoing Sample's
continued contribution after incoming EOF; exact retirement at the
Envelope-plus-Filter boundary; zero PCM output thereafter; and
**zero measured managed allocations** in both running and releasing
render segments. Because the oracle shares the authored Filter and
Envelope processing but not the changeable Source invocations, it
detects duplicate/lost release handling, a reset OnePole integrator,
premature EOF truncation and accidental completion at the 100-ms
source-fade boundary.

[Test `bb10e5f`](https://github.com/logiclrd/Heresy/commit/bb10e5fcbf95a54eb10e6660af0f5e1c436c82a0)
passed both parameterized cases in
[CI #43631732357](https://github.com/logiclrd/Heresy/actions/runs/43631732357),
along with the entire Release test suite and Linux ARM64 validation;
the release-bundle workflow conclusion is separately checked.
No production DSP fix was needed.

**C4 remains open.** The earlier Note Off during Multi *onset*
crossfade tests cover definite release and an authored IIR tail;
they do not establish a general sustain-release Envelope with
independent incoming/outgoing onset phases after Note Off.
Remaining C4 coverage includes that distinct active-onset release
case, multiple simultaneously held/latched and NNA/scoped virtual
voices, nested Synth Source release, overlapping active Source
replacement requests, incompatible candidates and older-audio status.
The C2 matrix, realtime stress C5 and physical listening C6 also
remain open. This test does not certify PR #12 for merge or mark the
overall all-properties TODO complete.

## C4(c) — Note Off during a prepared Multi onset with sustained Envelope and independent Filter histories (2026-11-14)

**Automated PASS, no production DSP change.**
`FmSynthMultiTests.NoteOffDuringPreparedMultiOnsetFadePreservesBothOnePoleHistoriesAndRelease`
covers Note Off at global frame **320** and **640**, both during the
active 100-ms onset crossfade begun at frame **128**. The Multi has
**three independently prepared oscillator and Filter states**:
`Sawtooth(309 Hz) → OnePole(700 Hz, 2-ms tail) → Multi(3)`.
A worker-prepared edit changes `RandomizePhase=false` to
`RandomizePhase=true`, with the same 1,600-sample cycle and authored
graph. Every replacement child carries its own phase skip, oscillator
clock and history-transferred OnePole integrator; the old children
remain audible during the outgoing side of the smoothstep.

Outside the Multi, the signal passes through
`Multiply(Node Envelope with sustain and 10-ms release)` and an
unchanged **OnePole(1300 Hz, 2-ms parent tail)**. Note Off is delivered
only *after* actually rendering the preceding 192 or 512 frames of
the first onset fade. The parent sustain Envelope must keep both
incoming and outgoing child Filters exciting and releasing on the
original note clock. Its release becomes permanently silent at the
exclusive frame **off + 481**; the outer Filter's 96-frame tail ends
at precisely **off + 577** (global 897/1217). The still-in-progress
4,800-frame rephase must neither cut off that signal nor prolong it
to its nominal global frame-4928 fade completion.

The independent oracle prepares exactly the same onset edit in a
**separate** `Oscillator → OnePole → Multi` renderer with no Envelope
and **no Note Off**. This generates a complete precomputed moving
signal carrying the distinct old/new phases and their separate
OnePole histories. A new frozen Sample containing that signal then
feeds a separately compiled release Envelope and outer OnePole.
Thus the expected release, Filter integration and retirement cannot
inherit the edited note's release/retirement state. The test compares
every sample both **before and after Note Off** at a 6e-5 tolerance,
asserts audible release, exact parent end, permanent silence after
that end and **zero measured PCM managed allocations**. The stronger
test additionally renders an uninterrupted old-onset baseline and
requires a measurable signal difference after the worker-prepared
rephase; otherwise an erroneously refused onset might have passed
the release comparison vacuously.

[Test commit `3f42db3`](https://github.com/logiclrd/Heresy/commit/3f42db3332410faa66200c9ab9c3443c6f534ce3)
first passed both Note Off cases in
[CI #43635949054](https://github.com/logiclrd/Heresy/actions/runs/43635949054),
**SUCCESS — 3,732/3,732 tests** (Render 1,182), Release bundle
and Linux ARM64. The non-vacuity witness was added in test-only
[`852ef5a`](https://github.com/logiclrd/Heresy/commit/852ef5a6e260bc8380df2e6d52ec6e70d6601bfd);
the strengthened test-suite and ARM64 checks passed in
[CI #43636190582](https://github.com/logiclrd/Heresy/actions/runs/43636190582).
The workflow's final packaging result and this audit-head result are
verified independently. No renderer fix was indicated.

**C4 remains OPEN.** This case establishes release semantics for
already-prepared oscillator onset replacement with unchanged OnePole
plans and an **external** shared sustain Envelope. It does not grant
general live mutation of a child release Envelope or eligibility for
other Filter histories, recursively nested Multi, active fade
supersession *combined* with Note Off, or cross-family Source changes.
Multiple held/latched voices, NNA-displaced and scoped virtual voices,
nested Synth Source release, incompatible candidate status and
simultaneous interrupted Source replacements still require C4
coverage. C2 and C5–C7 remain open; the all-properties TODO remains
unchecked and PR #12 remains draft/unmerged.

## C4(d) — multiple live revisions across physical, scoped virtual and displaced NNA voices (2026-11-14)

**Automated PASS; no production renderer or session changes required.**
Two deterministic `FmSynthNnaVirtualLifecycleTests` regressions verify
that live Synth revision and older-audio status belong to **each
independently sounding voice**, rather than a global editor generation
or its originating virtual channel number.

**C4(d1) — compatible edits, missed generation, fresh notes and retirement.**
`PhysicalScopedAndNnaVoicesRetainIndependentLiveRevisionCurrency`
starts three old Synth notes: one physical, and one each on virtual
channel 7 in independent invocation scopes **101** and **202**.
A second scope-101 note uses `NewNoteAction.Continue`, migrating its
earlier note into the NNA-displaced pool: **four old sounding voices**,
but only three current channel slots. A compatible constant-output
edit `0.25 → 0.5` must adopt across *all four*; older-audio count
stays **zero**, and the resulting mixed PCM settles at **2.0**,
not the previous combined level or a partial update.

A subsequently **structural** Synth edit is genuinely incompatible
with those four held graph states, so the older-audio count becomes
**four**. A newly started unscoped virtual note on channel 99
selects the new revision and does **not** acquire an older-audio flag.
A further compatible scalar edit in that new graph cannot undo
the four earlier misses; the old NNA voice still references the
compatible *pre-structural* sound, not an invented migrated state.
`CancelScopedVoices(101)` retires its **current and displaced NNA**
voices but leaves scope 202 and physical channel 0 sounding, reducing
the old count **4 → 2**. Scoped Note Off on owner 202 retires
only that voice (**2 → 1**); a physical Note Cut retires the final
old voice (**1 → 0**). The fresh note continues at its latest
`0.8` PCM level after its own compatible edit.

[Test `3424a52`](https://github.com/logiclrd/Heresy/commit/3424a523838b5cea7febddd3c48a7aaca67fc0a6)
passed [CI #43638391476](https://github.com/logiclrd/Heresy/actions/runs/43638391476):
**3,733/3,733 tests**, Release/native/apphost/bundle and Linux ARM64.

**C4(d2) — per-voice worker preparation and superseded pending SourceId.**
`PendingSampleRevisionsAcrossScopedAndDisplacedVoicesHonorLatestOnly`
uses the same four-voice layout, but each note contains a terminal
Sample Source rather than a Constant. Three frozen Sample assets
have distinct constant values: **0.2** (original), **0.6**
(obsolete intermediate) and **0.9** (latest). All four notes
receive an initial SourceId replacement that requires worker
preparation, followed by a newer SourceId revision *before any worker
has run*. Pending preparation must **not** mark older-audio.
Each voice cancels its obsolete pending candidate and keeps the
latest request relative to its **actual sounding old Sample**,
not the uncommitted intermediate candidate. The two scope-101
notes, one current and one NNA-displaced, are cancelled before
any preparation is queued. The remaining physical and scope-202
notes each prepare and publish the newest Sample independently.
Their mixed output follows two source-local **100-ms smoothstep**
fades; approximately halfway it is **1.1**, and afterward **1.8**.
Neither the obsolete **0.6** PCM nor a resurrected cancelled
NNA voice is allowed to contribute. Older-audio remains **zero**
both while pending and after publication. A fresh note bound to the
newest Sample increases the final mix to **2.7**.

The first two intermediate test commits
[`f579df3`](https://github.com/logiclrd/Heresy/commit/f579df38086bc329624daa8a98704125fb3092ef)
and [`894d7ed`](https://github.com/logiclrd/Heresy/commit/894d7ed7313f86f48e63fcd5d645da3912f39370)
had a missing **test-only `FirPreparationScheduler` using directive**;
their failed compilation runs were **not** behavioral red evidence.
Corrected [`f4c5b50`](https://github.com/logiclrd/Heresy/commit/f4c5b501a436576dfc4d2e7557576e434c5ffa3a)
passed the automated test suite and ARM64 in
[CI #43638723593](https://github.com/logiclrd/Heresy/actions/runs/43638723593).
Its final Release-bundle result and this audit-head result are
verified separately. The test uses *supersession before enqueue*;
it does not assert that already-running or saturated worker jobs
can be forcibly stopped mid-construction. C5 owns that stress case.

**C4 is still OPEN.** Verified here: physical/scoped/NNA enumeration
for compatible and incompatible edits, per-note sticky currency,
independent cancellation and retirement, pending-worker latest-wins
before enqueue, and Source-local PCM smoothing on surviving voices.
Still outstanding are the full session-level matrix for live
**already-releasing** notes, NNA `Off`/`Fade` and host Filter-tail
currency, linked nested Synth Sources and ancestor scopes, pending
worker results already in flight during cancellation, and definitive
incompatibility while a worker owns a different revision.
C2 and C5–C7 remain open. Do **not** check C4 or the overarching
all-nonstructural-properties TODO; PR #12 remains draft and unmerged.

## C4(e) — already-releasing scoped NNA, physical notes and host Filter tails (2026-11-14)

**Automated PASS, no production DSP/session change required.**
The parameterized
`FmSynthNnaVirtualLifecycleTests.ReleasingScopedNnaAndPhysicalVoicesRetainIndependentCurrencyAndHostTails`
regression runs with both `NewNoteAction.Off` and
`NewNoteAction.Fade`. It expands the earlier four-voice C4(d)
currency checks into **actual already-releasing** state and
independent **forked host Filter histories**.

A looping Sample selected through a nested Synth/Instrument has an
enabled sustain-loop Volume Envelope with a **three-frame release**.
Its parent Synth includes a OnePole with a **2-ms graph tail**.
Physical channel 0 and independently scoped owners **101 and 202**
(virtual channel 7) each hold an instance; all three hosts also have
their own OnePole **100-Hz channel-output Filter**, with prepared,
nonzero IIR tail budgets. A compatible parent OnePole cutoff change
**150 → 240 Hz** is accepted on all three held voices before
displacement, with older count **0** and the physical voice referring
to the new sound.

At session frame **120**, starting a second owner-101 note displaces
that owner's previous note into NNA **Off** (original Note Off at
frame 120) or **Fade** (exclusive fade end frame **124**).
The old NNA receives a **fork** of its owner-101 output Filter history;
the new current note continues through the un-forked host.
Owner 202 receives Note Off at frame **121**. At frame **122**
a structurally different Synth graph is definitively incompatible
with the original held physical note, the owner-101 current note,
the owner-202 releasing note and the displaced NNA note. The
older-audio count is exactly **4**; NNA Sound identity and its
original release/fade origin remain unchanged.

After rendering to frame **134**, both the NNA and owner-202 release
have retired; the old count is **2**. Note Off of owner 101 at frame
134 and rendering to frame **144** drops it to **1**, leaving physical
channel 0 alone. Physical Note Off at frame **144** and rendering
through frame **149** retires the last old voice (**1 → 0**).
**Real nonzero host Filter-tail PCM continues after frame 149** while
the older count remains **0**: an output Filter tail is not a
resurrected old voice. The fixture also asserts audible nonzero
release PCM, forked host Filter identity/tail budget and NNA
`NoteOffTime`/fade-end preservation.

[Test-only commit `cd300b0`](https://github.com/logiclrd/Heresy/commit/cd300b09d1c158c513809aef1b2b874590ee5f66)
passed [exact-commit CI #43640392451](https://github.com/logiclrd/Heresy/actions/runs/43640392451):
**3,736/3,736 tests**, comprising Core 910, Render.File 29,
Render **1,186**, Scripting 105, Playback 356 and UI 1,150;
Release desktop-native and Windows apphost resources, versioned
release-bundle smoke and Linux ARM64 shared codecs also succeeded.
The two additional tests are the `Off` and `Fade` cases.

**Bounded acceptance, not C4 closure.** This proves a concrete
already-releasing, host-filtered multi-owner event order; it does
**not** cover every Envelope-change-during-release policy, nested
ancestor scope and referenced-child revision path, interrupted
Source-local crossfade or worker result already in flight during a
later publication/cancellation. Those cases and C5 CPU deadline
stress remain separate. C4, C2, C5–C7 and the overarching Synth
live-properties TODO stay **unchecked**; PR #12 remains draft,
unmerged. Next C4 probes: nested Synth/ancestor scoped release
ownership and in-flight worker supersession with incompatible
publication before old workers complete.

## C4(f) — nested Synth revision currency across independent scoped releases (2026-11-14)

**Automated PASS; no production DSP/session fix required.**
`FmSynthNnaVirtualLifecycleTests.ScopedNestedSynthReleasesKeepChildRevisionsAndOriginalOwnerClocks`
exercises **referenced-child Synth publication** while a physically
owned parent and independent scoped/nested-owner notes remain active
or are already releasing. The parent has **no own sustain/release
Envelope**: its Source references a child Synth whose Constant is
multiplied by a sustain-loop Node Envelope (40-frame release at the
fixture's 1,000-Hz sample rate). A parent OnePole supplies an
independent 3-ms graph Filter tail.

Owners **101** and **202** both use scoped virtual channel **7**;
physical channel **0** is separately held. At frame **10**, a new
owner-101 note displaces its original into an **NNA Off** release,
whose original Note Off remains frame 10. Owner 202 gets Note Off
at frame **11**. A child Constant edit **0.2 → 0.5** then
publishes **child-before-parent** at frame **12**, mirroring the
transport dependency order: the child-only session publication does
not mark unrelated parent-origin voices older; the recompiled parent
adopts all four relevant voice states with older count **0**.
A third unedited-session PCM reference proves that the compatible
child edit causes *actual audible PCM change* in the releasing
ancestor graph, rather than being silently skipped. A second,
separately compatible reference confirms frame-by-frame equality
and preservation of each running ancestor Filter history.

At frame **20**, the referenced child is changed to a structurally
different Add/Multiply graph. The parent topology itself stays
stable, but each of four old active note states has an incompatible
nested child: the older-audio count becomes **4**, and the
already-releasing NNA keeps its original frame-10 release origin.
The complete mixed PCM across frames **20–27** matches an oracle
which never received that incompatible child revision, including
any continuing 100-ms compatible transition. No graph replay or
partial child migration is allowed. A fresh owner **303** note
started at frame **28** uses the newest child and is not older.

By frame **60**, owner-101's NNA Off and owner-202's release,
including parent graph tails, have retired: older count **4 → 2**.
Off on owner 101 and rendering to frame **110** gives **2 → 1**;
Off on physical channel 0 and rendering to frame **160**
gives **1 → 0**. The fresh scope-303 note remains audibly
active. No sibling-scoped release or structural child publication
resets another note's release, steals its output Filter history,
or transfers its sticky older mark.

The initial deterministic fixture was committed in
[`75f99fa`](https://github.com/logiclrd/Heresy/commit/75f99fa0aec12ab555b8a339be598810153df03e)
and passed its own full CI. Its non-vacuity improvement, adding
the unedited-output PCM oracle, was committed in
[`e59479e`](https://github.com/logiclrd/Heresy/commit/e59479eecf50a0c8c7ac6313f7b34ef6b7570b35)
and passed [CI #43641354569](https://github.com/logiclrd/Heresy/actions/runs/43641354569):
**3,737/3,737 tests** (Core 910; Render.File 29;
Render **1,187**; Scripting 105; Playback 356; UI 1,150);
Release desktop/native host, Windows apphost resources, versioned
bundle smoke and Linux ARM64 shared-codec validation also passed.
These are **automated PCM and lifecycle** claims, not a new
physical-device listening result.

**C4 stays OPEN.** This bounded release case does not establish
arbitrary multi-level ancestor/private-session cancellation,
incompatible child state hidden inside every possible Multi/Instrument
combination, editing the audible release Envelope while it is
already releasing, or **in-flight worker completion/cancellation**
under a later incompatible revision. The next focused C4 fixture
is worker results in flight versus late incompatible publication and
voice cancellation, including no resurrection of scoped/NNA notes.
C2, C5–C7 and the main all-nonstructural-property Synth TODO
remain unchecked. PR #12 remains draft and unmerged.

### Adjacent UI issues deliberately outside PR #12 closure

Two independently actionable main-line TODOs were logged in [`docs/todo.md`](https://github.com/logiclrd/Heresy/blob/main/docs/todo.md) via [`dd55714`](https://github.com/logiclrd/Heresy/commit/dd55714d677ee8ee4641594e1f8ae2cb394039c3): stale **Configured stages** after editing a shared Filter definition and selected-stage controls hidden to the right in a non-resizable strip instead of below the list. `FmSynthEditorControl.AddFilterInspector` currently deliberately avoids rebuilding on popup close to preserve unsaved per-use drafts, and `FilterInstanceEditorControl` uses a left/right layout; the eventual fix must refresh safely without losing those drafts. These are real UI defects, but not evidence that a held note failed a realtime coefficient update.

### Exit criterion

PR #12 can be declared complete when (1) every *currently available* nonstructural field has a specified verified live path **or** a narrowly defined, accepted incompatibility preserving both voice and generation semantics; (2) the named compound, interrupted and released-note risks have concrete tests/observations, not merely speculation; (3) automated test/worker/PCM invariants and physical listening/stress are green; and (4) open follow-ups are explicit. **Do not equate 3,706 passing tests with this all-properties sign-off; do not expand scope to Pattern/Sequence Source creation or comprehensive Instrument substitution.**
