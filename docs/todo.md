# Heresy — outstanding work

Only unfinished, independently actionable work belongs here. Completed
architecture and regression history are documented in the
[documentation index](README.md). The original-design programmability
features, including scripted Patterns, Sequences, Envelopes, Instruments,
virtual-voice controls, and compiled parametric effects, are implemented.

## FM oscillator pitch independence

- [ ] **Add a per-oscillator `Constant` frequency flag for fixed-rate FM modulation.**
  Place a checkbox labelled **Constant** directly below the oscillator's
  **Frequency Hz** field in the FM synth inspector, unchecked by default.
  When checked, the selected oscillator runs at its configured frequency
  in wall time, independent of the requested note, inherited instrument
  pitch, playback-speed pitch scaling, and note-level pitch modulation.
  Leave its optional connected multiplier input functional: this
  checkbox removes the *note pitch* multiplier, not modulation supplied
  explicitly by the FM graph. Other oscillators remain pitch-relative.
  Store the flag on `FmOscillatorNode`; support all clone/import/graph
  edit and song JSON round-trip paths, including snapshot isolation.
  Update `FmSynthSound` to bypass the pitch multiplier for that oscillator
  only. Add regressions comparing a fixed-rate vibrato LFO at two note
  pitches, independent oscillators in one graph, modulation inputs,
  and persisted/reopened patches. Verify the checkbox position and
  immediate document/audio revision on toggle.

## Node-envelope redesign

The [node-envelope design contract](node-envelope-design.md) records the
requested model, sustain timeline semantics, and exact graphical gestures.
The validated node model (including atomic authoring, JSON persistence and
snapshot copies) and the immutable renderer are implemented, with production
node-envelope resolution for both Instruments and FM graphs. ADSR continues
temporarily until the explicit editor/format cutover below; there is
deliberately no legacy-song migration. Continue in this order:

- [ ] **Cut over production envelope editing and remove ADSR entirely.**
  Replace default Envelope creation, standalone editor, FM node inspector,
  cloning/import, serializers and tests that still assume four ADSR
  scalars. Remove `AdsrEnvelopeDefinition`, `AdsrEnvelopeCurve`, and
  old graph/numeric editing code instead of retaining compatibility
  adapters. A minimal placeholder editor is acceptable during the
  graphical rebuild; keep Script Envelopes independent. Do not create a
  persisted-format migration or version bump for nonexistent legacy songs.

- [ ] **Build the interactive node-envelope graph and selection model.**
  Render linear segments, faint vertical node guides, and the sustain
  highlight `#04808080`; support one selection at most, a **4 px**
  nearest-node click threshold, and small non-touching selection brackets
  2 px taller than the node. Double-click a node to delete, or a segment
  to insert at the nearest projected point; validate monotonic time,
  sustain index adjustment, first-node invariants, and deletion limits.
  Exercise hit testing and projection in framework-independent tests.

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
