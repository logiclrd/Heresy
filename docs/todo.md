# Heresy — outstanding work

This file contains **only open, independently actionable work**. Current
architecture and implemented behavior belong in the [documentation index](README.md)
and linked design contracts; completed milestones remain available in Git history.
Do not reintroduce completed-task checklists here.

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
