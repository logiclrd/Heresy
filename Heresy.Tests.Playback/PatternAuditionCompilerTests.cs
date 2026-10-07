using System;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Playback;
using Heresy.Render.Realtime;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class PatternAuditionCompilerTests
{
	[Test]
	public void NoteAuditionUsesRememberedSourceFromEarlierRow()
	{
		ObjectId sampleId = (ObjectId)17U;
		DataPatternDefinition pattern =
			new((ObjectId)1U, "Pattern")
			{
				RowCount = 2,
				ChannelCount = 1,
			};
		pattern.Grid.GetOrCreateCell(0, 0).SourceId =
			sampleId;
		pattern.Grid.GetOrCreateCell(1, 0).Note =
			new StartPatternNote();

		NoteSchedule schedule =
			PatternAuditionCompiler.CompileNote(
				pattern,
				row: 1,
				channel: 0);

		schedule.Should().ContainSingle();
		schedule[0].Target.Should().Be(
			ChannelTarget.Physical(0));
		schedule[0].Offset.TimeOffset.Should().Be(TimeSpan.Zero);
		schedule[0].Commands.Should().ContainSingle()
			.Which.Should().Be(
				new StartNoteCommand(sampleId));
	}

	[Test]
	public void NoteAuditionUsesVolumeButDoesNotExecuteCellEffects()
	{
		ObjectId sampleId = (ObjectId)17U;
		DataPatternDefinition pattern =
			new((ObjectId)1U, "Pattern")
			{
				RowCount = 1,
				ChannelCount = 1,
			};
		PatternCell cell =
			pattern.Grid.GetOrCreateCell(0, 0);
		cell.SourceId = sampleId;
		cell.Note = new StartPatternNote();
		cell.Volume = 0.25;
		cell.Effects.Add(
			new SetPlaybackFrequencyPatternEffect(1234.0));

		NoteSchedule schedule =
			PatternAuditionCompiler.CompileNote(
				pattern,
				row: 0,
				channel: 0);

		schedule.Should().ContainSingle();
		schedule[0].Commands.Should().ContainSingle()
			.Which.Should().Be(
				new StartNoteCommand(
					sampleId,
					Volume: 0.25));
	}

	[Test]
	public void RowAuditionIncludesEveryChannelAtCurrentRow()
	{
		ObjectId firstSource = (ObjectId)11U;
		ObjectId secondSource = (ObjectId)12U;
		DataPatternDefinition pattern =
			new((ObjectId)1U, "Pattern")
			{
				RowCount = 2,
				ChannelCount = 2,
			};
		pattern.Grid.GetOrCreateCell(0, 0).SourceId =
			firstSource;
		pattern.Grid.GetOrCreateCell(0, 1).SourceId =
			secondSource;
		pattern.Grid.GetOrCreateCell(1, 0).Note =
			new StartPatternNote();
		pattern.Grid.GetOrCreateCell(1, 1).Note =
			new StartPatternNote(
				pitchMultiplier: 2.0);

		NoteSchedule schedule =
			PatternAuditionCompiler.CompileRow(
				pattern,
				row: 1);

		schedule.Should().HaveCount(2);
		schedule.Select(note => note.Target)
			.Should().BeEquivalentTo(
				new[]
				{
					ChannelTarget.Physical(0),
					ChannelTarget.Physical(1),
				});
		schedule.SelectMany(note => note.Commands)
			.OfType<StartNoteCommand>()
			.Select(command => command.SourceId)
			.Should().BeEquivalentTo(
				new[]
				{
					firstSource,
					secondSource,
				});
	}

	[Test]
	public void RowAuditionPrimesEffectMemoryFromEarlierRows()
	{
		DataPatternDefinition pattern =
			new((ObjectId)1U, "Pattern")
			{
				RowCount = 2,
				ChannelCount = 1,
			};
		pattern.Grid.GetOrCreateCell(0, 0)
			.Effects.Add(
				new VibratoPatternEffect(0x34));
		pattern.Grid.GetOrCreateCell(1, 0)
			.Effects.Add(
				new VibratoPatternEffect(0x00));

		NoteSchedule schedule =
			PatternAuditionCompiler.CompileRow(
				pattern,
				row: 1);

		SetVibratoCommand vibrato =
			schedule.SelectMany(note => note.Commands)
				.OfType<SetVibratoCommand>()
				.Should().ContainSingle()
				.Subject;
		vibrato.Speed.Should().Be(3);
		vibrato.Depth.Should().Be(4);
	}


	[Test]
	public void EnteredStartNoteTargetsPhysicalChannelAndReleasesPriorEditVoice()
	{
		ObjectId sourceId = (ObjectId)17U;
		DataPatternDefinition pattern =
			new((ObjectId)1U, "Pattern")
			{
				RowCount = 1,
				ChannelCount = 3,
			};
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 2);
		cell.SourceId = sourceId;
		cell.Note = new StartPatternNote();

		LivePlaybackEvent? liveEvent =
			PatternLiveEventCompiler.CompileEnteredNote(
				pattern,
				row: 0,
				channel: 2);

		liveEvent.Should().NotBeNull();
		liveEvent!.Target.Should().Be(
			ChannelTarget.Physical(2));
		liveEvent.Commands.Should().Equal(
			new NoteOffCommand(),
			new SetCurrentVoiceDisplacementActionCommand(
				NoteDisplacementAction.Cut),
			new StartNoteCommand(sourceId));
	}

	[Test]
	public void EnteredNoteOffDoesNotAddASecondRelease()
	{
		DataPatternDefinition pattern =
			new((ObjectId)1U, "Pattern")
			{
				RowCount = 1,
				ChannelCount = 1,
			};
		pattern.Grid.GetOrCreateCell(0, 0).Note =
			new PatternNoteOff();

		LivePlaybackEvent? liveEvent =
			PatternLiveEventCompiler.CompileEnteredNote(
				pattern,
				row: 0,
				channel: 0);

		liveEvent.Should().NotBeNull();
		liveEvent!.Commands.Should().ContainSingle()
			.Which.Should().BeOfType<NoteOffCommand>();
	}

	[Test]
	public void HeldPreviewTargetsRequestedVirtualChannel()
	{
		ObjectId sourceId = (ObjectId)17U;
		DataPatternDefinition pattern =
			new((ObjectId)1U, "Pattern")
			{
				RowCount = 1,
				ChannelCount = 1,
			};
		pattern.Grid.GetOrCreateCell(0, 0).SourceId =
			sourceId;

		LivePlaybackEvent? liveEvent =
			PatternLiveEventCompiler.CompileHeldPreviewStart(
				pattern,
				row: 0,
				channel: 0,
				virtualChannelId: 42,
				pitchMultiplier: 2.0);

		liveEvent.Should().NotBeNull();
		liveEvent!.Target.Should().Be(
			ChannelTarget.Virtual(42));
		liveEvent.Commands.Should().ContainSingle()
			.Which.Should().Be(
				new StartNoteCommand(
					sourceId,
					PitchMultiplier: 2.0));
	}


	[Test]
	public void AuditionRejectsCoordinatesOutsidePattern()
	{
		DataPatternDefinition pattern =
			new((ObjectId)1U, "Pattern")
			{
				RowCount = 2,
				ChannelCount = 2,
			};

		Action badRow = () =>
			PatternAuditionCompiler.CompileRow(
				pattern,
				row: 2);
		Action badChannel = () =>
			PatternAuditionCompiler.CompileNote(
				pattern,
				row: 0,
				channel: 2);

		badRow.Should().Throw<ArgumentOutOfRangeException>();
		badChannel.Should().Throw<ArgumentOutOfRangeException>();
	}
}
