using System;
using System.Globalization;
using System.Text;

namespace Heresy.Core.Sequencing;

/// <summary>
/// Impulse Tracker MIDI macro table used by SFx/Zxx compatibility effects.
/// Raw macro text is retained because arbitrary macros may target external MIDI
/// devices. PCM sequencing recognizes IT's internal resonant-filter SysEx and
/// translates only those instructions into audio commands.
/// </summary>
public sealed class TrackerMidiMacroConfiguration
{
	private readonly string?[] _parameterizedMacros = new string?[16];
	private readonly string?[] _fixedMacros = new string?[128];

	public static TrackerMidiMacroConfiguration CreateImpulseTrackerDefault()
	{
		TrackerMidiMacroConfiguration result = new();
		result.SetParameterizedMacro(0, "F0 F0 00 z");

		for (int i = 0; i < 16; i++)
		{
			result.SetFixedMacro(
				(byte)(0x80 + i),
				$"F0 F0 01 {i * 8:X2}");
		}

		return result;
	}

	public void SetParameterizedMacro(int index, string? macro)
	{
		if ((uint)index >= 16U)
			throw new ArgumentOutOfRangeException(nameof(index));
		_parameterizedMacros[index] = macro;
	}

	public string? GetParameterizedMacro(int index)
	{
		if ((uint)index >= 16U)
			throw new ArgumentOutOfRangeException(nameof(index));
		return _parameterizedMacros[index];
	}

	public void SetFixedMacro(byte parameter, string? macro)
	{
		if (parameter < 0x80)
			throw new ArgumentOutOfRangeException(nameof(parameter));
		_fixedMacros[parameter - 0x80] = macro;
	}

	public string? GetFixedMacro(byte parameter)
	{
		if (parameter < 0x80)
			throw new ArgumentOutOfRangeException(nameof(parameter));
		return _fixedMacros[parameter - 0x80];
	}

	internal NoteCommand? ResolvePcmCommand(byte selectedMacro, byte parameter)
	{
		if (selectedMacro > 0x0F)
			throw new ArgumentOutOfRangeException(nameof(selectedMacro));

		if (parameter < 0x80)
		{
			string normalized = Normalize(_parameterizedMacros[selectedMacro]);
			return normalized switch
			{
				"F0F000Z" => new SetResonantFilterCutoffCommand(parameter / 127.0),
				"F0F001Z" => new SetResonantFilterResonanceCommand(parameter / 127.0),
				_ => null,
			};
		}

		string fixedMacro = Normalize(_fixedMacros[parameter - 0x80]);
		if (fixedMacro.Length != 8)
			return null;

		bool cutoff;
		if (fixedMacro.StartsWith("F0F000", StringComparison.Ordinal))
			cutoff = true;
		else if (fixedMacro.StartsWith("F0F001", StringComparison.Ordinal))
			cutoff = false;
		else
			return null;

		if (!byte.TryParse(
			fixedMacro.AsSpan(6, 2),
			NumberStyles.HexNumber,
			CultureInfo.InvariantCulture,
			out byte value)
			|| value > 0x7F)
		{
			return null;
		}

		double normalizedValue = value / 127.0;
		return cutoff
			? new SetResonantFilterCutoffCommand(normalizedValue)
			: new SetResonantFilterResonanceCommand(normalizedValue);
	}

	private static string Normalize(string? macro)
	{
		if (string.IsNullOrEmpty(macro))
			return string.Empty;

		StringBuilder result = new(macro.Length);
		foreach (char character in macro)
		{
			if (character == '\0' || char.IsWhiteSpace(character))
				continue;
			result.Append(char.ToUpperInvariant(character));
		}
		return result.ToString();
	}
}
