# Script pitch-multiplier helpers — discovery and proposed API

**Status:** Owner-approved design (2026-11-21). The four helpers have been implemented in the separate draft implementation branch; they become available to users when that PR is accepted and merged. This document preserves the original discovery rationale and numerical contract.

**Scope:** the intuitive pitch-multiplier helper TODO in [todo.md](todo.md). Separate from Script Pattern physical/virtual control parity, the Script Instrument tone-authoring redesign, and tiered Undo/Redo.

## Audited current behavior

- `PatternScriptProgram.Note` and `NoteVirtual` both accept `double pitchMultiplier = 1.0`, followed by the separate `playbackSpeedMultiplier`. Each accepts only a **positive finite** ratio. A call emits a scheduled event, and streaming note/control calls must remain **direct statements**.
- A Script Instrument has three *single-expression* programs: `Source` selects a declared tone index, `PitchExpression` returns an additional scaling ratio for that tone's fixed pitch, and `NnaExpression` returns an `InstrumentNnaAction`. All receive the **incoming** `pitchMultiplier` relative to C4 = 1.0. This input is not a musical semitone offset; it is already a frequency ratio.
- Script Envelopes and native parametric effects evaluate scalar `double` expressions; Script Sequences use statement-level lookup to return the next Pattern. There is no musical pitch argument in these contexts, but allowing the same pure conversion functions is harmless and avoids surprising differences in the restricted language.
- All generated script wrappers currently import `System` and `Heresy.Scripting.Runtime`; Roslyn semantic validation permits methods on `PatternScriptProgram`, `SequenceScriptProgram`, and `System.Math` only. Expressions have an independent syntax/length/node budget (2,048 characters / 256 syntax nodes) and are compiled before evaluation.
- `Math.Pow(2.0, semitones / 12.0)` is already legal, and the Pattern editor reference documents it as today's workaround; `Semitone(...)` does **not** currently exist.
- Tracker keyboard pitch entry uses 12 equal divisions per octave. Data Instruments have a separately configurable `Divisions` scale. New `Semitone` functions should **not** silently use or modify an Instrument's divisions.

Source touchpoints: `Heresy.Scripting/Runtime/ScriptProgramRuntime.cs`, `Heresy.Scripting/Compilation/ScriptCompiler.cs`, `Heresy.UserInterface/Views/ScriptEditorHelp.cs`, `Heresy.UserInterface/PatternEditing/PatternNoteKeyboardEditor.cs`, and `docs/script-programmability.md`.

## Proposed minimal v1 surface

Approved signatures, implemented in the separate implementation branch as unqualified restricted-script functions returning `double`:

| Helper | Meaning | Formula |
| --- | --- | --- |
| `double Semitone(double semitones)` | Signed offset, in 12-TET semitones, as a frequency multiplier | `Math.Pow(2.0, semitones / 12.0)` |
| `double Cent(double cents)` | Signed offset, in cents, as a frequency multiplier | `Math.Pow(2.0, cents / 1200.0)` |
| `double Octave(double octaves)` | Signed offset, in octaves, as a frequency multiplier | `Math.Pow(2.0, octaves)` |
| `double SemitoneOffset(double pitchMultiplier)` | Convert a positive frequency ratio to a signed number of 12-TET semitones | `12.0 * Math.Log2(pitchMultiplier)` |

**Rationale:** `Semitone(7)` and `Cent(-25)` are intuitive in a `Note` call, while `SemitoneOffset(pitchMultiplier)` makes Script Instrument selector boundaries comprehensible without spelling out logarithms. `Octave(-1)` is much easier to read than `0.5` when composing musical offsets. Four simple methods are enough for routine work without introducing a second note notation or tuning system.

**Names are for approval.** Singular `Semitone`, `Cent`, `Octave` describe a conversion of an *amount* rather than a class of notes. `SemitoneOffset` explicitly signals an inverse producing an offset, not a multiplier. No unqualified `Note` or `Pitch` alias: those names already have other script meanings.

### Examples (compile on the implementation branch)

```csharp
// Script Pattern: same ratios for physical and virtual channels.
Note(0, 0, _O(35), pitchMultiplier: Semitone(7));
NoteVirtual(1, 3U, _O(35), pitchMultiplier: Cent(-25));
Note(2, 0, _O(35), pitchMultiplier: Octave(-1) * Semitone(4));

// Script Instrument Source: choose among declared tone indices.
SemitoneOffset(pitchMultiplier) >= 12.0 ? 1 : 0

// Script Instrument PitchExpression: extra factor on the selected tone.
Semitone(-7)

// Script Instrument NnaExpression: compare against a 12-TET boundary.
pitchMultiplier >= Semitone(12)
    ? InstrumentNnaAction.Cut : InstrumentNnaAction.Inherit
```

These examples **do not** change `playbackSpeedMultiplier`, source selection, channel ownership, or Note's direct-statement-yield requirements.

## Mathematical and validation contract

1. **Reference:** 12-tone equal temperament, with an octave of exactly a factor of two; 100 cents per semitone and 1,200 cents per octave. No base frequency or concert-pitch calibration is required: the functions convert **relative** ratios. They do not quantize.
2. **Types/units:** each argument and return is `double`. Signed **fractional** offsets are valid, including negative semitones/cents/octaves, e.g. `Semitone(-0.5)` and `Cent(12.5)`. `SemitoneOffset` accepts any positive finite ratio (not just an exact musical note).
3. **Input rejection:** all forward helpers reject `NaN` and positive/negative infinity; `SemitoneOffset` rejects zero, negative values, NaN, and either infinity. Throw `ArgumentOutOfRangeException` with the argument name.
4. **Output rejection:** forward helpers must reject any exponent so large that the computed multiplier is non-finite, zero or negative (including overflow to infinity and underflow to zero). Do not clamp, silently substitute `1.0`, or return a non-finite value. Positive *subnormal* results may remain valid. The inverse's finite positive input yields a finite double offset.
5. **Composition:** multiplying conversion results composes frequency ratios. The **final composed** multiplier is still validated at the ordinary note/tone boundary: individually valid factors can have an invalid product (overflow/underflow). No implicit normalization or pitch-range limitation belongs in the helpers.
6. **Precision:** use the .NET `System.Math` double implementations; test known points and round trips with stated tolerances. Do not promise bit-identical results across CPU/OS/runtime versions or exact inverse equality for general fractional values. A comparison near a boundary should preferably compare ratios to `Semitone(n)` rather than testing a floating-point logarithm for exact equality.
7. **Determinism:** no RNG, clock, mutable song state, allocations on the ordinary success path, or I/O. Existing script compilation/preparation and execution-budget rules still apply. Errors follow the existing script runtime diagnostic path.
8. **Compatibility:** no serialization/schema change, no new `SongObject` kind, no change to compiler-injected returns, no change to eager vs incremental scheduling, and no change to existing `System.Math` accessibility.

Reference test vectors (approximate except unity and powers of two):

| Expression | Expected result |
| --- | --- |
| `Semitone(0)`, `Cent(0)`, `Octave(0)` | `1.0` |
| `Semitone(12)`, `Cent(1200)`, `Octave(1)` | `2.0` |
| `Semitone(-12)`, `Cent(-1200)`, `Octave(-1)` | `0.5` |
| `Semitone(7)` | about `1.4983070768766815` |
| `Semitone(0.5)` | about `1.029302236643492` |
| `Cent(100)` | approximately `Semitone(1)` |
| `SemitoneOffset(1.0)`, `SemitoneOffset(2.0)` | `0.0`, `12.0` |
| `SemitoneOffset(Semitone(-7.25))` | approximately `-7.25` |

### Considered but deferred

- **Named notes / MIDI note numbers** (e.g. `NotePitch("C#4")`, `MidiNote(60)`): these require an explicit register-numbering and reference-C mapping and would overlap the existing `Note(row,...)` call. Avoid strings and tuning-policy ambiguity in v1.
- **Rational interval helpers** (`Ratio(3, 2)`, `Interval(...)`): `3.0 / 2.0` already gives a just fifth; introducing an interval type or an alternate temperament is a separate, larger feature.
- **A generalized transpose function** (`Transpose(ratio, semitones)`): `ratio * Semitone(semitones)` is already clear. No need to duplicate composition operations.
- **`CentOffset` and `OctaveOffset` inverses:** useful occasionally but straightforward as `100.0 * SemitoneOffset(r)` (100 cents per semitone) and `SemitoneOffset(r) / 12.0`. Prefer a single inverse until real usage justifies more names.
- **Instrument `Divisions`-aware conversion:** fundamentally different from 12-TET. Any future custom-scale helper should state its division count explicitly (not inspect mutable Instrument state), e.g. a separately designed `DivisionOffset(n, divisionsPerOctave)`.
- **Frequency-in-Hz conversions:** require explicit reference-frequency semantics and are not necessary to express `pitchMultiplier`. Can be studied later if Script Instrument authoring evolves.

## Exposure and implementation approach (approved)

**Recommendation: uniformly expose the four pure functions in every restricted scripting context**—Script Pattern (eager and incremental), Script Sequence, Script Instrument tone selector, pitch scaling, NNA expression, Script Envelope expression, and native parametric-effect expression. Contexts without a pitch parameter may still use them for ordinary numeric calculations. The function's meaning never changes with context.

**One implementation, mixin-like syntax:** Patterns and Instruments need exactly the same numerical conversions; these functions must not be duplicated in separate runtime classes. A C# default-interface-method "mixin" is a plausible design, but ordinary default interface members are not inherited as directly callable instance members of the implementing class. The generated Pattern wrappers already inherit `PatternScriptProgram`, whereas generated Instrument and Envelope expression wrappers implement distinct evaluator interfaces. Requiring all wrappers to implement an extra interface and forward calls would add ceremony without changing the calculation. Prefer **one static implementation with `using static` in each generated wrapper**, making `Semitone(7)` equally usable from Patterns, Instruments and other scripts. If a later requirement needs per-invocation dependencies, revisit an interface; these pure conversions do not need any.

1. Put implementation in a dedicated public static class such as `Heresy.Scripting.Runtime.ScriptPitchMath`, with four public pure methods and explicit input/output guards. Avoid adding Script Pattern-only protected methods, because the Instrument and Envelope expression types do not inherit `PatternScriptProgram`.
2. Add a `using static Heresy.Scripting.Runtime.ScriptPitchMath;` to **all eight generated wrapper forms** in `ScriptCompiler` (Pattern eager, Pattern incremental, Sequence, Instrument selector/pitch/NNA, Envelope, native parametric).
3. Extend `ValidateRestrictedSemantics` with that exact declaring type and an **explicit method-name/signature allowlist** for the four proposed methods. Do not authorize arbitrary methods by namespace or add broad framework member access. Author-facing invocation is unqualified (`Semitone(7)`) and needs no instance or object allocation.
4. The streaming direct-statement list and yield rewriter must **not** include these numerical helpers: `Semitone` and friends are ordinary nested expressions, not note/control emission calls.
5. Update the read-only Script Pattern reference (`ScriptEditorHelp.PatternReference`) to show exact signatures and usable examples; remove the explicit claim that `Semitone` is unavailable. Update Script Instrument reference (especially the pitch-scale vs selector distinction), Sequence and Envelope reference text, and any native parametric-editor help whose allowed arithmetic is affected. Keep snippets authentic until implementation.
6. Add focused tests in `Heresy.Tests.Scripting` for numerical validation/round trips and compiler analysis+execution in all the above contexts. Cover `Note` and `NoteVirtual`, both eager and incremental Script Pattern generation, Script Instrument selector/pitch/NNA, Sequence, Envelope, parametric effects, and compile-time refusal of unrelated static methods/instance calls.
7. Include regression checks that helper calls nested **inside** a `Note` argument remain legal while emitting `Note` in an expression context still fails streaming direct-statement validation. Check unchanged event order, pitch ratios, and eager-vs-streaming note equivalence.
8. After implementation, execute full exact-head CI and manual Script editor reference checks before requesting merge approval.

## Review decisions requested

1. **Surface:** approve the four-method minimum (`Semitone`, `Cent`, `Octave`, `SemitoneOffset`), or trim/expand it?
2. **Scope:** permit the same methods in all eight script compilation contexts (recommended), or only pitch-bearing contexts?
3. **Failure policy:** invalid/non-finite inputs and invalid multiplier results throw `ArgumentOutOfRangeException` instead of clamping (recommended).

**Implementation proceeds in a separate PR following owner approval.**
