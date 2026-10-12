namespace Heresy.Render.Filters;

/// <summary>
/// A previously prepared FIR kernel. Preflight accepts only a compatible
/// linear- or minimum-phase plan with matching structural identity;
/// publication never designs taps or replaces PCM history.
/// </summary>
internal readonly record struct RealtimeFirStageUpdate(
	int StageIndex, IStreamingFilterStagePlan Plan);

internal interface IRealtimeFirFilterState
{
	bool TryPrepareRealtime(IStreamingFilterStagePlan plan);
	void CommitRealtime(IStreamingFilterStagePlan plan);
}
