using System;
using System.Collections.Generic;

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
	: IBackgroundPlaybackSourceFactory
{
	private readonly RenderConfiguration _configuration;
	private readonly ISampleDataProvider _sampleDataProvider;

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
		SnapshotSoundResolver resolver =
			new(
				document,
				_sampleDataProvider);

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
							start?.Row);

					return CreateFiniteSource(
						compilation,
						resolver,
						$"Could not compile sequence {sequence.SequenceId.Value} for playback.");
				}

			case PatternPlaybackRequest pattern:
				{
					SongScheduleCompilationResult compilation =
						SongScheduleCompiler.CompilePattern(
							document,
							pattern.PatternId,
							pattern.StartRow);

					ThrowIfFailed(
						compilation,
						$"Could not compile pattern {pattern.PatternId.Value} for playback.");

					if (!pattern.Repeat)
					return CreateSource(compilation.Schedule!, resolver);

					long cycleFrames =
						FrameTime.Ceiling(
							compilation.Duration,
							_configuration.SampleRate);
					return new RepeatingPlaybackSource(
						_configuration,
						compilation.Schedule!,
						resolver,
						cycleFrames);
				}

			case AdHocPlaybackRequest adHoc:
				return CreateSource(
					adHoc.Schedule,
					resolver);

			default:
				throw new NotSupportedException(
					$"Playback request type {request.GetType().FullName} is not supported.");
		}
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

	private sealed class SnapshotSoundResolver
		: ISoundResolver,
			IEnvelopeCurveResolver
	{
		private readonly SongDocument _document;
		private readonly ISampleDataProvider _sampleDataProvider;
		private readonly Dictionary<ObjectId, ISound?> _sounds = [];
		private readonly Dictionary<ObjectId, IEnvelopeCurve?> _envelopes = [];

		public SnapshotSoundResolver(
			SongDocument document,
			ISampleDataProvider sampleDataProvider)
		{
			_document =
				document
					?? throw new ArgumentNullException(nameof(document));
			_sampleDataProvider =
				sampleDataProvider
					?? throw new ArgumentNullException(nameof(sampleDataProvider));
		}

		public bool TryResolve(
			ObjectId sourceId,
			bool mixdown,
			out ISound? sound)
		{
			_ = mixdown;

			if (_sounds.TryGetValue(
					sourceId,
					out sound))
			{
				return sound is not null;
			}

			if (!_document.TryGet(
					sourceId,
					out SongObject? songObject)
				|| songObject is null)
			{
				_sounds[sourceId] = null;
				sound = null;
				return false;
			}

			switch (songObject)
			{
				case SampleDefinition sample:
					sound =
						new SampleSound(
							sample,
							_sampleDataProvider.GetSampleData(sample));
					break;

				case InstrumentDefinition instrument:
					sound =
						new InstrumentSound(
							instrument,
							this,
							this);
					break;

				case FmSynthDefinition fmSynth:
					sound =
						new FmSynthSound(
							fmSynth.Graph,
							this);
					break;

				default:
					sound = null;
					break;
			}

			_sounds[sourceId] = sound;
			return sound is not null;
		}

		public bool TryResolve(
			ObjectId envelopeId,
			out IEnvelopeCurve? curve)
		{
			if (_envelopes.TryGetValue(
					envelopeId,
					out curve))
			{
				return curve is not null;
			}

			if (!_document.TryGet(
					envelopeId,
					out SongObject? songObject)
				|| songObject is not EnvelopeDefinition envelope)
			{
				_envelopes[envelopeId] = null;
				curve = null;
				return false;
			}

			curve =
				envelope switch
				{
					AdsrEnvelopeDefinition adsr =>
						new AdsrEnvelopeCurve(adsr),
					_ => null,
				};

			_envelopes[envelopeId] = curve;
			return curve is not null;
		}
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
