using Heresy.Core.Objects;

namespace Heresy.Core.Persistence;

/// <summary>
/// An unresolved encoded sample in a staged song load. The original asset
/// identity is preserved so callers can locate matching bytes without silently
/// substituting a different recording.
/// </summary>
public sealed record SampleAssetRecoveryRequest(
	ObjectId SampleId,
	string SampleName,
	string OriginalFullPath,
	string? ExpectedSha256,
	string FailureMessage);
