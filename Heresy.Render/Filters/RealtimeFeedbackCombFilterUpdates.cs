namespace Heresy.Render.Filters;

/// <summary>
/// Proposed numeric Feedback/Mix values for one fixed-length IIR comb.
/// Structural delay and its circular history are never rebuilt here.
/// </summary>
internal readonly record struct RealtimeFeedbackCombStageUpdate(
	int StageIndex, double Feedback, double Mix);

/// <summary>Prevalidated coefficients with stable absolute feedback below unity.</summary>
internal readonly record struct RealtimeFeedbackCombCoefficients(
	double Feedback, double Mix);

/// <summary>Allocation-free atomic numeric preflight and history-preserving commit.</summary>
internal interface IRealtimeFeedbackCombFilterState
{
	bool TryPrepareRealtime(double feedback, double mix,
		out RealtimeFeedbackCombCoefficients coefficients);
	void CommitRealtime(RealtimeFeedbackCombCoefficients coefficients);
}
