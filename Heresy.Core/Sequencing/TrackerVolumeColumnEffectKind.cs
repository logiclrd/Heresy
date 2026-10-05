namespace Heresy.Core.Sequencing;

/// <summary>
/// IT volume-column compatibility operations A through H. The names describe
/// behavior rather than the encoded file byte ranges.
/// </summary>
public enum TrackerVolumeColumnEffectKind
{
	FineVolumeUp,
	FineVolumeDown,
	VolumeSlideUp,
	VolumeSlideDown,
	PitchSlideDown,
	PitchSlideUp,
	TonePortamento,
	Vibrato,
}
