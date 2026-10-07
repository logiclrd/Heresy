using System;
using System.Linq;

using Heresy.Core.Objects;

namespace Heresy.UserInterface.PatternEditing;

public sealed record PatternSourceOption(
	ObjectId Id,
	string Name,
	SongObjectKind Kind)
{
	public string DisplayName =>
		$"{Name} <{Id.Value}>";

	public override string ToString() => DisplayName;
}

public static class PatternSourceCatalog
{
	public static PatternSourceOption[] GetSources(
		SongDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);

		return document.Objects.Values
			.Where(songObject => IsSoundSource(songObject.Kind))
			.OrderBy(songObject => KindOrder(songObject.Kind))
			.ThenBy(songObject => songObject.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(songObject => songObject.Id.Value)
			.Select(songObject =>
				new PatternSourceOption(
					songObject.Id,
					songObject.Name,
					songObject.Kind))
			.ToArray();
	}

	public static bool IsSoundSource(SongObjectKind kind)
		=> kind is
			SongObjectKind.Sample
			or SongObjectKind.FmSynth
			or SongObjectKind.Instrument
			or SongObjectKind.Pattern
			or SongObjectKind.Sequence;

	private static int KindOrder(SongObjectKind kind)
		=> kind switch
		{
			SongObjectKind.Sample => 0,
			SongObjectKind.FmSynth => 1,
			SongObjectKind.Instrument => 2,
			SongObjectKind.Pattern => 3,
			SongObjectKind.Sequence => 4,
			_ => int.MaxValue,
		};
}
