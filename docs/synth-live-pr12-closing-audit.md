# PR #12 — closing audit and finite acceptance plan

**Current closing-workstream checkpoint (2026-11-15): C1/C2/C3/C4 ACCEPTED by their bounded automated contracts; C5/C6/C7 and the original `docs/todo.md` all-nonstructural Synth feature remain OPEN.** **C5(a–d) now have accepted bounded automated worker/buffer, 3→17→3 retirement and combined multi-owner nested-Multi/Filter stress tests (3,822/3,822 at C5(d) implementation HEAD `717c954`); C5 remains OPEN for C5(f) measured load limits and C5(e)'s still-unconfirmed running .NET runtime/UI warning state; Linux CPU/audio-server/sink metadata is now recorded, while Windows hardware is currently unavailable and untested; first Linux physical recovery evidence was supplied on 2026-11-15**, as detailed in [C5 qualification](synth-live-pr12-c5-realtime-qualification.md). C2's [28-surface final reconciliation](synth-live-pr12-c2-inspector-matrix.md#c2af--final-28-surface-c2-automated-inspector-to-pcm-signoff-2026-11-15) covers the actual current editor and all eligible numeric/choice properties; these automated PCM proofs do **not** substitute for C5 device deadlines or C6 physical listening. This updates the historical November 14 baseline below without rewriting its chronological intermediate OPEN findings. PR #12 is still OPEN/DRAFT/UNMERGED.

**2026-11-15 C5(e) LINUX MACHINE/BACKEND FOLLOW-UP:** For the physical SDL run, the tester reports **8-core/16-thread AMD Ryzen 7 7840HS with Radeon 780M Graphics**, **30.65 GiB RAM** (~15.15 GiB in use at the `inxi` snapshot), kernel **Linux 7.0.0-38-generic x86_64**, audio server **PulseAudio (on PipeWire 1.6.2)** and default analog stereo sink `alsa_output.pci-0000_c1_00.6.analog-stereo`. `dotnet --version` printed **11.0.100-rc.2.26504.105**, which reports the **SDK**, not necessarily the .NET 10 runtime executing the net10.0 Heresy process; exact running runtime remains unconfirmed. The tester **does not currently have access to a Windows machine**: Windows physical audio validation is **not performed and is optional for now**, not presumed failed. Remaining discrete metadata: runtime actual version and whether UI Runtime Diagnostics showed any warnings (the recorded CSV fault type was empty). These host facts do **not** change the bounded Linux recovery success or the measured overloaded configurations. **C5(f), C6, C7, main TODO and merge remain OPEN.**

**2026-11-15 C5(e) FIRST PHYSICAL LINUX RUN — SAME-NOTE RECOVERY PASS, HIGH LOAD UNSUPPORTED, PARTIALLY QUALIFIED:** The user supplied one **99.8-second, 401-row real SDL device CSV** (`heresy-c5e-linux-20261115-130443.csv`) and a live Output Scope screenshot, and reported a Sine Oscillator → Filter → Multi Synth (pictured at **17 Spread voices, 12-cent detune, Randomize Phase and Gain Compensation ON**). The original first note was inadvertently auditioned at seven voices, then changed to three before the requested 3→17→3 stress. At **one 17-voice note** and **three concurrent nine-plus-voice notes**, playback audibly warbled/degraded and could not keep up, **but promptly recovered when returned to three voices**, without requiring a restart. Scope shows roughly **240 contiguous near-zero 1-sample/pixel columns (~5 ms)** in otherwise sounding PCM, consistent with actual padded underrun silence, though it is not timestamp-aligned to the CSV. The device session ran at **48-kHz stereo**, **2,048-frame ring / 256-frame producer blocks**, recording **236 ring underrun events (4 startup, 91 during ~32–42 s, 141 during ~76–79 s)** and **2,191 producer blocks over nominal budget**. After ~42.25 s the cumulative count held at **95** until ~76.25 s; after ~79.25 s it held at **236** through stop (~99.8 s): **two substantial no-new-underrun recovery intervals**. **7 worker Multi expansions / 29 children / zero cancellations**, peak queue 3, total off-PCM preparation ~0.925 ms; no CSV-reported fault type. Peak callback measured 1.361 ms, **zero nominal stream-request-duration overrun counter**, which is *not* a physical hardware deadline guarantee. These are user-supplied **physical** observations, not shared-runner CI results. [Full measurements and interpretation in C5 ledger](synth-live-pr12-c5-realtime-qualification.md#c5e--first-tester-observed-linux-sdl-device-run-physical-recovery-verified-with-overload-2026-11-15). **This establishes a bounded Linux recovery PASS and a real high-load performance limitation, not an all-load/device/OS pass.** Actual host CPU, Linux server/default physical output sink, .NET version, explicit UI Runtime Diagnostics observation and Windows run are still unreported. Preserve these limits for C5(f); **C5(e) PARTIAL, C5(f), C6, C7, main TODO and merge OPEN**.

**2026-11-15 C5(e) DEVICE QUALIFICATION PREPARATION — NOT PHYSICALLY ACCEPTED:** An optional SDL audio `HERESY_C5_AUDIO_CSV` recorder and deterministic, device-free CSV lifecycle test were added, and the [Linux-first/Windows-optional physical playbook](synth-live-pr12-c5e-device-playbook.md) defines exact output device, configuration, same-held-note 3→17→3, concurrent-note/rapid-revision, interruption, audible recovery, underrun-delta and counter evidence to collect. The file writer samples on its own timer **outside callback/PCM producer**; callback measurement is lightweight and opt-in only. An SDL stream callback's requested frames are not an OS/device hardware deadline, and a passing CI test of the recorder is **not** actual-device validation. **C5(e/f), C6, C7, main Synth TODO and merge remain OPEN awaiting tester/device evidence and final authorization.**

**2026-11-15 C5(d) AUTOMATED ACCEPTANCE ADDENDUM:** The [C5 qualification ledger](synth-live-pr12-c5-realtime-qualification.md) records exact C5(d) test-only HEAD [`717c954`](https://github.com/logiclrd/Heresy/commit/717c9543f5326a536f349628547ae8ee46fb2517), [Actions #43740229544](https://github.com/logiclrd/Heresy/actions/runs/43740229544), **3,822/3,822 Release tests**, Windows desktop/native/apphost/icon/versioned-release smoke and Linux ARM64 **SUCCESS**. Two new independent Playback regressions: a **three-owner nested Multi/three-stage Filter** test saturates the worker's **64 waiting slots** during 2→9→12→4 supersession, cuts/off-retires two owners and requires the remaining original note to adopt the *latest worker-prepared nested revision*, not fake +4 capacity (nested Multi gets **no audition headroom**). Worker **4 cancellations / 4 prepared children / 61 filler completions**, clean old-audio counts **0→2→1→0** after a deliberate structural refusal, fresh Note On and later compatible scalar edit. A separate **real buffered producer** uses three concurrent filtered/nested Multi notes, rapid 2→10→12 adoption, independent owner interruptions, same-note shrink to three and fresh-note isolation; **six completed expansions / sixty prepared children**, 188 producer blocks / 12,032 frames, 34 overbudget blocks, zero callback allocations, zero controlled/prebuffered underruns. **Zero underruns in the prebuffered test is not proof of device deadlines**. No production changes. **C5(e/f) physical device and load-envelope qualification, C6 listening, C7 authorization, main Synth TODO and merge remain OPEN.**

**2026-11-15 C5(c) AUTOMATED ACCEPTANCE ADDENDUM:** The [C5 qualification ledger](synth-live-pr12-c5-realtime-qualification.md#c5c--actual-filtered-multi-held-note-3173-producer-stall-and-recovery-2026-11-15) records exact-head [Actions #43737563480](https://github.com/logiclrd/Heresy/actions/runs/43737563480), **3,820/3,820 Release tests plus Windows release-bundle smoke and ARM64 SUCCESS**. A single original held Multi note used real three-stage OnePole child histories through worker-prepared 3→17→3; a gated buffered PCM producer was deliberately forced to underrun and subsequently recovered; the original's stable three-voice output survived, and a fresh distinct owner received current three-voice authoring. A paired direct-render test proved fourteen formerly expensive filtered children **stopped advancing**, without managed allocations in the measured PCM continuation. Observed CI producer render time per frame was **3.992 µs original three, 37.102 µs seventeen, 13.010 µs recovered three**—diagnostic only, not physical realtime deadlines; **three** actual callback underruns and **zero callback-allocated bytes** were recorded. All C5(c) modifications were test-only. **C5(d/e/f), C6, C7, the overall Synth TODO and merge remain OPEN**; never infer real-device signoff from a green shared-runner simulation.

**Audit date:** 2026-11-14. **Audited branch:** `feat/synth-live-parameter-foundation` at `ec2e1b324c5d1ee757fcf5e4684b78986ee466e8`. **State:** draft, unmerged. This is a **source/test inventory and closing plan**, not new DSP implementation, not a passing UI/device property matrix, and not authorization to merge. The current automated baseline is [CI #43610633937](https://github.com/logiclrd/Heresy/actions/runs/43610633937), 3,706 passing tests, Release, release-bundle smoke and ARM64 checks. The authoritative completion requirement remains [todo.md](todo.md), “Apply *all non-structural Synth property changes* to ongoing preview notes in realtime.”

**C2 active work (2026-11-14):** The [granular inspector→publication→held-PCM ledger](synth-live-pr12-c2-inspector-matrix.md) is now the current C2 status source. It inventories the *actual* available controls and separately tracks real UI commit (U), inspector callback (P), production transport publication (T), held-voice migration/currency (V), and PCM oracle (A). **C2(a)** covers the real Operator Minimum↔Maximum ComboBox, audio/document revision and callback counts, and 100-ms held-voice PCM crossfades without an older-audio mark. **C2(b)** covers the Multi **Unison preset**, **Override cycle** toggle and numeric **Cycle Length** TextBox, with real detached `FirPreparationScheduler` and independently authored PCM oracle. Those tests wire inspector callbacks directly to a live `PlaybackSession` and do not prove production transport T. **C2(c)** now exercises the real shared Script Envelope and shared OnePole Filter editor controls, including invalid and no-op suppression, exact `SharedAudioObjectCommitted` notifications, and **production `SongPlaybackTransport` child-before-parent publication** on a recording audio backend. Separate real renderer sessions prove the same held voice adopts the Script 100-ms fade and continuing OnePole retune against independent PCM oracles. The test does not claim the stub transport emitted PCM, that the MainWindow dispatcher ran or that other Script/Filter kinds are complete. **C2(d)** connects the real Source selector/Frequency/Constant/Exponential controls and Oscillator Waveform/Curvature/range/frequency/mode controls to a continuing physical voice and independently authored PCM, including a Source A→B detached preparation and B→None fade. It verifies single-commit revision/callback counts, no-op suppression, correct sound-generation adoption and no false older-audio currency. Its callback-to-session harness is not the production `SongPlaybackTransport` command gate; that particular T path remains open. **C2(e)** added actual Constant/Operator and the remaining Multi control families, the embedded per-use OnePole Override and Tail controls, and representative structural Input rewiring/Filter removal versus document-only canvas revisions. A red-first regression proved that submitting identical Multi text caused **three false audio revisions and publications**; `FmSynthEditorControl.AddMultiInspector.Apply` now suppresses fully identical immutable-node replacements without suppressing genuine preset changes. Tests cover 1,260 Multi PCM frames, two explicit 100-ms scalar/Operator ramps, and 1,800 continuing per-use OnePole frames against separate authored oracles. The corrected test HEAD passed [CI #43672698761](https://github.com/logiclrd/Heresy/actions/runs/43672698761), **3,773/3,773 tests** plus ARM64 and release packaging. This still does not verify the production `SongPlaybackTransport` T queue for these particular inspector controls, alternate Filter stage kinds, or shared Node/Instrument Volume editing. **C2(f)** now bridges an actual Operator and Constant inspector directly through the **production `SongPlaybackTransport`** background worker and actual `PlaybackRequestAudioSourceFactory` to the existing live source's **manually driven PCM**. The worker compiles frozen child Synth revisions before a dependent Synth parent; two 100-ms held-output transitions (Minimum→Maximum 0.2→0.8 and Constant 0.2→0.95 under Maximum) match explicit sample-by-sample oracles, do not reopen the audio session or mark the note older, and identical UI/transport publications are deduplicated. Unlike prior test-controlled `PlaybackSession` callback proofs, this verifies U/P/T/V/A as one path for these two controls, while deliberately not claiming SDL hardware/deadline performance. No production change was necessary. **C2(g)** now adds fully integrated real shared-object UI→production transport→existing-note PCM tests: the embedded **Node Envelope** editor (including invalid numeric/draft no-op, Vmax display-only revision suppression, exact shared notification, and held 0.25→0.75 100-ms fade), and the actual **modeless Instrument Volume** editor opened from a Source inspector (one 1→0.25 shared edit affecting two already-sounding Sample-backed Instrument sources through Add). Both shared-object notifications and parent Synth inspector callbacks coalesce to one child-before-parent, detached frozen preparation; the manually driven production live audio source renders frame-exact 100-ms smoothsteps without reopening the audition or falsely marking voices older. The new headless tests do **not** claim live migration of Script Instruments, nested non-Sample tone sources, post-release envelope edits, hardware callback deadlines or the MainWindow dispatcher itself. C2(g) used **test-only** compile and oracle corrections; no production DSP/UI/transport changes. Both new UI regressions and all **3,776/3,776 tests passed** at the corrected implementation HEAD, with Release, Windows apphost, versioned bundle and Linux ARM64 successful in [CI #43676099226](https://github.com/logiclrd/Heresy/actions/runs/43676099226). **C2(h)** extends integrated real-inspector U/P/T/V/A to one **SvfNotch per-use Q** update beside an already-connected absolute FM CutoffHz port: invalid/no-op suppression, preserved Filter state/port identity, detached child-before-parent production preparation and 900 continuing output-source PCM samples against a separately authored Filter oracle, with one session and no false older mark. Its initial CI failure was solely a missing test helper; the corrected implementation-head [CI #43677647958](https://github.com/logiclrd/Heresy/actions/runs/43677647958) passed **3,777/3,777**, full Release/native/Windows packaging and ARM64. No production changes. This does not certify other Filter kinds, FIR redesign or hardware deadlines. **C2(i)** now verifies the actual Envelope ComboBox Node A→Node B→Script→None while held and a separate Node selection after Note Off via the production worker/output source: each eligible held change matches 101 frames of 100-ms smoothing, and the already-releasing selection is atomically refused while the original release PCM, sticky older count and retirement are preserved, yet a fresh note uses the replacement. Two ordinary editor changed messages still yield just one semantic transport publication per selector action. The `New...` no-owner failure path safely restores selection; successful modal creation remains unverified. The first CI failure was a test-only mistaken notification count, corrected without production changes. [C2(i) exact-test-HEAD CI #43678549053](https://github.com/logiclrd/Heresy/actions/runs/43678549053) passed **3,779/3,779** plus Release/Windows/ARM64 packaging. **C2(j)** now takes the actual Source selector's Sample A→B→None changes through production `SongPlaybackTransport` and a true detached `FirPreparationScheduler` preparation mailbox: first A survives a producer boundary while B is still being prepared, then the continuing production note emits 101 exact smoothly blended frames for A→B and 101 for B→None without false older-generation currency, restart or duplicate publication. The manual test driver uses the real source's `IFirPreparationAudioSource` queue/adopt protocol around producer renders (the same interface used by the buffered worker), but does not exercise SDL callbacks or device deadlines. Test-only [CI #43679309430](https://github.com/logiclrd/Heresy/actions/runs/43679309430) passed **3,780/3,780**, full Release/package and ARM64. **C2(k)** extends the real Source/Oscillator inspector controls to frozen child-before-parent production transport publication and the actual continuing PCM output source, backed by separate independently authored original-voice oracles. Seven Oscillator edits (waveform, curvature, Vmin/Vmax, frequency, exponential and Constant) and three Sample-backed Source frequency/mode edits each publish once and render 141 corresponding oracle frames, with no session reopen or older currency. Initial red tests required a test-only fix to compare FM node parameter values instead of reference identity. Exact corrected test-head [CI #43680425908](https://github.com/logiclrd/Heresy/actions/runs/43680425908) succeeded **3,782/3,782 tests**, full Release/packaging and ARM64; no production changes. **C2(l)** integrates the real Multi inspector with production frozen child-before-parent transport, manually driven continuing PCM and the actual off-PCM FIR/Multi preparation mailbox. Thirteen independent UI commits cover Unison and Chorus presets, cycle enable/value/disable, detune, 2→3 voice growth, gain and random-phase toggles, Spread/Harmonic/Chord distributions and both component strings. Each revision's production PCM matches a separately authored *continuing* Multi Session for 141 frames, and Unison audibly differs from an untouched note. Worker onsets are required for Unison/cycle/random-phase edits, but not for modest voice growth within already-reserved headroom. Initial red CI exposed an overstrict worker-count assertion, not a DSP bug. Test-only [CI #43682150601](https://github.com/logiclrd/Heresy/actions/runs/43682150601) succeeded **3,783/3,783 tests** with Release/package/ARM64; no production changes. **C2(m)** now certifies the real shared-Filter definition ComboBox through production transport to continuing PCM, covering **same-definition numeric reset** (per-use OnePole cutoff 45→165 with preserved IIR and 900 matched PCM frames), **distinct-definition refusal even under identical stage kind/ID/defaults** (older=1 while a new note receives the new Filter), and **Filter→None refusal** (two older IIR voices retained while new notes bypass, then older count clears only on retirement). The test also verifies one frozen child-before-parent worker publication per changed selection, no audio restart or false duplicate, and the unattached `New...` failure path reverts safely. Its same-definition reselect is triggered via temporary *UI-only* deselection in a headless test and is not a claim that a normal same-item click would fire. Test-only [CI #43683088034](https://github.com/logiclrd/Heresy/actions/runs/43683088034) passed **3,784/3,784 tests**, Release/package/ARM64; no production changes. **C2(n)** now verifies the actual serial BiquadPeaking→CombFeedforward→FirLowPass per-use Filter stage inspector through *frozen production transport to continuing PCM*. Real GainDb 0→12, Mix 0.5→0.9 and unbound FIR CutoffHz 1000→1500 edits each publish once and produce 1,300 sample-matched continuing production frames against separately compiled Biquad/Comb/FIR states, with correct off-PCM preparation, no false older count and invalid/no-op suppression. A separate real Tail inspector case extends **0.012→0.018** after a finite four-frame Sample reaches natural EOF but while its OnePole residue rings, audibly exceeding the *original* exclusive frame-16 deadline; it then shortens **0.018→0.015** at frame 17 and exactly stops at the new exclusive frame-19 deadline, matching independent output throughout. The stronger endpoint regression was added to avoid proving only a stored Tail value. Test-only [CI #43684313160](https://github.com/logiclrd/Heresy/actions/runs/43684313160) **COMPLETED SUCCESS — 3,786/3,786 tests**, full Release/package/ARM64. No production code changes. These cases do **not** prove FM-bound FIR redesign, arbitrary stage families, or Note Off/release-envelope tail mutation. **C2(o)** adds two real shared-Envelope editor→frozen production transport→same-note PCM regressions. The Script editor commits source **0.25→0.75**, then release limit **90→70 ms** while held; the original note smoothly adopts and retains its release policy, while a subsequent **0.75→0.15 / 70→20 ms** shared Script edit *after Note Off* is deliberately refused on the old audible release, with identical PCM to an unchanged reference, sticky older currency and a fresh note using the revised script. The Node editor changes **Forward→PingPong** and sustain start **1→0** while held, each matching 101 PCM frames of an independently frozen-curve oracle anchored at the **last audible sample** (not next-frame old-curve reevaluation); changing Node initial level and final release endpoint during Note Off similarly leaves the old release/retirement intact while new notes use the revised curve. Red-first CI exposed two test-only oracle/notification assumptions, not a DSP bug. Corrected implementation-head [CI #43685741054](https://github.com/logiclrd/Heresy/actions/runs/43685741054) passed the new tests, Release and ARM64. No production changes. These cases do not certify arbitrary nested Script/Node graphs, unbounded releases, or hardware playback. **C2(p)** now integrates real Source selection through the production transport for an already active Sample A→B fade with pending C→latest D and verifies 101+101 frames of correct Source-local PCM: C's queued *Synth definition* does not become an audible/prepared Sample onset; D is created on the detached onset worker only after A→B completes, with no premature older mark. A second integrated test switches an old Sample to a new data Instrument (approved incompatible old voice), starts a fresh Instrument voice, opens its actual modeless shared Volume editor and changes Volume 1→0.5: the newer note takes the 100-ms compatible edit while the old Sample remains **older=1** until individually cut; its NoteCut uses a short de-click ramp, after which older count clears. This red-first test found a **real contextual Source-inspector refresh gap**: Instrument Volume controls did not appear until reselecting the graph node. [`7bfe581`](https://github.com/logiclrd/Heresy/commit/7bfe5816b69aac9697bbe28d9558d1d553e927b9) fixes only the UI refresh on successful changed SourceId; it does not alter cross-family DSP compatibility. A later red test expected instantaneous NoteCut silence and was corrected to check the actual de-click ramp. Corrected test-head [CI #43686827300](https://github.com/logiclrd/Heresy/actions/runs/43686827300) passed **3,790/3,790 tests**, Release/ARM64. The only production change is Source inspector refresh. **C2(q) audit of 28/28 actual inspector surface rows is COMPLETE, but C2 property acceptance remains OPEN.** The [detailed property audit](synth-live-pr12-c2-inspector-matrix.md#c2q--final-existing-inspector-inventory-and-evidence-disposition-not-a-c2-closure-2026-11-15) distinguishes individual bounded U/P/T/V/A proof, structural graph/descriptor changes, C1-approved Instrument substitution deferrals, presentation-only fields, and real gaps. This is a stricter gate than one representative test per family: docs/todo.md requires tests for every currently exposed nonstructural property. **Concrete gaps** include (1) Operator Add↔Multiply production transport PCM, (2) actual shared Filter editor→production-source held PCM instead of its existing recorder transport plus separate renderer PCM, (3) audible nonneutral Constant-mode changes, (4) 8 additional exposed Multi presets, and especially (5) untested FilterCatalogue numeric/choice properties (e.g. ShelfSlope, Resonance, Drive, Feedback, FIR band edges, Window/PhaseResponse, Oversampling, AllPass Order and OnePole Mode), each requiring a real compatibility/structural decision and regression. Explicitly structural Comb DelaySeconds and FIR TapCount/TransitionWidthHz are not incorrectly declared eligible. Headless modal New... success, graph rewiring, C1-approved unrelated Instrument substitution, device deadlines (C5) and listening (C6) remain separate. C2(q) also caught outdated visible FM Filter inspector copy claiming DSP and Tail were not implemented; production [5f38e87](https://github.com/logiclrd/Heresy/commit/5f38e8706c76c2a7b00bf2b29d4a05a0f573e32b) fixes that copy and test-only [1480529](https://github.com/logiclrd/Heresy/commit/148052930663c5b1a2f05452cbcca636c5265ed9) prevents regression. No DSP or transport changes. Next bounded C2(r) should bridge Operator Add/Multiply and shared Filter numeric defaults as actual whole paths, then divide the remaining catalogue property matrix without prematurely closing C2. The [detailed C2 matrix](synth-live-pr12-c2-inspector-matrix.md) governs remaining untested fields. The original C2 property-matrix table below remains the broad source-level inventory, not a completed UI-to-PCM checklist. **C2 stays OPEN**; C1/C3/C4 remain closed, C5/C6/C7 and the overarching all-properties TODO remain open.


## C2(h) — non-OnePole per-use SVF notch Q, connected cutoff and production transport (2026-11-14/15)

**Bounded U/P/T/V/A:** The actual Synth inspector embeds a per-use Filter instance editor for one shared SvfNotch stage. Its audio input is a 190-Hz Sine Oscillator; CutoffHz is **already connected** to Constant(280) as an absolute FM port, while Q is initially inherited/unconnected. The real Q Override toggle and Apply handler reject invalid NaN without publishing and commit **Q=9** exactly once, with unchanged stage topology, port identity and binding. Reapplying the same loaded value is a no-op.

The inspector callback reaches **production SongPlaybackTransport**, which freezes the graph and prepares the changed child Synth **before** its dependent parent through PlaybackRequestAudioSourceFactory. A manual backend drives the **real production output source**, not a recording stub or test-direct session publication. The existing session remains open, the worker snapshot has Q=9 and the immutable base song snapshot revision does not falsely advance. The same already-playing note produces **900 PCM frames** matching a separately authored Filter/Synth oracle with preserved oscillator/SVF histories and the original cutoff connection; a never-edited third reference rules out vacuous equality. Older-audio count remains **zero**. Identical transport re-publication is suppressed.

**Source/CI:** [FmSynthInspectorTransportPcmTests.ModulatedSvfNotchPerUseQInspectorRetunesContinuingProductionFilter](../Heresy.Tests.UserInterface/FmSynthInspectorTransportPcmTests.cs). Initial [9024855](https://github.com/logiclrd/Heresy/commit/90248558acfcc5f3f3c04c0df75af02371e99c1b) had a test-only compile error because the independent Resolver helper was missing (CS0246). Corrected [ce7ae11](https://github.com/logiclrd/Heresy/commit/ce7ae11a4026a98148223c60a55d67265e768d2c), with **no production code changes**. Exact test-HEAD [CI #43677647958](https://github.com/logiclrd/Heresy/actions/runs/43677647958) **COMPLETED SUCCESS**: **3,777/3,777 tests**, Release/native desktop, Windows apphost/versioned release bundle, Linux ARM64. The log explicitly passes the named regression.

**Limits:** The result does not prove other SVF parameters, Biquad/Ladder/Comb/FIR, multi-stage changes, other connected-port configurations, shared Filter definition replacement, worker-prepared FIR, already-releasing Filter tails, the MainWindow dispatcher, device deadlines or physical listening. The [C2 matrix](synth-live-pr12-c2-inspector-matrix.md) remains authoritative. C1/C3/C4 remain checked, **C2/C5/C6/C7 and the main TODO remain unchecked**, and PR #12 remains open/draft/unmerged.


## C2(i) — shared Envelope selector: sustained choices and already-releasing ownership (2026-11-15)

**Integrated U/P/T/V/A accepted, bounded:** [`FmSynthSharedNodeVolumeTransportPcmTests`](../Heresy.Tests.UserInterface/FmSynthSharedNodeVolumeTransportPcmTests.cs) now drives the real `FmSynthEditorControl.AddEnvelopeInspector` ComboBox on an already-held source whose output is an audible FmEnvelopeNode. Three choices **Node A (0.25)→Node B (0.75)→Script expression (0.4)→(None)** cause three distinct semantic edits. The original session is retained; each publication prepares an immutable child Synth then dependent parent through actual `SongPlaybackTransport` + `PlaybackRequestAudioSourceFactory`, and the **real production source** is manually rendered. All **3 × 101 frames** agree with independently calculated 100-ms smoothsteps and there is no false older count, session reopen or base-snapshot revision advancement. A selection invokes two ordinary editor notification callbacks (parameter commit plus rebuilt inspector) but the audio-revision gate correctly permits **only one worker publication**; duplicate reselect does nothing. An attempted `New...` from an unmounted editor cannot show its modal prompt and safely restores the original selection without publishing. Modal *success* is not tested.

The **already-releasing** companion test starts two identical independent production output sessions from the same original sustained Node Envelope with a 90-ms release. Both receive Note Off at the same frame. The real UI then selects a Node Envelope whose release ends at 20 ms. The actual transport worker prepares child before parent, but the original releasing voice **refuses** this changed audible release Envelope, retains PCM identical to the untouched production reference and reports sticky **older-audio count 1**. A new virtual note after publication uses the selected replacement, and the old count drops only when the original release retires. This directly couples C4(n)'s existing release-generation rule to the real inspector/production path, without changing its conservative release policy.

**History:** [`bd6a7b7`](https://github.com/logiclrd/Heresy/commit/bd6a7b7cedce53ce2f8d4ad3f37c7a42941e379a) first compiled and ran both tests. The release test passed; the held selector test falsely demanded a single **inspector changed notification** rather than one **audio publication**, failing despite correct worker deduplication. [`d66d5e3`](https://github.com/logiclrd/Heresy/commit/d66d5e3c37b4b48a5f68409e4c0618a063c8a126) fixed this test expectation only. **No production modifications.** Corrected exact-test-HEAD [CI #43678549053](https://github.com/logiclrd/Heresy/actions/runs/43678549053) **COMPLETED SUCCESS**, both new tests pass, **3,779/3,779 total** (Core 910, Render.File 29, Render 1,213, Scripting 105, Playback 356, UI 1,166), Release/native host/Windows apphost/versioned bundle and Linux ARM64.

**Explicit remaining limits:** successful `New...` dialog creation, arbitrary Script Envelope release, other initial unassigned UI paths, mixed/instrument/filter/recursive graph envelopes and device deadlines/listening are not proven by these specific tests. C2 remains OPEN; C5, C6, C7 and top-level all-nonstructural TODO remain unchecked. PR #12 stays OPEN/DRAFT/UNMERGED. Next potential bounded C2 residual is production transport for worker-prepared Source Sample switching or equivalent Filter-definition selection/structural currency.


## C2(j) — integrated Source selector and worker-prepared Sample replacement (2026-11-15)

**Accepted U/P/T/V/A plus actual off-PCM expansion worker, bounded to one Source node:** [`FmSynthInspectorTransportPcmTests.SourceSampleSelectorPublishesFrozenWorkerPreparedReplacementAndHeldPcm`](../Heresy.Tests.UserInterface/FmSynthInspectorTransportPcmTests.cs) exercises `FmSynthEditorControl.AddSoundSourceInspector` with real SampleDefinition assets that are imported and decoded before audition and have stable four-frame mono sustain loops. Initially the *same held virtual note* emits Sample A at **0.25**, then the real dropdown selects Sample B at **0.75**. The actual document mutation yields exactly one semantic audio/document revision and one inspector→production transport publication. The frozen `SongPlaybackTransport` worker compiles the new child graph before its dependent (not sounding) Synth parent and keeps the existing output source and base snapshot stable.

**New Sample invocation ownership:** A SourceId replacement is not a stateless parameter fade: incoming Sample B requires off-PCM invocation preparation. After the UI+transport work completes, the very next manually driven production-source PCM boundary still emits old Sample A. The test calls the source's actual `IFirPreparationAudioSource.QueuePendingFirControls` through a separate real `FirPreparationScheduler`, waits for its completed Multi/Sample expansion and checks the absence of worker faults, then calls `AdoptPreparedFirControls` before rendering the next block. This is the same handoff used in `BufferedAudioOutputSource`, with the scheduling cadence driven deterministically by the test rather than an audio device. After adoption, the held note renders **101 samples** matching the explicit **0.25→0.75 100-ms smoothstep**; the original note is never restarted and older-audio count remains zero.

**Removing the selected Sample:** The same ComboBox then selects `(None)`, generating one further single publication. This path requires no new Sample invocation; the outgoing B Sample state is retained through **101 samples of 0.75→0 smoothstep** before reaching silence, again without reopening the output or marking older. Same-value UI reselect and direct duplicate `PublishSynthEditAsync` calls are exact no-ops, and the transport's immutable base snapshot revision is unchanged.

**Evidence:** [`5dd53d1`](https://github.com/logiclrd/Heresy/commit/5dd53d1c119b748b96d6f479c3ec4cc1ab473e37) added the real selector + production transport + preparation worker held-PCM test and minimal frozen-SourceId test instrumentation; **no production code changes.** Exact test-HEAD [CI #43679309430](https://github.com/logiclrd/Heresy/actions/runs/43679309430) **COMPLETED SUCCESS**, including the named new regression, **3,780/3,780 tests** (Core 910, Render.File 29, Render 1,213, Scripting 105, Playback 356, UI 1,167), full Release/native host/Windows apphost/versioned release bundle and Linux ARM64.

**Explicit exclusions:** This fixture uses real production output/transport/worker interfaces with a manual PCM driver; it does **not** certify hardware callback scheduling, real ring-buffer deadline and underrun behavior, or physical hearing (C5/C6). It does not extend Source selection to same-asset/alias replacement, initial None→Sample, overlapping transitions, live re-pitch during SourceId replacement, an already-released Sample, Instrument/Synth/cross-family deferrals or arbitrary mixed graphs. All remain independent evidence; do not conflate older C2(d) direct-session support with transport T in those subcases. C2 stays OPEN, C5–C7 and overarching live-property TODO unchecked, PR draft and unmerged. Next bounded candidate: C2(k), frequency/mode/shape inspector controls through actual production transport.


## C2(k) — real Source and Oscillator pitch, shape and modes on production transport (2026-11-15)

**Integrated U/P/T/V/A accepted, bounded to two controlled FM graphs.** New real headless inspector tests in [FmSynthInspectorTransportPcmTests](../Heresy.Tests.UserInterface/FmSynthInspectorTransportPcmTests.cs) connect actual FmSynthEditorControl controls through actual SongPlaybackTransport and detached-snapshot PlaybackRequestAudioSourceFactory worker. Each committed frozen child Synth is prepared before an authored dependent parent. The same already-running production output (manually driven only at the device boundary) renders samples without a new Note On, audio session restart, false older-generation warning or original base-snapshot advancement. Each edit produces exactly one semantic audio/document revision, one inspector publication, correctly frozen fields, and the expected 141-frame PCM window. Repeated same-value UI actions and direct duplicate transport calls are no-ops.

**Oscillator:** One unchanged connected Constant=2 multiplier supplies a Sine oscillator at 80 Hz, output Vmin=-0.6, Vmax=0.6. The real Waveform, Curvature, Vmin, Vmax, Frequency, Exponential multiplier, and Constant controls commit **seven individual changes**: Sine→Square; Curvature 0→1.5; Vmin -0.6→-0.25; Vmax 0.6→0.9; frequency 80→117; linear input multiplier 2→2 semitones; and Constant false→true. The production output renders **7 × 141 = 987 consecutive PCM frames** matching the separately authored revised FmSynthSound on its own preexisting playback voice. A second never-edited reference checks nonvacuous differences for waveform, Vmin/Vmax, frequency, and exponential mode. The phase/clock is not restarted; the retained note remains current.

**Source:** A real imported/decoded **looping 16-frame Sample** is selected by a single Source node with connected Constant=2 multiplier. The actual Source Frequency Hz (neutral→1.5×neutral), exponential multiplier (linear→semitones) and Constant false→true controls each commit while that same Sample note plays. Independent Sound/Session oracle uses manually authored 16-bit PCM and immutable FmSoundSourceNode revisions; the original loop readhead/trajectories continue. The **3 × 141 = 423 PCM frames** of production output match sample-by-sample. Frequency/exponential revisions are audibly distinguishable from the never-edited note; Constant at neutral note pitch has only a numeric oracle, not nonvacuous transposition proof.

**CI/provenance:** Initial [c1b23e1](https://github.com/logiclrd/Heresy/commit/c1b23e1f56f0fb20e18477bbfa73bfb02b5ec50a) built, but tests initially stopped on object-identity comparisons for FM nodes (not value-equal by default). The test-only [6593962](https://github.com/logiclrd/Heresy/commit/6593962e5156dca388f7765ca300d5fb0a05d882) corrected both committed and frozen-worker comparisons to all scalar/identity fields; no renderer, UI or transport code changed. Corrected exact-test-HEAD [CI #43680425908](https://github.com/logiclrd/Heresy/actions/runs/43680425908) **COMPLETED SUCCESS, 3,782/3,782 tests** (Core 910, Render.File 29, Render 1,213, Scripting 105, Playback 356, UI 1,169), both named UI regressions PASS, and Release/native host/Windows apphost/versioned bundle/Linux ARM64 all PASS.

**Boundaries:** This is real manually rendered production PCM, not physical-device underrun/deadline proof or human hearing (C5/C6). It does not cover arbitrary multi-generation/overlapping fades, non-unit pitch slides for Constant, structural multiplier rewiring, other FM input source shapes, nested Filter/Multi, simultaneous SourceId+frequency, Script Instruments or already-releasing cases; do not extrapolate the controlled reference into a general compatibility guarantee. The existing C2(d) renderer-level tests and C3/C4 state/lifecycle evidence remain distinct. **C2 OPEN**, C1/C3/C4 checked, C5–C7 and overarching all-properties TODO unchecked; PR #12 still OPEN/DRAFT/UNMERGED. Next bounded recommended C2(l): actual Multi inspector publication via production transport to its continuing PCM oracle.


## C2(l) — integrated production-transport Multi inspector, active-voice PCM and off-PCM onset preparation (2026-11-15)

**Bounded acceptance U/P/T/V/A** through the real `FmSynthEditorControl.AddMultiInspector` in [`FmSynthInspectorTransportPcmTests.MultiInspectorControlsPublishWorkerPreparedHeldProductionPcm`](../Heresy.Tests.UserInterface/FmSynthInspectorTransportPcmTests.cs). One production-transport audition starts an 83-Hz Sine→Spread(2) Multi with Detune 15, Randomize Phase off and Gain Compensation on. A dependent parent Synth is also authored, so the real `SongPlaybackTransport.PublishSynthEditAsync` must prepare the changed child before that parent from each frozen document snapshot. Thirteen inspector changes occur during the *same sounding note*: Unison preset; cycle override on, 1120 and off; Spread detune 30 and voice count 3; gain off; random phase off; Harmonic distribution and its 1 3/2 2 components; Chord distribution and its 0 3 7 components; Chorus preset returning to Spread(4). Each commit creates exactly one semantic document/audio revision and publication; the child and parent are each compiled once in FIFO order and their immutable authored Multi values are checked field-by-field. No-op unchanged Multi TextBox submissions and a direct repeated publication do not produce phantom revisions.

**Same production output, independent retained-history reference:** The actual `PlaybackRequestAudioSourceFactory` source is manually driven instead of attached to SDL hardware. It stays open without a new Note On through all thirteen edits; the original transport base snapshot revision does not advance. For every revision, a separate original-voice `PlaybackSession` with its own independently authored `FmMultiNode` and `FmSynthSound` is migrated to that same authored parameter set. One immediate boundary frame plus 140 further production frames match this independent session within 7e-5. This is **13 × 141 = 1,833** continued PCM frames, preceded by 12 identical initial frames, with zero false older-audio count. A separately started never-edited reference confirms Unison is measurably non-identical to untouched playback.

**Prepared onsets vs reserved voices:** The same production source's `IFirPreparationAudioSource` is driven with a real `FirPreparationScheduler` and its worker completion/adoption boundary exactly as used by `BufferedAudioOutputSource`. The test explicitly demands independent completed worker preparation on **Unison random phase onset, cycle override enable/change/disable, and Randomize Phase true→false** for production and oracle histories before adoption. In contrast, **Spread Voice Count 2→3 and Chorus 3→4** use already-reserved prepared Multi capacity rather than creating another worker job. Ratio, component and gain edits are also compared without unsupported assumptions of fresh construction. Following each action, same-session PCM, preserved histories and current-generation currency are enforced; the off-PCM scheduler has no fault.

**CI and scope:** Initial implementation/test-only [`6bafd79`](https://github.com/logiclrd/Heresy/commit/6bafd79d1b7ae8105ef3988c345b132d8fe0e761) was followed by a stricter nonvacuous PCM assertion in [`3eae8e7`](https://github.com/logiclrd/Heresy/commit/3eae8e7fb0044f427e7cb2002ff8ebdabd891c74). That revision's test reached Spread growth then failed only on the *test* requiring two nonexistent fresh worker completions despite existing reserved voice headroom. [`047f737`](https://github.com/logiclrd/Heresy/commit/047f73741cc24154edeb472ebfd330b9d82b2d70) corrected worker expectations; exact-test-HEAD [CI #43682150601](https://github.com/logiclrd/Heresy/actions/runs/43682150601) **COMPLETED SUCCESS**, named regression PASS and **3,783/3,783 tests** (Core 910, Render.File 29, Render 1,213, Scripting 105, Playback 356, UI 1,170), native desktop/Windows apphost/versioned release bundle and Linux ARM64 successful. **No production code changes.**

**Limits:** This tests real frozen worker/transport/current held PCM and off-PCM preparation with a manually driven output, *not* OS audio device/underrun/deadline behavior (C5) or physical listening (C6). It covers one oscillator child, small voice counts up to four, two named presets, one per-edit 141-frame separation, and valid component strings. Larger Multi overload/recovery, overlapping 100-ms edits, other presets, new port wiring, nested compound graphs and release/refusal boundaries retain C1/C3/C4 or subsequent C2 scrutiny. C2 remains OPEN, C1/C3/C4 complete; C5–C7 and overarching TODO unchecked, PR #12 draft/unmerged. Next independent bounded C2 target: Filter shared-definition selection, compiled stage-plan changes, and older-generation refusal through real inspector and production transport.


## C2(m) — real Filter definition selection: compatible numeric reset versus cross-definition and None refusals (2026-11-15)

**Bounded integrated U/P/T/V/A accepted** by the new actual-inspector regression [`FmSynthInspectorTransportPcmTests.FilterDefinitionSelectorKeepsCompatibleHistoryAndRefusesOtherDefinitions`](../Heresy.Tests.UserInterface/FmSynthInspectorTransportPcmTests.cs). Two shared FilterDefinitions (A and B) have *different* ObjectIds but equivalent **OnePole stage ID=1, topology, kind and default CutoffHz=165**, so the test distinguishes definition-identity rules from mere numerical coefficient changes. The edited Synth starts with an Oscillator→Filter A using per-use CutoffHz=45; a dependent Synth parent is present for child-before-parent worker ordering checks. Actual `FmSynthEditorControl.AddFilterInspector` ComboBox selection commits are published with the production `SongPlaybackTransport` command worker and actual `PlaybackRequestAudioSourceFactory` live output, manually rendered without a device. Each semantic selection increments document/audio revision once, prepares the frozen child before parent and validates frozen Filter ID/cutoff; all continuing PCM frames match a separately authored Filter/Synth `PlaybackSession` oracle preserving independent IIR histories.

**Compatible same-object numeric reset:** The test temporarily clears *only the ComboBox's UI selection* without any model mutation, then rechooses A. This exercises the real selector branch rebuilding the **same shared definition** with a fresh per-use instance, thereby removing its CutoffHz=45 override and adopting the shared default 165. The held original voice preserves its ongoing OnePole history and renders **900 independent-oracle-matched PCM frames**, measurably different from an untouched 45-Hz reference, with older count zero. This is a controlled headless re-selection path; it does **not** claim a normal user click on an already-selected ComboBox option generates such a change.

**Definition identity boundary, despite equivalent stages:** Selecting Filter B has the same stage IDs, kinds, default parameters and topology as A, but a distinct persisted Filter ObjectId. Production correctly **refuses** live IIR state adoption; the old A voice remains audible and reports sticky **older=1**, while a freshly started note on another physical channel uses B. The oracle verifies the original A history, new B note and mixed PCM. Selecting `(None)` further refuses to bypass the two already-sounding OnePole histories, raising **older=2**; a third new note uses the bypass graph. Continuing mixed PCM agrees with an independent two-older/one-current oracle. The selector's `New...` operation, invoked without an owning Window in this headless fixture, safely restores the unassigned choice without any audio revision or transport publication. Cutting the two original channels retires their distinct older voices and restores older count zero while the new bypass note continues; repeated direct publication of unchanged revision is suppressed. The production audio source never reopens and its original frozen session snapshot never advances.

**Provenance:** [`3cff01d`](https://github.com/logiclrd/Heresy/commit/3cff01d74336bd46cef62a7fffff560fb4f43843) introduced the new test; [`8045dba`](https://github.com/logiclrd/Heresy/commit/8045dba3a0878c8e06f60de3707a4f77821d4c47) corrected *test-only* preparation-record variable scope and never-edited oracle clock advance. Corrected implementation exact-HEAD [CI #43683088034](https://github.com/logiclrd/Heresy/actions/runs/43683088034) **COMPLETED SUCCESS**, named test PASS and **3,784/3,784 tests** (Core 910, Render.File 29, Render 1,213, Scripting 105, Playback 356, UI 1,171). Release/native desktop/Windows apphost/versioned bundle and Linux ARM64 PASS. **No production UI/DSP/transport changes.**

**Excluded:** Not physical audio callback/ring/deadline evidence (C5) or listening (C6). No successful owned-modal `New...` creation, arbitrary multi-stage/Biquad/Comb/FIR topology, structural modulation-port rewires, tails after Note Off, or mixed Instrument/Filter graph cases. Cross-Filter-ObjectId selection is explicitly *incompatible* even when stage plans match. Keep C2 OPEN, C1/C3/C4 complete, C5–C7 and overarching all-properties TODO unchecked, PR #12 draft and unmerged. Next bounded C2(n) targets remaining Filter kinds, multi-stage and FIR worker preparation.


## C2(n) — real multi-stage Filter inspector, worker-prepared FIR and active natural-EOF Tail (2026-11-15)

**Bounded integrated U/P/T/V/A:** Two new headless tests in [FmSynthInspectorTransportPcmTests](../Heresy.Tests.UserInterface/FmSynthInspectorTransportPcmTests.cs) drive real Filter controls through the production SongPlaybackTransport frozen-child-before-parent compilation path and the true PlaybackRequestAudioSourceFactory output with only the final hardware device replaced by a manual backend. All continuation checks use separately authored FmSynthSound revisions with their *own* retained DSP/Sample state, not newly re-triggered notes or copied production state. Existing output session and original base snapshot remain stable. Exact publication, invalid input and no-op gates are enforced.

**Three-stage serial Biquad/Comb/FIR:** At 8,000 Hz, one oscillator drives a FilterDefinition containing BiquadPeaking stage 1, CombFeedforward stage 2 and FirLowPass stage 3. The actual per-use FilterInstanceEditorControl Stage ListBox, Override toggle, numeric edit and Apply action separately change **GainDb 0→12, Mix 0.5→0.9 and FIR CutoffHz 1000→1500**. Each live revision compiles frozen child and dependent parent in that order, preserving all stage IDs/types and input wires, and verifies the worker's authored three-stage Filter snapshot. An independent prepared plan cache and separate Biquad, delay-line Comb and FIR histories render matching PCM for **280 original frames plus 3 × 1,300 = 3,900 continuing frames**, within 7e-4 per sample. Each changed stage measurably changes the sound compared to a never-edited note, but never restarts the held voice or marks it older. The **unbound** FIR cutoff design is already an immutable prepared plan when the new revision arrives at PCM; the expensive design occurs in the off-PCM compile stage, not during callback processing. Invalid NaN and identical re-apply of FIR cutoff 1500 produce no additional audio revision/publication, including a repeated direct transport request.

**Tail extension and shortening while finite Sample Filter rings:** A nonlooping, imported 4-frame mono Sample (0.75 amplitude, 1,000 Hz output) feeds one OnePole Filter with CutoffHz=20, initial TailSeconds=0.012, and finite Sample end at **frame 4**. After rendering the first 7 frames the Sample has ended, but the Filter's existing integrators are still producing audible residual. An actual real Tail TextBox edit extends the deadline by selecting **0.018**, frozen/published once; the same original IIR history remains, and production PCM agrees with the independent Sample/Filter history, including **nonzero frame 16**, which is already *beyond* the former exclusive **4+12=16** deadline. At frame 17, while this new tail is still active, the real Tail control changes to **0.015**, again frozen/published only once. Frames **17 and 18** remain audibly nonzero and **frame 19 forward is exactly silent**, proving the newly authored *exclusive* **4+15=19** end without resetting the Sample or assigning a false older status. Both field commits change document/audio revisions exactly once and the worker sees the new TailSeconds values; unchanged submission/transport no-op, one original output session, unchanged playback base snapshot. This is a **natural source EOF** case, not an actual Note Off with sustain loop, release envelope, NNA displacement or a fading child Sample.

**CI/evidence:** [3139141](https://github.com/logiclrd/Heresy/commit/31391411f8f1df13e9f13b5769e05a68ef21c94c) added the multistage Filter regression, followed by [f0f15b1](https://github.com/logiclrd/Heresy/commit/f0f15b1ae43ae2703e14af7b6741d4d70218a5ef) adding the natural-EOF Tail case. The latter passed, but its initial test window only observed frames *before* the original deadline; [9c01c99](https://github.com/logiclrd/Heresy/commit/9c01c990043a03d8158d035115297551c6babd8a) strengthened the test to require nonzero output beyond old deadline and exact silence after newly shortened deadline. Exact corrected test-HEAD [CI #43684313160](https://github.com/logiclrd/Heresy/actions/runs/43684313160) **COMPLETED SUCCESS, 3,786/3,786 tests** (Core 910; Render.File 29; Render 1,213; Scripting 105; Playback 356; UI 1,173), both named new UI tests PASS, Release/native host/Windows apphost/versioned release bundle and Linux ARM64 all PASS. **No production DSP/UI/transport code changed.**

**Remaining boundaries:** This production output is manually rendered, not hardware callback/deadline (C5) or human listening (C6). No claim for arbitrary Filter families, phase/structural FIR changes, connected FM cutoff work queues, graph-stage order/kind reassignments, many simultaneous FIR plans, shared-filter stage UI in an owned modal window, interrupted or already-releasing Note Off states, or other nested Instrument/Multi graphs. Those are separate narrower C2/C3/C4/FIR performance obligations. C2 remains OPEN, C1/C3/C4 complete, C5–C7 and global all-nonstructural Synth TODO unchecked, PR #12 still OPEN/DRAFT/UNMERGED. Next bounded focus C2(o): shared Node/Script Envelope variants and release-currency integration, then residual property/structural audits before signing off C2.


## C2(o) — shared Node sustain-timeline edits, Script expression/release policy, and active Note Off refusal (2026-11-15)

**Two bounded U/P/T/V/A integrations, no production code changes.** [`FmSynthSharedNodeVolumeTransportPcmTests`](../Heresy.Tests.UserInterface/FmSynthSharedNodeVolumeTransportPcmTests.cs) now exercises *actual* embedded `ScriptEnvelopeEditorControl` and `NodeEnvelopeEditorControl` controls under `FmSynthEditorControl`. Both call the shared `EnvelopeDocumentEditor` authoring path, issue exact `SharedAudioObjectCommitted` events, and coalesce with the Synth inspector's changed callback into one `SongPlaybackTransport` frozen child-before-parent revision per semantic audio change. Real `PlaybackRequestAudioSourceFactory` held notes render PCM into a manual output backend; the original output session remains open and base audio snapshot unchanged. Worker evidence captures actual Script source/release-limit and Node loop-mode/sustain-start fields rather than testing only the document mutation.

**Script shared expression and finite release policy.** The new `SharedScriptEnvelopeEditorRetunesHeldPcmAndRefusesReleaseEdits` test starts a current Synth Envelope node using Script source `0.25` with a declared 90-ms finite release. The embedded editor rejects invalid script text without publishing, then commits **0.25→0.75** once and produces **101 exactly smoothstepped PCM frames** on the *already-sounding note*. A second real editor action changes the finite release duration **90→70 ms** *before Note Off*; the held voice stays current and steady at 0.75 while its future release policy changes. Reapplying the same source/limit may still produce a UI changed notification from the Script control but **does not advance the document's audio revision, notify shared-object subscribers or invoke another preparation worker**. An independently started unchanged transport from the already committed Script definition serves as the *old 70-ms release* reference, aligned at Note Off. While that original release is audible, the real editor changes source **0.75→0.15** and limit **70→20 ms**, both published for new notes; production correctly **refuses** to replace the previous voice's release envelope/retirement. The old release PCM remains equal to the untouched reference, **older count 1** until natural retirement, and a separately started new note hears the authored 0.15 Script. Older count then returns to zero without interrupting the new note. The original note was never restarted.

**Node sustain shape/timeline before release.** The `SharedNodeLoopModeAndSustainBoundaryRetuneHeldTimelineButNotRelease` test begins with a four-point Node envelope 0.2 at 0ms, 0.8 at 20ms, 0.4 at 60ms, 0 at 120ms and sustain endpoints 1..2, Forward loop. At **original note frame 145**, the real Node inspector's loop-mode ComboBox commits **Forward→PingPong**, then after the first 100-ms fade its sustain-start TextBox commits **index 1→0** (PingPong retained). Each real shared-object edit triggers one child-before-parent frozen prepared revision and a 101-frame continuing production output fade. The test constructs **independent immutable `NodeEnvelopeCurve` snapshots** of old/new curves and checks the PCM sample-by-sample against the true renderer live-output fade: the *last previously audible sample* is frozen at revision start, and a 100-ms smoothstep transitions toward the revised curve at the still-running note's original elapsed time. The first version of the test incorrectly evaluated the outgoing Node curve *again* on the first revised frame, producing a 0.01 discrepancy. The corrected test enforces the audio continuity policy and proves both loop and sustain-boundary edits actually affect PCM, with no older mark or re-onset. A separately driven second production note session follows the same compatible revisions for the later release-reference comparison.

**Node audible release refusal.** After a Note Off on both synchronized producers, the actual shared Node editor changes first-node level **0.2→0.9** and final release node time **120→200 ms**. Though the new object/revision is prepared for future notes, the prior audible release must remain on its frozen old timeline; production PCM from the old voice remains identical to the untouched reference, sticky **older=1**, and a fresh note on a second virtual channel immediately hears the new 0.9 starting level. Eventually the old release retires and the older count clears, independently of the newer held voice. A repeated identical shared Node draft creates no additional transport publication. This is an explicit **already-releasing** case, unlike C2(n)'s earlier natural Sample EOF tail allowance.

**Test-only provenance:** [`40b63ff`](https://github.com/logiclrd/Heresy/commit/40b63ff829b8ee32d4737144b399893dfe7f2357) added the Script editor regression and generalized test-only frozen-worker Envelope instrumentation. [`8e9f112`](https://github.com/logiclrd/Heresy/commit/8e9f112b8d724e01e139222997602d37c0c8b03e) added Node shared-loop and release coverage. Initial Script red was an overstrict `changed==ordinal` assertion, invalid because the Script editor can emit a UI-only changed event on unchanged Apply; [`9be025f`](https://github.com/logiclrd/Heresy/commit/9be025f4fd63e837bd2c609c698de137fdcbac37) corrected the test and the Script release scenario passed. The initial Node test computed fade origin from the outgoing curve at the *next* frame; [`ecf5579`](https://github.com/logiclrd/Heresy/commit/ecf5579639c7c1e81006ee7278b0c001b5f8e060) corrected it to the frozen **last emitted PCM frame**. Corrected implementation-head [CI #43685741054](https://github.com/logiclrd/Heresy/actions/runs/43685741054) passed both new regressions, Release/package and ARM64. **No production code changed.**

**Not covered:** Script unbounded releases, complex expressions/indefinite note ends, releaseSeconds-dependent automation, nested Synth/Instrument/Filter/Multis, simultaneous/overlapping edits inside an existing fade, arbitrary multi-segment PingPong loop durations, hardware callback/deadline (C5) or user listening (C6). Keep C2 OPEN; C1/C3/C4 COMPLETE, C5–C7 and overall all-nonstructural Synth TODO unchecked. PR #12 remains OPEN/DRAFT/UNMERGED. Next C2(p) should audit Source/Instrument shared-editor deferrals and older-audio warning semantics; C2(q) completes the final property/control audit.


## C2(p) — Source selection during active fade; Instrument-specific inspector refresh and older-current status currency (2026-11-15)

**Two bounded integrated U/P/T/V/A regressions plus one production UI fix.** The actual `FmSynthEditorControl.AddSoundSourceInspector` selector and modeless Instrument Volume button exercise `SongPlaybackTransport` production frozen snapshots, detached child-before-dependent-parent compilation, `PlaybackRequestAudioSourceFactory` retained virtual voices, and the real `IFirPreparationAudioSource`/background `FirPreparationScheduler` producer mailbox. The test drives the output source manually instead of using SDL audio hardware. Publication counts, frozen SourceId and Instrument Volume fields, per-voice status currency, actual mixed PCM, one unchanged output session and frozen base snapshot are checked on the same path. No unsafe cross-family state migration is made eligible by these tests.

**C2(p) latest-only Source deferral under a still-sounding first fade.** In `FmSynthInspectorTransportPcmTests.SourceInspectorSampleSupersessionDefersOnlyLatestThroughRealTransport`, the real selector changes Sample **A=0.25→B=0.75** on a held looping Sample note. The first producer boundary preserves A until a detached worker finishes B's new onset. With A→B's **100-ms Source-local smoothstep still running**, it changes **B→C=0.875 at fade offset 25**, then **C→D=0.5 at fade offset 45**, without interrupting A/B histories. **Every** semantic selector change publishes one child-before-parent *Synth definition* revision (3 total), but only **B and latest D** ever receive a distinct worker-prepared Sample onset; **C** is superseded as an intended future Source and never contributes audible PCM. The original A→B continues all **101 frame-offset samples** unchanged, zero false older count; after its end the worker prepares D and the old note performs **101 more independently calculated B→D smoothstep samples**, with clean steady D afterward. The prepared onset count is exactly 2, scheduler has no fault, there are no phantom same-revision preparations. This proves the real inspector→transport path beyond C4(h)/(m)'s renderer-level latest-wins cases. It does **not** claim same-asset alias, simultaneous Source pitch-mode edits, Note Off mid-fade or arbitrary nested sources.

**Approved Sample→Instrument refusal with eligible newer Instrument Volume.** In `InstrumentSelectionRefusalRetainsOlderStatusAcrossCompatibleSharedVolumeEdit`, the real Source selector switches from an old looping **A=0.25 Sample** to a *different data Instrument* whose direct tone is a looping Sample **B=0.75** with shared Node Volume initially 1.0. The original A voice **must remain on its own Sample invocation/readhead**, not be retargeted to a different Instrument. After publication is consumed on the PCM boundary, **older count=1** and the old A remains audible at 0.25; a second independently owned newly started virtual note uses the selected Instrument and makes total output **0.25+0.75=1.0**. The actual modeless shared Volume button opens the persistent Node editor, which commits shared envelope **1.0→0.5**. Its global shared-object event plus Synth editor callback deduplicate to **one** new frozen child-before-parent revision; frozen worker evidence confirms the selected Instrument and new shared gain. The newer Instrument note alone adopts the eligible gain edit on its existing Sample history, for **101 exact mixed PCM samples** smoothly changing total **1.0→0.625**, while old A's independent PCM stays 0.25 and **older count remains 1**. A compatible edit to a newer sounding voice is never permitted to erase an unrelated old voice's missed-generation warning. Cutting only old A initiates the playback owner's short finite de-click ramp from **0.625 toward 0.375**; after it settles, **older count returns to zero** and the newer revised Instrument note still sounds at 0.375. Repeated same-value shared edit and identical transport publication do not produce a phantom revision. No device/hardware deadlines inferred.

**Concrete Source inspector UI bug and correction.** The first attempt at the mixed Source/Instrument case successfully reached correct old A and fresh Instrument output, but failed on the **absence of `Instrument tone Volume` controls immediately after SourceId selection**: `FmSynthEditorControl.UpdateSoundSource` changed the underlying graph and refreshed the canvas but did not rebuild the object-family-specific inspector. Manually reselecting the node happened to restore the controls, hiding a genuine UX defect. Production [`7bfe581`](https://github.com/logiclrd/Heresy/commit/7bfe5816b69aac9697bbe28d9558d1d553e927b9) now calls `RebuildInspector` **only after a successful changed SourceId**; pure frequency, exponential mode, Constant and other numeric field edits leave the inspector's current controls intact. The regression explicitly requires the newly selected Instrument's Volume picker/button to exist *immediately*, without reselecting the node, and uses them in the live held-note acceptance scenario. No DSP, transport, SourceId-admission or older-count production changes were required. The subsequent red test occurred only at the final cut, because the test expected immediate zero from `NoteCutCommand` despite the actual very short owner de-click ramp; test-only [`79c28fa`](https://github.com/logiclrd/Heresy/commit/79c28fac110adfdba510189959970944acd783d0) corrected it to require a finite monotonic ramp to the continuing Instrument-only PCM, with older status clearing on original retirement.

**Evidence:** [`93329a8`](https://github.com/logiclrd/Heresy/commit/93329a8a9d9357aaf3cff2ca6544ab911d808f0e) introduced latest-wins UI integration, exact-HEAD [CI #43686378328](https://github.com/logiclrd/Heresy/actions/runs/43686378328) **SUCCESS 3,789/3,789** (Core 910, Render.File 29, Render 1,213, Scripting 105, Playback 356, UI 1,176) and Release/package/ARM64. [`c1b301c`](https://github.com/logiclrd/Heresy/commit/c1b301c30d263e53f4663b606757048c24581e44) introduced cross-family refusal/shared Volume/current-status test; [`7bfe581`](https://github.com/logiclrd/Heresy/commit/7bfe5816b69aac9697bbe28d9558d1d553e927b9) fixed genuine inspector family refresh; [`79c28fa`](https://github.com/logiclrd/Heresy/commit/79c28fac110adfdba510189959970944acd783d0) corrected a test-only NoteCut declick assumption. Corrected exact implementation-head [CI #43686827300](https://github.com/logiclrd/Heresy/actions/runs/43686827300) **3,790/3,790 tests** (Core 910, Render.File 29, Render 1,213, Scripting 105, Playback 356, UI 1,177), both new UI regressions, Release/native desktop/Windows apphost/versioned bundle and Linux ARM64. **Production change isolated to Source inspector rebuilding on genuine SourceId changes.**

**Deferred scope unchanged:** Cross-family Instrument↔Sample/Synth substitution remains an approved **old-generation refusal**, not a compatible live graph migration. This slice does not claim new support for Script Instrument tones or arbitrary nested Instrument/Synth selection, release during active Source fade, same Sample alias/restart or Source+pitch simultaneous deferral; those have existing C1/C4 policy and separate coverage requirements. C5 device callback/deadline and C6 physical listening remain not done. C1, C3 and C4 COMPLETE, **C2 OPEN**, C5–C7 and overarching all-nonstructural Synth TODO unchecked; PR #12 stays OPEN/DRAFT/UNMERGED. **Next C2(q):** exhaustive remaining UI property/control audit, assigning each row to proven bounded acceptance, genuinely structural UI, approved deferral or concrete unverified follow-up before any closure decision.


## C2(q) — final existing inspector audit, discovered UI copy drift, and C2 closure decision (2026-11-15)

**Audit outcome: all 28 currently inventoried FmSynthEditorControl surface rows inspected against source; C2(q) AUDIT COMPLETE but C2 PROPERTY MATRIX NOT SIGNED OFF.** The detailed [C2(q) matrix section](synth-live-pr12-c2-inspector-matrix.md#c2q--final-existing-inspector-inventory-and-evidence-disposition-not-a-c2-closure-2026-11-15) gives each row a bounded integrated green, partial/open, structurally exempt, C1-approved incompatible substitution, presentation-only or no-current-property disposition, without replacing incomplete evidence with an inference. Sources of truth include the actual Synth inspector, the standalone shared Filter/Node/Script controls, FilterCatalogue descriptors, existing UI/transport/renderer tests, and **docs/todo.md's explicit requirement for every nonstructural editable property**. A test of one Filter kind, one Element mode, one unchanged graph, or one neutral pitch does not certify all other visible and semantically distinct values.

**Definite remaining whole-path gaps (Blocker A).** The **Operator Add↔Multiply** real ComboBox has direct-editor→session held PCM (C2(e)), but only Minimum/Maximum has integrated actual inspector→production transport→same held output-source PCM (C2(f)). The **shared Filter definition stage defaults** real editor commits and shared-event/worker ordering are covered by C2(c), but that test's production transport is a *recording backend*, while held Filter/IIR PCM uses a separately driven Session. No one integrated actual shared Filter editor→production output-source PCM test yet exists. Neither is a known DSP failure; both are missing exact evidence for currently editable properties. C2(r) should close these first, with exact child-before-parent preparation, no-op revision gating, per-voice currency, preserved filter histories, and output PCM.

**Current-property breadth gaps (Blockers B/C).** The Oscillator/Source **Constant** toggle in C2(k) was set at neutral effective note frequency and lacks a nonvacuous audible control signal showing mode difference. Two Multi presets (Unison and Chorus) are integrated; eight other named active presets remain individually unaudited at the production transport/PCM boundary. Existing Node/Script tests cover representative curve/loop/release expressions but not every complex timeline/expression. **Most substantially, the FilterCatalogue defines 22 FilterKind members, including 21 exposed kind choices, with numeric and choice descriptors beyond the five particular parameter/kind pairs integrated in C2(e/h/n)**: ShelfSlope, Resonance, Drive, Feedback, Feedforward, FIR band LowCutoffHz/HighCutoffHz, and various cutoff/Q defaults; additionally OnePole/legacy Mode, AllPass Order, Ladder Saturated Oversampling, and FIR Window/PhaseResponse have *choice* values not simply predeclared structural. These must receive a defensible effective-topology compatibility policy and active-voice PCM test, not be excluded merely for using a different DSP algorithm. Descriptor-declared structural **DelaySeconds, TapCount and TransitionWidthHz** and stage kind/topology/edge changes are separately exempt from live numeric adoption, subject to old-generation safety. An unbound static FIR cutoff test cannot certify FM-connected FIR preparation and high-load callback deadlines.

**Not property blockers:** Structural rewiring/Output change/Node creation, owned-modal New... success (an object creation UI workflow), canvas positions/waypoints, Noise Vmin/Vmax (not yet an editable property), and unrelated Instrument substitution explicitly deferred by C1. Those can still have UI requirements or separate downstream acceptance; C2 must not mislabel them nonstructural live edits. Hardware callback deadlines/overload belong to C5, human listening to C6, PR signoff to C7. A bounded green test may be relied on for its named edit, never broadened to untested or structurally incompatible cases.

**Actual UI defect fixed in the audit:** The FM Filter inspector text still asserted that Filter nodes passed audio unchanged and Tail was merely stored, contradicting the already shipped and C2(m/n)-tested renderer. Production [5f38e87](https://github.com/logiclrd/Heresy/commit/5f38e8706c76c2a7b00bf2b29d4a05a0f573e32b) now accurately describes running Filter DSP, numeric modulation/static overrides, residual Tail and structural old-generation behavior. [1480529](https://github.com/logiclrd/Heresy/commit/148052930663c5b1a2f05452cbcca636c5265ed9) adds a headless UI assertion guarding the wording. The correction changes no DSP, transport or Filter configuration semantics.

**Decision:** Mark only **C2(q) AUDIT COMPLETE**. Leave **C2 [ ]**, **C1/C3/C4 [x]**, **C5/C6/C7 [ ]**, overarching all-nonstructural Synth TODO unchecked; leave PR #12 **OPEN/DRAFT/UNMERGED**. The next bounded C2(r) is missing Operator Add/Multiply and shared Filter defaults whole-path evidence, followed by small explicit property-family continuations before another C2 closure review. Do not treat an arbitrary finite count of slices or the current passing CI as evidence of complete property coverage.

## C2(r) — missing production-path Operator and shared Filter defaults acceptance (2026-11-15)

**Result:** The two *actual missing whole-path* cases from C2(q) Blocker A are now covered by test-only, integrated U/P/T/V/A regression; the broad **C2 gate is still OPEN**. [C2(r) detailed matrix](synth-live-pr12-c2-inspector-matrix.md#c2r--actual-operator-addmultiply-and-shared-filter-defaults-through-the-same-production-pcm-path-2026-11-15) updates the historical C2(q) disposition.

- `FmSynthInspectorTransportPcmTests.OperatorAddMultiplyInspectorRetunesSameProductionVoiceBothDirections`: The actual inspector's Operation ComboBox commits **Add→Multiply→Add** with independent nonneutral Constant inputs **0.2/0.4**. `SongPlaybackTransport` freezes one revision per real edit and prepares the child before its dependent parent. The original production output source then emits two **101-frame** smoothsteps **0.6→0.08→0.6**, preserving authored DC, the running voice and output session, with no false older mark or phantom no-op/double publication.
- `FmSynthInspectorTransportPcmTests.SharedFilterDefaultsEditorRetunesOriginalProductionIirHistory`: Real standalone shared `FilterEditorControl` applies **OnePole CutoffHz 165→45** through the workspace's shared-object event and the production shared-publication transport, child before parent. The same running production output is verified for **160 initial and 1,600 continuing PCM frames** against independently authored, continuing OnePole IIR/oscillator state; a never-edited state differs audibly, and old IIR history, snapshot, one session and current-voice status are preserved. Invalid `NaN`, identical Apply and duplicate shared publication are non-audio no-ops.

**Implementation provenance:** [`c12ff9f`](https://github.com/logiclrd/Heresy/commit/c12ff9f06c991430ae614731c004944b37eeaac1) introduced the two tests, and its [first CI #43688466462](https://github.com/logiclrd/Heresy/actions/runs/43688466462) failed on a **test-only** assumption that every dependent parent preparation must contain its child's changed Operator/Filter metadata. [`3329e9c`](https://github.com/logiclrd/Heresy/commit/3329e9cf1c285212e2846b586cb43ccffb146457) scopes field assertions to the child while checking the parent's publication and identical audio revision. Final exact implementation-HEAD [CI #43688569885](https://github.com/logiclrd/Heresy/actions/runs/43688569885) **SUCCESS, 3,792/3,792 tests** (Core 910, Render.File 29, Render 1,213, Scripting 105, Playback 356, UI 1,179); both named tests, Release/native desktop/Windows apphost/versioned release-bundle and Linux ARM64 all PASS. **Only test code changed; no renderer, transport, interface behavior or production compatibility was modified.**

**Decision:** Close **only the bounded C2(q) Blocker A missing integration evidence**. Current nonneutral Oscillator/Source Constant toggles, eight remaining named Multi presets, complex shared Envelope/Script states and Source/Instrument selector dispositions are still C2 **Blocker B**. The **22-kind FilterCatalogue numeric/choice descriptor breadth and FM-connected FIR** remain substantial C2 **Blocker C**; a shared unmodulated OnePole default does not close those families. C5 actual hardware callback/deadline and underrun metrics, C6 physical listening, C7 signoff and docs/todo.md all-properties checkbox stay OPEN. C1/C3/C4 accepted; **C2 [ ]**, **C5/C6/C7 [ ]**, PR #12 **OPEN/DRAFT/UNMERGED**. Proposed next bounded **C2(s)** is the nonneutral Source/Oscillator Constant-mode full-path probe; leave preset and Filter catalogue breadth separate.

## C2(s) — audible nonneutral Constant controls, U/P/T/V/A accepted (2026-11-15)

**Two new test-only, nonvacuous whole-path regressions resolve the exact Oscillator/Source Constant-mode gap identified by C2(q), without erasing the existing neutral C2(k) coverage.** Each starts one original note with **PitchMultiplier=1.5** and unchanged connected **Constant=2** FM control; the actual inspector's Constant box toggles false→true→false, so the effective node's outer pitch factor changes **3×→2×→3×** while input FM continues. The complete real editor→document revision→`SongPlaybackTransport` child-before-parent frozen worker→`PlaybackRequestAudioSourceFactory` retained PCM path is verified, not a standalone renderer-only callback or a recording transport.

- `OscillatorConstantInspectorWithNonneutralPitchPreservesHeldPhase`: continuing 80-Hz Sine with 2× FM shifts effective **240→160→240 Hz** at the real checkbox edits, preserving oscillator phase across two 141-frame output segments and a 23-frame initial history. Production PCM agrees frame by frame with a separately authored ongoing Session; the original never-edited note differs by >0.08 in each segment, so numeric Boolean publication alone cannot pass.
- `SampleSourceConstantInspectorWithNonneutralPitchRetainsIntegratedReadhead`: real imported 16-frame nonuniform looping Sample changes pitch integration **3×→2×→3×**, without re-onset or retroactive position rescale. All 17 initial and 282 continuing production frames agree with a separately initialized and continuously maintained SampleSound/Session, while a never-edited Sample reference differs by >0.08 in each segment. Current-generation count remains 0, no output restart/snapshot-base change, and repeated unchanged/direct publication is suppressed.

**Provenance:** [`84d2d71`](https://github.com/logiclrd/Heresy/commit/84d2d71803ed77c047cd8e2cc763e084f6be625d), tests only; exact implementation-head [CI #43689242092](https://github.com/logiclrd/Heresy/actions/runs/43689242092) **SUCCESS, 3,794/3,794** (Core 910, Render.File 29, Render 1,213, Scripting 105, Playback 356, UI 1,181), both new named tests PASS, Release/native desktop/Windows apphost/versioned bundle/Linux ARM64 PASS. No production DSP/UI/transport changes.

**Decision:** The two specifically missing *nonneutral Constant mode* surface-value proofs are green. The eight still-unverified Multi named presets, more complex Node/Script Envelope states and remaining Source/Instrument lifetime/selector dispositions still belong to C2(q) **Blocker B**. The FilterCatalogue's broader numeric/choice parameter kinds and FM-bound FIR controls remain **Blocker C**. These test-only paths do not establish arbitrary nested-graph proof, release semantics, physical hardware deadlines (C5) or listening (C6). C1/C3/C4 accepted; **C2 OPEN**, C5/C6/C7 and overarching all-property TODO OPEN, PR #12 **OPEN/DRAFT/UNMERGED**. Next bounded **C2(t)**: eight remaining Multi preset actions or exact descriptor aliases through production inspector and PCM, with explicit worker-onset versus headroom expectations.

## C2(t) — eight remaining Multi preset actions, independent held-PCM acceptance (2026-11-15)

**Eight previously unverified named UI preset actions are now integrated end-to-end.** `FmSynthInspectorTransportPcmTests.RemainingEightMultiPresetsPublishThroughTransportWithRetainedProductionPcm` starts one Sine→Multi(7) sounding voice, selects all eight remaining `FmMultiNode.Presets` through the real one-shot inspector ComboBox, and drives actual `SongPlaybackTransport` frozen child-before-parent compilation into `PlaybackRequestAudioSourceFactory` PCM. Per-preset expected `FmMultiNode` configuration and compiled ratios are authored **independently of `WithPreset`**. The cases are: **Super saw/super square** (Spread7, detune12); **Power Chord** (harmonics 1 3/2 2); **Major Third/Sixth/Triad** (0 4 / 0 9 / 0 4 7); **Minor Third/Sixth/Triad** (0 3 / 0 8 / 0 3 7). All reset phase randomization on and use correct distribution-specific gain-compensation policy; unmodified fields retain their previous values. The earlier C2(l) integrated Unison and Chorus tests make this **10/10 present named presets covered individually**, not all Multi composition types accepted.

**PCM and worker continuity:** One original note supplies **15 initial + 8×141 continuing PCM frames**, compared against a separately initialized and continuously migrated reference with its own Multi children. All cases are independently nonvacuous against a third never-edited running note (>0.025 maximum difference), with one session, unchanged immutable base playback snapshot, zero false older-audio voices and exactly one child-before-parent semantic revision per preset. The initial Super saw ratio change 40→12 cents alongside enabling randomized phase is *not* an unchanged-ratio rephase; the original onset is preserved while compatible ratios retune. Following fully completed retirement of child #3 during two-voice chord states, **Major Triad** and **Minor Triad** each require worker-prepared fresh third-child invocations on both production and reference `FirPreparationScheduler` paths, preserving continuing sibling histories. Do not infer PCM managed-allocation measurements or real device deadlines from this manually driven backend test.

**Evidence:** First test-only [`6f180c6`](https://github.com/logiclrd/Heresy/commit/6f180c6cbaffd1aac038fbaa2696c78695c386ac) failed once on an overstrict *test* assumption requiring a Super saw worker rephase despite changed ratios ([CI #43690280851](https://github.com/logiclrd/Heresy/actions/runs/43690280851)). Corrected test-only [`4e316bf`](https://github.com/logiclrd/Heresy/commit/4e316bff13ca8c88ee03f02841eb1d40081a0646) leaves both actual post-retirement worker assertions strict. Its exact implementation-HEAD [CI #43690461475](https://github.com/logiclrd/Heresy/actions/runs/43690461475) **SUCCESS, 3,795/3,795 tests** (Core 910, Render.File 29, Render 1,213, Scripting 105, Playback 356, UI 1,182), Release/native desktop/Windows apphost/versioned bundle/Linux ARM64 all PASS. No production DSP/editor/transport code changed.

**Decision:** The last eight named Multi preset *actions* in C2(q) Blocker B are now proved with U/P/T/V/A for these exact graphs. Remaining Blocker B: time-dependent/multi-point shared Node and Script Envelope, and Source/Instrument selector/lifecycle dispositions; Blocker C: FilterCatalogue parameter/choice breadth and FM-bound FIR. C1/C3/C4 accepted, **C2 OPEN**, C5 hardware/underrun, C6 listening, C7 signoff and overarching all-properties TODO still OPEN; PR #12 **OPEN/DRAFT/UNMERGED**. Next bounded **C2(u)**: time-dependent Script and multi-point Node sustain/release coverage through real editor/transport PCM.

## C2(u) — larger time-dependent Script and five-point Node shared Envelope batch (2026-11-15)

**C2(q) Blocker B's nontrivial Envelope gap: accepted for two large explicit cases, without declaring all possible Envelopes accepted.** Two new integration tests in `FmSynthSharedNodeVolumeTransportPcmTests` use *real* embedded Script/Node editors → exact shared-object notifications and one semantic revision → frozen `SongPlaybackTransport` child-before-dependent-parent preparations → the original continuing `PlaybackRequestAudioSourceFactory` PCM from a manual backend. They neither restart the original voice for an edit nor claim a physical device callback.

- **`SharedScriptTimeVaryingExpressionsAndReleaseUseOriginalProductionClock`**: initial `0.2 + 0.5*elapsedSeconds` (31 live frames), then three real shared held edits (dynamic linear expression, conditional `releaseSeconds` expression, release limit **80→120ms**) each with a **101-frame** last-audible-sample-frozen analytic smoothstep anchored to the *original note age*, frozen worker revision/order and deduplication. Two expression edits are independently nonvacuous. Note Off executes an explicitly time-varying `releaseSeconds` branch to its declared 120-ms exclusive bound; a later edit to a different release expression and **30ms** cannot steal that older release. Older=1 while the old generation sounds; new Note On gets the new Script and older status clears on natural retirement. Invalid source and identical Apply are rejected/suppressed.
- **`SharedMultipointNodeShapeAndSinglePointSustainUseHeldClockAndPreserveRelease`**: five-node **PingPong(1..3)** initial curve plus three actual live commits (**multi-point value edits; multiple timestamp changes plus end 3→2; Forward single-point sustain 2..2**). All initial and 3×101 continuing frames agree frame by frame with a separately frozen `NodeEnvelopeCurve` evaluated at the original active frame, using the established 100-ms last-audible-sample transition; each edit is nonvacuous, no duplicate shared publication or false older count. After Note Off, the single-point sustain's original forward release is retained despite a subsequent editor change to initial gain and final node time **200→280ms**; a new note sees the updated definition, and older=1 clears on original retirement.

**Exact implementation evidence:** Test-only [`9bcfb2d`](https://github.com/logiclrd/Heresy/commit/9bcfb2dd3823042f3dbd45bed190d65bba09803f), [CI #43691180192](https://github.com/logiclrd/Heresy/actions/runs/43691180192) **COMPLETED SUCCESS — 3,797/3,797 tests** (910 Core, 29 Render.File, 1,213 Render, 105 Scripting, 356 Playback, 1,184 UI), including both new tests, full Release/Windows apphost/versioned bundle/Linux ARM64. No production DSP/editor/transport changes.

**Boundary/next:** The exact time-varying Script and multi-point Node cases are newly green; these do not prove arbitrary Script execution, unbounded release, every Node loop/reversal, nested effects, or physical callback/listening. C2(q) **Blocker B** still contains Source/Instrument selector/lifecycle/property gaps, and **Blocker C** still contains the large FilterCatalogue numeric/choice/FM-bound FIR matrix. Keep **C2 OPEN**; C1/C3/C4 accepted, C5 hardware, C6 listening, C7 final authorization and overarching Synth TODO OPEN; PR #12 **OPEN/DRAFT/UNMERGED**. Prefer batching remaining Source/Instrument cases into C2(v) and then Filter kinds by compatible DSP family rather than individual-kind slices.

## C2(v) — grouped Source/Instrument selector lifecycles through real continuing PCM (2026-11-15)

**Two bounded integrated Source selector-lifetime cases accepted, not arbitrary cross-family migration.** `FmSynthInspectorTransportPcmTests.SourceNoneSampleNoneSameSampleReentryPreservesOneHeldProductionVoice` exercises `Constant(0.125)+Source(None)` on a held note with a real Source inspector choice cycle **None→Sample→None→the same Sample**. The newly selected looping 0.5 Sample cannot start on the producer's first PCM boundary; the worker prepares the fresh invocation before the **0.125→0.625** 101-frame fade. Selecting None produces a worker-free **0.625→0.125** 101-frame fade without losing the anchored note. Re-selecting the same asset after its old fade completes requires a **second off-PCM Sample onset** (not recycling the retired readhead), followed by another 101-frame fade. Actual document revisions and frozen child-before-dependent-parent transport publications are exactly three, older count remains zero, duplicate publications do nothing, and the output session never reopens.

`SourceSampleSynthDataAndScriptInstrumentSelectionsRetainOlderGenerations` exercises **Sample (0.125)→referenced Synth (0.25)→data Instrument (0.25)→scripted Instrument (0.1875)** through the real Source ComboBox, immutable production worker publication and the same ongoing PCM. Existing voices maintain their original incompatible runtime histories rather than migrating cross-family, with older count **1→2→3**; new Note On commands use the most recently selected family while old mixed PCM remains intact. The resulting simultaneous mixture is **0.8125**. Independent NoteCut owner operations remove the three older generations under the existing short de-click ramp, returning older=0 and leaving only the **0.1875** current scripted Instrument. This tests the previously unconfirmed ScriptInstrument selector family as an **approved old-generation-retention** case, not ScriptInstrument tone-expression editing or unsupported live substitution.

**Implementation/CI:** Test-only [`3fd1ff6`](https://github.com/logiclrd/Heresy/commit/3fd1ff678cf2c012954d692b05e4739ce141e360), no DSP/UI/transport code changes. Exact implementation-head [CI #43691883903](https://github.com/logiclrd/Heresy/actions/runs/43691883903) **COMPLETED SUCCESS — 3,799/3,799 tests** (Core 910, Render.File 29, Render 1,213, Scripting 105, Playback 356, UI 1,186), Release/native/Windows apphost/versioned release bundle/Linux ARM64 all passed. The prior chat stopped before sending its completion response or recording this acceptance; the commit and CI did persist remotely and have now been independently verified.

**Remaining boundaries:** Source selector None/Sample reentry and this four-family cross-substitution are covered. Current Source/Instrument residuals (nested Script Instrument tone or shared Script Volume editing, NNA and already-releasing interactions) require a separately scoped compatibility/lifetime disposition where relevant; no unsupported mutable-state transfer is authorized. **C2 remains OPEN** principally for FilterCatalogue numeric/choice families and FM-bound FIR delivery. C1/C3/C4 accepted; C5 physical callback performance, C6 listening, C7 final signoff and the overarching Synth TODO remain OPEN; PR #12 **OPEN/DRAFT/UNMERGED**. Continue with a **multi-kind Filter family batch** rather than serial single-kind test slices.

## C2(w) — family-batched OnePole + four SVF kinds and deliberate legacy RC refusal (2026-11-15)

**C2(q) Blocker C: ten numeric inspector fields tested end-to-end across five kinds; sixth legacy RC kind has explicit safe refusal instead of bogus live adoption. C2 as a whole is OPEN.** Two new `FmSynthInspectorTransportPcmTests` regressions create a real manually-driven continuing `PlaybackRequestAudioSourceFactory`, drive actual per-use Filter inspector controls and publication callbacks, require exact one-per-edit frozen child-before-dependent-parent `SongPlaybackTransport` compilation, and check sample-by-sample PCM and older-audio status against separate never-restarted `PlaybackSession` histories.

- **`OnePoleAndFourSvfKindsRetuneMultipleRealInspectorParametersOnHeldPcm`:** five exact filter kinds — **OnePole, SvfLowPass, SvfHighPass, SvfBandPass and SvfNotch** — each run with 8-kHz output, 220-Hz Sine, initial CutoffHz=180, 225 independently checked initial frames, then two per-use edits with **960 original-note PCM frames** per edit. All five kinds change CutoffHz **180→380**; OnePole then **380→90**, each SVF then Q **0.7071067811865476→6.5**. Each of **ten** edits matches a separately authored and continuously running migrated Filter reference (not a new note), differs by >0.012 from the third untouched independent history, publishes child before parent exactly once per audio revision, preserves IIR history, keeps older=0 and never reopens the output session. Duplicate Apply/revision publication is suppressed. Extra inspector view-refresh messages must not be mistaken for semantic callbacks; that overstrict assertion caused the initial red CI and was corrected test-only.
- **`LegacyRcCutoffAndModeRefuseOldStateButNewNoteUsesBothChoices`:** `SpeakerLegacyRc` is an explicitly legacy/output-speaker recurrence and currently lacks a realtime `CanUpdateKind` migration path, despite exposing CutoffHz and Mode through an existing compatible Filter definition's inspector. The real editor changes **CutoffHz 180→380** then **Mode LowPass→HighPass** while the original voice is sounding. The original preserves its exact unchanged RC LowPass PCM and stays **older=1** across both publications; a newly started note uses both new values and its independently rendered HighPass response joins the old voice for 240 exact mixed frames; cutting the older owner returns older=0. This is a verified **refusal**, not a claim of live compatibility or approval to exclude these nonstructural controls from final TODO signoff.

**Specific remaining Choice gap:** OnePole's LowPass↔HighPass `Mode` remains unverified for live voice adoption. The current TPT realtime update payload carries numeric cutoff/Q but no Mode; this must be addressed explicitly rather than assumed green from the two numeric OnePole cases. Other Filter families' numeric/choice and connected FIR cases remain in C2(q) Blocker C. Physical callback, underruns and listening remain C5/C6.

**Implementation provenance:** test-only [`6846ba5`](https://github.com/logiclrd/Heresy/commit/6846ba589124144346a1ccc9b2f44e3eb1dbc944), [`631e302`](https://github.com/logiclrd/Heresy/commit/631e302b383f3f00840520686ffa04de423d60f7), corrected inspector refresh assertion [`5539436`](https://github.com/logiclrd/Heresy/commit/5539436f616b3bea89b3b258698e2d923f304a89); no production DSP, UI or transport changes. Corrected implementation-head [CI #43695745564](https://github.com/logiclrd/Heresy/actions/runs/43695745564) has passed Release tests, desktop runtime checks and ARM64, with the package smoke completion recorded by GitHub when available. Two extra UI tests raise the test-suite inventory to **3,801 total**. Keep **C2 OPEN**, PR #12 OPEN/DRAFT/UNMERGED, C1/C3/C4 accepted and C5–C7 plus overarching todo unchecked. Next larger batch: seven Biquad kinds and their Cutoff/Q/Gain/ShelfSlope families, with choice family disposition separate.

## C2(x) — seven-kind Biquad numeric property batch, independent retained-PCM acceptance (2026-11-15)

**C2(q) Filter Blocker C: all seven Biquad catalogue kinds' individual numeric inspector properties accepted end-to-end; C2 itself remains OPEN.** Test-only `FmSynthInspectorTransportPcmTests.AllSevenBiquadFamiliesRetuneTheirDistinctInspectorParametersWithRetainedPcm` enumerates **LowPass, HighPass, BandPass, Notch** (CutoffHz 180→380; Q 0.7071067811865476→6.5); **Peaking** (GainDb 0→12, CutoffHz 180→380, Q→2.5); and **LowShelf / HighShelf** (GainDb 0→12, CutoffHz 180→380, ShelfSlope 1→0.4). That is **17** distinct real per-use numeric inspector changes covering every FilterCatalogue numeric descriptor in each of **seven** kinds, with the gain-based families changed nonvacuously from their 0-dB initial response.

Each case builds a separate **real Filter inspector → exactly one DocumentWorkspace semantic audio/document revision and one Synth publish callback → immutable production child-before-dependent-parent worker compilation → original held `PlaybackRequestAudioSourceFactory` PCM**. Each uses a third-party-independent *authoring path* for a separately instantiated and continually migrated reference `PlaybackSession` Biquad history (same DSP implementation, not a mathematical independent filter); a third never-edited history verifies every edit changes PCM (>0.008). **1,680 initial and 16,320 post-edit sample frames** check full continuing PCM including the standard **32-frame coefficient interpolation**, without restarting notes, losing integrator histories, or false older-audio status. Every per-kind invalid `NaN` draft is rejected before semantic publication; duplicate Apply and exact-revision publication remain no-ops. Single output session, unchanged base-song snapshot, older count 0 throughout. Does **not** claim all Biquad FM-bound/release/tree interactions, structural changes or physical audio callback deadlines.

**Exact implementation evidence:** [`7527152`](https://github.com/logiclrd/Heresy/commit/7527152f490c564f998533006b4a271a752d53c6) **test-only, no production changes**; [CI #43697204053](https://github.com/logiclrd/Heresy/actions/runs/43697204053) **COMPLETED SUCCESS**, **3,802/3,802 tests** (Core 910, Render.File 29, Render 1,213, Scripting 105, Playback 356, UI 1,189), Release tests/Windows apphost/versioned release bundle/Linux ARM64 all green; the named seven-kind regression passed.

**Remaining boundaries and sequence:** Filter Blocker C still requires LadderLinear/LadderSaturated, AllPass and Comb numeric/choice policy, all FIR families including FM-bound worker kernel preparation and nonstructural `Window`/`PhaseResponse`, and the OnePole LowPass/HighPass Mode-choice admission issue. Legacy RC's existing refusal remains safe but not live completion. Other residual Source/Instrument/Envelope cases stay explicitly bounded, not silently accepted. Prefer **C2(y)** as one larger group for Ladder, AllPass and both Comb types, then isolate specialized FIR and unresolved choice semantics. **C2 OPEN; C1/C3/C4 accepted; C5 hardware, C6 listening, C7 final signoff and overarching all-nonstructural Synth TODO OPEN; PR #12 OPEN/DRAFT/UNMERGED.**

## C2(y) — five-kind Ladder, AllPass and Comb numeric+structural inspector batch (2026-11-15)

**C2(q) Filter Blocker C: all live-eligible unbound numeric fields of five additional kinds accepted on original running PCM; four relevant structural changes verified for exact older-audio refusal and new-note application. C2 remains OPEN.** Two `FmSynthInspectorTransportPcmTests` regressions use the real `FilterInstanceEditorControl`, one semantic DocumentWorkspace revision/callback, frozen child-before-dependent-parent worker publication, the existing manually driven production `PlaybackRequestAudioSourceFactory`, and independent continuously running oracle/unchanged histories.

- **`LadderAllPassAndCombFamiliesRetuneAllNonstructuralNumericInspectorParameters`:** `LadderLinear` **CutoffHz 180→380/Resonance .25→.75**, `LadderSaturated` **Drive 1→3/CutoffHz 180→380/Resonance .25→.75**, `AllPass` **CutoffHz 180→380**, `CombFeedback` **Feedback .5→.8/Mix .5→.85**, `CombFeedforward` **Feedforward .5→−.5/Mix .5→.85**. **Ten** edits, checked against the exact `FilterCatalogue.IsModulatable` descriptor inventory of all five kinds. Each uses 240 original PCM frames + 960 per edit against separately authored same-history live revisions and a third unchanged nonvacuous PCM baseline (>0.004), with older=0, exact frozen dependent order/revisions and one continuously open audio session. NaN first-field draft and duplicate Apply/publication produce zero extra work. Existing test-only `TrackingFactory` assumed every Filter has `CutoffHz`, and was corrected to record null when legitimately inspecting either standalone Comb kind.
- **`LadderOversamplingAllPassOrderAndBothCombDelaysRefuseStructuralLiveTransfer`:** **Saturated Ladder Oversampling 2x→4x; AllPass Order 1→2; feedback Comb DelaySeconds .02→.035; feedforward Comb DelaySeconds .02→.035**. All descriptors explicitly assert `IsStructural=true`. Each original voice keeps its unchanged first 260 and subsequent 240 PCM frames from a never-edited independent Filter when the real inspector revises its structural plan. It becomes older=1, while a new note uses the changed plan and **960** mixed frames agree with separately authored older and revised-new live voices; a third fresh unedited Filter proves each structural change changes actual PCM. After cutting only the old owner, older=0; duplicate publisher is no-op. These are intended **incompatible-generation retention** cases, not promises to live-retune choice/delay state.

**Test-only provenance:** [`2d7f689`](https://github.com/logiclrd/Heresy/commit/2d7f6893a5450ffb6de7b346771f7f889c1b5dea), [`9ea49bd`](https://github.com/logiclrd/Heresy/commit/9ea49bd283cdfee7a3403313f0b97c405ea21ec9); no production DSP/UI/transport edits. The separate [numeric CI #43720785277](https://github.com/logiclrd/Heresy/actions/runs/43720785277) and [combined CI #43720854367](https://github.com/logiclrd/Heresy/actions/runs/43720854367) passed their Release suites and Linux ARM64; the final combined suite comprises **3,804 tests** (Core 910, Render.File 29, Render 1,213, Scripting 105, Playback 356, UI 1,191). Exact packaging completion is reported by linked GitHub Actions.

**Status / next:** Filter Descriptor choices are **all structurally classified**, including unverified `OnePole.Mode`, Ladder Oversampling, AllPass Order and FIR Window/PhaseResponse; structural tests here verify refusal and fresh onset, not live state migration. Remaining C2 Blocker C: **four FIR kinds** with unbound and FM-bound prepared-kernel controls plus specific `OnePole.Mode` direct structural acceptance; other Source/Instrument/Envelope boundaries remain explicit. C1/C3/C4 accepted; **C2 OPEN**; C5/C6/C7 and all-nonstructural TODO OPEN; PR #12 OPEN/DRAFT/UNMERGED.

## C2(z) — all four FIR numeric/structural/FM-bound inspector to retained production PCM (2026-11-15)

**C2(q) Filter Blocker C four-kind FIR coverage accepted at three specific integration layers; C2 overall OPEN.** All FIR cases use real editor controls and exact DocumentWorkspace audio/document revisions, one Synth transport callback and child-before-dependent-parent frozen source preparation, plus deterministic continuous production `PlaybackRequestAudioSourceFactory` PCM against separately prewarmed/frozen FIR plan states. No production source changes.

- **`AllFourFirKindsRetuneEveryNumericInspectorCutoffOnContinuingPreparedPcm`** enumerates **FirLowPass/HighPass/BandPass/Notch**, all **six** unbound numeric cutoff descriptors (`CutoffHz` for low/high; both `LowCutoffHz` and `HighCutoffHz` for band/notch), asserts exact `FilterCatalogue.IsModulatable` property inventory, uses a 575-Hz square oscillator at 8 kHz, and compares **960 initial + 5,760 post-edit PCM frames** with an independent continually migrated FIR state and a third untouched reference (>0.004 changed PCM). All original voices remain current, independent offline kernels preserve convolution history and 32-frame kernel transitions, invalid numeric drafts and no-op reapply do not publish.
- **`AllFourFirKindsWindowPhaseTapAndWidthRefuseStructuralLiveMigration`** uses **ten** real inspector cases: one `Window` and one `PhaseResponse` choice for each kind, plus `TapCount 127→63` (FirLowPass) and `TransitionWidthHz 200→350` (FirNotch). All descriptors explicitly structural. Existing notes retain exact independent old FIR PCM and older=1, while newly started notes use separately prepared revised kernels; **260 initial, 240 retained old and 960 independent old+new mixed frames** per case. Scoped cutting of original voice clears older status; invalid numeric structural drafts and duplicate Apply/pub do no extra work. These are deliberate **structural old-generation refusals**, not live coefficient migration.
- **`FourFmBoundFirCutoffsDeferLiveKernelAdmissionUntilOffPcmWorkerPreparation`** connects the actual graph's cutoff/low-edge FM port to a Constant and edits its value through the real **Synth Constant inspector** for **all four** kinds. The first PCM frame after detached publication uses the original kernel; producer/reference separately submit exact captured mailbox requests through an **off-PCM `FirPreparationScheduler`**, require two worker preparations, each adopts its own kernel, then **960 production PCM frames per kind** equal a separately authored continuously sounding original-history reference while differing from a third untouched note (>0.004), older=0 and no backend reopen. This proves a controlled FM-bound worker handoff, not cancellation of raced/stale mailbox results or hardware latency.

**Provenance:** Test-only [`bc79ec6`](https://github.com/logiclrd/Heresy/commit/bc79ec6945177c69f3034000e5dadb03ebb7dc96), [`12d01e1`](https://github.com/logiclrd/Heresy/commit/12d01e19ca78a653a7228b7e4fe5cbd29dab32d3), [`72311bc`](https://github.com/logiclrd/Heresy/commit/72311bca95371869e4bb7db8b567ab78ce37dfcb). All three integration test suites' Release test steps passed, Linux ARM64 validated; see [numeric CI #43721570948](https://github.com/logiclrd/Heresy/actions/runs/43721570948), [structural CI #43721640741](https://github.com/logiclrd/Heresy/actions/runs/43721640741), and [combined FM-bound CI #43721783397](https://github.com/logiclrd/Heresy/actions/runs/43721783397), with final packaging verdict available on those runs. Suite inventory now **3,807 tests** (Core 910, Render.File 29, Render 1,213, Scripting 105, Playback 356, UI 1,194). No production DSP/UI/transport/worker edits.

**Remaining:** Exact OnePole `Mode` structural choice/refusal-new-voice case, plus final C2 residual inventory across Source/Instrument/Envelope/Filter and FM-bound concurrency qualification; do not broaden specific case evidence to unsupported graph rewires, arbitrary lifetime edits, race cancellation or physical callback performance. C1/C3/C4 accepted; **C2 OPEN**, C5 hardware, C6 listening, C7 final signoff and overarching Synth TODO OPEN; PR #12 OPEN/DRAFT/UNMERGED. Next C2(aa): explicit OnePole Mode test and a closing descriptor/status audit.

## C2(aa) — OnePole Mode two-way structural-refusal regression and C2 closing audit disposition (2026-11-15)

**The exact current OnePole Mode LowPass↔HighPass choice behavior is integrated-verified, but C2 remains OPEN after a 28-surface roll-forward.** New test-only `FmSynthInspectorTransportPcmTests.OnePoleModeBothDirectionsRetainOldIirAndUseNewChoiceOnFreshNotes` runs two actual per-use Mode inspector edits, **LowPass→HighPass and HighPass→LowPass**, from an explicitly configured 180-Hz OnePole under a 1,373-Hz Sine at 8 kHz. In EACH direction, the original source retains the independently authored IIR history for **260 initial and 240 after-publication PCM frames**, the changed inspector makes exactly one semantic audio/document revision and one frozen child-before-dependent-parent worker publication, and the existing sounding note becomes **older=1** (not a fabricated live Mode migration). Starting a fresh note emits **960 exact mixed old+new PCM frames**, agreeing with separately authored original and opposite-mode fresh instances, with a third fresh unmodified response confirming the changed Mode is nonvacuously audible. Cutting only the older owner clears older status; redundant Apply/publish does no work. This proves **safe generation isolation and fresh-onset routing under the current descriptor classification**, not that structural refusal is necessarily the optimal user-facing behavior.

**Test-only provenance:** initial [`e53868d`](https://github.com/logiclrd/Heresy/commit/e53868d796a675507762dc8b3b0c7c0a9a31d05a) had a CS1061 test assertion (attempt to call `GetParameter` on an `IReadOnlyDictionary`); corrected test-only [`aa4f314`](https://github.com/logiclrd/Heresy/commit/aa4f3146d57d197196a6441ae9d063340df2c60c) indexes `["Mode"]`. No production DSP/UI/transport changes. Corrected test-head [CI #43722907900](https://github.com/logiclrd/Heresy/actions/runs/43722907900) **Release suite and ARM64 PASS** (check linked run for final bundle status). The suite has **3,808** tests after this new UI regression.

**Source-level C2 reconciliation:** The [C2 inspector matrix](synth-live-pr12-c2-inspector-matrix.md#c2aa--real-onepole-mode-both-directions-and-28-surface-c2-closing-reconciliation-2026-11-15) now rolls the original **28/28** C2(q) inspector surfaces forward through C2(r)–C2(aa) with explicit G (bounded U/P/T/V/A), S (current structural), D (C1 approved substitution deferral), P (named residual value/lifecycle), N (no current field), and X (presentation only), rather than reporting stale C2(q) OPEN cells as current gaps. All **21 selectable Filter kinds × 42 eligible numeric descriptor slots** have actual per-use/transport/continuous-PCM examples from C2(w/x/y/z). Every current Filter Choice is **declared IsStructural=true by FilterParameterDescriptor.Choice**; original C2(q)'s contrary choice-language is retained as historical input, not treated as current truth. C2(y/z/aa), plus C2(w) legacy RC Mode, prove their bounded safe-old-generation outcomes. Script/Node time-dependent expressions, Source/Instrument family switching, all ten Multi presets, shared Filter default and Operator Add/Multiply live cases also have newer integrated evidence.

**Why C2 is NOT signed off yet — three substantive eligibility decisions:**
1. **SpeakerLegacyRc.CutoffHz**: excluded from new authoring menu but reachable in an existing legacy Filter, this descriptor remains nonstructural numeric. C2(w) verified safe old-generation refusal, not a live cutoff retune. It needs compatible state migration or explicit user-approved legacy exemption; do not claim the todo passed by menu omission.
2. **Choice runtime-topology correctness**: Choice is marked structural by descriptor *type*, but OnePole LowPass/HighPass shares a scalar TPT integrator and changes only its selected output. Same-tap-count FIR Window/PhaseResponse can potentially retain convolution memory while swapping a prepared kernel. The C1 requirement makes structural meaning a DSP/runtime compatibility decision, **not a blanket Choice-type escape hatch**. Determine where the current refusal is justified versus where a click-safe prepared live transition is owed; preserve the already-green refusal tests unless intentionally changing accepted semantics.
3. **Eligible reachability and lifecycle of shared Script/Instrument properties**: C2(g/p/v) covers direct data Instrument shared Volume and approved unrelated Source-family retention, C2(o/u) covers finite time-dependent Node/Script Envelopes and releasing refusal. It has not proven a currently reachable same-bound Script Instrument/shared Script Volume live-editor path or an unbounded Script release after live edits. First classify current UI reachability and exact eligible lifetime; only add a focused integrated regression if those are active current properties. Generic arbitrary combinations, unavailable nested child editors and future Pattern/Sequence sources are *not* silently made part of PR #12.

**Deferrals owned elsewhere:** C3 compound graph and C4 interruptions/short-EOF/NNA are already bounded accepted; do not reopen them without a specific new defect. C5 owns callback deadlines, severe Multi CPU excursions and recovery; C6 owns physical listening on Linux/Windows. C7 and the user own final overall TODO/merge authorization. The total suite passing and each named refusal test are evidence but not a substitute for these remaining decisions.

**Disposition:** C1/C3/C4 accepted; **C2 OPEN**; C5/C6/C7 and overarching all-nonstructural Synth TODO OPEN, PR #12 **OPEN/DRAFT/UNMERGED**. Recommended next C2(ab): resolve the legacy RC numeric path and the OnePole/FIR choice structural classification against current implementation, then decide whether a targeted Script/Instrument lifecycle test is actually reachable/required before C2 final signoff.

## C2(ab) — held original RC and TPT Mode migration; FIR compatibility remains open (2026-11-15)

**C2(ab) supersedes C2(w)/C2(aa) behavior instead of treating their earlier safe structural refusals as permanent exemptions.** Under unchanged Filter stage ID/kind/graph, **OnePole.Mode LowPass↔HighPass** changes only the output tap of one TPT integrator; **SpeakerLegacyRc.Mode LowPass↔HighPass** changes only the tap of its distinct historical exponential-RC `_low` recurrence; **SpeakerLegacyRc.CutoffHz** retunes the same recurrence coefficient. All are now compatible, current original-voice updates, with one semantic inspector revision and frozen child-before-dependent-parent worker publication. `FilterParameterDescriptor.LiveChoice` separates processing compatibility from audio-rate modulation eligibility: the two output-tap choices are **nonstructural but not FM ports**, without changing structural choice classification for other Filter kinds. OnePole history copy/clone preserves the active integrator and tap; legacy RC history remains the original `_low`, not replaced with TPT. Optional mode selection travels through the existing two-phase, no-PCM-allocation scalar preflight, retaining the global 100-ms click-safe output transition and continuous note clocks.

**Tests:** `FmSynthInspectorTransportPcmTests.OnePoleModeBothDirectionsAdoptOriginalIntegratorAndKeepFreshNotesCurrent` now tests the real Mode per-use inspector in both directions, one frozen transport publication per edit, 240 same-original-invocation migrated frames and 960 original+fresh mixed frames per direction, **older=0**. `LegacyRcCutoffAndModeAdoptTheOriginalHistoricalRecurrence` tests one original RC note's 180→380-Hz cutoff, subsequent LowPass→HighPass Mode, independent same-recurrence original-time adoption oracle, nonvacuous difference against unmodified RC, and a newly authored second voice; **older=0** for both compatible changes. The two obsolete renderer assertions requiring Mode to reset an otherwise compatible OnePole history were deliberately updated; a true stage Kind/generation change still resets state. A new Core descriptor regression checks both `LiveChoice` fields are not FM ports. Baseline 3,808 tests prior to new Core assertion; no additional UI tests (two existing ones revised). Implementation commits [`0714ba1`](https://github.com/logiclrd/Heresy/commit/0714ba1f7c55840336f6075de0db1d0e16e988aa), [`9949da7`](https://github.com/logiclrd/Heresy/commit/9949da72dca9bca06323428cee05ad39e0ecff29) and [`9a1357e`](https://github.com/logiclrd/Heresy/commit/9a1357e47475e68b47a01eb1a75cae089cae8106); first implementation CI #43726788162 failed exactly the two stale renderer assertions while UI/ARM64 passed, corrected second implementation [CI #43727023053](https://github.com/logiclrd/Heresy/actions/runs/43727023053) passed, and [legacy Mode implementation run #43727147204](https://github.com/logiclrd/Heresy/actions/runs/43727147204) **completed SUCCESS**. Final docs+Core HEAD requires its own exact-HEAD Release/native/ARM64 validation.

**Precisely open FIR policy:** FIR `Window` and `TransitionWidthHz` change prepared coefficients without changing tap-ring length; same-tap-count Minimum↔Maximum `PhaseResponse` changes phase while both compiled plans declare fixed latency 0; Linear declares group delay `(TapCount−1)/2`, so switching from/to it entails an actual latency policy. `FirState.TryPrepareRealtime` currently refuses any Window/Phase/Width change; bound `FirControlRequest` has no Window and the original bound-worker preparation clones old authored configuration, so a descriptor-only reclassification is unsafe. C2(ac) should implement an exact off-PCM prepared stable-ring policy and original-voice PCM regression for truly compatible cases or document a *demonstrated* latency/state incompatibility for each exception. TapCount changes the convolution-history buffer size and remains structural.

**Source/Script boundary review:** the Synth Source inspector lists `ScriptInstrumentDefinition` as a selectable Source, but only `InstrumentDefinition` exposes the modeless `Instrument tone Volume` editor on the same audition surface; no currently exposed same-held Script Instrument shared Volume editor action can be tested without inventing UI. C1-approved unrelated Source-family substitution remains deferred. `docs/note-off-lifecycle.md` counts finite declared Script Envelope release as an implicit lifecycle; unbounded script has no declared sustain-release lifecycle. Retain existing C2(o/u) finite-held/releasing evidence without extrapolating to nonexistent additional controls.

**Current catalogue inventory correction:** the historical C2(aa) audit called SpeakerLegacyRc a compatibility-only 22nd kind absent from the new-Filter menu. Direct code inspection shows `FilterCatalogue.MenuItems` includes an enabled/selectable `SpeakerLegacyRc` leaf, `FilterEditorControl.BuildFilterTypeMenu` displays those menu entries, and the Core catalogue test requires every enum kind to appear once. The actual **current authoring menu has 22 selectable kinds**, with **43 distinct numeric modulatable descriptor slots** counting legacy CutoffHz, all now represented by bounded actual UI→worker→held-PCM cases. The legacy recurrence is nonetheless distinct from OnePole and retains compatibility processing. Do not remove the menu item or change historical DSP behavior as part of C2(ab).

**C2 remains OPEN**, including justified FIR Window/Phase/Width compatibility and any *reachable* Script/Instrument/release residual demonstrated from the actual UI. C1/C3/C4 stay accepted; C5 physical callback/stress and C6 real listening stay open; C7 and the main all-nonstructural TODO require explicit user authorization. PR #12 remains **OPEN / DRAFT / UNMERGED**. [C2(ab) property matrix detail](synth-live-pr12-c2-inspector-matrix.md#c2ab--original-history-scalar-rc-and-tpt-output-tap-migration-fir-compatibility-adjudication-2026-11-15).

## C2(ac) — bounded FIR original-note design migration and conditional latency policy (2026-11-15)

**Same-ring FIR design eligibility is no longer classified solely by Choice type.** Across all four standard FIR kinds, `Window` and `PhaseResponse` are compatible **non-FM** choices, and `TransitionWidthHz` is a compatible authored numeric field **without** an audio-rate FM port. An already-sounding Synth may migrate a prepared Window/width kernel on its original tap-sized convolution ring, and Minimum↔Maximum phase may migrate because both report zero fixed latency. Linear↔Minimum/Maximum is **not** admitted for that sounding voice: the reported fixed group-delay changes between `(TapCount−1)/2` and 0. TapCount changes the actual history buffer size; it remains structural. Old-generation state retention and correct fresh-note response continue for both incompatible categories.

**How:** `RealtimeFirStageUpdate.AllowDesignChange` is false by default. Only the explicit `FmFilterNodeProcessor` two-phase Synth revision uses true after detached FIR plan preparation and whole-voice preflight; `FirState` checks same ring length and fixed-latency regime and uses its existing 32-frame coefficient ramp without PCM design or state allocation. Ordinary `StreamingFilterChain.Configure` and channel/event Filter-control requests retain their earlier reset/refusal semantics, avoiding a silent change to non-Synth playback. `FirControlRequest` now carries Window as part of exact worker identity, and the worker applies requested Window/Phase/Width plus the last accepted bound cutoff(s) to a detached clone of the prior authored Filter. Both serial-layer and single-Filter paths are covered. Stale snapshots cannot publish via `PreparedFirControlBatch.Matches`.

**Evidence and deliberately bounded scope:** updated 14-scenario `AllFourFirKindsSameRingDesignEditsMigrateWhileLatencyAndTapsRefuse` real Filter inspector/transport/independent PCM test; new integrated FM-bound `FmBoundFirWindowAndWidthEditsWaitForExactOffPcmKernelAndPreserveRing` two-scenario worker-handoff test; new renderer `WorkerFirSnapshotIncludesWindowWidthAndPhaseAgainstOlderAuthoredFilter` showing requested 1350-Hz bound control differs from prior 1000-Hz fallback; Core catalogue invariants; and retained channel/event FIR refusal and ordinary Configure reset tests. Each integrated per-use edit requires one semantic document/audio revision, child-before-parent worker snapshot, old voice current on compatible changes, old voice older only on true latency/tap change, no extra audio session, and immutable original playback snapshot. Automated PCM is **not** proof of human inaudibility or callback deadlines. Historic C2(z) old Window/Width/MinMax older-generation cases are superseded only by this bounded new policy, not by a blanket promise for every graph/release race.

**C2 not yet signed off:** after exact-head CI, perform final reachability review of currently exposed Script/Instrument shared editors and finite/unbounded Script Envelope release; do not invent an unavailable nested child editor. Source-family substitutions remain the C1-approved deferral; C3/C4 already accepted, C5 performance/3→17→3 recovery and C6 real-device listening still pending, C7/user final authorization remains separate. C2(ac) does not claim general simultaneous FIR publication supersession, every tail boundary or unrelated effect Filter migration. PR #12 remains **OPEN/DRAFT/UNMERGED** and the top-level all-nonstructural TODO remains open. See [C2(ac) matrix evidence](synth-live-pr12-c2-inspector-matrix.md#c2ac--fir-same-ring-live-design-choices-exact-worker-snapshots-and-latency-gated-phase-2026-11-15).

## C2(ad) — actual Script nullable release, render-boundary currency proof, and final reachable Instrument scope (2026-11-15)

**Real Script release lifecycle audit and regression.** The existing C2(o/u) Script inspector/production PCM fixtures covered finite limits. The same embedded Script `Release limit` control also accepts **blank / null**. The new `ScriptReleaseLimitNullAndFiniteLiveEditsRespectDefiniteOffAndFrozenRelease` test exercises both held-edit directions (finite 90 ms→null and null→finite 90 ms) through the *real* editor, shared event, deduplicated child-before-parent worker publication and original PCM. A finite release declared before Note Off must be honored; a null Script cannot itself claim sustain. The test additionally commits finite→null while the original finite release is in progress and requires refusal, untouched original deadline/PCM, sticky older currency until retirement and fresh-note null policy. Renderer `ScriptEnvelopeCurveTests` assert the exact finite vs null `HasSustainLoop` and `IsZeroAfterCompletion` flags. The test's initial uint-channel compile slip was test-only. After correction, [CI #43731238003](https://github.com/logiclrd/Heresy/actions/runs/43731238003) reported `OlderAudioCount=0` before the next PCM block; [CI #43731438263](https://github.com/logiclrd/Heresy/actions/runs/43731438263) repeated it. **This was a mis-timed test expectation, not demonstrated DSP corruption**: worker preparation/publication does not itself adopt into the old PCM owner. [`618df88`](https://github.com/logiclrd/Heresy/commit/618df88c44b74ea402f926616bc5868a02250122) first verifies 20 actual continuing release frames on the old clock and only then reads the sticky older status. The intervening [production guard adjustment `0e6a749`](https://github.com/logiclrd/Heresy/commit/0e6a749af1e3416c4a95d3045188e943c48d43ad) deliberately makes after-Off *audible* curve-change refusal independent of the newly authored bounded-output-fade capability; **the earlier red result does not establish that the old guard allowed unsafe adoption**. The final exact-HEAD CI verdict is linked in the PR acceptance comment.

**Script Instrument is selectable but not a same-preview nested tone editor.** `AddSoundSourceInspector` lists Script Instruments as Source choices, but the data Instrument-only modeless shared-Node-Volume action is absent for `ScriptInstrumentDefinition`. `MainWindow.ShowInstrumentEditor(ScriptInstrumentDefinition)` replaces the current main view with `ScriptInstrumentEditorControl` rather than exposing an embedded subeditor in the still-mounted Synth preview. Do not claim or test a nonexistent same-bound Script tone editor, and do not reopen the approved cross-family SourceId exception. The *directly embedded Script Envelope editor* remains a real Synth surface and is qualified above.

**C2 is still OPEN: specifically reachable data-Instrument shared Volume with additional retained processing.** The existing modeless data-Instrument Volume action *is available* for tones that contain a Pitch/Panning/Filter envelope, a tone Filter, or a referenced Synth source. `FmSynthSound.TryGetLiveInstrumentVolume` currently requires `SampleSound`/`SampleSoundState` and refuses those other tone components; `InstrumentSound.TryGetPreparedDirectSampleVolume` has the same direct-Sample-only preparation limit. An otherwise unchanged selected Instrument/tone graph with only its shared Node Volume changed is a nonstructural inspector edit, **not** C1's unrelated family substitution. The existing C2(g/p) and C3 Volume tests establish real migration for the restricted direct Sample and selected compound graphs; they cannot be extrapolated to these exposed variants. Before C2 closure, C2(ae) should batch a *same-Sample with unchanged Pitch/tone Filter* and *nested referenced Synth tone* probe, require accurate old/new-generation status and atomic parent sibling updates, then implement only demonstrably safe migration or document a narrowly justified runtime-incompatibility refusal. Do not treat broad ScriptInstrument authoring or every NNA permutation as a new indefinite C2 obligation.

**Gate inventory:** 28 currently inventoried Synth inspector surfaces have been reconciled across C2(a–ac), plus the explicit Script null/finite limit in C2(ad). No general Filter numeric or FIR choice gap remains from the C2(ab/ac) implementation. **C2 stays unchecked because of the distinct reachable Instrument Volume eligibility gap.** C1/C3/C4 remain checked; C5 callback/deadline/3→17→3 recovery, C6 human Linux/Windows listening, C7 final merge-ready authorization and the overarching `docs/todo.md` all-nonstructural checkbox remain **OPEN**. PR #12 remains **OPEN/DRAFT/UNMERGED**; no source-family feature expansion or merge is authorized.

## C2(ae) — actual modeless data-Instrument Volume migration with original independent companion histories (2026-11-15)

**C2(ad)'s last named reachable-property blocker is now addressed in production and in one grouped real-editor test.** `ModelessInstrumentVolumeWithPitchToneFilterOrNestedSynthAdoptsContinuingPcm` opens the data-Instrument Source inspector's actual modeless shared Node Volume editor while a note is held, for three selected-tone variants: extra Pitch envelope, 180-Hz OnePole **tone** Filter, and referenced nested Synth. It checks 40 original frames and 101 continuous 100-ms gain-transition frames against separate same-age unedited transport PCM, then 31 settled frames, while verifying a single deduplicated shared-object revision/worker publication, child-before-dependent-parent frozen source preparation, one open audio source and **older=0**. The first test-only [red #43732817301](https://github.com/logiclrd/Heresy/actions/runs/43732817301) exposed the known held-Volume refusal (0.5 unchanged instead of first smoothstep value 0.499888). The positive implementation passed [CI #43733251089](https://github.com/logiclrd/Heresy/actions/runs/43733251089), **3,813/3,813 tests**, all Release/packaging and ARM64 jobs green. The previous compiler-definite-assignment and invalid 1-kHz-test-OnePole cutoff slips were corrected as nonbehavioral fixture/compile failures.

**Preflight and state ownership:** `InstrumentSound` now precomputes a selected-tone Volume candidate along with its terminal Sample or Synth, frozen companion envelopes and an immutable, worker-computed fingerprint of the **effective** tone Filter stage values. The PCM path verifies original selected frozen Tone/NNA/Source, unchanged companion full shapes, Sample readhead equivalence or nested Synth `TryPrepareRunningState` compatibility before changing gain. It retains the original tone Filter state, Sample playback trajectory and nested Synth invocation; a newly admitted nested child revision recursively commits using its original `SoundState`. No Filter kernel/resource preparation, Roslyn compilation, Source selection or SoundState allocation runs on the PCM owner.

**Negative generation guard:** The same grouped test was extended for the Pitch variant: separately changing its previously frozen Pitch curve (0.3→0.8) is *not* this Volume-only migration. The existing original note must keep its old Pitch and gain PCM and be marked **older=1 after the next render/adoption boundary**. Later compatible Volume edits cannot clear the warning or rewrite missed history, while new notes hear current authored Volume. This exercises `FmSynthSound.RetainsIncompatibleNestedSynth` for Instrument-wrapped terminal Sources and frozen non-Volume control differences. [Test `5394cb4`](https://github.com/logiclrd/Heresy/commit/5394cb46c94c5c352d09a216883c5a96373d509d) and [guard `e5795e4`](https://github.com/logiclrd/Heresy/commit/e5795e4fd08828dd401d3f1ab31c694338c5f0f5) are subject to final exact-HEAD green CI before being accepted as a passing case.

**Remaining gates:** No unadjudicated **currently reachable Synth inspector** property family has been identified after C2(ad/ae), but this is a bounded proof, not an exhaustive arbitrary graph × event-order product. The original explicit **C2 checklist stays unchecked until the final field/evidence reconciliation and exact-HEAD CI signoff**, to be performed as its own closing audit step. C1/C3/C4 remain accepted. Separate C5 physical deadline/worker saturation/3→17→3 recovery, C6 physical Linux/Windows audition, C7 merge authorization and overall `docs/todo.md` nonstructural Synth property completion remain OPEN; the PR remains OPEN/DRAFT/UNMERGED. Do not invent Script Instrument modeless editors, call Source-family substitution a compatible scalar, or mark physical listening completed by a manually driven production PCM backend.

## C2(af) — final currently-exposed property reconciliation and automated acceptance (2026-11-15)

**C2 checklist marked [x], for its precise automated property-matrix scope.** The inspector-to-publication-to-original-note PCM contract has been reconciled at **28/28 current inspector surface groups** against the *current* `FmSynthEditorControl`, exposed shared Node/Script/Filter editors, data-Instrument modeless shared Volume, 22-kind `FilterCatalogue`, `FilterParameterDescriptor` and the cited C2(a–ae) full-path tests. The authoritative, per-row final decision table is in [C2(af) of the inspector matrix](synth-live-pr12-c2-inspector-matrix.md#c2af--final-28-surface-c2-automated-inspector-to-pcm-signoff-2026-11-15). Earlier C2(q/aa) 28-row and 21-plus-one/structural-Choice statements record what was *known then*; they are not present-day unresolved blockers.

**Remaining named issues were resolved rather than waived.** C2(r) bridges real Operator Add/Multiply and shared Filter defaults into ongoing production PCM; C2(s) supplies nonneutral Oscillator/Source Constant-mode controls; C2(t) supplies all ten Multi presets; C2(u/v) cover time-dependent Node/Script and source-selection/lifetime cases. C2(w/x/y/z) cover numeric catalogue kinds (including bound FIR), C2(ab) preserves original scalar RC and TPT integrators for their non-FM Mode choices, C2(ac) worker-prepares compatible FIR Window/width/same-latency phase changes with latency-gated refusal, C2(ad) covers blank ↔ finite Script release, and C2(ae) corrects same-bound data-Instrument Volume migration with an unchanged Pitch envelope, tone Filter or nested Synth plus a **red-first verified** older-generation reporting fix for changed companion Pitch. The latter code-and-docs exact-HEAD [CI #43733984334](https://github.com/logiclrd/Heresy/actions/runs/43733984334) succeeded **3,813/3,813**, full Release/Windows/package/ARM64; the final C2(af) documentation commit's exact-HEAD Actions check is recorded in the PR acceptance comment.

**Evidence policy:** Every currently *editable nonstructural* property has at least one actual real-control U/P/T/V/A regression on a continuing note or an explicit state-dependent old-generation policy with verified current/new voice status. Per-surface G is **bounded**, not “tested every connected graph topology, transition overlap, release frame and user device.” Structural graph node/port rewiring, Filter topology/latency/ring mismatch and new-object creation are correctly classified; unrelated/cross-family Instrument/Synth SourceId swaps remain C1-approved deferrals. The Script-Instrument tone editor is a separate main view, not a missing same-bound modeless Volume control. Noise Vmin/Vmax has not been implemented, so is not a current property. The all-nonstructural `docs/todo.md` item specifically also requires actual latency/underrun/physical listening confirmation: **do not check that overall TODO based on C2 alone.**

**Still OPEN:** **C5** measured real callback deadlines, sustained worker queue saturation, Multi 3→17→3 overload and reclaim/recovery, **C6** actual device listening/Linux-Windows acceptance across representative compatible/refused/compound/release cases, **C7** consolidated signoff, any approved follow-up and **user-controlled** merge-ready decision. **C1/C2/C3/C4 accepted; C5/C6/C7 and overarching TODO open. PR #12 OPEN/DRAFT/UNMERGED.**

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
- [x] **C2 — Inspector-to-PCM property matrix (medium).** Build a compact auditable matrix across all actual inspector fields and the exposed shared Filter/Envelope editors, with at least one whole-path UI commit/revision/publication test per family and a PCM invariant proving the selected held voice actually adopted the edit. Include presets/cycle, multiple Filter stage kinds, Script Envelope, and Operator Minimum/Maximum; distinguish “UI publishes” from “held voice adopts.” Mark each cell *verified, intentionally retained, or red-first failing* with exact test and device observations.
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
