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
	private ChannelTarget(ChannelTargetKind kind, int physicalChannel, VirtualChannelIdentity virtualIdentity)
	{
		Kind = kind;
		PhysicalChannel = physicalChannel;
		VirtualIdentity = virtualIdentity;
	}

	public ChannelTargetKind Kind { get; }
	public int PhysicalChannel { get; }
	public VirtualChannelIdentity VirtualIdentity { get; }
	/// <summary>Legacy numeric slot; not a complete generation-qualified identity.</summary>
	public uint VirtualChannelId => VirtualIdentity.Slot;

	public static ChannelTarget Global => new(ChannelTargetKind.Global, -1, default);
	public static ChannelTarget AllVirtualInScope => new(ChannelTargetKind.AllVirtualInScope, -1, default);
	public static ChannelTarget AllVirtual => new(ChannelTargetKind.AllVirtual, -1, default);

	public static ChannelTarget Physical(int channel)
	{
		if (channel < 0)
			throw new ArgumentOutOfRangeException(nameof(channel));
		return new ChannelTarget(ChannelTargetKind.Physical, channel, default);
	}

	public static ChannelTarget Virtual(uint id)
		=> Virtual(VirtualChannelIdentity.Legacy(id));

	public static ChannelTarget Virtual(VirtualChannelIdentity identity)
		=> new(ChannelTargetKind.Virtual, -1, identity);
}
