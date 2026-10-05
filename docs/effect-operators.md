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
