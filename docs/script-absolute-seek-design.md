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

Keep public signatures unchanged. Seek(row, seconds) and SourceFrame(row,
nativeFrame) are absolute SOURCE playhead positions at the actual event
deadline, not new offsets relative to note onset. The latest successful
seek replaces the previous target, regardless of units. The first frame
rendered at the deadline corresponds to that target, subject to existing
fractional interpolation / loop mapping / finite-end behavior. Subsequent
progress uses the existing instantaneous pitch and playback speed. An idle
control does nothing; never remember a seek for a future start. Group
members seek their own current sounds to the same requested source position,
independently of their age; displaced voices and siblings are unaffected.

For Sample, seconds are time in original decoded PCM, independent of the
note's pitch/speed or output sample rate. Native frames are original PCM
frames. For private Pattern/Sequence, propose seconds on the instantiated
private playback timeline, native frames at its render sample rate,
including its selected speed; pitch affects child pitches, not duration.
A private seek deterministically positions the child timeline and rebuilds
its internal voice/automation/filter histories as needed; outer note age
and outer state remain intact. Confirm that this definition of private
seconds (as opposed to unscaled authored child time) is the desired one.

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
playhead. No broadcast capability. Keep validation of nonnegative finite
seconds and native long frames. Document replay cost and rate-limit
diagnostics for private seeks, including rewind-to-zero.

## Implementation direction (not yet code)

Introduce distinct typed script absolute seek commands, e.g.
SeekPlaybackTimeCommand(TimeSpan) and SeekSourceFrameCommand(long), rather
than repurposing SetPlaybackOffsetCommand / SetSourceFrameOffsetCommand.
Keep native data effects, persisted serialization and low-level additive
offset API unchanged. Extend existing explicit-target validation,
flattened suppression, command dispatch, F6 compiler bindings and group
fanout only as needed; no new authored helper or unsafe raw surface.

Use sound-specific absolute-seek capability with actual invocation-relative
frame and render context. Avoid a blanket signed correction to PlaybackOffset
or resetting note age. Proposed direct Sample anchor is (target native
frame, pitch integral at seek deadline). With P(t) the existing accumulated
relative pitch position and k the fixed initial pitch × speed × native/output
sample-rate ratio, new position at t is:

    target + (P(t) - P(seekFrame)) * k

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
   seeks, mixed units, zero and exact finite end, loop mapping, silent
   channel, deferred deadline on replacement, no effects on displaced/
   sibling voices, cancellation. Test exact target sample and progression,
   note-age/envelope preservation and block invariance. Preserve
   legacy Oxx/SAx/Qxy tests; log separate counterexamples if found.
3. Private Pattern/Sequence: independent child PCM slice reference,
   actual accent at target, rewind-to-zero, mixed units, independently
   started group members, nonunit speed/pitch, inherited pitch mapping,
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
