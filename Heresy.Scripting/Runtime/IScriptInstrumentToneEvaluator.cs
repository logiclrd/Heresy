namespace Heresy.Scripting.Runtime;

/// <summary>
/// Stateless compiled selection from a declared list of Instrument tones.
/// pitchMultiplier is the caller's positive finite relative C4 frequency.
/// -1 selects silence; other returned indices are validated by the binder.
/// </summary>
public interface IScriptInstrumentToneEvaluator
{
	int SelectToneIndex(double pitchMultiplier);
}
