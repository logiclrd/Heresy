namespace Heresy.Render.Filters;

/// <summary>
/// Numeric Feedforward/Mix change for one fixed-length two-tap FIR Comb.
/// </summary>
internal readonly record struct RealtimeFeedforwardCombStageUpdate(
	int StageIndex, double Feedforward, double Mix);

/// <summary>Prevalidated two-tap gain and wet/dry mix values.</summary>
internal readonly record struct RealtimeFeedforwardCombCoefficients(
	double Feedforward, double Mix);

/// <summary>
/// Prepare/commit without allocating or replacing the invocation's delay ring.
/// </summary>
internal interface IRealtimeFeedforwardCombFilterState
{
	bool TryPrepareRealtime(double feedforward, double mix,
		out RealtimeFeedforwardCombCoefficients coefficients);
	void CommitRealtime(RealtimeFeedforwardCombCoefficients coefficients);
}
