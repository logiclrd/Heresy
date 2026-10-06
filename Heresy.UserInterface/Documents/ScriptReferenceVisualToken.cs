using System;
using System.Collections.Generic;

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
		return [];
	}
}
