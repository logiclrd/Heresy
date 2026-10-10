using System;

using Heresy.Core.Objects;

namespace Heresy.Core.Envelopes;

/// <summary>
/// Persistent restricted-C# scalar expression. The expression receives
/// elapsedSeconds (active envelope time) and releaseSeconds (-1 until
/// Note Off, then elapsed active time since Note Off). No user script is
/// executed by the Core document model.
/// </summary>
public sealed class ScriptEnvelopeDefinition : EnvelopeDefinition
{
	public ScriptEnvelopeDefinition(ObjectId id, string name) : base(id, name) { }

	/// <summary>
	/// Single deterministic double-valued expression. An empty expression is
	/// invalid when preparing playback.
	/// </summary>
	public string Source { get; set; } = "releaseSeconds < 0 ? 1.0 : 0.0";

	/// <summary>
	/// A promised bound for the release in active-envelope time. The renderer
	/// enforces silence after this duration; null denotes an unbounded release
	/// and cannot be used to predict voice retirement.
	/// </summary>
	public TimeSpan? ReleaseDuration { get; set; }
}
