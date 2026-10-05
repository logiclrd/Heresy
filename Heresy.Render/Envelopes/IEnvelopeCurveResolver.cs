using Heresy.Core.Objects;

namespace Heresy.Render.Envelopes;

/// <summary>
/// Resolves a persistent envelope reference into an immutable executable curve.
/// Returning false represents an unresolved/deleted envelope reference.
/// </summary>
public interface IEnvelopeCurveResolver
{
	bool TryResolve(
		ObjectId envelopeId,
		out IEnvelopeCurve? curve);
}
