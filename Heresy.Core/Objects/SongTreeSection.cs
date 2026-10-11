using System;

namespace Heresy.Core.Objects;

/// <summary>
/// Fixed top-level organizational sections projected as the six panes of the
/// document view. The section is presentation structure, not object ownership.
/// </summary>
public enum SongTreeSection
{
	Sequences,
	Patterns,
	Instruments,
	Samples,
	Envelopes,
	Filters,
}

public static class SongTreeSections
{
	public static readonly SongTreeSection[] DocumentOrder =
	[
		SongTreeSection.Sequences,
		SongTreeSection.Patterns,
		SongTreeSection.Samples,
		SongTreeSection.Filters,
		SongTreeSection.Envelopes,
		SongTreeSection.Instruments,
	];

	public static string GetName(SongTreeSection section)
		=> section switch
		{
			SongTreeSection.Sequences => "Sequences",
			SongTreeSection.Patterns => "Patterns",
			SongTreeSection.Instruments => "Instruments",
			SongTreeSection.Samples => "Patches",
			SongTreeSection.Filters => "Filters",
			SongTreeSection.Envelopes => "Envelopes",
			_ => throw new ArgumentOutOfRangeException(nameof(section)),
		};

	public static SongTreeSection ForKind(SongObjectKind kind)
		=> kind switch
		{
			SongObjectKind.Sequence => SongTreeSection.Sequences,
			SongObjectKind.Pattern => SongTreeSection.Patterns,
			SongObjectKind.Instrument => SongTreeSection.Instruments,
			SongObjectKind.Envelope => SongTreeSection.Envelopes,
			SongObjectKind.Sample => SongTreeSection.Samples,
			SongObjectKind.FmSynth => SongTreeSection.Samples,
			SongObjectKind.Filter => SongTreeSection.Filters,
			_ => throw new NotSupportedException(
				$"Song-object kind {kind} does not have a document-tree section."),
		};
}
