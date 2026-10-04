namespace Heresy.Core.Objects;

/// <summary>
/// Minimal metadata retained for a deleted object while dead references to it
/// remain in the document.
/// </summary>
public sealed record ObjectTombstone(ObjectId Id, string LastKnownName, SongObjectKind Kind);
