using System;

namespace Heresy.Core.Timing;

public static class SequencingConstants
{
	/// <summary>
	/// Heresy name for the 2.5-second interval used by traditional tracker
	/// tempo math. Tempo is expressed as ticks per diachron.
	/// </summary>
	public static readonly TimeSpan Diachron = TimeSpan.FromSeconds(2.5);

	/// <summary>Universal musical C-4 tuning for pitch multiplier one.
	/// This is neither the sample's native frame rate nor an oscillator rate.</summary>
	public const double ReferenceC4FrequencyHz = 261.6255653005986;
	public const double DefaultTempo = 125.0;
	public const int DefaultSpeed = 6;
}
