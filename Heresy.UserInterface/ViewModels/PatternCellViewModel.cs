using System;
using System.Globalization;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;

namespace Heresy.UserInterface.ViewModels;

public sealed class PatternCellViewModel
{
	private PatternCellViewModel(
		int row,
		int channel,
		string noteText,
		int effectCount,
		string displayText)
	{
		Row = row;
		Channel = channel;
		NoteText = noteText;
		EffectCount = effectCount;
		DisplayText = displayText;
	}

	public int Row { get; }
	public int Channel { get; }
	public string NoteText { get; }
	public int EffectCount { get; }
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
		int effectCount = cell?.Effects.Count ?? 0;
		string display = effectCount == 0
			? noteText
			: $"{noteText}  +{effectCount} fx";

		return new PatternCellViewModel(
			row,
			channel,
			noteText,
			effectCount,
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
