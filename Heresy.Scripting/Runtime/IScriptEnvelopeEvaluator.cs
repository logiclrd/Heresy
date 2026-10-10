namespace Heresy.Scripting.Runtime;

/// <summary>
/// Immutable, compiled, stateless function of active envelope time.
/// -1 in releaseSeconds means Note Off has not yet occurred.
/// </summary>
public interface IScriptEnvelopeEvaluator
{
	double Evaluate(double elapsedSeconds, double releaseSeconds);
}
