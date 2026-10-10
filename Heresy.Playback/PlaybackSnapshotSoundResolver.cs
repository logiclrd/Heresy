using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Samples;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Instruments;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;
using Heresy.Scripting.Compilation;
using Heresy.Scripting.Runtime;

namespace Heresy.Playback;

internal sealed class PlaybackSnapshotSoundResolver
	: ISoundResolver,
		IEnvelopeCurveResolver
{
	private readonly SongDocument _document;
	private readonly ISampleDataProvider _sampleDataProvider;
	private readonly Dictionary<(ObjectId, bool), ISound?> _sounds = [];
	private readonly Dictionary<ObjectId, IEnvelopeCurve?> _envelopes = [];
	private readonly Dictionary<ObjectId, IScriptInstrumentToneEvaluator> _instrumentPrograms = [];
	private readonly Dictionary<ObjectId, IScriptInstrumentPitchEvaluator> _pitchPrograms = [];
	private readonly Dictionary<string, Func<double, double, double>> _parametricPrograms = new(StringComparer.Ordinal);
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


	/// <summary>Retire an invocation-unique source identity after its
	/// final physical, virtual or displaced voice has detached.</summary>
	public bool UnregisterPreparedMixdown(ObjectId id)
		=> _preparedMixdowns.TryRemove(id, out _);

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
	/// Nested Pattern/Sequence sources are not eagerly compiled by this
	/// preload. Producer-prepared private mixdowns register their own transient
	/// sound identities; no legacy greedy resolver remains.
	/// </summary>
	public void PrepareDirectSources()
	{
		foreach (SongObject source in _document.Objects.Values)
			if (source is SampleDefinition or InstrumentDefinition
				or ScriptInstrumentDefinition or FmSynthDefinition)
				TryResolve(source.Id, mixdown: false, out _);

		// All data-grid expression sources must compile *before* any PCM work,
		// including unreachable patterns and private nested invocations.
		foreach (DataPatternDefinition pattern in _document.Objects.Values
			.OfType<DataPatternDefinition>())
			foreach ((_, _, PatternCell cell) in pattern.Grid.EnumerateNonEmptyCells())
				foreach (ParametricPatternEffect effect in cell.Effects
					.OfType<ParametricPatternEffect>())
					PrepareParametricEvaluator(effect.Expression);

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

			case ScriptInstrumentDefinition scripted:
				// Compile once during source preparation, never in SDL callbacks.
				GetInstrumentProgram(scripted);
				GetInstrumentPitchProgram(scripted);
				sound = new InstrumentSound(
					pitch => SelectTone(scripted, pitch), this, this);
				break;

			case FmSynthDefinition fmSynth:
				sound =
					new FmSynthSound(
						fmSynth.Graph,
						this);
				break;

			case PatternDefinition when mixdown:
			case SequenceDefinition when mixdown:
				throw new InvalidOperationException(
					"Recursive Pattern/Sequence mixdowns must be constructed by " +
					"the incremental invocation owner, not a compiled sound resolver.");

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
				ScriptEnvelopeDefinition scripted =>
					CompileScriptEnvelope(scripted),
				_ => null,
			};

		_envelopes[envelopeId] = curve;
		return curve is not null;
	}

	/// <summary>Shared pure selected-tone preflight for recursive binding.</summary>
	public ToneSpecification? SelectTone(
		SongObject definition, double pitchMultiplier)
	{
		if (!(pitchMultiplier > 0.0) || !double.IsFinite(pitchMultiplier))
			throw new ArgumentOutOfRangeException(nameof(pitchMultiplier));
		return definition switch
		{
			InstrumentDefinition table =>
				InstrumentSound.SelectTone(table, pitchMultiplier),
			ScriptInstrumentDefinition scripted =>
				SelectScriptTone(scripted, pitchMultiplier),
			_ => null,
		};
	}

	private ToneSpecification? SelectScriptTone(
		ScriptInstrumentDefinition definition, double pitchMultiplier)
	{
		int index = GetInstrumentProgram(definition)
			.SelectToneIndex(pitchMultiplier);
		if (index == -1)
			return null;
		if ((uint)index >= (uint)definition.ToneSpecifications.Count)
			throw new InvalidOperationException(
				$"Instrument '{definition.Name}' selected invalid tone index {index}.");
		ToneSpecification tone = definition.ToneSpecifications[index];
		double factor = GetInstrumentPitchProgram(definition)
			.GetPitchScale(pitchMultiplier);
		double ratio = tone.PitchMultiplier * factor;
		if (!(ratio > 0.0) || !double.IsFinite(ratio))
			throw new InvalidOperationException(
				$"Instrument '{definition.Name}' produced an invalid pitch multiplier.");
		return new ToneSpecification
		{
			SourceId = tone.SourceId,
			PitchMultiplier = ratio,
			VolumeEnvelopeId = tone.VolumeEnvelopeId,
			PitchEnvelopeId = tone.PitchEnvelopeId,
			PanningEnvelopeId = tone.PanningEnvelopeId,
			FilterEnvelopeId = tone.FilterEnvelopeId,
		};
	}

	private IScriptInstrumentPitchEvaluator GetInstrumentPitchProgram(
		ScriptInstrumentDefinition definition)
	{
		if (_pitchPrograms.TryGetValue(definition.Id,
			out IScriptInstrumentPitchEvaluator? program))
			return program;
		ScriptCompilationResult<IScriptInstrumentPitchEvaluator> compiled =
			ScriptCompiler.CompileInstrumentPitch(definition);
		if (!compiled.Success || compiled.Program is null)
			throw new InvalidOperationException(
				$"Instrument '{definition.Name}' pitch expression failed to compile: " +
				string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
		_pitchPrograms.Add(definition.Id, compiled.Program);
		return compiled.Program;
	}

	private IScriptInstrumentToneEvaluator GetInstrumentProgram(
		ScriptInstrumentDefinition definition)
	{
		if (_instrumentPrograms.TryGetValue(definition.Id,
			out IScriptInstrumentToneEvaluator? evaluator))
			return evaluator;
		ScriptCompilationResult<IScriptInstrumentToneEvaluator> compiled =
			ScriptCompiler.CompileInstrument(definition);
		if (!compiled.Success || compiled.Program is null)
			throw new InvalidOperationException(
				$"Instrument '{definition.Name}' failed to compile: " +
				string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
		_instrumentPrograms.Add(definition.Id, compiled.Program);
		return compiled.Program;
	}

	public Func<double, double, double> GetParametricEvaluator(string source)
		=> _parametricPrograms.TryGetValue(source,
			out Func<double, double, double>? result)
			? result
			: throw new InvalidOperationException(
				"An expression was not prepared before parametric playback.");

	private void PrepareParametricEvaluator(string source)
	{
		if (_parametricPrograms.ContainsKey(source))
			return;
		ScriptCompilationResult<IScriptParametricEvaluator> compiled =
			ScriptCompiler.CompileParametric(source);
		if (!compiled.Success || compiled.Program is null)
			throw new InvalidOperationException(
				"Parametric expression failed to compile: " +
				string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
		_parametricPrograms.Add(source, compiled.Program.Evaluate);
	}

	private static IEnvelopeCurve CompileScriptEnvelope(
		ScriptEnvelopeDefinition definition)
	{
		ScriptCompilationResult<IScriptEnvelopeEvaluator> compiled =
			ScriptCompiler.CompileEnvelope(definition);
		if (!compiled.Success || compiled.Program is null)
		{
			throw new InvalidOperationException(
				$"Envelope '{definition.Name}' failed to compile: " +
				string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
		}
		return new ScriptEnvelopeCurve(
			compiled.Program.Evaluate, definition.ReleaseDuration);
	}
}
