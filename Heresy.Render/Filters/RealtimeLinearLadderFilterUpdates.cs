namespace Heresy.Render.Filters;

/// <summary>A changed linear Ladder stage's authored numeric parameters.</summary>
internal readonly record struct RealtimeLinearLadderStageUpdate(
	int StageIndex, double CutoffHz, double Resonance);

/// <summary>Prevalidated TPT gain parameter and strictly stable feedback.</summary>
internal readonly record struct RealtimeLinearLadderCoefficients(
	double G, double Feedback);

/// <summary>
/// Allocation-free two-phase numeric update, leaving all integrator
/// histories and interpolation state untouched until batch commit.
/// </summary>
internal interface IRealtimeLinearLadderFilterState
{
	bool TryPrepareRealtime(double cutoff, double resonance,
		int sampleRate, out RealtimeLinearLadderCoefficients coefficients);
	void CommitRealtime(RealtimeLinearLadderCoefficients coefficients);
}
