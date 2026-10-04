using System.Collections.Generic;

namespace Heresy.Core.Sequencing;

/// <summary>
/// Sequencer-visible state belonging to one mapped physical playback channel.
/// This is distinct from render-time channel state: it exists while patterns
/// are translated and persists between pattern invocations in the same
/// sequencing context.
/// </summary>
public sealed class SequencingChannelState
{
	private readonly Dictionary<EffectMemorySlot, byte> _effectMemory = [];

	/// <summary>
	/// Applies conventional tracker effect-memory semantics. A non-zero
	/// parameter replaces the remembered value and is returned. Zero recalls
	/// the remembered value, or remains zero if the slot has never been set.
	/// </summary>
	public byte ResolveEffectParameter(EffectMemorySlot slot, byte parameter)
	{
		if (parameter != 0)
		{
			_effectMemory[slot] = parameter;
			return parameter;
		}

		return _effectMemory.TryGetValue(slot, out byte remembered)
			? remembered
			: (byte)0;
	}

	public bool TryGetEffectParameter(EffectMemorySlot slot, out byte parameter)
		=> _effectMemory.TryGetValue(slot, out parameter);

	public void ClearEffectParameter(EffectMemorySlot slot)
		=> _effectMemory.Remove(slot);

	public void ClearEffectMemory()
		=> _effectMemory.Clear();
}
