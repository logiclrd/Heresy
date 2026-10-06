using System;

namespace Heresy.Core.Objects;

public sealed class SongDocumentSnapshot
{
	private SongDocumentSnapshot()
	{
		throw new NotImplementedException();
	}

	public SongDocument Document =>
		throw new NotImplementedException();

	public uint DocumentRevision =>
		throw new NotImplementedException();

	public uint AudioRevision =>
		throw new NotImplementedException();

	public static SongDocumentSnapshot Create(
		SongDocument document)
		=> throw new NotImplementedException();
}
