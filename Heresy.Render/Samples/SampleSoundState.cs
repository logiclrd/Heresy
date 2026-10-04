using System;

using Heresy.Render.Sounds;

namespace Heresy.Render.Samples;

public sealed class SampleSoundState : SoundState
{
	private long _sourceFrameOffset;

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
