using System;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Scripting.Compilation;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class PlaybackPositionCompilationTests
{
	[Test]
	public void StandalonePatternTimelineUsesResolvedRowTimingFromRequestedStartRow()
	{
		SongDocument document = new();
		ObjectId patternId = document.AllocateObjectId();
		DataPatternDefinition pattern =
			new(patternId, "Pattern")
			{
				RowCount = 4,
				ChannelCount = 1,
			};
		pattern.Grid.GetOrCreateCell(2, 0)
			.Effects.Add(new SetSpeedPatternEffect(3));
		document.Add(pattern);

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompilePattern(
				document,
				patternId,
				startRow: 1);

		result.Success.Should().BeTrue();
		result.PlaybackPositions.Should().Equal(
			new CompiledPatternPlaybackPosition(
				TimeSpan.Zero,
				patternId,
				1,
				null),
			new CompiledPatternPlaybackPosition(
				TimeSpan.FromMilliseconds(120),
				patternId,
				2,
				null),
			new CompiledPatternPlaybackPosition(
				TimeSpan.FromMilliseconds(180),
				patternId,
				3,
				null));
		result.Duration.Should().Be(
			TimeSpan.FromMilliseconds(240));
	}

	[Test]
	public void DataSequenceTimelineIdentifiesRepeatedPatternOccurrence()
	{
		SongDocument document = new();
		ObjectId patternId = document.AllocateObjectId();
		DataPatternDefinition pattern =
			new(patternId, "Shared")
			{
				RowCount = 3,
				ChannelCount = 1,
			};
		document.Add(pattern);

		ObjectId sequenceId = document.AllocateObjectId();
		DataSequenceDefinition sequence =
			new(sequenceId, "Sequence");
		sequence.Entries.Add(
			new SequenceEntry(patternId));
		sequence.Entries.Add(
			new SequenceEntry(
				patternId,
				startRow: 1));
		document.Add(sequence);

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompileSequence(
				document,
				sequenceId);

		result.Success.Should().BeTrue();
		result.PlaybackPositions.Select(position =>
				(position.Offset, position.PatternId, position.PatternRow, position.SequenceEntryIndex))
			.Should().Equal(
				(TimeSpan.Zero, patternId, 0, (int?)0),
				(TimeSpan.FromMilliseconds(120), patternId, 1, (int?)0),
				(TimeSpan.FromMilliseconds(240), patternId, 2, (int?)0),
				(TimeSpan.FromMilliseconds(360), patternId, 1, (int?)1),
				(TimeSpan.FromMilliseconds(480), patternId, 2, (int?)1));
		result.Duration.Should().Be(
			TimeSpan.FromMilliseconds(600));
	}

	[Test]
	public void ScriptSequenceTimelineTracksGeneratedPatternEntries()
	{
		SongDocument document = new();
		ObjectId patternId = document.AllocateObjectId();
		DataPatternDefinition pattern =
			new(patternId, "Pattern")
			{
				RowCount = 3,
				ChannelCount = 1,
			};
		document.Add(pattern);

		ObjectId sequenceId = document.AllocateObjectId();
		document.Add(
			new ScriptSequenceDefinition(
				sequenceId,
				"Script")
			{
				Source =
					$"Play(_O({patternId.Value}), 1); "
						+ $"Play(_O({patternId.Value}), 2);",
			});

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompileSequence(
				document,
				sequenceId);

		result.Success.Should().BeTrue();
		result.PlaybackPositions.Select(position =>
				(position.Offset, position.PatternId, position.PatternRow, position.SequenceEntryIndex))
			.Should().Equal(
				(TimeSpan.Zero, patternId, 1, (int?)0),
				(TimeSpan.FromMilliseconds(120), patternId, 2, (int?)0),
				(TimeSpan.FromMilliseconds(240), patternId, 2, (int?)1));
	}

	[Test]
	public void PatternLoopTimelineRevisitsTheActualSourceRows()
	{
		SongDocument document = new();
		ObjectId patternId = document.AllocateObjectId();
		DataPatternDefinition pattern =
			new(patternId, "Loop")
			{
				RowCount = 3,
				ChannelCount = 1,
			};
		pattern.Grid.GetOrCreateCell(0, 0)
			.Effects.Add(new TrackerPatternLoopPatternEffect(0));
		pattern.Grid.GetOrCreateCell(1, 0)
			.Effects.Add(new TrackerPatternLoopPatternEffect(2));
		document.Add(pattern);

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompilePattern(
				document,
				patternId);

		result.Success.Should().BeTrue();
		result.PlaybackPositions.Select(position =>
				position.PatternRow)
			.Should().Equal(
				0,
				1,
				0,
				1,
				0,
				1,
				2);
	}
}
