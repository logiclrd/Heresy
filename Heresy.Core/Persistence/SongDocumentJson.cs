using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Heresy.Core.Assets;
using Heresy.Core.Envelopes;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Core.Sequences;
using Heresy.Core.Scripting;

namespace Heresy.Core.Persistence;

/// <summary>
/// JSON persistence for a Heresy song document. Binary assets remain
/// external and are represented by <see cref="ExternalAssetReference"/> values.
/// The persisted object graph is flat: cross-object relationships are ObjectIds.
/// </summary>
public static class SongDocumentJson
{
	private const string FormatName = "Heresy";

	private static readonly JsonSerializerOptions JsonOptions =
		CreateJsonOptions();

	public static string SerializePatternEffects(
		IEnumerable<PatternEffect> effects)
	{
		ArgumentNullException.ThrowIfNull(effects);

		JsonArray array = [];
		foreach (PatternEffect effect in effects)
		{
			ArgumentNullException.ThrowIfNull(effect);
			array.Add(
				JsonSerializer.SerializeToNode(
					effect,
					typeof(PatternEffect),
					JsonOptions));
		}

		return array.ToJsonString(JsonOptions);
	}

	public static PatternEffect[] DeserializePatternEffects(
		string json)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(json);

		JsonNode? node = JsonNode.Parse(json);
		if (node is not JsonArray array)
		{
			throw new InvalidDataException(
				"Pattern-effect JSON must contain an array.");
		}

		List<PatternEffect> effects = [];
		foreach (JsonNode? item in array)
		{
			if (item is null)
				throw new InvalidDataException("Pattern effects may not be null.");

			PatternEffect? effect =
				item.Deserialize<PatternEffect>(JsonOptions);
			if (effect is null)
				throw new InvalidDataException("Pattern effects may not be null.");
			effects.Add(effect);
		}

		return [.. effects];
	}

	public static string Serialize(
		SongDocument document,
		string jsonPath,
		JsonAssetPathMode pathMode = JsonAssetPathMode.Relative,
		IScriptObjectReferenceAnalyzer? scriptReferenceAnalyzer = null)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);
		string fullPath = Path.GetFullPath(jsonPath);
		string directory = Path.GetDirectoryName(fullPath)
			?? throw new ArgumentException("The JSON path must identify a file.", nameof(jsonPath));
		return Serialize(
			document,
			sample => ToStoredPath(directory, sample, pathMode),
			scriptReferenceAnalyzer);
	}

	internal static string Serialize(
		SongDocument document,
		Func<SampleDefinition, string> assetPathSelector,
		IScriptObjectReferenceAnalyzer? scriptReferenceAnalyzer = null)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(assetPathSelector);

		SongReferenceAnalysis referenceAnalysis =
			SongReferenceAnalyzer.Analyze(document);
		HashSet<ObjectId> referencedIds =
			referenceAnalysis.References
				.Select(reference => reference.TargetId)
				.ToHashSet();

		if (referenceAnalysis.HasOpaqueScriptReferences)
		{
			foreach (ObjectId id in document.Tombstones.Keys)
				referencedIds.Add(id);
		}

		document.PruneTombstones(referencedIds);

		JsonObject root =
			new()
			{
				["format"] = FormatName,
				["version"] = SongDocument.FormatVersion,
				["nextObjectId"] = document.NextObjectIdForPersistence,
				["rootSequenceId"] = document.RootSequenceId.Value,
			};

		JsonObject objects = new();
		foreach ((ObjectId id, SongObject songObject) in
			document.Objects.OrderBy(pair => pair.Key.Value))
		{
			objects[id.Value.ToString(CultureInfo.InvariantCulture)] =
				WriteSongObject(songObject, assetPathSelector);
		}
		root["objects"] = objects;

		JsonObject tombstones = new();
		foreach ((ObjectId id, ObjectTombstone tombstone) in
			document.Tombstones.OrderBy(pair => pair.Key.Value))
		{
			tombstones[id.Value.ToString(CultureInfo.InvariantCulture)] =
				new JsonObject
				{
					["lastKnownName"] = tombstone.LastKnownName,
					["kind"] = ToJsonName(tombstone.Kind),
				};
		}
		root["tombstones"] = tombstones;
		root["tree"] = WriteTreeNode(document.Root);

		return root.ToJsonString(JsonOptions);
	}

	public static SongDocument Deserialize(
		string json,
		string jsonPath)
	{
		ArgumentNullException.ThrowIfNull(json);
		ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);
		string fullPath = Path.GetFullPath(jsonPath);
		string directory = Path.GetDirectoryName(fullPath)
			?? throw new ArgumentException("The JSON path must identify a file.", nameof(jsonPath));
		return Deserialize(
			json,
			storedPath => ResolveStoredPath(directory, storedPath));
	}

	internal static SongDocument Deserialize(
		string json,
		Func<string, string> assetPathResolver)
	{
		ArgumentNullException.ThrowIfNull(json);
		ArgumentNullException.ThrowIfNull(assetPathResolver);

		JsonNode? parsed = JsonNode.Parse(json);
		if (parsed is not JsonObject root)
			throw new InvalidDataException("The Heresy document root must be a JSON object.");

		string format = RequiredString(root, "format");
		if (!string.Equals(format, FormatName, StringComparison.Ordinal))
			throw new InvalidDataException($"Unsupported song format '{format}'.");

		int version = RequiredInt32(root, "version");
		if (version != SongDocument.FormatVersion)
			throw new NotSupportedException(
				$"Heresy document version {version} is not supported by this build.");

		uint nextObjectId = RequiredUInt32(root, "nextObjectId");
		uint rootSequenceId = RequiredUInt32(root, "rootSequenceId");

		JsonObject objects =
			RequiredObject(root, "objects");
		JsonObject tombstones =
			RequiredObject(root, "tombstones");
		JsonObject tree =
			RequiredObject(root, "tree");

		SongDocument document = new();

		foreach ((string key, JsonNode? value) in objects)
		{
			ObjectId id = ParseObjectIdKey(key);
			if (id.IsNone)
				throw new InvalidDataException("Song objects may not use ObjectId 0.");
			if (value is not JsonObject objectNode)
				throw new InvalidDataException($"Object {key} must be a JSON object.");

			document.RestoreObject(
				ReadSongObject(id, objectNode, assetPathResolver));
		}

		foreach ((string key, JsonNode? value) in tombstones)
		{
			ObjectId id = ParseObjectIdKey(key);
			if (id.IsNone)
				throw new InvalidDataException("Tombstones may not use ObjectId 0.");
			if (value is not JsonObject tombstoneNode)
				throw new InvalidDataException($"Tombstone {key} must be a JSON object.");

			document.RestoreTombstone(
				new ObjectTombstone(
					id,
					RequiredString(tombstoneNode, "lastKnownName"),
					ParseSongObjectKind(
						RequiredString(tombstoneNode, "kind"))));
		}

		try
		{
			document.RestoreNextObjectId(nextObjectId);
		}
		catch (ArgumentOutOfRangeException ex)
		{
			throw new InvalidDataException(
				"nextObjectId must be greater than every persisted object and tombstone ID, or zero for an exhausted ID space.",
				ex);
		}

		document.RootSequenceId =
			new ObjectId(rootSequenceId);

		SongTreeNode rootNode = ReadTreeNode(tree);
		if (rootNode is not SongTreeFolder rootFolder)
			throw new InvalidDataException("The document tree root must be a folder.");

		try
		{
			document.RestoreTree(rootFolder);
		}
		catch (InvalidOperationException ex)
		{
			throw new InvalidDataException(
				"The persisted song tree does not match the required four-section structure.",
				ex);
		}

		return document;
	}

	public static void Save(
		string path,
		SongDocument document,
		JsonAssetPathMode pathMode = JsonAssetPathMode.Relative,
		IScriptObjectReferenceAnalyzer? scriptReferenceAnalyzer = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(document);

		string fullPath = Path.GetFullPath(path);
		string directory = Path.GetDirectoryName(fullPath)
			?? throw new ArgumentException("The JSON path must identify a file.", nameof(path));
		Directory.CreateDirectory(directory);

		if (pathMode == JsonAssetPathMode.Relative)
			ValidateRelativeAssetLocations(directory, document);

		MaterializeArchiveAssets(directory, document);

		File.WriteAllText(
			fullPath,
			Serialize(
				document,
				sample => ToStoredPath(directory, sample, pathMode),
				scriptReferenceAnalyzer),
			new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
	}

	public static SongDocument Load(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		string fullPath = Path.GetFullPath(path);
		string directory = Path.GetDirectoryName(fullPath)
			?? throw new ArgumentException("The JSON path must identify a file.", nameof(path));

		return Deserialize(
			File.ReadAllText(fullPath, Encoding.UTF8),
			storedPath => ResolveStoredPath(directory, storedPath));
	}

	private static JsonObject WriteSongObject(
		SongObject songObject,
		Func<SampleDefinition, string> assetPathSelector)
	{
		JsonObject result =
			new()
			{
				["name"] = songObject.Name,
			};

		switch (songObject)
		{
			case SampleDefinition sample:
				result["type"] = "sample";
				result["asset"] =
					new JsonObject
					{
						["path"] = assetPathSelector(sample),
						["sha256"] = sample.Asset.Sha256,
					};
				result["referenceFrequencyHz"] = sample.ReferenceFrequencyHz;
				result["loop"] =
					JsonSerializer.SerializeToNode(sample.Loop, JsonOptions);

				JsonArray positions = [];
				foreach (Vector3 position in sample.SourceChannelPositions)
				{
					positions.Add(
						new JsonObject
						{
							["x"] = position.X,
							["y"] = position.Y,
							["z"] = position.Z,
						});
				}
				result["sourceChannelPositions"] = positions;
				break;

			case InstrumentDefinition instrument:
				result["type"] = "instrument";
				result["divisions"] = instrument.Divisions;
				result["offset"] = instrument.Offset;
				result["toneSpecifications"] =
					JsonSerializer.SerializeToNode(
						instrument.ToneSpecifications,
						JsonOptions);
				result["toneTable"] =
					JsonSerializer.SerializeToNode(
						instrument.ToneTable,
						JsonOptions);
				break;

			case DataPatternDefinition pattern:
				result["type"] = "dataPattern";
				WritePatternCommon(result, pattern);

				JsonArray cells = [];
				foreach ((int row, int channel, PatternCell cell) in
					pattern.Grid.EnumerateNonEmptyCells())
				{
					JsonObject cellNode =
						new()
						{
							["row"] = row,
							["channel"] = channel,
						};

					if (!cell.SourceId.IsNone)
						cellNode["sourceId"] = cell.SourceId.Value;

					if (cell.Note is not null)
					{
						cellNode["note"] =
							JsonSerializer.SerializeToNode(
								cell.Note,
								typeof(PatternNoteEntry),
								JsonOptions);
					}

					if (cell.Volume.HasValue)
						cellNode["volume"] = cell.Volume.Value;

					JsonArray effects = [];
					foreach (PatternEffect effect in cell.Effects)
					{
						effects.Add(
							JsonSerializer.SerializeToNode(
								effect,
								typeof(PatternEffect),
								JsonOptions));
					}
					cellNode["effects"] = effects;
					cells.Add(cellNode);
				}

				result["cells"] = cells;
				break;

			case ScriptPatternDefinition pattern:
				result["type"] = "scriptPattern";
				WritePatternCommon(result, pattern);
				result["source"] = pattern.Source;
				break;

			case DataSequenceDefinition sequence:
				result["type"] = "dataSequence";
				result["entries"] =
					JsonSerializer.SerializeToNode(
						sequence.Entries,
						JsonOptions);
				break;

			case ScriptSequenceDefinition sequence:
				result["type"] = "scriptSequence";
				result["source"] = sequence.Source;
				break;

			case AdsrEnvelopeDefinition envelope:
				result["type"] = "adsrEnvelope";
				result["attack"] =
					JsonSerializer.SerializeToNode(
						envelope.Attack,
						JsonOptions);
				result["decay"] =
					JsonSerializer.SerializeToNode(
						envelope.Decay,
						JsonOptions);
				result["sustainLevel"] = envelope.SustainLevel;
				result["release"] =
					JsonSerializer.SerializeToNode(
						envelope.Release,
						JsonOptions);
				break;

			default:
				throw new NotSupportedException(
					$"Song object type {songObject.GetType().FullName} is not supported by JSON persistence.");
		}

		return result;
	}

	private static SongObject ReadSongObject(
		ObjectId id,
		JsonObject node,
		Func<string, string> assetPathResolver)
	{
		string type = RequiredString(node, "type");
		string name = RequiredString(node, "name");

		switch (type)
		{
			case "sample":
				{
					JsonObject assetNode = RequiredObject(node, "asset");
					string storedPath = RequiredString(assetNode, "path");
					string? sha256 =
						assetNode["sha256"] is JsonNode shaNode
							? shaNode.GetValue<string>()
							: null;
					ExternalAssetReference asset =
						new(
							assetPathResolver(storedPath),
							sha256);
					SampleDefinition sample =
						new(id, name, asset)
						{
							ReferenceFrequencyHz =
								RequiredDouble(
									node,
									"referenceFrequencyHz"),
							Loop =
								DeserializeRequired<SampleLoop>(
									node,
									"loop"),
						};

					JsonArray positions =
						RequiredArray(
							node,
							"sourceChannelPositions");
					foreach (JsonNode? positionNode in positions)
					{
						if (positionNode is not JsonObject position)
						{
							throw new InvalidDataException(
								"Sample source-channel positions must be JSON objects.");
						}

						sample.SourceChannelPositions.Add(
							new Vector3(
								RequiredSingle(position, "x"),
								RequiredSingle(position, "y"),
								RequiredSingle(position, "z")));
					}

					return sample;
				}

			case "instrument":
				{
					InstrumentDefinition instrument =
						new(id, name)
						{
							Divisions = RequiredDouble(node, "divisions"),
							Offset = RequiredInt32(node, "offset"),
						};

					List<ToneSpecification> tones =
						DeserializeRequired<List<ToneSpecification>>(
							node,
							"toneSpecifications");
					instrument.ToneSpecifications.AddRange(tones);

					List<int> toneTable =
						DeserializeRequired<List<int>>(
							node,
							"toneTable");
					instrument.ToneTable.AddRange(toneTable);

					return instrument;
				}

			case "dataPattern":
				{
					DataPatternDefinition pattern =
						new(id, name);
					ReadPatternCommon(node, pattern);

					foreach (JsonNode? cellNode in
						RequiredArray(node, "cells"))
					{
						if (cellNode is not JsonObject cell)
							throw new InvalidDataException("Pattern cells must be JSON objects.");

						int row = RequiredInt32(cell, "row");
						int channel = RequiredInt32(cell, "channel");
						PatternCell target =
							pattern.Grid.GetOrCreateCell(
								row,
								channel);

						if (cell["sourceId"] is JsonNode sourceNode)
							target.SourceId = new ObjectId(sourceNode.GetValue<uint>());

						if (cell["note"] is JsonNode noteNode)
						{
							target.Note =
								noteNode.Deserialize<PatternNoteEntry>(
									JsonOptions)
								?? throw new InvalidDataException(
									"Pattern note could not be deserialized.");
						}

						if (cell["volume"] is JsonNode volumeNode)
							target.Volume = volumeNode.GetValue<double>();

						foreach (JsonNode? effectNode in
							RequiredArray(cell, "effects"))
						{
							if (effectNode is null)
								throw new InvalidDataException("Pattern effects may not be null.");

							target.Effects.Add(
								effectNode.Deserialize<PatternEffect>(
									JsonOptions)
								?? throw new InvalidDataException(
									"Pattern effect could not be deserialized."));
						}
					}

					return pattern;
				}

			case "scriptPattern":
				{
					ScriptPatternDefinition pattern =
						new(id, name);
					ReadPatternCommon(node, pattern);
					pattern.Source =
						RequiredString(node, "source");
					return pattern;
				}

			case "dataSequence":
				{
					DataSequenceDefinition sequence =
						new(id, name);
					List<SequenceEntry> entries =
						DeserializeRequired<List<SequenceEntry>>(
							node,
							"entries");
					sequence.Entries.AddRange(entries);
					return sequence;
				}

			case "scriptSequence":
				return new ScriptSequenceDefinition(id, name)
				{
					Source = RequiredString(node, "source"),
				};

			case "adsrEnvelope":
				return new AdsrEnvelopeDefinition(id, name)
				{
					Attack =
						DeserializeRequired<TimeSpan>(
							node,
							"attack"),
					Decay =
						DeserializeRequired<TimeSpan>(
							node,
							"decay"),
					SustainLevel =
						RequiredDouble(
							node,
							"sustainLevel"),
					Release =
						DeserializeRequired<TimeSpan>(
							node,
							"release"),
				};

			default:
				throw new NotSupportedException(
					$"Persisted song object type '{type}' is not supported.");
		}
	}

	private static void MaterializeArchiveAssets(
		string jsonDirectory,
		SongDocument document)
	{
		foreach (SampleDefinition sample in
			document.Objects.Values.OfType<SampleDefinition>())
		{
			if (!HeresyModulePath.TrySplit(
				sample.Asset.FullPath,
				out string archivePath,
				out string entryPath)
				|| !File.Exists(archivePath))
			{
				continue;
			}

			HeresyModulePath.ValidateEntryPath(entryPath);
			string nativeEntry =
				entryPath.Replace('/', Path.DirectorySeparatorChar);
			string destination =
				Path.GetFullPath(Path.Combine(jsonDirectory, nativeEntry));
			string rootWithSeparator =
				Path.GetFullPath(jsonDirectory)
				.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
				+ Path.DirectorySeparatorChar;
			if (!destination.StartsWith(
				rootWithSeparator,
				OperatingSystem.IsWindows()
					? StringComparison.OrdinalIgnoreCase
					: StringComparison.Ordinal))
			{
				throw new InvalidDataException(
					$"Archive asset path '{entryPath}' escapes the JSON directory.");
			}

			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			using (Stream source =
				ExternalAssetIntegrity.OpenRead(sample.Asset.FullPath))
			using (FileStream target = File.Create(destination))
				source.CopyTo(target);

			sample.Asset = sample.Asset with
			{
				FullPath = destination,
			};
		}
	}

	private static void ValidateRelativeAssetLocations(
		string jsonDirectory,
		SongDocument document)
	{
		foreach (SampleDefinition sample in
			document.Objects.Values.OfType<SampleDefinition>())
		{
			if (HeresyModulePath.TrySplit(
				sample.Asset.FullPath,
				out string archivePath,
				out _)
				&& File.Exists(archivePath))
			{
				continue;
			}

			if (!TryGetPortableRelativePath(
				jsonDirectory,
				sample.Asset.FullPath,
				out _))
			{
				throw new InvalidOperationException(
					$"Sample '{sample.Name}' ({sample.Id}) uses asset '{sample.Asset.FullPath}', " +
					"which is outside the .hm.json directory subtree. " +
					"Choose the '.hm.json (absolute paths)' save type or move the asset beneath the JSON file's directory.");
			}
		}
	}

	private static string ToStoredPath(
		string jsonDirectory,
		SampleDefinition sample,
		JsonAssetPathMode pathMode)
	{
		if (pathMode == JsonAssetPathMode.Absolute)
		{
			string fullPath = Path.GetFullPath(sample.Asset.FullPath);
			if (!LooksAbsoluteStoredPath(fullPath))
			{
				throw new InvalidOperationException(
					$"Asset path '{fullPath}' cannot be represented as a supported absolute .hm.json path.");
			}
			return fullPath;
		}

		if (TryGetPortableRelativePath(
			jsonDirectory,
			sample.Asset.FullPath,
			out string? relative))
		{
			return relative;
		}

		throw new InvalidOperationException(
			$"Sample '{sample.Name}' ({sample.Id}) uses asset '{sample.Asset.FullPath}', " +
			"which is outside the .hm.json directory subtree. " +
			"Choose the '.hm.json (absolute paths)' save type or move the asset beneath the JSON file's directory. " +
			"TODO: navigate the editor directly to the offending sample.");
	}

	private static bool TryGetPortableRelativePath(
		string jsonDirectory,
		string fullAssetPath,
		out string relativePath)
	{
		string root = Path.GetFullPath(jsonDirectory);
		string asset = Path.GetFullPath(fullAssetPath);
		string relative = Path.GetRelativePath(root, asset);

		if (Path.IsPathRooted(relative)
			|| relative == ".."
			|| relative.StartsWith(
				".." + Path.DirectorySeparatorChar,
				StringComparison.Ordinal)
			|| (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar
				&& relative.StartsWith(
					".." + Path.AltDirectorySeparatorChar,
					StringComparison.Ordinal)))
		{
			relativePath = string.Empty;
			return false;
		}

		if (Path.DirectorySeparatorChar != '/')
			relative = relative.Replace(Path.DirectorySeparatorChar, '/');
		if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar
			&& Path.AltDirectorySeparatorChar != '/')
		{
			relative = relative.Replace(Path.AltDirectorySeparatorChar, '/');
		}

		// A literal backslash is a valid Unix filename character, but it would
		// make a portable relative path ambiguous. Such assets require absolute mode.
		if (relative.Contains('\\'))
		{
			relativePath = string.Empty;
			return false;
		}

		relativePath = relative;
		return true;
	}

	private static string ResolveStoredPath(
		string jsonDirectory,
		string storedPath)
	{
		if (string.IsNullOrWhiteSpace(storedPath))
			throw new InvalidDataException("External asset paths must be non-empty.");

		string resolved;
		if (LooksAbsoluteStoredPath(storedPath))
		{
			bool unixStyle = storedPath.StartsWith("/", StringComparison.Ordinal);
			if (unixStyle == OperatingSystem.IsWindows())
			{
				throw new InvalidDataException(
					$"Absolute asset path '{storedPath}' does not match this host's path convention.");
			}

			try
			{
				if (!Path.IsPathFullyQualified(storedPath))
				throw new InvalidDataException(
					$"Absolute asset path '{storedPath}' is not fully qualified on this host.");
				resolved = Path.GetFullPath(storedPath);
			}
			catch (Exception ex) when (
				ex is ArgumentException
				or NotSupportedException
				or PathTooLongException)
			{
				throw new InvalidDataException(
					$"Absolute asset path '{storedPath}' cannot be interpreted on this host.",
					ex);
			}
		}
		else
		{
			if (storedPath.Contains('\\'))
			{
				throw new InvalidDataException(
					"Relative .hm.json asset paths must use '/' separators.");
			}

			string[] components = storedPath.Split('/');
			if (components.Any(component =>
				component.Length == 0
					|| component == "."
					|| component == ".."))
			{
				throw new InvalidDataException(
					"Relative .hm.json asset paths may not escape the JSON directory or contain empty path components.");
			}

			string nativePath =
				storedPath.Replace('/', Path.DirectorySeparatorChar);
			resolved = Path.GetFullPath(Path.Combine(jsonDirectory, nativePath));
		}

		if (!File.Exists(resolved))
		{
			// TODO: expose a UI resolution workflow that lets the user locate
			// missing/foreign assets and then navigate directly to the offending item.
			throw new FileNotFoundException(
				$"External asset '{storedPath}' could not be resolved while loading the .hm.json document.",
				resolved);
		}

		return resolved;
	}

	internal static bool LooksAbsoluteStoredPath(string path)
	{
		if (path.StartsWith("/", StringComparison.Ordinal))
			return true;

		int slash = path.IndexOfAny('/', '\\');
		int colon = path.IndexOf(':');
		return colon >= 0 && (slash < 0 || colon < slash);
	}

	public static JsonAssetPathMode DetectPathMode(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		JsonNode? parsed = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
		if (parsed is not JsonObject root)
			throw new InvalidDataException("The Heresy document root must be a JSON object.");

		JsonObject objects = RequiredObject(root, "objects");
		foreach ((_, JsonNode? node) in objects)
		{
			if (node is not JsonObject songObject
				|| !string.Equals(
					RequiredString(songObject, "type"),
					"sample",
					StringComparison.Ordinal))
			{
				continue;
			}

			JsonObject asset = RequiredObject(songObject, "asset");
			if (LooksAbsoluteStoredPath(RequiredString(asset, "path")))
				return JsonAssetPathMode.Absolute;
		}

		return JsonAssetPathMode.Relative;
	}

	private static void WritePatternCommon(
		JsonObject result,
		PatternDefinition pattern)
	{
		result["rowCount"] = pattern.RowCount;
		result["channelCount"] = pattern.ChannelCount;
		result["minorHighlightRows"] = pattern.MinorHighlightRows;
		result["majorHighlightRows"] = pattern.MajorHighlightRows;
	}

	private static void ReadPatternCommon(
		JsonObject node,
		PatternDefinition pattern)
	{
		pattern.RowCount = RequiredInt32(node, "rowCount");
		pattern.ChannelCount = RequiredInt32(node, "channelCount");
		pattern.MinorHighlightRows =
			RequiredInt32(node, "minorHighlightRows");
		pattern.MajorHighlightRows =
			RequiredInt32(node, "majorHighlightRows");
	}

	private static JsonObject WriteTreeNode(
		SongTreeNode node)
	{
		switch (node)
		{
			case SongTreeFolder folder:
				{
					JsonArray children = [];
					foreach (SongTreeNode child in folder.Children)
						children.Add(WriteTreeNode(child));

					return new JsonObject
					{
						["type"] = "folder",
						["name"] = folder.Name,
						["children"] = children,
					};
				}

			case SongTreeObject songObject:
				return new JsonObject
				{
					["type"] = "object",
					["name"] = songObject.Name,
					["objectId"] = songObject.ObjectId.Value,
				};

			default:
				throw new NotSupportedException(
					$"Song-tree node type {node.GetType().FullName} is not supported.");
		}
	}

	private static SongTreeNode ReadTreeNode(
		JsonObject node)
	{
		string type = RequiredString(node, "type");
		string name = RequiredString(node, "name");

		switch (type)
		{
			case "folder":
				{
					SongTreeFolder folder = new(name);
					foreach (JsonNode? childNode in
						RequiredArray(node, "children"))
					{
						if (childNode is not JsonObject child)
							throw new InvalidDataException("Tree children must be JSON objects.");

						folder.Children.Add(
							ReadTreeNode(child));
					}
					return folder;
				}

			case "object":
				return new SongTreeObject(
					name,
					new ObjectId(
						RequiredUInt32(
							node,
							"objectId")));

			default:
				throw new NotSupportedException(
					$"Persisted tree-node type '{type}' is not supported.");
		}
	}

	private static JsonSerializerOptions CreateJsonOptions()
	{
		DefaultJsonTypeInfoResolver resolver = new();
		resolver.Modifiers.Add(
			typeInfo =>
			{
				if (typeInfo.Type == typeof(PatternEffect))
				ConfigurePatternEffectPolymorphism(typeInfo);
				else if (typeInfo.Type == typeof(PatternNoteEntry))
					ConfigurePatternNotePolymorphism(typeInfo);
			});

		JsonSerializerOptions options =
			new()
			{
				PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
				PropertyNameCaseInsensitive = true,
				WriteIndented = true,
				TypeInfoResolver = resolver,
			};

		options.Converters.Add(
			new JsonStringEnumConverter(
				JsonNamingPolicy.CamelCase));
		options.Converters.Add(
			new ObjectIdJsonConverter());

		return options;
	}

	private static void ConfigurePatternEffectPolymorphism(
		JsonTypeInfo typeInfo)
	{
		JsonPolymorphismOptions polymorphism =
			new()
			{
				TypeDiscriminatorPropertyName = "type",
				IgnoreUnrecognizedTypeDiscriminators = false,
				UnknownDerivedTypeHandling =
					JsonUnknownDerivedTypeHandling.FailSerialization,
			};

		foreach (Type type in typeof(PatternEffect).Assembly
			.GetTypes()
			.Where(type =>
				type.IsClass
				&& !type.IsAbstract
				&& typeof(PatternEffect).IsAssignableFrom(type))
			.OrderBy(type => type.FullName))
		{
			polymorphism.DerivedTypes.Add(
				new JsonDerivedType(
					type,
					GetPatternEffectTypeName(type)));
		}

		typeInfo.PolymorphismOptions = polymorphism;
	}

	private static void ConfigurePatternNotePolymorphism(
		JsonTypeInfo typeInfo)
	{
		JsonPolymorphismOptions polymorphism =
			new()
			{
				TypeDiscriminatorPropertyName = "type",
			};
		polymorphism.DerivedTypes.Add(
			new JsonDerivedType(
				typeof(StartPatternNote),
				"start"));
		polymorphism.DerivedTypes.Add(
			new JsonDerivedType(
				typeof(PatternNoteOff),
				"off"));
		polymorphism.DerivedTypes.Add(
			new JsonDerivedType(
				typeof(PatternNoteCut),
				"cut"));
		typeInfo.PolymorphismOptions = polymorphism;
	}

	private static string GetPatternEffectTypeName(Type type)
	{
		const string suffix = "PatternEffect";
		string name = type.Name.EndsWith(
			suffix,
			StringComparison.Ordinal)
			? type.Name[..^suffix.Length]
			: type.Name;

		return char.ToLowerInvariant(name[0]) + name[1..];
	}

	private static ObjectId ParseObjectIdKey(string key)
	{
		if (!uint.TryParse(
			key,
			NumberStyles.None,
			CultureInfo.InvariantCulture,
			out uint value))
		{
			throw new InvalidDataException(
				$"Object-table key '{key}' is not an unsigned integer ID.");
		}

		return new ObjectId(value);
	}

	private static string ToJsonName(
		SongObjectKind kind)
		=> kind switch
		{
			SongObjectKind.Sample => "sample",
			SongObjectKind.Instrument => "instrument",
			SongObjectKind.Pattern => "pattern",
			SongObjectKind.Sequence => "sequence",
			SongObjectKind.Envelope => "envelope",
			SongObjectKind.Unknown => "unknown",
			_ => throw new NotSupportedException(
				$"Unsupported song-object kind {kind}."),
		};

	private static SongObjectKind ParseSongObjectKind(
		string value)
		=> value switch
		{
			"sample" => SongObjectKind.Sample,
			"instrument" => SongObjectKind.Instrument,
			"pattern" => SongObjectKind.Pattern,
			"sequence" => SongObjectKind.Sequence,
			"envelope" => SongObjectKind.Envelope,
			"unknown" => SongObjectKind.Unknown,
			_ => throw new InvalidDataException(
				$"Unknown tombstone object kind '{value}'."),
		};

	private static JsonObject RequiredObject(
		JsonObject node,
		string name)
		=> node[name] as JsonObject
			?? throw new InvalidDataException(
				$"Required JSON object '{name}' is missing.");

	private static JsonArray RequiredArray(
		JsonObject node,
		string name)
		=> node[name] as JsonArray
			?? throw new InvalidDataException(
				$"Required JSON array '{name}' is missing.");

	private static string RequiredString(
		JsonObject node,
		string name)
		=> node[name]?.GetValue<string>()
			?? throw new InvalidDataException(
				$"Required string '{name}' is missing.");

	private static int RequiredInt32(
		JsonObject node,
		string name)
		=> node[name]?.GetValue<int>()
			?? throw new InvalidDataException(
				$"Required integer '{name}' is missing.");

	private static uint RequiredUInt32(
		JsonObject node,
		string name)
		=> node[name]?.GetValue<uint>()
			?? throw new InvalidDataException(
				$"Required unsigned integer '{name}' is missing.");

	private static double RequiredDouble(
		JsonObject node,
		string name)
		=> node[name]?.GetValue<double>()
			?? throw new InvalidDataException(
				$"Required number '{name}' is missing.");

	private static float RequiredSingle(
		JsonObject node,
		string name)
		=> node[name]?.GetValue<float>()
			?? throw new InvalidDataException(
				$"Required number '{name}' is missing.");

	private static T DeserializeRequired<T>(
		JsonObject node,
		string name)
	{
		JsonNode value =
			node[name]
			?? throw new InvalidDataException(
				$"Required value '{name}' is missing.");

		return value.Deserialize<T>(JsonOptions)
			?? throw new InvalidDataException(
				$"Value '{name}' could not be deserialized as {typeof(T).Name}.");
	}

	private sealed class ObjectIdJsonConverter
		: JsonConverter<ObjectId>
	{
		public override ObjectId Read(
			ref Utf8JsonReader reader,
			Type typeToConvert,
			JsonSerializerOptions options)
			=> new(reader.GetUInt32());

		public override void Write(
			Utf8JsonWriter writer,
			ObjectId value,
			JsonSerializerOptions options)
			=> writer.WriteNumberValue(value.Value);
	}
}
