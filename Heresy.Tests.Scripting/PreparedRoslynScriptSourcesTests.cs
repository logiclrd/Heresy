using System;
using System.Collections.Generic;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Compilation;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class PreparedRoslynScriptSourcesTests
{
	[Test]
	public void PreparationCapturesSnapshotAndReusesCompiledCodeAcrossInvocations()
	{
		SongDocument authoring = new();
		ScriptSequenceDefinition sequence = new((ObjectId)10U, "Root")
		{
			Source = "return sequenceIndex == 0 ? Play(_O(11)) : null;",
		};
		ScriptPatternDefinition pattern = new((ObjectId)11U, "Child")
		{
			RowCount = 1,
			ChannelCount = 1,
			Source = "Cut(0, 0);",
		};
		authoring.Add(sequence);
		authoring.Add(pattern);
		SongDocumentSnapshot snapshot = SongDocumentSnapshot.Create(authoring);

		PreparedRoslynIncrementalScriptSources prepared = new(snapshot);

		// Changes to either the authoring document or the supplied snapshot
		// cannot alter what was prepared for this playback session.
		pattern.Source = "Off(0, 0);";
		sequence.Source = "return null;";
		pattern.RowCount = 9;
		ScriptPatternDefinition snapshotPattern =
			(ScriptPatternDefinition)snapshot.Document.Objects[pattern.Id];
		snapshotPattern.Source = "Off(0, 0);";
		snapshotPattern.RowCount = 9;

		prepared.TryResolve(pattern.Id, out SongObject? resolved).Should().BeTrue();
		resolved.Should().BeOfType<ScriptPatternDefinition>()
			.Which.RowCount.Should().Be(1);

		var firstPatternFactory = prepared.CompilePattern(
			(ScriptPatternDefinition)resolved!);
		var secondPatternFactory = prepared.CompilePattern(
			(ScriptPatternDefinition)resolved!);
		firstPatternFactory.Should().BeSameAs(secondPatternFactory);

		prepared.TryResolve(sequence.Id, out SongObject? sequenceSource)
			.Should().BeTrue();
		var sequenceFactory = prepared.CompileSequence(
			(ScriptSequenceDefinition)sequenceSource!);
		sequenceFactory.Should().BeSameAs(prepared.CompileSequence(
			(ScriptSequenceDefinition)sequenceSource!));

		foreach (int _ in Enumerable.Range(0, 2))
		{
			using IncrementalRecursiveTimeline timeline =
				prepared.CreateTimeline(new SequencingContext());
			timeline.AddRoot(sequence.Id);
			List<NoteEvent> events = [];
			while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
				if (step is IncrementalPatternTimelineStep.Emit emit)
					events.Add(emit.Note);

			events.Should().ContainSingle();
			events[0].Commands.Single().Should().BeOfType<NoteCutCommand>();
			timeline.Elapsed.Should().Be(TimeSpan.FromMilliseconds(120));
		}
	}

	[Test]
	public void InvalidScriptFailsDuringPreparationBeforePlaybackStarts()
	{
		SongDocument document = new();
		ScriptPatternDefinition invalid = new((ObjectId)21U, "Invalid")
		{
			Source = "System.IO.File.ReadAllText(\"forbidden\");",
		};
		document.Add(invalid);

		Action prepare = () => new PreparedRoslynIncrementalScriptSources(
			SongDocumentSnapshot.Create(document));
		prepare.Should().Throw<NotSupportedException>()
			.WithMessage("*HRS2001*");
	}

	[Test]
	public void ScriptedSequenceCreatesIndependentRngBoundLookupInstances()
	{
		SongDocument document = new();
		ScriptSequenceDefinition source = new((ObjectId)30U, "Random")
		{
			Source = "return sequenceIndex == 0 ? (Random() < 0.5 ? Play(_O(1)) : Play(_O(2))) : null;",
		};
		document.Add(source);
		PreparedRoslynIncrementalScriptSources prepared = new(
			SongDocumentSnapshot.Create(document));
		prepared.TryResolve(source.Id, out SongObject? resolved).Should().BeTrue();
		ISequenceEntrySourceFactory factory =
			prepared.CompileSequence((ScriptSequenceDefinition)resolved!);

		static ObjectId[] Pick(ISequenceEntrySourceFactory sourceFactory)
		{
			ISequenceEntryProvider provider = sourceFactory.Create(
				new SequencingContext(random: new DeterministicRandom(12345)));
			return Enumerable.Range(0, 4)
				.Select(i => provider.GetSequenceEntry(i, 0, i == 0 ? -1 : 0)!.PatternId)
				.ToArray();
		}

		Pick(factory).Should().Equal(Pick(factory));
	}
}
