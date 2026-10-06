using System;
using System.Collections.Generic;
using System.IO;

using Heresy.Core.Patterns;
using Heresy.Core.Persistence;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Text format used for copying one complete ordered pattern-effect stack.
/// The payload deliberately uses the same polymorphic PatternEffect JSON
/// representation as song persistence.
/// </summary>
public static class PatternEffectClipboardCodec
{
	private const string Header = "Heresy Pattern Effects/1";

	public static string Serialize(
		IEnumerable<PatternEffect> effects)
	{
		ArgumentNullException.ThrowIfNull(effects);
		return Header
			+ Environment.NewLine
			+ SongDocumentJson.SerializePatternEffects(effects);
	}

	public static PatternEffect[] Deserialize(string text)
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
				"The clipboard does not contain a Heresy pattern-effect stack.",
				nameof(text));
		}

		string json = reader.ReadToEnd();
		if (string.IsNullOrWhiteSpace(json))
		{
			throw new ArgumentException(
				"The clipboard effect-stack payload is empty.",
				nameof(text));
		}

		try
		{
			return SongDocumentJson.DeserializePatternEffects(json);
		}
		catch (Exception ex)
			when (ex is InvalidDataException
				or System.Text.Json.JsonException
				or NotSupportedException)
		{
			throw new ArgumentException(
				"The clipboard contains an invalid Heresy pattern-effect stack.",
				nameof(text),
				ex);
		}
	}
}
