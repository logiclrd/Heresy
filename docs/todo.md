# Heresy — outstanding work

Only unfinished, independently actionable work belongs here. Completed
architecture and regression history are documented in the
[documentation index](README.md). The original-design programmability
features, including scripted Patterns, Sequences, Envelopes, Instruments,
virtual-voice controls, and compiled parametric effects, are implemented.

## Node-envelope redesign

The [node-envelope design contract](node-envelope-design.md) records the
requested model, sustain timeline semantics, and exact graphical gestures.
The validated node model, immutable renderer, JSON persistence and
production authoring cutover are implemented for both Instruments and FM
graphs. The four-scalar ADSR definition, renderer, and graphical editor have
been removed without a legacy-song migration. The shared interactive graph now renders the curve and node guides,
supports 4 px single-node selection, dragging, line insertion and node
deletion; the node-list editor stays available for precise values.
Sustain-guide gestures and Match remain separate. Continue in this order:

- [ ] **Implement sustain-guide gestures and Match editing.**
  Drag either sustain endpoint's guide to another node without crossing
  the opposite endpoint; dragging a non-highlighted node guide in either
  direction selects a new region. Self-drag is a no-op, while a
  double-click on a node guide defines a single-node sustain.
  Preview during drag; commit on release and cancel on capture loss.
  When a selected node is one of two distinct sustain endpoints,
  show Match, disable it when values agree, and otherwise copy the
  opposite endpoint's value in one audio-revision commit.

- [ ] **Complete node-envelope UI integration and physical audition.**
  Use the same interactive control in the standalone Envelope editor
  and FM graph inspector, remove stale ADSR wording, ensure navigation
  and shared-object edits update correctly, and verify direct/instrument/FM
  playback through held, releasing and finished stages. Include desktop
  pointer/keyboard smoke checks and the final CI regression suite.

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
