using System;

using Heresy.Render.Sounds;

namespace Heresy.Render.Samples;

public sealed class SampleSoundState : SoundState
{
	private long _sourceFrameOffset;

	// Latest unwrapped source target, relative note frame, and integrated
	// pitch position at the resolved seek deadline. No replay history.
	internal readonly record struct PlayheadAnchor(double Target, long Frame, double PitchPosition);
	internal PlayheadAnchor? SeekAnchor { get; set; }

	protected override void OnPlaybackOffsetChanged() => SeekAnchor = null;

	public long SourceFrameOffset
	{
		get => _sourceFrameOffset;
		internal set
		{
			if (value < 0)
				throw new ArgumentOutOfRangeException(nameof(value));
			_sourceFrameOffset = value;
			SeekAnchor = null;
		}
	}
}
