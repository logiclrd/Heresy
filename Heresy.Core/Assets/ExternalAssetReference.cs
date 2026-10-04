namespace Heresy.Core.Assets;

/// <summary>
/// Reference to binary data stored outside the JSON song document. Sha256 is a
/// lowercase hexadecimal content hash when known.
/// </summary>
public sealed record ExternalAssetReference(string RelativePath, string? Sha256 = null);
