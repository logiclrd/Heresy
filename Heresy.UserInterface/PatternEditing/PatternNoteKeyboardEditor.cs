using System;

using Avalonia.Input;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.PatternEditing;

public sealed class PatternNoteInputState
{
	public PatternNoteInputState(
		ObjectId sourceId,
		int baseOctave)
	{
		SourceId = sourceId;
		BaseOctave = ValidateOctave(baseOctave);
	}

	public ObjectId SourceId { get; set; }

	public PatternEditMask EditMask { get; set; } =
		PatternEditMask.Default;

	public double? CurrentVolume
	{
		get => _currentVolume;
		set
		{
			if (value.HasValue
				&& (double.IsNaN(value.Value)
					|| double.IsInfinity(value.Value)
					|| value.Value < 0.0
					|| value.Value > 1.0))
			{
				throw new ArgumentOutOfRangeException(nameof(value));
			}
			_currentVolume = value;
		}
	}

	public int BaseOctave
	{
		get => _baseOctave;
		set => _baseOctave = ValidateOctave(value);
	}

	private int _baseOctave;
	private double? _currentVolume;

	private static int ValidateOctave(int value)
	{
		if (value is < 0 or > 8)
			throw new ArgumentOutOfRangeException(
				nameof(value),
				"Tracker base octave must be in the range 0..8.");
		return value;
	}
}

public readonly record struct PatternNoteInputResult(
	bool Handled,
	bool Changed,
	bool Rejected)
{
	public bool NoteApplied { get; init; }
}

public static class PatternNoteKeyboardEditor
{
	public static PatternNoteInputResult TypePhysical(
		DocumentWorkspace workspace,
		DataPatternDefinition pattern,
		PatternEffectCursor cursor,
		PatternNoteInputState state,
		PhysicalKey key)
	{
		if (!PatternNoteKeyboard.TryGetTrackerCharacter(
			key,
			out char trackerKey))
		{
			return new PatternNoteInputResult(
				false,
				false,
				false);
		}

		return Type(
			workspace,
			pattern,
			cursor,
			state,
			trackerKey);
	}

	public static PatternNoteInputResult Type(
		DocumentWorkspace workspace,
		DataPatternDefinition pattern,
		PatternEffectCursor cursor,
		PatternNoteInputState state,
		char value)
	{
		Validate(workspace, pattern, cursor, state);

		if (cursor.Field != PatternCellField.Note)
			return new PatternNoteInputResult(false, false, false);

		PatternNoteEntry? note;
		if (value == '1')
		{
			note = new PatternNoteCut();
		}
		else if (value is '`' or '~')
		{
			note = new PatternNoteOff();
		}
		else if (PatternNoteKeyboard.TryGetSemitoneOffset(
			value,
			out int semitoneOffset))
		{
			PatternCell? existingCell =
				pattern.Grid[cursor.Row, cursor.Channel];
			StartPatternNote? existing =
				existingCell?.Note as StartPatternNote;

			int relativeSemitone =
				((state.BaseOctave - 4) * 12)
					+ semitoneOffset;
			double pitchMultiplier =
				Math.Pow(
					2.0,
					relativeSemitone / 12.0);

			note =
				new StartPatternNote(
					existing?.SourceId ?? ObjectId.None,
					pitchMultiplier,
					existing?.PlaybackSpeedMultiplier ?? 1.0,
					existing?.Mixdown ?? false);
		}
		else
		{
			return new PatternNoteInputResult(
				false,
				false,
				false);
		}

		bool noteApplied =
			(state.EditMask & PatternEditMask.Note) != 0;
		bool changed =
			PatternDocumentEditor.SetMaskedEntry(
				workspace,
				pattern,
				cursor.Row,
				cursor.Channel,
				state.EditMask,
				note,
				state.SourceId,
				state.CurrentVolume);

		if (pattern.RowCount > 0)
			cursor.MoveDown(pattern.RowCount);

		return new PatternNoteInputResult(
			true,
			changed,
			false)
			{
				NoteApplied = noteApplied,
			};
	}

	private static void Validate(
		DocumentWorkspace workspace,
		DataPatternDefinition pattern,
		PatternEffectCursor cursor,
		PatternNoteInputState state)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(pattern);
		ArgumentNullException.ThrowIfNull(cursor);
		ArgumentNullException.ThrowIfNull(state);

		if (!workspace.Document.TryGet(
			pattern.Id,
			out SongObject? stored)
			|| !ReferenceEquals(stored, pattern))
		{
			throw new InvalidOperationException(
				"The pattern is not part of the active song document.");
		}

		if (pattern.RowCount <= 0)
			throw new InvalidOperationException(
				"An empty pattern has no editable note rows.");
		if ((uint)cursor.Row >= (uint)pattern.RowCount
			|| (uint)cursor.Channel >= (uint)pattern.ChannelCount)
		{
			throw new InvalidOperationException(
				"The note cursor lies outside the pattern dimensions.");
		}
	}
}
