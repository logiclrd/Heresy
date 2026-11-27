using System;

using Heresy.Core.Objects;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;

namespace Heresy.Scripting.Runtime;

/// <summary>Exclusive script-side ownership of a logical virtual channel.
/// Disposal only releases the lease; scheduled audio is independent.</summary>
public sealed class VirtualNote : IDisposable
{
    private readonly PatternScriptProgram _owner;
    private readonly VirtualChannelIdentity _identity;
    private bool _disposed;

    internal VirtualNote(PatternScriptProgram owner, VirtualChannelIdentity identity)
    {
        _owner = owner;
        _identity = identity;
    }

    private bool CheckOwned()
    {
        if (!_disposed && _owner.OwnsVirtualChannelLease(_identity))
            return true;
        _owner.ReportVirtualOwnershipIssue("Ignored operation on a disposed, transferred or no-longer-owned VirtualNote.");
        return false;
    }

    // Transfer is a two-phase operation: validate ownership first,
    // reserve destination storage, then invalidate this wrapper.
    // No lease release/reacquisition occurs during transfer.
    internal bool TryGetTransferIdentity(PatternScriptProgram destination,
        out VirtualChannelIdentity identity)
    {
        identity = _identity;
        return ReferenceEquals(_owner, destination)
            && !_disposed && _owner.OwnsVirtualChannelLease(_identity);
    }

    internal void MarkTransferred() => _disposed = true;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.ReleaseVirtualChannelLease(_identity);
    }

    public void Note(double row, ObjectId sourceId, double pitchMultiplier = 1.0,
        double playbackSpeedMultiplier = 1.0,
        double? volume = null, double timeOffsetSeconds = 0.0,
        ObjectId? eventFilterDefinitionId = null)
        => NoteCore(null, row, sourceId, pitchMultiplier, playbackSpeedMultiplier,
            volume, timeOffsetSeconds, eventFilterDefinitionId);

    public void Note(SequenceCallsite callsite, double row, ObjectId sourceId,
        double pitchMultiplier = 1.0, double playbackSpeedMultiplier = 1.0,
        double? volume = null, double timeOffsetSeconds = 0.0,
        ObjectId? eventFilterDefinitionId = null)
        => NoteCore(callsite, row, sourceId, pitchMultiplier, playbackSpeedMultiplier,
            volume, timeOffsetSeconds, eventFilterDefinitionId);

    private void NoteCore(SequenceCallsite? callsite, double row, ObjectId sourceId,
        double pitchMultiplier, double playbackSpeedMultiplier,
        double? volume, double timeOffsetSeconds, ObjectId? eventFilterDefinitionId)
    {
        if (!CheckOwned()) return;
        _owner.StartVirtualLease(_identity, row, sourceId, pitchMultiplier,
            playbackSpeedMultiplier, volume, timeOffsetSeconds,
            eventFilterDefinitionId, callsite);
    }

    public void Volume(double row, double volume, double timeOffsetSeconds = 0.0)
        => VolumeCore(null, row, volume, timeOffsetSeconds);

    public void Volume(SequenceCallsite callsite, double row, double volume, double timeOffsetSeconds = 0.0)
        => VolumeCore(callsite, row, volume, timeOffsetSeconds);

    private void VolumeCore(SequenceCallsite? callsite, double row, double volume, double timeOffsetSeconds)
    {
        if (!CheckOwned()) return;
        if (!double.IsFinite(volume) || volume < 0 || volume > 1) throw new ArgumentOutOfRangeException(nameof(volume));
        _owner.ControlVirtualLease(_identity, row, new SetNoteVolumeCommand(volume), timeOffsetSeconds, callsite);
    }

    public void Off(double row, double timeOffsetSeconds = 0.0)
        => OffCore(null, row, timeOffsetSeconds);

    public void Off(SequenceCallsite callsite, double row, double timeOffsetSeconds = 0.0)
        => OffCore(callsite, row, timeOffsetSeconds);

    private void OffCore(SequenceCallsite? callsite, double row, double timeOffsetSeconds)
    {
        if (!CheckOwned()) return;
        _owner.ControlVirtualLease(_identity, row, new NoteOffCommand(), timeOffsetSeconds, callsite);
    }

    public void Cut(double row, double timeOffsetSeconds = 0.0)
        => CutCore(null, row, timeOffsetSeconds);

    public void Cut(SequenceCallsite callsite, double row, double timeOffsetSeconds = 0.0)
        => CutCore(callsite, row, timeOffsetSeconds);

    private void CutCore(SequenceCallsite? callsite, double row, double timeOffsetSeconds)
    {
        if (!CheckOwned()) return;
        _owner.ControlVirtualLease(_identity, row, new NoteCutCommand(), timeOffsetSeconds, callsite);
    }

    public void NoteFilter(double row, ObjectId? definitionId = null, double timeOffsetSeconds = 0.0)
        => NoteFilterCore(null, row, definitionId, timeOffsetSeconds);

    public void NoteFilter(SequenceCallsite callsite, double row, ObjectId? definitionId = null, double timeOffsetSeconds = 0.0)
        => NoteFilterCore(callsite, row, definitionId, timeOffsetSeconds);

    private void NoteFilterCore(SequenceCallsite? callsite, double row, ObjectId? definitionId, double timeOffsetSeconds)
    {
        if (!CheckOwned()) return;
        _owner.ControlVirtualLease(_identity, row, new SetNoteFilterCommand(definitionId), timeOffsetSeconds, callsite);
    }

    public void ChannelFilter(double row, ObjectId? definitionId = null, double timeOffsetSeconds = 0.0)
        => ChannelFilterCore(null, row, definitionId, timeOffsetSeconds);

    public void ChannelFilter(SequenceCallsite callsite, double row, ObjectId? definitionId = null, double timeOffsetSeconds = 0.0)
        => ChannelFilterCore(callsite, row, definitionId, timeOffsetSeconds);

    private void ChannelFilterCore(SequenceCallsite? callsite, double row, ObjectId? definitionId, double timeOffsetSeconds)
    {
        if (!CheckOwned()) return;
        _owner.ControlVirtualLease(_identity, row, new SetChannelFilterCommand(definitionId), timeOffsetSeconds, callsite);
    }

    public void SourceFrame(double row, long sourceFrameOffset, double timeOffsetSeconds = 0.0)
        => SourceFrameCore(null, row, sourceFrameOffset, timeOffsetSeconds);

    public void SourceFrame(SequenceCallsite callsite, double row, long sourceFrameOffset, double timeOffsetSeconds = 0.0)
        => SourceFrameCore(callsite, row, sourceFrameOffset, timeOffsetSeconds);

    private void SourceFrameCore(SequenceCallsite? callsite, double row, long sourceFrameOffset, double timeOffsetSeconds)
    {
        if (!CheckOwned()) return;
        if (sourceFrameOffset < 0) throw new ArgumentOutOfRangeException(nameof(sourceFrameOffset));
        _owner.ControlVirtualLease(_identity, row, new SetSourceFrameOffsetCommand(sourceFrameOffset), timeOffsetSeconds, callsite);
    }

    public void Displacement(double row, NoteDisplacementAction action, double timeOffsetSeconds = 0.0)
        => DisplacementCore(null, row, action, timeOffsetSeconds);

    public void Displacement(SequenceCallsite callsite, double row, NoteDisplacementAction action, double timeOffsetSeconds = 0.0)
        => DisplacementCore(callsite, row, action, timeOffsetSeconds);

    private void DisplacementCore(SequenceCallsite? callsite, double row, NoteDisplacementAction action, double timeOffsetSeconds)
    {
        if (!CheckOwned()) return;
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        _owner.ControlVirtualLease(_identity, row, new SetCurrentVoiceDisplacementActionCommand(action), timeOffsetSeconds, callsite);
    }

    public void Past(double row, TrackerPastNoteAction action, double timeOffsetSeconds = 0.0)
        => PastCore(null, row, action, timeOffsetSeconds);

    public void Past(SequenceCallsite callsite, double row, TrackerPastNoteAction action, double timeOffsetSeconds = 0.0)
        => PastCore(callsite, row, action, timeOffsetSeconds);

    private void PastCore(SequenceCallsite? callsite, double row, TrackerPastNoteAction action, double timeOffsetSeconds)
    {
        if (!CheckOwned()) return;
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        _owner.ControlVirtualLease(_identity, row, new ApplyPastNoteActionCommand(action), timeOffsetSeconds, callsite);
    }

}
