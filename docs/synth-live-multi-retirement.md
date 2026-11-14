# PR #12 — Retired Multi DSP and live-output listening status (2026-11-13)

This note **supersedes** the older final sections of `synth-live-parameter-plan.md`, `synth-live-editor-property-audit.md`, and the old PR description where they describe preview-only DC suppression as current. The `MonitorFinalSynthDc` path was reverted in `26d543ac4ff`; the authored Add DC offset is present in both Synth Test and ordinary render/export.

## Listening acceptance (reported by the user)

- **PASS:** Final-output Operator switching **Multiply ↔ Add** was manually checked successfully on the current unified-transition build without the earlier unwanted clicking. This confirmation was omitted from the 2026-11-13 continuation handoff, which incorrectly described the entire later listening pass as pending.
- This is a **bounded final-Operator mode-switch pass**, not proof that every final Add Constant-value edit, every Sample/Filter layout, or every extreme voice load has been tested. Earlier Add offset-related pops and earlier failed variants remain historical results, not the current switching outcome.
- The common transition clock remains sample-rate-scaled **100 ms smoothstep**. Positive oscillator/ratio and unbound OnePole/SVF cutoff glides use logarithmic frequency interpolation; the authored final-output equation retains its gain **and** DC bias.

## Multi failure and retirement policy

The user's held **3 → 17 → 3** Multi edit produced audible gaps and output underruns while 17 voices were active; the same held note did not recover on shrinking back to three, whereas a new three-voice note did. Source inspection identified a sticky `MultiVoice.HasStarted`: children with final mix zero still rendered every frame forever.

PR #12's bounded retirement slice changes the contract:

1. Continue processing all audible constituents throughout the standard **100 ms** mix transition, even when shrinking.
2. Once a constituent's target mix and interpolated mix are **both exactly zero**, and it no longer owns an outgoing onset fade, retire it. Thereafter its child DSP stops advancing and stops incurring per-frame cost.
3. A compatible later increase **during** the original retirement fade retargets its gain from the current value and retains full audio/phase/history continuity.
4. A later increase **after** complete retirement must obtain a **fresh child invocation prepared by the existing bounded, cancellable off-PCM Multi worker**. Publish it only with the complete compatible revision; start its child-local clock at that publication and fade it in over 100 ms. Continuing audible siblings are not restarted.
5. A fully retired Sample/Filter child deliberately **does not** preserve its counterfactual, never-heard Sample position or IIR history across silence. It has a new onset instead; no unbounded history catch-up occurs inside PCM. This is a material change from the earlier deliberate silent-continuation policy.
6. Do not infer 17 concurrently sounding voices are realtime-safe. Retirement is intended to recover capacity **after reducing** the count. Sustained 17-voice capacity depends on graph cost and hardware; measure underruns and renderer timing instead of hardcoding a safe maximum.

Desired-invariant regressions: `FmSynthMultiRetirementTests.ThreeToSeventeenToThreeStopsAdvancingFourteenRetiredChildren` inspects actual child render progress across the zero-mix boundary, while `FullyRetiredConstituentsRequireWorkerPreparedFreshRevival` requires worker preparation and checks a new local lifetime. Existing fade, Note Off/Cut, Sample EOF, Nested Multi, rapid retargeting and allocation invariants must remain intact.

**Automated acceptance:** [run #43550811563](https://github.com/logiclrd/Heresy/actions/runs/43550811563) passed all 3,672 tests, Release, and Linux ARM64 on `ce2ba0b1d6e`.

**Physical acceptance (reported by user, 2026-11-13): PASS for overload recovery.** Deliberately exceeding available CPU with a higher Multi voice count still produces audible artifacts as expected, but reducing the held Multi voice count now **recovers regular playback without restarting the note**. This establishes the previously missing end-to-end recovery behavior, not a guarantee that arbitrary voice counts can run in realtime. A second increase/revival after retirement and Sample/Filter-rich child variants have not yet been separately confirmed by this report.

PR #12 stays **draft/unmerged**; the overarching all-nonstructural-properties TODO stays open.


## Follow-up slice — nested and Sample/OnePole revival (2026-11-13)

**Red-first discovery:** The original retirement checks used lightweight Constant children. New tests for (1) two outer Multi voices each containing an inner Multi that is fully retired then revived, and (2) a long direct Sample through a OnePole Filter and Multi, found **first-render managed allocations** after fresh revival: 1,184 bytes and 11,968 bytes respectively on the CI worker. Audio was able to progress, but the first revived PCM frame lazily created child render contexts and/or Filter states, violating the no-PCM-allocation contract.

**Implementation:** `FmSynthSound.PrepareFreshMultiChildOnWorker` primes only **detached, freshly created** Multi constituent state on the bounded cancellable expansion worker, including recursively nested Sources/Multis, mono child render contexts, known sample rate, and per-stage Filter histories. This is separate from existing phase-preserving onset-history transfer and never resets the two continuing audible siblings. Reactivated children still begin an explicitly fresh local lifetime and fade in under the common 100 ms clock; no historical Sample/Filter catch-up is performed in PCM.

The initial worker-preparation implementation also exposed an important **unstarted-note boundary**: two established scheduler tests offer growth before the first Render, when `SampleRate == 0`. The revised implementation prewarms rate-specific state only when a real sample rate is known; it leaves never-rendered notes on their existing first-render initialization path. Bounded queues, stale worker cancellation, and latest-wins revisions remain mandatory.

**Verification:** [CI #43552282430](https://github.com/logiclrd/Heresy/actions/runs/43552282430) **SUCCESS** at code commit `ba3a6e7f5612a039f54f68b4090dbf74eb3cc213`: **3,674 tests passed** (910 + 105 + 1,126 Render + 29 Render.File + 355 Playback + 1,149 UI), Release and Linux ARM64. Specifically `NestedMultiRevivalAfterCompleteRetirementPreservesParentVoices`, `SampleAndOnePoleMultiReviveFromFreshStateWithoutReplayingRetiredHistory`, `MultiExpansionWorkerQueueIsBoundedAndRetriesSurvivingVoices`, and `SupersededInFlightMultiExpansionPreparesOnlyLatestRevision` all passed. Their desired invariants were **not weakened**.

**Physical acceptance remains scoped:** the user's previously reported hardware overload-recovery pass is accepted. The new nested/Sample/OnePole cases have automated, not yet separate human listening, verification. The full property audit and SourceId/remaining Filter-family migration work remain open; do not merge PR #12 or check off the broad milestone as a consequence of this slice.
