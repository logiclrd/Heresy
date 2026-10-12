using System;
using System.Collections.Generic;

using Heresy.Core.Filters;

namespace Heresy.Render.Filters;

/// <summary>
/// One nonstructural FIR control proposal captured without allocating.
/// Structural tap/window/phase/transition settings come from the immutable
/// authored stage and must not be changed by the FM sample.
/// </summary>
internal readonly record struct FirControlRequest(
	int StageIndex, int StageId, FilterKind Kind,
	double CutoffHz, double LowCutoffHz, double HighCutoffHz,
	int TapCount, double TransitionWidthHz, string PhaseResponse,
	int LayerIndex = 0);

/// <summary>A control-side prepared plan; publication into running DSP is
/// deliberately left to the separate per-FIR response milestones.</summary>
internal readonly record struct PreparedFirControlStage(
	int StageIndex, IStreamingFilterStagePlan Plan);

internal sealed class PreparedFirControlBatch(
	PreparedFirControlStage[] stages, FirControlRequest[] requests)
{
	public IReadOnlyList<PreparedFirControlStage> Stages { get; } =
		Array.AsReadOnly(stages);

	/// <summary>The exact immutable-value snapshot that was designed off-loop.
	/// A later PCM frame must match the whole snapshot before publication.</summary>
	internal bool Matches(ReadOnlySpan<FirControlRequest> proposed)
	{
		if (proposed.Length != requests.Length)
			return false;
		for (int i = 0; i < requests.Length; i++)
			if (proposed[i] != requests[i])
				return false;
		return true;
	}

	internal bool AllRealtimePhases
	{
		get
		{
			foreach (FirControlRequest request in requests)
				if (request.PhaseResponse is not ("Linear" or "Minimum" or "Maximum"))
					return false;
			return true;
		}
	}
}

/// <summary>
/// Invocation-local bounded handoff from the PCM evaluator to a non-PCM
/// control-preparation caller. Offer is allocation-free and constant-space:
/// at most four FIR stages; duplicate stages and invalid coupled parameters
/// are rejected as one batch. The latest accepted control snapshot replaces
/// the previous pending snapshot, never building an unbounded journal.
/// 
/// PreparePending MUST be called outside frame evaluation. Its deterministic
/// tap/FFT work-unit admission bound prevents expensive FIR plans from being
/// designed when overloaded. A missed/invalid/unprepared request leaves the
/// current DSP kernel and every other stage untouched.
/// </summary>
internal sealed class FirControlPreparationPolicy
{
	public const int MaximumQueuedStages = 4;
	public const int MaximumPreparationWorkUnits = 131072;
	public const int MaximumTemporaryBytes = 2 * 1024 * 1024;
	private readonly FirControlRequest[] _pending = new FirControlRequest[MaximumQueuedStages];
	private readonly int _sampleRate;
	private int _count;
	public int PendingCount => _count;

    /// <summary>Copy a bounded complete PCM proposal into caller-owned
    /// scratch without creating a live reference to mutable pending slots.</summary>
    internal int CopyPending(Span<FirControlRequest> destination)
    {
        if (destination.Length < _count)
            throw new ArgumentException("Insufficient FIR snapshot capacity.", nameof(destination));
        _pending.AsSpan(0, _count).CopyTo(destination);
        return _count;
    }

	public FirControlPreparationPolicy(int sampleRate)
	{
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));
		_sampleRate = sampleRate;
	}

	/// <summary>Entire sample's FIR proposal is validated before any slot
	/// changes. Rejection cancels earlier pending work to avoid stale commits.</summary>
	public bool TryOffer(ReadOnlySpan<FirControlRequest> requests)
	{
		if (requests.Length is < 1 or > MaximumQueuedStages)
		{
			_count = 0;
			return false;
		}
		long units = 0;
		long bytes = 0;
		for (int i = 0; i < requests.Length; i++)
		{
			FirControlRequest req = requests[i];
			if (!Validate(req, _sampleRate))
			{
				_count = 0;
				return false;
			}
			for (int j = 0; j < i; j++)
				if (requests[j].StageIndex == req.StageIndex
					|| (requests[j].LayerIndex == req.LayerIndex
						&& requests[j].StageId == req.StageId))
				{
					_count = 0;
					return false;
				}
			// Work-unit accounting is conservative, independent of the host
			// clock, and capped across the *whole* batch. Linear includes
			// tap/window design and normalization; non-linear phases account
			// for the padded FFT/cepstrum pipeline.
			int transformLength = 4096;
			if (req.PhaseResponse != "Linear")
			{
				while (transformLength < req.TapCount * 32)
					transformLength *= 2;
				units += (long)transformLength * 16;
				bytes += (long)transformLength * 32;
			}
			units += (long)req.TapCount * 12;
			bytes += (long)req.TapCount * 32;
			if (units > MaximumPreparationWorkUnits
				|| bytes > MaximumTemporaryBytes)
			{
				_count = 0;
				return false;
			}
		}
		requests.CopyTo(_pending);
		_count = requests.Length;
		return true;
	}

	public void Clear() => _count = 0;

	/// <summary>
	/// Explicit bounded control-side preparation, never called from the PCM
	/// evaluator. No active Filter chain or histories are changed here.
	/// Every member succeeds before a candidate batch is made available.
	/// </summary>
	public bool TryPreparePending(FilterInstance authored,
		out PreparedFirControlBatch? prepared)
	{
		ArgumentNullException.ThrowIfNull(authored);
		prepared = null;
		if (_count == 0)
			return false;
		try
		{
			FilterInstance candidate = authored.Clone();
			for (int i = 0; i < _count; i++)
			{
				FirControlRequest req = _pending[i];
				if (req.Kind is FilterKind.FirLowPass or FilterKind.FirHighPass)
					candidate.SetStageParameter(req.StageId, "CutoffHz",
						FilterParameterValue.FromNumber(req.CutoffHz));
				else
					candidate.SetStageParameters(req.StageId, [
						new("LowCutoffHz", FilterParameterValue.FromNumber(req.LowCutoffHz)),
						new("HighCutoffHz", FilterParameterValue.FromNumber(req.HighCutoffHz)),
					]);
			}
			IReadOnlyList<ConfiguredFilterStage> stages = candidate.GetStages();
			PreparedFirControlStage[] plans = new PreparedFirControlStage[_count];
			for (int i = 0; i < _count; i++)
			{
				FirControlRequest req = _pending[i];
				if ((uint)req.StageIndex >= (uint)stages.Count
					|| stages[req.StageIndex].StageId != req.StageId
					|| stages[req.StageIndex].Kind != req.Kind)
				{
					_count = 0;
					return false;
				}
				ConfiguredFilterStage resolved = stages[req.StageIndex];
				Dictionary<string, FilterParameterValue> parameters =
					new(StringComparer.OrdinalIgnoreCase);
				foreach (FilterParameterDescriptor descriptor in resolved.Parameters.Parameters)
					parameters.Add(descriptor.Name,
						resolved.Parameters.GetParameter(descriptor.Name));
				PreparedFilterStage stage = new(0, req.StageIndex,
					candidate.DefinitionId, req.StageId, req.Kind,
					candidate.GetStageGenerationForPersistence(req.StageId),
					parameters);
				plans[i] = new PreparedFirControlStage(req.StageIndex,
					LinearPhaseFirFilterStageFactory.Instance.Prepare(stage, _sampleRate));
			}
			FirControlRequest[] captured = new FirControlRequest[_count];
			Array.Copy(_pending, captured, _count);
			prepared = new PreparedFirControlBatch(plans, captured);
			_count = 0;
			return true;
		}
		catch (Exception error) when (error is ArgumentException
			or InvalidOperationException or OverflowException)
		{
			_count = 0;
			return false;
		}
	}

	/// <summary>Prepare the same bounded atomic FIR batch for a serial chain
	/// containing several independently configured note/scope Filters.
	/// StageIndex is the global serial ordinal; StageId is layer-local.
	/// All authored layers and their histories remain untouched.</summary>
	public bool TryPreparePending(FilterChain authored,
		out PreparedFirControlBatch? prepared)
	{
		ArgumentNullException.ThrowIfNull(authored);
		prepared = null;
		if (_count == 0) return false;
		try
		{
			IReadOnlyList<FilterInstance> layers = authored.Layers;
			FilterInstance[] proposed = new FilterInstance[layers.Count];
			for (int i = 0; i < layers.Count; i++)
				proposed[i] = layers[i].Clone();
			for (int i = 0; i < _count; i++)
			{
				FirControlRequest request = _pending[i];
				if ((uint)request.LayerIndex >= (uint)proposed.Length)
					throw new ArgumentException("Unrecognized FIR layer.");
				if (request.Kind is FilterKind.FirLowPass or FilterKind.FirHighPass)
					proposed[request.LayerIndex].SetStageParameter(
						request.StageId, "CutoffHz",
						FilterParameterValue.FromNumber(request.CutoffHz));
				else
					proposed[request.LayerIndex].SetStageParameters(
						request.StageId, [
							new("LowCutoffHz", FilterParameterValue.FromNumber(request.LowCutoffHz)),
							new("HighCutoffHz", FilterParameterValue.FromNumber(request.HighCutoffHz)),
						]);
			}
			PreparedFirControlStage[] stages = new PreparedFirControlStage[_count];
			for (int i = 0; i < _count; i++)
			{
				FirControlRequest request = _pending[i];
				int ordinal = 0;
				bool found = false;
				for (int layerIndex = 0; layerIndex < proposed.Length; layerIndex++)
				{
					FilterInstance instance = proposed[layerIndex];
					foreach (ConfiguredFilterStage stage in instance.GetStages())
					{
						if (ordinal++ != request.StageIndex) continue;
						if (layerIndex != request.LayerIndex
							|| stage.StageId != request.StageId
							|| stage.Kind != request.Kind)
							throw new ArgumentException("FIR stage identity changed.");
						Dictionary<string, FilterParameterValue> values =
							new(StringComparer.OrdinalIgnoreCase);
						foreach (FilterParameterDescriptor descriptor
							in stage.Parameters.Parameters)
							values.Add(descriptor.Name,
								stage.Parameters.GetParameter(descriptor.Name));
						PreparedFilterStage preparedStage = new(
							layerIndex, request.StageIndex, instance.DefinitionId,
							stage.StageId, stage.Kind,
							instance.GetStageGenerationForPersistence(stage.StageId),
							values);
						stages[i] = new PreparedFirControlStage(request.StageIndex,
							LinearPhaseFirFilterStageFactory.Instance.Prepare(
								preparedStage, _sampleRate));
						found = true;
					}
				}
				if (!found) throw new ArgumentException("FIR stage no longer exists.");
			}
			FirControlRequest[] requests = new FirControlRequest[_count];
			Array.Copy(_pending, requests, _count);
			prepared = new PreparedFirControlBatch(stages, requests);
			_count = 0;
			return true;
		}
		catch (Exception error) when (error is ArgumentException
			or InvalidOperationException or OverflowException)
		{
			_count = 0;
			return false;
		}
	}

	private static bool Validate(FirControlRequest req, int sampleRate)
	{
		if (!LinearPhaseFirFilterStageFactory.IsStandardFir(req.Kind)
			|| req.StageIndex < 0 || req.StageId <= 0
			|| req.TapCount < 3 || req.TapCount > 2047
			|| (req.TapCount & 1) == 0
			|| !double.IsFinite(req.TransitionWidthHz)
			|| req.TransitionWidthHz < 1 || req.TransitionWidthHz > 5000
			|| req.PhaseResponse is not ("Linear" or "Minimum" or "Maximum"))
			return false;
		double nyquist = sampleRate * 0.5;
		double half = req.TransitionWidthHz * 0.5;
		if (req.Kind is FilterKind.FirLowPass or FilterKind.FirHighPass)
		{
			double value = req.CutoffHz;
			if (!double.IsFinite(value) || value < 1 || value > 20000
				|| value >= nyquist)
				return false;
			double midpoint = req.Kind == FilterKind.FirLowPass
				? value + half : value - half;
			return midpoint > 0 && midpoint < nyquist;
		}
		if (!double.IsFinite(req.LowCutoffHz)
			|| !double.IsFinite(req.HighCutoffHz)
			|| req.LowCutoffHz < 1 || req.LowCutoffHz > 20000
			|| req.HighCutoffHz < 1 || req.HighCutoffHz > 20000
			|| req.LowCutoffHz >= req.HighCutoffHz
			|| req.HighCutoffHz >= nyquist)
			return false;
		double low = req.Kind == FilterKind.FirBandPass
			? req.LowCutoffHz - half : req.LowCutoffHz + half;
		double high = req.Kind == FilterKind.FirBandPass
			? req.HighCutoffHz + half : req.HighCutoffHz - half;
		return low > 0 && low < high && high < nyquist;
	}
}
