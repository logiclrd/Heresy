using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

using Heresy.Core.Filters;
using Heresy.Core.Objects;

namespace Heresy.Core.Persistence;

/// <summary>
/// Version-one, ID-based serialization of shared Filter definitions and
/// configured uses. Processing histories are intentionally never serialized.
/// The documented instance/chain codec can be used by tone, FM and native
/// command persistence as those authoring surfaces are introduced.
/// </summary>
internal static class FilterJson
{
	internal static JsonObject WriteDefinition(FilterDefinition definition)
	{
		JsonArray stages = [];
		foreach (FilterStageDefinition stage in definition.Stages)
		{
			stages.Add(new JsonObject
			{
				["stageId"] = stage.StageId,
				["name"] = stage.Name,
				["kind"] = stage.Kind.ToString(),
				["defaults"] = WriteValues(stage.DefaultParameters),
			});
		}

		JsonArray generations = [];
		foreach ((int id, long generation) in
			definition.StageGenerationsForPersistence.OrderBy(pair => pair.Key))
		{
			generations.Add(new JsonObject
			{
				["stageId"] = id,
				["generation"] = generation,
			});
		}

		return new JsonObject
		{
			["stages"] = stages,
			["nextStageId"] = definition.NextStageIdForPersistence,
			["stageGenerations"] = generations,
		};
	}

	internal static FilterDefinition ReadDefinition(
		ObjectId id, string name, JsonObject node)
		=> Decode(() =>
		{
			List<FilterStageDefinition> stages = [];
			foreach (JsonNode? raw in RequireArray(node, "stages"))
			{
				JsonObject stage = RequireObject(raw, "Filter stage");
				FilterKind kind = ReadKind(stage, "kind");
				stages.Add(new FilterStageDefinition(
					RequireInt(stage, "stageId"),
					RequireString(stage, "name"),
					kind,
					ReadValues(RequireObject(stage, "defaults"), kind)));
			}

			FilterDefinition definition = new(id, name, stages);
			Dictionary<int, long> generations = [];
			foreach (JsonNode? raw in RequireArray(node, "stageGenerations"))
			{
				JsonObject item = RequireObject(raw, "Stage generation");
				if (!generations.TryAdd(
					RequireInt(item, "stageId"),
					RequireLong(item, "generation")))
					throw new InvalidDataException(
						"Filter stage-generation IDs may not repeat.");
			}
			definition.RestoreStageIdentity(
				RequireInt(node, "nextStageId"), generations);
			return definition;
		}, "Filter definition");

	internal static JsonObject WriteInstance(FilterInstance instance)
	{
		JsonArray overrides = [];
		foreach (ConfiguredFilterStage stage in instance.GetStages())
		{
			IReadOnlyDictionary<string, FilterParameterValue> values =
				instance.GetOverrides(stage.StageId);
			if (values.Count == 0)
				continue;
			overrides.Add(new JsonObject
			{
				["stageId"] = stage.StageId,
				["kind"] = stage.Kind.ToString(),
				["generation"] = instance.GetStageGenerationForPersistence(stage.StageId),
				["parameters"] = WriteValues(values),
			});
		}
		return new JsonObject
		{
			["definitionId"] = instance.DefinitionId.Value,
			["overrides"] = overrides,
		};
	}

	internal static FilterInstance ReadInstance(
		JsonObject node,
		SongDocument document,
		IReadOnlyDictionary<ObjectId, ObjectId>? idRemap = null)
		=> Decode(() =>
		{
			ObjectId id = new(RequireUInt(node, "definitionId"));
			if (idRemap?.TryGetValue(id, out ObjectId remappedId) == true)
				id = remappedId;
			if (!document.TryGet(id, out SongObject? songObject)
				|| songObject is not FilterDefinition definition)
			{
				throw new InvalidDataException(
					$"Filter configuration references missing or invalid Filter definition {id.Value}.");
			}

			FilterInstance instance = new(definition);
			HashSet<int> seen = [];
			foreach (JsonNode? raw in RequireArray(node, "overrides"))
			{
				JsonObject item = RequireObject(raw, "Filter overrides");
				int stageId = RequireInt(item, "stageId");
				if (!seen.Add(stageId))
					throw new InvalidDataException(
						$"Filter stage {stageId} has duplicate override blocks.");
				FilterStageDefinition stage = definition.GetStage(stageId);
				if (ReadKind(item, "kind") != stage.Kind
					|| RequireLong(item, "generation")
						!= definition.GetStageGeneration(stageId))
				{
					throw new InvalidDataException(
						$"Filter override for stage {stageId} is incompatible with its definition.");
				}
				instance.SetStageParameters(
					stageId,
					ReadValues(RequireObject(item, "parameters"), stage.Kind));
			}
			return instance;
		}, "Filter instance");

	internal static JsonObject WriteChain(FilterChain chain)
	{
		JsonArray layers = [];
		foreach (FilterInstance instance in chain.Layers)
			layers.Add(WriteInstance(instance));
		return new JsonObject { ["layers"] = layers };
	}

	internal static FilterChain ReadChain(
		JsonObject node,
		SongDocument document,
		IReadOnlyDictionary<ObjectId, ObjectId>? idRemap = null)
		=> Decode(() =>
		{
			List<FilterInstance> layers = [];
			foreach (JsonNode? raw in RequireArray(node, "layers"))
			{
				layers.Add(ReadInstance(
					RequireObject(raw, "Filter chain layer"),
					document, idRemap));
			}
			return new FilterChain(layers);
		}, "Filter chain");

	private static JsonObject WriteValues(
		IReadOnlyDictionary<string, FilterParameterValue> parameters)
	{
		JsonObject values = new();
		foreach ((string name, FilterParameterValue value) in
			parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
		{
			values[name] = value.Kind switch
			{
				FilterParameterValueKind.Number => JsonValue.Create(value.NumericValue),
				FilterParameterValueKind.Choice => JsonValue.Create(value.ChoiceValue),
				_ => throw new NotSupportedException(
					$"Unknown filter value type for '{name}'."),
			};
		}
		return values;
	}

	private static IEnumerable<KeyValuePair<string, FilterParameterValue>>
		ReadValues(JsonObject values, FilterKind kind)
	{
		Dictionary<string, FilterParameterDescriptor> descriptors =
			FilterCatalogue.GetParameters(kind).ToDictionary(
				descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase);
		List<KeyValuePair<string, FilterParameterValue>> result = [];
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
		foreach ((string key, JsonNode? raw) in values)
		{
			if (!seen.Add(key))
				throw new InvalidDataException(
					$"Filter parameter '{key}' has duplicate assignments.");
			if (!descriptors.TryGetValue(key, out FilterParameterDescriptor? descriptor))
				throw new InvalidDataException(
					$"Filter type {kind} has no parameter '{key}'.");
			if (raw is null)
				throw new InvalidDataException($"Filter parameter '{key}' may not be null.");
			FilterParameterValue value = descriptor.Kind switch
			{
				FilterParameterValueKind.Number =>
					FilterParameterValue.FromNumber(raw.GetValue<double>()),
				FilterParameterValueKind.Choice =>
					FilterParameterValue.FromChoice(raw.GetValue<string>()),
				_ => throw new InvalidDataException(
					$"Unsupported filter parameter type '{descriptor.Kind}'."),
			};
			result.Add(new KeyValuePair<string, FilterParameterValue>(
				descriptor.Name, descriptor.Normalize(value)));
		}
		return result;
	}

	private static FilterKind ReadKind(JsonObject node, string name)
	{
		string raw = RequireString(node, name);
		if (!Enum.TryParse(raw, ignoreCase: false, out FilterKind kind)
			|| !Enum.IsDefined(kind)
			|| !string.Equals(raw, kind.ToString(), StringComparison.Ordinal))
			throw new InvalidDataException($"Unknown Filter kind '{raw}'.");
		return kind;
	}

	private static JsonObject RequireObject(JsonObject parent, string name)
		=> RequireObject(parent[name], name);

	private static JsonObject RequireObject(JsonNode? node, string name)
		=> node as JsonObject
			?? throw new InvalidDataException($"Missing or invalid Filter object '{name}'.");

	private static JsonArray RequireArray(JsonObject parent, string name)
		=> parent[name] as JsonArray
			?? throw new InvalidDataException($"Missing Filter array '{name}'.");

	private static string RequireString(JsonObject parent, string name)
		=> parent[name]?.GetValue<string>()
			?? throw new InvalidDataException($"Missing Filter text '{name}'.");

	private static int RequireInt(JsonObject parent, string name)
		=> parent[name]?.GetValue<int>()
			?? throw new InvalidDataException($"Missing Filter integer '{name}'.");

	private static uint RequireUInt(JsonObject parent, string name)
		=> parent[name]?.GetValue<uint>()
			?? throw new InvalidDataException($"Missing Filter ID '{name}'.");

	private static long RequireLong(JsonObject parent, string name)
		=> parent[name]?.GetValue<long>()
			?? throw new InvalidDataException($"Missing Filter integer '{name}'.");

	private static T Decode<T>(Func<T> action, string context)
	{
		try
		{
			return action();
		}
		catch (Exception error) when (
			error is ArgumentException
			or InvalidOperationException
			or KeyNotFoundException
			or OverflowException
			or FormatException)
		{
			throw new InvalidDataException($"Invalid persisted {context}: {error.Message}",
				error);
		}
	}
}
