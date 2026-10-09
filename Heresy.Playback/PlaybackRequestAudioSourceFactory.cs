using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Diagnostics;
using Heresy.Core.Sequencing;
using Heresy.Render.Configuration;
using Heresy.Render.Realtime;
using Heresy.Render.Samples;
using Heresy.Scripting.Analysis;

namespace Heresy.Playback;

public sealed class PlaybackSourceCompilationException : InvalidOperationException
{
	public PlaybackSourceCompilationException(
		string message, IReadOnlyList<ScriptAnalysisDiagnostic> diagnostics)
		: base(message)
	{
		Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
	}
	public IReadOnlyList<ScriptAnalysisDiagnostic> Diagnostics { get; }
}

/// <summary>
/// The realtime transport now uses exactly the same coroutine recursion
/// as offline rendering. All script execution, timing and PCM synthesis
/// happen on the SDL-owned dedicated PCM worker, not during Create().
/// </summary>
public sealed class PlaybackRequestAudioSourceFactory :
	IBackgroundPlaybackSourceFactory,
	IPlaybackPositionTimelineProvider,
	IPlaybackRuntimeDiagnosticReportProvider
{
	private readonly RenderConfiguration _configuration;
	private readonly ISampleDataProvider _samples;
	private readonly object _gate = new();
	private readonly Dictionary<PlaybackRequest, PreparedIncrementalPlaybackPlan>
		_active = new(ReferenceEqualityComparer.Instance);
	private readonly Dictionary<PlaybackRequest, PlaybackPositionTimeline>
		_positions = new(ReferenceEqualityComparer.Instance);

	public PlaybackRequestAudioSourceFactory(RenderConfiguration configuration)
		: this(configuration, new InMemorySampleDataProvider()) { }

	public PlaybackRequestAudioSourceFactory(RenderConfiguration configuration,
		ISampleDataProvider sampleDataProvider)
	{
		_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		_samples = sampleDataProvider ?? throw new ArgumentNullException(nameof(sampleDataProvider));
	}

	public IAudioOutputSource Create(PlaybackRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		PreparedIncrementalPlaybackFactory factory = new(_configuration, _samples);
		PreparedIncrementalPlaybackPlan plan;
		try
		{
			plan = request switch
			{
				SequencePlaybackRequest sequence => factory.Create(
					sequence.Snapshot, sequence.SequenceId,
					startOrder: sequence.StartPosition?.Order ?? 0,
					startRow: sequence.StartPosition?.Row),
				PatternPlaybackRequest pattern => factory.Create(
					pattern.Snapshot, pattern.PatternId,
					startRow: pattern.StartRow,
					repeatPattern: pattern.Repeat),
				AdHocPlaybackRequest adHoc =>
					factory.CreateAdHoc(adHoc.Snapshot, adHoc.Schedule),
				_ => throw new NotSupportedException(
					$"Unsupported playback request: {request.GetType().Name}"),
			};
		}
		catch (ArgumentException error)
		{
			throw new PlaybackSourceCompilationException(
				$"Could not prepare recursive playback: {error.Message}",
				[new ScriptAnalysisDiagnostic(
					"HRS3001", ScriptDiagnosticSeverity.Error,
					error.Message, new ScriptSourceSpan(0, 0))]);
		}
		PlaybackPositionTimeline? positions = request is AdHocPlaybackRequest
			? null : new PlaybackPositionTimeline();
		if (positions is not null)
		{
			plan.Timeline.RowBegan += (id, row, sequence, order, at) =>
				positions.Append(new PlaybackPositionTimelineEntry(at,
					new PlaybackPatternPosition(id, row, sequence, order)));
		}
		lock (_gate)
		{
			_active.Clear();
			_active[request] = plan;
			if (positions is not null)
				_positions[request] = positions;
		}
		return new LiveRecursiveSource(plan, positions);
	}

	public bool TryTakePlaybackPositionTimeline(PlaybackRequest request,
		out PlaybackPositionTimeline? timeline)
	{
		lock (_gate)
			return _positions.Remove(request, out timeline);
	}

	public bool TryTakeRuntimeDiagnostics(PlaybackRequest request,
		out SequencingDiagnostic[] diagnostics)
	{
		lock (_gate)
		{
			if (_active.TryGetValue(request, out PreparedIncrementalPlaybackPlan? plan))
			{
				diagnostics = plan.SequencingContext.Diagnostics.Drain();
				if (diagnostics.Length != 0)
				{
					_active.Remove(request);
					return true;
				}
			}
		}
		diagnostics = [];
		return false;
	}

	private sealed class LiveRecursiveSource(
		PreparedIncrementalPlaybackPlan plan,
		PlaybackPositionTimeline? positions) : ILiveAudioOutputSource, IDisposable
	{
		private readonly ConcurrentQueue<LivePlaybackEvent> _commands = new();
		public AudioOutputFormat Format => plan.Source.Format;

		public void EnqueueLiveEvent(ChannelTarget target,
			IReadOnlyList<NoteCommand> commands)
		{
			ArgumentNullException.ThrowIfNull(commands);
			_commands.Enqueue(new LivePlaybackEvent(target, commands.ToArray()));
		}
		public void Render(int frameCount, Span<float> destination)
		{
			while (_commands.TryDequeue(out LivePlaybackEvent? command))
				plan.Session.ApplyLiveEvent(command.Target, command.Commands);
			plan.Source.Render(frameCount, destination);
			if (plan.Source.IsComplete && positions is not null)
				positions.Finish(plan.Source.LogicalDuration);
		}
		public void Dispose() => plan.Dispose();
	}
}
