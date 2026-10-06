using System;
using System.Text;

using Heresy.Core.Patterns;
using Heresy.UserInterface.PatternEditing;

namespace Heresy.UserInterface.ViewModels;

public sealed class PatternEffectViewModel
{
	private PatternEffectViewModel(
		PatternEffect effect,
		bool isTrackerStyle,
		char? command,
		byte? parameter,
		string compactText,
		int colorKey)
	{
		Effect = effect;
		IsTrackerStyle = isTrackerStyle;
		Command = command;
		Parameter = parameter;
		CompactText = compactText;
		ColorKey = colorKey;
	}

	public PatternEffect Effect { get; }
	public bool IsTrackerStyle { get; }
	public char? Command { get; }
	public byte? Parameter { get; }
	public string CompactText { get; }
	public int ColorKey { get; }

	public static PatternEffectViewModel Create(PatternEffect effect)
	{
		ArgumentNullException.ThrowIfNull(effect);

		if (PatternEffectCodec.TryDecodeTracker(
			effect,
			out char command,
			out byte parameter))
		{
			return new PatternEffectViewModel(
				effect,
				true,
				command,
				parameter,
				$"{command}{parameter:X2}",
				command - 'A');
		}

		string label = GetNativeLabel(effect);
		return new PatternEffectViewModel(
			effect,
			false,
			null,
			null,
			label,
			StableColorKey(effect.GetType().Name));
	}

	private static string GetNativeLabel(PatternEffect effect)
		=> effect switch
		{
			SetTempoPatternEffect => "Tempo",
			SetSpeedPatternEffect => "Speed",
			SetNoteVolumePatternEffect => "Note vol",
			SetOverallChannelVolumePatternEffect => "Channel vol",
			SetPlaybackFrequencyPatternEffect => "Frequency",
			SetPlaybackOffsetPatternEffect => "Offset",
			SetResonantFilterPatternEffect => "Filter",
			PitchSlidePatternEffect => "Pitch slide",
			NoteVolumeSlidePatternEffect => "Volume slide",
			TrackerVolumeColumnPatternEffect => "Vol column",
			TrackerVolumeColumnPanningPatternEffect => "Vol pan",
			_ => Humanize(effect.GetType().Name),
		};

	private static string Humanize(string typeName)
	{
		const string suffix = "PatternEffect";
		if (typeName.EndsWith(suffix, StringComparison.Ordinal))
			typeName = typeName[..^suffix.Length];

		StringBuilder result = new();
		for (int index = 0; index < typeName.Length; index++)
		{
			char value = typeName[index];
			if (index != 0
				&& char.IsUpper(value)
				&& !char.IsUpper(typeName[index - 1]))
			{
				result.Append(' ');
			}
			result.Append(value);
		}
		return result.ToString();
	}

	private static int StableColorKey(string value)
	{
		unchecked
		{
			int hash = 17;
			foreach (char character in value)
				hash = (hash * 31) + character;
			return hash & int.MaxValue;
		}
	}
}
