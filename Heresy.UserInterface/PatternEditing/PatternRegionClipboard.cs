using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

using Avalonia.Input;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Persistence;
using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.PatternEditing;

public sealed record PatternRegionClipboardCell(
	int RowOffset,
	int ChannelOffset,
	PatternNoteEntry? Note,
	ObjectId SourceId,
	double? Volume,
	PatternEffect[] Effects)
{
	public bool IsEmpty =>
		Note is null
			&& SourceId.IsNone
			&& Volume is null
			&& Effects.Length == 0;
}

public sealed class PatternRegionClipboardData
{
	public PatternRegionClipboardData(
		int rowCount,
		int channelCount,
		IReadOnlyList<PatternRegionClipboardCell> cells)
	{
		if (rowCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(rowCount));
		if (channelCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(channelCount));
		ArgumentNullException.ThrowIfNull(cells);

		HashSet<(int Row, int Channel)> addresses = [];
		foreach (PatternRegionClipboardCell cell in cells)
		{
			if ((uint)cell.RowOffset >= (uint)rowCount)
				throw new ArgumentOutOfRangeException(nameof(cells));
			if ((uint)cell.ChannelOffset >= (uint)channelCount)
				throw new ArgumentOutOfRangeException(nameof(cells));
			ArgumentNullException.ThrowIfNull(cell.Effects);
			if (cell.Effects.Any(effect => effect is null))
				throw new ArgumentException(
					"Clipboard effect stacks may not contain null entries.",
					nameof(cells));
			if (!addresses.Add((cell.RowOffset, cell.ChannelOffset)))
				throw new ArgumentException(
					"Clipboard cells may not contain duplicate coordinates.",
					nameof(cells));
		}

		RowCount = rowCount;
		ChannelCount = channelCount;
		Cells = [.. cells];
	}

	public int RowCount { get; }

	public int ChannelCount { get; }

	public IReadOnlyList<PatternRegionClipboardCell> Cells { get; }
}

public static class PatternRegionClipboardCodec
{
	private const string Header = "Heresy Pattern Region/1";

	public static string Serialize(
		PatternRegionClipboardData data)
	{
		ArgumentNullException.ThrowIfNull(data);

		JsonArray cells = [];
		foreach (PatternRegionClipboardCell cell in data.Cells)
		{
			JsonObject node =
				new()
				{
					["row"] = cell.RowOffset,
					["channel"] = cell.ChannelOffset,
					["sourceId"] = cell.SourceId.Value,
					["effects"] =
						JsonNode.Parse(
							SongDocumentJson.SerializePatternEffects(
								cell.Effects)),
				};

			if (cell.Note is not null)
			{
				node["note"] =
					JsonNode.Parse(
						SongDocumentJson.SerializePatternNote(
							cell.Note));
			}

			if (cell.Volume.HasValue)
				node["volume"] = cell.Volume.Value;

			cells.Add(node);
		}

		JsonObject root =
			new()
			{
				["rows"] = data.RowCount,
				["channels"] = data.ChannelCount,
				["cells"] = cells,
			};

		return Header
			+ Environment.NewLine
			+ root.ToJsonString();
	}

	public static PatternRegionClipboardData Deserialize(
		string text)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(text);

		using StringReader reader = new(text);
		string? header = reader.ReadLine();
		if (!string.Equals(
			header,
			Header,
			StringComparison.Ordinal))
		{
			throw new ArgumentException(
				"The clipboard does not contain a Heresy pattern region.",
				nameof(text));
		}

		string json = reader.ReadToEnd();
		if (string.IsNullOrWhiteSpace(json))
		{
			throw new ArgumentException(
				"The clipboard pattern-region payload is empty.",
				nameof(text));
		}

		try
		{
			JsonNode? parsed = JsonNode.Parse(json);
			if (parsed is not JsonObject root)
				throw new InvalidDataException(
					"Pattern-region JSON must be an object.");

			int rows = RequiredInt32(root, "rows");
			int channels = RequiredInt32(root, "channels");
			if (root["cells"] is not JsonArray cellsNode)
				throw new InvalidDataException(
					"Pattern-region cells must be an array.");

			List<PatternRegionClipboardCell> cells = [];
			foreach (JsonNode? item in cellsNode)
			{
				if (item is not JsonObject cell)
					throw new InvalidDataException(
						"Pattern-region cells must be objects.");

				int row = RequiredInt32(cell, "row");
				int channel = RequiredInt32(cell, "channel");
				uint sourceId =
					cell["sourceId"] is JsonNode sourceNode
						? sourceNode.GetValue<uint>()
						: 0U;
				double? volume =
					cell["volume"] is JsonNode volumeNode
						? volumeNode.GetValue<double>()
						: null;

				PatternNoteEntry? note = null;
				if (cell["note"] is JsonNode noteNode)
				{
					note =
						SongDocumentJson.DeserializePatternNote(
							noteNode.ToJsonString());
				}

				if (cell["effects"] is not JsonNode effectsNode)
					throw new InvalidDataException(
						"Pattern-region cell effects are required.");

				PatternEffect[] effects =
					SongDocumentJson.DeserializePatternEffects(
						effectsNode.ToJsonString());

				cells.Add(
					new PatternRegionClipboardCell(
						row,
						channel,
						note,
						new ObjectId(sourceId),
						volume,
						effects));
			}

			return new PatternRegionClipboardData(
				rows,
				channels,
				cells);
		}
		catch (Exception ex)
			when (ex is InvalidDataException
				or JsonException
				or NotSupportedException
				or ArgumentOutOfRangeException
				or ArgumentException)
		{
			throw new ArgumentException(
				"The clipboard contains an invalid Heresy pattern region.",
				nameof(text),
				ex);
		}
	}

	public static bool TryDeserialize(
		string text,
		out PatternRegionClipboardData? data)
	{
		try
		{
			data = Deserialize(text);
			return true;
		}
		catch (ArgumentException)
		{
			data = null;
			return false;
		}
	}

	private static int RequiredInt32(
		JsonObject node,
		string name)
	{
		if (node[name] is not JsonNode value)
			throw new InvalidDataException(
				$"Pattern-region property '{name}' is required.");

		return value.GetValue<int>();
	}
}

public enum PatternRegionPasteMode
{
	Merge,
	Overwrite,
}

public static class PatternRegionClipboardEditor
{
	public static PatternRegionClipboardData Capture(
		PatternEditorContext context,
		PatternSelectionRegion region)
	{
		ArgumentNullException.ThrowIfNull(context);
		ValidateRegion(context, region);

		List<PatternRegionClipboardCell> cells = [];
		for (int displayRow = region.TopRow;
			displayRow <= region.BottomRow;
			displayRow++)
		{
			PatternEditorRow row =
				context.GetRow(displayRow);
			for (int channel = region.LeftChannel;
				channel <= region.RightChannel;
				channel++)
			{
				if (channel >= row.Pattern.ChannelCount)
					continue;

				PatternCell? cell =
					row.Pattern.Grid[
						row.PatternRow,
						channel];
				cells.Add(
					new PatternRegionClipboardCell(
						displayRow - region.TopRow,
						channel - region.LeftChannel,
						cell?.Note,
						cell?.SourceId ?? ObjectId.None,
						cell?.Volume,
						cell is null
							? []
							: [.. cell.Effects]));
			}
		}

		return new PatternRegionClipboardData(
			region.BottomRow - region.TopRow + 1,
			region.RightChannel - region.LeftChannel + 1,
			cells);
	}

	public static bool Paste(
		DocumentWorkspace workspace,
		PatternEditorContext context,
		PatternEffectCursor cursor,
		PatternRegionClipboardData data,
		PatternRegionPasteMode mode)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(cursor);
		ArgumentNullException.ThrowIfNull(data);

		if (context.Rows.Count == 0)
			return false;
		if ((uint)cursor.Row >= (uint)context.Rows.Count)
			throw new InvalidOperationException(
				"The editor cursor lies outside the paste context.");

		bool changed = false;
		bool affectsAudio = false;

		foreach (PatternRegionClipboardCell source in data.Cells)
		{
			int displayRow =
				cursor.Row + source.RowOffset;
			if ((uint)displayRow >= (uint)context.Rows.Count)
				continue;

			PatternEditorRow targetRow =
				context.GetRow(displayRow);
			int channel =
				cursor.Channel + source.ChannelOffset;
			if ((uint)channel >= (uint)targetRow.Pattern.ChannelCount)
				continue;

			PatternCell? destination =
				targetRow.Pattern.Grid[
					targetRow.PatternRow,
					channel];

			PatternCellSnapshot before =
				PatternCellSnapshot.From(destination);
			PatternCellSnapshot after =
				mode switch
				{
					PatternRegionPasteMode.Merge =>
						before.Merge(source),
					PatternRegionPasteMode.Overwrite =>
						PatternCellSnapshot.From(source),
					_ =>
						throw new ArgumentOutOfRangeException(
							nameof(mode)),
				};

			if (before.Equals(after))
				continue;

			ApplySnapshot(
				targetRow.Pattern,
				targetRow.PatternRow,
				channel,
				after);

			changed = true;
			affectsAudio |=
				AffectsAudio(before, after);
		}

		if (changed)
			workspace.Document.MarkChanged(affectsAudio);

		return changed;
	}

	public static bool Clear(
		DocumentWorkspace workspace,
		PatternEditorContext context,
		PatternSelectionRegion region)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(context);
		ValidateRegion(context, region);

		bool changed = false;
		bool affectsAudio = false;

		for (int displayRow = region.TopRow;
			displayRow <= region.BottomRow;
			displayRow++)
		{
			PatternEditorRow row =
				context.GetRow(displayRow);
			for (int channel = region.LeftChannel;
				channel <= region.RightChannel;
				channel++)
			{
				if (channel >= row.Pattern.ChannelCount)
					continue;

				PatternCell? cell =
					row.Pattern.Grid[
						row.PatternRow,
						channel];
				if (cell is null)
					continue;

				PatternCellSnapshot before =
					PatternCellSnapshot.From(cell);
				affectsAudio |=
					AffectsAudio(
						before,
						PatternCellSnapshot.Empty);
				row.Pattern.Grid.ClearCell(
					row.PatternRow,
					channel);
				changed = true;
			}
		}

		if (changed)
			workspace.Document.MarkChanged(affectsAudio);

		return changed;
	}

	private static void ApplySnapshot(
		DataPatternDefinition pattern,
		int row,
		int channel,
		PatternCellSnapshot snapshot)
	{
		if (snapshot.IsEmpty)
		{
			pattern.Grid.ClearCell(row, channel);
			return;
		}

		PatternCell cell =
			pattern.Grid.GetOrCreateCell(
				row,
				channel);
		cell.Note = snapshot.Note;
		cell.SourceId = snapshot.SourceId;
		cell.Volume = snapshot.Volume;
		cell.Effects.Clear();
		cell.Effects.AddRange(snapshot.Effects);
	}

	private static bool AffectsAudio(
		PatternCellSnapshot before,
		PatternCellSnapshot after)
	{
		if (!Equals(before.Note, after.Note)
			|| before.SourceId != after.SourceId
			|| before.Volume != after.Volume)
		{
			return true;
		}

		if (before.Effects.SequenceEqual(after.Effects))
			return false;

		return before.Effects.Any(
				effect => effect is not EmptyTrackerPatternEffect)
			|| after.Effects.Any(
				effect => effect is not EmptyTrackerPatternEffect);
	}

	private static void ValidateRegion(
		PatternEditorContext context,
		PatternSelectionRegion region)
	{
		if (context.Rows.Count == 0)
			throw new InvalidOperationException(
				"The pattern editor context has no clipboard rows.");
		if (region.TopRow < 0
			|| region.BottomRow < region.TopRow
			|| region.BottomRow >= context.Rows.Count
			|| region.LeftChannel < 0
			|| region.RightChannel < region.LeftChannel)
		{
			throw new ArgumentOutOfRangeException(nameof(region));
		}
	}

	private sealed record PatternCellSnapshot(
		PatternNoteEntry? Note,
		ObjectId SourceId,
		double? Volume,
		PatternEffect[] Effects)
	{
		public static PatternCellSnapshot Empty { get; } =
			new(
				null,
				ObjectId.None,
				null,
				[]);

		public bool IsEmpty =>
			Note is null
				&& SourceId.IsNone
				&& Volume is null
				&& Effects.Length == 0;

		public static PatternCellSnapshot From(
			PatternCell? cell)
			=> cell is null
				? Empty
				: new PatternCellSnapshot(
					cell.Note,
					cell.SourceId,
					cell.Volume,
					[.. cell.Effects]);

		public static PatternCellSnapshot From(
			PatternRegionClipboardCell cell)
			=> new(
				cell.Note,
				cell.SourceId,
				cell.Volume,
				[.. cell.Effects]);

		public PatternCellSnapshot Merge(
			PatternRegionClipboardCell source)
			=> new(
				source.Note ?? Note,
				source.SourceId.IsNone
					? SourceId
					: source.SourceId,
				source.Volume ?? Volume,
				source.Effects.Length == 0
					? Effects
					: [.. source.Effects]);
	}
}

public enum PatternRegionClipboardCommand
{
	Copy,
	Cut,
	PasteMerge,
	PasteOverwrite,
	Clear,
}

public static class PatternRegionClipboardKeyboard
{
	public static bool TryGetCommand(
		Key key,
		KeyModifiers modifiers,
		out PatternRegionClipboardCommand command)
	{
		KeyModifiers primary =
			modifiers & (KeyModifiers.Control | KeyModifiers.Meta);
		KeyModifiers remaining =
			modifiers
				& ~(KeyModifiers.Control | KeyModifiers.Meta);

		if (primary == 0
			|| (remaining & KeyModifiers.Alt) != 0)
		{
			command = default;
			return false;
		}

		if (key == Key.V
			&& remaining == KeyModifiers.Shift)
		{
			command =
				PatternRegionClipboardCommand.PasteOverwrite;
			return true;
		}

		if (remaining != KeyModifiers.None)
		{
			command = default;
			return false;
		}

		command =
			key switch
			{
				Key.C => PatternRegionClipboardCommand.Copy,
				Key.X => PatternRegionClipboardCommand.Cut,
				Key.V => PatternRegionClipboardCommand.PasteMerge,
				Key.Delete => PatternRegionClipboardCommand.Clear,
				_ => default,
			};

		return key is Key.C or Key.X or Key.V or Key.Delete;
	}
}
