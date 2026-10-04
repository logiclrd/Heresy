namespace Heresy.Core.Sequencing;

/// <summary>
/// Memory slots used by tracker-style effects. Effects which share tracker
/// memory share the same slot here; for example, normal and fine vibrato both
/// use Vibrato.
/// </summary>
public enum EffectMemorySlot
{
	Vibrato = 0,
}
