using Heresy.Core.Objects;
using Heresy.Core.Patterns;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Editor state carried when a standalone tracker view switches to an adjacent
/// pattern placement.
/// </summary>
public sealed record PatternEditorOpenState(
	ObjectId SourceId,
	int BaseOctave,
	int PatternRow,
	int Channel,
	PatternCellField Field,
	PatternEditMask EditMask = PatternEditMask.Default,
	double? CurrentVolume = null);

public sealed record PatternEditorSwitchRequest(
	int Delta,
	PatternEditorOpenState State);
