using System;
using System.Collections.Generic;

using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
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
	private readonly Dictionary<ObjectId, ISound?> _sounds = [];
	private readonly Dictionary<ObjectId, IEnvelopeCurve?> _envelopes = [];

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
