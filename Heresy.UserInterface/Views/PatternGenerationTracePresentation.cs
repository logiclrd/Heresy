using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Compilation;

namespace Heresy.UserInterface.Views;

public enum PatternTraceOrder
{
    OriginalOrder,
    PlaybackOrder,
    PlaybackOrderByChannel,
}

public enum PatternTraceRowKind
{
    Event,
    LoopMarker,
    ChannelHeader,
    GroupOperation,
    GroupCreation,
    Transfer,
    Status,
}

/// <summary>Rows are presentation objects, not playback instructions. Only
/// musical events have occurrence IDs; headers may represent several events.</summary>
public sealed record PatternTraceRow(
    PatternTraceRowKind Kind, string Text, int Depth,
    PatternGenerationTraceEvent? Event = null,
    SequenceCallsite? SourceSpan = null,
    long? InvocationId = null, ChannelTarget? Target = null)
{
    public long? OccurrenceId => Event?.OccurrenceId;
    public bool Dropped => Event?.DroppedInStrictMode == true;
}

public sealed record PatternTraceNavigation(int Start, int Length);

/// <summary>Headless, snapshot-local presentation state. The underlying
/// capture is never mutated or re-executed by a projection or selection.</summary>
public sealed class PatternGenerationTracePresentation
{
    private readonly PatternGenerationTraceResult _trace;
    private readonly Dictionary<long, PatternGenerationTraceEvent> _events;
    private readonly Dictionary<long, PatternGenerationTraceInvocation> _invocations;
    private readonly Dictionary<long, PatternGenerationTraceMarker> _scopes;
    private readonly Dictionary<long, int> _depths = [];
    private readonly Dictionary<ChannelTarget, string> _channelLabels = [];
    private readonly Dictionary<long, string> _groupLabels = [];
    private readonly Dictionary<PatternTraceOrder, IReadOnlyList<PatternTraceRow>> _projections = [];
    private readonly HashSet<long> _selection = [];
    private readonly int[] _lineStarts;
    private readonly SequenceCallsite[] _navigationSpans;

    public PatternGenerationTracePresentation(PatternGenerationTraceResult trace)
    {
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        _events = trace.Events.ToDictionary(e => e.OccurrenceId);
        _invocations = trace.Invocations.ToDictionary(i => i.Id);
        _scopes = trace.Markers.Where(m => m.Kind is PatternGenerationTraceMarkerKind.LoopEnter
            or PatternGenerationTraceMarkerKind.IterationBegin).ToDictionary(m => m.ScopeId);
        foreach (var scope in _scopes.Values.OrderBy(m => m.TraceOrdinal))
            _depths[scope.ScopeId] = Depth(scope.ParentScopeId) + 1;
        List<int> starts = [0];
        for (int i = 0; i < trace.Source.Length; i++)
        {
            if (trace.Source[i] == '\r')
            {
                if (i + 1 < trace.Source.Length && trace.Source[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
            else if (trace.Source[i] == '\n') starts.Add(i + 1);
        }
        _lineStarts = starts.ToArray();
        BuildLabels();
        _navigationSpans = trace.Invocations.Where(i => !i.IsReferenceFallback)
            .Select(i => (SequenceCallsite?)i.SourceSpan)
            .Concat(trace.Events.Select(e => e.Note.EditorCallsite))
            .Where(IsValidSpan).Select(s => s!.Value).Distinct()
            .OrderBy(s => s.Length).ThenByDescending(s => s.Start).ToArray();
    }

    public PatternGenerationTraceResult Trace => _trace;
    public PatternTraceOrder Order { get; private set; } = PatternTraceOrder.OriginalOrder;
    public long? PrimaryOccurrenceId { get; private set; }
    public IReadOnlyList<long> SelectedOccurrenceIds => _selection.OrderBy(id => id).ToArray();
    public IReadOnlyList<PatternTraceRow> Rows => GetProjection(Order);
    public int PrimaryRowIndex
    {
        get
        {
            var rows = Rows;
            if (PrimaryOccurrenceId is not long id) return -1;
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].OccurrenceId == id) return i;
            return -1;
        }
    }

    public void SetOrder(PatternTraceOrder order)
    {
        if (!Enum.IsDefined(order)) throw new ArgumentOutOfRangeException(nameof(order));
        Order = order;
    }

    public void SelectOccurrences(IEnumerable<long> ids, long? primary = null)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var selected = ids.Where(_events.ContainsKey).Distinct().ToArray();
        _selection.Clear();
        foreach (long id in selected) _selection.Add(id);
        PrimaryOccurrenceId = primary is long p && _selection.Contains(p) ? p
            : selected.Length == 0 ? null : selected[0];
    }

    public void SelectGroupOperation(long invocationId)
        => SelectOccurrences(_trace.Events.Where(e => e.InvocationId == invocationId)
            .Select(e => e.OccurrenceId));

    public IReadOnlyList<SequenceCallsite> SelectedSourceSpans => _selection
        .SelectMany(id => EventSpans(_events[id]))
        .Distinct().OrderBy(s => s.Start).ThenBy(s => s.Length).ToArray();

    private IEnumerable<SequenceCallsite> EventSpans(PatternGenerationTraceEvent entry)
    {
        if (IsValidSpan(entry.Note.EditorCallsite)) yield return entry.Note.EditorCallsite!.Value;
        if (entry.InvocationId is long id && _invocations.TryGetValue(id, out var invocation)
            && IsValidSpan(invocation.SourceSpan))
            yield return invocation.SourceSpan;
    }

    public bool IsStale(string liveSource) => !string.Equals(_trace.Source, liveSource, StringComparison.Ordinal);

    /// <summary>Canonical UTF-16 offsets, half-open call spans. Prefer the
    /// innermost known authored call; never apply a stale offset to new text.</summary>
    public PatternTraceNavigation? Navigate(string liveSource, int clickedOffset)
    {
        if (IsStale(liveSource)) return null;
        if (clickedOffset < 0 || clickedOffset > _trace.Source.Length)
            throw new ArgumentOutOfRangeException(nameof(clickedOffset));
        foreach (var span in _navigationSpans)
            if (clickedOffset >= span.Start && clickedOffset - span.Start < span.Length)
                return new(span.Start, span.Length);
        return new(clickedOffset, 0);
    }

    public string ChannelLabel(ChannelTarget target)
        => _channelLabels.TryGetValue(target, out var label) ? label : target.Kind switch
        {
            ChannelTargetKind.Global => "Global controls",
            ChannelTargetKind.Physical => $"Channel {target.PhysicalChannel}",
            ChannelTargetKind.Virtual => "Virtual channel",
            ChannelTargetKind.AllVirtualInScope => "Virtual channels in scope",
            ChannelTargetKind.AllVirtual => "All virtual channels",
            _ => "Unknown target",
        };

    public string GroupLabel(long id) => _groupLabels.TryGetValue(id, out var label)
        ? label : "Note group";

    public string StatusText => _trace.Status switch
    {
        PatternGenerationTraceStatus.Completed => "Generation completed — no audio published.",
        PatternGenerationTraceStatus.Failed => "Generation failed — retained prefix only; no audio published.",
        PatternGenerationTraceStatus.Canceled => "Generation canceled — retained prefix only; no audio published.",
        PatternGenerationTraceStatus.Truncated => "Generation truncated — retained prefix only; no audio published.",
        _ => "Unknown generation status",
    };

    private bool IsValidSpan(SequenceCallsite? span) => span is { Start: >= 0, Length: > 0 } s
        && s.Start <= _trace.Source.Length && s.Length <= _trace.Source.Length - s.Start;

    private (int Line, int Column) Coordinates(SequenceCallsite span)
    {
        int index = Array.BinarySearch(_lineStarts, span.Start);
        if (index < 0) index = ~index - 1;
        return (index + 1, span.Start - _lineStarts[index] + 1);
    }

    private string OriginLabel(string kind, SequenceCallsite? span, bool fallback)
    {
        if (!IsValidSpan(span)) return kind;
        var location = Coordinates(span!.Value);
        return $"{kind} — {(fallback ? "reference at line" : "line")} {location.Line}";
    }

    private void BuildLabels()
    {
        var origins = _trace.GetVirtualOrigins();
        var virtualLabels = origins.Select(o => (Origin: o,
            Label: OriginLabel("Virtual channel", o.CreationSpan, o.IsReferenceFallback))).ToArray();
        foreach (var group in virtualLabels.GroupBy(v => v.Label))
        {
            int count = group.Count(), occurrence = 0;
            foreach (var item in group)
                _channelLabels[ChannelTarget.Virtual(item.Origin.Channel)] = count == 1 ? item.Label
                    : Disambiguate(item.Label, item.Origin.CreationSpan, item.Origin.EnclosingScopeId, ++occurrence);
        }
        foreach (var group in _trace.Groups.GroupBy(g => OriginLabel("Note group", g.CreationSpan, g.IsReferenceFallback)))
        {
            int count = group.Count(), occurrence = 0;
            foreach (var item in group)
                _groupLabels[item.Id] = count == 1 ? group.Key
                    : Disambiguate(group.Key, item.CreationSpan, item.EnclosingScopeId, ++occurrence);
        }
    }

    private string Disambiguate(string label, SequenceCallsite? span, long? scope, int occurrence)
    {
        string column = IsValidSpan(span) ? $", column {Coordinates(span!.Value).Column}" : "";
        string iteration = scope is long id && _scopes.TryGetValue(id, out var marker)
            && marker.IterationIndex is int i ? $", iteration {i + 1}" : "";
        // A trace-local occurrence suffix is not a channel number.
        return $"{label}{column}{iteration} (occurrence {occurrence})";
    }

    private int Depth(long? scope) => scope is long id && _depths.TryGetValue(id, out int d) ? d : 0;

    public IReadOnlyList<PatternTraceRow> GetProjection(PatternTraceOrder order)
    {
        if (_projections.TryGetValue(order, out var cached)) return cached;
        List<PatternTraceRow> rows = [];
        switch (order)
        {
            case PatternTraceOrder.OriginalOrder:
                BuildOriginal(rows);
                break;
            case PatternTraceOrder.PlaybackOrder:
                foreach (var entry in PlaybackEvents()) rows.Add(EventRow(entry, 0, includeTarget: true));
                break;
            case PatternTraceOrder.PlaybackOrderByChannel:
                // GroupBy retains the relative order of its sorted input.
                var first = _trace.Events.GroupBy(e => e.Note.Target)
                    .ToDictionary(g => g.Key, g => g.First().OccurrenceId);
                foreach (var group in PlaybackEvents().GroupBy(e => e.Note.Target)
                    .OrderBy(g => g.Key.Kind)
                    .ThenBy(g => g.Key.Kind == ChannelTargetKind.Physical ? g.Key.PhysicalChannel : first[g.Key]))
                {
                    rows.Add(new(PatternTraceRowKind.ChannelHeader, ChannelLabel(group.Key), 0, Target: group.Key));
                    foreach (var entry in group) rows.Add(EventRow(entry, 1, includeTarget: false));
                }
                break;
            default: throw new ArgumentOutOfRangeException(nameof(order));
        }
        string status = StatusText + (string.IsNullOrWhiteSpace(_trace.Detail) ? "" : " " + _trace.Detail);
        var context = _trace.FailureContext;
        if (context is not null)
            status += context.IsInvocationFailure ? " Failing invocation." : " Last known source context.";
        rows.Add(new(PatternTraceRowKind.Status, status, 0,
            SourceSpan: IsValidSpan(context?.SourceSpan) ? context!.SourceSpan : null));
        return _projections[order] = rows.AsReadOnly();
    }

    private IEnumerable<PatternGenerationTraceEvent> PlaybackEvents()
        => _trace.Events.OrderBy(e => e.Note.Offset.RowOffset).ThenBy(e => e.Note.EmissionOrder);

    private void BuildOriginal(List<PatternTraceRow> rows)
    {
        // AtTraceOrdinal is a boundary, not an independent event ordinal.
        // At equal boundaries show ownership metadata before the next marker/event.
        var metadata = _trace.Groups.Select(g => (Ordinal: g.AtTraceOrdinal,
            Row: new PatternTraceRow(PatternTraceRowKind.GroupCreation, "Create " + GroupLabel(g.Id),
                Depth(g.EnclosingScopeId), SourceSpan: g.CreationSpan)))
            .Concat(_trace.Transfers.Select(t => (Ordinal: t.AtTraceOrdinal,
                Row: new PatternTraceRow(PatternTraceRowKind.Transfer,
                    (t.Succeeded ? "Transfer" : "Ignored transfer") + " → " + GroupLabel(t.GroupId)
                    + (t.Channel is { } channel ? " · " + ChannelLabel(ChannelTarget.Virtual(channel)) : ""),
                    Depth(t.EnclosingScopeId), SourceSpan: t.SourceSpan))))
            .OrderBy(m => m.Ordinal).ToArray();
        var markers = _trace.Markers.Select(m => (m.TraceOrdinal, Marker: (PatternGenerationTraceMarker?)m,
            Event: (PatternGenerationTraceEvent?)null));
        var events = _trace.Events.Select(e => (e.TraceOrdinal, Marker: (PatternGenerationTraceMarker?)null,
            Event: (PatternGenerationTraceEvent?)e));
        int nextMetadata = 0;
        HashSet<long> groupHeaders = [];
        foreach (var item in markers.Concat(events).OrderBy(i => i.TraceOrdinal))
        {
            while (nextMetadata < metadata.Length && metadata[nextMetadata].Ordinal <= item.TraceOrdinal)
                rows.Add(metadata[nextMetadata++].Row);
            if (item.Marker is { } marker)
            {
                string label = marker.Kind switch
                {
                    PatternGenerationTraceMarkerKind.LoopEnter => "Enter loop",
                    PatternGenerationTraceMarkerKind.LoopExit => "Exit loop",
                    PatternGenerationTraceMarkerKind.IterationBegin => $"Iteration {marker.IterationIndex + 1}",
                    _ => $"End iteration {marker.IterationIndex + 1}",
                };
                if (marker.Kind is PatternGenerationTraceMarkerKind.LoopEnter or PatternGenerationTraceMarkerKind.LoopExit
                    && IsValidSpan(marker.SourceSpan))
                    label += $" — line {Coordinates(marker.SourceSpan).Line}";
                rows.Add(new(PatternTraceRowKind.LoopMarker, label, Depth(marker.ParentScopeId), SourceSpan: marker.SourceSpan));
            }
            else if (item.Event is { } entry)
            {
                int depth = Depth(entry.EnclosingScopeId);
                if (entry.GroupId is long group && entry.InvocationId is long id && _invocations.TryGetValue(id, out var invocation))
                {
                    if (groupHeaders.Add(id))
                        rows.Add(new(PatternTraceRowKind.GroupOperation, invocation.Method + " — " + GroupLabel(group),
                            depth, SourceSpan: IsValidSpan(invocation.SourceSpan) ? invocation.SourceSpan : entry.Note.EditorCallsite,
                            InvocationId: id));
                    depth++;
                }
                rows.Add(EventRow(entry, depth, includeTarget: true));
            }
        }
        while (nextMetadata < metadata.Length) rows.Add(metadata[nextMetadata++].Row);
    }

    private PatternTraceRow EventRow(PatternGenerationTraceEvent entry, int depth, bool includeTarget)
    {
        var note = entry.Note;
        string time = note.Offset.RowOffset.ToString("G", CultureInfo.InvariantCulture);
        if (note.Offset.TimeOffset != TimeSpan.Zero)
            time += $" ({note.Offset.TimeOffset.TotalSeconds.ToString("+0.#########;-0.#########;0", CultureInfo.InvariantCulture)} s)";
        string label = "Row " + time + " · " + (includeTarget ? ChannelLabel(note.Target) + " · " : "")
            + string.Join("; ", note.Commands.Select(DescribeCommand));
        if (entry.GroupId is long group) label += " · via " + GroupLabel(group);
        if (entry.DroppedInStrictMode) label += " · Dropped in strict mode";
        return new(PatternTraceRowKind.Event, label, depth, entry,
            EventSpans(entry).Select(s => (SequenceCallsite?)s).FirstOrDefault(), entry.InvocationId, note.Target);
    }

    private static string DescribeCommand(NoteCommand command) => command switch
    {
        StartNoteCommand n => FormattableString.Invariant($"Note · _O({n.SourceId}) · pitch ×{n.PitchMultiplier:G} · speed ×{n.PlaybackSpeedMultiplier:G}")
            + (n.Volume is double v ? FormattableString.Invariant($" · volume {v:G}") : "")
            + (n.Mixdown ? " · mixdown" : "")
            + (n.EventFilterDefinitionId is { } filter ? $" · filter _O({filter})" : ""),
        NoteOffCommand => "Off",
        NoteCutCommand => "Cut",
        SetTempoCommand t => FormattableString.Invariant($"Tempo {t.TicksPerDiachron:G}"),
        SetSpeedCommand s => FormattableString.Invariant($"Speed {s.TicksPerRow}"),
        SetNoteVolumeCommand v => FormattableString.Invariant($"Volume {v.Volume:G}"),
        SeekSourceFrameCommand s => FormattableString.Invariant($"{(s.Relative ? "SourceFrameRelative" : "SourceFrame")} {s.Frame}"),
        SeekPlaybackTimeCommand s => FormattableString.Invariant($"{(s.Relative ? "SeekRelative" : "Seek")} {s.Time.TotalSeconds:G} s"),
        SetNoteFilterCommand f => f.FilterDefinitionId is { } id ? $"NoteFilter _O({id})" : "NoteFilter — clear",
        SetChannelFilterCommand f => f.FilterDefinitionId is { } id ? $"ChannelFilter _O({id})" : "ChannelFilter — clear",
        SetSurroundCommand s => $"Surround {s.Enabled}",
        SetResonantFilterCommand f => FormattableString.Invariant($"Filter {f.Cutoff:G}, {f.Resonance:G}"),
        SetResonantFilterCutoffCommand f => FormattableString.Invariant($"FilterCutoff {f.Cutoff:G}"),
        SetResonantFilterResonanceCommand f => FormattableString.Invariant($"FilterResonance {f.Resonance:G}"),
        SetSpatialPositionCommand p => FormattableString.Invariant($"Pan {p.Position.X:G}, {p.Position.Y:G}, {p.Position.Z:G}"),
        SetCurrentVoiceDisplacementActionCommand d => $"Displacement {d.Action}",
        ApplyPastNoteActionCommand p => $"Past {p.Action}",
        _ => command.ToString() ?? command.GetType().Name,
    };
}
