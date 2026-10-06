namespace Heresy.Core.Persistence;

/// <summary>
/// Controls how external asset paths are written by the bare .hm.json format.
/// </summary>
public enum JsonAssetPathMode
{
	/// <summary>
	/// Write portable '/'-separated paths relative to the JSON directory.
	/// Saving fails if an asset is outside that directory subtree.
	/// </summary>
	Relative,

	/// <summary>
	/// Write OS-conventional fully qualified paths for every external asset.
	/// </summary>
	Absolute,
}
