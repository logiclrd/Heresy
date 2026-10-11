namespace Heresy.Render.Filters;

/// <summary>
/// Prepared scalar Audio EQ biquad targets for a particular serial stage.
/// Unconnected values are supplied from the independent per-use instance.
/// </summary>
internal readonly record struct RealtimeBiquadStageUpdate(
	int StageIndex, double CutoffHz, double Q, double ShelfSlope, double GainDb);

/// <summary>
/// Exact normalized RBJ coefficients, written to invocation-owned scratch
/// by preflight before publishing any changes to processor histories.
/// </summary>
internal readonly record struct RealtimeBiquadCoefficients(
	double B0, double B1, double B2, double A1, double A2);

/// <summary>Two-phase, allocation-free biquad numeric-update contract.</summary>
internal interface IRealtimeBiquadFilterState
{
	bool TryPrepareRealtime(double cutoff, double q, double shelfSlope,
		double gainDb, int sampleRate, out RealtimeBiquadCoefficients target);
	void CommitRealtime(RealtimeBiquadCoefficients target);
}
