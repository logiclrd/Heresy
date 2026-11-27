using System;
using System.Collections.Generic;

using Heresy.Core.Objects;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;

namespace Heresy.Scripting.Runtime;

/// <summary>Exclusive aggregation of invocation-local virtual leases.
/// No engine group channel and no wrapper ownership remain after transfer.</summary>
public sealed class NoteGroup : IDisposable
{
    private readonly PatternScriptProgram _owner;
    private readonly List<VirtualChannelIdentity> _leases = [];
    private bool _disposed;

    internal NoteGroup(PatternScriptProgram owner) => _owner = owner;

    // C# 14 instance compound assignment, not binary + followed by
    // reassignment of a readonly using local.
    public void operator +=(VirtualNote note)
    {
        _owner.CheckOwnershipBudget();
        if (_disposed || note is null
            || !note.TryGetTransferIdentity(_owner, out var identity))
        {
            _owner.ReportVirtualOwnershipIssue(
                "Ignored invalid NoteGroup transfer: disposed destination or disposed, transferred, null or foreign source.");
            return;
        }
        // A live wrapper is exclusive. Reserve/add storage before
        // invalidating it; no allocator release occurs on transfer.
        _leases.Add(identity);
        note.MarkTransferred();
    }

    private bool CheckOwned()
    {
        if (!_disposed) return true;
        _owner.ReportVirtualOwnershipIssue("Ignored operation on a disposed NoteGroup.");
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var identity in _leases)
            _owner.ReleaseVirtualChannelLease(identity);
        _leases.Clear();
    }

    public void Volume(double row, double volume, double timeOffsetSeconds = 0.0)
        => VolumeCore(null, row, volume, timeOffsetSeconds);

    public void Volume(SequenceCallsite callsite, double row, double volume, double timeOffsetSeconds = 0.0)
        => VolumeCore(callsite, row, volume, timeOffsetSeconds);

    private void VolumeCore(SequenceCallsite? callsite, double row, double volume, double timeOffsetSeconds)
    {
        if (!CheckOwned()) return;
        if (!double.IsFinite(volume) || volume < 0 || volume > 1) throw new ArgumentOutOfRangeException(nameof(volume));
        _owner.ControlVirtualGroup(_leases, row, new SetNoteVolumeCommand(volume), timeOffsetSeconds, callsite);
    }

    public void Seek(double row, double playbackSeconds, double timeOffsetSeconds = 0.0)
        => SeekCore(null, row, playbackSeconds, timeOffsetSeconds);

    public void Seek(SequenceCallsite callsite, double row, double playbackSeconds, double timeOffsetSeconds = 0.0)
        => SeekCore(callsite, row, playbackSeconds, timeOffsetSeconds);

    private void SeekCore(SequenceCallsite? callsite, double row, double playbackSeconds, double timeOffsetSeconds)
    {
        if (!CheckOwned()) return;
        _owner.ControlVirtualGroup(_leases, row, PatternScriptProgram.CreateSeekCommand(playbackSeconds), timeOffsetSeconds, callsite);
    }

    public void SeekRelative(double row, double deltaSeconds, double timeOffsetSeconds = 0.0)
        => SeekRelativeCore(null, row, deltaSeconds, timeOffsetSeconds);

    public void SeekRelative(SequenceCallsite callsite, double row, double deltaSeconds, double timeOffsetSeconds = 0.0)
        => SeekRelativeCore(callsite, row, deltaSeconds, timeOffsetSeconds);

    private void SeekRelativeCore(SequenceCallsite? callsite, double row, double deltaSeconds, double timeOffsetSeconds)
    {
        if (!CheckOwned()) return;
        _owner.ControlVirtualGroup(_leases, row, PatternScriptProgram.CreateSeekCommand(deltaSeconds, relative: true), timeOffsetSeconds, callsite);
    }

    public void Pan(double row, double x, double y = 0, double z = 0, double timeOffsetSeconds = 0.0)
        => PanCore(null, row, x, y, z, timeOffsetSeconds);

    public void Pan(SequenceCallsite callsite, double row, double x, double y = 0, double z = 0, double timeOffsetSeconds = 0.0)
        => PanCore(callsite, row, x, y, z, timeOffsetSeconds);

    private void PanCore(SequenceCallsite? callsite, double row, double x, double y, double z, double timeOffsetSeconds)
    {
        if (!CheckOwned()) return;
        _owner.ControlVirtualGroup(_leases, row, PatternScriptProgram.CreatePanCommand(x, y, z), timeOffsetSeconds, callsite);
    }

    public void Surround(double row, bool enabled, double timeOffsetSeconds = 0.0)
        => SurroundCore(null, row, enabled, timeOffsetSeconds);

    public void Surround(SequenceCallsite callsite, double row, bool enabled, double timeOffsetSeconds = 0.0)
        => SurroundCore(callsite, row, enabled, timeOffsetSeconds);

    private void SurroundCore(SequenceCallsite? callsite, double row, bool enabled, double timeOffsetSeconds)
    {
        if (!CheckOwned()) return;
        _owner.ControlVirtualGroup(_leases, row, new SetSurroundCommand(enabled), timeOffsetSeconds, callsite);
    }

    public void Filter(double row, double cutoff, double resonance, double timeOffsetSeconds = 0.0)
        => FilterCore(null, row, cutoff, resonance, timeOffsetSeconds);

    public void Filter(SequenceCallsite callsite, double row, double cutoff, double resonance, double timeOffsetSeconds = 0.0)
        => FilterCore(callsite, row, cutoff, resonance, timeOffsetSeconds);

    private void FilterCore(SequenceCallsite? callsite, double row, double cutoff, double resonance, double timeOffsetSeconds)
    {
        if (!CheckOwned()) return;
        PatternScriptProgram.ValidateUnitInterval(cutoff, nameof(cutoff));
        PatternScriptProgram.ValidateUnitInterval(resonance, nameof(resonance));
        _owner.ControlVirtualGroup(_leases, row, new SetResonantFilterCommand(cutoff, resonance), timeOffsetSeconds, callsite);
    }

    public void FilterCutoff(double row, double cutoff, double timeOffsetSeconds = 0.0)
        => FilterCutoffCore(null, row, cutoff, timeOffsetSeconds);

    public void FilterCutoff(SequenceCallsite callsite, double row, double cutoff, double timeOffsetSeconds = 0.0)
        => FilterCutoffCore(callsite, row, cutoff, timeOffsetSeconds);

    private void FilterCutoffCore(SequenceCallsite? callsite, double row, double cutoff, double timeOffsetSeconds)
    {
        if (!CheckOwned()) return;
        PatternScriptProgram.ValidateUnitInterval(cutoff, nameof(cutoff));
        _owner.ControlVirtualGroup(_leases, row, new SetResonantFilterCutoffCommand(cutoff), timeOffsetSeconds, callsite);
    }

    public void FilterResonance(double row, double resonance, double timeOffsetSeconds = 0.0)
        => FilterResonanceCore(null, row, resonance, timeOffsetSeconds);

    public void FilterResonance(SequenceCallsite callsite, double row, double resonance, double timeOffsetSeconds = 0.0)
        => FilterResonanceCore(callsite, row, resonance, timeOffsetSeconds);

    private void FilterResonanceCore(SequenceCallsite? callsite, double row, double resonance, double timeOffsetSeconds)
    {
        if (!CheckOwned()) return;
        PatternScriptProgram.ValidateUnitInterval(resonance, nameof(resonance));
        _owner.ControlVirtualGroup(_leases, row, new SetResonantFilterResonanceCommand(resonance), timeOffsetSeconds, callsite);
    }

    public void Off(double row, double timeOffsetSeconds = 0.0)
        => OffCore(null, row, timeOffsetSeconds);

    public void Off(SequenceCallsite callsite, double row, double timeOffsetSeconds = 0.0)
        => OffCore(callsite, row, timeOffsetSeconds);

    private void OffCore(SequenceCallsite? callsite, double row, double timeOffsetSeconds)
    {
        if (!CheckOwned()) return;
        _owner.ControlVirtualGroup(_leases, row, new NoteOffCommand(), timeOffsetSeconds, callsite);
    }

    public void Cut(double row, double timeOffsetSeconds = 0.0)
        => CutCore(null, row, timeOffsetSeconds);

    public void Cut(SequenceCallsite callsite, double row, double timeOffsetSeconds = 0.0)
        => CutCore(callsite, row, timeOffsetSeconds);

    private void CutCore(SequenceCallsite? callsite, double row, double timeOffsetSeconds)
    {
        if (!CheckOwned()) return;
        _owner.ControlVirtualGroup(_leases, row, new NoteCutCommand(), timeOffsetSeconds, callsite);
    }

    public void NoteFilter(double row, ObjectId? definitionId = null, double timeOffsetSeconds = 0.0)
        => NoteFilterCore(null, row, definitionId, timeOffsetSeconds);

    public void NoteFilter(SequenceCallsite callsite, double row, ObjectId? definitionId = null, double timeOffsetSeconds = 0.0)
        => NoteFilterCore(callsite, row, definitionId, timeOffsetSeconds);

    private void NoteFilterCore(SequenceCallsite? callsite, double row, ObjectId? definitionId, double timeOffsetSeconds)
    {
        if (!CheckOwned()) return;
        _owner.ControlVirtualGroup(_leases, row, new SetNoteFilterCommand(definitionId), timeOffsetSeconds, callsite);
    }

    public void ChannelFilter(double row, ObjectId? definitionId = null, double timeOffsetSeconds = 0.0)
        => ChannelFilterCore(null, row, definitionId, timeOffsetSeconds);

    public void ChannelFilter(SequenceCallsite callsite, double row, ObjectId? definitionId = null, double timeOffsetSeconds = 0.0)
        => ChannelFilterCore(callsite, row, definitionId, timeOffsetSeconds);

    private void ChannelFilterCore(SequenceCallsite? callsite, double row, ObjectId? definitionId, double timeOffsetSeconds)
    {
        if (!CheckOwned()) return;
        _owner.ControlVirtualGroup(_leases, row, new SetChannelFilterCommand(definitionId), timeOffsetSeconds, callsite);
    }

    public void SourceFrame(double row, long sourceFrameOffset, double timeOffsetSeconds = 0.0)
        => SourceFrameCore(null, row, sourceFrameOffset, timeOffsetSeconds);

    public void SourceFrame(SequenceCallsite callsite, double row, long sourceFrameOffset, double timeOffsetSeconds = 0.0)
        => SourceFrameCore(callsite, row, sourceFrameOffset, timeOffsetSeconds);

    private void SourceFrameCore(SequenceCallsite? callsite, double row, long sourceFrameOffset, double timeOffsetSeconds)
    {
        if (!CheckOwned()) return;
        if (sourceFrameOffset < 0) throw new ArgumentOutOfRangeException(nameof(sourceFrameOffset));
        _owner.ControlVirtualGroup(_leases, row, new SeekSourceFrameCommand(sourceFrameOffset), timeOffsetSeconds, callsite);
    }

    public void SourceFrameRelative(double row, long deltaFrames, double timeOffsetSeconds = 0.0)
        => SourceFrameRelativeCore(null, row, deltaFrames, timeOffsetSeconds);

    public void SourceFrameRelative(SequenceCallsite callsite, double row, long deltaFrames, double timeOffsetSeconds = 0.0)
        => SourceFrameRelativeCore(callsite, row, deltaFrames, timeOffsetSeconds);

    private void SourceFrameRelativeCore(SequenceCallsite? callsite, double row, long deltaFrames, double timeOffsetSeconds)
    {
        if (!CheckOwned()) return;
        _owner.ControlVirtualGroup(_leases, row, new SeekSourceFrameCommand(deltaFrames, Relative: true), timeOffsetSeconds, callsite);
    }

    public void Displacement(double row, NoteDisplacementAction action, double timeOffsetSeconds = 0.0)
        => DisplacementCore(null, row, action, timeOffsetSeconds);

    public void Displacement(SequenceCallsite callsite, double row, NoteDisplacementAction action, double timeOffsetSeconds = 0.0)
        => DisplacementCore(callsite, row, action, timeOffsetSeconds);

    private void DisplacementCore(SequenceCallsite? callsite, double row, NoteDisplacementAction action, double timeOffsetSeconds)
    {
        if (!CheckOwned()) return;
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        _owner.ControlVirtualGroup(_leases, row, new SetCurrentVoiceDisplacementActionCommand(action), timeOffsetSeconds, callsite);
    }

    public void Past(double row, TrackerPastNoteAction action, double timeOffsetSeconds = 0.0)
        => PastCore(null, row, action, timeOffsetSeconds);

    public void Past(SequenceCallsite callsite, double row, TrackerPastNoteAction action, double timeOffsetSeconds = 0.0)
        => PastCore(callsite, row, action, timeOffsetSeconds);

    private void PastCore(SequenceCallsite? callsite, double row, TrackerPastNoteAction action, double timeOffsetSeconds)
    {
        if (!CheckOwned()) return;
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        _owner.ControlVirtualGroup(_leases, row, new ApplyPastNoteActionCommand(action), timeOffsetSeconds, callsite);
    }

}
