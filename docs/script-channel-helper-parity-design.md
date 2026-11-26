# Script Pattern physical/virtual channel helper parity (#71)

Status: **owner-approved direction, 2026-11-26; implementation in progress.** The API below is a target contract, not a statement of currently implemented methods.
Baseline: merged PR #70, main `716c2092dc9d5f085205affb5beaaa3d4a988a14`.
Tracking: [issue #71](https://github.com/logiclrd/Heresy/issues/71).
Related: [virtual ownership redesign](script-virtual-note-ownership-design.md),
[current script API](script-programmability.md), [outstanding work](todo.md).

## Goal and boundaries

Make the ordinary Script Pattern control vocabulary consistent between a
physical channel, an owned VirtualNote, and an owning NoteGroup. A physical
channel remains a Pattern-local integer index mapped to its logical playback
owner and physical host. A virtual note remains an exclusive generation-
qualified lease within a Pattern invocation. A group remains a fanout of its
owned leases, not a bus or a new engine channel. Do not reintroduce numeric
virtual IDs, group.Note, implicit virtual flattening, or an arbitrary
NoteCommand submission API. Do not broaden the managed-code trust boundary.

This is parity of the **existing script vocabulary**, not exposure of every
native tracker effect. Global volume, overall channel volume, typed Filter
stage assignments/envelopes, tracker memory/slides, pitch automation, and
a procedural Instrument API are separate design questions. Scope/global
virtual Off/Cut broadcasts and global Tempo/Speed remain unchanged.

## Audited public surface and recommendation

| Operation | Physical today | VirtualNote today | NoteGroup today | Recommendation |
| --- | --- | --- | --- | --- |
| Start/retrigger | Note | NoteVirtual / note.Note | No | Keep distinction |
| Release / cut | Off / Cut | Off / Cut | Off / Cut | Existing |
| Event Filter | NoteFilter | NoteFilter | NoteFilter | Existing |
| Channel-output Filter | ChannelFilter | ChannelFilter | ChannelFilter | Existing |
| Note volume | — | Volume | Volume | Add physical |
| Native source frames | — | SourceFrame | SourceFrame | Add physical |
| Current note NNA override | — | Displacement | Displacement | Add physical |
| Past displaced notes | — | Past | Past | Add physical |
| Playback offset in seconds | Seek | — | — | Add virtual and group |
| Absolute spatial position | Pan | — | — | Add virtual and group |
| Surround routing | Surround | — | — | Add virtual and group |
| Legacy resonant low-pass | Filter | — | — | Add virtual and group |
| Legacy cutoff only | FilterCutoff | — | — | Add virtual and group |
| Legacy resonance only | FilterResonance | — | — | Add virtual and group |

Row semantics, optional event Filter IDs and physical start parameters
stay unchanged. Remove the virtual start `mixdown` parameter from both
NoteVirtual and VirtualNote.Note: recursively sourced virtual notes always
use private mixdown, inferred from the resolved source type. Direct Sample,
Synth and Instrument starts preserve ordinary direct-source behavior. NoteFilter and ChannelFilter still select or clear song Filter
definitions. The proposed Filter(cutoff,resonance) is specifically the same
normalized legacy resonant low-pass operation as the existing physical
helper, **not** a shorter alias for either definition-selection method.
Do not add a one-ID Filter overload. This is an explicit new capability
proposal, not a reinterpretation of the #54 ownership contract.

## Proposed signatures (not available until implemented)

Revised virtual start signatures (owner-approved):

```csharp
VirtualNote NoteVirtual(double row, ObjectId sourceId,
    double pitchMultiplier = 1.0, double playbackSpeedMultiplier = 1.0,
    double? volume = null, double timeOffsetSeconds = 0.0,
    ObjectId? eventFilterDefinitionId = null);
void Note(double row, ObjectId sourceId,
    double pitchMultiplier = 1.0, double playbackSpeedMultiplier = 1.0,
    double? volume = null, double timeOffsetSeconds = 0.0,
    ObjectId? eventFilterDefinitionId = null); // VirtualNote instance
```

Do not simply mark all StartNoteCommands Mixdown=true: the flag participates
in source resolution and OriginSourceId/live Synth preparation. Resolve the
source in the recursive preparation path before deciding whether to flatten;
only Pattern/Sequence on an owned virtual target is automatically private.
Keep direct sound IDs, snapshot/dependency resolution, Instrument tone
preparation and legacy/live engine commands unchanged. Decide the exact
internal representation during implementation (explicit typed intent or
source-aware normalization); no authored command escape hatch. Remove both
public and generated callsite overload parameters and update every bound
compiler signature/allow-list and test, including optional/named/positional
argument binding and delegate references. Existing scripts that pass mixdown
must delete that argument; do not retain a misleading compatibility switch.
No persisted Data Pattern schema change. Physical Note retains its existing
mixdown option. Test direct voice source IDs, live Synth routing, private
Pattern and Sequence retrigger, both ordering modes and F6. Preserve rejection
of unsupported raw legacy virtual flattening where applicable.

Physical PatternScriptProgram author-callable methods:

```csharp
void Volume(double row, int channel, double volume,
    double timeOffsetSeconds = 0.0);
void SourceFrame(double row, int channel, long sourceFrameOffset,
    double timeOffsetSeconds = 0.0);
void Displacement(double row, int channel, NoteDisplacementAction action,
    double timeOffsetSeconds = 0.0);
void Past(double row, int channel, TrackerPastNoteAction action,
    double timeOffsetSeconds = 0.0);
```

Identical instance signatures on VirtualNote and NoteGroup:

```csharp
void Seek(double row, double playbackSeconds,
    double timeOffsetSeconds = 0.0);
void Pan(double row, double x, double y = 0, double z = 0,
    double timeOffsetSeconds = 0.0);
void Surround(double row, bool enabled,
    double timeOffsetSeconds = 0.0);
void Filter(double row, double cutoff, double resonance,
    double timeOffsetSeconds = 0.0);
void FilterCutoff(double row, double cutoff,
    double timeOffsetSeconds = 0.0);
void FilterResonance(double row, double resonance,
    double timeOffsetSeconds = 0.0);
```

Preserve existing parameter names and defaults so named arguments behave
consistently. Validate finite row in [0,RowCount], physical channel in
[0,ChannelCount), finite normalized volume/cutoff/resonance in [0,1],
nonnegative native long frame offset, finite nonnegative representable
playback seconds, finite coordinates representable as float (not silently
clamped to [-1,1]), and defined enum values. Existing signed representable
wall-offset validation in the script receiver does not grant negative
offset support to incremental playback; the production incremental merger
still rejects negative offsets. Admit the new operations at zero and
positive fixed-wall offsets, applying at the resolved event deadline.
No new event ordering, timing quantization or parameter range.

## Source-backed engine inventory

| Command | Physical renderer and scheduler | Targeted virtual renderer and scheduler |
| --- | --- | --- |
| SetNoteVolumeCommand | Supported, including flattened logical source gain | Supported; idle scalar-memory lifetime caveat below |
| SetSourceFrameOffsetCommand | Supported; shared ISourceFrameSeekableSound helper, Oxx cost diagnostic | Supported; same native frame semantics |
| SetCurrentVoiceDisplacementActionCommand | Supported; recursive timeline also updates active flattened instigator policy | Supported |
| ApplyPastNoteActionCommand | Supported; origin physical host + logical owner, excludes virtual origins and current voice | Supported; full virtual identity + cursor owner |
| SetPlaybackOffsetCommand | Supported; writes current SoundState.PlaybackOffset | Not admitted/handled; shared current-voice operation is available |
| SetSpatialPositionCommand / SetSurroundCommand | Supported via PlaybackChannelState | Not admitted/handled; same state type and AttachVoice propagation already used |
| SetResonantFilterCommand / cutoff / resonance | Supported via PlaybackChannelState | Not admitted/handled; same base filter state and voice construction already used |

Source locations at the audited baseline:
- Heresy.Scripting/Runtime/ScriptProgramRuntime.cs: 437–525, 570–789
  physical helpers, metadata overloads, validation and Append;
  VirtualNote.cs and NoteGroup.cs have the existing typed controls.
- Heresy.Core/Patterns/IncrementalPatternTimeline.cs: 858–999 explicitly
  admits the four missing physical commands and positive wall offsets;
  six reverse-direction commands are currently admitted only on physical
  targets. Both admission checks must change for targeted virtual only,
  not scope/global broadcasts. Eager/raw processing must preserve
  equivalent payloads and not assume successful renderer delivery.
- Heresy.Render/Playback/PlaybackSession.cs: 1763–1871 virtual dispatch,
  1873–1888 shared native seek, 1919–2001 virtual voice construction,
  2099–2218 physical volume/spatial/filter/seek dispatch, 2448–2477
  physical NNA and Past, 3191–3235 physical displaced origin matching.
- Heresy.Render/Playback/PlaybackChannelState.cs: 89–145 defaults and
  remembered volume, 323–358 Pan/Surround interaction, 867–918
  filter setters and AttachVoice. Both kinds use the same state type.
- Heresy.Core/Sequences/IncrementalRecursiveTimeline.cs: 452–515
  flattened lifecycle and remembered gain; 562–647 flattened start.
- Heresy.Playback/PreparedIncrementalPlaybackFactory.cs: 505–601 tracks
  physical and virtual private mixdown displacement/Past independently.
- Heresy.Scripting/Compilation/ScriptPatternEmissionPolicy.cs and
  ScriptCompiler.cs: exact host-member authorization, strict emission
  shapes, generated callsite overloads and relaxed delegate rewriting;
  Heresy.UserInterface/Views/ScriptEditorHelp.cs: current public reference.

No runtime experiment or .NET test execution in this discovery slice.
The matrix is a source audit, not a claim that newly proposed APIs work.

## Approved musical direction

### Volume, seeking and note lifecycle

Volume sets the current note's normalized base volume and remembered
logical-channel note volume, not overall channel gain. It must also be
usable while the channel is silent, so a later start with no explicit
volume recalls it. An explicit successful note-start volume supersedes
remembered volume. A failed start does not seed volume. On a flattened
physical instigator, use the existing live source gain controller and
remember the value for future starts; do not separately apply it to each
descendant. This applies once, including to virtual descendants via
existing gain ancestry. Scope-wide broadcasts keep their existing semantics.

SourceFrame is the existing semantic Oxx native offset without the raw
tracker effect-memory/nibble encoding. It acts on the current bound sound
only if ISourceFrameSeekableSound is supported; otherwise no-op. Keep
direct/replay-required semantics and bounded cost warnings. Seek is the
existing seconds-based SoundState.PlaybackOffset operation, not a conversion
through the output sample rate or a replacement for SourceFrame. Preserve
existing source-specific seconds behavior. Both resolve the voice present
at the actual deadline, not necessarily the voice present when emitted.

Displacement overrides the currently sounding voice's next displacement
(Cut, Continue, Off, Fade), not a permanent setting for future replacements.
An idle channel does not seed a future NNA override. A physical flattened
instigator has an existing logical displacement policy; use that existing
scope behavior and validate Continue/Off/Fade/Cut, future producer behavior
and sibling isolation. Past (Cut/Off/Fade) applies to already displaced
logical notes with the matching origin, whether a single rendered voice,
a private mixdown, or a flattened source invocation. It does not affect
the current replacement, unrelated logical owners, or virtual generations.
Preserve existing private mixdown child lifecycle bookkeeping and release/
fade durations. See the expanded flattened-source contract below.

### Spatial and legacy resonant filter controls

Pan uses absolute scene coordinates, like existing physical Pan. Surround
true resets base position to zero and enables surround; Pan disables
surround. Surround false disables surround without inventing a previous
pan position. Persist on the same logical virtual identity across starts,
including silent gaps. Each new voice inherits current logical settings.
Already displaced voices retain their own prior state. Legacy Filter
sets cutoff and resonance together; single-parameter controls preserve
the other value; default is the existing disabled filter. They are
independent of event Filter and persistent output Filter selection and
history. No new DSP algorithm or surround routing model is proposed.
Verify mono/stereo/5.1 behavior through existing renderer routes.

For private mixdown sources, apply spatial/filter controls to the outer
voice's output, not separately to its children; do not add a hidden
flattening mode. NNA and past-note handling use the existing private
renderer control path.

### Intentional flattened-source boundary

A flattened physical Pattern/Sequence has a logical source controller,
not a single rendered sound. Volume and Displacement already have
logical-instigator semantics; NoteFilter and Off/Cut already have
scope-aware semantics. Seek and SourceFrame must not arbitrarily pick a
descendant or seek an entire flattened subtree; there is no single bound
seekable voice to seek. Use an explicit mixdown for single-voice seek.
Physical Past currently matches displaced rendered voices by host and
logical owner; there is no equivalent past-flattened-instigator registry
in the inspected incremental timeline. **Owner decision 2026-11-26:** Past
must target the displaced logical note, including a flattened Pattern or
Sequence, rather than the individual note events it emits. This supersedes
the initial discovery proposal to defer that capability.

A replacement (ordinary, mixdown or flattened) displaces the previously
current flattened instigator according to its existing NNA policy. Track
that former logical note by its unique source scope and parent logical
physical-channel identity (playback owner, mapped host), not by child host,
source object ID, or cursor-local channel number. Continue leaves its
producer and all descendants running; Off/Fade stop future production and
release/fade existing descendants; Cut stops production and cuts them.
A later Past on that parent channel targets all still-live displaced
logical notes of that origin, including descendants of old flattened
instigators whose producers have already completed but whose rendered
voices remain audible. The current flattened scope and its descendants
must not be targeted. Nested descendants, scoped virtual handles, migrated
NNA voices, and private recursive children inherit scope ownership; use the
existing scope-aware lifecycle dispatch rather than indiscriminate host
broadcasts. A child-channel Past remains local to that child's logical
channel and does not affect other descendants of its parent's current
source.

Resolve at the actual wall deadline. Both sequencing (stop future producers
and pending event emission) and renderer/private mixdown lifecycle must
participate. Existing renderer scope controllers are retired at producer
completion while descendants may still reference them; a registry that
only tracks active timeline frames would lose past notes with audible tails.
Conversely, keeping every historical scope ID until the song ends is not
acceptable. Design explicit producer and audible-descendant retirement
signals or equivalent bounded ownership before implementation. Distinguish
displacement from an ordinary Off/Cut on the current source. Do not double
apply a release/fade via both direct voice origin matching and scope
ancestry, or use the NNA-specific fade duration in place of ordinary Past
Fade without an explicit semantic decision/test. Preserve raw tracker S7x
compatibility for ordinary voices; resolved physical Past should consistently
use logical-note semantics, with regression coverage for Data Patterns too.
The exact internal protocol remains to be designed in a dedicated slice.

Test two or more displaced flattened invocations, a current replacement,
same host in a sibling owner, a child-owned displaced voice, delayed
Past across row boundaries, producer naturally completed but audible tails,
already-released/fading descendants, nested mixdown and flattened sources,
all three actions, and bounded retirement under repeated replacement.
Existing physical Pan, Surround and legacy Filter do not become new
flattened-subtree effects. Seek and SourceFrame retain the single-voice
boundary described above.

## Important lifetime finding: idle virtual state

PlaybackSession.RenderSegment (baseline lines 3490–3564) removes
targeted and scoped PlaybackChannelState entries when the current voice
is absent and the anti-click tail has completed. This is independent
of producer lease ownership and the scheduler final-use barrier.
GetVirtualChannelState / the scoped equivalent create a new default
PlaybackChannelState when necessary. The separately keyed channel-output
Filter already has a final-use retirement protocol, but the ordinary
scalar channel state does not.

Consequently an owned handle can issue Volume, wait through silence,
then retrigger with no explicit volume and lose the remembered value
when the channel state was discarded. The same mechanism would discard
newly exposed position, surround and resonant filter settings. This is
a concrete source-level defect candidate requiring a red regression;
do not claim measured PCM failure until a test runs. It can affect
chunk invariance and delayed starts. This is not a reason to retain
every old DSP channel forever.

Proposed fix: keep compact generation-qualified **remembered controls**
separate from active PCM channels, only for generated scoped identities
where needed. A sidecar can contain the base volume, base position,
surround and legacy cutoff/resonance, keyed by (cursor owner, full
VirtualChannelIdentity). Establish/update it at accepted control/start
application, including idle changes; avoid allocation for default/no
state where practical. Restore it when a new active channel is needed.
Do not copy mutable filter histories or per-voice NNA state into it.
Use the existing producer/scheduler/consumer final-use notification to
remove remembered controls immediately after the last accepted event
is applied or discarded. Active voice/tail state keeps what it needs
independently. Do not re-create remembered controls when an already
retired identity's old NNA tail needs a temporary host. No per-generation
tombstones or historical map; no scan of idle scalar records in PCM,
no synthetic musical events. Alternative compact implementation is
acceptable if it meets the same bounds and behavior.

Do not alter generation-zero/legacy numeric engine lifetime semantics.
Cancel, stop-producing, abandoned prefetch, natural completion, private
rewind and delayed deadlines must all observe the existing final-use
barrier. A lease release is not itself the final event; silent memory
cannot be discarded until no scheduled reference can need it. An
untouched silent owned lease need not materialize DSP state at all.
A still-open generated identity with nondefault remembered controls is
legitimate retained state; its memory bound is live/future-reference
overlap, not cumulative allocation count. Relaxed collection remains
intentionally buffered. Existing filter retirement and voice tail policy
are not replaced.

## Compiler, scheduling and group contract

- Bind only the approved host members; update exact compiler authorization
  and emission policy, including generated-only SequenceCallsite overloads.
  Reject metadata overloads, arbitrary command construction and new access
  in Sequence or scalar scripts. Preserve existing _O reference discovery.
- Strict: direct statements, one raw event per physical/handle action,
  one event per member of a group, and yield before the next authored
  statement. No callback/method-group emission in strict mode.
- Relaxed: synchronous callbacks and approved delegates, transactional
  generation, stable equal-row ordering and existing cancellation/work
  budget. Do not attempt cross-mode RNG or authored-order equivalence
  for different source programs.
- F6: full original invocation span, per-member fanout same source span,
  active authored delegate invocation precedence and existing unambiguous
  bound-method reference fallback; ordinary playback does not acquire
  editor metadata.
- Ownership checks precede handle/group action, including disposed or
  transferred wrappers. Retain warning-only behavior for invalid ownership
  and normal validation for valid live targets; group fanout prevalidates
  arguments before first emission. No new group channel or broadcasts.
- Scheduler: add targeted virtual dispatch and positive fixed-wall
  admission for the six reverse-direction commands, preserving stable
  scheduling and generation-qualified final-use references. Keep raw
  broadcasts limited to their existing allowed commands. No negative
  incremental offsets, synthetic repeats or tracker memory expansion.

## Proposed slices and acceptance

1. **Idle state red/green.** Existing Volume then silent-gap retrigger;
   idle command before a future start; explicit start override and failed
   start; old-generation delayed controls after slot reuse; same local IDs
   in siblings; owner completion, cancel, prefetch abandonment, private
   rewind; thousands of generations, bounded remembered state, zero
   after final-use; compare uninterrupted and differently chunked PCM.
   Test nondefault remembered controls without retaining silent buses.
   Keep generation-zero behavior and existing #54 stress tests.
2. **Physical helpers.** Four direct wrappers and metadata variants using
   existing commands; input validation, exact author-callable surface,
   strict/relaxed raw/PCM tests, deferred actions targeting replacement,
   native sample/private mixdown seeking, physical owner-specific NNA/Past
   and flattened source Volume/Displacement and the intentional Seek
   boundary. Stage flattened logical Past separately (2b): design and
   implement producer + renderer retirement and scope-qualified action,
   verify ordinary tracker S7x and both physical/virtual origin isolation.
   Preserve tracker Oxx/S7x tests. If source-level discrepancies
   are uncovered, resolve against these approved semantics before widening
   capability.
3. **Virtual start simplification (2c).** Remove mixdown from the virtual
   start APIs, infer private recursion only for recursive sources, and
   migrate compiler bindings, scripts, test fixtures and help. Keep direct
   source resolution and live editing behavior, physical mixdown, legacy
   engine commands, and exact F6 provenance. This may land separately from
   flattened Past; no dependency on new spatial controls.
4. **Reverse parity.** Six methods each on handle/group, shared argument
   validation, targeted virtual dispatch/admission; demonstrate persistent
   spatial/surround/legacy-filter state across silence, partial parameter
   updates, NNA isolation, private mixdown outer voice behavior, group
   equality with explicit handles and unrelated sibling isolation,
   disposal and deferred old-generation safety. Include seconds vs frames
   seek and positive offsets for every new method.
5. **Integration and final acceptance.** Strict/relaxed F6 source spans,
   method-group equality and unauthorized metadata refusals; mono/stereo/
   multichannel PCM, actual realtime/F6/export parity, chunk invariance,
   nested private and flattened hierarchies, final-use stress. Update
   in-editor reference and current programmability docs only when actual
   methods land. Exact-head Release/ARM64 CI, owner desktop audition
   and explicit approval before merge.

Owner approved the revised logical Past and inferred virtual mixdown
on 2026-11-26 and authorized proceeding. Full parity and the remembered
scalar state approach remain the implementation baseline. The detailed
flattened Past lifetime protocol and fade handling must be established
and tested before widening behavior. This document does not promise new
global/overall volume or arbitrary tracker-effect API.
