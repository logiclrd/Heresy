using Heresy.Core.Objects;

namespace Heresy.Core.Sequencing;

/// <summary>
/// Atomic operation carried by a note event. The command vocabulary is expected
/// to grow as effects are implemented; grouping several commands in one event
/// preserves their atomic emission ordering.
/// </summary>
public abstract record NoteCommand;

public sealed record StartNoteCommand(
	ObjectId SourceId,
	double PitchMultiplier = 1.0,
	double PlaybackSpeedMultiplier = 1.0,
	bool Mixdown = false) : NoteCommand;

public sealed record NoteOffCommand : NoteCommand;

public sealed record NoteCutCommand : NoteCommand;

public sealed record SetTempoCommand(double TicksPerDiachron) : NoteCommand;

public sealed record SetSpeedCommand(int TicksPerRow) : NoteCommand;
