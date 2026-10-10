# Heresy — open work

**Only genuinely unfinished work belongs here.** Completed work and
red-to-green regressions live in focused [reference docs](README.md)
or the [chronological engineering log](incremental-sequencing.md).
Reconciled October 10, 2026. The original design's programmability
features are **active work**; the hardware/optimization/schema tasks
below remain conditional. This is not a list of completed milestones.

## Original design — remaining programmability

The [programmability architecture](script-programmability.md) separates
persisted authoring objects from compiled, snapshot-owned runtime behavior.
The existing restricted-C# Pattern/Sequence scripts, sound/envelope resolver
interfaces, and compositional effect operators are starting points, **not**
proof that the following authorable features are complete.

### Pattern-script virtual channels

Both Pattern-script virtual-channel slices are complete: lifecycle and
broadcast helpers, and the scoped voice-local volume, native frame seek,
displacement and past-note actions. Their validation and renderer
semantics are documented in
[script programmability](script-programmability.md).
Further work requires a new concrete regression or feature request.

### Programmable object types

Script-defined Envelopes (expression compilation, bounded release,
Instrument/FM rendering, persistence and authoring) are complete;
see [script programmability](script-programmability.md).

- [ ] **Implement script-defined Instruments.** Add a persisted Instrument
  subtype with a deterministic compiled tone-selection function taking the
  invoked pitch (and any explicitly documented context), yielding a source,
  pitch multiplier and per-target optional Envelope overrides, or silence.
  The selection must occur per note invocation, preserve recursive/private
  source handling and inherited envelopes, and never run compilation on the
  SDL callback. Specify bounded evaluation, stable object-reference and
  tombstone behavior for dynamic selectors, recursive rejection, snapshots,
  serialization, authoring and diagnostics. Keep existing tone-grid
  Instruments unchanged and regression-tested.

- [ ] **Make native parametric effects authorable as compiled expressions.**
  Define a persisted semantic effect with destination scope/parameter,
  lifetime, wall-time (and optional tracker row-time) input, persistent versus
  transient commit policy and expression source. Compile under a bounded
  deterministic restricted-C# subset before playback and evaluate as a
  per-frame absolute operator contribution, never by mutating other
  operators' baselines. Enforce finite outputs/error policy, shared-clock
  deadlines, lifecycle ownership and chunk-invariant realtime/offline PCM.
  Add editing, serialization, validation and tests; reuse the existing
  `FunctionalRowPlaybackOperator` style of composition without treating
  tracker-specific semantics as native wall-time semantics.

## Physical validation

- [ ] **Verify real 5.1/7.1 output mapping and encoder-specific multichannel
  FLAC/MP3 export** on supported physical devices/codecs. Check SDL
  speaker order, per-device behavior and playable encoded layouts.
  Software PCM parity, speaker routing/filtering and WAV channel masks
  are already tested. **Requires real hardware/integration evidence**;
  synthetic tests alone cannot close it.
  See [audio output](audio-output.md) and [export workflow](export-workflow.md).

## Evidence-dependent improvements

- [ ] **Optionally add bounded deterministic reconstruction checkpoints**
  for expensive long-distance native Oxx/Qxy seeks in private recursive
  sources, **only after measurements show replay-required seeking causes
  real-time PCM-ring underruns**. Preserve native-frame accuracy,
  deterministic replay and the one-coroutine architecture. Do not
  reintroduce unbounded journals, future schedules or cached PCM.
  See [seek hints](source-seek-hints.md) and
  [recursive sounds](recursive-sounds.md).

- [ ] **Optionally display determinate export progress** only when an
  independently proven finite logical total can be obtained without
  expanding future script output. Arbitrarily scripted/indefinite
  sequences must continue to show rendered musical time with
  indeterminate progress. No fabricated percentages or ETAs.
  See [export workflow](export-workflow.md).

## Deferred until schema evolution

- [ ] **Introduce format-version migration tooling only when a genuine
  persisted format change requires it.** The pre-release schema
  deliberately remains **version 1**; legacy four-pane tree migration,
  stable IDs and old sample-metadata tolerance are already implemented.
  Do not create a speculative v2 or migration framework.
  See [document panes](document-panes.md) and
  [sample storage](sample-storage.md).

## Adding new tasks

Add a narrowly scoped checkbox with a reproducible bug or independently
actionable feature and an acceptance criterion. When closing it,
update the relevant topical contract and tests, and verify GitHub
Actions green at the final commit. **Do not reopen the completed
advanced lifecycle, flattened ownership, source-frequency, seeking,
or missing-asset recovery series** without a concrete failing example.
