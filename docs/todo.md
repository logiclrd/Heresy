# Heresy — outstanding work

This file contains **only open, independently actionable work**. Current
architecture and implemented behavior belong in the [documentation index](README.md)
and linked design contracts; completed milestones remain available in Git history.
Do not reintroduce completed-task checklists here.

## Synth graph — Multi node

- [ ] **Implement a polyphonic Multi node in the Synth filter family.**
  One audio input is independently instantiated at multiple related musical
  frequencies and mixed. Multi must work with arbitrary upstream subgraphs,
  including oscillators, sampled/Instrument/Synth Sources, operators, filters
  and nested Multis; voice-local oscillator, child playback, envelope and DSP
  state must not be shared. Existing Constant/pitch semantics, deterministic
  replay, note-off/tails, bounded preparation and realtime allocation constraints
  must be preserved. Graph authoring, connection routing, source import,
  persistence, cloning and save/reload must all support the new node.

  **Distributions.** Spread places N voices evenly across total width D
  cents (N=1 is the exact note; even N omits it, odd N includes it).
  Harmonic accepts space-separated positive frequency ratios, including
  decimals or fractions (e.g. `1/2 1 2 3`). Chord accepts space-separated
  signed floating semitones with the same parser (e.g. `0 4 12`, `7/2`);
  its frequency ratios are `2^(semitones/12)`. Reject nonfinite entries,
  zero denominator, empty lists and invalid harmonic ratios. Both harmonic
  and chord component order and authored text should survive persistence.

  **Mixing.** Persist `Gain Compensation`: checked = arithmetic mean of
  constituent outputs, unchecked = straight additive sum. On each manual
  Distribution change, automatically check it for Spread and uncheck for
  Harmonic/Chord; otherwise a user override persists. Choosing a preset
  always resets Gain Compensation according to the preset Distribution even
  when the Distribution value did not change.

  **Phase and cycle.** Persist `Randomize Phase` (default checked).
  When checked, apply independently seeded deterministic playback-time offsets
  to *the entire* constituent input subgraph (not just oscillators), up to
  one resolved cycle. Pre-roll must reconstruct internal DSP/PCM state;
  randomized offsets must not change the authored note-on/note-off schedule.
  Each offset of K skipped output samples has a voice-local fade-in of K/2
  samples (smooth envelope; no fade for K=0), applied after upstream processing.
  Changing Distribution alone never changes Randomize Phase, but choosing
  *any* preset always turns it on.

  **Override cycle.** A checkbox `Override cycle` controls visibility of a
  `Cycle Length` numeric field, with no explanation placeholder when hidden.
  Persist a nullable positive finite fractional number of samples at neutral
  C4 and reference 48 kHz; round the effective sample count after scaling
  to actual output rate and constituent pitch. With no override, infer a
  reliably known simple oscillator's cycle; otherwise assume the requested
  nominal note frequency (A4=440 Hz). Inferred values are never persisted.
  Unchecking the override removes its persisted value but keeps the field
  draft while the inspector remains selected; rechecking restores that draft,
  while navigating away and back discards it.

  **Presets.** Show a `Presets` drop-down which resets its selection label
  after application. Selecting any preset applies all its distribution values,
  resets Gain Compensation for the selected Distribution, sets Randomize Phase
  to true, and leaves Cycle Length override unchanged:
  `Super saw/super square` = Spread 12 cents, 7 voices;
  `Unison` = Spread 15 cents, 2 voices;
  `Chorus` = Spread 25 cents, 4 voices;
  `Power Chord` = Harmonic `1 3/2 2`;
  `Major Third` = Chord `0 4`;
  `Major Sixth` = Chord `0 9`;
  `Major Triad` = Chord `0 4 7`;
  `Minor Third` = Chord `0 3`;
  `Minor Sixth` = Chord `0 8`;
  `Minor Triad` = Chord `0 3 7`.
  Regression-test exact pitches, even/odd spread, gain, preset state changes,
  fractions, sample-based phase behavior/fade, nested state, seek/replay,
  persistence and realtime stability.

## Desktop and listening acceptance

### Pattern editing

- [ ] **Finish physical Pattern row Insert/Delete acceptance on Windows and Linux.**
  On both desktops, exercise Insert/Delete and Alt+Insert/Delete near the
  beginning/end of long populated, multi-channel Patterns and reused Patterns
  in Sequences. Check selection, cursor/focus, scroll position, continued
  realtime playback and underruns. Distinguish Pattern row actions from
  Alt+Insert/Delete in the effect stack, and record any remaining lag or
  platform-specific defects. Linux testing already found major improvement:
  current-channel row edits keep up with normal keyboard repeat, while
  all-channel edits sustain about 4–5 updates/s with some latency; this is
  **not** Windows/device/functional acceptance. Record observations in
  [Pattern row mutation performance](pattern-row-mutation-performance.md).

### Filters

- [ ] **Finish physical filter-feature listening and desktop acceptance.**
  Complete manual case **11**, per-speaker final-output filter choices,
  independence and preference persistence; and case **12**, filtered
  realtime-versus-export parity, release/tail behavior, actual Windows/Linux
  audio devices and underrun diagnostics. Exercise expensive configurations
  when practical and distinguish missed realtime deadlines from correct but
  slower-than-realtime offline exports. Cases **1–10** have already passed
  their documented manual scopes; do not repeat them without a new regression.
  See [filter physical acceptance](filter-physical-acceptance.md) and
  [realtime filter qualification](filter-realtime-qualification.md).
  CI/PCM equivalence alone cannot close hardware listening checks.

### Node envelopes and FM release

- [ ] **Physically validate node-envelope editing and audible release tails.**
  On a real Windows desktop, recheck the final five-pixel guide hit targets,
  node-versus-guide priority, guide hover cursors, outward and crossing
  drags, sustain-boundary guides, single-node sustain, Match, drag previews,
  keyboard focus, lost-capture cancellation and the fixed display domain
  with its sticky/right-edge refit control. Confirm edits synchronize between
  standalone Envelope and FM inspectors, survive save/reload, and preserve
  unsaved numeric drafts appropriately. Audition held, Note Off, release and
  retirement for Instruments and FM Test notes with **Forward, PingPong
  and no-sustain** modes. Recheck the formerly reported FM modulation-only
  finite Envelope retirement and the sticky-domain/release patch on actual
  audio; the original problematic patch values were not captured.
  Prior manual Windows/Linux editing and headless PCM tests do **not**
  establish these outstanding audible and hands-on results. See
  [interactive envelope graph](envelope-graph.md) and
  [node envelope design](node-envelope-design.md).

### Multichannel output

- [ ] **Validate surround playback and encoded multichannel output on real
  hardware.** Verify SDL speaker routing on actual 5.1/7.1 devices and
  FLAC/MP3 encoder-specific supported layouts with playable outputs.
  Automated PCM, WAV speaker-mask and software routing tests already exist;
  physical-device and codec evidence is still required. See
  [audio output](audio-output.md) and [export workflow](export-workflow.md).

## Conditional improvements

These are contingent on evidence; they are **not** authorization to
preemptively alter the production architecture.

- [ ] **Consider bounded seek-reconstruction checkpoints only if measured
  underruns justify them.** Profile long-distance Oxx/Qxy private-source
  seeking with the existing underrun instrumentation. Introduce
  deterministic, memory-bounded checkpoints only if measurements demonstrate
  realtime PCM-ring underruns. Preserve native-frame accuracy and the
  coroutine renderer; do not restore eager event journals or PCM caching.
  See [source-seek hints](source-seek-hints.md).

- [ ] **Consider determinate export progress only with a proven finite
  duration.** A percentage must use a logically established total without
  pre-expanding arbitrary scripted sequences. Otherwise continue reporting
  rendered musical time and indeterminate progress. See
  [export workflow](export-workflow.md).

## Deferred compatibility

- [ ] **Add persisted-format migrations only when a genuine schema change
  requires them.** The current `.hm.json`/`.hm` format remains version 1;
  do not invent version 2 preemptively. Existing four-/five-/six-pane
  compatibility, sample metadata tolerance and stable IDs remain supported.
  See [document panes](document-panes.md) and
  [sample storage](sample-storage.md).

## Maintenance policy

New tasks require a reproducible defect or concrete feature, testable
acceptance criteria and the relevant design-contract link. On completion,
update the reference documentation, add appropriate regression coverage,
verify CI on the final commit, then **remove the completed entry** from
this file. Git history preserves the implementation chronology.
