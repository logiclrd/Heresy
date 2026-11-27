using System;
using System.Collections.Generic;
using System.Diagnostics;

using Heresy.Core.Diagnostics;
using Heresy.Core.Objects;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Runtime;

namespace Heresy.Scripting.Compilation;

public enum PatternGenerationTraceStatus
{
    Completed,
    Failed,
    Canceled,
    Truncated,
}

public sealed record PatternGenerationTraceEvent(NoteEvent Note, bool DroppedInStrictMode,
    long TraceOrdinal, long? EnclosingScopeId, long? InvocationId, long? GroupId)
{
    public long OccurrenceId => Note.EmissionOrder;
}

public sealed record PatternGenerationTraceInvocation(long Id, SequenceCallsite SourceSpan,
    string Method, long? EnclosingScopeId, long? ParentInvocationId, long? GroupId);

public sealed record PatternGenerationTraceGroup(long Id, SequenceCallsite? CreationSpan,
    long? EnclosingScopeId);

public sealed record PatternGenerationTraceTransfer(SequenceCallsite SourceSpan,
    long GroupId, VirtualChannelIdentity? Channel, bool Succeeded, long? EnclosingScopeId);

public enum PatternGenerationTraceMarkerKind
{
    LoopEnter,
    LoopExit,
    IterationBegin,
    IterationEnd,
}

/// <summary>Scope IDs are the opening marker's ordinal, local to one trace.
/// An iteration index is zero-based execution count, not a loop variable.
/// Closing markers describe scope unwinding, not successful completion.</summary>
public sealed record PatternGenerationTraceMarker(
    long TraceOrdinal, long ScopeId, long? ParentScopeId,
    PatternGenerationTraceMarkerKind Kind, SequenceCallsite SourceSpan, int? IterationIndex);

/// <summary>One isolated generation attempt, including a retained prefix on failure.
/// Not a playback schedule.</summary>
public sealed record PatternGenerationTraceResult(
    ObjectId PatternId, string Source, int RowCount, int ChannelCount, bool Relaxed,
    IReadOnlyList<PatternGenerationTraceEvent> Events,
    IReadOnlyList<PatternGenerationTraceMarker> Markers,
    IReadOnlyList<PatternGenerationTraceInvocation> Invocations,
    IReadOnlyList<PatternGenerationTraceGroup> Groups,
    IReadOnlyList<PatternGenerationTraceTransfer> Transfers,
    PatternGenerationTraceStatus Status, string? Detail,
    IReadOnlyList<SequencingDiagnostic> Diagnostics);

internal sealed class PatternGenerationTraceLimitException(string message) : Exception(message);

/// <summary>Only diagnostic generated wrappers receive this object. Public
/// type for cross-assembly generated constructors; authored scripts cannot
/// construct or access it through the canonical symbol policy.</summary>
public sealed class PatternGenerationTraceBudget
{
    public const int MaximumRecords = 100_000;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly SequencingCancellation _cancellation;
    private readonly TimeSpan _duration;
    private readonly int _maximumRecords;
    private int _records;
    private long _nextOrdinal;
    private TraceScope? _scope;
    private readonly List<PatternGenerationTraceMarker> _markers = [];

    internal IReadOnlyList<PatternGenerationTraceMarker> Markers => _markers.AsReadOnly();
    internal long? CurrentScopeId => _scope?.Id;
    private readonly List<PatternGenerationTraceInvocation> _invocations = [];
    private readonly List<PatternGenerationTraceGroup> _groups = [];
    private readonly List<PatternGenerationTraceTransfer> _transfers = [];
    private readonly Dictionary<NoteGroup, long> _groupIds = new(ReferenceEqualityComparer.Instance);
    private InvocationScope? _invocation;
    internal IReadOnlyList<PatternGenerationTraceInvocation> Invocations => _invocations.AsReadOnly();
    internal IReadOnlyList<PatternGenerationTraceGroup> Groups => _groups.AsReadOnly();
    internal IReadOnlyList<PatternGenerationTraceTransfer> Transfers => _transfers.AsReadOnly();
    internal long? CurrentInvocationId => _invocation?.Record.Id;
    internal long? CurrentGroupId => _invocation?.Record.GroupId;

    internal long RegisterGroup(NoteGroup group, SequenceCallsite? span)
    {
        if (_groupIds.TryGetValue(group, out long id)) return id;
        ReserveRecords(1);
        id = _groups.Count;
        _groupIds.Add(group, id);
        _groups.Add(new(id, span, CurrentScopeId));
        return id;
    }

    internal IDisposable EnterInvocation(SequenceCallsite span, string method, NoteGroup? group)
    {
        long? groupId = group is null ? null : RegisterGroup(group, null);
        ReserveRecords(1);
        var record = new PatternGenerationTraceInvocation(_invocations.Count, span, method,
            CurrentScopeId, CurrentInvocationId, groupId);
        _invocations.Add(record);
        var scope = new InvocationScope(this, _invocation, record);
        _invocation = scope;
        return scope;
    }

    internal void ReserveTransfer() => ReserveRecords(1);

    internal void RecordTransfer(SequenceCallsite span, long groupId,
        VirtualChannelIdentity? channel, bool succeeded)
        => _transfers.Add(new(span, groupId, channel, succeeded, CurrentScopeId));

    private sealed class InvocationScope(PatternGenerationTraceBudget owner,
        InvocationScope? parent, PatternGenerationTraceInvocation record) : IDisposable
    {
        internal PatternGenerationTraceInvocation Record { get; } = record;
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner._invocation = parent;
        }
    }

    internal PatternGenerationTraceBudget(SequencingCancellation cancellation,
        int maximumRecords, TimeSpan duration)
    {
        _cancellation = cancellation;
        _maximumRecords = maximumRecords;
        _duration = duration;
    }

    internal void Checkpoint()
    {
        _cancellation.ThrowIfCancellationRequested();
        if (_clock.Elapsed >= _duration)
            throw new PatternGenerationTraceLimitException("Diagnostic execution time limit reached.");
    }

    private void ReserveRecords(int count)
    {
        Checkpoint();
        if (count > _maximumRecords - _records)
            throw new PatternGenerationTraceLimitException("Diagnostic record limit reached.");
        _records += count;
    }

    internal long ReserveEvent()
    {
        ReserveRecords(1);
        return _nextOrdinal++;
    }

    internal IDisposable EnterLoop(SequenceCallsite source)
        => EnterScope(source, iterationIndex: null);

    internal IDisposable EnterIteration()
    {
        TraceScope loop = _scope ?? throw new InvalidOperationException("No diagnostic loop scope.");
        if (loop.IterationIndex.HasValue)
            throw new InvalidOperationException("Expected diagnostic loop scope, not iteration.");
        return EnterScope(loop.Source, loop.NextIteration++);
    }

    private IDisposable EnterScope(SequenceCallsite source, int? iterationIndex)
    {
        // Reserve both markers together. Closing a scope must never throw a
        // cancellation/retention exception or mask the authored failure.
        ReserveRecords(2);
        TraceScope scope = new(this, _scope, _nextOrdinal++, source, iterationIndex);
        _scope = scope;
        _markers.Add(new(scope.Id, scope.Id, scope.Parent?.Id,
            iterationIndex.HasValue ? PatternGenerationTraceMarkerKind.IterationBegin
                : PatternGenerationTraceMarkerKind.LoopEnter, source, iterationIndex));
        return scope;
    }

    private sealed class TraceScope(PatternGenerationTraceBudget owner, TraceScope? parent,
        long id, SequenceCallsite source, int? iterationIndex) : IDisposable
    {
        internal TraceScope? Parent { get; } = parent;
        internal long Id { get; } = id;
        internal SequenceCallsite Source { get; } = source;
        internal int? IterationIndex { get; } = iterationIndex;
        internal int NextIteration { get; set; }
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner._markers.Add(new(owner._nextOrdinal++, Id, Parent?.Id,
                IterationIndex.HasValue ? PatternGenerationTraceMarkerKind.IterationEnd
                    : PatternGenerationTraceMarkerKind.LoopExit, Source, IterationIndex));
            owner._scope = Parent;
        }
    }
}

/// <summary>Diagnostic-only base; no changes to the normal runtime object
/// layout or production checkpoint methods.</summary>
public abstract class DiagnosticPatternScriptProgram : PatternScriptProgram
{
    private readonly PatternGenerationTraceBudget _traceBudget;

    protected DiagnosticPatternScriptProgram(SequencingContext context, INoteReceiver output,
        double rowCount, int channelCount, PatternGenerationTraceBudget traceBudget)
        : base(context, output, rowCount, channelCount)
        => _traceBudget = traceBudget;

    private IDisposable? _pendingInvocation;

    protected IDisposable __HeresyEnterTraceInvocation(SequenceCallsite span, string method, NoteGroup? group)
        => _traceBudget.EnterInvocation(span, method, group);

    protected void __HeresyBeginTraceInvocation(SequenceCallsite span, string method, NoteGroup? group)
    {
        if (_pendingInvocation is not null)
            throw new InvalidOperationException("Diagnostic invocation did not drain.");
        _pendingInvocation = _traceBudget.EnterInvocation(span, method, group);
    }

    protected void __HeresyEndTraceInvocation()
    {
        _pendingInvocation?.Dispose();
        _pendingInvocation = null;
    }

    protected NoteGroup __HeresyTraceNewGroup(SequenceCallsite span)
    {
        using var invocation = _traceBudget.EnterInvocation(span, "NewNoteGroup", null);
        NoteGroup group = NewNoteGroup();
        _traceBudget.RegisterGroup(group, span);
        return group;
    }

    protected void __HeresyTraceTransfer(SequenceCallsite span, NoteGroup group, VirtualNote note)
    {
        // Preserve evaluation order: receiver and note arguments have
        // already evaluated, including a possible pending strict start.
        ArgumentNullException.ThrowIfNull(group);
        long groupId = _traceBudget.RegisterGroup(group, null);
        _traceBudget.ReserveTransfer();
        VirtualChannelIdentity? identity = note is not null
            && note.TryGetTransferIdentity(this, out var channel) ? channel : null;
        int before = group.DiagnosticLeaseCount;
        group += note!;
        _traceBudget.RecordTransfer(span, groupId, identity, group.DiagnosticLeaseCount > before);
    }

    protected void __HeresyDiagnosticCheckpoint() => _traceBudget.Checkpoint();
    protected IDisposable __HeresyEnterTraceLoop(SequenceCallsite span) => _traceBudget.EnterLoop(span);
    protected IDisposable __HeresyEnterTraceIteration() => _traceBudget.EnterIteration();
}

/// <summary>Factory for isolated diagnostic generation. Deliberately does
/// not implement either playback generator interface.</summary>
public sealed class CompiledPatternGenerationTrace
{
    private readonly Type _programType;
    private readonly ObjectId _patternId;
    private readonly string _source;
    private readonly int _rowCount, _channelCount;
    private readonly bool _relaxed;

    internal CompiledPatternGenerationTrace(Type programType, ObjectId patternId,
        string source, int rowCount, int channelCount, bool relaxed)
    {
        _programType = programType;
        _patternId = patternId;
        _source = source;
        _rowCount = rowCount;
        _channelCount = channelCount;
        _relaxed = relaxed;
    }

    /// <summary>Run synchronously on the caller's diagnostic worker, never on
    /// the audio thread. Context, RNG and diagnostics are fresh per run.</summary>
    public PatternGenerationTraceResult Capture(SequencingCancellation? cancellation = null,
        int maximumRecords = PatternGenerationTraceBudget.MaximumRecords,
        TimeSpan? maximumDuration = null)
    {
        if (maximumRecords <= 0 || maximumRecords > PatternGenerationTraceBudget.MaximumRecords)
            throw new ArgumentOutOfRangeException(nameof(maximumRecords));
        TimeSpan duration = maximumDuration ?? TimeSpan.FromSeconds(10);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        SequencingContext context = new(cancellation: cancellation);
        PatternGenerationTraceBudget budget = new(context.Cancellation, maximumRecords, duration);
        List<PatternGenerationTraceEvent> events = [];
        PatternGenerationTraceStatus status = PatternGenerationTraceStatus.Completed;
        string? detail = null;
        try
        {
            budget.Checkpoint();
            if (_relaxed)
            {
                // Same musical helpers, synchronous execution and raw
                // append order as normal relaxed collection. The diagnostic
                // cap is below NoteScheduleBuilder's production event cap.
                var receiver = new CaptureReceiver(events, budget);
                CreateProgram(context, receiver, budget).ExecuteRelaxed();
            }
            else
            {
                // Do not substitute a collecting receiver: the concrete
                // one-slot receiver also controls lazy group fanout.
                var receiver = new IncrementalPatternEventReceiver();
                PatternScriptProgram program = CreateProgram(context, receiver, budget);
                double lastRow = 0;
                using var iterator = program.Enumerate().GetEnumerator();
                while (true)
                {
                    budget.Checkpoint();
                    if (!iterator.MoveNext()) break;
                    if (iterator.Current is RawPatternStep.Emit emission)
                    {
                        bool dropped = emission.Row < lastRow;
                        long ordinal = budget.ReserveEvent();
                        events.Add(new(emission.Note.WithEmissionOrder(events.Count), dropped,
                            ordinal, budget.CurrentScopeId, budget.CurrentInvocationId, budget.CurrentGroupId));
                        if (dropped)
                            context.Diagnostics.ReportDroppedOutOfOrderNote(emission.Row, lastRow);
                        else
                            lastRow = emission.Row;
                    }
                    else if (iterator.Current is RawPatternStep.Advance advance && advance.Row > lastRow)
                        lastRow = advance.Row;
                    // Match ordinary strict generator ownership cleanup
                    // boundaries; releases are not musical ListBox events.
                    while (program.TryTakeVirtualChannelRelease(out _)) { }
                }
                while (program.TryTakeVirtualChannelRelease(out _)) { }
            }
            budget.Checkpoint();
        }
        catch (PatternGenerationTraceLimitException exception)
        {
            status = PatternGenerationTraceStatus.Truncated;
            detail = exception.Message;
        }
        catch (OperationCanceledException exception)
        {
            status = PatternGenerationTraceStatus.Canceled;
            detail = exception.Message;
        }
        catch (SequencingResourceLimitException exception)
        {
            status = PatternGenerationTraceStatus.Truncated;
            detail = exception.Message;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            status = PatternGenerationTraceStatus.Failed;
            detail = exception.GetType().Name + ": " + exception.Message;
        }
        if (detail is { Length: > 2048 }) detail = detail[..2048];
        return new(_patternId, _source, _rowCount, _channelCount, _relaxed,
            events.AsReadOnly(), budget.Markers, budget.Invocations, budget.Groups, budget.Transfers,
            status, detail, Array.AsReadOnly(context.Diagnostics.Drain()));
    }

    private PatternScriptProgram CreateProgram(SequencingContext context, INoteReceiver receiver,
        PatternGenerationTraceBudget budget)
        => (PatternScriptProgram)(Activator.CreateInstance(_programType, context, receiver,
            (double)_rowCount, _channelCount, budget)
            ?? throw new InvalidOperationException("Could not construct the diagnostic script."));

    private sealed class CaptureReceiver(List<PatternGenerationTraceEvent> events,
        PatternGenerationTraceBudget budget) : INoteReceiver
    {
        public void Append(NoteEvent noteEvent)
        {
            ArgumentNullException.ThrowIfNull(noteEvent);
            long ordinal = budget.ReserveEvent();
            events.Add(new(noteEvent.WithEmissionOrder(events.Count), false,
                ordinal, budget.CurrentScopeId, budget.CurrentInvocationId, budget.CurrentGroupId));
        }
    }
}
