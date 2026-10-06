using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

using Heresy.Core.Assets;
using Heresy.Core.Envelopes;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Persistence;
using Heresy.Core.Samples;
using Heresy.Core.Sequences;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class SongDocumentJsonTests
{
	private static readonly string JsonContextPath =
		Path.Combine(
			Path.GetTempPath(),
			"heresy-json-tests",
			"track.hm.json");

	[SetUp]
	public void CreateReferencedSampleAsset()
	{
		string assetPath =
			Path.Combine(
				Path.GetDirectoryName(JsonContextPath)!,
				"assets",
				"piano-c4.wav");
		Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
		File.WriteAllText(assetPath, "fixture");
	}

	[OneTimeTearDown]
	public void RemoveReferencedSampleAsset()
	{
		string directory = Path.GetDirectoryName(JsonContextPath)!;
		if (Directory.Exists(directory))
			Directory.Delete(directory, recursive: true);
	}

	[Test]
	public void MixedDocumentRoundTripsAsFlatJson()
	{
		SongDocument document = BuildMixedDocument();

		string json = SongDocumentJson.Serialize(document, JsonContextPath);

		JsonObject root =
			JsonNode.Parse(json)!.AsObject();

		Assert.That(root["format"]!.GetValue<string>(), Is.EqualTo("Heresy"));
		Assert.That(root["version"]!.GetValue<int>(), Is.EqualTo(1));
		Assert.That(root["nextObjectId"]!.GetValue<uint>(), Is.EqualTo(8U));
		Assert.That(root["rootSequenceId"]!.GetValue<uint>(), Is.EqualTo(6U));

		JsonObject objects = root["objects"]!.AsObject();
		Assert.That(objects.Count, Is.EqualTo(7));
		Assert.That(objects.ContainsKey("1"), Is.True);
		Assert.That(objects.ContainsKey("7"), Is.True);
		Assert.That(
			objects["3"]!["type"]!.GetValue<string>(),
			Is.EqualTo("dataPattern"));

		// Cross-object references remain IDs rather than recursively embedding
		// the referenced objects.
		JsonArray tones =
			objects["2"]!["toneSpecifications"]!.AsArray();
		Assert.That(
			tones[0]!["sourceId"]!.GetValue<uint>(),
			Is.EqualTo(3U));
		Assert.That(
			tones[0]!.AsObject().ContainsKey("source"),
			Is.False);

		SongDocument restored =
			SongDocumentJson.Deserialize(json, JsonContextPath);

		AssertMixedDocument(restored);
		Assert.That(restored.DocumentRevision, Is.EqualTo(0));
		Assert.That(restored.AudioRevision, Is.EqualTo(0));
		Assert.That(
			restored.AllocateObjectId(),
			Is.EqualTo((ObjectId)8U));
	}

	[Test]
	public void SaveAndLoadRoundTripUtf8JsonFile()
	{
		SongDocument document = BuildMixedDocument();
		string path = Path.Combine(
			Path.GetTempPath(),
			$"heresy-{Guid.NewGuid():N}.hm.json");

		try
		{
			SongDocumentJson.Save(path, document);
			string text = File.ReadAllText(path);
			Assert.That(text, Does.StartWith("{"));
			Assert.That(text, Does.Contain("\"format\": \"Heresy\""));

			SongDocument restored =
				SongDocumentJson.Load(path);
			AssertMixedDocument(restored);
		}
		finally
		{
			if (File.Exists(path))
				File.Delete(path);
		}
	}

	[Test]
	public void EveryCurrentPatternEffectSubtypeRoundTrips()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		DataPatternDefinition pattern =
			new(id, "Effects")
			{
				RowCount = 1,
				ChannelCount = 1,
			};

		PatternEffect[] effects =
			typeof(PatternEffect).Assembly
				.GetTypes()
				.Where(type =>
					type.IsClass
					&& !type.IsAbstract
					&& typeof(PatternEffect).IsAssignableFrom(type))
				.OrderBy(type => type.FullName)
				.Select(CreatePatternEffect)
				.ToArray();

		pattern.Grid.GetOrCreateCell(0, 0)
			.Effects.AddRange(effects);
		document.Add(pattern);

		SongDocument restored =
			SongDocumentJson.Deserialize(
				SongDocumentJson.Serialize(document, JsonContextPath),
				JsonContextPath);

		DataPatternDefinition restoredPattern =
			(DataPatternDefinition)restored.Objects[id];
		PatternEffect[] restoredEffects =
			restoredPattern.Grid[0, 0]!.Effects.ToArray();

		Assert.That(
			restoredEffects.Select(effect => effect.GetType()),
			Is.EqualTo(effects.Select(effect => effect.GetType())));
		Assert.That(restoredEffects, Is.EqualTo(effects));
	}

	[Test]
	public void PatternNoteKindsRoundTrip()
	{
		SongDocument document = new();
		ObjectId patternId = document.AllocateObjectId();
		ObjectId sourceId = (ObjectId)1234U;
		DataPatternDefinition pattern =
			new(patternId, "Notes")
			{
				RowCount = 3,
				ChannelCount = 1,
			};
		PatternCell startCell = pattern.Grid.GetOrCreateCell(0, 0);
		startCell.SourceId = sourceId;
		startCell.Note =
			new StartPatternNote(
				pitchMultiplier: 2.0,
				playbackSpeedMultiplier: 0.5,
				mixdown: true);
		startCell.Volume = 0.75;
		pattern.Grid.GetOrCreateCell(1, 0).Note =
			new PatternNoteOff();
		pattern.Grid.GetOrCreateCell(2, 0).Note =
			new PatternNoteCut();
		document.Add(pattern);

		SongDocument restored =
			SongDocumentJson.Deserialize(
				SongDocumentJson.Serialize(document, JsonContextPath),
				JsonContextPath);

		DataPatternDefinition copy =
			(DataPatternDefinition)restored.Objects[patternId];

		Assert.That(copy.Grid[0, 0]!.SourceId, Is.EqualTo(sourceId));
		Assert.That(
			copy.Grid[0, 0]!.Note,
			Is.EqualTo(
				new StartPatternNote(
					2.0,
					0.5,
					true)));
		Assert.That(copy.Grid[0, 0]!.Volume, Is.EqualTo(0.75));
		Assert.That(copy.Grid[1, 0]!.Note, Is.TypeOf<PatternNoteOff>());
		Assert.That(copy.Grid[2, 0]!.Note, Is.TypeOf<PatternNoteCut>());
	}

	[Test]
	public void ReferencedTombstonesPersistAndUnreferencedTombstonesDrop()
	{
		SongDocument document = new();

		ObjectId referencedId = document.AllocateObjectId();
		document.Add(
			new SampleDefinition(
				referencedId,
				"Deleted Kick",
				new ExternalAssetReference("kick.wav")));
		Assert.That(document.Remove(referencedId), Is.True);

		ObjectId unreferencedId = document.AllocateObjectId();
		document.Add(
			new SampleDefinition(
				unreferencedId,
				"Deleted Snare",
				new ExternalAssetReference("snare.wav")));
		Assert.That(document.Remove(unreferencedId), Is.True);

		ObjectId patternId = document.AllocateObjectId();
		DataPatternDefinition pattern =
			new(patternId, "Broken Reference")
			{
				RowCount = 1,
				ChannelCount = 1,
			};
		pattern.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(referencedId);
		document.Add(pattern);

		// Advance the allocator beyond IDs which will no longer appear in either
		// objects or tombstones after serialization.
		ObjectId historicalId = document.AllocateObjectId();
		Assert.That(historicalId, Is.EqualTo((ObjectId)4U));

		string json = SongDocumentJson.Serialize(document, JsonContextPath);
		JsonObject tombstones =
			JsonNode.Parse(json)!["tombstones"]!.AsObject();

		Assert.That(tombstones.ContainsKey("1"), Is.True);
		Assert.That(tombstones.ContainsKey("2"), Is.False);
		Assert.That(
			tombstones["1"]!["lastKnownName"]!.GetValue<string>(),
			Is.EqualTo("Deleted Kick"));
		Assert.That(
			tombstones["1"]!["kind"]!.GetValue<string>(),
			Is.EqualTo("sample"));

		SongDocument restored =
			SongDocumentJson.Deserialize(json, JsonContextPath);

		Assert.That(restored.Tombstones.ContainsKey(referencedId), Is.True);
		Assert.That(restored.Tombstones.ContainsKey(unreferencedId), Is.False);
		Assert.That(
			restored.AllocateObjectId(),
			Is.EqualTo((ObjectId)5U));
	}

	[Test]
	public void TreeReferenceKeepsDeletedObjectTombstoneAlive()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		document.Add(
			new SampleDefinition(
				id,
				"Tree Sample",
				new ExternalAssetReference("tree.wav")));
		document.Remove(id);
		document.GetSectionRoot(SongTreeSection.Samples).Children.Add(
			new SongTreeObject("Tree Sample", id));

		SongDocument restored =
			SongDocumentJson.Deserialize(
				SongDocumentJson.Serialize(document, JsonContextPath),
				JsonContextPath);

		Assert.That(restored.Tombstones.ContainsKey(id), Is.True);
		Assert.That(
			((SongTreeObject)restored
				.GetSectionRoot(SongTreeSection.Samples)
				.Children.Single()).ObjectId,
			Is.EqualTo(id));
	}

	[Test]
	public void ScriptedObjectsConservativelyRetainTombstones()
	{
		SongDocument document = new();
		ObjectId deleted = document.AllocateObjectId();
		document.Add(
			new SampleDefinition(
				deleted,
				"Maybe Scripted",
				new ExternalAssetReference("maybe.wav")));
		document.Remove(deleted);

		ObjectId scriptId = document.AllocateObjectId();
		document.Add(
			new ScriptPatternDefinition(scriptId, "Script")
			{
				Source = "_O(1)",
			});

		SongDocument restored =
			SongDocumentJson.Deserialize(
				SongDocumentJson.Serialize(document, JsonContextPath),
				JsonContextPath);

		Assert.That(restored.Tombstones.ContainsKey(deleted), Is.True);
	}

	[Test]
	public void UnknownFormatVersionIsRejected()
	{
		const string json =
			"""
			{
			  "format": "Heresy",
			  "version": 999,
			  "nextObjectId": 1,
			  "rootSequenceId": 0,
			  "objects": {},
			  "tombstones": {},
			  "tree": { "type": "folder", "name": "Song", "children": [] }
			}
			""";

		Assert.That(
			() => SongDocumentJson.Deserialize(json, JsonContextPath),
			Throws.TypeOf<NotSupportedException>());
	}

	private static SongDocument BuildMixedDocument()
	{
		SongDocument document = new();

		ObjectId sampleId = document.AllocateObjectId();
		SampleDefinition sample =
			new(
				sampleId,
				"Piano",
				new ExternalAssetReference(
					Path.Combine(
						Path.GetDirectoryName(JsonContextPath)!,
						"assets",
						"piano-c4.wav"),
					"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"))
			{
				ReferenceFrequencyHz = 440.0,
				Loop = new SampleLoop(
					SampleLoopMode.PingPong,
					10,
					100),
			};
		sample.SourceChannelPositions.Add(new Vector3(-1, 0, 0));
		sample.SourceChannelPositions.Add(new Vector3(1, 0, 0));
		document.Add(sample);

		ObjectId instrumentId = document.AllocateObjectId();
		InstrumentDefinition instrument =
			new(instrumentId, "Recursive Instrument")
			{
				Divisions = 24.0,
				Offset = 3,
			};
		document.Add(instrument);

		ObjectId patternId = document.AllocateObjectId();
		DataPatternDefinition pattern =
			new(patternId, "Pattern")
			{
				RowCount = 4,
				ChannelCount = 2,
				MinorHighlightRows = 2,
				MajorHighlightRows = 4,
			};
		PatternCell cell = pattern.Grid.GetOrCreateCell(1, 0);
		cell.Note = new StartPatternNote(instrumentId, 2.0, 0.5, true);
		cell.Effects.Add(
			new TrackerVolumeColumnPanningPatternEffect(48));
		cell.Effects.Add(
			new TrackerEnvelopeControlPatternEffect(
				TrackerEnvelopeControlTarget.PitchOrFilter,
				enabled: false));
		document.Add(pattern);

		ObjectId envelopeId = document.AllocateObjectId();
		document.Add(
			new AdsrEnvelopeDefinition(envelopeId, "Envelope")
			{
				Attack = TimeSpan.FromMilliseconds(10),
				Decay = TimeSpan.FromMilliseconds(20),
				SustainLevel = 0.75,
				Release = TimeSpan.FromMilliseconds(30),
			});

		instrument.ToneSpecifications.Add(
			new ToneSpecification
			{
				SourceId = patternId,
				PitchMultiplier = 1.25,
				VolumeEnvelopeId = envelopeId,
				FilterEnvelopeId = envelopeId,
			});
		instrument.ToneTable.Add(0);
		instrument.ToneTable.Add(-1);

		ObjectId scriptPatternId = document.AllocateObjectId();
		document.Add(
			new ScriptPatternDefinition(scriptPatternId, "Script Pattern")
			{
				RowCount = 12,
				ChannelCount = 3,
				MinorHighlightRows = 3,
				MajorHighlightRows = 6,
				Source = "yield return something;",
			});

		ObjectId sequenceId = document.AllocateObjectId();
		DataSequenceDefinition sequence =
			new(sequenceId, "Sequence");
		sequence.Entries.Add(new SequenceEntry(patternId, 2));
		sequence.Entries.Add(new SequenceEntry(scriptPatternId));
		document.Add(sequence);

		ObjectId scriptSequenceId = document.AllocateObjectId();
		document.Add(
			new ScriptSequenceDefinition(
				scriptSequenceId,
				"Script Sequence")
			{
				Source = "yield return pattern;",
			});

		document.RootSequenceId = sequenceId;
		document.Root.Name = "Arrangement";

		SongTreeFolder samples =
			document.GetSectionRoot(SongTreeSection.Samples);
		SongTreeObject sampleNode =
			(SongTreeObject)samples.Children.Single();
		samples.Children.Clear();
		SongTreeFolder keys = new("Keys");
		keys.Children.Add(sampleNode);
		samples.Children.Add(keys);

		return document;
	}

	private static void AssertMixedDocument(
		SongDocument document)
	{
		Assert.That(document.Objects.Count, Is.EqualTo(7));
		Assert.That(document.RootSequenceId, Is.EqualTo((ObjectId)6U));
		Assert.That(document.Root.Name, Is.EqualTo("Arrangement"));

		SampleDefinition sample =
			(SampleDefinition)document.Objects[(ObjectId)1U];
		Assert.That(sample.Name, Is.EqualTo("Piano"));
		Assert.That(
			sample.Asset.FullPath,
			Is.EqualTo(
				Path.GetFullPath(
					Path.Combine(
						Path.GetDirectoryName(JsonContextPath)!,
						"assets",
						"piano-c4.wav"))));
		Assert.That(
			sample.Asset.Sha256,
			Is.EqualTo(
				"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));
		Assert.That(sample.ReferenceFrequencyHz, Is.EqualTo(440.0));
		Assert.That(
			sample.Loop,
			Is.EqualTo(
				new SampleLoop(
					SampleLoopMode.PingPong,
					10,
					100)));
		Assert.That(
			sample.SourceChannelPositions,
			Is.EqualTo(
				new[]
				{
					new Vector3(-1, 0, 0),
					new Vector3(1, 0, 0),
				}));

		InstrumentDefinition instrument =
			(InstrumentDefinition)document.Objects[(ObjectId)2U];
		Assert.That(instrument.Divisions, Is.EqualTo(24.0));
		Assert.That(instrument.Offset, Is.EqualTo(3));
		Assert.That(instrument.ToneTable, Is.EqualTo(new[] { 0, -1 }));
		Assert.That(
			instrument.ToneSpecifications.Single().SourceId,
			Is.EqualTo((ObjectId)3U));
		Assert.That(
			instrument.ToneSpecifications.Single().VolumeEnvelopeId,
			Is.EqualTo((ObjectId)4U));

		DataPatternDefinition pattern =
			(DataPatternDefinition)document.Objects[(ObjectId)3U];
		Assert.That(pattern.RowCount, Is.EqualTo(4));
		Assert.That(pattern.ChannelCount, Is.EqualTo(2));
		Assert.That(pattern.MinorHighlightRows, Is.EqualTo(2));
		Assert.That(pattern.MajorHighlightRows, Is.EqualTo(4));
		Assert.That(
			pattern.Grid[1, 0]!.Note,
			Is.EqualTo(
				new StartPatternNote(
					(ObjectId)2U,
					2.0,
					0.5,
					true)));
		Assert.That(
			pattern.Grid[1, 0]!.Effects,
			Is.EqualTo(
				new PatternEffect[]
				{
					new TrackerVolumeColumnPanningPatternEffect(48),
					new TrackerEnvelopeControlPatternEffect(
						TrackerEnvelopeControlTarget.PitchOrFilter,
						false),
				}));

		AdsrEnvelopeDefinition envelope =
			(AdsrEnvelopeDefinition)document.Objects[(ObjectId)4U];
		Assert.That(envelope.Attack, Is.EqualTo(TimeSpan.FromMilliseconds(10)));
		Assert.That(envelope.Decay, Is.EqualTo(TimeSpan.FromMilliseconds(20)));
		Assert.That(envelope.SustainLevel, Is.EqualTo(0.75));
		Assert.That(envelope.Release, Is.EqualTo(TimeSpan.FromMilliseconds(30)));

		ScriptPatternDefinition scriptPattern =
			(ScriptPatternDefinition)document.Objects[(ObjectId)5U];
		Assert.That(scriptPattern.Source, Is.EqualTo("yield return something;"));
		Assert.That(scriptPattern.RowCount, Is.EqualTo(12));
		Assert.That(scriptPattern.ChannelCount, Is.EqualTo(3));

		DataSequenceDefinition sequence =
			(DataSequenceDefinition)document.Objects[(ObjectId)6U];
		Assert.That(
			sequence.Entries,
			Is.EqualTo(
				new[]
				{
					new SequenceEntry((ObjectId)3U, 2),
					new SequenceEntry((ObjectId)5U),
				}));

		ScriptSequenceDefinition scriptSequence =
			(ScriptSequenceDefinition)document.Objects[(ObjectId)7U];
		Assert.That(
			scriptSequence.Source,
			Is.EqualTo("yield return pattern;"));

		Assert.That(document.Root.Children, Has.Count.EqualTo(4));
		Assert.That(
			document.Root.Children.Select(node => node.Name),
			Is.EqualTo(new[] { "Sequences", "Patterns", "Instruments", "Samples" }));

		SongTreeFolder samples =
			document.GetSectionRoot(SongTreeSection.Samples);
		SongTreeFolder keys =
			(SongTreeFolder)samples.Children.Single();
		Assert.That(keys.Name, Is.EqualTo("Keys"));
		Assert.That(
			((SongTreeObject)keys.Children.Single()).ObjectId,
			Is.EqualTo((ObjectId)1U));

		Assert.That(
			document.GetSectionRoot(SongTreeSection.Instruments)
				.Children
				.Cast<SongTreeObject>()
				.Select(node => node.ObjectId),
			Is.EqualTo(new[] { (ObjectId)2U, (ObjectId)4U }));
	}

	private static PatternEffect CreatePatternEffect(Type type)
	{
		ConstructorInfo constructor =
			type.GetConstructors()
				.OrderBy(ctor => ctor.GetParameters().Length)
				.First();

		object?[] arguments =
			constructor.GetParameters()
				.Select(parameter =>
					CreateConstructorArgument(
						parameter.ParameterType))
				.ToArray();

		return (PatternEffect)constructor.Invoke(arguments);
	}

	private static object CreateConstructorArgument(Type type)
	{
		if (type == typeof(byte))
			return (byte)1;
		if (type == typeof(int))
			return 1;
		if (type == typeof(double))
			return 0.5;
		if (type == typeof(bool))
			return true;
		if (type == typeof(TimeSpan))
			return TimeSpan.FromMilliseconds(10);
		if (type.IsEnum)
			return Enum.GetValues(type).GetValue(0)!;

		throw new InvalidOperationException(
			$"No generated test argument for {type.FullName}.");
	}
}
