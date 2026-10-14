using System;

namespace Heresy.Core.Objects;

/// <summary>
/// Persistent named object in a song. Containment in the UI tree does not imply
/// ownership: references between objects are always by <see cref="ObjectId"/>.
/// </summary>
public abstract class SongObject
{
	protected SongObject(ObjectId id, string name, bool isSoundSource = false)
	{
		if (id.IsNone)
			throw new ArgumentException("Song objects may not use ObjectId.None.", nameof(id));

		Id = id;
		Name = name ?? throw new ArgumentNullException(nameof(name));
		if (isSoundSource)
			SourceColourArgb = SongSourceColour.CreateRandom();
	}

	public ObjectId Id { get; }

	public string Name { get; set; }

    /// <summary>Stable platform-neutral ARGB visual metadata per source.
    /// Null for non-source objects. Playback does not use this value.</summary>
    public uint? SourceColourArgb { get; private set; }

    /// <summary>Preserve the visible tint of an explicitly copied source.</summary>
    public void CopySourceColourFrom(SongObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!SourceColourArgb.HasValue || source.SourceColourArgb is not uint tint)
            throw new InvalidOperationException("Both objects must be sound sources.");
        SourceColourArgb = tint;
    }

    internal void RestoreSourceColour(uint argb)
    {
        if (!SourceColourArgb.HasValue)
            throw new InvalidOperationException("A non-source cannot have a source colour.");
        SongSourceColour.Validate(argb);
        SourceColourArgb = argb;
    }

	public abstract SongObjectKind Kind { get; }
}
