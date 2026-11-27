# Absolute Script Pattern seeking — discovery and proposed design

Issue [#73](https://github.com/logiclrd/Heresy/issues/73).
Baseline: merged PR #72, main `22162709d82a211d30b2b069aa72b42e25a36d74`. This document is
a design proposal for owner review, not a claim of implemented behavior.
All 4,739 tests were green at the parity head; owner accepted parity,
but explicitly deferred absolute seek correctness to this issue.

## Observed behavior and independent reproductions

The owner confirmed equivalent physical/virtual PCM by ear, but a late
Seek(4, ..., 2.0) plays at two seconds plus elapsed note time. A second
note started at the same time and immediately offset by two seconds
aligns after the first note's seek. Changing SourceFrame subsequently
also retains the seconds offset. In a private Pattern, Seek(2, 0.63)
overshoots the first accent and SourceFrame(4, 0) alone fails to rewind.
The issue contains the original Sample and recursive reproduction
scripts. Do not infer a regression in #72: the baseline before #72,
`716c2092dc9d5f085205affb5beaaa3d4a988a14`, contains the same setters
and private mapping. Desktop main reproduction not performed.

## Source-backed audit

| Existing component | Contract / finding |
| --- | --- |
| ScriptProgramRuntime, VirtualNote, NoteGroup | Emit SetPlaybackOffsetCommand and SetSourceFrameOffsetCommand. Strict/relaxed emission, generated-only F6 overloads and group fanout already work. |
| IncrementalPatternTimeline | Admits explicit physical/virtual targets and positive fixed-wall deadlines for these commands; excludes broadcast. Resolve the currently bound voice at the deadline. |
| DataPatternDefinition / PatternEffect | SetPlaybackOffsetPatternEffect persists a native time-offset effect and generates SetPlaybackOffsetCommand. Do not silently change its meaning. |
| PatternNoteProcessor 2473–2490 | Oxx resolves effect memory even without start; only a new note produces SetSourceFrameOffsetCommand, combining SAx high bits and low parameter. |
| PlaybackSession 1887–1889, 1913, 1953–1967, 2290–2297 | Seconds setter assigns PlaybackOffset; native setter invokes capability with no event-frame parameter. SourceFrame unsupported sound is no-op. ReplayRequired warning for nonzero offset. |
| SoundState | Holds nonnegative PlaybackOffset, PlaybackOriginFrame, NoteOffTime, PitchTrajectory and natural-end observation. RestartPlaybackAt clears natural-end marker. |
| SampleSound | Native and seconds offsets add, then integrated pitch distance times native/output rate and initial pitch/speed is added. End calculation inverts accumulated pitch trajectory when possible. Loop mapping applies afterward. |
| PitchTrajectory | Monotonic accumulated position in note-relative output-frame coordinates; independent piecewise pitch curves, exact existing integration, cached segment positions and efficient constant-pitch end inversion. |
| PreparedRecursiveMixdownSound | Child source position = invocation frame − PlaybackOriginFrame + native offset + ceil(seconds offset × private sample rate). Private clock scales speed, pitch transposes children. Rebuild on backward position/origin changes; no PCM journal. End/cut and inherited-parent-pitch mapping also use origin/offset. |
| PlaybackVoice.Retrigger / PlaybackSession 2463–2477 | Clears old offsets, restarts playback origin, resets tone/event/enclosing filter histories and output observation, anti-click and volume transform. Voice StartFrame remains unchanged. |
| FmSynthSound | Implements IStreamingFiniteSound and IFirPreparationSound, not ISourceFrameSeekableSound. Does not consume its top-level PlaybackOffset in Render; occurrences in this file transfer a nested direct Sample state during live graph revision. Synth stateful graph reconstruction is not equivalent to direct PCM indexing. |
| InstrumentSound.CreateInvocation | Selects and composes the terminal SoundInvocation; production controls apply to the bound terminal sound, not to the Instrument selector. |
| FlattenedSourceEffectPolicy | Both old seek/offset commands are single-voice effects; no arbitrary descendant seeking. |

Key compatibility evidence:
- PlaybackSampleOffsetTests.GenericPlaybackOffsetAndNativeSourceOffsetAreAdditive
  explicitly expects an initial offset of 2ms plus 3 frames to render
  frame 5. Preserve this low-level contract and its regression.
- PlaybackSampleOffsetTests.SourceFrameOffsetUsesNativeFramesWithoutTimeConversion
  verifies native offset at onset. Tracker Oxx, SAx and Qxy tests remain.
- PlaybackRetriggerTests.RetriggerRestartsExistingVoiceWithoutReallocation
  checks identity, filter-state object and playback origin, but does not
  independently verify the first sample after Qxy. SampleSound's current
  position calculation does not explicitly subtract PlaybackOriginFrame;
  audit with a ramp PCM counterexample before deciding whether Qxy needs a
  separate defect. Do not broaden or silently redefine Qxy in this change.
- RecursiveNativeSeekBoundaryTests cover onset Oxx, private sequence
  boundaries, Instrument selection, finite ends, Qxy and inherited pitch.
- DirectScriptPlaybackControlTests checks the stored seconds offset, not
  independently the target sample. ScriptVirtualControlPlaybackTests
  uses stepped PCM to establish event deadline and replacement targeting;
  a plateau cannot distinguish frame 300 from 310. Existing physical/
  virtual parity and partition comparisons are necessary but not enough.

## Proposed authored contract (owner approval required)

Owner refinement: distinguish absolute and relative seeks in both native
frame and time coordinates. The following is a revised proposal, not yet
a finalized API decision.

Keep existing absolute helper signatures; propose SeekRelative(row,
deltaSeconds) and SourceFrameRelative(row, deltaFrames), with the same
physical channel argument, owned-handle/group variants and optional
fixed-wall offset as their absolute counterparts.

At the actual resolved event deadline, let C be the current fractional
source playhead and R be its instantaneous source-frame rate per playback
second. The four target calculations are:

| Operation | Target |
| --- | --- |
| SourceFrame(F) | F |
| SourceFrameRelative(dF) | C + dF |
| Seek(T) | T * R |
| SeekRelative(dT) | C + dT * R |

Absolute time is a constant-current-rate conversion from the source origin,
NOT the integrated history of previous pitches. Relative time is a
constant-current-rate conversion from the current playhead, NOT a replay
of earlier or predicted future modulation. Snapshot the rate and position
at execution, not raw row preparation. The latest successful absolute
seek replaces the previous target, regardless of units; relative seeks
derive from the current position including earlier seeks. Subsequent
progress follows existing live pitch/speed. Idle controls are no-op;
never remember a seek for a future note. Groups evaluate each member's
own source rate/position, not a shared frame target for unlike sources.
Displaced voices and siblings are unaffected.

For Sample, native frames are original decoded PCM frames. R is native
sample rate times the effective instantaneous pitch (including modulation)
and playback speed; output sample rate affects sampling, not the
seconds-to-native-frame conversion. Example: at 48 kHz, effective 2x
rate, Seek(1s) lands on frame 96000, whereas SourceFrame(48000) lands on
frame 48000. At that rate SeekRelative(-0.5s) subtracts 48000 frames.
Fractional current position must be retained, even for an integer delta.

For private Pattern/Sequence, propose using the instantiated private
render timeline, whose native frames are at its render sample rate.
Its source-frame rate per outer playback second is that sample rate:
child clock already accounts for selected speed and pitch affects child
pitches, not duration. Do not apply pitch or speed a second time.
An alternate authored/unscaled child-time coordinate would require
explicit agreement (especially with child Tempo changes). A private
seek deterministically positions the child timeline and rebuilds its
internal voice/automation/filter histories as needed; outer note age
and outer state remain intact.

Propose signed relative deltas, clamping targets before frame zero to
zero, while absolute negative inputs remain invalid. For zero relative
delta recommend a no-op without replay or reanchoring.

### Loop-coordinate contract (owner clarified)

The owner explicitly confirmed a left-bounded, unwrapped playhead space:
lead-in concatenated with indefinitely repeated loop traversals. Let x
be the nonnegative (possibly fractional) unwrapped playhead, L the
loop start and N the loop length (exclusive end minus start). For a
forward loop, map to the actual Sample coordinate as:

    sampleFrame = x < L ? x : L + (x - L) % N

All four seek operations calculate their target in unwrapped coordinates
before normal loop mapping. Relative seeks originate at x, never at
the mapped sample address; crossing a loop boundary backwards returns
to the preceding iteration if one exists, otherwise to the lead-in.
A sufficiently large negative delta can return to the lead-in even
after many iterations. The left boundary is zero (proposed clamp).
Retain the fractional component of x. No separate loop counter or
history is needed. At L=1000, N=500:

| Playhead | Frame delta | New playhead | Actual sample frame |
| ---: | ---: | ---: | ---: |
| 1050 | -200 | 850 | 850 |
| 1550 | -200 | 1350 | 1350 |
| 2050 | -200 | 1850 | 1350 |
| 1550 | +500 | 2050 | 1050 |
| 100 | -200 | 0 | 0 |

For ping-pong loops propose the same unwrapped coordinate system with
the existing reflected mapping (period 2*(N-1), special case N=1).
This retains traversal direction across seeks and does not change the
existing endpoint interpolation policy. Owner's explicit formula
describes forward loops; the ping-pong extension is proposed, not
separately approved. Absolute targets beyond the loop endpoint map
through the normal loop traversal rather than bypassing it. Confirm
large signed deltas, finite end and private seek during release.

Preserve outer StartFrame, voice identity, NoteOffTime, release disposition,
envelopes, fades, pitch operator phase, volume, spatial state, NNA policy,
source selection and outer DSP histories. Do not turn Seek into Note or
Qxy; no volume transform, filter reset, voice resurrection, or note-age
reset. A successful seek clears stale source-end observations. A finite
source sought to/beyond its end ends at the deadline (configured tail
policy still applies), and a subsequently absent voice cannot be revived
by a seek. For looping Sample, use the existing source-coordinate mapping,
including forward and ping-pong behavior beyond the first pass; do not
invent a reverse traversal API. Audit pending note-off and release
intersection before finalizing private seek during release.

Unsupported terminal sounds: recommend preserving a harmless no-op for
both forms on procedural Synths (possibly an explicit bounded diagnostic,
decide with owner), rather than promising arbitrary graph rewind or
silently reinitializing oscillator state. Instrument-selected Samples or
private sounds support seeks through their terminal capabilities. Flattened
recursive sources remain unsupported, as they do not have one bound
playhead. No broadcast capability. Keep validation of nonnegative finite absolute seconds and native long
frames; relative seconds are signed finite and representable, relative
frames are signed long. Handle arithmetic without overflow. Document replay cost and rate-limit
diagnostics for private seeks, including rewind-to-zero.

## Implementation direction (not yet code)

Introduce distinct typed script absolute seek commands, e.g.
SeekPlaybackTimeCommand(TimeSpan) and SeekSourceFrameCommand(long), plus
relative counterparts (or a typed absolute/relative discriminator), rather
than repurposing SetPlaybackOffsetCommand / SetSourceFrameOffsetCommand.
Keep native data effects, persisted serialization and low-level additive
offset API unchanged. Extend existing explicit-target validation,
flattened suppression, command dispatch, F6 compiler bindings and group
fanout only as needed; only the two proposed relative authored helpers, no unsafe raw surface.

Use sound-specific absolute-seek capability with actual invocation-relative
frame and render context. Avoid a blanket signed correction to PlaybackOffset
or resetting note age. Proposed direct Sample anchor is (target native
frame, pitch integral at seek deadline). With P(t) the existing accumulated
relative pitch position and k the fixed initial pitch × speed × native/output
sample-rate ratio, new position at t is:

    target + (P(t) - P(seekFrame)) * k

For relative seeks calculate the current source position from the active
anchor or legacy mapping, then add the signed delta in native coordinates.
For time seeks convert using the instantaneous effective rate at the
deadline; do not use the historical integral for that conversion.
The previous seek target is replaced, not accumulated. Snapshot P at the
deadline once, not per sample; retain only latest anchor, no new history or
per-sample allocations. Initial legacy offsets retain their existing
mapping until an absolute seek explicitly supersedes them. End inversion
solves P(end) = P(seekFrame) + (sampleLength - target)/k; handle
target >= end explicitly, streaming/non-projectable curves and saturated
arithmetic. Preserve fractional interpolation and same-bound-sound
identity. Reset or reconcile anchor on legacy retrigger paths without
changing unrelated tracker semantics. Inspect live Synth nested Sample
state transfer if new fields could be relevant.

For private mixdown, use an independent source mapping anchor:
childFrame = targetFrame + (outerInvocationFrame - seekFrame). Use existing
bounded forward discard and deterministic rebuild on rewind. Source clock
mapping must be reflected consistently in inherited pitch callbacks,
natural end projection, observed end and scheduled cut/release handling.
Distinguish the outer note-age clock from child clock; skipped preroll
must not consult invalid negative parent age. Preserve independent sibling
registrations, cancellation and no retained PCM or event journal. A
private seek while releasing may require special lifecycle handling;
design/test before enabling that edge rather than accidentally reviving
cut producers. Do not conflate seek generation with ownership generation.

Review numeric precision for very large frame targets and TimeSpan
conversion: no overflow in render arithmetic or unbounded replay for
invalid/finite out-of-range targets. SeekCost is advisory; the existing
cooperative work/cancellation limits apply. Avoid new per-sample
allocations (owner hot-path policy), and retain bounded per-voice scalar
state only.

## Staged work and acceptance

1. Discovery/owner sign-off on units, unsupported sources, release/loop
   semantics and compatibility boundary. No renderer change in discovery.
2. Direct Sample red/green: distinct commands + scheduler + renderer
   capability, preserve low-level offsets. Use distinctive ramp/impulse
   PCM with independent oracle, at different native/output rates,
   nonunit pitch/speed, varying pitch curves, repeated forward/backward
   absolute and relative seeks, fractional positions, mixed units, signed
   deltas and clamp-to-zero, zero delta, exact finite end, loop mapping (first
   traversal versus later traversals, multiple backward wraps, lead-in,
   fractional interpolation, one-frame and ping-pong loops), silent
   channel, deferred deadline on replacement, no effects on displaced/
   sibling voices, cancellation. Test exact target sample and progression,
   note-age/envelope preservation and block invariance. Preserve
   legacy Oxx/SAx/Qxy tests; log separate counterexamples if found.
3. Private Pattern/Sequence: independent child PCM slice reference,
   actual accent at target, rewind-to-zero, mixed units, independently
   started group members, relative deltas, nonunit speed/pitch, inherited pitch mapping,
   release/cut/end boundaries, nested private/flattened descendants,
   Instrument selection, bounded rebuild and registry retirement.
4. Integration: strict/relaxed, F6 spans and delegate provenance, exact
   command payloads, actual realtime/F6/export parity, multiple speaker
   feeds, stress old-generation/canceled delayed events and private
   rebuild. Update editor help, programmability docs and TODO after
   implementation. Full exact-head Release/ARM64 CI and owner desktop
   retest of #72 tests 2/4, then explicit merge approval.

Do not claim red tests have run during this source audit. No local .NET
execution is available in this workflow.

## Slice 1 — direct Sample implementation

Owner authorized implementation after the unwrapped loop clarification.
New typed commands and signed relative script helpers route through the
same deadline/ownership/F6 policy. SampleSound implements a per-state
anchor of target, deadline and accumulated pitch; end projection uses
that anchor. Legacy offset assignments clear the anchor; absolute seeks
clear both additive offsets. Relative zero is a no-op, negative targets
clamp at zero, extreme coordinates saturate to the native long range.
No seek changes to note age, outer DSP state or loop mapping. Unsupported
procedural sounds ignore seeks. Transitional private recursive absolute
operations retain the old offset behavior; relative private operations
are no-op until slice 2 implements the capability. Do not merge or accept
private seek semantics in this state. Editor reference explicitly notes
the transitional state. New independent ramp tests exercise all four
operations on physical/handle/group targets in both ordering modes,
nonunit rate, pitch trajectory, finite end and owner forward-loop examples.
Raw legacy offset regression remains intact. CI required; no local test run.

## Slice 2 — private recursive implementation

Private Pattern/Sequence (including Instrument-selected recursive terminal
sounds) implements IPlaybackSeekableSound. Time conversion uses the private
render sample rate, which already incorporates the instantiated tracker
speed; pitch does not change timeline duration. Seconds round by ceiling
on that native grid, including signed deltas. Relative targets clamp to
zero with Int128 intermediate arithmetic. Each nontrivial seek reconstructs
a fresh private generator and session immediately at the event deadline,
prerenders/discards to the target in existing 256-frame scratch blocks and
binds the new child-to-outer map. This deliberately also rebuilds on forward
seeks: reusing an already integrated child pitch history across a changed
parent-time mapping is not equivalent to deterministic reconstruction.
A same-target or zero relative seek avoids replay. No PCM/event journal.

Preroll uses the instantaneous outer pitch at the seek deadline (frozen
for the skipped prefix); from the target onward it reads the live outer
pitch at the anchor-mapped parent note age. The outer voice, filter
histories, age and identity are unchanged. This is an explicit proposed
policy for unrendered child history rather than an attempt to invent a
past outer pitch trajectory. Natural end and cut map back through the
anchor; lifecycle advancement uses the active child position. Seeking
far past a finite end maps completion to the deadline and does not revive
the detached voice. Explicit outer Off/Fade/Cut irrevocably stops its
producer: subsequent private seeks are ignored rather than recreating
released descendants. Native Oxx/Qxy retain their original code paths.
Legacy offset assignments clear the anchor. Replay hints use the existing
bounded diagnostic sink with the actual native target, including zero.

New tests: six strict/relaxed Pattern/Sequence/Instrument cases compare
four seek operations with slices of an independently rendered, speed-scaled
reference; two independently started group-member cases assert synchronized
PCM and cut at the outer deadline; a release no-resurrection case and a
finite-end/sibling isolation case. Slice 3 still needs inherited pitch,
recursive descendants, editor/export integration and additional stress.
No local execution; exact-head CI required. Explicit owner review of the
preroll/release choices is still needed before merge.

## Slice 3 — integration and hardening

Extended existing realtime/F6/offline 1/2/6/8-speaker parity fixtures to
include absolute and relative operations inside private descendants and
on the outer group/physical channel, with mixed strict/relaxed ordering.
Extended recycled-generation/canceled-deadline stress with signed deltas.
Added direct Sample fractional and ping-pong traversal cases; extended
nested private registration/reclamation tests to the actual absolute
command route while keeping all legacy Oxx-style tests. Added explicit
inherited pitch preroll/live transition tests at one and two recursive
levels and exact relaxed delegate callsites for both relative helpers.
Public help/programming docs and replay hint docs describe the actual
implementation. Sample coordinates remain double precision (including
fractional interpolation); extreme long-range coordinates cannot represent
every integer exactly. No claim of arbitrary precision or unlimited
private replay. CI and desktop acceptance required before merge.
