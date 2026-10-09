using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Scripting.Compilation;
using Heresy.Core.Samples;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Instruments;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;

namespace Heresy.Playback;

internal sealed class PlaybackSnapshotSoundResolver
	: ISoundResolver,
		IEnvelopeCurveResolver
{
	private readonly SongDocument _document;
	private readonly ISampleDataProvider _sampleDataProvider;
	private readonly Dictionary<(ObjectId, bool), ISound?> _sounds = [];
	private readonly Dictionary<ObjectId, IEnvelopeCurve?> _envelopes = [];
	private readonly ConcurrentDictionary<ObjectId, ISound> _preparedMixdowns = new();
	private uint _nextPreparedId = uint.MaxValue;

	/// <summary>
	/// Reserve an invocation-unique source identity before publishing its
	/// StartNote. This lookup is concurrency-safe on the audio consumer;
	/// prepared IDs never alias persisted document object identities.
	/// </summary>
	public ObjectId RegisterPreparedMixdown(ISound sound)
	{
		ArgumentNullException.ThrowIfNull(sound);
		while (_nextPreparedId != 0)
		{
			ObjectId id = new(_nextPreparedId--);
			if (!_document.Objects.ContainsKey(id)
				&& _preparedMixdowns.TryAdd(id, sound))
				return id;
		}
		throw new InvalidOperationException(
			"Prepared recursive mixdown exhausted the source ID space.");
	}

	public PlaybackSnapshotSoundResolver(
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

	/// <summary>
	/// Resolve direct PCM-producing sources and envelopes outside the audio
	/// callback. Experimental recursive playback may dynamically address any
	/// sound object, including sources not named in the root arrangement.
	/// Nested cooked Pattern/Sequence mixdowns are deliberately not compiled
	/// by this preload; the prepared adapter rejects those unsupported starts.
	/// </summary>
	public void PrepareDirectSources()
	{
		foreach (SongObject source in _document.Objects.Values)
			if (source is SampleDefinition or InstrumentDefinition or FmSynthDefinition)
				TryResolve(source.Id, mixdown: false, out _);

		foreach (SongObject source in _document.Objects.Values)
			if (source is EnvelopeDefinition)
				TryResolve(source.Id, out IEnvelopeCurve? _);
	}

	public bool TryResolve(
		ObjectId sourceId,
		bool mixdown,
		out ISound? sound)
	{
		if (_preparedMixdowns.TryGetValue(sourceId, out sound))
			return true;
		if (_sounds.TryGetValue(
				(sourceId, mixdown),
				out sound))
		{
			return sound is not null;
		}

		if (!_document.TryGet(
				sourceId,
				out SongObject? songObject)
			|| songObject is null)
		{
			_sounds[(sourceId, mixdown)] = null;
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

			case PatternDefinition when mixdown:
			case SequenceDefinition when mixdown:
				SongScheduleCompilationResult compilation =
					songObject is PatternDefinition
						? SongScheduleCompiler.CompilePattern(_document, sourceId)
						: SongScheduleCompiler.CompileSequence(_document, sourceId);
				if (!compilation.Success || compilation.Schedule is null)
				{
					throw new PlaybackSourceCompilationException(
						$"Could not compile nested sound source {sourceId.Value}.",
						compilation.Diagnostics);
				}
				sound = new CompiledNestedMixdownSound(
					sourceId,
					compilation.Schedule,
					compilation.Duration,
					this);
				break;

			default:
				sound = null;
				break;
		}

		_sounds[(sourceId, mixdown)] = sound;
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
