using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;

namespace Heresy.UserInterface.ViewModels;

public sealed class PatternCellViewModel
{
	private PatternCellViewModel(
		int row,
		int channel,
		string noteText,
		IReadOnlyList<PatternEffectViewModel> effects,
		string displayText)
	{
		Row = row;
		Channel = channel;
		NoteText = noteText;
		Effects = effects;
		DisplayText = displayText;
	}

	public int Row { get; }
	public int Channel { get; }
	public string NoteText { get; }
	public IReadOnlyList<PatternEffectViewModel> Effects { get; }
	public int EffectCount => Effects.Count;
	public string DisplayText { get; }

	public static PatternCellViewModel Create(
		SongDocument document,
		DataPatternDefinition pattern,
		int row,
		int channel)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(pattern);

		PatternCell? cell = pattern.Grid[row, channel];
		string noteText = FormatNote(document, cell?.Note);
		IReadOnlyList<PatternEffectViewModel> effects =
			cell?.Effects
				.Select(PatternEffectViewModel.Create)
				.ToArray()
			?? Array.Empty<PatternEffectViewModel>();
		string display = effects.Count == 0
			? noteText
			: $"{noteText}  +{effects.Count} fx";

		return new PatternCellViewModel(
			row,
			channel,
			noteText,
			effects,
			display);
	}

	private static string FormatNote(
		SongDocument document,
		PatternNoteEntry? note)
		=> note switch
		{
			null => "—",
			PatternNoteOff => "OFF",
			PatternNoteCut => "CUT",
			StartPatternNote start => FormatStart(document, start),
			_ => note.GetType().Name,
		};

	private static string FormatStart(
		SongDocument document,
		StartPatternNote start)
	{
		string source = ResolveSource(document, start.SourceId);
		string pitch =
			start.PitchMultiplier == 1.0
				? string.Empty
				: $" p×{start.PitchMultiplier.ToString("G4", CultureInfo.InvariantCulture)}";
		string speed =
			start.PlaybackSpeedMultiplier == 1.0
				? string.Empty
				: $" s×{start.PlaybackSpeedMultiplier.ToString("G4", CultureInfo.InvariantCulture)}";
		string mixdown = start.Mixdown ? " [mix]" : string.Empty;
		return $"{source}{pitch}{speed}{mixdown}";
	}

	private static string ResolveSource(
		SongDocument document,
		ObjectId id)
	{
		if (document.TryGet(id, out SongObject? songObject)
			&& songObject is not null)
		{
			return $"{songObject.Name} <{id.Value}>";
		}

		if (document.Tombstones.TryGetValue(id, out ObjectTombstone? tombstone))
			return $"⚠ {tombstone.LastKnownName} <{id.Value}>";

		return $"⚠ <{id.Value}>";
	}
}
