using System.Collections.Generic;

using Heresy.Core.Sequencing;

namespace Heresy.Scripting.Runtime;

/// <summary>
/// The authored relaxed invocation has already completed. Its disposal
/// order is not musical order. Emit a final-use release after the last
/// sorted raw reference to each generated identity, never before it.
/// The scheduler still owns fixed wall deadlines and actual delivery.
/// </summary>
internal static class RelaxedVirtualChannelReplay
{
    public static IEnumerable<RawPatternStep> Enumerate(IReadOnlyList<NoteEvent> sorted)
    {
        Dictionary<VirtualChannelIdentity, int>? last = null;
        for (int i = 0; i < sorted.Count; i++)
        {
            ChannelTarget target = sorted[i].Target;
            if (target.Kind == ChannelTargetKind.Virtual && target.VirtualIdentity.Generation != 0)
                (last ??= new())[target.VirtualIdentity] = i;
        }

        for (int i = 0; i < sorted.Count; i++)
        {
            NoteEvent note = sorted[i];
            yield return new RawPatternStep.Emit(note);
            if (last is not null && note.Target.Kind == ChannelTargetKind.Virtual
                && last.TryGetValue(note.Target.VirtualIdentity, out int final) && final == i)
            {
                last.Remove(note.Target.VirtualIdentity);
                yield return new RawPatternStep.ReleaseVirtualChannel(note.Target.VirtualIdentity);
            }
        }
    }
}
