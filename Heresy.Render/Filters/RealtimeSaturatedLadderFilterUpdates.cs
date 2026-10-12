namespace Heresy.Render.Filters;

/// <summary>Numeric modulation proposed for one fixed-oversampling saturated Ladder.</summary>
internal readonly record struct RealtimeSaturatedLadderStageUpdate(
	int StageIndex, double CutoffHz, double Resonance, double Drive);

/// <summary>Stable TPT gain, bounded feedback and validated nonlinear drive.</summary>
internal readonly record struct RealtimeSaturatedLadderCoefficients(
	double G, double Feedback, double Drive);

/// <summary>Two-phase allocation-free numeric retuning without replacing DSP histories.</summary>
internal interface IRealtimeSaturatedLadderFilterState
{
	bool TryPrepareRealtime(double cutoff, double resonance, double drive,
		int sampleRate, out RealtimeSaturatedLadderCoefficients coefficients);
	void CommitRealtime(RealtimeSaturatedLadderCoefficients coefficients);
}
