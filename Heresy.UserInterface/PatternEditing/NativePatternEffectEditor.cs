using System;
using System.Collections.Generic;
using System.Globalization;

using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

namespace Heresy.UserInterface.PatternEditing;

public sealed record NativePatternEffectField(
	string Key,
	string Label,
	string Value,
	IReadOnlyList<string>? Choices = null);

public sealed record NativePatternEffectEditModel(
	string Title,
	IReadOnlyList<NativePatternEffectField> Fields);

/// <summary>
/// Framework-independent projection and reconstruction of native (non tracker
/// notation) pattern effects. The Avalonia dialog only renders these fields;
/// semantic validation remains in the Core effect constructors.
/// </summary>
public static class NativePatternEffectEditor
{
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
			or TrackerVolumeColumnPanningPatternEffect;
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
		CultureInfo? culture = null)
	{
		ArgumentNullException.ThrowIfNull(original);
		ArgumentNullException.ThrowIfNull(values);
		culture ??= CultureInfo.CurrentCulture;

		return original switch
		{
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
