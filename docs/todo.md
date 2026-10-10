# Heresy — outstanding work

Only unfinished, independently actionable work belongs here. Completed
architecture and regression history are documented in the
[documentation index](README.md). The original-design programmability
features, including scripted Patterns, Sequences, Envelopes, Instruments,
virtual-voice controls, and compiled parametric effects, are implemented.

## Physical validation

- [ ] **Validate surround playback and encoded multichannel output on real
  hardware.** Verify SDL speaker routing on actual 5.1/7.1 devices and
  FLAC/MP3 encoder-specific supported layouts with playable outputs.
  Automated PCM, WAV speaker-mask and software routing tests are already
  in place. Completion requires physical-device/codec evidence, not only
  synthetic tests. See [audio output](audio-output.md) and
  [export workflow](export-workflow.md).

## Conditional improvements

- [ ] **Consider bounded seek-reconstruction checkpoints only if measured
  underruns justify them.** Profile long-distance Oxx/Qxy private-source
  seeking with the existing underrun instrumentation. Introduce
  deterministic, memory-bounded checkpoints only when measurements
  demonstrate real-time PCM-ring underruns. Preserve native-frame
  accuracy and the coroutine renderer; do not restore eager event
  journals or PCM caching. See [source-seek hints](source-seek-hints.md).

- [ ] **Consider determinate export progress only with a proven finite
  duration.** A percentage must be based on a logically established
  total without pre-expanding arbitrary scripted sequences. Continue
  reporting rendered musical time and indeterminate progress otherwise.
  See [export workflow](export-workflow.md).

## Deferred compatibility work

- [ ] **Add persisted-format migrations when a genuine schema change
  requires them.** The current `.hm.json` / `.hm` format remains at
  version 1. Do not preemptively invent version 2. Existing five-pane
  migration, sample metadata tolerance and stable-ID behavior remain
  supported. See [document panes](document-panes.md) and
  [sample storage](sample-storage.md).

## Maintenance policy

New tasks need a reproducible defect or concrete feature, testable
acceptance criteria, and a link to the relevant design contract.
When completing a task, update its documentation, add regression
coverage where appropriate, and verify CI on the final commit.
Do not re-add historical completed milestones as open work.
