using System;
using System.Collections.Generic;

using Heresy.Core.Objects;
using Heresy.Scripting.Analysis;

namespace Heresy.UserInterface.Documents;

public readonly record struct ScriptProjectionSelection(
	int Start,
	int End);

public sealed record ScriptProjectionEdit(
	string Source,
	int SourceCaret);

public sealed record ScriptProjectedReferenceToken(
	ObjectId Id,
	ScriptSourceSpan SourceSpan,
	ScriptSourceSpan DisplaySpan,
	string DisplayName,
	SongObjectKind Kind,
	ScriptObjectReferenceResolution Resolution);

/// <summary>
/// Editable display projection for script source. Object references remain
/// canonical _O(id) in Source while Text replaces semantic references with
/// atomic named tokens.
/// </summary>
public sealed class ScriptSourceProjection
{
	private ScriptSourceProjection(
		string source,
		string text,
		IReadOnlyList<ScriptProjectedReferenceToken> tokens)
	{
		Source = source;
		Text = text;
		Tokens = tokens;
	}

	public string Source { get; }
	public string Text { get; }
	public IReadOnlyList<ScriptProjectedReferenceToken> Tokens { get; }

	public static ScriptSourceProjection Create(
		string source,
		IReadOnlyList<ProjectedScriptObjectReference> references)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(references);
		return new(source, source, []);
	}

	public ScriptProjectionSelection NormalizeSelection(
		int start,
		int end)
		=> new(start, end);

	public int MoveCaret(
		int displayPosition,
		int direction)
		=> Math.Clamp(
			displayPosition + Math.Sign(direction),
			0,
			Text.Length);

	public ScriptProjectionEdit Replace(
		int selectionStart,
		int selectionEnd,
		string replacement)
	{
		ArgumentNullException.ThrowIfNull(replacement);
		return new(Source, 0);
	}

	public ScriptProjectionEdit DeleteBackward(
		int selectionStart,
		int selectionEnd)
		=> new(Source, 0);

	public ScriptProjectionEdit DeleteForward(
		int selectionStart,
		int selectionEnd)
		=> new(Source, 0);

	public string GetSourceText(
		int selectionStart,
		int selectionEnd)
		=> string.Empty;

	public int DisplayPositionFromSource(int sourcePosition)
		=> Math.Clamp(sourcePosition, 0, Text.Length);
}
