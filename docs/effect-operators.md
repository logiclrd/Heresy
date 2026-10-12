# Effect operators and tracker compatibility

Heresy has two deliberately separate effect models.

## Native effects use wall time

Native Heresy effects use ordinary wall-clock time for human-facing quantities
such as durations. A native half-second fade remains a half-second fade
regardless of tracker tempo. Tracker timing must never silently redefine the
domain of a native effect functor.

## Tracker effects are row-scoped compatibility operators

Impulse Tracker effects are translated into operators whose lifetime is one
tracker row (or one compatibility row span when a row is explicitly extended).
They are parameter modifiers, not owners of the underlying playback state.

Each playback scope has persistent baseline state. An active operator owns its
own vector of parameter deltas. For an output frame the effective state is
formed conceptually as

```
effective = baseline + sum(active operator deltas)
```

Each operator is updated independently for the frame. Updating one operator
must not overwrite another operator's delta.

When an operator expires, one of two things happens:

- **persistent operator** — its final delta is merged into the baseline once,
  then the operator is removed;
- **transient operator** — it is simply removed and contributes nothing to the
  baseline.

This makes split rendering and single-call rendering equivalent: operators are
absolute functions of their inputs, not incremental per-frame mutations.

## Canonical additive coordinates

Parameters should use an additive coordinate wherever practical before the
effective render state is converted to its final representation.

Examples:

- pitch effects contribute IT linear-pitch units / log-frequency deltas;
- note, channel and global volume slides contribute gain-coordinate deltas;
- panning operators contribute spatial-X deltas;
- tempo slides contribute tempo deltas.

Modulators such as vibrato, tremolo, arpeggio and panbrello are transient
operators. Slides and tone portamento are persistent operators. Tremor and
other intrinsically discrete operations may still use thresholded/discrete
operator outputs; the architecture does not require every operator to be
continuous.

## Runtime ownership

The same operator model is used at the three additive playback scopes:

- a **voice** owns its pitch and note-volume baseline plus voice operators;
- a **physical channel** owns channel volume and spatial-position baseline plus
  channel operators;
- the **playback session** owns global volume and tempo baseline plus global
  operators.

An operator never replaces another operator's current value. Each one updates
only its own contribution, and effective state is composed from the baseline
and all currently active contributions.

Pitch uses linear/log-frequency units while operators are active, then converts
the sum to a multiplier for rendering. Tone portamento therefore commits its
audible pitch delta at row end while retaining its hidden continuous glissando
position separately as tracker-compatibility state.

Intrinsically discrete operations do not have to invent an additive numeric
coordinate merely to fit the vector. Tremor, note cut/delay and retrigger may
use row-scoped discrete operator state or threshold crossings. The requirement
is lifecycle/compositional isolation: they must not mutate unrelated baseline
parameters incrementally per output frame.

## Tracker row time

A tracker compatibility operator receives both wall time and a continuous
tracker row coordinate, `row_t`.

For a normal row whose captured speed is `S`:

```
0 <= row_t <= S
```

`Axx` is resolved before row operators are constructed, so those operators
capture the row's effective speed. Fine-delay extensions may enlarge the
compatibility row span when the tracker format explicitly extends the row.

At constant tempo `T`:

```
d(row_t) / dt = T / 2.5
```

A tracker operator may therefore use `row_t` while a native operator ignores
it and uses wall time.

## Preserve row results, not tick-processing artifacts

The compatibility layer preserves the amount of change an IT effect would have
accumulated over the row, but it does not preserve incidental implementation
details such as "only execute on nonzero ticks" for effects that are naturally
continuous.

For example, if a legacy slide changes a parameter by `d` on each of the
`S - 1` nonzero ticks, its total row delta is

```
D = (S - 1) * d
```

The continuous Heresy compatibility operator uses

```
delta(row_t) = D * row_t / S
```

through the row. At `row_t = S`, `D` is committed to the baseline. If the
same effect appears on the next row, that row creates a fresh operator whose
delta starts at zero relative to the newly committed baseline. Consecutive
rows therefore join continuously without exposing legacy tick stair-steps.

Fine/immediate effects remain instantaneous when that immediacy is musically
meaningful.

## Transient modulation

Vibrato, tremolo, arpeggio and panbrello do not commit their instantaneous
output delta to the playback baseline at row end. Their tracker-compatibility
state (effect memory, selected waveform, oscillator phase, deterministic random
state, and similar state) is distinct from the baseline sound parameters and
may persist where the format requires it.

A displaced NNA voice captures the effective state it had when displaced.
Later physical-channel operators do not continue to affect that virtual voice.

## Tempo slides

Tempo is global baseline state plus any active row tempo operator.

For a row beginning at baseline tempo `T0`, a tracker tempo slide is first
resolved using IT compatibility rules to determine the tempo `T1` that would
exist at the next-row boundary. Heresy then represents that result as a
continuous row operator:

```
T(row_t) = T0 + (T1 - T0) * row_t / S
```

The row coordinate therefore advances nonlinearly in wall time:

```
d(row_t) / dt = T(row_t) / 2.5
```

but it still traverses the same fixed domain from 0 to `S`. At the row
boundary the final tempo delta is committed, so the next row's baseline tempo
is `T1` even if no further tempo effect is present.

An instantaneous tempo set changes the baseline immediately instead of creating
a ramp operator.

This row-time mapping is tracker compatibility infrastructure only. It is not a
replacement clock for native effects.

In Render, `TrackerTickClock` is the analytic integration/inversion of that
tracker timing trajectory. It maps wall time to `row_t` for compatibility
operators; it does **not** own effect state and it does not redefine native
wall-time functors. The live global tempo operator and the analytic timing map
must describe the same trajectory.


## Authorable parametric expressions

`ParametricPatternEffect` uses the existing
`FunctionalRowPlaybackOperator` additive operator model and
snapshot-owned restricted-C# evaluator. Its `Duration` is a
**wall-time `TimeSpan`**, independent of tracker tempo; its
`elapsedSeconds` is relative to the effect's start, while
`rowTime` is the optional continuous tracker-tick coordinate
measured from that event. The end frame is exclusive. The
last expression value is evaluated at expiry and either
committed once or removed, according to `CommitOnExpire`.
Multiple simultaneous operators sum their absolute deltas
against a shared baseline without overwriting one another.

Five destinations are authorable: session
`GlobalVolume`, channel `OverallChannelVolume` and
`SpatialX`, and current-voice `NoteVolume` and
`PitchLinearUnits`. Voice effects follow NNA displacement.
The separate analytic `Tempo` coordinate is not exposed as
an arbitrary scripted modulator; tracker tempo/ramp updates
remain responsible for the timing map.

Every expression is compiled from the frozen document
before playback begins. Native effects never compile in the
PCM worker or callback. A nonfinite evaluated delta fails
deterministically rather than contaminating the mix.
Explicit Pattern cursor cancellation removes its active
operators without terminal commit; natural completion permits
the declared wall duration to finish. Further contracts and
test names: [script programmability](script-programmability.md).


## Tracker-effect keyboard semantics and authoring follow-ups — 12 October 2026

All supported tracker-effect command letters are parsed from the
same semantic effect codec. **Axx** now has an editable tracker
speed record, translated to the existing global SetSpeedCommand
for nonzero values; A00 is retained as an inert editable byte, never
a zero-ticks-per-row timing command. The S command is parsed for
*every possible byte*. Values with supported subcommands select
their corresponding concrete effects; values without a supported
meaning use `UndefinedTrackerSpecialPatternEffect(byte)`, which
persists exactly through song JSON and clipboard and emits no
playback instruction. This includes S00 when typing the bare S
command into a blank tracker slot. Each typed parameter nibble
re-evaluates and replaces the semantic record for the **current
byte**, including transitions from defined to undefined and back.
Existing cursor vertical advance remains after the final nibble.
S73–S76 are therefore editable without requiring a pre-existing
special-effect slot. Unknown Sxx values are explicitly no-op,
not other S subcommands guessed by prefix.

The **Insert Tracker Effect...** picker is implemented in both
empty and occupied effect cells. The empty-cell context menu has a
direct submenu listing all 26 IT command letters A–Z, each accompanied
by its conventional full name and a coloured swatch identical to the
letter-derived sticker colour. Picking one immediately inserts a
concrete effect with parameter byte 00; S00 is preserved as the
intentional editable undefined special, not translated to another
special subtype. On an occupied effect, the picker has **Before
selected** and **After selected** submenus. Insertion is anchored
to the actual effect tab whose context menu was opened, even if
keyboard focus previously selected another tab; the newly inserted
command is the expanded selected tab, with command field active.
Stack ordering and later native effects are preserved. The original
**Insert Tracker Slot Before/After**, **Insert Native Effect Before/After...**,
copy/paste, and native parameter editor are unchanged. The
`PatternEffectCodec.TrackerCommands` collection is the single list
of available letters, names and creation factories. Keyboard entry
uses the same factory; picker and stickers share
`PatternEffectCodec.TrackerColorKey` and the existing palette.
Avalonia and document regression tests cover picker parity, colour
swatches, contextual submenu hierarchy, clicked-index before/after
ordering and direct insertion into blank sequence-mapped cells.

The contextual guidance line remains **separately tracked work**,
sharing the same vertical status area as real diagnostics. The
native Enter configuration dialog already exists and must be called
**Configure effect with Enter**, not described as a future dialog.
A multi-item collapsed stack instead says **Expand stacked effects
with Enter**.
