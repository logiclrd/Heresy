using System;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Playback;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class HeldNotePreviewCompilerTests
{
	[Test]
	public void PreviewUsesRememberedSourceAndRequestedPitch()
	{
		ObjectId sourceId = (ObjectId)17U;
		DataPatternDefinition pattern =
			new((ObjectId)1U, "Pattern")
			{
				RowCount = 2,
				ChannelCount = 1,
			};
		pattern.Grid.GetOrCreateCell(0, 0).SourceId =
			sourceId;

		StartNoteCommand? command =
			PatternAuditionCompiler.CompileHeldNoteStart(
				pattern,
				row: 1,
				channel: 0,
				pitchMultiplier: 2.0);

		command.Should().Be(
			new StartNoteCommand(
				sourceId,
				PitchMultiplier: 2.0));
	}

	[Test]
	public void PreviewPreservesCurrentCellSourceVolumeSpeedAndMixdown()
	{
		ObjectId sourceId = (ObjectId)21U;
		DataPatternDefinition pattern =
			new((ObjectId)1U, "Pattern")
			{
				RowCount = 1,
				ChannelCount = 1,
			};
		PatternCell cell =
			pattern.Grid.GetOrCreateCell(0, 0);
		cell.SourceId = sourceId;
		cell.Volume = 0.25;
		cell.Note =
			new StartPatternNote(
				ObjectId.None,
				pitchMultiplier: 1.0,
				playbackSpeedMultiplier: 0.75,
				mixdown: true);

		StartNoteCommand? command =
			PatternAuditionCompiler.CompileHeldNoteStart(
				pattern,
				row: 0,
				channel: 0,
				pitchMultiplier: 1.5);

		command.Should().Be(
			new StartNoteCommand(
				sourceId,
				PitchMultiplier: 1.5,
				PlaybackSpeedMultiplier: 0.75,
				Mixdown: true,
				Volume: 0.25));
	}

	[Test]
	public void PreviewWithNoResolvableSourceIsNoOp()
	{
		DataPatternDefinition pattern =
			new((ObjectId)1U, "Pattern")
			{
				RowCount = 1,
				ChannelCount = 1,
			};

		PatternAuditionCompiler.CompileHeldNoteStart(
				pattern,
				row: 0,
				channel: 0,
				pitchMultiplier: 1.0)
			.Should().BeNull();
	}

	[Test]
	public void PreviewDoesNotExecuteCurrentCellEffects()
	{
		ObjectId sourceId = (ObjectId)17U;
		DataPatternDefinition pattern =
			new((ObjectId)1U, "Pattern")
			{
				RowCount = 1,
				ChannelCount = 1,
			};
		PatternCell cell =
			pattern.Grid.GetOrCreateCell(0, 0);
		cell.SourceId = sourceId;
		cell.Effects.Add(
			new SetPlaybackFrequencyPatternEffect(123.0));

		StartNoteCommand? command =
			PatternAuditionCompiler.CompileHeldNoteStart(
				pattern,
				row: 0,
				channel: 0,
				pitchMultiplier: 1.25);

		command.Should().Be(
			new StartNoteCommand(
				sourceId,
				PitchMultiplier: 1.25));
	}
}
