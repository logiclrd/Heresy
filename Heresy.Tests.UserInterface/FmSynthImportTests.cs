using System;
using System.IO;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Persistence;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthImportTests
{
	[Test]
	public void LoadImportSourceEnumeratesFmSynthsInObjectIdOrder()
	{
		using TempProject project = new();
		string sourcePath = project.GetPath("source.hm.json");
		SongDocument document = new();
		FmSynthDefinition first =
			AddSynth(
				document,
				"First",
				new FmConstantNode(0, 0.25));
		FmSynthDefinition second =
			AddSynth(
				document,
				"Second",
				new FmConstantNode(0, 0.5));
		SongDocumentStorage.Save(
			sourcePath,
			document);

		SongFmSynthImportSource source =
			FmSynthDocumentEditor.LoadImportSource(
				sourcePath);

		source.Synths.Select(synth => synth.Id)
			.Should().Equal(
				first.Id,
				second.Id);
		source.Synths.Select(synth => synth.Name)
			.Should().Equal(
				"First",
				"Second");
	}

	[Test]
	public void ImportRemapsSharedEnvelopeDependencyAndPreservesEditorMetadata()
	{
		SongDocument sourceDocument = new();

		ObjectId envelopeId =
			sourceDocument.AllocateObjectId();
		AdsrEnvelopeDefinition envelope =
			new(
				envelopeId,
				"Shared Amp")
			{
				Attack = TimeSpan.FromMilliseconds(10),
				Decay = TimeSpan.FromMilliseconds(20),
				SustainLevel = 0.75,
				Release = TimeSpan.FromMilliseconds(30),
			};
		sourceDocument.Add(envelope);

		ObjectId unusedEnvelopeId =
			sourceDocument.AllocateObjectId();
		sourceDocument.Add(
			new AdsrEnvelopeDefinition(
				unusedEnvelopeId,
				"Unused"));

		ObjectId firstId =
			sourceDocument.AllocateObjectId();
		FmSynthDefinition first =
			new(
				firstId,
				"Carrier",
				new FmSynthGraph(
					[
						new FmEnvelopeNode(
							10,
							envelopeId),
						new FmOscillatorNode(
							11,
							FmOscillatorWaveform.Sine,
							frequencyHz: 440.0,
							multiplierNodeId: 10,
							exponentialMultiplier: true),
					],
					outputNodeId: 11));
		first.NodePositions.Add(
			new FmSynthNodePosition(
				10,
				100.0,
				120.0));
		first.NodePositions.Add(
			new FmSynthNodePosition(
				11,
				360.0,
				120.0));
		first.ConnectionRoutingHints.Add(
			new FmSynthConnectionRoutingHint(
				10,
				11,
				0,
				[
					new FmSynthRoutePoint(
						260.0,
						180.0),
				]));
		sourceDocument.Add(first);

		ObjectId secondId =
			sourceDocument.AllocateObjectId();
		FmSynthDefinition second =
			new(
				secondId,
				"Envelope Only",
				new FmSynthGraph(
					[
						new FmEnvelopeNode(
							20,
							envelopeId),
					],
					outputNodeId: 20));
		sourceDocument.Add(second);

		SongFmSynthImportSource source =
			new(
				"source.hm",
				sourceDocument,
				[first, second]);
		DocumentWorkspace target = new();
		for (int index = 0; index < 20; index++)
			target.Document.AllocateObjectId();

		var imported =
			FmSynthDocumentEditor.ImportFromSong(
				target,
				source,
				[first.Id, second.Id]);

		imported.Should().HaveCount(2);
		imported.Select(synth => synth.Name)
			.Should().Equal(
				"Carrier",
				"Envelope Only");
		imported.Select(synth => synth.Id)
			.Should().OnlyContain(id =>
				id != first.Id
					&& id != second.Id);

		AdsrEnvelopeDefinition importedEnvelope =
			target.Document.Objects.Values
				.OfType<AdsrEnvelopeDefinition>()
				.Should().ContainSingle()
				.Which;
		importedEnvelope.Id.Should().NotBe(envelopeId);
		importedEnvelope.Name.Should().Be("Shared Amp");
		importedEnvelope.Attack.Should()
			.Be(TimeSpan.FromMilliseconds(10));
		importedEnvelope.Decay.Should()
			.Be(TimeSpan.FromMilliseconds(20));
		importedEnvelope.SustainLevel.Should().Be(0.75);
		importedEnvelope.Release.Should()
			.Be(TimeSpan.FromMilliseconds(30));

		foreach (FmSynthDefinition synth in imported)
		{
			synth.Graph.Nodes
				.OfType<FmEnvelopeNode>()
				.Should().OnlyContain(node =>
					node.EnvelopeId
						== importedEnvelope.Id);
		}

		FmSynthDefinition importedFirst =
			imported.Single(synth =>
				synth.Name == "Carrier");
		importedFirst.NodePositions.Should()
			.Equal(first.NodePositions);
		importedFirst.ConnectionRoutingHints
			.Should().ContainSingle();
		FmSynthConnectionRoutingHint importedHint =
			importedFirst.ConnectionRoutingHints.Single();
		importedHint.SourceNodeId.Should().Be(10);
		importedHint.TargetNodeId.Should().Be(11);
		importedHint.TargetInputIndex.Should().Be(0);
		importedHint.RoutePoints.Should().Equal(
			new FmSynthRoutePoint(
				260.0,
				180.0));

		target.Document.Objects.Values
			.OfType<AdsrEnvelopeDefinition>()
			.Should().NotContain(item =>
				item.Name == "Unused");
	}

	[Test]
	public void ImportOnlyCopiesDependenciesOfSelectedSynths()
	{
		SongDocument sourceDocument = new();

		ObjectId firstEnvelopeId =
			sourceDocument.AllocateObjectId();
		sourceDocument.Add(
			new AdsrEnvelopeDefinition(
				firstEnvelopeId,
				"First Env"));

		ObjectId secondEnvelopeId =
			sourceDocument.AllocateObjectId();
		sourceDocument.Add(
			new AdsrEnvelopeDefinition(
				secondEnvelopeId,
				"Second Env"));

		FmSynthDefinition first =
			AddSynth(
				sourceDocument,
				"First",
				new FmEnvelopeNode(
					0,
					firstEnvelopeId));
		FmSynthDefinition second =
			AddSynth(
				sourceDocument,
				"Second",
				new FmEnvelopeNode(
					0,
					secondEnvelopeId));

		SongFmSynthImportSource source =
			new(
				"source.hm.json",
				sourceDocument,
				[first, second]);
		DocumentWorkspace target = new();

		var imported =
			FmSynthDocumentEditor.ImportFromSong(
				target,
				source,
				[second.Id]);

		imported.Should().ContainSingle()
			.Which.Name.Should().Be("Second");
		target.Document.Objects.Values
			.OfType<AdsrEnvelopeDefinition>()
			.Select(item => item.Name)
			.Should().Equal("Second Env");
	}

	[Test]
	public void MissingEnvelopeDependencyRejectsWholeBatchBeforeMutation()
	{
		SongDocument sourceDocument = new();
		ObjectId synthId =
			sourceDocument.AllocateObjectId();
		FmSynthDefinition synth =
			new(
				synthId,
				"Broken",
				new FmSynthGraph(
					[
						new FmEnvelopeNode(
							0,
							(ObjectId)999U),
					],
					outputNodeId: 0));
		sourceDocument.Add(synth);
		SongFmSynthImportSource source =
			new(
				"broken.hm",
				sourceDocument,
				[synth]);
		DocumentWorkspace target = new();

		Action action = () =>
			FmSynthDocumentEditor.ImportFromSong(
				target,
				source,
				[synth.Id]);

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*999*envelope*");
		target.Document.Objects.Should().BeEmpty();
		target.Document.DocumentRevision.Should().Be(0);
		target.Document.AudioRevision.Should().Be(0);
	}

	private static FmSynthDefinition AddSynth(
		SongDocument document,
		string name,
		FmSynthNode node)
	{
		ObjectId id =
			document.AllocateObjectId();
		FmSynthDefinition synth =
			new(
				id,
				name,
				new FmSynthGraph(
					[node],
					node.Id));
		document.Add(synth);
		return synth;
	}

	private sealed class TempProject : IDisposable
	{
		private readonly string _root =
			Path.Combine(
				Path.GetTempPath(),
				$"heresy-fm-import-{Guid.NewGuid():N}");

		public TempProject()
			=> Directory.CreateDirectory(_root);

		public string GetPath(
			params string[] parts)
		{
			string path = _root;
			foreach (string part in parts)
				path = Path.Combine(path, part);
			return path;
		}

		public void Dispose()
		{
			if (Directory.Exists(_root))
			{
				Directory.Delete(
					_root,
					recursive: true);
			}
		}
	}
}
