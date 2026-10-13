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
using System.Threading.Tasks;

using Heresy.Core.Assets;
using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Filters;
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

	public static string SerializePatternNote(
		PatternNoteEntry note)
	{
		ArgumentNullException.ThrowIfNull(note);
		JsonNode? node =
			JsonSerializer.SerializeToNode(
				note,
				typeof(PatternNoteEntry),
				JsonOptions);
		return (node
			?? throw new InvalidOperationException(
				"Pattern note could not be serialized."))
			.ToJsonString(JsonOptions);
	}

	public static PatternNoteEntry DeserializePatternNote(
		string json)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(json);
		JsonNode? node = JsonNode.Parse(json);
		return node?.Deserialize<PatternNoteEntry>(JsonOptions)
			?? throw new InvalidDataException(
				"Pattern note could not be deserialized.");
	}

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

	/// <summary>Serialize a configured Filter use as a stable definition ID
	/// plus per-stage overrides; no mutable processing state is captured.</summary>
	public static string SerializeFilterInstance(FilterInstance instance)
	{
		ArgumentNullException.ThrowIfNull(instance);
		return FilterJson.WriteInstance(instance).ToJsonString(JsonOptions);
	}

	public static FilterInstance DeserializeFilterInstance(
		string json, SongDocument document,
		IReadOnlyDictionary<ObjectId, ObjectId>? idRemap = null)
	{
		ArgumentNullException.ThrowIfNull(json);
		ArgumentNullException.ThrowIfNull(document);
		return FilterJson.ReadInstance(
			JsonNode.Parse(json) as JsonObject
				?? throw new InvalidDataException("Filter instance must be a JSON object."),
			document, idRemap);
	}

	public static string SerializeFilterChain(FilterChain chain)
	{
		ArgumentNullException.ThrowIfNull(chain);
		return FilterJson.WriteChain(chain).ToJsonString(JsonOptions);
	}

	public static FilterChain DeserializeFilterChain(
		string json, SongDocument document,
		IReadOnlyDictionary<ObjectId, ObjectId>? idRemap = null)
	{
		ArgumentNullException.ThrowIfNull(json);
		ArgumentNullException.ThrowIfNull(document);
		return FilterJson.ReadChain(
			JsonNode.Parse(json) as JsonObject
				?? throw new InvalidDataException("Filter chain must be a JSON object."),
			document, idRemap);
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
		IScriptObjectReferenceAnalyzer? scriptReferenceAnalyzer = null,
		bool pruneUnreferencedTombstones = true)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(assetPathSelector);

		if (pruneUnreferencedTombstones)
		{
			SongReferenceAnalysis referenceAnalysis =
				SongReferenceAnalyzer.Analyze(
					document,
					scriptReferenceAnalyzer);
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
		}

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
		SongDocument document =
			Deserialize(
				json,
				storedPath => ResolveStoredPath(directory, storedPath));
		SampleAssetPersistence.HydratePersistedSamples(document);
		return document;
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

		// Decode shared Filter definitions first: FM Filter uses resolve by
		// stable ObjectId, regardless of the serialized object table order.
		foreach ((string key, JsonNode? value) in objects)
		{
			if (value is JsonObject filterNode
				&& RequiredString(filterNode, "type") == "filter")
			{
				document.RestoreObject(FilterJson.ReadDefinition(
					ParseObjectIdKey(key), RequiredString(filterNode, "name"),
					filterNode));
			}
		}

		foreach ((string key, JsonNode? value) in objects)
		{
			if (value is JsonObject knownNode
				&& RequiredString(knownNode, "type") == "filter")
				continue;
			ObjectId id = ParseObjectIdKey(key);
			if (id.IsNone)
				throw new InvalidDataException("Song objects may not use ObjectId 0.");
			if (value is not JsonObject objectNode)
				throw new InvalidDataException($"Object {key} must be a JSON object.");

			document.RestoreObject(
				ReadSongObject(id, objectNode, assetPathResolver, document));
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
				"The persisted song tree does not match the required six-section or legacy four-/five-section structure.",
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

		List<PendingAssetTransition> pending =
			MaterializePendingAssets(
				directory,
				document);
		try
		{
			SampleAssetPersistence.VerifyPersistedAssetsForSave(document);

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
		catch
		{
			RestorePendingAssets(pending);
			throw;
		}
	}

	public static SongDocument Load(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		string fullPath = Path.GetFullPath(path);
		string directory = Path.GetDirectoryName(fullPath)
			?? throw new ArgumentException("The JSON path must identify a file.", nameof(path));

		SongDocument document =
			Deserialize(
				File.ReadAllText(fullPath, Encoding.UTF8),
				storedPath => ResolveStoredPath(directory, storedPath));
		SampleAssetPersistence.HydratePersistedSamples(document);
		return document;
	}


	/// <summary>
	/// Load into a temporary song and permit an interactive replacement when a
	/// persisted sample cannot be read. Callers publish the result only after
	/// every asset has been resolved and decoded.
	/// </summary>
	public static async Task<SongDocument> LoadWithRecoveryAsync(
		string path,
		Func<SampleAssetRecoveryRequest, Task<string?>> locateAsync)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(locateAsync);
		string fullPath = Path.GetFullPath(path);
		string directory = Path.GetDirectoryName(fullPath)
			?? throw new ArgumentException("The JSON path must identify a file.", nameof(path));
		SongDocument document =
			Deserialize(
				File.ReadAllText(fullPath, Encoding.UTF8),
				storedPath => ResolveStoredPath(
					directory, storedPath, allowMissing: true));
		await SampleAssetPersistence.HydratePersistedSamplesWithRecoveryAsync(
			document, locateAsync);
		return document;
	}

	/// <summary>Persist tone Filter uses with the same typed, generation-checked
	/// instance format as FM nodes. Legacy tones without a Filter field load as
	/// bypass; never ask System.Text.Json to traverse shared definitions.</summary>
	private static JsonArray WriteToneSpecifications(
		IEnumerable<ToneSpecification> tones)
	{
		JsonArray array = [];
		foreach (ToneSpecification tone in tones)
		{
			JsonObject node = JsonSerializer.SerializeToNode(tone, JsonOptions)
				?.AsObject() ?? throw new InvalidDataException(
					"Could not serialize tone specification.");
			if (tone.FilterConfiguration is FilterInstance filter)
				node["filterConfiguration"] = FilterJson.WriteInstance(filter);
			array.Add(node);
		}
		return array;
	}

	private static List<ToneSpecification> ReadToneSpecifications(
		JsonObject owner, SongDocument document)
	{
		List<ToneSpecification> tones = [];
		foreach (JsonNode? raw in RequiredArray(owner, "toneSpecifications"))
		{
			if (raw is not JsonObject node)
				throw new InvalidDataException(
					"Tone specification must be a JSON object.");
			ToneSpecification source = node.Deserialize<ToneSpecification>(JsonOptions)
				?? throw new InvalidDataException("Tone specification is missing.");
			source.Nna.Validate();
            FilterInstance? filter = node["filterConfiguration"] is JsonNode config
				? FilterJson.ReadInstance(config as JsonObject
					?? throw new InvalidDataException(
						"Tone Filter configuration must be a JSON object."),
					document)
				: null;
			tones.Add(new ToneSpecification
			{
				SourceId = source.SourceId,
				PitchMultiplier = source.PitchMultiplier,
				VolumeEnvelopeId = source.VolumeEnvelopeId,
				PitchEnvelopeId = source.PitchEnvelopeId,
				PanningEnvelopeId = source.PanningEnvelopeId,
				FilterEnvelopeId = source.FilterEnvelopeId,
				FilterConfiguration = filter,
                Nna = source.Nna,
            });
		}
		return tones;
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
			case FilterDefinition filter:
				result["type"] = "filter";
				foreach ((string key, JsonNode? value) in FilterJson.WriteDefinition(filter))
					result[key] = value?.DeepClone();
				break;

			case SampleDefinition sample:
				result["type"] = "sample";
				result["asset"] =
					new JsonObject
					{
						["path"] = assetPathSelector(sample),
						["sha256"] =
							sample.Asset?.Sha256
								?? sample.PendingAsset?.Sha256,
					};
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

			case FmSynthDefinition fmSynth:
				result["type"] = "fmSynth";
				result["graph"] =
					WriteFmSynthGraph(fmSynth.Graph);
				result["editorLayout"] =
					WriteFmSynthEditorLayout(fmSynth);
				break;

			case ScriptInstrumentDefinition scripted:
				result["type"] = "scriptInstrument";
				result["source"] = scripted.Source;
				result["pitchExpression"] = scripted.PitchExpression;
                result["nnaExpression"] = scripted.NnaExpression;
                result["nnaFadeDuration"] = JsonSerializer.SerializeToNode(
                    scripted.NnaFadeDuration, JsonOptions);
				result["toneSpecifications"] =
					WriteToneSpecifications(scripted.ToneSpecifications);
				break;

			case InstrumentDefinition instrument:
				result["type"] = "instrument";
				result["divisions"] = instrument.Divisions;
				result["offset"] = instrument.Offset;
                result["nnaOverride"] = JsonSerializer.SerializeToNode(
                    instrument.NnaOverride, JsonOptions);
                result["nnaFadeDuration"] = JsonSerializer.SerializeToNode(
                    instrument.NnaFadeDuration, JsonOptions);
				result["toneSpecifications"] =
					WriteToneSpecifications(instrument.ToneSpecifications);
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

			case ScriptEnvelopeDefinition envelope:
				result["type"] = "scriptEnvelope";
				result["source"] = envelope.Source;
				result["releaseDuration"] =
					envelope.ReleaseDuration.HasValue
						? JsonSerializer.SerializeToNode(
							envelope.ReleaseDuration.Value, JsonOptions)
						: null;
				break;

			case NodeEnvelopeDefinition envelope:
				result["type"] = "nodeEnvelope";
				result["nodes"] = JsonSerializer.SerializeToNode(
					envelope.Nodes, JsonOptions);
				result["vmin"] = envelope.Vmin;
				result["vmax"] = envelope.Vmax;
				result["sustainStartIndex"] = envelope.SustainStartIndex;
				result["sustainEndIndex"] = envelope.SustainEndIndex;
				result["sustainLoopMode"] = JsonSerializer.SerializeToNode(
					envelope.SustainLoopMode, JsonOptions);
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
		Func<string, string> assetPathResolver,
		SongDocument document)
	{
		string type = RequiredString(node, "type");
		string name = RequiredString(node, "name");

		switch (type)
		{
			case "filter":
				return FilterJson.ReadDefinition(id, name, node);

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

			case "fmSynth":
				return ReadFmSynthDefinition(
					id,
					name,
					RequiredObject(node, "graph"),
					RequiredObject(node, "editorLayout"), document);

			case "scriptInstrument":
				{
					ScriptInstrumentDefinition scripted = new(id, name)
					{
						Source = RequiredString(node, "source"),
						PitchExpression = node["pitchExpression"] is JsonNode pitchExpression
							? pitchExpression.GetValue<string>() : "1.0",
                        NnaExpression = node["nnaExpression"] is JsonNode nnaExpression
                            ? nnaExpression.GetValue<string>()
                            : "InstrumentNnaAction.Inherit",
                        NnaFadeDuration = node["nnaFadeDuration"] is JsonNode fade
                            ? fade.Deserialize<TimeSpan>(JsonOptions)
                            : TimeSpan.FromMilliseconds(100),
                    };
                    if (scripted.NnaFadeDuration <= TimeSpan.Zero)
                        throw new InvalidDataException(
                            "Script Instrument NNA Fade duration must be positive.");
					scripted.ToneSpecifications.AddRange(
						ReadToneSpecifications(node, document));
					return scripted;
				}

			case "instrument":
				{
					InstrumentDefinition instrument =
						new(id, name)
						{
							Divisions = RequiredDouble(node, "divisions"),
							Offset = RequiredInt32(node, "offset"),
                            NnaOverride = node["nnaOverride"] is JsonNode overrideNode
                                ? overrideNode.Deserialize<InstrumentNnaOverride>(JsonOptions)
                                : InstrumentNnaOverride.UseToneNna,
                            NnaFadeDuration = node["nnaFadeDuration"] is JsonNode durationNode
                                ? durationNode.Deserialize<TimeSpan>(JsonOptions)
                                : TimeSpan.FromMilliseconds(100),
                        };
                    if (!Enum.IsDefined(instrument.NnaOverride)
                        || instrument.NnaFadeDuration <= TimeSpan.Zero)
                        throw new InvalidDataException("Invalid Instrument NNA override.");

					List<ToneSpecification> tones =
						ReadToneSpecifications(node, document);
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

			case "scriptEnvelope":
				return new ScriptEnvelopeDefinition(id, name)
				{
					Source = RequiredString(node, "source"),
					ReleaseDuration = node["releaseDuration"] is JsonNode release
						? release.Deserialize<TimeSpan>(JsonOptions)
						: null,
				};

			case "nodeEnvelope":
				{
					NodeEnvelopeDefinition envelope = new(id, name);
					envelope.ReplaceCurve(
						DeserializeRequired<List<EnvelopeNode>>(node, "nodes"),
						DeserializeRequired<int>(node, "sustainStartIndex"),
						DeserializeRequired<int>(node, "sustainEndIndex"),
						DeserializeRequired<EnvelopeSustainLoopMode>(
							node, "sustainLoopMode"));
					envelope.SetDisplayRange(
						DeserializeRequired<double>(node, "vmin"),
						DeserializeRequired<double>(node, "vmax"));
					return envelope;
				}

			default:
				throw new NotSupportedException(
					$"Persisted song object type '{type}' is not supported.");
		}
	}


	private static JsonObject WriteFmSynthGraph(
		FmSynthGraph graph)
	{
		JsonArray nodes = [];
		foreach (FmSynthNode node in graph.Nodes)
		{
			JsonObject nodeJson =
				new()
				{
					["id"] = node.Id,
				};

			switch (node)
			{
				case FmConstantNode constant:
					nodeJson["type"] = "constant";
					nodeJson["value"] = constant.Value;
					break;

				case FmNoiseNode:
					nodeJson["type"] = "noise";
					break;

				case FmOscillatorNode oscillator:
					nodeJson["type"] = "oscillator";
					nodeJson["waveform"] =
						JsonSerializer.SerializeToNode(
							oscillator.Waveform,
							JsonOptions);
					nodeJson["frequencyHz"] = oscillator.FrequencyHz;
					nodeJson["minimum"] = oscillator.Minimum;
					nodeJson["maximum"] = oscillator.Maximum;
					if (oscillator.MultiplierNodeId.HasValue)
						nodeJson["multiplierNodeId"] = oscillator.MultiplierNodeId.Value;
					nodeJson["exponentialMultiplier"] = oscillator.ExponentialMultiplier;
					nodeJson["constant"] = oscillator.Constant;
					break;

				case FmSoundSourceNode source:
					nodeJson["type"] = "soundSource";
					nodeJson["sourceId"] = source.SourceId.Value;
					nodeJson["frequencyHz"] = source.FrequencyHz;
					if (source.MultiplierNodeId is int modulation)
						nodeJson["multiplierNodeId"] = modulation;
					nodeJson["exponentialMultiplier"] = source.ExponentialMultiplier;
					nodeJson["constant"] = source.Constant;
					break;

				case FmEnvelopeNode envelope:
					nodeJson["type"] = "envelope";
					nodeJson["envelopeId"] = envelope.EnvelopeId.Value;
					break;

				case FmFilterNode filter:
					nodeJson["type"] = "filter";
					nodeJson["tailSeconds"] = filter.TailSeconds;
					if (filter.AudioInputNodeId is int audio)
						nodeJson["audioInputNodeId"] = audio;
					if (filter.Configuration is FilterInstance config)
						nodeJson["configuration"] = FilterJson.WriteInstance(config);
					JsonArray connections = [];
					foreach (FmFilterParameterConnection wire in filter.Connections)
					{
						connections.Add(new JsonObject
						{
							["stageId"] = wire.StageId,
							["kind"] = wire.Kind.ToString(),
							["generation"] = wire.Generation,
							["parameterName"] = wire.ParameterName,
							["sourceNodeId"] = wire.SourceNodeId,
						});
					}
					nodeJson["parameterConnections"] = connections;
					break;

				case FmOperatorNode op:
					nodeJson["type"] = "operator";
					nodeJson["operation"] =
						JsonSerializer.SerializeToNode(
							op.Operation,
							JsonOptions);
					nodeJson["inputNodeIds"] =
						JsonSerializer.SerializeToNode(
							op.InputNodeIds,
							JsonOptions);
					break;

				default:
					throw new NotSupportedException(
						$"FM graph node type {node.GetType().FullName} is not supported by JSON persistence.");
			}

			nodes.Add(nodeJson);
		}

		return new JsonObject
		{
			["outputNodeId"] = graph.OutputNodeId,
			["nodes"] = nodes,
		};
	}

	private static JsonObject WriteFmSynthEditorLayout(
		FmSynthDefinition fmSynth)
	{
		JsonArray nodePositions = [];
		foreach (FmSynthNodePosition position in fmSynth.NodePositions)
		{
			nodePositions.Add(
				new JsonObject
				{
					["nodeId"] = position.NodeId,
					["x"] = position.X,
					["y"] = position.Y,
				});
		}

		JsonArray routingHints = [];
		foreach (FmSynthConnectionRoutingHint hint in fmSynth.ConnectionRoutingHints)
		{
			JsonArray routePoints = [];
			foreach (FmSynthRoutePoint point in hint.RoutePoints)
			{
				routePoints.Add(
					new JsonObject
					{
						["x"] = point.X,
						["y"] = point.Y,
					});
			}

			routingHints.Add(
				new JsonObject
				{
					["sourceNodeId"] = hint.SourceNodeId,
					["targetNodeId"] = hint.TargetNodeId,
					["targetInputIndex"] = hint.TargetInputIndex,
					["routePoints"] = routePoints,
				});
		}

		return new JsonObject
		{
			["nodePositions"] = nodePositions,
			["connectionRoutingHints"] = routingHints,
		};
	}

	private static FmFilterNode ReadFmFilterNode(
		int nodeId, JsonObject node, SongDocument document)
	{
		FilterInstance? configuration = node["configuration"] is JsonObject item
			? FilterJson.ReadInstance(item, document) : null;
		List<FmFilterParameterConnection> connections = [];
		foreach (JsonNode? raw in RequiredArray(node, "parameterConnections"))
		{
			if (raw is not JsonObject wire)
				throw new InvalidDataException("FM Filter connections must be objects.");
			string kindText = RequiredString(wire, "kind");
			if (!Enum.TryParse(kindText, false, out FilterKind kind)
				|| !Enum.IsDefined(kind)
				|| kindText != kind.ToString())
				throw new InvalidDataException("FM Filter wire has unknown stage kind.");
			connections.Add(new FmFilterParameterConnection(
				RequiredInt32(wire, "stageId"),
				kind,
				wire["generation"]?.GetValue<long>()
					?? throw new InvalidDataException("Missing FM Filter generation."),
				RequiredString(wire, "parameterName"),
				RequiredInt32(wire, "sourceNodeId")));
		}
		return new FmFilterNode(nodeId,
			node["audioInputNodeId"]?.GetValue<int>(),
			configuration, connections,
			node["tailSeconds"]?.GetValue<double>() ?? 0);
	}

	private static FmSynthDefinition ReadFmSynthDefinition(
		ObjectId id,
		string name,
		JsonObject graphNode,
		JsonObject editorLayout,
		SongDocument document)
	{
		List<FmSynthNode> nodes = [];
		foreach (JsonNode? rawNode in RequiredArray(graphNode, "nodes"))
		{
			if (rawNode is not JsonObject node)
				throw new InvalidDataException("FM graph nodes must be JSON objects.");

			int nodeId = RequiredInt32(node, "id");
			string type = RequiredString(node, "type");
			nodes.Add(
				type switch
				{
					"constant" =>
						new FmConstantNode(
							nodeId,
							RequiredDouble(node, "value")),
					"noise" => new FmNoiseNode(nodeId),
					"oscillator" =>
						new FmOscillatorNode(
							nodeId,
							DeserializeRequired<FmOscillatorWaveform>(
								node,
								"waveform"),
							RequiredDouble(node, "frequencyHz"),
							RequiredDouble(node, "minimum"),
							RequiredDouble(node, "maximum"),
							node["multiplierNodeId"] is JsonNode multiplier
								? multiplier.GetValue<int>()
								: null,
							RequiredBoolean(node, "exponentialMultiplier"),
							node["constant"]?.GetValue<bool>() ?? false),
					"soundSource" =>
						new FmSoundSourceNode(
							nodeId,
							new ObjectId(RequiredUInt32(node, "sourceId")),
							RequiredDouble(node, "frequencyHz"),
							node["multiplierNodeId"] is JsonNode sourceMod
								? sourceMod.GetValue<int>() : null,
							node["exponentialMultiplier"]?.GetValue<bool>() ?? false,
							node["constant"]?.GetValue<bool>() ?? false),
					"envelope" =>
						new FmEnvelopeNode(
							nodeId,
							new ObjectId(
								RequiredUInt32(
									node,
									"envelopeId"))),
					"filter" => ReadFmFilterNode(nodeId, node, document),
					"operator" =>
						new FmOperatorNode(
							nodeId,
							DeserializeRequired<FmOperatorKind>(
								node,
								"operation"),
							DeserializeRequired<List<int>>(
								node,
								"inputNodeIds")),
					_ => throw new InvalidDataException(
						$"Unknown FM graph node type '{type}'."),
				});
		}

		FmSynthDefinition result =
			new(
				id,
				name,
				new FmSynthGraph(
					nodes,
					RequiredInt32(
						graphNode,
						"outputNodeId")));

		foreach (JsonNode? rawPosition in
			RequiredArray(editorLayout, "nodePositions"))
		{
			if (rawPosition is not JsonObject position)
				throw new InvalidDataException("FM node positions must be JSON objects.");

			result.NodePositions.Add(
				new FmSynthNodePosition(
					RequiredInt32(position, "nodeId"),
					RequiredDouble(position, "x"),
					RequiredDouble(position, "y")));
		}

		foreach (JsonNode? rawHint in
			RequiredArray(editorLayout, "connectionRoutingHints"))
		{
			if (rawHint is not JsonObject hint)
				throw new InvalidDataException("FM connection routing hints must be JSON objects.");

			List<FmSynthRoutePoint> routePoints = [];
			foreach (JsonNode? rawPoint in RequiredArray(hint, "routePoints"))
			{
				if (rawPoint is not JsonObject point)
					throw new InvalidDataException("FM routing points must be JSON objects.");
				routePoints.Add(
					new FmSynthRoutePoint(
						RequiredDouble(point, "x"),
						RequiredDouble(point, "y")));
			}

			result.ConnectionRoutingHints.Add(
				new FmSynthConnectionRoutingHint(
					RequiredInt32(hint, "sourceNodeId"),
					RequiredInt32(hint, "targetNodeId"),
					RequiredInt32(hint, "targetInputIndex"),
					routePoints));
		}

		return result;
	}

	private static List<PendingAssetTransition> MaterializePendingAssets(
		string jsonDirectory,
		SongDocument document)
	{
		List<PendingAssetTransition> transitions = [];
		HashSet<string> reserved =
			new(
				OperatingSystem.IsWindows()
					? StringComparer.OrdinalIgnoreCase
					: StringComparer.Ordinal);

		foreach (SampleDefinition sample in
			document.Objects.Values.OfType<SampleDefinition>())
		{
			if (sample.PendingAsset is not PendingSampleAsset pending)
				continue;

			string directory =
				Path.Combine(
					jsonDirectory,
					"pcm");
			Directory.CreateDirectory(directory);
			string destination =
				MakeUniquePendingDestination(
					directory,
					pending.FileName,
					reserved);
			File.WriteAllBytes(
				destination,
				pending.Bytes.ToArray());

			ExternalAssetReference? previousAsset = sample.Asset;
			transitions.Add(
				new PendingAssetTransition(
					sample,
					previousAsset,
					pending));
			sample.MarkAssetPersisted(
				new ExternalAssetReference(
					destination,
					pending.Sha256));
		}

		return transitions;
	}

	private static string MakeUniquePendingDestination(
		string directory,
		string fileName,
		HashSet<string> reserved)
	{
		string safeName =
			Path.GetFileName(fileName);
		if (string.IsNullOrWhiteSpace(safeName))
			safeName = "sample.wav";

		string candidate =
			Path.Combine(
				directory,
				safeName);
		if (!File.Exists(candidate)
			&& reserved.Add(candidate))
		{
			return candidate;
		}

		string extension = Path.GetExtension(safeName);
		string stem =
			safeName[..^extension.Length];
		for (int suffix = 2; ; suffix++)
		{
			candidate =
				Path.Combine(
					directory,
					$"{stem}-{suffix}{extension}");
			if (!File.Exists(candidate)
				&& reserved.Add(candidate))
			{
				return candidate;
			}
		}
	}

	private static void RestorePendingAssets(
		IEnumerable<PendingAssetTransition> transitions)
	{
		foreach (PendingAssetTransition transition in transitions)
		{
			transition.Sample.Asset = transition.PreviousAsset;
			transition.Sample.PendingAsset = transition.PendingAsset;
		}
	}

	private static ExternalAssetReference RequireAsset(
		SampleDefinition sample)
		=> sample.Asset
			?? throw new InvalidOperationException(
				$"Sample '{sample.Name}' ({sample.Id}) has not yet persisted its encoded data.");

	private sealed record PendingAssetTransition(
		SampleDefinition Sample,
		ExternalAssetReference? PreviousAsset,
		PendingSampleAsset PendingAsset);

	private static void MaterializeArchiveAssets(
		string jsonDirectory,
		SongDocument document)
	{
		foreach (SampleDefinition sample in
			document.Objects.Values.OfType<SampleDefinition>())
		{
			if (!HeresyModulePath.TrySplit(
				RequireAsset(sample).FullPath,
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
				ExternalAssetIntegrity.OpenRead(RequireAsset(sample).FullPath))
			using (FileStream target = File.Create(destination))
				source.CopyTo(target);

			sample.Asset = RequireAsset(sample) with
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
				RequireAsset(sample).FullPath,
				out string archivePath,
				out _)
				&& File.Exists(archivePath))
			{
				continue;
			}

			if (!TryGetPortableRelativePath(
				jsonDirectory,
				RequireAsset(sample).FullPath,
				out _))
			{
				throw new InvalidOperationException(
					$"Sample '{sample.Name}' ({sample.Id}) uses asset '{RequireAsset(sample).FullPath}', " +
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
			string fullPath = Path.GetFullPath(RequireAsset(sample).FullPath);
			if (!LooksAbsoluteStoredPath(fullPath))
			{
				throw new InvalidOperationException(
					$"Asset path '{fullPath}' cannot be represented as a supported absolute .hm.json path.");
			}
			return fullPath;
		}

		if (TryGetPortableRelativePath(
			jsonDirectory,
			RequireAsset(sample).FullPath,
			out string? relative))
		{
			return relative;
		}

		throw new InvalidOperationException(
			$"Sample '{sample.Name}' ({sample.Id}) uses asset '{RequireAsset(sample).FullPath}', " +
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
		string storedPath,
		bool allowMissing = false)
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

		if (!allowMissing && !File.Exists(resolved))
		{
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
			SongObjectKind.FmSynth => "fmSynth",
			SongObjectKind.Filter => "filter",
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
			"fmSynth" => SongObjectKind.FmSynth,
			"filter" => SongObjectKind.Filter,
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

	private static bool RequiredBoolean(
		JsonObject node,
		string name)
		=> node[name]?.GetValue<bool>()
			?? throw new InvalidDataException(
				$"Required boolean '{name}' is missing.");

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
