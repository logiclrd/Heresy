using System;

namespace Heresy.Core.Timing;

public static class SequencingConstants
{
	/// <summary>
	/// Heresy name for the 2.5-second interval used by traditional tracker
	/// tempo math. Tempo is expressed as ticks per diachron.
	/// </summary>
	public static readonly TimeSpan Diachron = TimeSpan.FromSeconds(2.5);

	public const double DefaultTempo = 125.0;
	public const int DefaultSpeed = 6;
}
