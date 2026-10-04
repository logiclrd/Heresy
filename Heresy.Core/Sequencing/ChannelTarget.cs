using System;

namespace Heresy.Core.Sequencing;

public enum ChannelTargetKind
{
	Global = 0,
	Physical,
	Virtual,
	AllVirtualInScope,
	AllVirtual,
}

/// <summary>
/// Target of a note event. VirtualChannelId is local to the current pattern
/// invocation. Broadcast virtual targets apply only to pre-existing voices:
/// voices whose resolved note-start time is strictly earlier than the event.
/// </summary>
public readonly record struct ChannelTarget
{
	private ChannelTarget(ChannelTargetKind kind, int physicalChannel, uint virtualChannelId)
	{
		Kind = kind;
		PhysicalChannel = physicalChannel;
		VirtualChannelId = virtualChannelId;
	}

	public ChannelTargetKind Kind { get; }
	public int PhysicalChannel { get; }
	public uint VirtualChannelId { get; }

	public static ChannelTarget Global => new(ChannelTargetKind.Global, -1, 0);
	public static ChannelTarget AllVirtualInScope => new(ChannelTargetKind.AllVirtualInScope, -1, 0);
	public static ChannelTarget AllVirtual => new(ChannelTargetKind.AllVirtual, -1, 0);

	public static ChannelTarget Physical(int channel)
	{
		if (channel < 0)
			throw new ArgumentOutOfRangeException(nameof(channel));
		return new ChannelTarget(ChannelTargetKind.Physical, channel, 0);
	}

	public static ChannelTarget Virtual(uint id)
		=> new(ChannelTargetKind.Virtual, -1, id);
}
