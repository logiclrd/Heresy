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

	public override SongObjectKind Kind => SongObjectKind.Sample;

	public ExternalAssetReference Asset { get; set; }

	/// <summary>Frequency represented by an untransposed sample, normally middle C.</summary>
	public double ReferenceFrequencyHz { get; set; } = 261.6255653005986;

	public SampleLoop Loop { get; set; } = new(SampleLoopMode.None, 0, 0);

	/// <summary>
	/// Local spatial position for each source channel in the asset. Importers
	/// may default mono to (0,0,0) and stereo to (-1,0,0)/(+1,0,0).
	/// </summary>
	public List<Vector3> SourceChannelPositions { get; } = [];
}
