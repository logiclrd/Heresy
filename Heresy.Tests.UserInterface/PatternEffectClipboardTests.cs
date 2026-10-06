using System;
using System.Collections.Generic;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Persistence;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternEffectClipboardTests
{
	[Test]
	public void ClipboardCodecRoundTripsMixedOrderedEffectStack()
	{
		PatternEffect[] effects =
		[
			new TonePortamentoPatternEffect(0x15),
			new SetResonantFilterPatternEffect(0.4, 0.7),
			new EmptyTrackerPatternEffect(0x22),
			new SetPlaybackOffsetPatternEffect(TimeSpan.FromSeconds(1.25)),
		];

		string text =
			PatternEffectClipboardCodec.Serialize(effects);
		PatternEffect[] decoded =
			PatternEffectClipboardCodec.Deserialize(text);

		decoded.Should().Equal(effects);
	}

	[Test]
	public void ClipboardCodecRejectsOrdinaryJson()
	{
		var action = () =>
			PatternEffectClipboardCodec.Deserialize(
				"{\"effects\":[]}");

		action.Should().Throw<ArgumentException>();
	}

	[Test]
	public void ClipboardCodecSupportsEmptyStackForClearingEffects()
	{
		string text =
			PatternEffectClipboardCodec.Serialize(
				Array.Empty<PatternEffect>());

		PatternEffectClipboardCodec.Deserialize(text)
			.Should().BeEmpty();
	}

	[Test]
	public void ReplaceAllPreservesNonEffectCellData()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Pattern",
				rowCount: 2,
				channelCount: 1);
		ObjectId sourceId = workspace.Document.AllocateObjectId();
		StartPatternNote note = new();
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = note;
		cell.SourceId = sourceId;
		cell.Volume = 0.5;
		cell.Effects.Add(new VibratoPatternEffect(0x22));
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.EffectCommand);

		bool changed =
			PatternEffectStackEditor.ReplaceAll(
				workspace,
				pattern,
				cursor,
				new PatternEffect[]
				{
					new SetSpeedPatternEffect(6),
					new SetPlaybackFrequencyPatternEffect(440.0),
				});

		changed.Should().BeTrue();
		cell.Note.Should().BeSameAs(note);
		cell.SourceId.Should().Be(sourceId);
		cell.Volume.Should().Be(0.5);
		cell.Effects.Should().Equal(
			new SetSpeedPatternEffect(6),
			new SetPlaybackFrequencyPatternEffect(440.0));
		cursor.IsExpanded.Should().BeTrue();
		cursor.ExpandedEffectIndex.Should().Be(0);
	}

	[Test]
	public void ReplaceAllWithEmptyStackClearsEffectsAndRemovesOtherwiseEmptyCell()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new SetSpeedPatternEffect(6));
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.EffectCommand);

		bool changed =
			PatternEffectStackEditor.ReplaceAll(
				workspace,
				pattern,
				cursor,
				Array.Empty<PatternEffect>());

		changed.Should().BeTrue();
		pattern.Grid[0, 0].Should().BeNull();
		cursor.IsExpanded.Should().BeFalse();
		cursor.Field.Should().Be(PatternCellField.EffectCommand);
	}

	[Test]
	public void ReplaceAllEqualStackIsNoOp()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new SetSpeedPatternEffect(6));
		cell.Effects.Add(new EmptyTrackerPatternEffect(0x12));
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.EffectCommand);
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;

		bool changed =
			PatternEffectStackEditor.ReplaceAll(
				workspace,
				pattern,
				cursor,
				new PatternEffect[]
				{
					new SetSpeedPatternEffect(6),
					new EmptyTrackerPatternEffect(0x12),
				});

		changed.Should().BeFalse();
		workspace.Document.DocumentRevision.Should().Be(documentRevision);
		workspace.Document.AudioRevision.Should().Be(audioRevision);
	}

	[Test]
	public void ReplacingOnlyInertTrackerSlotsDoesNotMarkAudio()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new EmptyTrackerPatternEffect(0x12));
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.EffectCommand);
		uint audioRevision = workspace.Document.AudioRevision;

		PatternEffectStackEditor.ReplaceAll(
			workspace,
			pattern,
			cursor,
			new PatternEffect[]
			{
				new EmptyTrackerPatternEffect(0x34),
			});

		workspace.Document.AudioRevision.Should().Be(audioRevision);
	}
}
