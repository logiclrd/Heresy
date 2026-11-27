using System;
using System.Collections.Generic;
using System.Diagnostics;

using Heresy.Core.Diagnostics;
using Heresy.Core.Objects;
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

public sealed record PatternGenerationTraceEvent(NoteEvent Note, bool DroppedInStrictMode)
{
    public long OccurrenceId => Note.EmissionOrder;
}

/// <summary>One isolated generation attempt, including a retained prefix on failure.
/// Not a playback schedule.</summary>
public sealed record PatternGenerationTraceResult(
    ObjectId PatternId, string Source, int RowCount, int ChannelCount, bool Relaxed,
    IReadOnlyList<PatternGenerationTraceEvent> Events,
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

    internal void ReserveRecord()
    {
        Checkpoint();
        if (_records >= _maximumRecords)
            throw new PatternGenerationTraceLimitException("Diagnostic record limit reached.");
        _records++;
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

    protected void __HeresyDiagnosticCheckpoint() => _traceBudget.Checkpoint();
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
                        budget.ReserveRecord();
                        events.Add(new(emission.Note.WithEmissionOrder(events.Count), dropped));
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
            events.AsReadOnly(), status, detail, Array.AsReadOnly(context.Diagnostics.Drain()));
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
            budget.ReserveRecord();
            events.Add(new(noteEvent.WithEmissionOrder(events.Count), false));
        }
    }
}
