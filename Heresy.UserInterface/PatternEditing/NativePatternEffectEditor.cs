using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Heresy.Core.Patterns;
using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Compilation;
using Heresy.Scripting.Analysis;

namespace Heresy.UserInterface.PatternEditing;

public sealed record NativePatternEffectField(
	string Key,
	string Label,
	string Value,
	IReadOnlyList<string>? Choices = null,
	bool Multiline = false);

public sealed record NativePatternEffectEditModel(
	string Title,
	IReadOnlyList<NativePatternEffectField> Fields);

public enum NativePatternEffectKind
{
	Tempo,
	Speed,
	NoteVolume,
	OverallChannelVolume,
	PlaybackFrequency,
	PlaybackOffset,
	ResonantFilter,
	PitchSlide,
	NoteVolumeSlide,
	VolumeColumnCompatibility,
	VolumeColumnPanning,
	Parametric,
	ChannelFilterSelect,
	NoteFilterSelect,
	ChannelFilterAssignments,
	NoteFilterAssignments,
}

public sealed record NativePatternEffectChoice(
	NativePatternEffectKind Kind,
	string DisplayName)
{
	public override string ToString() => DisplayName;
}

/// <summary>
/// Framework-independent projection and reconstruction of native (non tracker
/// notation) pattern effects. The Avalonia dialog only renders these fields;
/// semantic validation remains in the Core effect constructors.
/// </summary>
public static class NativePatternEffectEditor
{
	private static readonly NativePatternEffectChoice[] CreationChoices =
	[
		new(NativePatternEffectKind.Tempo, "Tempo"),
		new(NativePatternEffectKind.Speed, "Speed"),
		new(NativePatternEffectKind.NoteVolume, "Note volume"),
		new(NativePatternEffectKind.OverallChannelVolume, "Overall channel volume"),
		new(NativePatternEffectKind.PlaybackFrequency, "Playback frequency"),
		new(NativePatternEffectKind.PlaybackOffset, "Playback offset"),
		new(NativePatternEffectKind.ResonantFilter, "Resonant filter"),
		new(NativePatternEffectKind.PitchSlide, "Pitch slide"),
		new(NativePatternEffectKind.NoteVolumeSlide, "Note-volume slide"),
		new(NativePatternEffectKind.VolumeColumnCompatibility, "Volume-column compatibility"),
		new(NativePatternEffectKind.VolumeColumnPanning, "Volume-column panning"),
		new(NativePatternEffectKind.Parametric, "Parametric expression"),
		new(NativePatternEffectKind.ChannelFilterSelect,
			"Channel Filter — select/clear"),
		new(NativePatternEffectKind.NoteFilterSelect,
			"Note Filter — select/clear"),
		new(NativePatternEffectKind.ChannelFilterAssignments,
			"Channel Filter stage assignments"),
		new(NativePatternEffectKind.NoteFilterAssignments,
			"Note Filter stage assignments"),
	];

	public static NativePatternEffectChoice[] GetCreationChoices()
		=> [.. CreationChoices];

	public static PatternEffect CreateDefault(
		NativePatternEffectKind kind)
		=> kind switch
		{
			NativePatternEffectKind.Tempo =>
				new SetTempoPatternEffect(24.0),
			NativePatternEffectKind.Speed =>
				new SetSpeedPatternEffect(6),
			NativePatternEffectKind.NoteVolume =>
				new SetNoteVolumePatternEffect(1.0),
			NativePatternEffectKind.OverallChannelVolume =>
				new SetOverallChannelVolumePatternEffect(1.0),
			NativePatternEffectKind.PlaybackFrequency =>
				new SetPlaybackFrequencyPatternEffect(440.0),
			NativePatternEffectKind.PlaybackOffset =>
				new SetPlaybackOffsetPatternEffect(TimeSpan.Zero),
			NativePatternEffectKind.ResonantFilter =>
				new SetResonantFilterPatternEffect(1.0, 0.0),
			NativePatternEffectKind.PitchSlide =>
				new PitchSlidePatternEffect(0.0),
			NativePatternEffectKind.NoteVolumeSlide =>
				new NoteVolumeSlidePatternEffect(0.0),
			NativePatternEffectKind.VolumeColumnCompatibility =>
				new TrackerVolumeColumnPatternEffect(
					TrackerVolumeColumnEffectKind.FineVolumeUp,
					0),
			NativePatternEffectKind.ChannelFilterSelect =>
				new SetChannelFilterPatternEffect(),
			NativePatternEffectKind.NoteFilterSelect =>
				new SetNoteFilterPatternEffect(),
			NativePatternEffectKind.ChannelFilterAssignments =>
				new SetChannelFilterAssignmentsPatternEffect(""),
			NativePatternEffectKind.NoteFilterAssignments =>
				new SetNoteFilterAssignmentsPatternEffect(""),
			NativePatternEffectKind.Parametric =>
				new ParametricPatternEffect(
					ParametricEffectParameter.OverallChannelVolume,
					TimeSpan.FromSeconds(0.1), "0.0"),
			NativePatternEffectKind.VolumeColumnPanning =>
				new TrackerVolumeColumnPanningPatternEffect(32),
			_ => throw new ArgumentOutOfRangeException(nameof(kind)),
		};

	public static bool CanEdit(PatternEffect effect)
	{
		ArgumentNullException.ThrowIfNull(effect);
		return effect is
			SetTempoPatternEffect
			or SetSpeedPatternEffect
			or SetNoteVolumePatternEffect
			or SetOverallChannelVolumePatternEffect
			or SetPlaybackFrequencyPatternEffect
			or SetPlaybackOffsetPatternEffect
			or SetResonantFilterPatternEffect
			or PitchSlidePatternEffect
			or NoteVolumeSlidePatternEffect
			or TrackerVolumeColumnPatternEffect
			or TrackerVolumeColumnPanningPatternEffect
			or ParametricPatternEffect
			or SetChannelFilterAssignmentsPatternEffect
			or SetNoteFilterAssignmentsPatternEffect
			or SetChannelFilterPatternEffect
			or SetNoteFilterPatternEffect;
	}

	public static NativePatternEffectEditModel Describe(
		PatternEffect effect,
		CultureInfo? culture = null)
	{
		ArgumentNullException.ThrowIfNull(effect);
		culture ??= CultureInfo.CurrentCulture;

		string Format(double value) =>
			value.ToString("G17", culture);
		string FormatInt(int value) =>
			value.ToString(culture);

		return effect switch
		{
			SetChannelFilterPatternEffect selected =>
				Model("Channel Filter selection",
					Field("filterId", "Filter definition ID (empty clears)",
						selected.FilterDefinitionId?.ToString() ?? "")),
			SetNoteFilterPatternEffect selected =>
				Model("Note Filter selection",
					Field("filterId", "Filter definition ID (empty clears)",
						selected.FilterDefinitionId?.ToString() ?? "")),
			SetChannelFilterAssignmentsPatternEffect text =>
				FilterAssignmentModel("Channel Filter assignments",
					text.AssignmentText, text.AuthoringFilterId),
			SetNoteFilterAssignmentsPatternEffect text =>
				FilterAssignmentModel("Note Filter assignments",
					text.AssignmentText, text.AuthoringFilterId),
			ParametricPatternEffect value =>
				Model("Native parametric expression",
					new NativePatternEffectField(
						"parameter", "Destination",
						value.Parameter.ToString(),
						Enum.GetNames<ParametricEffectParameter>()),
					Field("duration", "Wall duration (seconds)",
						Format(value.Duration.TotalSeconds)),
					Field("expression", "C# delta expression (elapsedSeconds, rowTime)",
						value.Expression),
					new NativePatternEffectField(
						"commit", "Commit on expiry",
						value.CommitOnExpire ? "True" : "False",
						new[] { "False", "True" })),
			SetTempoPatternEffect value =>
				Model(
					"Tempo",
					Field(
						"ticksPerDiachron",
						"Ticks per diachron",
						Format(value.TicksPerDiachron))),

			SetSpeedPatternEffect value =>
				Model(
					"Speed",
					Field(
						"ticksPerRow",
						"Ticks per row",
						FormatInt(value.TicksPerRow))),

			SetNoteVolumePatternEffect value =>
				Model(
					"Note volume",
					Field(
						"volume",
						"Volume",
						Format(value.Volume))),

			SetOverallChannelVolumePatternEffect value =>
				Model(
					"Overall channel volume",
					Field(
						"volume",
						"Volume",
						Format(value.Volume))),

			SetPlaybackFrequencyPatternEffect value =>
				Model(
					"Playback frequency",
					Field(
						"frequency",
						"Frequency (Hz)",
						Format(value.Frequency))),

			SetPlaybackOffsetPatternEffect value =>
				Model(
					"Playback offset",
					Field(
						"seconds",
						"Offset (seconds)",
						Format(value.Offset.TotalSeconds))),

			SetResonantFilterPatternEffect value =>
				Model(
					"Resonant filter",
					Field(
						"cutoff",
						"Cutoff (0..1)",
						Format(value.Cutoff)),
					Field(
						"resonance",
						"Resonance (0..1)",
						Format(value.Resonance))),

			PitchSlidePatternEffect value =>
				Model(
					"Pitch slide",
					Field(
						"linearUnitsPerTick",
						"Linear units per tick",
						Format(value.LinearUnitsPerTick))),

			NoteVolumeSlidePatternEffect value =>
				Model(
					"Note-volume slide",
					Field(
						"trackerUnitsPerTick",
						"Tracker units per tick",
						Format(value.TrackerUnitsPerTick))),

			TrackerVolumeColumnPatternEffect value =>
				Model(
					"Volume-column compatibility effect",
					new NativePatternEffectField(
						"kind",
						"Operation",
						value.Kind.ToString(),
						Enum.GetNames<TrackerVolumeColumnEffectKind>()),
					Field(
						"parameter",
						"Parameter (0..9)",
						FormatInt(value.Parameter))),

			TrackerVolumeColumnPanningPatternEffect value =>
				Model(
					"Volume-column panning",
					Field(
						"value",
						"Panning (0..64)",
						FormatInt(value.Value))),

			_ => throw new NotSupportedException(
				$"Pattern effect {effect.GetType().Name} is not a native effect supported by the parameter editor."),
		};
	}

	public static PatternEffect Rebuild(
		PatternEffect original,
		IReadOnlyDictionary<string, string> values,
		CultureInfo? culture = null,
		SongDocument? song = null,
		bool allowInvalidFilterAssignments = false)
	{
		ArgumentNullException.ThrowIfNull(original);
		ArgumentNullException.ThrowIfNull(values);
		culture ??= CultureInfo.CurrentCulture;

		return original switch
		{
			SetChannelFilterPatternEffect =>
				new SetChannelFilterPatternEffect(ParseSelectionId(values, song)),
			SetNoteFilterPatternEffect =>
				new SetNoteFilterPatternEffect(ParseSelectionId(values, song)),
			SetChannelFilterAssignmentsPatternEffect =>
				RebuildFilterAssignments(true, values, song, allowInvalidFilterAssignments),
			SetNoteFilterAssignmentsPatternEffect =>
				RebuildFilterAssignments(false, values, song, allowInvalidFilterAssignments),
			ParametricPatternEffect =>
				RebuildParametric(values, culture),
			SetTempoPatternEffect =>
				new SetTempoPatternEffect(
					ParseDouble(
						values,
						"ticksPerDiachron",
						"Ticks per diachron",
						culture)),

			SetSpeedPatternEffect =>
				new SetSpeedPatternEffect(
					ParseInt(
						values,
						"ticksPerRow",
						"Ticks per row",
						culture)),

			SetNoteVolumePatternEffect =>
				new SetNoteVolumePatternEffect(
					ParseDouble(
						values,
						"volume",
						"Volume",
						culture)),

			SetOverallChannelVolumePatternEffect =>
				new SetOverallChannelVolumePatternEffect(
					ParseDouble(
						values,
						"volume",
						"Volume",
						culture)),

			SetPlaybackFrequencyPatternEffect =>
				new SetPlaybackFrequencyPatternEffect(
					ParseDouble(
						values,
						"frequency",
						"Frequency",
						culture)),

			SetPlaybackOffsetPatternEffect =>
				new SetPlaybackOffsetPatternEffect(
					TimeSpan.FromSeconds(
						ParseDouble(
							values,
							"seconds",
							"Offset",
							culture))),

			SetResonantFilterPatternEffect =>
				new SetResonantFilterPatternEffect(
					ParseDouble(
						values,
						"cutoff",
						"Cutoff",
						culture),
					ParseDouble(
						values,
						"resonance",
						"Resonance",
						culture)),

			PitchSlidePatternEffect =>
				new PitchSlidePatternEffect(
					ParseDouble(
						values,
						"linearUnitsPerTick",
						"Linear units per tick",
						culture)),

			NoteVolumeSlidePatternEffect =>
				new NoteVolumeSlidePatternEffect(
					ParseDouble(
						values,
						"trackerUnitsPerTick",
						"Tracker units per tick",
						culture)),

			TrackerVolumeColumnPatternEffect =>
				new TrackerVolumeColumnPatternEffect(
					ParseEnum<TrackerVolumeColumnEffectKind>(
						values,
						"kind",
						"Operation"),
					ParseByte(
						values,
						"parameter",
						"Parameter",
						culture)),

			TrackerVolumeColumnPanningPatternEffect =>
				new TrackerVolumeColumnPanningPatternEffect(
					ParseByte(
						values,
						"value",
						"Panning",
						culture)),

			_ => throw new NotSupportedException(
				$"Pattern effect {original.GetType().Name} is not a native effect supported by the parameter editor."),
		};
	}

	private static ObjectId? ParseSelectionId(
		IReadOnlyDictionary<string, string> values, SongDocument? document)
	{
		string text = Get(values, "filterId", "Filter definition ID").Trim();
		if (text.Length == 0) return null;
		if (!uint.TryParse(text, NumberStyles.None,
			CultureInfo.InvariantCulture, out uint id) || id == 0)
			throw new ArgumentException("Filter selection requires a positive object ID.");
		if (document is not null
			&& (!document.TryGet((ObjectId)id, out SongObject? obj)
				|| obj is not FilterDefinition))
			throw new ArgumentException("Filter selection requires an existing shared Filter.");
		return (ObjectId)id;
	}

	private static NativePatternEffectEditModel FilterAssignmentModel(
		string title, string text, ObjectId? id)
		=> Model(title,
			Field("filterId", "Filter definition ID (assistant)",
				id?.ToString() ?? ""),
			new NativePatternEffectField("assignments",
				"Stage.parameter = value", text, Multiline: true));

	private static PatternEffect RebuildFilterAssignments(
		bool channel, IReadOnlyDictionary<string, string> values,
		SongDocument? song, bool allowInvalid)
	{
		string source = Get(values, "assignments", "Assignments");
		string idText = Get(values, "filterId", "Authoring Filter");
		ObjectId? id = null;
		if (!string.IsNullOrWhiteSpace(idText))
		{
			if (!uint.TryParse(idText, NumberStyles.None,
				CultureInfo.InvariantCulture, out uint parsed) || parsed == 0)
				throw new ArgumentException("Filter assistant ID must be a valid positive object ID.");
			id = (ObjectId)parsed;
		}
		if (string.IsNullOrWhiteSpace(source))
			throw new ArgumentException("At least one stage assignment is required.");
		if (song is not null)
		{
			if (id is not ObjectId selected ||
				!song.TryGet(selected, out SongObject? entry) ||
				entry is not FilterDefinition definition)
				throw new ArgumentException(
					"Choose an existing shared Filter definition to validate these assignments.");
			if (!allowInvalid && FilterAssignmentEnvelopeText.ParseAndValidate(
				new FilterInstance(definition), source,
				FilterAssignmentEnvelopeNames.Capture(song)).Count == 0)
				throw new ArgumentException("At least one stage assignment is required.");
		}
		return channel
			? new SetChannelFilterAssignmentsPatternEffect(source, id)
			: new SetNoteFilterAssignmentsPatternEffect(source, id);
	}

	private static ParametricPatternEffect RebuildParametric(
		IReadOnlyDictionary<string, string> values, CultureInfo culture)
	{
		string source = Get(values, "expression", "Expression");
		var diagnostics = ScriptCompiler.AnalyzeParametricSource(source);
		if (diagnostics.Any(d => d.Severity == ScriptDiagnosticSeverity.Error))
			throw new ArgumentException(
				string.Join("; ", diagnostics.Select(d => d.Message)),
				nameof(values));
		double duration = ParseDouble(values, "duration", "Duration", culture);
		if (!double.IsFinite(duration) || duration < 0.0)
			throw new ArgumentOutOfRangeException(nameof(values),
				"Duration must be finite and nonnegative.");
		return new ParametricPatternEffect(
			ParseEnum<ParametricEffectParameter>(values, "parameter", "Destination"),
			TimeSpan.FromSeconds(duration), source,
			bool.Parse(Get(values, "commit", "Commit")));
	}

	private static NativePatternEffectEditModel Model(
		string title,
		params NativePatternEffectField[] fields)
		=> new(title, fields);

	private static NativePatternEffectField Field(
		string key,
		string label,
		string value)
		=> new(key, label, value);

	private static string Get(
		IReadOnlyDictionary<string, string> values,
		string key,
		string label)
	{
		if (!values.TryGetValue(key, out string? text))
			throw new ArgumentException($"{label} is missing.", nameof(values));
		return text;
	}

	private static double ParseDouble(
		IReadOnlyDictionary<string, string> values,
		string key,
		string label,
		CultureInfo culture)
	{
		string text = Get(values, key, label);
		if (!double.TryParse(
			text,
			NumberStyles.Float,
			culture,
			out double value))
		{
			throw new ArgumentException(
				$"{label} is not a valid number.",
				nameof(values));
		}
		return value;
	}

	private static int ParseInt(
		IReadOnlyDictionary<string, string> values,
		string key,
		string label,
		CultureInfo culture)
	{
		string text = Get(values, key, label);
		if (!int.TryParse(
			text,
			NumberStyles.Integer,
			culture,
			out int value))
		{
			throw new ArgumentException(
				$"{label} is not a valid integer.",
				nameof(values));
		}
		return value;
	}

	private static byte ParseByte(
		IReadOnlyDictionary<string, string> values,
		string key,
		string label,
		CultureInfo culture)
	{
		string text = Get(values, key, label);
		if (!byte.TryParse(
			text,
			NumberStyles.Integer,
			culture,
			out byte value))
		{
			throw new ArgumentException(
				$"{label} is not a valid byte value.",
				nameof(values));
		}
		return value;
	}

	private static T ParseEnum<T>(
		IReadOnlyDictionary<string, string> values,
		string key,
		string label)
		where T : struct, Enum
	{
		string text = Get(values, key, label);
		if (!Enum.TryParse(text, ignoreCase: true, out T value)
			|| !Enum.IsDefined(value))
		{
			throw new ArgumentException(
				$"{label} is not a valid {typeof(T).Name} value.",
				nameof(values));
		}
		return value;
	}
}
