using System;
using System.Collections.Generic;
using System.Numerics;

using Heresy.Core.Assets;
using Heresy.Core.Objects;

namespace Heresy.Core.Samples;

public sealed class SampleDefinition : SongObject
{
	public SampleDefinition(ObjectId id, string name, ExternalAssetReference asset)
		: base(id, name)
	{
		Asset = asset ?? throw new ArgumentNullException(nameof(asset));
	}

	private SampleDefinition(ObjectId id, string name)
		: base(id, name)
	{
	}

	public static SampleDefinition CreateImported(
		ObjectId id,
		string name,
		string encodedFileName,
		ReadOnlySpan<byte> encodedBytes)
	{
		PendingSampleAsset pending =
			new(encodedFileName, encodedBytes);
		return new SampleDefinition(id, name)
		{
			PcmData =
				SampleAudioCodec.Decode(
					pending.Bytes.Span,
					pending.FileName),
			PendingAsset = pending,
		};
	}

	public override SongObjectKind Kind => SongObjectKind.Sample;

	/// <summary>
	/// Identity of the encoded representation already persisted by the current
	/// song. Playback never reads this path.
	/// </summary>
	public ExternalAssetReference? Asset { get; set; }

	/// <summary>
	/// Immutable decoded PCM owned by the live song and used by realtime
	/// playback without filesystem or codec work.
	/// </summary>
	public SamplePcmData? PcmData { get; internal set; }

	/// <summary>
	/// Encoded bytes retained only for a newly imported sample until the current
	/// song successfully writes its own persisted copy.
	/// </summary>
	public PendingSampleAsset? PendingAsset { get; internal set; }

	public void ReplaceImportedEncoding(
		string encodedFileName,
		ReadOnlySpan<byte> encodedBytes)
	{
		PendingSampleAsset pending =
			new(encodedFileName, encodedBytes);
		SetImportedData(
			SampleAudioCodec.Decode(
				pending.Bytes.Span,
				pending.FileName),
			pending);
	}

	internal void SetLoadedPcm(
		SamplePcmData pcmData,
		ExternalAssetReference asset)
	{
		PcmData =
			pcmData
				?? throw new ArgumentNullException(nameof(pcmData));
		Asset =
			asset
				?? throw new ArgumentNullException(nameof(asset));
		PendingAsset = null;
	}

	internal void SetImportedData(
		SamplePcmData pcmData,
		PendingSampleAsset pendingAsset)
	{
		PcmData =
			pcmData
				?? throw new ArgumentNullException(nameof(pcmData));
		PendingAsset =
			pendingAsset
				?? throw new ArgumentNullException(nameof(pendingAsset));
		Asset = null;
	}

	internal void MarkAssetPersisted(
		ExternalAssetReference asset)
	{
		Asset =
			asset
				?? throw new ArgumentNullException(nameof(asset));
		PendingAsset = null;
	}

	/// <summary>Frequency represented by an untransposed sample, normally middle C.</summary>
	public double ReferenceFrequencyHz { get; set; } = 261.6255653005986;

	public SampleLoop Loop { get; set; } = new(SampleLoopMode.None, 0, 0);

	/// <summary>
	/// Local spatial position for each source channel in the asset. Importers
	/// may default mono to (0,0,0) and stereo to (-1,0,0)/(+1,0,0).
	/// </summary>
	public List<Vector3> SourceChannelPositions { get; } = [];
}
