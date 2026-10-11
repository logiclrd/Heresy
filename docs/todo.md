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
Sustain-guide boundary moves, bidirectional region selection, single-node
sustain by guide double-click, and selected-endpoint Match are now
implemented. Headless/shared-editor synchronization, numeric draft
conflict protection, navigation and direct FM/Instrument render lifecycle
checks are automated. The remaining node-envelope milestone is desktop
hardware and actual audible playback verification:

- [ ] **Physically validate node-envelope editing and audible release tails.**
  Windows desktop feedback identified inadequate sustain shading,
  ambiguous outward guide drags, missing fixed display bounds, missing
  drag preview, no-sustain mode, and keyboard interactions. The first
  corrections are implemented and regression-tested. Subsequent
  feedback clarified five-pixel guide hit targets, guide hover cursors,
  crossing boundaries, interior-guide drags, and thicker boundary
  lines, now covered by further regression tests. A fresh hands-on
  Windows check is still required before closure.
  Automated regression coverage exercises shared standalone/FM editor
  synchronization, unsaved-draft conflict prevention, navigation,
  simulated headless pointer dragging, and chunk-invariant held/released/
  retired rendering for both direct FM and sample-backed Instruments.
  On a real desktop, check node and guide priority, mouse/touchpad
  drags in both directions, double-click insertion/deletion and single-
  node sustain, lost-capture cancellation, and Match button state.
  Verify that edits made in the FM inspector appear in the standalone
  editor and survive reopening the song. Audition actual Instrument
  notes and the FM editor's Test notes through held, Note Off, and
  final silence, using both Forward and PingPong sustain.
  This physical-audio and window-system check must remain open until
  observed on actual desktop hardware; CI/headless cannot certify it.

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
