using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Input;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.PatternEditing;

public enum PatternChordType
{
	Major,
	Minor,
	DominantSeventh,
	MajorSeventh,
	MinorSeventh,
	HalfDiminishedSeventh,
	DiminishedSeventh,
}

public readonly record struct PatternChordStatusTone(
	string NoteText,
	bool Enabled);

public sealed record PatternChordInputSnapshot(
	PatternChordType Type,
	int[] ToneOffsets,
	bool[] EnabledTones,
	int? RootRelativeSemitone);

public sealed class PatternChordInputState
{
	private readonly List<int> _toneOffsets = [];
	private readonly List<bool> _enabledByIndex = [];
	private PatternChordType? _type;
	private int? _rootRelativeSemitone;

	public bool IsActive => _type.HasValue;

	public PatternChordType? Type => _type;

	public IReadOnlyList<int> ToneOffsets => _toneOffsets;

	public IReadOnlyList<bool> EnabledTones =>
		_enabledByIndex.Take(_toneOffsets.Count).ToArray();

	public int? RootRelativeSemitone => _rootRelativeSemitone;

	public PatternChordInputSnapshot CreateSnapshot()
	{
		PatternChordType type =
			RequireActive();
		return new PatternChordInputSnapshot(
			type,
			[.. _toneOffsets],
			[.. EnabledTones],
			_rootRelativeSemitone);
	}

	public void Restore(
		PatternChordInputSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		if (!Enum.IsDefined(snapshot.Type))
			throw new ArgumentOutOfRangeException(nameof(snapshot));
		if (snapshot.ToneOffsets is null
			|| snapshot.EnabledTones is null
			|| snapshot.ToneOffsets.Length == 0
			|| snapshot.EnabledTones.Length
				!= snapshot.ToneOffsets.Length)
		{
			throw new ArgumentException(
				"Chord snapshot tone and enable arrays must be non-empty and have the same length.",
				nameof(snapshot));
		}

		for (int index = 1;
			index < snapshot.ToneOffsets.Length;
			index++)
		{
			if (snapshot.ToneOffsets[index]
				<= snapshot.ToneOffsets[index - 1])
			{
				throw new ArgumentException(
					"Chord snapshot tones must be strictly ascending.",
					nameof(snapshot));
			}
		}

		_type = snapshot.Type;
		_rootRelativeSemitone =
			snapshot.RootRelativeSemitone;
		_toneOffsets.Clear();
		_toneOffsets.AddRange(
			snapshot.ToneOffsets);
		_enabledByIndex.Clear();
		_enabledByIndex.AddRange(
			snapshot.EnabledTones);
	}

	public void Select(
		PatternChordType type)
	{
		_type = type;
		_rootRelativeSemitone = null;
		_toneOffsets.Clear();
		_toneOffsets.AddRange(GetBaseIntervals(type));
		EnsureEnabledCapacity(_toneOffsets.Count);
	}

	public void SetRoot(
		int relativeSemitoneFromC4)
	{
		RequireActive();
		_rootRelativeSemitone = relativeSemitoneFromC4;
	}

	public void RotateFirstToEnd()
	{
		RequireActive();
		if (_toneOffsets.Count <= 1)
			return;

		int moved = _toneOffsets[0];
		_toneOffsets.RemoveAt(0);
		while (moved <= _toneOffsets[^1])
			moved += 12;
		_toneOffsets.Add(moved);
	}

	public void RotateLastToBeginning()
	{
		RequireActive();
		if (_toneOffsets.Count <= 1)
			return;

		int moved = _toneOffsets[^1];
		_toneOffsets.RemoveAt(_toneOffsets.Count - 1);
		while (moved >= _toneOffsets[0])
			moved -= 12;
		_toneOffsets.Insert(0, moved);
	}

	public void AddTone()
	{
		PatternChordType type =
			RequireActive();
		int[] baseIntervals =
			GetBaseIntervals(type);
		int last = _toneOffsets[^1];
		int pitchClass = PositiveModulo(last, 12);
		int baseIndex =
			Array.IndexOf(baseIntervals, pitchClass);

		int nextPitchClass;
		if (baseIndex >= 0)
		{
			nextPitchClass =
				baseIntervals[
					(baseIndex + 1)
						% baseIntervals.Length];
		}
		else
		{
			nextPitchClass =
				baseIntervals[0];
			foreach (int interval in baseIntervals)
			{
				if (interval > pitchClass)
				{
					nextPitchClass = interval;
					break;
				}
			}
		}

		int candidate =
			last - pitchClass + nextPitchClass;
		while (candidate <= last)
			candidate += 12;

		_toneOffsets.Add(candidate);
		EnsureEnabledCapacity(_toneOffsets.Count);
	}

	public void RemoveTone()
	{
		RequireActive();
		if (_toneOffsets.Count <= 1)
			return;

		_toneOffsets.RemoveAt(
			_toneOffsets.Count - 1);
	}

	public bool ToggleTone(
		int index)
	{
		RequireActive();
		if ((uint)index >= (uint)_toneOffsets.Count)
			return false;

		EnsureEnabledCapacity(
			_toneOffsets.Count);
		_enabledByIndex[index] =
			!_enabledByIndex[index];
		return true;
	}

	public void EnableAll()
	{
		RequireActive();
		EnsureEnabledCapacity(
			_toneOffsets.Count);
		for (int index = 0;
			index < _toneOffsets.Count;
			index++)
		{
			_enabledByIndex[index] = true;
		}
	}

	public IReadOnlyList<PatternChordStatusTone> GetStatusTones()
	{
		RequireActive();
		if (!_rootRelativeSemitone.HasValue)
			return [];

		PatternChordStatusTone[] result =
			new PatternChordStatusTone[
				_toneOffsets.Count];
		for (int index = 0;
			index < result.Length;
			index++)
		{
			result[index] =
				new PatternChordStatusTone(
					FormatTrackerNote(
						_rootRelativeSemitone.Value
							+ _toneOffsets[index]),
					_enabledByIndex[index]);
		}

		return result;
	}

	private void EnsureEnabledCapacity(
		int count)
	{
		while (_enabledByIndex.Count < count)
			_enabledByIndex.Add(true);
	}

	private PatternChordType RequireActive()
		=> _type
			?? throw new InvalidOperationException(
				"No chord type is active.");

	private static int[] GetBaseIntervals(
		PatternChordType type)
		=> type switch
		{
			PatternChordType.Major =>
				[0, 4, 7],
			PatternChordType.Minor =>
				[0, 3, 7],
			PatternChordType.DominantSeventh =>
				[0, 4, 7, 10],
			PatternChordType.MajorSeventh =>
				[0, 4, 7, 11],
			PatternChordType.MinorSeventh =>
				[0, 3, 7, 10],
			PatternChordType.HalfDiminishedSeventh =>
				[0, 3, 6, 10],
			PatternChordType.DiminishedSeventh =>
				[0, 3, 6, 9],
			_ =>
				throw new ArgumentOutOfRangeException(
					nameof(type)),
		};

	private static string FormatTrackerNote(
		int relativeSemitoneFromC4)
	{
		int absolute =
			checked(48 + relativeSemitoneFromC4);
		int pitchClass =
			PositiveModulo(
				absolute,
				12);
		int octave =
			(int)Math.Floor(
				absolute / 12.0);
		string[] names =
		[
			"C-",
			"C#",
			"D-",
			"D#",
			"E-",
			"F-",
			"F#",
			"G-",
			"G#",
			"A-",
			"A#",
			"B-",
		];
		return $"{names[pitchClass]}{octave}";
	}

	private static int PositiveModulo(
		int value,
		int modulus)
		=> ((value % modulus) + modulus)
			% modulus;
}

public enum PatternChordCommandKind
{
	Select,
	RotateFirstToEnd,
	RotateLastToBeginning,
	AddTone,
	RemoveTone,
	ToggleTone,
	EnableAll,
}

public readonly record struct PatternChordCommand(
	PatternChordCommandKind Kind,
	PatternChordType? ChordType = null,
	int ToneIndex = -1);

public static class PatternChordKeyboard
{
	public static bool TryGetCommand(
		PhysicalKey key,
		KeyModifiers modifiers,
		out PatternChordCommand command)
	{
		KeyModifiers chordModifiers =
			KeyModifiers.Control
				| KeyModifiers.Alt;

		if (modifiers == chordModifiers)
		{
			if (TryGetChordType(
				key,
				out PatternChordType type))
			{
				command =
					new PatternChordCommand(
						PatternChordCommandKind.Select,
						type);
				return true;
			}

			if (TryGetToneIndex(
				key,
				out int toneIndex))
			{
				command =
					new PatternChordCommand(
						PatternChordCommandKind.ToggleTone,
						ToneIndex: toneIndex);
				return true;
			}

			command =
				key switch
				{
					PhysicalKey.Minus =>
						new PatternChordCommand(
							PatternChordCommandKind.RotateFirstToEnd),
					PhysicalKey.NumPadMultiply =>
						new PatternChordCommand(
							PatternChordCommandKind.AddTone),
					PhysicalKey.NumPadDivide =>
						new PatternChordCommand(
							PatternChordCommandKind.RemoveTone),
					PhysicalKey.Equal =>
						new PatternChordCommand(
							PatternChordCommandKind.EnableAll),
					_ => default,
				};

			return key is
				PhysicalKey.Minus
				or PhysicalKey.NumPadMultiply
				or PhysicalKey.NumPadDivide
				or PhysicalKey.Equal;
		}

		if (modifiers
				== (chordModifiers | KeyModifiers.Shift)
			&& key == PhysicalKey.Equal)
		{
			command =
				new PatternChordCommand(
					PatternChordCommandKind.RotateLastToBeginning);
			return true;
		}

		command = default;
		return false;
	}

	private static bool TryGetChordType(
		PhysicalKey key,
		out PatternChordType type)
	{
		type =
			key switch
			{
				PhysicalKey.Z =>
					PatternChordType.Major,
				PhysicalKey.X =>
					PatternChordType.Minor,
				PhysicalKey.C =>
					PatternChordType.DominantSeventh,
				PhysicalKey.V =>
					PatternChordType.MajorSeventh,
				PhysicalKey.B =>
					PatternChordType.MinorSeventh,
				PhysicalKey.N =>
					PatternChordType.HalfDiminishedSeventh,
				PhysicalKey.M =>
					PatternChordType.DiminishedSeventh,
				_ => default,
			};

		return key is
			PhysicalKey.Z
				or PhysicalKey.X
				or PhysicalKey.C
				or PhysicalKey.V
				or PhysicalKey.B
				or PhysicalKey.N
				or PhysicalKey.M;
	}

	private static bool TryGetToneIndex(
		PhysicalKey key,
		out int index)
	{
		index =
			key switch
			{
				PhysicalKey.Digit1 => 0,
				PhysicalKey.Digit2 => 1,
				PhysicalKey.Digit3 => 2,
				PhysicalKey.Digit4 => 3,
				PhysicalKey.Digit5 => 4,
				PhysicalKey.Digit6 => 5,
				PhysicalKey.Digit7 => 6,
				PhysicalKey.Digit8 => 7,
				PhysicalKey.Digit9 => 8,
				_ => -1,
			};
		return index >= 0;
	}
}

public readonly record struct PatternChordInputResult(
	bool Handled,
	bool Changed,
	bool NoteApplied,
	IReadOnlyList<int> Channels);

public static class PatternChordEditor
{
	public static PatternChordInputResult TypeRootPhysical(
		DocumentWorkspace workspace,
		DataPatternDefinition pattern,
		PatternEffectCursor cursor,
		PatternNoteInputState noteState,
		PatternChordInputState chordState,
		PhysicalKey key)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(pattern);
		ArgumentNullException.ThrowIfNull(cursor);
		ArgumentNullException.ThrowIfNull(noteState);
		ArgumentNullException.ThrowIfNull(chordState);

		if (!chordState.IsActive
			|| cursor.Field != PatternCellField.Note
			|| !PatternNoteKeyboard.TryGetSemitoneOffset(
				key,
				out int keySemitone))
		{
			return new PatternChordInputResult(
				false,
				false,
				false,
				[]);
		}

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
				"An empty pattern has no editable chord rows.");
		if ((uint)cursor.Row >= (uint)pattern.RowCount
			|| (uint)cursor.Channel >= (uint)pattern.ChannelCount)
		{
			throw new InvalidOperationException(
				"The chord cursor lies outside the pattern dimensions.");
		}

		int rootRelativeSemitone =
			((noteState.BaseOctave - 4) * 12)
				+ keySemitone;
		chordState.SetRoot(
			rootRelativeSemitone);

		ObjectId sourceId =
			NormalizeSource(
				workspace,
				noteState,
				noteState.SourceId);
		bool noteApplied =
			(noteState.EditMask
				& PatternEditMask.Note) != 0;
		bool changed = false;
		List<int> channels = [];
		int destinationChannel =
			cursor.Channel;

		for (int toneIndex = 0;
			toneIndex < chordState.ToneOffsets.Count;
			toneIndex++)
		{
			if (!chordState.EnabledTones[toneIndex])
				continue;
			if (destinationChannel
				>= pattern.ChannelCount)
			{
				break;
			}

			channels.Add(
				destinationChannel);
			PatternCell? existingCell =
				pattern.Grid[
					cursor.Row,
					destinationChannel];
			StartPatternNote? existingNote =
				existingCell?.Note
					as StartPatternNote;
			int relativeSemitone =
				rootRelativeSemitone
					+ chordState.ToneOffsets[
						toneIndex];
			StartPatternNote note =
				new(
					existingNote?.SourceId
						?? ObjectId.None,
					Math.Pow(
						2.0,
						relativeSemitone / 12.0),
					existingNote?.PlaybackSpeedMultiplier
						?? 1.0,
					existingNote?.Mixdown
						?? false);

			changed |=
				ApplyMaskedEntry(
					pattern,
					cursor.Row,
					destinationChannel,
					noteState.EditMask,
					note,
					sourceId,
					noteState.CurrentVolume);

			destinationChannel++;
		}

		if (changed)
			workspace.Document.MarkChanged(
				affectsAudio: true);

		cursor.MoveDown(
			pattern.RowCount);

		return new PatternChordInputResult(
			true,
			changed,
			noteApplied,
			channels);
	}

	private static ObjectId NormalizeSource(
		DocumentWorkspace workspace,
		PatternNoteInputState noteState,
		ObjectId sourceId)
	{
		if ((noteState.EditMask
				& PatternEditMask.Source) == 0
			|| sourceId.IsNone)
		{
			return sourceId;
		}

		return workspace.Document.TryGet(
				sourceId,
				out SongObject? source)
			&& source is not null
			&& PatternSourceCatalog.IsSoundSource(
				source.Kind)
				? sourceId
				: ObjectId.None;
	}

	private static bool ApplyMaskedEntry(
		DataPatternDefinition pattern,
		int row,
		int channel,
		PatternEditMask mask,
		PatternNoteEntry note,
		ObjectId sourceId,
		double? volume)
	{
		PatternCell? cell =
			pattern.Grid[row, channel];
		bool noteChanged =
			(mask & PatternEditMask.Note) != 0
				&& !Equals(
					cell?.Note,
					note);
		bool sourceChanged =
			(mask & PatternEditMask.Source) != 0
				&& (cell?.SourceId
					?? ObjectId.None)
					!= sourceId;
		bool volumeChanged =
			(mask & PatternEditMask.Volume) != 0
				&& cell?.Volume != volume
				&& !(cell is null
					&& volume is null);

		if (!noteChanged
			&& !sourceChanged
			&& !volumeChanged)
		{
			return false;
		}

		cell ??=
			pattern.Grid.GetOrCreateCell(
				row,
				channel);
		if ((mask & PatternEditMask.Note) != 0)
			cell.Note = note;
		if ((mask & PatternEditMask.Source) != 0)
			cell.SourceId = sourceId;
		if ((mask & PatternEditMask.Volume) != 0)
			cell.Volume = volume;

		if (cell.IsEmpty)
			pattern.Grid.ClearCell(
				row,
				channel);
		return true;
	}
}
