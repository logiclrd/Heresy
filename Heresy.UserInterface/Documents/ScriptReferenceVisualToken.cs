using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Scripting.Analysis;

namespace Heresy.UserInterface.Documents;

public sealed record ScriptReferenceVisualToken(
	ObjectId Id,
	ScriptSourceSpan SourceSpan,
	string Text,
	SongObjectKind Kind,
	ScriptObjectReferenceResolution Resolution);

public static class ScriptReferenceVisualTokenCatalog
{
	public static IReadOnlyList<ScriptReferenceVisualToken> Create(
		IReadOnlyList<ProjectedScriptObjectReference> references)
	{
		ArgumentNullException.ThrowIfNull(references);

		return references
			.OrderBy(reference => reference.Span.Start)
			.Select(reference =>
				new ScriptReferenceVisualToken(
					reference.Id,
					reference.Span,
					Sanitize(reference.DisplayName),
					reference.Kind,
					reference.Resolution))
			.ToArray();
	}

	private static string Sanitize(string text)
		=> text
			.Replace('\r', ' ')
			.Replace('\n', ' ')
			.Replace('\t', ' ');
}
