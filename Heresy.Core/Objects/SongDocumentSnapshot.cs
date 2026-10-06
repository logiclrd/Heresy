using System;

using Heresy.Core.Persistence;

namespace Heresy.Core.Objects;

/// <summary>
/// Isolated deep copy of a mutable authoring document captured at a specific
/// document/audio revision. The cloned document may be consumed freely by a
/// playback worker without observing later authoring edits.
/// </summary>
public sealed class SongDocumentSnapshot
{
	private SongDocumentSnapshot(
		SongDocument document,
		uint documentRevision,
		uint audioRevision)
	{
		Document =
			document
				?? throw new ArgumentNullException(nameof(document));
		DocumentRevision = documentRevision;
		AudioRevision = audioRevision;
	}

	public SongDocument Document { get; }

	public uint DocumentRevision { get; }

	public uint AudioRevision { get; }

	public static SongDocumentSnapshot Create(
		SongDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);

		uint documentRevision = document.DocumentRevision;
		uint audioRevision = document.AudioRevision;

		string json =
			SongDocumentJson.Serialize(
				document,
				sample => sample.Asset.FullPath,
				scriptReferenceAnalyzer: null,
				pruneUnreferencedTombstones: false);

		SongDocument clone =
			SongDocumentJson.Deserialize(
				json,
				storedPath => storedPath);

		return new SongDocumentSnapshot(
			clone,
			documentRevision,
			audioRevision);
	}
}
