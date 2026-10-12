namespace Heresy.Render.Filters;

/// <summary>Changed numeric cutoff for one fixed-order All-Pass serial stage.</summary>
internal readonly record struct RealtimeAllPassStageUpdate(int StageIndex, double CutoffHz);

/// <summary>Prevalidated stable reflection coefficient for each all-pass section.</summary>
internal readonly record struct RealtimeAllPassCoefficient(double Value);

/// <summary>Atomic no-allocation numeric retuning that preserves all section histories.</summary>
internal interface IRealtimeAllPassFilterState
{
	bool TryPrepareRealtime(double cutoff, int sampleRate,
		out RealtimeAllPassCoefficient coefficient);
	void CommitRealtime(RealtimeAllPassCoefficient coefficient);
}
