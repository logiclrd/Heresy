using System;
using System.Collections.Generic;

using Heresy.Core.Objects;

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
	private int _retriggerCountdown;
	private byte _sampleOffsetHigh;
	private TrackerWaveform _vibratoWaveform;
	private double _vibratoDepthScale = 1.0;
	private TrackerWaveform _tremoloWaveform;
	private TrackerWaveform _panbrelloWaveform;
	private byte _midiMacroIndex;
	private bool _glissandoEnabled;

	/// <summary>
	/// Last explicit sound source selected on this mapped physical tracker
	/// channel. It is sequencing memory, not render-time voice state.
	/// </summary>
	public ObjectId CurrentSourceId { get; set; } = ObjectId.None;

	/// <summary>Logical channel's recalled note volume for source starts.
	/// The live renderer is authoritative when a voice has a volume slide.</summary>
	public double NoteVolume { get; set; } = 1.0;

	/// <summary>The scope of the most recently started flattened
	/// collection on this *logical* channel, until replaced by another
	/// note. Later-row effects on that channel must continue targeting
	/// its live source-note volume rather than an unrelated host voice.</summary>
	public long ActiveFlattenedSourceScopeId { get; set; }

	/// <summary>Per-current-instigator NNA override. Null chooses Cut,
	/// since a flattened collection has no single instrument voice from
	/// which to inherit an instrument-defined displacement policy.</summary>
	public NoteDisplacementAction? FlattenedSourceDisplacementAction { get; set; }

	/// <summary>
	/// Applies conventional whole-byte tracker effect-memory semantics. A
	/// non-zero parameter replaces the remembered value and is returned. Zero
	/// recalls the remembered value, or remains zero if the slot has never been
	/// set.
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

	/// <summary>
	/// Applies tracker memory independently to the high and low nibbles. This is
	/// used by effects such as Hxy/Uxy vibrato, where x=0 preserves the previous
	/// speed and y=0 preserves the previous depth.
	/// </summary>
	public byte ResolveEffectParameterNibbles(EffectMemorySlot slot, byte parameter)
	{
		byte remembered = _effectMemory.TryGetValue(slot, out byte value)
			? value
			: (byte)0;

		byte high = (parameter & 0xF0) != 0
			? (byte)(parameter & 0xF0)
			: (byte)(remembered & 0xF0);
		byte low = (parameter & 0x0F) != 0
			? (byte)(parameter & 0x0F)
			: (byte)(remembered & 0x0F);

		byte resolved = (byte)(high | low);

		if (parameter != 0)
			_effectMemory[slot] = resolved;

		return resolved;
	}

	public bool TryGetEffectParameter(EffectMemorySlot slot, out byte parameter)
		=> _effectMemory.TryGetValue(slot, out parameter);

	public void ClearEffectParameter(EffectMemorySlot slot)
		=> _effectMemory.Remove(slot);

	public byte SampleOffsetHigh
	{
		get => _sampleOffsetHigh;
		set
		{
			if (value > 0x0F)
				throw new ArgumentOutOfRangeException(nameof(value));
			_sampleOffsetHigh = value;
		}
	}

	public TrackerWaveform VibratoWaveform
	{
		get => _vibratoWaveform;
		set
		{
			if (!Enum.IsDefined(value))
				throw new ArgumentOutOfRangeException(nameof(value));
			_vibratoWaveform = value;
		}
	}

	public double VibratoDepthScale
	{
		get => _vibratoDepthScale;
		set
		{
			if (!(value >= 0.0)
				|| double.IsNaN(value)
				|| double.IsInfinity(value))
			{
				throw new ArgumentOutOfRangeException(nameof(value));
			}

			_vibratoDepthScale = value;
		}
	}

	public TrackerWaveform TremoloWaveform
	{
		get => _tremoloWaveform;
		set
		{
			if (!Enum.IsDefined(value))
				throw new ArgumentOutOfRangeException(nameof(value));
			_tremoloWaveform = value;
		}
	}

	public TrackerWaveform PanbrelloWaveform
	{
		get => _panbrelloWaveform;
		set
		{
			if (!Enum.IsDefined(value))
				throw new ArgumentOutOfRangeException(nameof(value));
			_panbrelloWaveform = value;
		}
	}
	public byte MidiMacroIndex
	{
		get => _midiMacroIndex;
		set
		{
			if (value > 0x0F)
				throw new ArgumentOutOfRangeException(nameof(value));
			_midiMacroIndex = value;
		}
	}

	public bool GlissandoEnabled
	{
		get => _glissandoEnabled;
		set => _glissandoEnabled = value;
	}

	public int RetriggerCountdown
	{
		get => _retriggerCountdown;
		set
		{
			if (value < 0 || value > 15)
				throw new ArgumentOutOfRangeException(nameof(value));
			_retriggerCountdown = value;
		}
	}

	public void ClearEffectMemory()
	{
		_effectMemory.Clear();
		_retriggerCountdown = 0;
		_sampleOffsetHigh = 0;
		_vibratoWaveform = TrackerWaveform.Sine;
		_vibratoDepthScale = 1.0;
		_tremoloWaveform = TrackerWaveform.Sine;
		_panbrelloWaveform = TrackerWaveform.Sine;
		_midiMacroIndex = 0;
		_glissandoEnabled = false;
	}
}
