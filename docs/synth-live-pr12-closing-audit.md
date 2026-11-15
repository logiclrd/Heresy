# PR #12 — closing audit and finite acceptance plan

**Audit date:** 2026-11-14. **Audited branch:** `feat/synth-live-parameter-foundation` at `ec2e1b324c5d1ee757fcf5e4684b78986ee466e8`. **State:** draft, unmerged. This is a **source/test inventory and closing plan**, not new DSP implementation, not a passing UI/device property matrix, and not authorization to merge. The current automated baseline is [CI #43610633937](https://github.com/logiclrd/Heresy/actions/runs/43610633937), 3,706 passing tests, Release, release-bundle smoke and ARM64 checks. The authoritative completion requirement remains [todo.md](todo.md), “Apply *all non-structural Synth property changes* to ongoing preview notes in realtime.”

**C2 active work (2026-11-14):** The [granular inspector→publication→held-PCM ledger](synth-live-pr12-c2-inspector-matrix.md) is now the current C2 status source. It inventories the *actual* available controls and separately tracks real UI commit (U), inspector callback (P), production transport publication (T), held-voice migration/currency (V), and PCM oracle (A). **C2(a)** covers the real Operator Minimum↔Maximum ComboBox, audio/document revision and callback counts, and 100-ms held-voice PCM crossfades without an older-audio mark. **C2(b)** covers the Multi **Unison preset**, **Override cycle** toggle and numeric **Cycle Length** TextBox, with real detached `FirPreparationScheduler` and independently authored PCM oracle. Those tests wire inspector callbacks directly to a live `PlaybackSession` and do not prove production transport T. **C2(c)** now exercises the real shared Script Envelope and shared OnePole Filter editor controls, including invalid and no-op suppression, exact `SharedAudioObjectCommitted` notifications, and **production `SongPlaybackTransport` child-before-parent publication** on a recording audio backend. Separate real renderer sessions prove the same held voice adopts the Script 100-ms fade and continuing OnePole retune against independent PCM oracles. The test does not claim the stub transport emitted PCM, that the MainWindow dispatcher ran or that other Script/Filter kinds are complete. C2's granular acceptance matrix below still governs untested fields. The original C2 property-matrix table below remains the broad source-level inventory, not a completed UI-to-PCM checklist. **C2 stays OPEN**; C1/C3/C4 remain closed, C5/C6/C7 and the overarching all-properties TODO remain open.

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
3. Sample SourceId replacements are prepared off PCM and crossfaded **at each Source output** for 100 ms before any downstream Operator, fan-out or Filter. Graph-wide wiring/state preflight supersedes the former Add/OnePole whitelist. The same Source cannot start another replacement during its current fade: eligible later SourceId requests now coalesce into a single latest-wins deferred intent, which starts ordinary detached preparation only after the first fade finishes. Released voices still decline; a simultaneous Sample SourceId+FrequencyHz/Constant/exponential-mode edit is now eligible if the entire graph is compatible: the detached incoming Source uses the new basis while the outgoing Source retains its prior frozen rate controls.
4. Compatible *nested Synth* sound sources can recursively migrate their `FmSynthSound` instances and active histories, including SourceId reassignment between compatible Synth definitions. An **Instrument** selects a terminal tone/source when its `SoundInvocation` is first constructed, and its note configuration can include Volume/Pitch/Panning/Filter envelopes, tone Filter chains and release/NNA settings. Some held Instrument-backed Volume-envelope updates are supported, but replacing an arbitrary Instrument invocation is *not*. Pattern/Sequence are not direct `ISound` graph Source choices.
5. Numeric Filter stage retunes (IIR and prepared FIR) can use two-phase transactional adoption across multiple stages/nodes; changed graph or effective processing topology remains a different category. Exact ongoing FM-bound parameter values matter when worker-preparing FIR. Scripted Envelope equivalence cannot always be proven across revisions. The existing log of slice-specific restrictions is **historical**, not an authoritative current whitelist.
6. Audition-only smooth transitions and DC-monitor policy are distinct from raw Synth rendering and ordinary playback/export. Live output transitions depend on whether a change affects an audible signal, a modulation control, a Filter history, or a stable Multi child. There is **no proved universal single crossfade** for arbitrary nested signal graphs.

### Property-matrix audit (existing proof vs outstanding evidence)

| Surface / edit | Current supported route and specific regression evidence | Close-out gap |
| --- | --- | --- |
| Constant Value | Compatible scalar publication; output transitions in `FmSynthCompoundShapeLiveTests`, `FmSynthLiveSampleOutputTests`, `FmSynthLiveFilterOutputTests`, stable Multi and filtered/sample Multi tests | Probe *upstream* Constant edits in evolving Multi/Source/Filter compound children, plus rapid overlapping revisions and release |
| Oscillator Waveform, Curvature, Vmin/Vmax | Retains phase; bounded audible shape transition in `FmSynthCompoundShapeLiveTests`; `FmSynthUnifiedTransitionTests` covers common 100-ms clock | Probe *inside* a live Multi child and through mixed Filter/Source operators; verify no click without resetting child phase |
| Oscillator Frequency Hz, Constant, Exponential multiplier | Existing phase/rate migration, logarithmic frequency glide and control-only distinction; `FmSynthUnifiedTransitionTests`, `FmSynthChildFrequencyModulationTests` | Real inspector/PCM test matrix for rapid audio-rate modulation and mixed child ancestry |
| Source selected Sample; Source scalar Frequency/Constant/Exponential | `FmSynthLiveSourceIdTests` verifies independent Sample fades, multiple Sources, short EOF, graph-wide mixed Filters/Operators/fan-out, modulated input, atomic refusal; pre-existing forward-integrated pitch controls | C4(j) now verifies simultaneous SourceId+frequency/Constant/exponential edits under one Filter and two-Source Add atomicity. Still probe SourceId switching inside a nested Multi, late frequency/mode edits within a deferred fade, further release/short EOF combinations, and the interaction of Source `None` selections with interrupted prior fades; C4(k) now proves the basic `None` ↔ Sample onset/retirement and shared Filter-tail cases; C4(h)/(i) verify held-voice latest-wins and release/EOF boundaries |
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
| **Different Sample SourceId AND changed frequency/Constant/exponential basis in one authored revision** | **Worker-prepared Source-local 100-ms fade, with independent old/new pitch controls (C4(j))** | The detached incoming Sample captures its new frequency and mode at a fresh local onset. The outgoing Sample retains the pre-edit frozen Source node definition and forward-integrated playhead throughout the fade; no historical playback is reconstructed, no third history is created, and whole-graph preflight remains atomic. Actual modulation wiring changes remain structural |
| **Unassigned `None` ↔ an eligible Sample source** | **Eligible bounded live adoption (C4(k))** | `None→Sample` starts a worker-built fresh Sample at the actual live-publication frame and fades in from silence at the Source output; `Sample→None` fades the outgoing, forward-integrated Sample to silence without constructing an invocation. Both preserve siblings and downstream Filter histories; an already-releasing note cannot begin a fresh Sample onset. Terminal/sibling-backed EOF and Filter-tail lifetime have narrow PCM proofs, not arbitrary repeated-transition coverage |
| A second different SourceId while the same Source's 100-ms old/new fade is unfinished | **Defer compatible replacement until the active fade ends; latest pending SourceId wins** | Current `FadeFrom` still stores one outgoing state. The held voice finishes its original 100-ms mixture unchanged, with no premature older-audio warning. Subsequent SourceId edits replace the single pending intent; once the fade finishes, full preflight and worker preparation start an ordinary second 100-ms Source-local fade. Fresh notes use the latest definition immediately. An already-releasing note or a structural modulation-wiring change does not gain unsafe deferral. A compatible simultaneous SourceId + frequency/mode revision may itself be deferred behind the current fade under C4(j)'s independent-pitch policy. If Note Off arrives while a Sample replacement is deferred, the pending fresh onset becomes definitively missed **at that Note Off event** and older-audio currency is sticky only until the old voice retires. A sole expired Source cannot resurrect a retired note; an expired branch in an otherwise continuing multi-Source note may accept a prepared replacement |
| SourceId reassignment **after Note Off has begun** | **Retain prior generation until release/EOF/tail retirement** | A replacement would introduce a fresh onset in a note whose release semantics are already committed. New notes take the new source; original note must never lose its previous tail |
| A changed **audible Node Envelope curve after Note Off** | **Retain original releasing curve/lifetime until proven migratable** | The old curve may be audible after the new curve ends; replacing it without dual-curve end bookkeeping truncates release. Purely unrelated/matched envelope changes still follow ordinary preflight; revisit only with a concrete release-migration design |
| Source swap involving unrelated Instrument invocations; Sample↔Instrument, Instrument↔Synth | **Approved feature deferral: retain prior generation** | Tone selection, envelopes, tone Filters and lifecycle state are independent types. Same-bound-Instrument *compatible property edits* are **not** blanket-exempt from the audit |
| Sample↔Synth unrelated runtime state kinds | **Retain prior generation unless a separate verified state-conversion/crossfade design exists** | Do not cast/reconstruct a running state on PCM. Reevaluate under a future generalized terminal-ISound replacement effort |
| Filter stage insertion/removal/reordering/kind/generation change, change of node IDs/type/output or actual graph/audio/modulation wiring | **Retain old generation (effective processing topology)** | This is an explicit structural exception. Unchanged stage kinds with changed *numeric parameters* remain eligible, even in multi-stage/modulated Filters |
| Filter definition selection with an *equivalent* stage plan and unchanged graph wires | **Probe under C2/C3; don't infer topology just from ObjectId change** | Identity/version and owned histories must be tested; approve adoption when stage-kind/generation/port and state invariants actually hold, else document the precise incompatible state |
| Script Envelope revision or other authored control that changes compiled processing resources without changing wiring | **Probe, not assumed structural** | If safe stateless control migration cannot be proved, record exactly why and retain older generation; dedicated audible tests determine whether smoothing is required |
| A revision definitively refused by one running note, while another note/new Note On is current | **Sticky per-note older-generation status until that note ends** | Compatible later edits must not pretend the missed generation was retroactively adopted; pending asynchronous preparation alone must not show the warning |

**C1 acceptance contract:** In all refusal cases, no partial Filter/Source/Multi updates occur, the old voice continues at its original clocks, the new definition is available to fresh Note Ons, and the status reflects actual retained voices. Unsupported Source kinds (Pattern/Sequence) and the approved Instrument-substitution deferral are future features, **not tests that certify an eligible property as adopted**. This table resolves disposition *policy*, not the remaining interrupted-fade combinations, property-matrix completeness, hardware timing or physical listening.

## Finite proposed closing work, priority-ordered

Do not turn this into arbitrary “slice 61, 62, ...” development. Each item begins with a reproducible gap probe, a classified decision, or a bounded stress/acceptance case. **Status at initial audit: all closure items below open; current checked items incorporate subsequent acceptance evidence** (regardless of earlier individual-feature tests).

- [x] **C1 — Adopt/retain/refuse policy contract (small, prerequisite; decision record above).** Record agreed deferrals: cross-family/unrelated Instrument SourceId replacements and Pattern/Sequence Source support; runtime-topology Filter stage changes; structural graph connections; no fake continuity after a missed revision. Decide remaining boundary cases **individually**, especially SourceId+base-frequency, `None` ↔ assigned, simultaneous changes to a referenced nested Synth, active Note Off, and reassignments during a fade. For each, require an explicit rationale, new-note behavior, old-generation status, and atomicity; if a nonstructural case can reasonably be supported without new processor topology, treat it as a functionality gap rather than assuming refusal is acceptable.
- [ ] **C2 — Inspector-to-PCM property matrix (medium).** Build a compact auditable matrix across all actual inspector fields and the exposed shared Filter/Envelope editors, with at least one whole-path UI commit/revision/publication test per family and a PCM invariant proving the selected held voice actually adopted the edit. Include presets/cycle, multiple Filter stage kinds, Script Envelope, and Operator Minimum/Maximum; distinguish “UI publishes” from “held voice adopts.” Mark each cell *verified, intentionally retained, or red-first failing* with exact test and device observations.
- [x] **C3 — Compound/nested migration probes (likely highest functional risk).** Use four focused repeatable fixtures: (a) Oscillator waveform/range and Node/Script Envelope edits **inside** a sounding Multi with a Filter or Source sibling; (b) SourceId change **inside** Multi and independently within a referenced child Synth, including held siblings; (c) simultaneous upstream Source/Filter controls and downstream Operator changes through a mixed multi-stage Filter; (d) Filter with connected FM cutoff plus an unconnected coefficient or prepared FIR change during parent Multi output smoothing. Demand no state replay, PCM allocation, missing audible transition, or partial-graph commit. Implement only demonstrated violations; preserve existing invariants. **Disposition:** C3's named deterministic fixtures and compound migration contracts are automated-verified as of 2026-11-14; physical acceptance remains C6 and active interrupted/releasing behavior remains C4.
- [x] **C4 — Interruption and release/EOF closure (medium/high).** Exercise edit-before-fade-end, latest-wins preparation cancellation, simultaneous multiple held/latched notes, Note Off during the Source fade, active Node Envelope releases, short Sample EOF plus Filter tail, NNA-displaced and scoped virtual notes. If a candidate must stay old, it must keep sounding correctly until retirement and show the correct older-audio status. Include event order and nested child Source notes; do not invent a new release clock.
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

## C4(g) — in-flight and ready nested Multi workers versus definitive incompatibility and scoped NNA cancellation (2026-11-14)

**Automated PASS, no production DSP/session fix required.**
The two parameterized cases of
`FmSynthNnaVirtualLifecycleTests.InFlightNestedMultiWorkersCannotResurrectCancelledScopedNnaVoices`
exercise **real detached work** for four independently sounding
parent-owned Synth invocations. The 1,000-Hz fixture uses a nested
`Constant(0.2) → Multi(1)` child beneath a parent Source and
OnePole Filter, with a second child revision
`Constant(0.6) → Multi(4)` requiring three prepared children
**per old note**. Physical channel **0**, scoped virtual channel 7
owned independently by **101** and **202**, and a displaced
`NewNoteAction.Continue` predecessor in owner 101's NNA pool
contribute **four old voices**. The parent Filter histories are
per-voice; the test uses an independently unchanged session as
the frame-by-frame PCM oracle.

Both cases offer the compatible-but-not-yet-prepared nested
expansion to all four voices; older-audio count remains **0**,
because pending work is not definitive refusal.

- **In-flight variant (`False`):** A deterministic off-PCM
  `AfterMultiConstituentPrepared` gate holds the first worker
  **after constructing a real child**, while the other three
  voice-specific mailboxes are queued. The test then publishes
  a different **structurally incompatible parent** and cancels
  owner 101 before allowing the worker to finish.
  All **four** obsolete queued/in-flight mailbox jobs are
  accounted as **cancelled**, no obsolete child preparation is
  counted as complete, and no abandoned arrays become audible.
- **Ready-but-unadopted variant (`True`):** All **four**
  obsolete worker results finish preparation **before** the
  incompatible revision/cancellation, but have not been
  consumed by the PCM owner. Their ready arrays are invalidated
  and cannot publish after the structural edit or bring back
  a cancelled owner.

In **both** variants, definitive incompatibility changes the
older-audio count **0 → 4** while the original parents continue
sounding; `CancelScopedVoices(101)` retires that owner's
current and NNA predecessor **4 → 2** without affecting
owner 202 or physical 0. Explicit `AdoptPreparedFirControls`
calls before and after worker completion cannot revive either
cancelled note or replace the two retained old Synths.
A frame-by-frame oracle verifies unchanged audio across
the pending and post-cancellation windows. A newly started
virtual-99 note uses the *current* structural graph:
the two older notes remain at **0.2** each alongside the new
**0.8** note, for settled mixed PCM **1.2**. Explicit owner-202
and physical cuts reduce the sticky old count **2 → 1 → 0**;
the new note remains audible at **0.8**. The worker reports no
fault; the finally block always releases its deterministic
gate, including on failed test assertions.

[Test-only commit `c0c1c05`](https://github.com/logiclrd/Heresy/commit/c0c1c0580deeccfe709e1ee38012ff78e01c82fb)
passed [exact-commit CI #43642579808](https://github.com/logiclrd/Heresy/actions/runs/43642579808):
**3,739/3,739 tests**, Core 910, Render.File 29,
Render **1,189**, Scripting 105, Playback 356 and UI 1,150.
Release/native host, Windows apphost icon, versioned release-bundle
and Linux ARM64 shared codecs were all green. This adds
**two** deterministic worker-order cases over the 3,737-test
C4(f) baseline. There is no actual device-CPU deadline or
human sub-100-ms auditory claim.

**C4 remains OPEN.** This bounds the stale *nested Multi*
worker and scoped NNA cancellation interactions; it does not
demonstrate every source-FIR worker combination, release
Envelope changes while already releasing, or repeated
**SourceId changes during an active Source-local 100-ms fade**.
The last is a distinct unresolved eligible C1/C4 policy
boundary. C5 still owns saturated worker queues, real callback
deadlines and overload recovery; C2 owns the inspector-to-PCM
property matrix. C2, C4–C7 and the overarching
all-nonstructural Synth TODO stay unchecked and PR #12 stays
draft/unmerged. Next bounded C4 target: interrupted Source-local
Sample transitions, including a later conflicting SourceId,
release/EOF and older-generation accounting, without inventing
fake historical Sample playheads.

## C4(h) — repeated Sample SourceId edits during an active Source-local fade (2026-11-14)

**RED first → compatible live migration FIXED.** A running note's
already-audible Sample crossfade was originally excluded by
`NeedsPreparedSampleReplacement`: a second otherwise eligible
`SourceId` edit immediately marked the old note as
`Playback uses older audio`, leaving it on the preceding Sample
generation until retirement. The new
`FmSynthNnaVirtualLifecycleTests.RepeatedSampleSourceEditsDuringLiveFadeDeferAndAdoptOnlyLatestAfterCompletion`
test first failed exactly at that invariant: expected old-note count
**0**, actual **1**, with no unrelated compilation/setup failure.
[Test commit `6d7af89`](https://github.com/logiclrd/Heresy/commit/6d7af892f95ef301f8911709e99c27d6a2120c7a)
recorded this regression before production changes.

**Explicit bounded policy:** Do **not** rewrite an active old/new
Sample's timeline, freeze a made-up historical mix, or form an
unbounded three-/four-way fade. An otherwise preflight-compatible
second Sample selection is held as *one deferred latest revision*
on that particular voice until its current 100-ms Source-local
fade has fully ended. Further inspector edits replace that pending
intent, latest-wins, without constructing the superseded Sample
invocation. Pending is **not** a missed revision and does **not**
set sticky older-audio currency. The next normal
`AdoptPreparedFirControls` / `QueuePendingFirControls` sequence
repreflights and prepares the latest Sample entirely off PCM,
then attaches its new invocation at the actual current frame,
starting another ordinary **100-ms smoothstep**. The first
fade is never shortened; a new note always selects the newest
authored Sample immediately. The worker may introduce a small
additional post-fade wait, but does not block the PCM callback.

This is implemented by
`FmSynthSound.CanDeferSampleReplacementUntilFadeCompletes`,
a narrow `allowActiveSampleFade` flag **only for deferred
admission preflight** (not for direct commit/worker construction),
and `PlaybackVoice`'s existing single
`_pendingLiveSynth` latest-wins slot:
[admission commit `4a345ba`](https://github.com/logiclrd/Heresy/commit/4a345baa745069dbd3d7083837d2b33cf73c71a5)
and [voice commit `8d2d39b`](https://github.com/logiclrd/Heresy/commit/8d2d39bbaeed426b72a8d29f258591829ea91667).
A truly incompatible revision still sets older currency;
edits involving a changed Source frequency/mode basis or an
already-releasing note remain subject to their existing refusal
rules. There is no new worker, allocation or third Sample history
on PCM while the first fade is active.

**End-to-end audible PCM contract at 1,000 Hz:** the original
physical note has Sample A at **0.2**. At frame **10**, A→B
(**0.6**) starts its 100-frame fade. At frame **20** the editor
selects C (**0.8**), and at frame **30** D (**0.4**)
supersedes C, both before the first fade completes.
Older count remains **0**. A fresh virtual-99 note started at
frame 30 uses **D** immediately. The original note remains
on the undisturbed A→B clock through frame **110**:
mixed PCM at frame **60** is **0.8** (A/B halfway + fresh D)
and at frame **110** is **1.0** (B + fresh D).
Only after that is D prepared off-thread: the retained note
starts a B→D fade at frame **111**, with mixed PCM **1.0**
at frame 111, **0.9** at frame **161**, and **0.8** at
frame **211**. The worker completes exactly **two**
Sample replacements (B, D): the superseded C is never
constructed. The old physical note's later Cut leaves the
fresh D note at **0.4**, no stale note or warning.
The test asserts worker fault-free publication and exact
transition values throughout; no audio renderer or Filter
history has been artificially replayed.

[Implementation CI #43644074525](https://github.com/logiclrd/Heresy/actions/runs/43644074525)
passes the Release test-suite step, including the new PCM
regression (previous baseline **3,739** tests; now **3,740**).
Linux ARM64, native desktop host and Windows apphost checks also
passed; exact final packaging status is checked separately.
This is automated PCM acceptance, not a claim of physical
device listening or missed hardware deadlines.

**C4 stays OPEN.** This slice proves direct Sample
SourceId latest-wins during an existing finite 100-ms fade,
not all nested/Multi instances, short incoming EOF after a
deferred supersession, release/Note Off *while deferral is
outstanding*, shared-filter/FIR combinations, or
SourceId-plus-frequency combined edits. Those are distinct
remaining C1/C2/C4 contracts; C5 covers saturated workers
and actual realtime callback deadlines. C2 and C4–C7 and
the main all-nonstructural-properties TODO remain unchecked;
PR #12 remains draft and unmerged.

## C4(i) — deferred Sample revision at Note Off and short-Sample EOF, with independent sibling (2026-11-14)

**Automated PASS — one bounded production fix; three new tests.**
The first
[`9b2804d`](https://github.com/logiclrd/Heresy/commit/9b2804d4a4d5467a466d858056cdc96ec4f4eb32)
test commit used red-first acceptance cases. The genuine
failure was in
`FmSynthNnaVirtualLifecycleTests.NoteOffDuringDeferredSampleEditRetainsOriginalReleaseAndMarksOldImmediately`:
**the audible note's older count remained 0 after Note Off,
although the pending new Sample onset had become definitively
ineligible**. A later off-thread publication poll could eventually
notice refusal, but the immediate session status was incorrect.
The exact red run
[CI #43645014704](https://github.com/logiclrd/Heresy/actions/runs/43645014704)
reported expected older count 1, actual 0.

**Policy and fix:** `PlaybackVoice.ApplyNoteOff` checks the
single compatible Sample replacement waiting behind an active
Source-local fade **before** storing the first Note Off timestamp.
That pending intent is invalidated at the actual release event,
the old sounding voice receives its sticky older-audio mark,
and neither the old A→B fade nor its original release/Filter
state is restarted. No C onset or replacement worker is created.
Only the old voice's eventual retirement clears the warning.
[Production commit `6c59596`](https://github.com/logiclrd/Heresy/commit/6c59596a6e09ccf62096b2efc4f9ba3fe58ba8e1).

The first regression uses **1,000 Hz** Sample A=0.2 and
B=0.6, with A→B starting at frame **10**, and deferred
C=0.9 requested at frame **20**. Note Off at frame **30**
starts the parent Synth's **160-frame** sustain-release
Envelope. A reference session with the same A→B fade
but no deferred C supplies a *frame-by-frame PCM oracle*
through release and the 3-ms parent OnePole Filter tail.
Older count changes **0 → 1 immediately at Off**,
the original Note Off remains frame 30 even after a repeated
Off, and old audio continues on **B**, not C.
After original release and Filter retirement the old
count becomes **0**. A separately started virtual-99
note already uses the new C definition throughout. No
third Sample worker job is enqueued.

**Natural EOF is conditional on the enclosing note's life.**
The first EOF test initially assumed a direct Sample's
Filter-tail budget must keep its physical voice alive *after*
the completed fade even when the incoming terminal Sample
was already at natural EOF. That overstates the existing
Source EOF contract. An attempted blanket EOF-eligibility
guard and an extended completed-fade end conflicted with
two **preexisting desirable regressions**:
`FilteredAddMixerCanReviveSourceAfterShortSampleEofWithoutResettingOnePole`
(legitimate branch revival while a sibling continues)
and `ShortReplacementSampleEofCannotEraseAudibleOutgoingFade`
(no artificial extension of a direct Source's ended audio).
Those experimental changes were **reverted in full** via
[commit `23381ee`](https://github.com/logiclrd/Heresy/commit/23381eea8be6b1b0effb04f3e6fb60701d85ac13).
The final `FmSynthSound.cs` retains its prior C4(h) contents:
there is **no new blanket ban on expired branches and no
new retroactive Source end extension**.

The corrected
`DeferredSampleEditCannotRestartExpiredIncomingSampleFromFilterTail`
proves that when there is **no continuing sibling**,
B's 15-frame readhead (begun at frame 10) ends at frame **25**
while A still fades out to frame **110**. Deferred C requested
at frame 30 cannot recreate the already-retired physical note
after that fade: it makes **no replacement worker**, does
not leave an older-audio warning on a nonexistent note, and
a fresh virtual-99 note correctly uses C.

The third regression,
`DeferredReplacementMayReviveFinishedSampleWhileSiblingKeepsNoteActive`,
covers the opposite eligible case. The first A→short-B
crossfade again ends at frame 110; however an independent
Sample sibling at **0.1** keeps the Add→OnePole parent
note genuinely active. The deferred C is then prepared
off-thread and adopted at frame **111**, without rewinding
the sibling or shared IIR history. The second 100-ms
fade yields filtered mix approximately **0.1** initially,
**0.55** halfway (C contributes 0.45), and **1.0**
at the end. Older-audio count remains 0. This preserves
the existing independent-branch revival contract without
treating a completed, wholly retired note as still active.

The corrected/expanded test commit is
[`c79d7ce`](https://github.com/logiclrd/Heresy/commit/c79d7ce6cd7745bff1d75fa83338b40b7ed8b9d2);
[exact-commit CI #43645558819](https://github.com/logiclrd/Heresy/actions/runs/43645558819)
is **SUCCESS — 3,743/3,743 tests**, including all three
explicit C4(i) tests: Core 910, Render.File 29,
Render **1,193**, Scripting 105, Playback 356 and UI 1,150.
Release/native host, Windows apphost icon, versioned
release bundle and Linux ARM64 shared-codec checks
all passed. These are deterministic automated PCM and
currency/lifecycle claims, not a physical listening test.

**C4 remains OPEN.** This closes one narrowly defined
Note Off/EOF deferral boundary, but **not** every active
release-Envelope edit, SourceId + simultaneous Source-frequency
basis change, `None` ↔ Source selection, every nested
Filter/Instrument combination, or C5 hardware overload and
worker-queue deadlines. C2/C4–C7 and the overarching Synth
all-properties TODO remain unchecked; PR #12 stays draft,
unmerged, pending user-approved sign-off.

## C4(j) — Sample SourceId plus frequency/mode basis in one live revision (2026-11-14)

**RED first, then FIXED.** The initial
`FmSynthLiveSourceIdTests.ChangedSampleAndFrequencyBasisCrossfadeAtIndependentReadheadsBeforeOnePole`
test was introduced in
[`dab0d4c`](https://github.com/logiclrd/Heresy/commit/dab0d4cfeafb4c7dfc63e9952d869829bdf928b9):
[red CI #43651938547](https://github.com/logiclrd/Heresy/actions/runs/43651938547)
failed at `original.CanPrepareMultiExpansion(revised, held)`,
expected **true**, actual **false**. The old
`NeedsPreparedSampleReplacement` had conservatively refused
a changed Sample `SourceId` coupled with `FrequencyHz`,
`Constant` or `ExponentialMultiplier` changes. This was a
legitimate *authoring-nonstructural* gap, not a permitted
graph-wiring/topology refusal.

**Bounded production fix:** The detached worker already
constructs the incoming Sample invocation from the **new**
`FrequencyHz` and Constant-mode pitch/speed basis. The
missing piece was keeping the outgoing readhead on the
**previous** Source node's authored frequency/modulation
controls during the 100-ms Source-local smoothstep:
otherwise simply removing admission guards causes
`EvaluateOneSource` to retune the old Source immediately
from the new graph definition. In
[`e7c43e0`](https://github.com/logiclrd/Heresy/commit/e7c43e07b7706dd3a468e90e037ab2387ac72577),
`SourceVoice` retains an immutable
`OutgoingSourceDefinition` reference when the prepared
crossfade attaches. `EvaluateSource` uses that frozen node
*only* when rendering the previous outgoing Sample; the
incoming Source uses the new graph node. Both Sample
readheads, their prior `FmSourcePitchCurve` integrations
and any downstream Filter histories remain independent.
The Source frequency/Constant/exponential equality guards
were removed **only for eligible Sample replacement**;
the original Sample's modulation wiring, structural
preflight, readiness and lifecycle rules still apply.
The PCM commit adds only reference assignments, no
Sample construction, thread wait or managed allocation.

**Independent PCM oracle and real-value pitch checks:**
At 48 kHz, Sample A has a varying 20,000-frame waveform.
The old Source has its original C4-frequency basis with
a connected **1.25× linear-rate control**; Source B has
a different varying 20,000-frame waveform, selected with
a revised **2× C4 frequency basis** at frame **128**.
Four parameterized cases cover every pair of revised
`Constant` and `ExponentialMultiplier` flags. The
two raw Samples are rendered separately at **their own
authored clock, pitch and mode**; their frames are mixed
under the exact 4,800-frame smoothstep and baked into an
independent **unity-rate** Sample. This reference passes
through a single uninterrupted OnePole, and the actual
live-edited parent is compared frame by frame with
tolerance 3e-5 over the complete fade and continuation,
with **zero managed PCM allocations**. The first frame
must remain the old trajectory, the middle genuinely
mixes distinct positions, and the finished output must
follow the new independent onset. A preparatory version
of the expanded oracle mistakenly applied the 1.25×
rate to the **already baked** PCM a second time;
[`23172e6`](https://github.com/logiclrd/Heresy/commit/23172e6b78ecd025b777b447e9ce14a7e0b4eac6)
corrects this reference instead of weakening PCM
tolerances or changing production DSP.

Two historical **refusal** tests were deliberately
rewritten as *positive atomic-adoption* regressions:
`AddMixerAdoptsTwoSampleReplacementsWithSecondFrequencyBasisChangedAtomically`
and
`FilteredAddMixerAdoptsSourceIdAndFrequencyTogetherWithoutResettingOnePole`.
In both, the second selected Sample changes identity and
doubles its frequency while the first selected Sample
also changes identity. Both must be prepared before the
whole graph commits, preserving a common source-local
100-ms A/B→C/D transition from **0.28 to 0.66**.
The filtered case compares every resulting PCM frame
with a separately baked mix through the **same single
continuous OnePole**, rather than resetting its history.
This tests atomicity under the formerly refused condition,
not partial acceptance. The test updates were committed in
[`087529a`](https://github.com/logiclrd/Heresy/commit/087529a3a0de01edef3f79780dad62016036d7f6);
the four-mode test and its clarifying text in
[`a218a20`](https://github.com/logiclrd/Heresy/commit/a218a20bbddc6b10679cc7fce5492ba66c61ad08)
and
[`be64d4d`](https://github.com/logiclrd/Heresy/commit/be64d4d7174347e97ca35598dd981ed88a1a57c3).

The corrected [CI #43652211609](https://github.com/logiclrd/Heresy/actions/runs/43652211609)
passes its full Release test-suite step and Linux ARM64
validation, including all four waveform/mode oracles
and both renamed Add/OnePole atomic tests
(**3,747 total tests** against the 3,743 C4(i) baseline).
The earlier ordinary-basis implementation run
[CI #43652029089](https://github.com/logiclrd/Heresy/actions/runs/43652029089)
also passed full CI. This is a deterministic offline
PCM regression, not C6 physical listening or C5
hardware realtime deadline measurement.

**C4 remains OPEN.** This establishes the direct
Sample SourceId + scalar basis combination with
independent pitch histories, not every nested Multi/
Instrument case, editing multiple Source bases while
another fade is *deferred*, late Note Off/EOF with
this exact combined update, `None` ↔ assigned
Sample, or arbitrary unrelated sound-kind
substitution. C2 UI inspector-to-PCM verification,
C5 stress, C6 device listening, C7 signoff and the
overarching Synth live-edit TODO remain open.
PR #12 stays draft/unmerged pending the user's approval.

## C4(k) — Source selected None ↔ Sample, source-local fade and retirement (2026-11-14)

**RED first, then FIXED.** Test-only
[`bdbc114`](https://github.com/logiclrd/Heresy/commit/bdbc1144e29b15f81f42ca0e23c7dda961eaa223)
introduced the two fundamental `FmSynthLiveSourceIdTests` cases
`UnassignedSourceAdoptsWorkerPreparedSampleAtCurrentAgeBeforeSharedOnePole`
and
`SampleToUnassignedSourceFadesOutWithoutResettingSiblingOrSharedOnePole`.
[CI #43656023812](https://github.com/logiclrd/Heresy/actions/runs/43656023812)
failed **exactly these two** new tests in `CanPrepareMultiExpansion`
on the old compiled implementation. The existing Source preflight required a
non-null Sample `SourceVoice` and refused a different SourceId targeting
no resolved Sample. These failures arose at admission, not from a malformed
fixture or an independently observed audible DSP discontinuity.

**Bounded implementation:**
[`8f0b1cc`](https://github.com/logiclrd/Heresy/commit/8f0b1cc8748c9c3bbe21675fa84e3a31feebc682)
extends the established full-graph preflight for precisely eligible
`FmSoundSourceNode` `None↔Sample` edits with unchanged wiring and an
active, unreleased parent. A newly assigned Sample is created by the
existing versioned **off-PCM** `MultiExpansionMailbox`, retains its own
fresh local age/pitch basis from the actual commit frame, and uses a
**100-ms Source-local silence→Sample smoothstep**. A Sample removed from
the selection retains its **real** invocation, frozen outgoing authored
Source pitch controls and forward-integrated readhead while performing
a worker-free **100-ms Sample→silence fade**. It is then reclaimed from
the Source slot, with its known natural EOF or fade endpoint bounding
the child lifetime; a shared Filter keeps its independent IIR history
and explicitly authored tail. No fabricated eternal silent Sample,
Sample decoder, worker job, Filter reconstruction or managed allocation
was added to PCM. A new Note On uses its revised selected Source
immediately, without the held voice's audition fade. Off/releasing notes
cannot begin the newly assigned Sample onset. The implementation
follow-up
[`b842ff3`](https://github.com/logiclrd/Heresy/commit/b842ff3e6c4dc68e6aefcbc089f09a7ccbc53273)
corrected a C# **CS0136 local-variable shadowing** build error in the
new end-accounting block; that first build failure was a syntax error,
**not** a failed DSP assertion.

**Independent PCM/lifetime evidence:** The two original regressions
render a held Synth with an unassigned/assigned Source branch
plus a *continuing sibling Sample*, both under Add→OnePole. They bake
the expected Source-only 4,800-frame smoothstep into an independently
rendered Sample and compare *every frame* after one uninterrupted
OnePole, using tolerance **3e-5** and **zero managed PCM allocations**.
The source-only change never rewinds sibling audio or restarts the
Filter. Fresh-note behavior is separately checked. Additional tests
[`d410abe`](https://github.com/logiclrd/Heresy/commit/d410abe4426a563a9c57e2c6a35b4be84ab30df7)
cover a **48-frame newly assigned Sample** reaching EOF at its fresh
onset age while the sibling stays audible, and refusal to create a
new Sample after `Indefinite` Note Off. A fifth test in
[`c66ba19`](https://github.com/logiclrd/Heresy/commit/c66ba193a9e05f562fded0c7c9d0834ebbf127c1)
removes the **only** Sample in a Synth→OnePole path, asserts its
exclusive end at edit frame 128 + fade 4,800 + Filter tail 144,
and checks every filtered PCM frame through retirement and after
the hard tail cutoff. These are bounded acceptance fixtures, not
proof of every simultaneous, nested or interrupted selection.

At the C4(k) implementation run,
[CI #43656165366](https://github.com/logiclrd/Heresy/actions/runs/43656165366)
passed its Release test-suite step and Linux ARM64 job. The expanded
five-test head is
[CI #43656210320](https://github.com/logiclrd/Heresy/actions/runs/43656210320).
The final full-workflow conclusions and exact documentation HEAD must
still be checked independently.

**C4 remains OPEN.** This tests first-order silence/Source
selection and limited Note Off/EOF/tail behavior. The immediately
following C4(l) addresses **deferred retargeting and Note Off during
active silence fades**, but does not certify all scoped NNA
currency/status paths or nested Instrument/Filter release histories.
C2 inspector-to-PCM coverage, C5 callback/overload stress,
C6 physical listening and C7 final sign-off remain open. Do not
check the main all-properties TODO or merge/undraft PR #12.

## C4(l) — interrupted silence Source fades, latest-only intent and Note Off currency (2026-11-14)

**RED first, then FIXED; bounded automated acceptance.** The initial
test-only [`cc70742`](https://github.com/logiclrd/Heresy/commit/cc70742779691f45dd3434481acd944e9f056421)
added parameterized `FmSynthNnaVirtualLifecycleTests.SilenceTransitionSupersessionDefersOnlyLatestWithoutStaleCurrency(true/false)`.
At 1 kHz and a live sibling Source, either `None→Sample` or
`Sample→None` started at frame 10, followed by Source edits at frames
20 and 30. In [red CI #43659357090](https://github.com/logiclrd/Heresy/actions/runs/43659357090),
**both** new tests failed at the immediate older-audio count:
expected **0**, actual **1**. The older renderer only recognized
`SourceVoice.FadeFrom` (Sample→Sample) as a deferrable active fade,
rather than the new silence-onset/fade-out states; these were genuine
policy/admission failures. Expanded test-only
[`d16597c`](https://github.com/logiclrd/Heresy/commit/d16597c2e12972976316c8ec15e2dc70800cde5c)
added `NoteOffDuringDeferredSilenceTransitionCancelsOnsetAndPreservesRelease(true/false)`.
[Red CI #43659398999](https://github.com/logiclrd/Heresy/actions/runs/43659398999)
failed exactly **four** new parameterized cases at the same premature
older-audio warning; the existing 3,752 tests passed.

**Production correction**
[`6b24bed`](https://github.com/logiclrd/Heresy/commit/6b24bed15a3ab7f2680bc599e567eece48f7d112):
`CanDeferSampleReplacementUntilFadeCompletes` now recognizes
`FadingInFromSilence` and `FadingOutToSilenceDefinition` alongside
`FadeFrom`. The whole-graph `CanAdoptRunningState` preflight checks
whether the *future* Source change would be eligible after the existing
transition, while the ordinary immediate-commit gate still refuses
creating a second transition during that fade. Existing
`PlaybackVoice._pendingLiveSynth` keeps **one latest-wins intent**,
and prepares any new Sample on the existing off-PCM worker **only
after** the original 100-ms fade finishes. Pending work does not mark
an older generation. If Note Off occurs first, the existing
`PlaybackVoice.ApplyNoteOff` immediate cancellation marks the
genuinely missed revision sticky until the old sounding voice retires,
without restarting release, modifying the first fade, or constructing
the pending Sample. Importantly, the old `Sample` retained only as the
outgoing half of `Sample→None` cannot satisfy a new `sameSample`
identity-alias adoption: reselecting even that *same* Sample object
after `None` requires a freshly prepared onset/readhead, not an
aborted retirement or a historical readhead reset. No new PCM-time
construction or allocation was introduced.

**PCM and lifetime evidence:** Both latest-only parameterized cases
use physical and fresh virtual notes with independent 0.1 Sample
siblings, plus actual old and new Source levels. They verify each
frame of the first unmodified 100-frame silence fade, from frame 30
through its original frame-110 deadline, and a second
**worker-prepared latest-only** fade from frame 111 to 211.
There is no obsolete middle-Sample onset and the newer note sounds
immediately using its current authored Source. First worker
completions remain **one** for `None→Sample` and **zero** for
`Sample→None`; only the latest successor adds a subsequent
preparation. Both pending and adopted phases report **zero older
voices**. The new Note Off tests independently compare actual PCM
to an unrevised oracle through the uninterrupted original Source
transition, 160-frame sustain release, and shared OnePole tail;
they assert **0→1 at frame-30 Off**, repeated Off preserves its
first origin, then **1→0 on actual voice retirement**. Newest virtual
voices continue without inheriting the held note's old state.

A separate
[`ReselectSameSampleAfterNoneFadeRequiresFreshIndependentOnset`](https://github.com/logiclrd/Heresy/blob/feat/synth-live-parameter-foundation/Heresy.Tests.Render/FmSynthLiveSourceIdTests.cs)
regression (test commits
[`6a93f86`](https://github.com/logiclrd/Heresy/commit/6a93f8671b1d1cf97c7f94853a915395b9dcbdbc),
[`4dcec02`](https://github.com/logiclrd/Heresy/commit/4dcec02d4734e19a459328ac7f7483bfc95cee58))
uses a *time-varying* Sample to distinguish a newly selected onset
from the outgoing Sample's old integrated readhead, including
**zero managed PCM allocations** during the measured new fade.
The first corrected implementation runs had only test-oracle mistakes:
[`8aab635`](https://github.com/logiclrd/Heresy/commit/8aab635a775cb5d0d239fc28aec3001bc3f44715)
includes the **fresh note's own 0.1 sibling** in expected mixed
PCM; [`e959c12`](https://github.com/logiclrd/Heresy/commit/e959c1291e3fc87703794013ab063cf93028baef)
checks exact newest-only PCM **after**, not four frames into,
the existing authored Note Cut anti-click tail. Neither required
weakening the actual two 100-ms PCM comparisons or a production
audio change.

[**CI #43659821822**](https://github.com/logiclrd/Heresy/actions/runs/43659821822)
is **SUCCESS** at implementation/test HEAD `e959c1291e3fc87703794013ab063cf93028baef`:
**3,757/3,757** tests (Core 910, Render.File 29, Render
1,207, Scripting 105, Playback 356, UI 1,150), plus
Release/native host, Windows apphost resources,
versioned release-bundle smoke and Linux ARM64 shared-codec publishing.
This is *automated* interrupted Source acceptance, not C6 real-device
audition or C5 callback deadline certification.

**C4 remains OPEN; finite residual inventory after C4(l).**
The next distinct candidates are (1) **deferred SourceId plus
simultaneous frequency/Constant/exponential basis changes**, to
verify independent outgoing/incoming pitch trajectories when a prior
fade must finish first; (2) the **approved refusal** of a changed
audible Node Envelope while already releasing, verifying original
release PCM/end and sticky older currency until retirement;
and (3) only a specifically motivated outstanding nested Source/
Filter/NNA lifecycle case not already represented by C4(b)–(g)
and C4(i). Do **not** expand this into a Cartesian product.
C2 retains UI property-to-PCM matrix ownership, C5 actual
deadline/overload stress, C6 human auditions, C7 final sign-off.
The main all-properties TODO stays unchecked; PR #12 stays
draft/unmerged pending user authorization.

## C4(m) — deferred combined Sample SourceId, frequency and mode edits (2026-11-14)

**GREEN integration regression; no production change.** The C4(h)
latest-only pending SourceId policy and C4(j) independent outgoing/
incoming frequency-basis preservation were individually verified, but
not previously exercised *together* when another authored edit changes
both the Sample and its Source's FrequencyHz/Constant/exponential mode
while an earlier Sample crossfade is still audible.

Added `FmSynthNnaVirtualLifecycleTests.DeferredSampleAndFrequencyModeRevisionKeepsBothPitchHistoriesBeforeOnePole`
in [`9871116`](https://github.com/logiclrd/Heresy/commit/987111665bbbe970db38a5e6f1b72bf5d3e0ac64),
with all **four** combinations of the latest Source's `Constant`
and `ExponentialMultiplier` flags. The test uses
**1,000 Hz** and a **100-frame/100-ms** smoothstep:
original A (frequency basis ×1.4, exponential rate modulation) sounds
at note start; B (basis ×0.75, linear modulation) is worker-prepared
and crossfaded from frame **10** through frame **110**;
at frame **20** a third C is requested with a different Sample and
frequency/mode; at frame **30** it is superseded by D (basis ×2,
one of the four Constant/exponential combinations). The note retains
the genuine B revision and correct original A→B pitch trajectories
until that first fade finishes. C is never constructed for the held
voice; the already-existing latest-wins pending slot retains D
without a sticky older-generation mark. After the original fade ends
the worker prepares **D alone** and the second crossfade starts at
frame **111**. Total prepared Source replacements are **exactly two**
(B and D), not three.

**Independent oracle:** A, B and D are separately rendered as
Source-only `FmSynthSound` invocations, each with distinct
nonconstant Sample data, original frequency basis, and authored rate
mode; each incoming Source starts with its *own* local Sample playhead
at its actual publication frame. Only after constructing the two
100-frame smoothstep transitions is the independently sounding 0.1
sibling added. The complete 240-frame authored PCM mix is then passed
through a separately instantiated **continuously running OnePole
Filter**. Every live-session output frame is compared against this
filtered reference at **3e-5** tolerance, spanning original
A→B history, deferred intent, second B→D transition, latest scalar
basis and mode, and post-fade steady state. The test separately
asserts that older-audio count remains zero at both deferral and
adoption boundaries and that the old physical voice actually
publishes D only when the second worker result becomes available.

The initial test-only
[CI #43661450756](https://github.com/logiclrd/Heresy/actions/runs/43661450756)
failed **before the new Render test ran** due to CS0219, an unused
`secondFadeEnd` constant under warnings-as-errors. This was a
fixture compilation oversight, **not** red DSP evidence; it was
corrected in
[`6c7279e`](https://github.com/logiclrd/Heresy/commit/6c7279ed4ec1546787fae9a691633ba1bb96e15e).
At this exact test HEAD,
[CI #43661605124](https://github.com/logiclrd/Heresy/actions/runs/43661605124)
completed **SUCCESS** including **3,761/3,761 tests**
(Core 910, Render.File 29, Render 1,211, Scripting 105,
Playback 356, UI 1,150), native desktop Release host,
Windows apphost icon/resources, versioned release-bundle smoke
and Linux ARM64 shared-codec gates. The implementation was already
correct for this named interaction: **no production files changed**.
This is automated PCM evidence, not a physical-device listening
test, a callback-overload stress proof, or a universal assertion
about simultaneous updates to an *upstream modulation Constant
node's own value* (which is different from the Source `Constant`
mode).

**C4 remains OPEN after C4(m).** The next distinct bounded risk is
the specified **audible Node Envelope edit arriving while a note is
already releasing**: prove rejection is atomic (retain original
release PCM and its Filter tail), sticky older currency survives
otherwise-compatible later edits, and retirement clears the count.
Only an independently evidenced nested Source/Filter/NNA lifecycle
gap should justify further C4 variants, rather than an unbounded
matrix. C2 inspector-property-to-PCM audit, C5 physical
callback/CPU-load stress, C6 device listening and C7 final signoff
remain separate and open. PR #12 remains draft/unmerged, with
C4 and the overarching all-properties TODO unchecked.

## C4(n) — audible Node Envelope edit after Note Off, atomic refusal and old-voice retirement (2026-11-14)

**GREEN bounded release/PCM regression; no production change.**
The renderer's whole-graph preflight in
`FmSynthSound.CanAdoptRunningState` already declines a changed
**audible** frozen `NodeEnvelopeCurve` once the held Synth's
`SoundState.HasNoteOffTime` is true, where its bounded audible
output path can be checked. This is not a general ban on otherwise
compatible scalar updates during a release. The remaining risk was
whether that refusal really preserved *every* original node and
Filter state, and whether the session's old-generation warning was
sticky but retired at the old envelope's correct end.

Added parameterized
`FmSynthNnaVirtualLifecycleTests.ReleasingAudibleNodeEnvelopeEditRefusesWholeGraphAndRetainsStickyCurrency`
in [`fafd738`](https://github.com/logiclrd/Heresy/commit/fafd7389618f5bfe207155631be6486195dbbdd8).
The original graph is **Constant (0.4) × audible Node Envelope
(40-ms final point; one-node sustain) → OnePole at 240 Hz with a
4-ms authored tail**. Its physical note begins at frame 0
(1,000 Hz session), enters actual Note Off/release at **frame 12**,
and is rendered through frame 16 before the edit. The two cases
compile an alternative same-`EnvelopeId` curve with a final point
at **5 ms** or **85 ms**; *simultaneously* the proposed graph changes
the Constant to **0.9** and Filter cutoff to **60 Hz** while
retaining the same graph wiring and stage plan.

For both cases, `CanPrepareMultiExpansion` must reject the
whole proposed revision. The existing physical `PlaybackVoice.Sound`
continues to be the **original compiled Synth**; it is marked as
using **older audio immediately**, its original one-shot Note Off
timestamp survives, and its `GetEndFrameExclusive` is unchanged.
No partial Constant update, envelope rewrite, filter coefficient
change, tail reset or new worker onset is permitted.

**Independent session/PCM evidence:** A second unchanged
`PlaybackSession` is rendered at *exactly the same frame boundaries*
as the edited session. Every PCM frame is compared at
**1e-5** tolerance through the refused edit, release,
uninterrupted OnePole tail, and final retirement. A later authored
successor to the **refused definition** cannot migrate a voice still
sounding the older predecessor, so sticky currency persists. The
test also explicitly publishes a separate **compatible Constant
edit against the actually sounding original generation** at frame
**22** (0.4→0.65 with the unchanged original frozen Envelope and
OnePole configuration). Both edited and reference sessions adopt
it, prove identical continuing PCM, and retain the original
release deadline and **same frame-12 Note Off**. Crucially,
`CountOlderAudioVoices()` **remains 1** even though this latter
independent migration succeeds: accepting a future edit cannot
erase an applicable earlier missed revision.

At **frame 24**, both sessions start a fresh current-generation
virtual note using a separate latest Synth; it must not acquire
the physical voice's older-audio status. The test compares PCM
and checks **one old voice** immediately before the original
release-plus-authored-Filter-tail deadline. Rendering across that
exclusive boundary retires the old physical note and changes
the older-audio count **1→0**, while the newly started virtual
note continues. The test derives this end from the *original*
prepared Synth and separately checks agreement with the
unchanged reference state; it does not substitute the revised
Envelope's shorter or longer end.

[CI #43662393967](https://github.com/logiclrd/Heresy/actions/runs/43662393967)
is **SUCCESS** at the C4(n) test-only HEAD `fafd7389618f5bfe207155631be6486195dbbdd8`.
**3,763/3,763 tests** passed: Core 910, Render.File 29,
Render 1,213 (two new parameterized cases), Scripting 105,
Playback 356 and UI 1,150. Release desktop host,
Windows apphost resources, versioned release-bundle smoke and
Linux ARM64 shared-codec publishing also passed.
**No production DSP code changed**; this is automated PCM and
retirement evidence, not C6 physical audition nor C5
deadline/underrun stress.

**C4(n) accepted; C4 remains OPEN only for a bounded final
closing audit.** That audit should compare C4(a)–(n) against
the original interruption/Note Off/EOF/NNA obligations,
identify only a concrete untested nested Source/Filter/NNA
risk if any, and either close C4 with evidence or scope one
last focused regression. Do **not** add an unbounded
combination matrix. C2 inspector-to-PCM property coverage,
C5 actual callback/overload stress, C6 device listening
and C7 final signoff are separate and remain open. Leave
the overarching all-properties TODO unchecked and PR #12
draft/unmerged without explicit user approval.

## C4 closing decision — bounded interruption and release/EOF acceptance (2026-11-14)

**C4 IS COMPLETE for the agreed bounded automated scope.**
This is a cross-slice **evidence audit**, not a new DSP algorithm,
a new physical audition, or a claim of exhaustive support for every
possible graph × revision × event order. The initial checklist C4
was to exercise: editing before a fade ends, latest-wins
preparation/cancellation, multiple held/latched voices, Note Off
during Source fades, releasing Node Envelopes, short Sample EOF
with a Filter tail, NNA-displaced and scoped virtual notes,
nested child Source event order, correct original lifetimes,
and truthful per-voice older-audio currency. All those named
contracts now have deterministic regression evidence across
C4(a)–(n), without identifying a new, independent concrete gap
during this closing pass.

| Original C4 obligation | Accepted evidence | Result / exact invariant |
| --- | --- | --- |
| Edit before 100-ms fade finishes, supersede prepared or pending onset | **C4(a)** `FmSynthMultiTests.SupersedingPreparedRephaseDuringActiveMultiOnsetFadePublishesOnlyLatest`; **C4(h)** `FmSynthNnaVirtualLifecycleTests.RepeatedSampleSourceEditsDuringLiveFadeDeferAndAdoptOnlyLatestAfterCompletion`; **C4(l)** `SilenceTransitionSupersessionDefersOnlyLatestWithoutStaleCurrency`; **C4(m)** `DeferredSampleAndFrequencyModeRevisionKeepsBothPitchHistoriesBeforeOnePole` | The existing audible transition runs to its original deadline. Only the latest future Source/Multi state is prepared or adopted, with no obsolete intermediate audio or premature older-status warning |
| Note Off during active Source or Multi fade and original release clocks | **C4(b)** `NoteOffDuringSampleReplacementKeepsBothReleaseContributionsAndFilterTail`; **C4(c)** `NoteOffDuringPreparedMultiOnsetFadePreservesBothOnePoleHistoriesAndRelease`; **C4(i)** `NoteOffDuringDeferredSampleEditRetainsOriginalReleaseAndMarksOldImmediately`; **C4(l)** `NoteOffDuringDeferredSilenceTransitionCancelsOnsetAndPreservesRelease` | Original Sustain/Note Off and outgoing/fading histories remain audible, repeated Off does not restart them, and deferred *new Sample onsets* become definitely missed at Note Off rather than reappearing later |
| Short Sample EOF and actual Filter-tail ownership | **C4(b)** Source fade and short EOF; **C4(i)** `DeferredSampleEditCannotRestartExpiredIncomingSampleFromFilterTail` and `DeferredReplacementMayReviveFinishedSampleWhileSiblingKeepsNoteActive`; **C4(k)** `ShortSampleAssignedFromNoneEofsAtNewLocalAgeWithoutSilencingSibling` and `RemovingOnlySampleBoundsNaturalNoteEndAndPreservesOnePoleTail` | An expired sole Source cannot resurrect a retired note. An expired Source branch with an independently sounding sibling may be safely replaced. Source-local fades, EOF and authored Filter tails end at their own deadlines |
| Several held/latched voices, scoped virtual channels and NNA-displaced predecessors | **C4(d)** `PhysicalScopedAndNnaVoicesRetainIndependentLiveRevisionCurrency`, `PendingSampleRevisionsAcrossScopedAndDisplacedVoicesHonorLatestOnly`; **C4(e)** `ReleasingScopedNnaAndPhysicalVoicesRetainIndependentCurrencyAndHostTails` (`Off` and `Fade`) | Independent voice state, old-generation mark, release/cut and scope cancellation; a forked *host* Filter tail is not falsely counted as a resurrected older note |
| Referenced child Synth publication, scoped ancestor release, in-flight/ready workers and cancellation | **C4(f)** `ScopedNestedSynthReleasesKeepChildRevisionsAndOriginalOwnerClocks`; **C4(g)** `InFlightNestedMultiWorkersCannotResurrectCancelledScopedNnaVoices` (in-flight and ready-unadopted variants) | Child-before-parent revision order, original nested release clocks and parent Filter histories; stale worker results cannot revive cancelled NNA/scoped voices or overwrite a later incompatible generation |
| Sample SourceId with independent pitch histories and unassigned selection | **C4(j)** `ChangedSampleAndFrequencyBasisCrossfadeAtIndependentReadheadsBeforeOnePole` (four frequency/mode cases); **C4(k)** fundamental `None↔Sample` PCM/EOF/tail cases; **C4(l)** same-Sample re-onset; **C4(m)** combined SourceId/frequency/mode deferral (four variants) | Distinct old and new forward-integrated playheads and frozen pitch settings; silence onset/removal without fabricated Sample, correct same-asset re-onset and shared OnePole continuity |
| Audible Node Envelope edit while release is active, whole-graph refusal and sticky currency | **C4(n)** `ReleasingAudibleNodeEnvelopeEditRefusesWholeGraphAndRetainsStickyCurrency` (5-ms and 85-ms replacement releases) | Reject changed audible frozen release atomically, including simultaneous Constant/Filter changes; preserve original PCM, Note Off frame and tail, retain older currency across later compatible adoption, clear exactly on true old-voice retirement |

**Last independent implementation/test evidence:** [C4(n) CI
#43662393967](https://github.com/logiclrd/Heresy/actions/runs/43662393967)
and [its exact audit-HEAD CI
#43662637451](https://github.com/logiclrd/Heresy/actions/runs/43662637451)
both **SUCCESS** with **3,763/3,763 tests**, full
Release/native-desktop/Windows-apphost/release-bundle smoke and
Linux ARM64 shared-codec validation. C4 was accepted after
matching the original finite list to the 14 completed slices;
**no extra DSP/test change was justified by this review**.
The present C4-checkbox-and-audit HEAD requires its own
independent CI verification, recorded separately.

**Why no further C4 slice:** Earlier section-level "C4 remains
OPEN" statements described the historical gap *at those
intermediate commits*. C4(e) now resolves C4(d)'s released NNA
and host-filter tails; C4(f)/(g) resolve nested ancestry,
already-releasing scope and worker races; C4(h)–(m) resolve
interrupted and combined Source replacements, short Sample EOF
and silence transitions; C4(n) resolves the previously named
changed audible release-Envelope refusal. The closing audit
did not uncover another **distinct, reproducible** nested
Source/Filter/NNA bug or obligation not already covered by
one of those cases. Arbitrary deeper nested combinations
cannot be treated as a finite completion prerequisite without
new failure evidence.

**Strict boundary:** The C4 checkbox does **not** imply
all nonstructural inspector properties have been audited,
that every possible Script/Instrument/Filter combination was
physically heard, or that saturated worker queues and real
callback deadlines are qualified. C2 owns the per-field
inspector→publication→PCM matrix, including Script Envelope,
equivalent-shape Filter definition selection, Operator Minimum/
Maximum and real inspector binding; C5 owns actual hardware
deadlines, worker saturation and the 3→17→3 Multi overload
recovery; C6 owns physical Linux/Windows listening and status
acceptance; C7 owns final signoff, overall TODO and any
merge-ready decision. The mutually agreed C1 deferrals
(unrelated/cross-family Instrument substitution and future
Pattern/Sequence Source types) remain explicitly deferred,
not falsely verified as compatible live adoption.
**C1, C3 and now C4 are checked; C2, C5, C6, C7 and the
overarching all-properties TODO remain unchecked.**
PR #12 remains **open, draft, unmerged** until explicit user
authorization.

### Adjacent UI issues deliberately outside PR #12 closure

Two independently actionable main-line TODOs were logged in [`docs/todo.md`](https://github.com/logiclrd/Heresy/blob/main/docs/todo.md) via [`dd55714`](https://github.com/logiclrd/Heresy/commit/dd55714d677ee8ee4641594e1f8ae2cb394039c3): stale **Configured stages** after editing a shared Filter definition and selected-stage controls hidden to the right in a non-resizable strip instead of below the list. `FmSynthEditorControl.AddFilterInspector` currently deliberately avoids rebuilding on popup close to preserve unsaved per-use drafts, and `FilterInstanceEditorControl` uses a left/right layout; the eventual fix must refresh safely without losing those drafts. These are real UI defects, but not evidence that a held note failed a realtime coefficient update.

### Exit criterion

PR #12 can be declared complete when (1) every *currently available* nonstructural field has a specified verified live path **or** a narrowly defined, accepted incompatibility preserving both voice and generation semantics; (2) the named compound, interrupted and released-note risks have concrete tests/observations, not merely speculation; (3) automated test/worker/PCM invariants and physical listening/stress are green; and (4) open follow-ups are explicit. **Do not equate 3,706 passing tests with this all-properties sign-off; do not expand scope to Pattern/Sequence Source creation or comprehensive Instrument substitution.**
