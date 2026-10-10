using System;

using Heresy.Render.Sounds;

namespace Heresy.Render.Samples;

public sealed class SampleSoundState : SoundState
{
	private long _sourceFrameOffset;

	/// <summary>
	/// Invocation-local policy selected by InstrumentSound after its final
	/// volume envelope has been resolved. A bare sample still ends at Note
	/// Off; an Instrument-selected sample stays active while that envelope
	/// performs its release. This never changes a shared SampleDefinition.
	/// </summary>
	internal bool ReleaseIsControlledByVolumeEnvelope { get; set; }

	public long SourceFrameOffset
	{
		get => _sourceFrameOffset;
		internal set
		{
			if (value < 0)
				throw new ArgumentOutOfRangeException(nameof(value));
			_sourceFrameOffset = value;
		}
	}
}
