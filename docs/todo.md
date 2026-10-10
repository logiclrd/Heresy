# Heresy — open work

**Only genuinely unfinished work belongs here.** Completed work and
red-to-green regressions live in focused [reference docs](README.md)
or the [chronological engineering log](incremental-sequencing.md).
Reconciled October 10, 2026: **four open items**. None is an
unconditional outstanding playback-correctness task.

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
