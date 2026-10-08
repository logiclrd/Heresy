# Heresy TODO

This file tracks the remaining requirements recovered from the original design
and the current repository audit (2026-10-08). Work should be implemented
test-first with a red checkpoint, green GitHub Actions, and focused documentation.
Items that were already completed remain in Git history, not in this checklist.

## Recursive sound sources — architecture priority

- [ ] Resolve data/script **Patterns and Sequences as playable note sources**
  through the concrete playback sound resolver, including from instruments and
  other nested source graphs. They must render in realtime and offline rather
  than resolve silently.
- [ ] Preserve the original distinction between **flattened** nested invocation
  (mapped child channels, shared parent sequencing state where appropriate)
  and **mixdown** (private child sequencing state and a cooked multichannel
  signal exposed as one parent playback voice). A playback-channel mixdown must
  not collapse physical speaker feeds to mono.
- [ ] Complete **concurrent flattened child/parent timing**.
  Immediate child tempo changes and a first chronological queue for future
  child-row SetTempo events now reach the parent at their due wall time,
  including within-row changes across sequence orders. Replace the queue's
  precalculated wall-clock due times with a unified tick-domain scheduler
  capable of interleaving parent and child row boundaries when an intervening
  tempo change moves a later child event. Include nested tempo ramps,
  overlapping parent tempo slides, speed changes and pattern/fine delays.
  Detected unsupported combinations should fail explicitly, never silently
  produce an invalid shared timeline.
- [ ] Defer **future flattened child channel-state changes** (remembered
  Source, tracker effect memory and related commands) until the actual
  chronological child-row boundary. Although sources resolve correctly when
  each row executes, eager compilation of all future child rows can still
  mutate shared channel state too early. Add tests where a parent note falls
  between a child invocation and a later source/effect change.
- [ ] Propagate pitch, playback-speed multiplier, origin timing, Note Off/Cut,
  envelope/release behavior and new-note actions through nested sources.
  Preserve snapshot isolation, deterministic random/effect memory, script
  compilation safety, event caps, and finite/infinite lifetime semantics.
- [ ] Implement mixdown **native source-frame seeking**, including accurate
  ReplayRequired seeking and Oxx/retrigger behavior without silently dropping
  expensive but legal offsets. Add tests for nested patterns, sequences,
  instruments, repeated invocation, notes/releases, seek, and offline parity.
- [ ] Detect or safely bound recursive object-reference cycles and
  unbounded event expansion without hanging playback or export.

## Output audio configuration and physical speaker processing

- [ ] Apply configured per-output-channel filtering to the **final speaker
  feeds**, independently of the existing per-voice tracker resonant filter;
  cover None, LowPass and HighPass and continuity across render blocks.
- [ ] Expose configuration of output channel count/layout (including 5.1 and
  7.1), speaker positions, positional importance, optional speaker filters
  and cutoff frequencies, and sample rate. Wire the chosen configuration to
  realtime and offline paths, preserving the same PCM engine and proper
  distinctions between playback channels and speaker outputs.
- [ ] Test spatial and filtered output routing, including arbitrary output
  layouts, stable multi-block rendering, and configured channel ordering.

## Export workflow

- [ ] Show offline rendering progress in **rendered musical time** against
  the logical length (not guessed wall-clock ETA).
- [ ] Add cooperative cancellation at safe render blocks, preserving the
  atomic temporary-output-file behavior and leaving existing exports intact.
- [ ] Support user-selectable output configuration for export and additional
  WAV depths beyond default 16-bit PCM. Retain FLAC as default and MP3/WAV.
- [ ] Ensure export and realtime render identically for equal snapshots and
  render configurations, aside from intentionally different end-of-song
  handling and output encoding.

## Asset portability

- [ ] Add an interactive recovery flow for missing/unreadable sample assets
  when opening an existing project: select substitute files or search folders,
  validate loaded encoded data, and retry without mutating the current
  document on failure. Preserve in-memory decoded PCM and archive semantics.

## Playback state and authoring feedback

- [ ] Measure/report actual realtime audio underruns. Late audio produces a
  temporary dropout without skipping the musical timeline or stopping playback.
  The UI underrun indicator must be **hidden until the first underrun**.
- [ ] Show playback-affecting changes since the most recent playback snapshot,
  separately from unsaved-file status, using document/audio revision tracking.
- [ ] Highlight Oxx/offset operations on ReplayRequired mixdown sources when
  the runtime capability indicates expensive realtime seeking. Explain in a
  tooltip that export remains correct; provide an option to suppress warnings.
  Do not prohibit these operations.

## Startup branding and application identity

- [ ] Use `Heresy.UserInterface/Images/Icon.ico` as the main window icon
  so the application has its own icon in the title bar, taskbar, Alt-Tab,
  and other window-manager UI on supported platforms.
- [ ] Embed `Heresy.UserInterface/Images/Icon.ico` as the Windows executable
  icon in the Windows build, including the native apphost/stub `.exe` file,
  while preserving cross-platform builds.
- [ ] On startup show the supplied `Heresy.UserInterface/Images/Logo.axaml`
  control in a separate chromeless splash window in front of the main window.
  Dismiss it on any keyboard key, any pointer click, or automatically after
  four seconds, whichever happens first. Do not delay or block main-window
  initialization or leave the splash open when the main window closes.

## Documentation and later maintenance

- [ ] Reconcile stale README descriptions of WAVE-only realtime sample
  decoding with the implemented **load/import-time** WAVE/FLAC/MP3/OGG/AIFF
  decoding and immutable, shared in-memory PCM playback architecture. Treat
  docs/sample-storage.md and current code as authoritative.
- [ ] Add format-version migration tooling **only when** actual documents
  require schema evolution; intentionally retain format version 1 during
  pre-release development.

Completed implementation history is preserved in Git, while stable architecture
belongs in the README and focused documentation.
