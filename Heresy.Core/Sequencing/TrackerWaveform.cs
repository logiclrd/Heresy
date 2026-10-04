namespace Heresy.Core.Sequencing;

/// <summary>
/// Impulse-Tracker modulation waveform selected by S3x/S4x.
/// </summary>
public enum TrackerWaveform : byte
{
	Sine = 0,
	RampDown = 1,
	Square = 2,
	Random = 3,
}
