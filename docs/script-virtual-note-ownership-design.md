# Script Pattern virtual-note ownership redesign (#54)

Status: single-handle and initial owning-group implementation committed, awaiting exact-head CI and final acceptance.
Base: merged #69, main `1cfad6012107fdfc1d6bf48de8d993a133b86a67`.
[Issue #54](https://github.com/logiclrd/Heresy/issues/54) is authoritative;
this document records the implementation choices and boundaries. The single-handle and initial group APIs have been implemented; final acceptance remains.

## Public scripting surface

Replace the caller-selected virtual ID, not the underlying virtual-channel
capability. `NoteVirtual(row, sourceId, pitchMultiplier = 1.0, ...)` returns
an exclusive `VirtualNote`. Its remaining parameters/defaults match the
existing helper: playbackSpeedMultiplier, mixdown, volume, timeOffsetSeconds,
eventFilterDefinitionId. `VirtualNote.Note(row, sourceId, ...)` retriggers
on the same logical channel. It is not named Replace. The wrapper is a
reference object so ownership changes invalidate all aliases at once.

| Existing virtual helper | Replacement |
| --- | --- |
| NoteVirtual(row, virtualId, sourceId, ...) | NoteVirtual(row, sourceId, ...) returning VirtualNote |
| NoteVirtual on an existing ID | note.Note(row, sourceId, ...) |
| VolumeVirtual | note.Volume |
| SourceFrameVirtual | note.SourceFrame (native source frames, not seconds) |
| DisplacementVirtual / PastVirtual | note.Displacement / note.Past |
| OffVirtual / CutVirtual | note.Off / note.Cut |
| NoteFilterVirtual / ChannelFilterVirtual | note.NoteFilter / note.ChannelFilter |

The two Filter assignment commands have different semantics. Do not silently
turn the existing physical resonant Filter(cutoff, resonance) into a virtual
capability or conflate note and channel filter assignment. If a shorter
`Filter` alias is desired, settle its meaning explicitly rather than
inventing a new musical operation. The existing no-ID scope/global
Off/Cut broadcasts may remain distinct from the owning group API; they do
not grant a numeric-ID lookup or a way to reacquire a transferred lease.
They must never be used to implement group fanout.

`NewNoteGroup()` constructs a mutable owning group; `group += note`
moves the underlying lease, invalidating the source wrapper permanently.
A group forwards existing-note controls only; no group Note. Transfer
validates destination and source before mutation; duplicate, disposed,
transferred, or cross-invocation ownership is warning-only, with no
musical commands. The same group instance remains the destination.
Explicit Dispose is idempotent, including on a transferred wrapper.

The approved examples use using blocks and compound assignment. Roslyn
must be tested for readonly using-local compound assignment: do not assume
an ordinary static operator + returning the same object permits reassignment
of a using variable. Investigate the compiler's supported instance compound
assignment operator or an explicit trusted lowering that preserves the
authored semantics. The approved syntax must work, not merely a workaround
in the editor examples.

## Identity and lifetime

Keep three independent lifetimes:

1. An invocation-local allocator owns live leases, with an immediately
   reusable slot on release. Disposal is bookkeeping, not a musical event.
2. Each emitted virtual target carries an immutable identity. Pending
   events and old voices keep that identity after lease release.
3. Audio channel state, displaced NNA voices, output-filter history,
   and anti-click tails retire according to actual audio lifetime.

Proposed internal identity: a value type with uint slot and ulong
generation; generation zero reserved for existing engine/preview targets.
Use an invocation-wide monotonically increasing generation counter, with
explicit exhaustion check, and a free-slot stack. No per-slot historical
generation map or per-lease tombstones; bookkeeping scales with peak
simultaneous ownership. The renderer's effective identity is the existing
cursor owner plus the full value. Never derive a new generation from a
currently sounding voice. Preserve legacy non-script uint entrypoints
where necessary for live audition and data/engine tests; scripts do not
receive that ID or generation. Do not narrow a generated identity to uint
in a resolver, sorting key, displaced voice or renderer cache.

Lease ownership state is distinct from audio. A retained silent wrapper
must not keep an otherwise unnecessary PlaybackChannelState alive.
An old generation may still have events after the script has disposed it,
even after a reused slot has started a new voice. No Off, Cut, Fade or
other musical command is injected on disposal.

### Reclamation proof obligation

PlaybackSession currently removes empty _scopedVirtualChannels after
voice and anti-click completion, but _scopedVirtualOutputFilters is a
separate keyed map and is still flushed for every segment. Merely widening
the key would make old generation filter entries accumulate indefinitely.
Also, early deletion of persistent channel filter configuration would break
a valid later retrigger on the same lease. Design explicit non-musical
retirement/last-reference bookkeeping: a generation may retire only after
no additional authored emission can refer to it, all scheduled uses (including
deferred wall offsets and prefetched events) are consumed/canceled, and
its voice/filter/anti-click tails no longer require the state. Relaxed
collection can determine complete raw event horizons before replay; strict
collection cannot predict future author actions. Natural cursor completion,
cancellation, and source rewinds must all participate. Do not retire at
Dispose alone, or assume row order is wall-deadline order. Need tests to
choose/validate the sideband representation before broad implementation.
No per-frame scan over historical released leases, no indefinitely growing
identity-to-retirement journal, and no change to legacy non-script filter
semantics.

### Internal foundation and final-use barrier (slice 2a)

The internal value is now `VirtualChannelIdentity(uint Slot, ulong Generation)`.
Generation zero remains the legacy numeric engine target. `ChannelTarget`
carries the full value; its compatibility numeric accessor is not a key.
Core eager comparison, renderer current/scoped channels, NNA origin,
filter completion keys and buses, and private mixdown tracking use the
full identity. `VirtualChannelLeaseAllocator` owns a sparse live-slot
map, free stack, next-slot counter and invocation-wide generation counter.
It rejects stale releases and checks slot/generation exhaustion before
mutation. No historical generation map is needed. At slice 2a the scripting runtime
did not allocate these leases yet; cross-invocation wrapper/allocator
ownership must be checked at transfer, not inferred from equal local IDs.

The renderer now has a non-musical `RetireScopedVirtualChannel` final-use
endpoint for nonzero generations. Only materialized output filters with
a confirmed final-use signal enter retirement bookkeeping; reclaim them
after their current channel, displaced origin voices and filter tails
finish. An unmaterialized or already reclaimed generation leaves no
tombstone. The endpoint does not change the existing generation-zero
filter policy, cut sound, or imply that a lease release is itself enough
to retire a filter.

### Producer/scheduler retirement protocol (slice 2b)

A trusted `RawPatternStep.ReleaseVirtualChannel` is an enumeration-order
sideband, not a musical position, command or progress marker. The invocation
runtime has a lazy allocator and release queue. Strict compilation drains
successful releases after yielded actions/cooperation (including dropped
backwards emissions), and again after normal iterator completion, since
disposal/finally itself cannot yield. Relaxed replay projects releases
immediately after each identity's last **sorted** raw reference, independent
of authored disposal order. Legacy targets produce no markers.

The cursor accounts only accepted, validated generated virtual events
entering row preparation. A raw lookahead cannot be overtaken by a later
release marker; skipped/filtered events never acquire an accounting entry.
A marker or producer completion closes the generation. A reference moves
from the prepared row into the fixed-wall deadline queue, or departs when
resolved/dropped. Generated virtual operations are already restricted to
the non-repeating, non-tracker-synthetic subset, and cannot be introduced
by replay-safe tracker producers. A completed cursor remains referenced
by its deferred deadlines until drained. Cancellation abandons accepted
references after dropping the cursor's pending deadlines; no historical
generation tombstones are kept. A producer must never emit for a released
identity (the forthcoming wrapper ownership checks enforce this contract).

The final-use notification is forwarded by Pattern cursor identity to
PreparedIncrementalAudioSource. It is held until the current or prefetched
Emit (including an editor-deferred Emit) has been applied/discarded.
Producer-only stop drops future work without requesting audible cuts;
the renderer retains already sounding voices and tails. Natural completion,
subtree cancellation and fresh private timelines do not reuse old renderer
identities. The standalone row processor ignores non-musical markers; the
eager raw path returns schedules, not renderer final-use callbacks.

Regression coverage includes explicit/implicit closure, a delayed action
past the final row, starting-row skips, cancellation, release queue and
sorted replay projection, silent-filter survival through a delayed note
start, and discarded prefetch reclamation. The public scripting surface
still uses numeric IDs; the trusted internal lease APIs and test raw
producers exercise the foundation. Actual handle/group ownership validation,
cleanup warnings, full generation-qualified private mixdown/rewind and
eager/export parity remain acceptance work after the public API switch.

Tests in this foundation exercise one million allocations at peak eight
leases, old-generation deferred Cut/Past with a new generation and
sibling sounding, separate same-slot filter/NNA identities, and explicit
final-use retirement with PCM tail parity and no retained tombstones.
Actual CI status is tracked on the PR.

### Single handle implementation (slice 3a)

The draft branch now supports `NoteVirtual(row, sourceId, ...)` returning
a sealed `VirtualNote`, with private owner and generation-qualified
identity. The older numeric helpers were temporarily callable during fixture migration; slice 3b removes them. The new handle offers Note, Volume, Off,
Cut, SourceFrame, Displacement, Past, NoteFilter and ChannelFilter.
No ambiguous Filter alias or virtual resonant-filter capability is added.

A Pattern or Sequence used as the source of a virtual note must specify
`mixdown: true`, both for the initial NoteVirtual and any subsequent Note
that selects such a source. Flattened children require a physical parent
channel under the existing recursive timeline contract; the handle redesign
does not implicitly change the requested mixdown mode. Direct Sample,
Synth and Instrument sources do not require this flag solely because the
note is virtual. The diagnostic now identifies the explicit mixdown option.

The first combined CI run found six scripting failures caused by top-level
using declarations parsing differently at standalone submission scope. The
reference analyzer now falls back to a valid block interpretation and maps
references back to original offsets. Compiler statement audits and rewrites
use the actual method-body context. The fix adds reference-span and private
mixdown retrigger regressions; exact-head CI remains required.

A handle validates the live allocator lease before every musical
operation. Dispose invalidates all aliases, releases immediately and
never emits a musical event. Invocation cleanup invalidates and reclaims
unreleased leases even on exception or suspended-iterator disposal.
Ownership misuse reports bounded HRSEQ011/012 warnings and does not
append a command; cleanup of unreleased ownership reports the same
warning category. HRS2101 is a conservative source warning for an
unproven ownership termination: using is accepted; simple direct
local Dispose is recognized; aliasing and more complex control flow
can warn conservatively. Group transfer analysis is not implemented yet.

The canonical compiler authorizes only the exact VirtualNote methods
and implicit Dispose implementation, not constructors, metadata or
trusted runtime entrypoints. Strict emission permits a single direct
local initializer (including using declarations and using statements)
as well as direct action statements. Yields are inserted into the
actual method-body shape, without wrapping a local declaration in a
new scope; for using statements the initial yield is inside the using
body. Invalid ownership operations yield no event. F6 direct
initializer and instance method invocations use their original full
span; ordinary execution receives no metadata. The existing relaxed
delegate stack remains in use. Indirect bound-instance method-group
fallback and group-specific callsite coverage remain final F6 work.

Added compile/runtime tests exercise the complete single-note method
set, eager/streamed parity, slot reuse, invalid aliases, using
cancellation, warnings and original source spans. Initial CI found parser defects; slice 3b migrates numeric-script tests and examples and removes old numeric entrypoints before NoteGroup work. Full production
mixdown/rewind and export checks remain outstanding.

### Numeric script API removal (slice 3b)

The numeric script-facing overloads of NoteVirtual, NoteFilterVirtual,
ChannelFilterVirtual, VolumeVirtual, SourceFrameVirtual,
DisplacementVirtual, PastVirtual, OffVirtual and CutVirtual have been
removed, including their F6 metadata variants and compiler policy
entries. No compatibility path remains for scripts. Numeric generation-zero
engine targets and separate scope/global broadcasts are unchanged.

Existing scripting, production playback, filter, F6, pitch and export
fixtures now exercise owned handles. The editor reference and current
programmability documentation describe the new API and explicit
mixdown requirement. Negative compilation tests reject each retired
entrypoint in eager, strict, relaxed and traced compilation. Historical
milestone descriptions are not an alternate API contract.

Owner accepted manual Synth retrigger and explicit private Pattern
mixdown auditions. CI #45736135524 passed all other suites and 400/401
scripting tests, including the two new production private mixdown
tests. The remaining failure was a missing final brace when rewriting
a using statement on one line; extraction now uses actual Roslyn
brace-token spans rather than fixed-length suffix trimming. Combined
exact-head CI must validate the fix and migrated fixtures before
starting group transfer. No full generation-qualified private rewind
or all final stress/F6 cases have been claimed yet.

### Owning NoteGroup (slice 4)

A sealed NoteGroup owns a list of underlying invocation-local lease
identities, never the VirtualNote wrapper. The C# 14 instance `operator +=`
mutates the same reference-type group in place. Transfer validates both
destination and wrapper ownership (including invocation reference equality),
adds the identity to destination storage, then permanently invalidates
the source wrapper. No slot is released or reacquired. Dispose on the
transferred wrapper is harmless. Invalid transfer and group-control use
share bounded HRSEQ011/012 warnings. Disposal of the group releases its
leases without cancelling any already scheduled audio.

#### Using-local compound assignment correction

Native C# 14 rejects even the void instance compound assignment on
readonly using/foreach variables (CS1656). Keep the authored Heresy
syntax `using var chord = NewNoteGroup(); chord += note;` and
`using (var chord = NewNoteGroup()) { chord += note; }`.

Before canonical binding, inspect the original wrapper to identify a
direct += statement on a single-variable using declaration of the exact
NoteGroup type with a VirtualNote-compatible RHS. Only the CS1656 error
on the matching left operand is waived. Other original C# errors,
including reassignment or ref/out use of the using variable, still fail.
Original restricted syntax and wrapper boundary checks remain mandatory.
Lower the declaration to a mutable local plus an immediately adjacent
compiler-owned using alias capturing the original instance; using
statements become a new enclosing block retaining their original body.
The alias name cannot collide with any authored identifier (including
escaped identifier spellings). Do not change ordinary using locals or
add a foreach extension. The canonical bound-operation audit still
authorizes the actual instance operator and every authored member.
Ownership analysis recognizes the exact generated using alias and its
captured symbol as proof of disposal, including transfers to it. A
boundary map projects canonical diagnostics and F6 spans back to
unmodified authored text, including same-line and CRLF source.

The host PCM regression uses double-dollar interpolated raw strings
so embedded script blocks have ordinary braces. The native cross-
invocation test uses a normal group local and a separate using alias.
The owner verified the suggested chord audition with a workaround
for native C#'s using restriction; direct Heresy using/+= and new
scope/collision/readonly/source-span regressions await exact-head CI.

The group exposes Volume, Off, Cut, SourceFrame, Displacement, Past,
NoteFilter and ChannelFilter, but no Note operation. It does not produce
scope/global broadcast targets. In eager/relaxed mode forwarding is
synchronous in membership order. Strict mode retains one fanout cursor
over the group list, command and callsite, and drains through the existing
single-event receiver; the next authored statement does not execute until
all member events have yielded. The generated drain loop is annotated so
it does not count as an authored CPU cooperation loop. Cancellation of a
suspended iterator executes lexical using cleanup and abandons the
unemitted suffix. The scheduler retains any accepted earlier events and
applies normal generation-qualified retirement accounting.

Compiler authorization is exact-symbol based for NewNoteGroup, the
instance compound operator, approved group controls and Dispose. The
strict statement validator accepts a direct NoteVirtual RHS only when
the bound compound assignment is the approved group operator. F6
instrumentation injects the original complete invocation span into
the group method, reused on all forwarded events; the initial inline
NoteVirtual retains its own span. Conservative source warnings recognize
direct transfer to a lexical using group or simple explicit disposal.
Complex flow and aliases can still warn; runtime checks remain essential.

Tests cover using declarations and blocks, transfer of existing and inline
handles, group-only controls, invalid and cross-invocation transfers,
alias invalidation, group disposal and slot reuse, cancellation halfway
through a group operation, ordinary/traced strict/relaxed command parity,
source spans and explicit-handle PCM parity including delayed cuts.
Editor help includes the group contract. No group test execution is
claimed yet. The previous migration head's CI passed all 4,602 tests and
ARM64 validation, but its final build was canceled by subsequent pushes.
Exact-head CI and manual group audition are required; final F6 timeline,
bound-instance delegate fallback, stress, private rewind and export
acceptance remain.

## Compiler integration after #58 and #69

The issue's older observation that using/member calls are prohibited is
superseded: canonical Roslyn IOperation validation now supports managed
data, synchronous using, approved bound members and LINQ. #69 adds strict
and relaxed Pattern modes. Expand the exact bound-symbol host allow-list
for the two opaque wrapper types and approved methods, IDisposable
implementation and compound assignment operator only. Constructors, arbitrary
host properties, metadata helpers, forged identity, other host members and
reflection remain unavailable. Audit before trusted rewrites.

Strict mode still has a single pending-event slot. A NoteVirtual initializer
must create the lease and emit its start, then suspend before the next
authored action. Handle method statements must suspend after their event.
Group operations must enumerate/forward one event per suspension, not
append multiple events to the same pending slot. A using initializer
requires a scope-preserving lowering: ensure disposal even on cancellation
while suspended at the initial emission. Compound assignment must preserve
receiver/argument evaluation, transfer semantics, and the original span.
Prove the permitted language shapes rather than attempting unrestricted
expression splitting across iterator suspension. Relaxed mode uses its
existing transactional synchronous collector, so callbacks and group
fanout can append normally; maintain the same ownership rules and stable
sorting. No callback is permitted to outlive its invocation.

F6: use original full authored NoteVirtual/handle/group invocation spans,
including initializer and transfer contexts; all member emissions of one
group action share its span. Respect #69 active delegate invocation override
and framework-callback fallback. Metadata-only runtime entrypoints cannot
be authored; ordinary playback does not acquire F6 instrumentation.

Compile-time warnings should be conservative: demonstrably unowned return
values and unproven termination; known transfer/Dispose/using ownership
paths; avoid claiming complete alias analysis. Runtime invalid operations
must be suppressed with a bounded diagnostic (use the existing
SequencingDiagnosticLog pattern with dedicated codes/counter); no silent
ownership errors. Invocation cleanup reports/reclaims still-owned leases
even if a script loses its last wrapper reference, and cannot affect already
scheduled musical commands. Cleanup works across normal completion,
exception and iterator disposal.

## Source migration inventory

The following is the first pass at the exact merged baseline, not a claim
that grep exhaustively proves every reference. Repeat semantic/build
inventory during migration.

- Core: ChannelTarget (uint VirtualChannelId), NoteEvent and
  PatternNoteProcessor.CompareTargets; raw and resolved scheduler/cursor
  paths, deferred deadlines and scope/cancellation/broadcast behavior.
- Render: PlaybackSession scoped channel and output-filter dictionaries
  (currently (long Owner, uint Id)), anti-click cut and displaced voice
  routing, filter fork/buses/tails, PlaybackVoice.OriginVirtualChannelId,
  cancellation and output retirement. Preserve targeted legacy virtual
  preview behavior. Inspect all equality, seed, sort and cache key uses.
- Playback: PreparedIncrementalPlaybackFactory scoped/private mixdown
  tracking and instrument binding, PatternLiveEventCompiler and other live
  preview entrypoints (retain non-script numeric behavior).
- Scripting: ScriptProgramRuntime existing helpers and F6 overloads,
  ScriptPatternEmissionPolicy, ScriptCompiler canonical checks and strict/
  relaxed rewriters, ScriptRuntimeOperationVerifier using/member/operator
  authorization, wrapper containment; add allocator/opaque owner runtime.
- UI/docs: ScriptEditorHelp, script-programmability, script-pitch-helper-design
  examples, reference and source-analysis diagnostics/highlighting.
- Existing script fixtures to migrate: ScriptVirtualChannelTests,
  ScriptVirtualControlTests, ScriptVirtualPlaybackTests,
  ScriptVirtualControlPlaybackTests, ScriptPitchMathTests,
  EditorPatternCallsiteTests, EditorPatternPlaybackTraceTests,
  ScriptInstrumentPlaybackTests, PlaybackExportPcmParityTests.
- Renderer/Core fixtures using numeric ChannelTarget.Virtual for internal
  behavior should remain valid or migrate only where explicitly testing the
  generation-qualified form. ChannelFilterNnaForkTests and
  FmSynthNnaVirtualLifecycleTests assert displaced origin identity.
- Diagnostics: SequencingDiagnosticLog presently reserves HRSEQ001–010;
  use a dedicated bounded ownership-warning category, not the out-of-order
  or filter-error counters.

## Implementation gates

1. **Design/inventory (this slice):** baseline and public mapping above;
   verify readonly using-local operator semantics and choose retirement
   sideband at the relevant implementation gate.
2. **Internal identity and allocator:** generation-qualified value carried
   through all Core/Render/Playback boundaries, legacy preview parity,
   explicit overflow and bounded allocator tests. Before changing script API,
   prove old delayed commands, sibling scopes, same-slot overlap, NNA,
   private mixdown and filter retirement isolation with renderer tests.
3. **Single handle:** exclusive lease ownership, NoteVirtual returns
   handle, retrigger and controls, disposal/cleanup, warnings, strict
   initializer lowering, relaxed callbacks. Migrate numeric script fixtures.
4. **Owning group:** transactional transfer/aliases, per-member fanout and
   strict suspension, disposal, invalid ownership diagnostics and group
   Note rejection.
5. **F6 and integration:** full original span propagation, manual editor
   audition, reference migration, randomized/stress and nested/repeated/
   export PCM parity, exact-head CI and explicit owner merge approval.

Keep one draft feature PR until the entire redesign is accepted. A
production identity migration, API switch, or unverified retirement
mechanism is not implied by this design-only first commit.
