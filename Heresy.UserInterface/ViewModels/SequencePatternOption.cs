using System;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;

namespace Heresy.UserInterface.ViewModels;

public sealed record SequencePatternOption(
	ObjectId Id,
	string Name)
{
	public string DisplayName => $"{Name} <{Id.Value}>";

	public override string ToString() => DisplayName;
}

public static class SequencePatternCatalog
{
	public static SequencePatternOption[] GetPatterns(
		SongDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);

		return document.Objects.Values
			.OfType<PatternDefinition>()
			.OrderBy(pattern => pattern.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(pattern => pattern.Id.Value)
			.Select(pattern =>
				new SequencePatternOption(
					pattern.Id,
					pattern.Name))
			.ToArray();
	}
}
