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
				sample =>
					sample.Asset?.FullPath
						?? System.IO.Path.Combine(
							System.IO.Path.GetTempPath(),
							$"heresy-pending-{sample.Id.Value}.sample"),
				scriptReferenceAnalyzer: null,
				pruneUnreferencedTombstones: false);

		SongDocument clone =
			SongDocumentJson.Deserialize(
				json,
				storedPath => storedPath);

		foreach ((ObjectId id, SongObject sourceObject) in document.Objects)
		{
			if (sourceObject is not Heresy.Core.Samples.SampleDefinition sourceSample
				|| !clone.TryGet(id, out SongObject? clonedObject)
				|| clonedObject is not Heresy.Core.Samples.SampleDefinition clonedSample)
			{
				continue;
			}

			clonedSample.Asset = sourceSample.Asset;
			clonedSample.PcmData = sourceSample.PcmData;
			clonedSample.PendingAsset = sourceSample.PendingAsset;
		}

		return new SongDocumentSnapshot(
			clone,
			documentRevision,
			audioRevision);
	}
}
