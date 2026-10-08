using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Diagnostics;
using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Core.Sequencing;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Instruments;
using Heresy.Render.Playback;
using Heresy.Render.Realtime;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;
using Heresy.Scripting.Analysis;
using Heresy.Scripting.Compilation;

namespace Heresy.Playback;

public sealed class PlaybackSourceCompilationException
	: InvalidOperationException
{
	public PlaybackSourceCompilationException(
		string message,
		IReadOnlyList<ScriptAnalysisDiagnostic> diagnostics)
		: base(message)
	{
		Diagnostics =
			diagnostics
				?? throw new ArgumentNullException(nameof(diagnostics));
	}

	public IReadOnlyList<ScriptAnalysisDiagnostic> Diagnostics { get; }
}

/// <summary>
/// Concrete snapshot-to-PCM composition layer. Sequencing and Roslyn-backed
/// script compilation happen here, while the realtime controller remains
/// ignorant of song structure and the audio backend remains a pure PCM sink.
/// </summary>
public sealed class PlaybackRequestAudioSourceFactory
	: IBackgroundPlaybackSourceFactory,
		IPlaybackPositionTimelineProvider,
		IPlaybackRuntimeDiagnosticReportProvider
{
	private readonly RenderConfiguration _configuration;
	private readonly ISampleDataProvider _sampleDataProvider;
	private readonly object _timelineGate = new();
	private readonly Dictionary<PlaybackRequest, PlaybackPositionTimeline>
		_positionTimelines =
			new(ReferenceEqualityComparer.Instance);
	private readonly Dictionary<PlaybackRequest, SequencingDiagnostic[]>
		_diagnosticReports =
			new(ReferenceEqualityComparer.Instance);

	public PlaybackRequestAudioSourceFactory(
		RenderConfiguration configuration)
		: this(
			configuration,
			new InMemorySampleDataProvider())
	{
	}

	public PlaybackRequestAudioSourceFactory(
		RenderConfiguration configuration,
		ISampleDataProvider sampleDataProvider)
	{
		_configuration =
			configuration
				?? throw new ArgumentNullException(nameof(configuration));
		_sampleDataProvider =
			sampleDataProvider
				?? throw new ArgumentNullException(nameof(sampleDataProvider));
	}

	public IAudioOutputSource Create(
		PlaybackRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);

		SongDocument document =
			request.Snapshot.Document;
		PlaybackSnapshotSoundResolver resolver =
			new(
				document,
				_sampleDataProvider);
		SequencingContext sequencingContext = new();

		switch (request)
		{
			case SequencePlaybackRequest sequence:
				{
					SequencePlaybackPosition? start =
						sequence.StartPosition;
					SongScheduleCompilationResult compilation =
						SongScheduleCompiler.CompileSequence(
							document,
							sequence.SequenceId,
							start?.Order ?? 0,
							start?.Row,
							sequencingContext);

					ThrowIfFailed(
						compilation,
						$"Could not compile sequence {sequence.SequenceId.Value} for playback.");
					// The compiler expands flattened sources during each active
					// parent row; re-expanding a finished schedule would lose
					// shared timing and tracker-channel memory.
					IAudioOutputSource source =
						CreateSource(compilation.Schedule!, resolver);
					return AttachTimeline(
						sequence,
						source,
						compilation,
						sequence.SequenceId,
						repeat: false,
						sequencingContext);
				}

			case PatternPlaybackRequest pattern:
				{
					SongScheduleCompilationResult compilation =
						SongScheduleCompiler.CompilePattern(
							document,
							pattern.PatternId,
							pattern.StartRow,
							sequencingContext);

					ThrowIfFailed(
						compilation,
						$"Could not compile pattern {pattern.PatternId.Value} for playback.");

					IAudioOutputSource source;
					if (!pattern.Repeat)
					{
						source =
							CreateSource(
								compilation.Schedule!,
								resolver);
					}
					else
					{
						long cycleFrames =
							FrameTime.Ceiling(
								compilation.Duration,
								_configuration.SampleRate);
						source =
							new RepeatingPlaybackSource(
								_configuration,
								compilation.Schedule!,
								resolver,
								cycleFrames);
					}

					return AttachTimeline(
						pattern,
						source,
						compilation,
						sequenceId: null,
						pattern.Repeat,
						sequencingContext);
				}

			case AdHocPlaybackRequest adHoc:
				return CreateSource(
					FlattenedNestedScheduleExpander.Expand(
						document,
						adHoc.Schedule,
						TimeSpan.Zero).Schedule,
					resolver);

			default:
				throw new NotSupportedException(
					$"Playback request type {request.GetType().FullName} is not supported.");
		}
	}

	public bool TryTakePlaybackPositionTimeline(
		PlaybackRequest request,
		out PlaybackPositionTimeline? timeline)
	{
		ArgumentNullException.ThrowIfNull(request);

		lock (_timelineGate)
		{
			if (_positionTimelines.Remove(
				request,
				out PlaybackPositionTimeline? found))
			{
				timeline = found;
				return true;
			}
		}

		timeline = null;
		return false;
	}

	public bool TryTakeRuntimeDiagnostics(
		PlaybackRequest request,
		out SequencingDiagnostic[] diagnostics)
	{
		ArgumentNullException.ThrowIfNull(request);
		lock (_timelineGate)
		{
			if (_diagnosticReports.Remove(
				request, out SequencingDiagnostic[]? found))
			{
				diagnostics = found;
				return true;
			}
		}

		diagnostics = [];
		return false;
	}

	private IAudioOutputSource AttachTimeline(
		PlaybackRequest request,
		IAudioOutputSource source,
		SongScheduleCompilationResult compilation,
		ObjectId? sequenceId,
		bool repeat,
		SequencingContext context)
	{
		SequencingDiagnostic[] warnings = context.Diagnostics.Drain();
		if (warnings.Length != 0)
		{
			lock (_timelineGate)
				_diagnosticReports[request] = warnings;
		}

		PlaybackPositionTimelineEntry[] entries =
			compilation.PlaybackPositions
				.Select(position =>
					new PlaybackPositionTimelineEntry(
						position.Offset,
						new PlaybackPatternPosition(
							position.PatternId,
							position.PatternRow,
							sequenceId,
							position.SequenceEntryIndex)))
				.ToArray();

		if (entries.Length != 0
			&& compilation.Duration > TimeSpan.Zero)
		{
			PlaybackPositionTimeline timeline =
				new(
					entries,
					compilation.Duration,
					repeat);
			lock (_timelineGate)
				_positionTimelines[request] = timeline;
		}

		return source;
	}

	private IAudioOutputSource CreateFiniteSource(
		SongScheduleCompilationResult compilation,
		ISoundResolver resolver,
		string failureMessage)
	{
		ThrowIfFailed(
			compilation,
			failureMessage);
		return CreateSource(
			compilation.Schedule!,
			resolver);
	}

	private IAudioOutputSource CreateSource(
		NoteSchedule schedule,
		ISoundResolver resolver)
		=> new PlaybackSessionAudioSource(
			new PlaybackSession(
				new RenderContext(_configuration),
				schedule,
				resolver));

	private static void ThrowIfFailed(
		SongScheduleCompilationResult compilation,
		string failureMessage)
	{
		if (compilation.Success
			&& compilation.Schedule is not null)
		{
			return;
		}

		throw new PlaybackSourceCompilationException(
			failureMessage,
			compilation.Diagnostics);
	}

	private sealed class RepeatingPlaybackSource
		: IAudioOutputSource
	{
		private readonly RenderConfiguration _configuration;
		private readonly NoteSchedule _schedule;
		private readonly ISoundResolver _resolver;
		private readonly long _cycleFrames;

		private PlaybackSession _session;
		private long _cyclePosition;

		public RepeatingPlaybackSource(
			RenderConfiguration configuration,
			NoteSchedule schedule,
			ISoundResolver resolver,
			long cycleFrames)
		{
			_configuration =
				configuration
					?? throw new ArgumentNullException(nameof(configuration));
			_schedule =
				schedule
					?? throw new ArgumentNullException(nameof(schedule));
			_resolver =
				resolver
					?? throw new ArgumentNullException(nameof(resolver));
			if (cycleFrames < 0)
				throw new ArgumentOutOfRangeException(nameof(cycleFrames));

			_cycleFrames = cycleFrames;
			Format =
				new AudioOutputFormat(
					configuration.SampleRate,
					configuration.OutputChannelCount);
			_session = CreateSession();
		}

		public AudioOutputFormat Format { get; }

		public void Render(
			int frameCount,
			Span<float> destination)
		{
			if (frameCount < 0)
				throw new ArgumentOutOfRangeException(nameof(frameCount));

			int requiredSamples =
				checked(
					frameCount
						* Format.ChannelCount);
			if (destination.Length != requiredSamples)
			{
				throw new ArgumentException(
					"Destination length must exactly match frame count and channel count.",
					nameof(destination));
			}

			destination.Clear();
			if (frameCount == 0 || _cycleFrames == 0)
				return;

			int destinationFrame = 0;
			while (destinationFrame < frameCount)
			{
				if (_cyclePosition == _cycleFrames)
					RestartCycle();

				long remainingCycleFrames =
					_cycleFrames - _cyclePosition;
				int chunkFrames =
					(int)Math.Min(
						frameCount - destinationFrame,
						remainingCycleFrames);

				Span<float> chunk =
					destination.Slice(
						checked(
							destinationFrame
								* Format.ChannelCount),
						checked(
							chunkFrames
								* Format.ChannelCount));

				_session.Render(
					_session.NextFrame,
					chunkFrames,
					chunk);

				destinationFrame += chunkFrames;
				_cyclePosition += chunkFrames;
			}
		}

		private void RestartCycle()
		{
			_session = CreateSession();
			_cyclePosition = 0;
		}

		private PlaybackSession CreateSession()
			=> new(
				new RenderContext(_configuration),
				_schedule,
				_resolver);
	}
}
