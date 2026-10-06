using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Scripting;

namespace Heresy.Scripting.Analysis;

public enum ScriptObjectReferenceResolution
{
	Live,
	Tombstone,
	Missing,
}

public sealed record ProjectedScriptObjectReference(
	ObjectId Id,
	ScriptSourceSpan Span,
	string? Name,
	SongObjectKind Kind,
	ScriptObjectReferenceResolution Resolution)
{
	public string DisplayName
		=> Name ?? ScriptObjectReferenceSyntax.Format(Id);
}

public static class ScriptObjectReferenceProjector
{
	public static IReadOnlyList<ProjectedScriptObjectReference> Project(
		SongDocument document,
		ScriptReferenceAnalysis analysis)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(analysis);

		return analysis.References
			.Select(reference =>
				new ProjectedScriptObjectReference(
					reference.Id,
					reference.Span,
					Name: null,
					SongObjectKind.Unknown,
					ScriptObjectReferenceResolution.Missing))
			.ToArray();
	}
}
