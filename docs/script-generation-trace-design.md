# Script Pattern generation trace — discovery and design (#68)

Status: design slice, not implemented. Owner UI/instrumentation decisions in
[issue #68](https://github.com/logiclrd/Heresy/issues/68) are authoritative.
Source audit at main `1224757b9ffaf88bca9dafa382aaba886aeb686f` (merged #74;
4,803 pre-merge tests). Implementation choices below are proposals to be
verified by focused prototypes; do not weaken the owner contracts to make a
prototype pass.

## 1. Purpose and non-goals

Explicit Generation Trace action in Script Pattern editor runs a diagnostic
invocation of the current authored Pattern, without playing anything. Inspect
emitted commands in execution order, sorted row order, or grouped channel order;
show partial emissions even if generation fails. Relaxed playback remains
transactional; diagnostic traces are never published to playback, even on
success. Do not trace recursive expansion, wall-clock scheduling, PCM, child
Pattern invocations, or ongoing F6 playback in this first feature. The trace
describes this Pattern's raw generation, not the eventual song schedule.
Support strict and relaxed modes, with their actual compiler restrictions.

Three compilation variants: ordinary, existing F6 callsite, and new diagnostic.
No additional generated instructions, runtime trace checks, bookkeeping fields
requiring initialization, allocations, or event metadata on ordinary or F6
paths. No tracing state on NoteEvent, PlaybackSession or device/PCM threads.
No new public authored APIs. Keep trace-specific generated code and cache
identity separate, including any prepared-program cache; don't globally
enable diagnostics or route normal preparation through a trace receiver.

## 2. Verified source touchpoints

- `Heresy.Scripting/Compilation/ScriptCompiler.cs`: CompileIncrementalPattern
  (L299ff) analyzes order directive, lowers group using scopes, audits original
  authoring and binds exact emitter/transfer symbols before rewriting. Relaxed
  builds ExecuteScript; strict builds EnumerateScript with per-helper drains.
  InstrumentRelaxedPatternCallsites uses source-span projection, delegate
  adapters and trusted fallback scopes. Its ambiguity rule deliberately
  preserves method-group delegate equality. StrictPatternEmitterRewriter
  handles statements, single local initializers and direct group += allocation.
  LoopCheckpointRewriter adds budget/cooperation checkpoints and excludes
  trusted fanout drains. Do not instrument those drains as authored loops.
- CompiledRelaxedPatternGenerator.Collect (L2450ff) calls ExecuteRelaxed,
  freezes NoteScheduleBuilder, sorts by RowOffset then EmissionOrder, checks
  cancellation, and publishes only on success. RelaxedVirtualChannelReplay
  is subsequent scheduling housekeeping, not authored emissions.
  CompiledIncrementalPatternGenerator.Enumerate (L2390ff) drains RawPatternStep,
  drops earlier rows with diagnostic, publishes lease releases after boundary
  event, advances rowCount on completion. Strict filtering happens *after*
  the helper emitted its raw event. Diagnostic must not lose rejected events.
- `Heresy.Scripting/Runtime/ScriptProgramRuntime.cs`: PatternScriptProgram
  owns budget, context, INoteReceiver, virtual allocator, pending fanout,
  and editor callsite. Append validates wall offset, checkpoints and emits
  NoteEvent with one command. Normal helper behavior is shared. Strict
  IncrementalPatternEventReceiver is sealed and HasPendingEvent performs
  group fanout lazily, one member per drain. Swapping in a generic collecting
  INoteReceiver would change semantics! Keep that concrete receiver in
  diagnostic strict execution. ExecuteRelaxed/Enumerate finally clean leases.
- `Runtime/VirtualNote.cs` and `Runtime/NoteGroup.cs`: sealed handle types,
  not subclassable. NoteGroup owns a list of generation-qualified leases;
  += validates transfer then invalidates source wrapper without releasing
  lease. Dispose releases ownership only. ControlVirtualGroup dispatches
  synchronously in relaxed mode and retains a fanout cursor in strict.
  NewNoteGroup does not emit a musical event. First NoteVirtual emits the
  start before returning the wrapper. No group channel exists.
- `Heresy.Core/Sequencing/NoteScheduleBuilder.cs`: receiver assigns increasing
  EmissionOrder and enforces 1,000,000 generated note cap. ScriptExecutionBudget
  also bounds synchronous units and checks 10-second runaway budget every
  1024 units. Strict CPU-only cooperation can continue indefinitely; diagnostic
  collection needs its own finite stopping policy. ChannelTargetKind covers
  Global, Physical, Virtual, AllVirtualInScope and AllVirtual. Full virtual
  identity is Slot + Generation within invocation, never just Slot.
- `Compilation/PreparedRoslynIncrementalScriptSources.cs` and
  `RoslynIncrementalScriptSourceCompiler.cs`: ordinary bridge and opt-in
  editor callsite selection by object. Keep trace compilation outside these
  normal preparation entrypoints.
- `Heresy.UserInterface/Views/ScriptEditorControl.cs`: single AvaloniaEdit
  TextEditor for source, reference element generator, syntax colorizer,
  diagnostic/F6 background renderers, text-change and dirty-state routing.
  Source offsets use the underlying canonical text, not token display width.
  Existing F6 stale-source guard compares exact source snapshots. Reuse
  source-map/rendering infrastructure, not the live playback session state.

## 3. Diagnostic-only implementation route

Prefer a new explicit CompilePatternGenerationTrace entrypoint returning a
diagnostic program factory (not IIncrementalRawPatternNoteGenerator to be
published by playback). Keep production wrappers unchanged. Reuse the canonical
authored semantic audit and lowered source/offset map. Produce trusted
diagnostic wrapper with its own trace sink and scope operations. Verify
untrusted source *before* injection against exactly the ordinary symbol
contract; do not blanket-allow generated host types or names. Generated
diagnostic code must have explicit provenance and narrow allowed signatures.
No trace capability is accessible through user-written calls, delegates,
casts, constructors, fields or reflection. Reuse F6 span resolution and
delegate adapter rules where semantically applicable, not UI F6 playback.

Capture actual NoteEvent data, not reconstructed strings or guessed commands.
For relaxed execution, a diagnostic receiver delegates validation and ordinal
assignment to the normal schedule-builder semantics (share existing limits;
avoid double retention where possible). Capture at Append, while the dynamic
call scope is live, and keep retained prefix on exception. Never send this
receiver to the timeline. For strict, retain IncrementalPatternEventReceiver
and collect at the existing trusted drain/yield boundary, before chronology
filtering. Instrument the entire authored operation plus its drain as a
diagnostic scope, across suspension. Include raw releases/advance/cooperate
only as internal bookkeeping where necessary, not musical ListBox entries.
Compare accepted/dropped status against the real strict chronology rule.
Do not force strict scripts to run synchronously as relaxed ones.

Use diagnostic-only wrappers around semantically resolved helper calls,
preserving argument evaluation order, named/optional arguments, return
values, short circuiting, receiver evaluation once, exception behavior,
using lifetime and method-group equality. Never blindly replace every
call with a lambda capturing variables (ref/out, mutation, and evaluation
timing can differ). Prototype typed adapters and scope placement first;
trusted generated try/finally is appropriate where valid in an iterator.
For strict, retain declaration scope (do not wrap a using declaration in
a block that disposes it early). For relaxed callbacks, distinguish actual
emitter execution from enclosing delegate invocation; preserve existing
ambiguous method-group fallback rules and report unknown provenance
rather than invent a callsite. Evaluate callback expressions only once.
F6 adapters already address some of these constraints.

For sealed NoteGroup/VirtualNote, prefer diagnostic wrapper/adapters taking
the real instance and forwarding exactly once to its existing method/operator.
No parallel lease allocator or duplicate command validators. Investigate
diagnostic-only internal inspection accessors for existing ownership state
(e.g. lease identity or membership after successful transfer), invoked only
by the trusted diagnostic adapter. No extra fields/checks on normal objects.
Map group object reference to a trace-local creation ID; record NoteVirtual
allocation by its actual first emitted target identity and origin span;
record transfer only if real operator succeeded, not merely because += was
attempted. Scope group control invocation across strict drain to associate
all members with one dynamic ID. Record actual member emissions even if
operation fails or execution stops partway through. Empty/invalid operations
may have diagnostics but do not fabricate musical events.
A group member may retain origin after wrapper disposal/transfer; no
number exposed as a stable user channel ID. If an adapter approach cannot
preserve actual semantics and the no-production-cost requirement, stop and
review alternatives (e.g. separately generated trusted helper binding) before
changing any shared hot path.

## 4. Trace data and execution structure

Immutable result owned by diagnostic view, tagged with exact source snapshot,
Pattern ID/row/channel configuration, order mode, deterministic invocation
seed/context and trace session ID. No changes to song serialization. Data
records: source spans (canonical UTF-16 offset/length); occurrence ID / stable
emission ordinal; actual event target/command/row/time offset; disposition
(raw, accepted, strict dropped); dynamic invocation ID and static callsite;
allocation origin; group creation ID and membership/transfer provenance at
the time of command; parent operation; loop/iteration path; failure or
truncation information. Separate structural records for loop enter/exit,
iteration begin/end, group operation and ownership actions. IDs are trace
local, not persistent object IDs, not virtual channel numbers.

Use a balanced diagnostic scope around authored for/foreach/while/do loops,
and separate iteration scope around the original body. Enter/exit includes
zero-iteration loops; iteration ordinal is execution count, not an inferred
loop variable. Ensure scope cleanup on break/continue/return, condition or
increment failures, foreach enumerator disposal, exception, cancellation
and iterator disposal. Preserve budget/cooperation checkpoints in the
same execution positions. Generated fanout drains/adapter scaffolding are
not authored loops. Approved synchronous LINQ/lambda callbacks use actual
callsite context; do not pretend LINQ internals are visible authored loops.
Capture failure with best available trustworthy source span and active
context before unwinding; if not recoverable show last known context clearly
labeled, not a false exact failure location.

Run asynchronously on a worker owned by the editor, not on PCM or UI thread;
fresh isolated invocation context and deterministic RNG, no mutation of live
song, no automatic source commit. User can trace unsaved editor text.
Cancel/close/new trace invalidates prior result; never let stale worker
publish to a replacement editor. Display compilation diagnostics without
execution if canonical compilation fails. Runtime failure retains prefix and
diagnostics; cancellation/truncation visibly marked incomplete, never
described as completed playback.

Proposed initial diagnostic retention cap: 100,000 total retained records
(events, structural markers and metadata), plus bounded source/message data
and nesting; stop collection cooperatively at limit, retain prefix with one
explicit truncation status outside cap. Proposed independent 10-second
execution budget and cancellation checks at diagnostic loop/cooperation and
event boundaries, including CPU-only strict loops and synchronous callbacks.
Compilation time separate; existing author execution limits still apply.
Limits are diagnostic policy, not changes to musical script limits. Refine
exact accounting and bounds in prototype; no unbounded loop markers even
without notes, no unbounded per-event text, no automatic repeated runs.
Record status distinctly for completed, failed, canceled and truncated.
No promise of preempting an arbitrary blocking approved framework call.

## 5. UI projection and source navigation

Pattern-only explicit Generation Trace action/tab; return to ordinary source
without losing buffer/caret/undo/dirty status. Left virtualized ListBox and
drop-down, right read-only AvaloniaEdit view of captured canonical source
with line numbers, syntax colorization, reference projections, and exact
selected-event span overlays. Selection identity not index, row, callsite,
or text. Extended selection proposed; switching modes restores all selected
occurrences and scrolls primary item into view; no re-execution.

Three projections:
- Original order (default): raw emission order interspersed with loop and
  iteration markers and group-operation headers. Header for a single
  authored group control, children are actual individual emitted commands;
  repeated calls distinct even on same line. Group header selection may
  select/highlight all child events (prototype).
- Playback order: flat raw events sorted by row and then emission ordinal
  using production relaxed comparator (not timeOffsetSeconds or channel).
  Strict accepted events are already chronological; dropped events remain
  visible with explicit "Dropped in strict mode" status, not represented as
  played. Incomplete/failed relaxed traces explicitly say no playback was
  published; the sort is a preview of retained emissions, not a claim that
  the engine would play the partial schedule.
- Playback order by channel: partition same sorted projection by full logical
  target; maintain within-group relative ordering. Physical headers use
  authored channel numbering, virtual headers "Virtual channel – line 42"
  (allocation callsite, not retrigger). Distinguish same-line occurrences
  by iteration context and column when needed; if still ambiguous use a
  trace-local occurrence suffix rather than a runtime slot number. Group
  provenance "Note group – line 42" is context, not another playback channel.
  Header for Global controls and distinct scope-wide/all-virtual broadcasts;
  do not fabricate member fanout for broadcasts actually resolved later.
  Order physical channels numerically, virtual allocations by first emission,
  special groups consistently (proposal). Structural loop rows omitted
  from both sorted projections; retain original context metadata.

Read-only source reflects exact trace snapshot. On selection highlight full
source invocation, including multiline span; scroll to primary selection
without moving editable caret. Multiple events with same source span
deduplicate overlays, not identities. Double-click source anywhere returns
to editable view: hit inside known authored invocation selects complete call
(prefer innermost relevant call; clarify nested non-emitter helper behavior
with owner if ambiguous); otherwise place caret at canonical clicked offset.
Use underlying document offset hit-testing with visual token mappings.
If source changed since capture, mark result stale; never apply old offsets
to different text. Return to live editor without stale selection and offer
rerun; no automatic overwrite of edited source. No Save/commit side effect.
Test keyboard navigation, pointer hit on token edges, multiline, CRLF,
scroll/focus and no editor history contamination.

## 6. Bounded implementation slices and gates

1. Discovery (this document). No production code modified.
2. Compiler/collector proof: explicit diagnostic variant, canonical audit,
   raw strict and relaxed capture, failure prefix, stable ordinals, limit and
   cancellation. Prove generated normal/F6 source or IL unchanged (normalize
   generated class names and nondeterministic assembly IDs; compare resolved
   calls, not merely grep "Trace"). Real command/target/time/row/order parity
   under identical deterministic seeds; strict accepted/dropped and relaxed
   transactional behavior. No playback or GUI work.
3. Execution structure and provenance: loop scopes, nested control flow,
   callback/direct/delegate callsites, allocations/transfer/group fanout;
   strict suspension, cleanup and partial operation failure. Prove same
   command output, ownership warnings and return/evaluation semantics.
   Negative canonical-symbol tests for spoofed trace helpers and generated
   locals, delegate/cast/field accesses. Any production-path changes require
   separate review; zero trace instructions is a gate.
4. Projection and headless UI model: one immutable trace, exact three modes,
   stable event/multiselection, source map and labels, partial/dropped statuses.
5. Editor UI/worker integration: snapshot dirty source, explicit run/cancel,
   read-only pane, virtualized list, navigation and no stale worker publication.
6. Acceptance and hardening: Linux/Windows desktop, large/failed/canceled
   trace, F6/normal playback and export regression, inspect generated code/IL
   and ensure no trace allocations/branches in production hot paths. Update
   editor help and durable documentation. Exact-head CI and owner approval
   before merge.

Concrete regression matrix: repeated equal-row emission; strict backward
rows; relaxed callback sort; invalid row/argument after several events;
cancellation before and during generation; infinite CPU-only loop;
for/foreach/while/do zero/one/multiple iterations; nested break/continue/
return and implicit framework exceptions; nested/combined delegates and
delegate equality; explicit typed and inferred VirtualNote/NoteGroup,
using/+= lowering, repeated allocation same line/recycled slot, invalid
transfer/disposal, group with multiple members and same-line repeated calls,
empty group, scope-wide broadcast; group control suspended mid-drain;
source mapping with directives, lowering, multiline, `_O()` tokens and
CRLF; stable selection over all projections; no document/PCM changes.

Open proof obligations: exact adapter syntax for strict declaration scopes
and group transfer, delegate ambiguity without semantic changes, reliable
failure-span capture, retention accounting and worker lifecycle. These are
engineering discovery/prototype questions, not permission to add tracing
checks to production. If any cannot be met, report the specific blocker
before adopting an alternative.


## Slice 3a implementation: loop and iteration structure

Slice 2 is verified green (4,823/4,823 at `5addcaf312eb`,
[CI #46080448332](https://github.com/logiclrd/Heresy/actions/runs/46080448332)).
Slice 3a extends only diagnostic compilation and capture:

- Annotate authored for/foreach/while/do syntax with canonical original
  spans before emitter rewrites. Preserve annotations through diagnostic
  delegate receiver adaptation; generated fanout drains remain excluded.
  Wrap the loop and its iteration body in trusted generated disposable
  scopes. The original checkpoint/cooperation placement remains intact,
  including strict lambda synchronous checkpoints. Do not lower authored
  return in strict mode (existing iterator compiler restriction).
- Every dynamic loop/iteration scope uses its opening trace ordinal as ID;
  parent IDs preserve nesting. Iteration index is zero-based execution
  count, not the value of a user variable. Events retain a stable emission
  ordinal separately from the unified trace ordinal and enclosing scope.
- Reserve capacity for opening and closing markers together. Scope disposal
  appends its already-reserved close without a new cancellation or budget
  check, including exception, stop, break, continue, return or truncated
  strict iterator disposal. A close denotes unwinding, not successful
  execution. A limit may leave unused capacity when a new scope needs two
  records. The 100,000 record limit now includes events and loop markers;
  CPU-only loops can hit record retention before the time/cancellation
  limit. Dynamic call/group metadata accounting follows in Slice 3b.
- Twelve new cases exercise nested spans and IDs, zero iterations,
  foreach/while/do, break/continue/relaxed return, failure, balanced bounded
  prefix, group fanout, relaxed callbacks, cancellation unwind, and
  rejection of authored diagnostic scope calls. The existing generated IL
  test now checks both new scope calls in addition to diagnostic checkpoint
  isolation. Full baseline IL equivalence remains a later gate.

This is not yet a full provenance implementation. Dynamic helper call
identity, allocation/group creation and membership transfers, group-call
scope across strict drain, and failure location beyond the last known
context remain open. No changes to shared musical helpers or playback
runtime. Exact-head Slice 3a CI must pass before the next code slice.
