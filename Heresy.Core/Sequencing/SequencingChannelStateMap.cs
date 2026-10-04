using System;
using System.Collections.Generic;

namespace Heresy.Core.Sequencing;

/// <summary>
/// Sparse state table keyed by fully mapped physical channel number.
/// Flattened sequencing contexts share this table. Mixdown contexts receive a
/// new table because their local playback channels are independent.
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
