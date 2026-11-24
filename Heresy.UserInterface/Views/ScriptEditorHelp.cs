using Avalonia.Controls;
using Avalonia.Media;

namespace Heresy.UserInterface.Views;

/// <summary>
/// Actual author-facing script APIs as implemented in ScriptProgramRuntime,
/// and the constrained expression contracts used by other script objects.
/// Reference text is read-only but supports normal selection/copy.
/// When runtime helper APIs change, change these strings and the related tests.
/// </summary>
public static class ScriptEditorHelp
{
    public const string SequenceReference = """
SCRIPT SEQUENCE — LOOKUP AND RETURN VALUES
Called once for each requested order. The returned SequenceEntry starts one Pattern.
Returning null, default, or falling through ends the Sequence.
The playback engine's Play() accepts a Pattern object ID, not a Sequence ID.

Variables available to each invocation (int):
  absoluteIndex          number of entry lookups so far, starting at 0
  sequenceIndex          requested order index, starting at 0
  previousSequenceIndex  previous order; -1 on the first call

Functions:
  ObjectId _O(uint id)
  ObjectId _O(int id)
    Resolve a nonzero song ObjectId; _O(35) is a literal lookup.
    Computed lookups are valid C# but not statically deep-duplicable.

  SequenceEntry Play(ObjectId patternId, int startRow = 0)
    Return this result to launch the named Pattern at its startRow.
    Invalid or missing Pattern IDs cannot produce the expected order.

  double Random()
    Deterministic per-invocation RNG result in [0, 1).

  void Checkpoint()
    Explicit execution-budget checkpoint for computation-heavy scripts.

PITCH CONVERSIONS (12-TET; available in all restricted scripts)
  double Semitone(double semitones)             2^(semitones / 12)
  double Cent(double cents)                     2^(cents / 1200)
  double Octave(double octaves)                 2^octaves
  double SemitoneOffset(double pitchMultiplier) 12*log2(pitchMultiplier)
  Signed/fractional offsets are valid. Forward results must be positive
  finite ratios; inverse input must be a positive finite ratio.
  Invalid inputs or unrepresentable results throw ArgumentOutOfRangeException.
  These functions do not change playbackSpeedMultiplier or use Instrument Divisions.

Examples (replace 35 and 36 with existing Pattern IDs):
  if (sequenceIndex == 0)
      return Play(_O(35));
  if (sequenceIndex == 1)
      return Play(_O(36), 4);
  return default;

A switch is also supported:
  switch (sequenceIndex)
  {
      case 0: return Play(_O(35));
      default: return default;
  }

C# subset: locals, conditionals, loops (including foreach), switches,
arithmetic, strings/interpolation, arrays, managed object creation and using.
Approved framework types include List<T>, Dictionary<TKey,TValue>, StringBuilder,
and ordinary string and collection members; see docs/script-runtime-allow-list.txt.
The allow-list excludes particular mutating or capability-bearing members,
including filesystem access through path-based constructors.
LINQ-to-Objects methods and inline lambdas are available from System.Linq.dll:
  var order = Enumerable.Range(0, 8).Where(n => n % 2 == 0).ToArray();
Only the exact System.Linq.dll assembly is permitted, not PLINQ, Queryable or
expression trees. Musical helpers remain direct statements (not lambda bodies).
Method-group delegates, user-defined helpers, unsafe, async, reflection, and
exception-control constructs remain unavailable at this stage.
Heresy-specific calls remain limited to approved script helpers.
""";

    public const string PatternReference = """
SCRIPT PATTERN — NOTE, CONTROL AND VOICE HELPERS
A script runs once per Pattern invocation. Helpers emit row-timed commands.
In streaming playback, every emitting call must be a direct statement.
Rows are doubles (including fractional rows) in [0, RowCount].
Physical channels are ints in [0, ChannelCount - 1]. Virtual IDs are
uints scoped to this Pattern invocation, independent of ChannelCount.
timeOffsetSeconds adds a signed wall-time offset to the specified row.

OBJECT REFERENCES, PITCH AND UTILITIES
  ObjectId _O(uint id)
  ObjectId _O(int id)
    _O(35) is object 35; zero is invalid. Computed IDs are executable
    but not statically discoverable for deep duplication.
  double Random()                 deterministic value in [0, 1)
  void Checkpoint()               explicit execution-budget checkpoint

  Pitch is specified as pitchMultiplier, a positive finite frequency ratio:
  1.0 = unshifted; 2.0 = +12 semitones; 0.5 = -12 semitones.

PITCH CONVERSIONS (12-TET; available in all restricted scripts)
  double Semitone(double semitones)             2^(semitones / 12)
  double Cent(double cents)                     2^(cents / 1200)
  double Octave(double octaves)                 2^octaves
  double SemitoneOffset(double pitchMultiplier) 12*log2(pitchMultiplier)
  Signed/fractional offsets are valid. Forward results must be positive
  finite ratios; inverse input must be a positive finite ratio.
  Invalid inputs or unrepresentable results throw ArgumentOutOfRangeException.
  These functions do not change playbackSpeedMultiplier or use Instrument Divisions.

PHYSICAL NOTE START
  Note(double row, int channel, ObjectId sourceId,
       double pitchMultiplier = 1.0,
       double playbackSpeedMultiplier = 1.0,
       bool mixdown = false, double? volume = null,
       double timeOffsetSeconds = 0.0,
       ObjectId? eventFilterDefinitionId = null)
    Begin a note on a physical channel; volume is an optional [0,1] value.

VIRTUAL NOTE START
  NoteVirtual(double row, uint virtualId, ObjectId sourceId,
              double pitchMultiplier = 1.0,
              double playbackSpeedMultiplier = 1.0,
              bool mixdown = false, double? volume = null,
              double timeOffsetSeconds = 0.0,
              ObjectId? eventFilterDefinitionId = null)
    Begin a Pattern-invocation-scoped virtual voice.

NOTE FILTERS (OUTER FILTER OF CURRENT NOTE)
  NoteFilter(double row, int channel,
             ObjectId? definitionId = null,
             double timeOffsetSeconds = 0.0)
  NoteFilterVirtual(double row, uint virtualId,
                    ObjectId? definitionId = null,
                    double timeOffsetSeconds = 0.0)
    Set/clear current note's Filter definition; not the tracker channel filter.

CHANNEL FILTERS (PERSISTENT OUTPUT FILTER)
  ChannelFilter(double row, int channel,
                ObjectId? definitionId = null,
                double timeOffsetSeconds = 0.0)
  ChannelFilterVirtual(double row, uint virtualId,
                       ObjectId? definitionId = null,
                       double timeOffsetSeconds = 0.0)
    Set/clear channel output Filter by definition ID.

VIRTUAL-ONLY VOICE CONTROLS
  VolumeVirtual(double row, uint virtualId, double volume,
                double timeOffsetSeconds = 0.0)
    Set current virtual note volume to [0,1], remembered for later notes
    on the same scoped virtual ID. No physical Volume helper exists yet.

  SourceFrameVirtual(double row, uint virtualId, long sourceFrameOffset,
                     double timeOffsetSeconds = 0.0)
    Seek in native source frames (nonnegative). Non-seekable sounds ignore it.
    Unlike physical Seek, this is NOT a playback-seconds offset.

  DisplacementVirtual(double row, uint virtualId, NoteDisplacementAction action,
                      double timeOffsetSeconds = 0.0)
    Set current voice's next-displacement policy: Cut, Continue, Off, Fade.

  PastVirtual(double row, uint virtualId, TrackerPastNoteAction action,
              double timeOffsetSeconds = 0.0)
    Act on displaced voices in this virtual scope: Cut, Off, Fade.

NOTE RELEASE/CUT
  Off(double row, int channel, double timeOffsetSeconds = 0.0)
  Cut(double row, int channel, double timeOffsetSeconds = 0.0)
  OffVirtual(double row, uint virtualId, double timeOffsetSeconds = 0.0)
  CutVirtual(double row, uint virtualId, double timeOffsetSeconds = 0.0)
  OffVirtualInScope(double row, double timeOffsetSeconds = 0.0)
  CutVirtualInScope(double row, double timeOffsetSeconds = 0.0)
    Scope-wide actions apply to virtual voices within this Pattern invocation.
  OffAllVirtual(double row, double timeOffsetSeconds = 0.0)
  CutAllVirtual(double row, double timeOffsetSeconds = 0.0)
    Global actions address previously started virtual voices in all scopes.

GLOBAL TIMING
  Tempo(double row, double ticksPerDiachron)
  Speed(double row, int ticksPerRow)
    Set positive global tempo/speed values.

PHYSICAL-CHANNEL CONTROLS
  Seek(double row, int channel, double playbackSeconds,
       double timeOffsetSeconds = 0.0)
    Seek physical playback in seconds, not source frames.
  Pan(double row, int channel, double x, double y = 0, double z = 0,
      double timeOffsetSeconds = 0.0)
    Set absolute 3D spatial position.
  Surround(double row, int channel, bool enabled,
           double timeOffsetSeconds = 0.0)
    Toggle physical surround.
  Filter(double row, int channel, double cutoff, double resonance,
         double timeOffsetSeconds = 0.0)
  FilterCutoff(double row, int channel, double cutoff,
               double timeOffsetSeconds = 0.0)
  FilterResonance(double row, int channel, double resonance,
                  double timeOffsetSeconds = 0.0)
    Tracker-style resonant filtering. Cutoff and resonance are [0,1].
    This is distinct from ChannelFilter(ObjectId).

COPYABLE EXAMPLES (substitute actual object IDs)
  Note(0, 0, _O(35), pitchMultiplier: 1.0, volume: 0.75);
  Note(1, 0, _O(35), pitchMultiplier: Semitone(7));
  NoteVirtual(0, 7U, _O(35), pitchMultiplier: Cent(-25));
  Note(2, 0, _O(35), pitchMultiplier: Octave(-1) * Semitone(4));
  VolumeVirtual(1, 7U, 0.5);
  NoteFilter(2, 0, _O(36));
  ChannelFilterVirtual(2, 7U, _O(36));
  OffVirtual(4, 7U);

RESTRICTIONS
Musical helpers, the four pitch conversions, and named voice-action enum
constants are approved. Ordinary managed strings, arrays, collections, and
foreach are also available through the reviewed .NET runtime allow-list
(docs/script-runtime-allow-list.txt). For example:
  var rows = new List<int> { 0, 2, 4 };
  foreach (int row in rows) Note(row, 0, _O(35));
LINQ-to-Objects and inline lambdas support filtering and data transforms:
  var even = rows.Where(row => row % 2 == 0).ToArray();
  foreach (int row in even) Note(row, 0, _O(35));
Only System.Linq.dll is approved, not PLINQ, Queryable or expression trees.
Lambdas may compute data but cannot call musical note/control helpers.
No arbitrary Heresy host-object access, filesystem capabilities, method-group
delegates, unsafe, async, reflection or exception control yet.
Emission is streaming; out-of-order events may be dropped. For each note
pitchMultiplier/playbackSpeedMultiplier must be positive finite numbers.
""";

    public const string EnvelopeIntroduction =
        "A Script Envelope is one restricted-C# scalar expression returning " +
        "a double; it is evaluated over active Envelope time, not a sequence " +
        "of note commands.";

    public const string EnvelopeReference = """
SCRIPT ENVELOPE — SCALAR EXPRESSION
Expression variables:
  double elapsedSeconds    active time since note/envelope start
  double releaseSeconds    -1 before Note Off; elapsed active release time after

Return a finite double (not automatically clamped to [0,1]). An optional
Release limit in seconds is enforced separately: 0 means immediate end;
blank means unbounded. Script Envelopes do not expose Pattern note helpers.

PITCH CONVERSIONS (12-TET; available in all restricted scripts)
  double Semitone(double semitones)             2^(semitones / 12)
  double Cent(double cents)                     2^(cents / 1200)
  double Octave(double octaves)                 2^octaves
  double SemitoneOffset(double pitchMultiplier) 12*log2(pitchMultiplier)
  Signed/fractional offsets are valid. Forward results must be positive
  finite ratios; inverse input must be a positive finite ratio.
  Invalid inputs or unrepresentable results throw ArgumentOutOfRangeException.
  These functions do not change playbackSpeedMultiplier or use Instrument Divisions.
Example expression:
  releaseSeconds < 0 ? 0.7 : 0.7 * Math.Max(0, 1 - releaseSeconds / 0.2)

Expressions support arithmetic, comparisons, conditionals and approved
System.Math functions. No statements, allocations, I/O, or arbitrary calls.
""";

    public const string InstrumentIntroduction =
        "Script Instruments use restricted scalar expressions to choose a " +
        "predeclared tone index and optionally change pitch/NNA. " +
        "Tone definitions are still edited separately, not constructed " +
        "procedurally by the script.";

    public const string InstrumentReference = """
SCRIPT INSTRUMENT — EXPRESSION FIELDS
All expressions receive:
  double pitchMultiplier   the incoming positive pitch ratio (C4 = 1.0)
  PitchExpression returns an ADDITIONAL ratio for the chosen tone, not
  an absolute note pitch.

PITCH CONVERSIONS (12-TET; available in all restricted scripts)
  double Semitone(double semitones)             2^(semitones / 12)
  double Cent(double cents)                     2^(cents / 1200)
  double Octave(double octaves)                 2^octaves
  double SemitoneOffset(double pitchMultiplier) 12*log2(pitchMultiplier)
  Signed/fractional offsets are valid. Forward results must be positive
  finite ratios; inverse input must be a positive finite ratio.
  Invalid inputs or unrepresentable results throw ArgumentOutOfRangeException.
  These functions do not change playbackSpeedMultiplier or use Instrument Divisions.
Tone selection Source:
  Returns an int index into declared ToneSpecifications, or -1 for silence.
  Example: SemitoneOffset(pitchMultiplier) >= 12.0 ? 1 : 0
  (Requires at least two declared tones, indices 0 and 1.)

Pitch scaling expression:
  Returns a positive finite double multiplied by the chosen tone's fixed ratio.
  Example: Semitone(-7)

NNA expression:
  Returns InstrumentNnaAction.Inherit, Cut, Continue, Off or Fade.
  Example:
    pitchMultiplier >= Semitone(12)
      ? InstrumentNnaAction.Cut : InstrumentNnaAction.Inherit
  Fade uses the separately configured NNA Fade duration.

These are individual bounded expressions, not Pattern-style statement
programs. System.Math and the four pitch conversions are allowed; no
general allocation, I/O, other member access or free-form procedural
tone construction is supported yet.
""";

    /// <summary>A dedicated, text-selectable viewport for copying API text.
    /// The outer document view must not be responsible for scrolling it.</summary>
    public static TextBox CreateReferenceViewer(string reference)
    {
        TextBox viewer = new()
        {
            Text = reference,
            AcceptsReturn = true,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 260,
            MinHeight = 180,
            MinWidth = 360,
        };
        ScriptEditorFonts.ApplyTo(viewer);
        return viewer;
    }
}
