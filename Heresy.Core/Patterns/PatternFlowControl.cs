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

	/// <summary>
	/// Original source-pattern row containing the selected sequence-control
	/// instruction. This remains stable when local pattern loops expand rows
	/// before sequence control is consumed by the surrounding sequence.
	/// </summary>
	public int? SourceRow { get; init; }

	public bool HasControl => OrderJump.HasValue || BreakRow.HasValue;
}
