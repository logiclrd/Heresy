using Heresy.Core.Objects;

namespace Heresy.Core.Sequences;

/// <summary>
/// Identifies one concrete Bxx order-jump instruction as sequence playback
/// encounters it. PatternRow is the original source row, not a row created by
/// local pattern-loop expansion.
/// </summary>
public readonly record struct SequenceOrderJumpEncounter(
	ObjectId PatternId,
	int PatternRow,
	byte TargetOrder);
