using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Playback;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class PlaybackStartResolverTests
{
	[Test]
	public void PreferredContainingSequenceWinsEvenWhenItIsNotRoot()
	{
		SongDocument document = new();
		ObjectId patternId = AddPattern(document);
		ObjectId rootId = AddSequence(document, patternId);
		ObjectId preferredId = AddSequence(document, patternId);
		document.RootSequenceId = rootId;

		PlaybackStartLocation result =
			PlaybackStartResolver.ResolveFromPattern(
				document,
				patternId,
				row: 7,
				preferredId,
				preferredOrder: 0);

		result.Should().Be(
			new SequencePlaybackStartLocation(
				preferredId,
				0,
				7));
	}

	[Test]
	public void RootSequenceIsPreferredForStandalonePatternView()
	{
		SongDocument document = new();
		ObjectId patternId = AddPattern(document);
		_ = AddSequence(document, patternId);
		ObjectId rootId = AddSequence(document, patternId);
		document.RootSequenceId = rootId;

		PlaybackStartResolver.ResolveFromPattern(
				document,
				patternId,
				row: 3)
			.Should().Be(
				new SequencePlaybackStartLocation(
					rootId,
					0,
					3));
	}

	[Test]
	public void UnanchoredContainingSequenceIsUsedWhenRootDoesNotContainPattern()
	{
		SongDocument document = new();
		ObjectId patternId = AddPattern(document);
		ObjectId otherPatternId = AddPattern(document);
		ObjectId containingId = AddSequence(document, patternId);
		ObjectId rootId = AddSequence(document, otherPatternId);
		document.RootSequenceId = rootId;

		PlaybackStartResolver.ResolveFromPattern(
				document,
				patternId,
				row: 5)
			.Should().Be(
				new SequencePlaybackStartLocation(
					containingId,
					0,
					5));
	}

	[Test]
	public void PatternItselfIsFallbackWhenNoSequenceContainsIt()
	{
		SongDocument document = new();
		ObjectId patternId = AddPattern(document);

		PlaybackStartResolver.ResolveFromPattern(
				document,
				patternId,
				row: 9)
			.Should().Be(
				new PatternPlaybackStartLocation(
					patternId,
					9));
	}

	private static ObjectId AddPattern(
		SongDocument document)
	{
		ObjectId id = document.AllocateObjectId();
		document.Add(
			new DataPatternDefinition(
				id,
				$"Pattern {id.Value}"));
		return id;
	}

	private static ObjectId AddSequence(
		SongDocument document,
		ObjectId patternId)
	{
		ObjectId id = document.AllocateObjectId();
		DataSequenceDefinition sequence =
			new(
				id,
				$"Sequence {id.Value}");
		sequence.Entries.Add(
			new SequenceEntry(patternId));
		document.Add(sequence);
		return id;
	}
}
