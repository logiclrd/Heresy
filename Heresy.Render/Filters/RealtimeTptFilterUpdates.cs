using System;

namespace Heresy.Render.Filters;

/// <summary>
/// One prepared, allocation-free compatible numeric control change. The
/// caller owns the arrays that contain these values; no instance/definition
/// objects or DSP state are published during the preflight phase.
/// </summary>
public readonly record struct RealtimeTptStageUpdate(
	int StageIndex, double CutoffHz, double Q);

/// <summary>Small coefficient payload kept in caller-owned scratch storage.</summary>
internal readonly record struct RealtimeTptCoefficients(double G, double K);

/// <summary>
/// Optional two-phase, no-allocation TPT retune contract. The core chain
/// validates ALL changed stages before mutating any running history.
/// </summary>
internal interface IRealtimeTptFilterState
{
	bool TryPrepareRealtime(
		double cutoffHz, double q, int sampleRate,
		out RealtimeTptCoefficients coefficients);
	void CommitRealtime(RealtimeTptCoefficients coefficients);
}
