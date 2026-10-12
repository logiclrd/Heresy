using System;
using System.Collections.Generic;

using Heresy.Core.Filters;
using Heresy.Render.Envelopes;
using Heresy.Render.Filters;

namespace Heresy.Render.Playback;

/// <summary>
/// A voice-private, allocation-free PCM control for numeric note Filter
/// envelopes. Construct/rebind only at an authored command boundary; the
/// per-frame path never clones configurations or allocates parameter values.
/// FIR kernel preparation is explicit and outside PCM rendering.
/// </summary>
internal sealed class NoteFilterEnvelopeRealtimeControls
{
	internal readonly record struct Binding(int LayerIndex, int StageId,
		string Parameter, EnvelopePlaybackState Envelope);

	private sealed class Stage
	{
		public required int Ordinal;
		public required int LayerIndex;
		public required int StageId;
		public required FilterKind Kind;
		public required FilterParameterDescriptor[] Descriptors;
		public required double[] Base;
		public required double[] Accepted;
		public required double[] Proposed;
		public int TapCount;
		public double TransitionWidth;
		public string PhaseResponse = "Linear";
		public bool Dirty;

		public int IndexOf(string parameter)
		{
			for (int i = 0; i < Descriptors.Length; i++)
				if (string.Equals(Descriptors[i].Name, parameter,
					StringComparison.OrdinalIgnoreCase))
					return i;
			return -1;
		}

		public double Read(string parameter, double fallback = 0.0)
		{
			int index = IndexOf(parameter);
			return index < 0 ? fallback : Proposed[index];
		}
	}

	private readonly record struct ActiveBinding(
		int StageIndex, int ParameterIndex,
		EnvelopePlaybackState Envelope);

	private readonly Stage[] _stages;
	private readonly FilterChain _authored;
	private ActiveBinding[] _bindings = [];
	private readonly FirControlPreparationPolicy? _firPolicy;
    private readonly FirPreparationMailbox? _firMailbox;
	private readonly FirControlRequest[] _firRequests;
    private readonly FirControlRequest[] _heldRequests;
    private int _heldCount;
	private readonly RealtimeFirStageUpdate[] _firUpdates;
	private PreparedFirControlBatch? _firReady;
	private bool _reportedBudget;

	private readonly RealtimeTptStageUpdate[] _tpt;
	private readonly RealtimeTptCoefficients[] _tptScratch;
	private readonly RealtimeBiquadStageUpdate[] _biquad;
	private readonly RealtimeBiquadCoefficients[] _biquadScratch;
	private readonly RealtimeLinearLadderStageUpdate[] _ladder;
	private readonly RealtimeLinearLadderCoefficients[] _ladderScratch;
	private readonly RealtimeSaturatedLadderStageUpdate[] _saturated;
	private readonly RealtimeSaturatedLadderCoefficients[] _saturatedScratch;
	private readonly RealtimeAllPassStageUpdate[] _allPass;
	private readonly RealtimeAllPassCoefficient[] _allPassScratch;
	private readonly RealtimeFeedbackCombStageUpdate[] _feedback;
	private readonly RealtimeFeedbackCombCoefficients[] _feedbackScratch;
	private readonly RealtimeFeedforwardCombStageUpdate[] _feedforward;
	private readonly RealtimeFeedforwardCombCoefficients[] _feedforwardScratch;

	internal NoteFilterEnvelopeRealtimeControls(FilterChain authored, int sampleRate)
	{
		_authored = authored ?? throw new ArgumentNullException(nameof(authored));
		if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
		List<Stage> stages = [];
		IReadOnlyList<FilterInstance> layers = authored.Layers;
		int firCount = 0;
		for (int layerIndex = 0; layerIndex < layers.Count; layerIndex++)
		{
			foreach (ConfiguredFilterStage stage in layers[layerIndex].GetStages())
			{
				List<FilterParameterDescriptor> descriptors = [];
				List<double> values = [];
				foreach (FilterParameterDescriptor descriptor in FilterCatalogue.GetParameters(stage.Kind))
				{
					if (!descriptor.IsModulatable) continue;
					descriptors.Add(descriptor);
					values.Add(stage.Parameters.GetParameter(descriptor.Name).NumericValue);
				}
				double[] baseline = [.. values];
				Stage entry = new()
				{
					Ordinal = stages.Count, LayerIndex = layerIndex,
					StageId = stage.StageId, Kind = stage.Kind,
					Descriptors = [.. descriptors], Base = baseline,
					Accepted = (double[])baseline.Clone(),
					Proposed = (double[])baseline.Clone(),
				};
				if (LinearPhaseFirFilterStageFactory.IsStandardFir(stage.Kind))
				{
					entry.TapCount = (int)stage.Parameters.GetParameter("TapCount").NumericValue;
					entry.TransitionWidth =
						stage.Parameters.GetParameter("TransitionWidthHz").NumericValue;
					entry.PhaseResponse =
						stage.Parameters.GetParameter("PhaseResponse").ChoiceValue!;
					firCount++;
				}
				stages.Add(entry);
			}
		}
		_stages = [.. stages];
		int n = _stages.Length;
		_tpt = new RealtimeTptStageUpdate[n];
		_tptScratch = new RealtimeTptCoefficients[n];
		_biquad = new RealtimeBiquadStageUpdate[n];
		_biquadScratch = new RealtimeBiquadCoefficients[n];
		_ladder = new RealtimeLinearLadderStageUpdate[n];
		_ladderScratch = new RealtimeLinearLadderCoefficients[n];
		_saturated = new RealtimeSaturatedLadderStageUpdate[n];
		_saturatedScratch = new RealtimeSaturatedLadderCoefficients[n];
		_allPass = new RealtimeAllPassStageUpdate[n];
		_allPassScratch = new RealtimeAllPassCoefficient[n];
		_feedback = new RealtimeFeedbackCombStageUpdate[n];
		_feedbackScratch = new RealtimeFeedbackCombCoefficients[n];
		_feedforward = new RealtimeFeedforwardCombStageUpdate[n];
		_feedforwardScratch = new RealtimeFeedforwardCombCoefficients[n];
		_firRequests = new FirControlRequest[Math.Min(firCount,
			FirControlPreparationPolicy.MaximumQueuedStages)];
		_firUpdates = new RealtimeFirStageUpdate[_firRequests.Length];
        _heldRequests = new FirControlRequest[_firRequests.Length];
		_firPolicy = firCount > 0 ? new FirControlPreparationPolicy(sampleRate) : null;
        _firMailbox = firCount > 0 ? new FirPreparationMailbox(_authored, sampleRate) : null;
	}

	/// <summary>Replace the entire binding set after an authored action;
	/// preserves last accepted DSP values so a detach restores the base.</summary>
	internal void SetBindings(IEnumerable<Binding> bindings)
	{
		ArgumentNullException.ThrowIfNull(bindings);
		List<ActiveBinding> prepared = [];
		HashSet<(int, int)> seen = [];
		foreach (Binding binding in bindings)
		{
			int stageIndex = -1;
			for (int i = 0; i < _stages.Length; i++)
				if (_stages[i].LayerIndex == binding.LayerIndex
					&& _stages[i].StageId == binding.StageId)
				{
					stageIndex = i;
					break;
				}
			if (stageIndex < 0)
				throw new ArgumentException("Bound note Filter stage no longer exists.");
			int parameterIndex = _stages[stageIndex].IndexOf(binding.Parameter);
			if (parameterIndex < 0)
				throw new ArgumentException("Bound note Filter parameter is not numeric.");
			if (!seen.Add((stageIndex, parameterIndex)))
				throw new ArgumentException("Duplicate note Filter envelope binding.");
			prepared.Add(new ActiveBinding(stageIndex, parameterIndex, binding.Envelope));
		}
		_bindings = [.. prepared];
		_firReady = null;
        _heldCount = 0;
		_firPolicy?.Clear();
		_reportedBudget = false;
	}

	/// <summary>Explicit bounded kernel design call at a control boundary,
	/// never within ProcessFrame or the system audio callback.</summary>
	internal bool TryPreparePendingFirs()
	{
		if (_firPolicy is null || _firPolicy.PendingCount == 0)
			return false;
		_firReady = null;
		return _firPolicy.TryPreparePending(_authored, out _firReady);
	}

    /// <summary>Accept a completed immutable control plan only between PCM blocks.</summary>
    internal bool AdoptPreparedFirs()
    {
        PreparedFirControlBatch? ready = _firMailbox?.TakeReady();
        if (ready is null) return false;
        _firReady = ready;
        return true;
    }

    /// <summary>Coalesce the latest request without running FIR design on PCM.</summary>
    internal bool QueuePendingFirs(FirPreparationScheduler scheduler)
    {
        if (_firPolicy is null || _firMailbox is null
            || _firPolicy.PendingCount == 0) return false;
        // A moving FIR control is sampled once at a quantum boundary and
        // then held until its prepared kernel can actually be published.
        // This prevents an audio-rate curve from invalidating every FFT
        // result before the worker has a chance to finish it.
        if (_heldCount == 0)
            _heldCount = _firPolicy.CopyPending(_heldRequests);
        return _firMailbox.CapturePending(_firPolicy, scheduler);
    }

	/// <summary>One complete atomic sample's numeric controls. Rejected
	/// values do not alter any coefficient/history or last accepted value.</summary>
	internal void Apply(long frame, StreamingFilterChain chain,
		Action<string>? warn)
	{
		if (_bindings.Length == 0)
		{
			// An earlier detach may still require one restoration to base.
			bool needsRestore = false;
			for (int i = 0; i < _stages.Length && !needsRestore; i++)
				for (int k = 0; k < _stages[i].Base.Length; k++)
					if (_stages[i].Base[k] != _stages[i].Accepted[k])
					{
						needsRestore = true;
						break;
					}
			if (!needsRestore) return;
		}

		for (int i = 0; i < _stages.Length; i++)
			Array.Copy(_stages[i].Base, _stages[i].Proposed,
				_stages[i].Base.Length);
		for (int i = 0; i < _bindings.Length; i++)
		{
			ActiveBinding binding = _bindings[i];
			Stage stage = _stages[binding.StageIndex];
			double value;
			try
			{
				value = binding.Envelope.GetValue(frame);
			}
			catch (Exception error) when (error is ArgumentException
				or InvalidOperationException or OverflowException)
			{
				warn?.Invoke(error.Message);
				return;
			}
			FilterParameterDescriptor descriptor =
				stage.Descriptors[binding.ParameterIndex];
			// FilterParameterValue is a reference record: do not construct
			// one for every frame. Mirror its numeric descriptor guards.
			if (!double.IsFinite(value)
				|| value < descriptor.Minimum!.Value
				|| value > descriptor.Maximum!.Value
				|| (descriptor.IsInteger && value != Math.Truncate(value))
				|| (descriptor.IsOddOnly && ((long)value & 1L) == 0))
			{
				warn?.Invoke("Note Filter Envelope sample violates numeric descriptor range.");
				return;
			}
			stage.Proposed[binding.ParameterIndex] = value;
		}

        // Keep the captured FIR target stable while its immutable plan is
        // designed on the worker. The numeric IIR controls remain sampled
        // at the current frame, but mixed-family publication is atomic.
        for (int i = 0; i < _heldCount; i++)
        {
            FirControlRequest hold = _heldRequests[i];
            Stage stage = _stages[hold.StageIndex];
            int index = stage.IndexOf("CutoffHz");
            if (index >= 0) stage.Proposed[index] = hold.CutoffHz;
            index = stage.IndexOf("LowCutoffHz");
            if (index >= 0) stage.Proposed[index] = hold.LowCutoffHz;
            index = stage.IndexOf("HighCutoffHz");
            if (index >= 0) stage.Proposed[index] = hold.HighCutoffHz;
        }

		bool anyDirty = false, firDirty = false;
		for (int i = 0; i < _stages.Length; i++)
		{
			Stage stage = _stages[i];
			stage.Dirty = false;
			for (int k = 0; k < stage.Proposed.Length; k++)
				if (stage.Proposed[k] != stage.Accepted[k])
				{
					stage.Dirty = true;
					break;
				}
			anyDirty |= stage.Dirty;
			firDirty |= stage.Dirty
				&& LinearPhaseFirFilterStageFactory.IsStandardFir(stage.Kind);
		}
		if (!anyDirty) return;

		int firCount = 0;
		PreparedFirControlBatch? ready = null;
		if (firDirty)
		{
			foreach (Stage stage in _stages)
			{
				if (!stage.Dirty
					|| !LinearPhaseFirFilterStageFactory.IsStandardFir(stage.Kind))
					continue;
				if (firCount >= _firRequests.Length)
				{
					_firPolicy?.Clear();
					_firReady = null;
					ReportBudget(warn);
					return;
				}
				_firRequests[firCount++] = new FirControlRequest(
					stage.Ordinal, stage.StageId, stage.Kind,
					stage.Read("CutoffHz"), stage.Read("LowCutoffHz"),
					stage.Read("HighCutoffHz"), stage.TapCount,
					stage.TransitionWidth, stage.PhaseResponse, stage.LayerIndex);
			}
			ready = _firReady;
			if (ready is null || !ready.AllRealtimePhases
				|| !ready.Matches(_firRequests.AsSpan(0, firCount)))
			{
				_firReady = null;
				if (!_firPolicy!.TryOffer(_firRequests.AsSpan(0, firCount)))
					ReportBudget(warn);
				return; // mixed IIR/FIR changes are never partially published
			}
		}
		else
		{
			_firReady = null;
			_firPolicy?.Clear();
		}

		int tptCount = 0, biquadCount = 0, ladderCount = 0, saturatedCount = 0;
		int allPassCount = 0, feedbackCount = 0, feedforwardCount = 0;
		foreach (Stage stage in _stages)
		{
			if (!stage.Dirty) continue;
			int index = stage.Ordinal;
			FilterKind kind = stage.Kind;
			if (kind == FilterKind.OnePole || SvfFilterStageFactory.IsSvf(kind))
				_tpt[tptCount++] = new(index, stage.Read("CutoffHz"),
					stage.Read("Q", 1.0));
			else if (BiquadFilterStageFactory.IsBiquad(kind))
				_biquad[biquadCount++] = new(index, stage.Read("CutoffHz"),
					stage.Read("Q", 1.0), stage.Read("ShelfSlope", 1.0),
					stage.Read("GainDb"));
			else if (kind == FilterKind.LadderLinear)
				_ladder[ladderCount++] = new(index, stage.Read("CutoffHz"),
					stage.Read("Resonance"));
			else if (kind == FilterKind.LadderSaturated)
				_saturated[saturatedCount++] = new(index, stage.Read("CutoffHz"),
					stage.Read("Resonance"), stage.Read("Drive"));
			else if (kind == FilterKind.AllPass)
				_allPass[allPassCount++] = new(index, stage.Read("CutoffHz"));
			else if (kind == FilterKind.CombFeedback)
				_feedback[feedbackCount++] = new(index, stage.Read("Feedback"),
					stage.Read("Mix"));
			else if (kind == FilterKind.CombFeedforward)
				_feedforward[feedforwardCount++] = new(index,
					stage.Read("Feedforward"), stage.Read("Mix"));
		}
		int readyCount = 0;
		if (ready is not null)
			foreach (PreparedFirControlStage stage in ready.Stages)
				_firUpdates[readyCount++] = new(stage.StageIndex, stage.Plan);

		if (!chain.TryApplyRealtimeNumericUpdates(
			_tpt.AsSpan(0, tptCount), _tptScratch,
			_biquad.AsSpan(0, biquadCount), _biquadScratch,
			_ladder.AsSpan(0, ladderCount), _ladderScratch,
			_saturated.AsSpan(0, saturatedCount), _saturatedScratch,
			_allPass.AsSpan(0, allPassCount), _allPassScratch,
			_feedback.AsSpan(0, feedbackCount), _feedbackScratch,
			_feedforward.AsSpan(0, feedforwardCount), _feedforwardScratch,
			_firUpdates.AsSpan(0, readyCount)))
		{
			warn?.Invoke("Note Filter Envelope batch failed atomic DSP preflight.");
			return;
		}
		foreach (Stage stage in _stages)
			if (stage.Dirty)
				Array.Copy(stage.Proposed, stage.Accepted, stage.Proposed.Length);
		if (ready is not null)
		{
            _heldCount = 0;
			_firReady = null;
			_firPolicy!.Clear();
			_reportedBudget = false;
		}
	}

	private void ReportBudget(Action<string>? warn)
	{
		if (_reportedBudget) return;
		_reportedBudget = true;
		warn?.Invoke("Note Filter Envelope FIR update exceeds bounded preparation budget; retaining last valid Filter response.");
	}
}
