using AwesomeAssertions;

using Avalonia.Input;

using Heresy.Core.Objects;
using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternSourceNavigationTests
{
	private static readonly PatternSourceOption[] Sources =
	[
		new((ObjectId)1U, "A", SongObjectKind.Sample),
		new((ObjectId)2U, "B", SongObjectKind.Instrument),
		new((ObjectId)3U, "C", SongObjectKind.Pattern),
	];

	[Test]
	public void ForwardSelectsNextSource()
	{
		PatternSourceNavigation.Move(
				Sources,
				Sources[1],
				delta: 1)
			.Should().BeSameAs(Sources[2]);
	}

	[Test]
	public void BackwardSelectsPreviousSource()
	{
		PatternSourceNavigation.Move(
				Sources,
				Sources[1],
				delta: -1)
			.Should().BeSameAs(Sources[0]);
	}

	[Test]
	public void ForwardClampsAtLastSource()
	{
		PatternSourceNavigation.Move(
				Sources,
				Sources[2],
				delta: 1)
			.Should().BeSameAs(Sources[2]);
	}

	[Test]
	public void BackwardClampsAtFirstSource()
	{
		PatternSourceNavigation.Move(
				Sources,
				Sources[0],
				delta: -1)
			.Should().BeSameAs(Sources[0]);
	}

	[Test]
	public void ForwardFromNoSelectionChoosesFirstSource()
	{
		PatternSourceNavigation.Move(
				Sources,
				current: null,
				delta: 1)
			.Should().BeSameAs(Sources[0]);
	}

	[Test]
	public void BackwardFromNoSelectionChoosesLastSource()
	{
		PatternSourceNavigation.Move(
				Sources,
				current: null,
				delta: -1)
			.Should().BeSameAs(Sources[2]);
	}

	[Test]
	public void CurrentSelectionMatchesByIdRatherThanReference()
	{
		PatternSourceOption equivalent =
			new((ObjectId)2U, "Renamed", SongObjectKind.Instrument);

		PatternSourceNavigation.Move(
				Sources,
				equivalent,
				delta: 1)
			.Should().BeSameAs(Sources[2]);
	}

	[TestCase('<', -1)]
	[TestCase('>', 1)]
	public void AngleBracketTextSelectsAdjacentSource(
		char value,
		int expectedDelta)
	{
		PatternSourceNavigationKeyboard.TryGetDelta(
				value,
				out int delta)
			.Should().BeTrue();

		delta.Should().Be(expectedDelta);
	}

	[TestCase(Key.Up, -1)]
	[TestCase(Key.Down, 1)]
	public void ControlArrowSelectsAdjacentSource(
		Key key,
		int expectedDelta)
	{
		PatternSourceNavigationKeyboard.TryGetDelta(
				key,
				KeyModifiers.Control,
				out int delta)
			.Should().BeTrue();

		delta.Should().Be(expectedDelta);
	}

	[Test]
	public void UnmodifiedArrowIsNotSourceNavigation()
	{
		PatternSourceNavigationKeyboard.TryGetDelta(
				Key.Down,
				KeyModifiers.None,
				out int delta)
			.Should().BeFalse();

		delta.Should().Be(0);
	}

	[Test]
	public void ControlShiftArrowIsReservedForSelectionCommands()
	{
		PatternSourceNavigationKeyboard.TryGetDelta(
				Key.Down,
				KeyModifiers.Control | KeyModifiers.Shift,
				out int delta)
			.Should().BeFalse();

		delta.Should().Be(0);
	}

	[Test]
	public void ControlAltArrowIsReservedForOtherCommands()
	{
		PatternSourceNavigationKeyboard.TryGetDelta(
				Key.Down,
				KeyModifiers.Control | KeyModifiers.Alt,
				out int delta)
			.Should().BeFalse();

		delta.Should().Be(0);
	}

	[Test]
	public void OtherTextIsNotSourceNavigation()
	{
		PatternSourceNavigationKeyboard.TryGetDelta(
				'.',
				out int delta)
			.Should().BeFalse();

		delta.Should().Be(0);
	}

	[Test]
	public void EmptyCatalogHasNoSelection()
	{
		PatternSourceNavigation.Move(
				[],
				current: null,
				delta: 1)
			.Should().BeNull();
	}
}
