namespace Heresy.Core.Patterns;

/// <summary>
/// Sequence-control request produced by one tracker pattern invocation.
/// Bxx supplies OrderJump, Cxx supplies BreakRow, and the two compose when
/// they occur on the same terminating row.
/// </summary>
public readonly record struct PatternFlowControl(
	byte? OrderJump,
	byte? BreakRow)
{
	public static PatternFlowControl None => new(null, null);

	public bool HasControl => OrderJump.HasValue || BreakRow.HasValue;
}
