using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternSourceSelectionTests
{
	[Test]
	public void ExplicitCellSourceSelectsMatchingToolbarOption()
	{
		ObjectId sourceId = (ObjectId)17U;
		PatternCell cell = new() { SourceId = sourceId };
		PatternSourceOption[] sources =
		[
			new((ObjectId)9U, "Other", SongObjectKind.Sample),
			new(sourceId, "Piano", SongObjectKind.Instrument),
		];

		PatternSourceOption? selected =
			PatternSourceSelection.FindExplicitSource(
				cell,
				sources);

		selected.Should().BeSameAs(sources[1]);
	}

	[Test]
	public void BlankCellSourceDoesNotResolveRememberedSource()
	{
		PatternCell cell = new();
		PatternSourceOption[] sources =
		[
			new((ObjectId)17U, "Piano", SongObjectKind.Instrument),
		];

		PatternSourceSelection.FindExplicitSource(
				cell,
				sources)
			.Should().BeNull();
	}

	[Test]
	public void MissingCellSourceCannotBecomeToolbarSelection()
	{
		PatternCell cell =
			new()
			{
				SourceId = (ObjectId)99U,
			};
		PatternSourceOption[] sources =
		[
			new((ObjectId)17U, "Piano", SongObjectKind.Instrument),
		];

		PatternSourceSelection.FindExplicitSource(
				cell,
				sources)
			.Should().BeNull();
	}

	[Test]
	public void EmptyCellHasNoExplicitSource()
	{
		PatternSourceOption[] sources =
		[
			new((ObjectId)17U, "Piano", SongObjectKind.Instrument),
		];

		PatternSourceSelection.FindExplicitSource(
				cell: null,
				sources)
			.Should().BeNull();
	}
}
