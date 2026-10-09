using System;
using System.Collections.Generic;

namespace Heresy.Core.Sequencing;

/// <summary>
/// Sparse per-context tracker Source and effect memory.
/// Root contexts may address it by physical channel number; flattened
/// invocations address it by local logical-channel number and never share
/// the map with parent or sibling invocations.
/// </summary>
public sealed class SequencingChannelStateMap
{
	private readonly Dictionary<int, SequencingChannelState> _physicalChannels = [];

	public SequencingChannelState GetPhysical(int channel)
	{
		if (channel < 0)
			throw new ArgumentOutOfRangeException(nameof(channel));

		if (!_physicalChannels.TryGetValue(channel, out SequencingChannelState? state))
		{
			state = new SequencingChannelState();
			_physicalChannels.Add(channel, state);
		}

		return state;
	}

	public bool TryGetPhysical(int channel, out SequencingChannelState? state)
	{
		if (channel < 0)
			throw new ArgumentOutOfRangeException(nameof(channel));

		return _physicalChannels.TryGetValue(channel, out state);
	}

	public void Clear()
		=> _physicalChannels.Clear();
}
