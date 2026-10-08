using Heresy.Core.Sequencing;

namespace Heresy.Core.Patterns;

/// <summary>
/// Marks a raw data-pattern row slice whose Source-column commands must be
/// resolved against live mapped channel memory when that row executes.
/// </summary>
public interface IDeferredSourcePatternGenerator : IRawPatternNoteGenerator
{
}
