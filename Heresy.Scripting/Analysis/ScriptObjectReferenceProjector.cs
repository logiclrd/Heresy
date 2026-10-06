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
				ProjectReference(
					document,
					reference))
			.ToArray();
	}

	private static ProjectedScriptObjectReference ProjectReference(
		SongDocument document,
		ScriptObjectReference reference)
	{
		if (document.TryGet(
			reference.Id,
			out SongObject? songObject))
		{
			return new ProjectedScriptObjectReference(
				reference.Id,
				reference.Span,
				songObject.Name,
				songObject.Kind,
				ScriptObjectReferenceResolution.Live);
		}

		if (document.Tombstones.TryGetValue(
			reference.Id,
			out ObjectTombstone? tombstone))
		{
			return new ProjectedScriptObjectReference(
				reference.Id,
				reference.Span,
				tombstone.LastKnownName,
				tombstone.Kind,
				ScriptObjectReferenceResolution.Tombstone);
		}

		return new ProjectedScriptObjectReference(
			reference.Id,
			reference.Span,
			Name: null,
			SongObjectKind.Unknown,
			ScriptObjectReferenceResolution.Missing);
	}
}
