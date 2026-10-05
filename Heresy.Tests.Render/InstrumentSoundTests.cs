using System;
using System.Collections.Generic;
using System.Numerics;

using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.Instruments;
using Heresy.Render.Playback;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class InstrumentSoundTests
{
	[Test]
	public void MiddleCSelectsMappedToneAndComposesPitch()
	{
		ObjectId sourceId = (ObjectId)10U;
		ProbeSound source = new();
		TestSoundResolver sounds = new((sourceId, source));
		InstrumentDefinition definition = Instrument((ObjectId)1U);
		definition.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = sourceId,
			PitchMultiplier = 1.5,
		});
		definition.ToneTable.Add(0);

		InstrumentSound instrument = new(
			definition,
			sounds,
			new TestEnvelopeResolver());

		SoundInvocation? invocation =
			instrument.CreateInvocation(
				pitchMultiplier: 1.0,
				playbackSpeedMultiplier: 0.75);

		Assert.That(invocation, Is.Not.Null);
		Assert.That(invocation!.Sound, Is.SameAs(source));
		Assert.That(invocation.State.PitchMultiplier, Is.EqualTo(1.5));
		Assert.That(
			invocation.State.PlaybackSpeedMultiplier,
			Is.EqualTo(0.75));
	}

	[Test]
	public void OctaveUpSelectsToneTwelveWithTwelveDivisions()
	{
		ObjectId middleSourceId = (ObjectId)10U;
		ObjectId octaveSourceId = (ObjectId)11U;
		ProbeSound middle = new();
		ProbeSound octave = new();
		TestSoundResolver sounds = new(
			(middleSourceId, middle),
			(octaveSourceId, octave));
		InstrumentDefinition definition = Instrument((ObjectId)1U);
		definition.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = middleSourceId,
		});
		definition.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = octaveSourceId,
		});
		for (int i = 0; i <= 12; i++)
			definition.ToneTable.Add(-1);
		definition.ToneTable[0] = 0;
		definition.ToneTable[12] = 1;

		InstrumentSound instrument = new(
			definition,
			sounds,
			new TestEnvelopeResolver());

		SoundInvocation? invocation =
			instrument.CreateInvocation(
				pitchMultiplier: 2.0,
				playbackSpeedMultiplier: 1.0);

		Assert.That(invocation, Is.Not.Null);
		Assert.That(invocation!.Sound, Is.SameAs(octave));
		Assert.That(invocation.State.PitchMultiplier, Is.EqualTo(2.0));
	}

	[Test]
	public void OffsetMovesMiddleCToRequestedToneTableIndex()
	{
		ObjectId sourceId = (ObjectId)10U;
		ProbeSound source = new();
		InstrumentDefinition definition = Instrument((ObjectId)1U);
		definition.Offset = 3;
		definition.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = sourceId,
		});
		definition.ToneTable.AddRange(new[] { -1, -1, -1, 0 });

		InstrumentSound instrument = new(
			definition,
			new TestSoundResolver((sourceId, source)),
			new TestEnvelopeResolver());

		SoundInvocation? invocation =
			instrument.CreateInvocation(1.0, 1.0);

		Assert.That(invocation, Is.Not.Null);
		Assert.That(invocation!.Sound, Is.SameAs(source));
	}

	[Test]
	public void MinusOneAndOutOfRangeToneMappingsAreSilent()
	{
		ObjectId sourceId = (ObjectId)10U;
		InstrumentDefinition definition = Instrument((ObjectId)1U);
		definition.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = sourceId,
		});
		definition.ToneTable.Add(-1);

		InstrumentSound instrument = new(
			definition,
			new TestSoundResolver((sourceId, new ProbeSound())),
			new TestEnvelopeResolver());

		Assert.That(
			instrument.CreateInvocation(1.0, 1.0),
			Is.Null);
		Assert.That(
			instrument.CreateInvocation(2.0, 1.0),
			Is.Null);
	}

	[Test]
	public void NestedInstrumentReceivesComposedPitchBeforeItsToneLookup()
	{
		ObjectId nestedId = (ObjectId)2U;
		ObjectId lowSourceId = (ObjectId)10U;
		ObjectId highSourceId = (ObjectId)11U;
		ProbeSound low = new();
		ProbeSound high = new();
		TestSoundResolver sounds = new(
			(lowSourceId, low),
			(highSourceId, high));

		InstrumentDefinition nestedDefinition = Instrument(nestedId);
		nestedDefinition.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = lowSourceId,
		});
		nestedDefinition.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = highSourceId,
		});
		for (int i = 0; i <= 12; i++)
			nestedDefinition.ToneTable.Add(-1);
		nestedDefinition.ToneTable[0] = 0;
		nestedDefinition.ToneTable[12] = 1;

		InstrumentSound nested = new(
			nestedDefinition,
			sounds,
			new TestEnvelopeResolver());
		sounds.Add(nestedId, nested);

		InstrumentDefinition outerDefinition = Instrument((ObjectId)1U);
		outerDefinition.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = nestedId,
			PitchMultiplier = 2.0,
		});
		outerDefinition.ToneTable.Add(0);

		InstrumentSound outer = new(
			outerDefinition,
			sounds,
			new TestEnvelopeResolver());

		SoundInvocation? invocation =
			outer.CreateInvocation(1.0, 1.0);

		Assert.That(invocation, Is.Not.Null);
		Assert.That(invocation!.Sound, Is.SameAs(high));
		Assert.That(invocation.State.PitchMultiplier, Is.EqualTo(2.0));
	}

	[Test]
	public void ChildNotePolicyIsPreservedThroughInstrumentBinding()
	{
		ObjectId sourceId = (ObjectId)10U;
		ProbeSound source = new(NewNotePolicy.Continue);
		InstrumentDefinition definition = Instrument((ObjectId)1U);
		definition.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = sourceId,
		});
		definition.ToneTable.Add(0);

		InstrumentSound instrument = new(
			definition,
			new TestSoundResolver((sourceId, source)),
			new TestEnvelopeResolver());

		SoundInvocation? invocation =
			instrument.CreateInvocation(1.0, 1.0);

		Assert.That(
			invocation!.Configuration.NewNotePolicy,
			Is.EqualTo(NewNotePolicy.Continue));
	}

	[Test]
	public void ToneVolumeEnvelopeOverridesChildEnvelopeAndReachesPcm()
	{
		ObjectId instrumentId = (ObjectId)1U;
		ObjectId sourceId = (ObjectId)10U;
		ObjectId childEnvelopeId = (ObjectId)20U;
		ObjectId toneEnvelopeId = (ObjectId)21U;

		ConstantCurve childEnvelope = new(0.75);
		ConstantCurve toneEnvelope = new(0.25);
		ProbeSound source = new(
			NewNotePolicy.Cut,
			new EnvelopeConfigurationSnapshot(
				Volume: childEnvelope),
			value: 1.0f);

		TestSoundResolver sounds = new((sourceId, source));
		TestEnvelopeResolver envelopes = new(
			(childEnvelopeId, childEnvelope),
			(toneEnvelopeId, toneEnvelope));

		InstrumentDefinition definition = Instrument(instrumentId);
		definition.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = sourceId,
			VolumeEnvelopeId = toneEnvelopeId,
		});
		definition.ToneTable.Add(0);

		InstrumentSound instrument = new(
			definition,
			sounds,
			envelopes);
		sounds.Add(instrumentId, instrument);

		PlaybackSession session = Session(
			1,
			Schedule(
				Event(
					TimeSpan.Zero,
					new StartNoteCommand(instrumentId))),
			sounds);
		float[] output = new float[1];

		session.Render(0, 1, output);

		Assert.That(output[0], Is.EqualTo(0.25f).Within(1e-6));
	}

	[Test]
	public void UnspecifiedToneEnvelopeLeavesChildEnvelopeIntact()
	{
		ObjectId sourceId = (ObjectId)10U;
		ConstantCurve childEnvelope = new(0.75);
		ProbeSound source = new(
			NewNotePolicy.Cut,
			new EnvelopeConfigurationSnapshot(
				Volume: childEnvelope));

		InstrumentDefinition definition = Instrument((ObjectId)1U);
		definition.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = sourceId,
		});
		definition.ToneTable.Add(0);

		InstrumentSound instrument = new(
			definition,
			new TestSoundResolver((sourceId, source)),
			new TestEnvelopeResolver());

		SoundInvocation? invocation =
			instrument.CreateInvocation(1.0, 1.0);

		Assert.That(
			invocation!.Configuration.Envelopes.Volume,
			Is.SameAs(childEnvelope));
	}

	private static InstrumentDefinition Instrument(ObjectId id)
		=> new(id, "Instrument")
		{
			Divisions = 12.0,
			Offset = 0,
		};

	private static PlaybackSession Session(
		int sampleRate,
		NoteSchedule schedule,
		ISoundResolver resolver)
		=> new(
			new RenderContext(
				new RenderConfiguration(
					sampleRate,
					new[]
					{
						new OutputChannelConfiguration(
							Vector3.Zero,
							positionalImportance: 0.0),
					})),
			schedule,
			resolver);

	private static NoteSchedule Schedule(params NoteEvent[] events)
	{
		NoteScheduleBuilder builder = new();
		foreach (NoteEvent noteEvent in events)
			builder.Append(noteEvent);
		return builder.Freeze();
	}

	private static NoteEvent Event(
		TimeSpan time,
		params NoteCommand[] commands)
		=> new(
			new MusicalTime(time, 0.0),
			ChannelTarget.Physical(0),
			commands);

	private sealed class ConstantCurve : IEnvelopeCurve
	{
		private readonly double _value;

		public ConstantCurve(double value)
			=> _value = value;

		public double GetValue(
			long activeFrame,
			int sampleRate,
			long? noteOffActiveFrame)
			=> _value;
	}

	private sealed class ProbeSound : ISound
	{
		private readonly NewNotePolicy _policy;
		private readonly EnvelopeConfigurationSnapshot _envelopes;
		private readonly float _value;

		public ProbeSound(
			NewNotePolicy? policy = null,
			EnvelopeConfigurationSnapshot? envelopes = null,
			float value = 0.0f)
		{
			_policy = policy ?? NewNotePolicy.Cut;
			_envelopes =
				envelopes ?? EnvelopeConfigurationSnapshot.Empty;
			_value = value;
		}

		public NoteConfigurationSnapshot SnapshotNoteConfiguration()
			=> new(
				_policy,
				envelopes: _envelopes);

		public SoundState CreateState()
			=> new ProbeState();

		public long? GetEndFrameExclusive(
			RenderContext context,
			SoundState state)
			=> null;

		public void Render(
			RenderContext context,
			SoundState state,
			long startFrame,
			int frameCount,
			Span<float> destination)
		{
			for (int i = 0; i < destination.Length; i++)
				destination[i] += _value;
		}
	}

	private sealed class ProbeState : SoundState
	{
	}

	private sealed class TestSoundResolver : ISoundResolver
	{
		private readonly Dictionary<ObjectId, ISound> _sounds = [];

		public TestSoundResolver(
			params (ObjectId Id, ISound Sound)[] sounds)
		{
			foreach ((ObjectId id, ISound sound) in sounds)
				_sounds.Add(id, sound);
		}

		public void Add(ObjectId id, ISound sound)
			=> _sounds.Add(id, sound);

		public bool TryResolve(
			ObjectId sourceId,
			bool mixdown,
			out ISound? sound)
			=> _sounds.TryGetValue(sourceId, out sound);
	}

	private sealed class TestEnvelopeResolver : IEnvelopeCurveResolver
	{
		private readonly Dictionary<ObjectId, IEnvelopeCurve> _curves = [];

		public TestEnvelopeResolver(
			params (ObjectId Id, IEnvelopeCurve Curve)[] curves)
		{
			foreach ((ObjectId id, IEnvelopeCurve curve) in curves)
				_curves.Add(id, curve);
		}

		public bool TryResolve(
			ObjectId envelopeId,
			out IEnvelopeCurve? curve)
			=> _curves.TryGetValue(envelopeId, out curve);
	}
}
