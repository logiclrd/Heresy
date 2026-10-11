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
		NodeEnvelopeDefinition envelope =
			new(envelopeId, "Shared Amp");
		envelope.ReplaceCurve(
			[
				new(TimeSpan.Zero, 0.0),
				new(TimeSpan.FromMilliseconds(10), 1.0),
				new(TimeSpan.FromMilliseconds(30), 0.75),
				new(TimeSpan.FromMilliseconds(60), 0.0),
			],
			2, 2, EnvelopeSustainLoopMode.PingPong);
		sourceDocument.Add(envelope);

		ObjectId unusedEnvelopeId =
			sourceDocument.AllocateObjectId();
		sourceDocument.Add(
			new NodeEnvelopeDefinition(
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
							exponentialMultiplier: true,
							constant: true),
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

		NodeEnvelopeDefinition importedEnvelope =
			target.Document.Objects.Values
				.OfType<NodeEnvelopeDefinition>()
				.Should().ContainSingle()
				.Which;
		importedEnvelope.Id.Should().NotBe(envelopeId);
		importedEnvelope.Name.Should().Be("Shared Amp");
		importedEnvelope.Nodes.Should().Equal(envelope.Nodes);
		importedEnvelope.SustainStartIndex.Should().Be(2);
		importedEnvelope.SustainEndIndex.Should().Be(2);
		importedEnvelope.SustainLoopMode.Should().Be(EnvelopeSustainLoopMode.PingPong);

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
		importedFirst.Graph.Nodes.OfType<FmOscillatorNode>()
			.Single().Constant.Should().BeTrue();
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
			.OfType<NodeEnvelopeDefinition>()
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
			new NodeEnvelopeDefinition(
				firstEnvelopeId,
				"First Env"));

		ObjectId secondEnvelopeId =
			sourceDocument.AllocateObjectId();
		sourceDocument.Add(
			new NodeEnvelopeDefinition(
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
			.OfType<NodeEnvelopeDefinition>()
			.Select(item => item.Name)
			.Should().Equal("Second Env");
	}

	[Test]
	public void ImportRemapsSharedScriptEnvelopeWithoutConvertingItsType()
	{
		SongDocument source = new();
		ObjectId envelopeId = source.AllocateObjectId();
		ScriptEnvelopeDefinition expression = new(envelopeId, "Generated")
		{
			Source = "releaseSeconds < 0 ? 0.25 : 0.0",
			ReleaseDuration = TimeSpan.FromMilliseconds(90),
		};
		source.Add(expression);
		ObjectId synthId = source.AllocateObjectId();
		FmSynthDefinition synth = new(synthId, "FM",
			new FmSynthGraph([new FmEnvelopeNode(2, envelopeId)], 2));
		source.Add(synth);
		DocumentWorkspace target = new();
		for (int i = 0; i < 5; i++)
			target.Document.AllocateObjectId();
		FmSynthDefinition copied = FmSynthDocumentEditor.ImportFromSong(
			target,
			new SongFmSynthImportSource("source.hm.json", source, [synth]),
			[synthId]).Single();
		ObjectId copiedEnvelopeId = copied.Graph.Nodes.OfType<FmEnvelopeNode>()
			.Single().EnvelopeId;
		copiedEnvelopeId.Should().NotBe(envelopeId);
		ScriptEnvelopeDefinition copy = target.Document.Objects[copiedEnvelopeId]
			.Should().BeOfType<ScriptEnvelopeDefinition>().Subject;
		copy.Source.Should().Be(expression.Source);
		copy.ReleaseDuration.Should().Be(expression.ReleaseDuration);
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
