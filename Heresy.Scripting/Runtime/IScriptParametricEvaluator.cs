namespace Heresy.Scripting.Runtime;
/// <summary>Stateless compiled native wall-time/optional row-time delta.</summary>
public interface IScriptParametricEvaluator
{
	double Evaluate(double elapsedSeconds, double rowTime);
}
