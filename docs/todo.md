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

- [ ] **Complete broader physical acceptance of flattened-source Gxx and
  instantaneous GFF.** **Manually verified (limited):** a single-channel
  Pattern used as an instrument inside another Pattern responded correctly
  to both `G01` (slow continuous portamento) and `GFF` (instant tone
  change). This verifies the core use case, **not** the complexities below.
  **Not yet explicitly manually verified:** multi-channel nested Patterns,
  nested Sequence sources, multiple levels of flattening, simultaneous
  unrelated channels sharing physical hosts, displaced/NNA and virtual
  descendant voices, `G00` target/speed memory, `Lxx` combined pitch and
  volume effects, future Instrument-tone selection after retuning,
  interactions with vibrato, envelopes and glissando, release and native
  sample-position continuity, and `GFF` on ordinary Samples, Instruments,
  Synths and private mixdowns. Recheck the Pattern editor's note/`?` and
  unsupported-effect warning presentation, and verify no inadvertent
  source restarts, altered child-row timing, clicks or underruns. Test on
  Windows and Linux where practicable and record results per case rather
  than treating the one-channel test as blanket acceptance. Automated
  PCM, NNA, nesting and producer tests passed, but do not replace these
  physical listening/UI checks. See [effect operators](effect-operators.md)
  and [UI workflow](user-interface-workflows.md).

- [ ] **Commit partially entered Pattern volume-command bytes on focus loss.**
  If the first hexadecimal character/nybble of a volume change has been
  entered and the Pattern editor loses focus before the second one, commit
  that first high nybble together with **exactly the low nybble currently
  displayed for the partial edit** (the prior low nybble or zero). Do not
  discard the partial value or substitute a different unseen low nybble.
  Exercise switching Pattern focus by mouse, keyboard and pane changes;
  preserve complete two-nybble entry, undo/redo and saved values. See
  [UI workflow](user-interface-workflows.md).

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

## Main window — object management

- [ ] **Add Duplicate Object to every main-window object list.**
  Provide a consistent **Duplicate Object** action in each object list,
  creating a new independently identifiable object of the same kind with
  the selected object's content and settings copied according to that
  object's existing reference/ownership semantics. Keep the original
  unchanged, give the copy a usable name and selection, and maintain valid
  internal IDs, references, document persistence and undo/redo where
  applicable. Audit each list rather than implementing just one object
  type; check both complex objects and objects referenced elsewhere.
  See [UI workflow](user-interface-workflows.md).

## Envelope editor — graph affordances

- [ ] **Draw thin green horizontal Vmin and Vmax guide lines.**
  The Envelope editor's graph rescales and offsets the visible nodes, so
  the drawn X baseline is **not** the numeric zero axis. Show separate,
  visually precise horizontal green guides at the actual displayed
  positions of **Vmin** and **Vmax**, updating as the limits, viewport,
  value range or graph transform change. Do not imply that the X line
  represents zero. Verify guides at edge cases including Vmin/Vmax
  outside the node-data span. See [interactive envelope graph](envelope-graph.md)
  and [node envelope design](node-envelope-design.md).

- [ ] **Clamp envelope node-value drags to Vmin/Vmax unless Ctrl is held.**
  During node dragging, restrict the value to the configured inclusive
  limits by default; holding **Ctrl** permits values outside them. Ctrl
  must be handled **continuously during an active drag**: when pressed,
  immediately move to the unclamped value under the current pointer,
  including outside the limits; when released, immediately snap an
  out-of-range node to the corresponding boundary, even if the pointer
  has not moved. Resume the appropriate mode as the pointer moves,
  preserving drag/capture, undo grouping, pointer-to-value mapping
  and unaffected time-axis behaviour. See
  [interactive envelope graph](envelope-graph.md) and
  [node envelope design](node-envelope-design.md).

## Desktop UI — keyboard shortcuts

- [ ] **Support F2 as a keyboard shortcut for Rename wherever appropriate.**
  For renameable items that already offer a context-menu **Rename** action
  (for example, via right-click), pressing **F2** with the item selected and
  keyboard focus in the relevant list/tree/editor should invoke the same
  rename operation. Reuse the existing rename command, editing UI,
  validation, cancellation and commit/persistence behaviour rather than
  introducing a separate rename path. Do not intercept F2 while typing in
  text fields or in contexts where it already has a distinct intended
  meaning; do not enable renaming for non-renameable items. Audit applicable
  item views and verify selection/focus, Enter/ESC, keyboard-only use and
  existing context-menu behaviour on the supported desktops. See
  [UI workflow](user-interface-workflows.md).

## Synth editor — node value ranges and symmetry link

- [ ] **Add Vmin and Vmax properties to Synth Noise nodes.**
  Expose editable **Vmin** and **Vmax** values for Noise nodes in the
  Synth editor, and apply them to the Noise node's generated output range.
  Preserve today's Noise output for existing projects and newly created
  nodes using suitable defaults (the existing bipolar range), while
  allowing explicit range edits to persist, reload, render and take
  effect under the Synth live-parameter policy where compatible.
  Verify endpoint/range mapping and interaction with downstream
  Operators and preview audio.

- [ ] **Add a Vmin/Vmax symmetry-link toggle between their inputs.**
  Wherever a Synth node exposes both **Vmin** and **Vmax** (including
  Oscillator, Envelope and the new Noise properties), put a toggle
  **directly between the Vmin and Vmax text inputs**, whether the
  existing controls were laid out horizontally or vertically. In the
  **Envelope node inspector**, move the Vmin/Vmax labels **above**
  their inputs so the two text inputs occupy a horizontal row with
  a natural central position for the toggle. Use the existing icons
  `Heresy.UserInterface/Images/ChainUnlinked.axaml` and
  `Heresy.UserInterface/Images/ChainLinked.axaml` for unchecked
  and checked visual states, respectively.
  The toggle is **initially checked if and only if Vmin = -Vmax**.
  While checked, editing either bound immediately sets the other to
  its **opposite sign** (Vmin = -Vmax). While unchecked, allow the
  bounds to be edited independently. When the user turns the toggle
  **from unchecked to checked**, perform this exact recentering:
  `newVmax = (oldVmax - oldVmin) / 2`, then
  `newVmin = -newVmax`; update both controls and the rendered node
  through the normal edit/validation/undo path. Turning the toggle
  off must **not** change either value. Check initial state on newly
  created and reopened nodes, both directions of linked editing,
  symmetric/asymmetric values, toggle transitions, layout and
  immediate preview updates.

## Synth editor — UI defects

- [ ] **Distinguish Caps Lock from Shift in Synth numeric text input under
  Xephyr.** With the Synth preview mode set to **Latch** (Caps Lock on),
  entering `0.2` into Vmax currently produces `)>@`, as if Shift were
  held down. Investigate keyboard modifier translation in the Xephyr/X11
  input path and ensure Caps Lock affects alphabetic case without shifting
  digit/punctuation characters. Reproduce with Latch/Caps Lock on and off,
  verify `0.2` and symbols in numeric fields, and regression-test modifier
  handling where possible. Do not alter Latch's intended Caps Lock behavior.
  See [UI workflow](user-interface-workflows.md).

- [ ] **Align Synth graph node attachment points with their labels.**
  Input/output connection anchors currently appear visually displaced from
  the corresponding node-port labels. Audit the graph editor's label,
  socket, hit-target and connection-line coordinates (including any scaling
  or text metrics), correct the visual alignment, and verify mouse hit
  testing and wire drawing stay aligned at different node layouts/scales.
  See [UI workflow](user-interface-workflows.md).

- [ ] **Make Synth graph content extents scrollable with 20 px margins.**
  Treat **every node and every connection edge** as contributing to
  the graph's content extent, expanded by **20 pixels orthogonally in
  each of the four directions**. Allow the viewport to scroll through
  the full expanded bounds; hide scrollbars independently when that
  axis's extents fit. When scrolling is possible, dragging the
  **background** pans the graph without accidentally starting a node
  move, connection edit or other pointer action. Recompute bounds after
  moving, adding, deleting or rewiring graph elements and on resizing
  the viewport. See [UI workflow](user-interface-workflows.md).

- [ ] **Place newly added Synth nodes in unobstructed graph space.**
  Choose a placement that does not intersect existing **node bodies**
  or cover existing **connection edges**, respecting the node/edge
  geometry and 20 px visible-content margin defined by the scrolling
  task. When the chosen position would leave the new node or any of
  its 20 px buffer partially outside the viewport, **automatically
  scroll** to bring the entire node and buffer into view. Check densely
  populated graphs, long edges, changed viewport sizes and repeated
  additions; preserve subsequent manual placement and wiring. See
  [UI workflow](user-interface-workflows.md).

- [ ] **Expose Synth node drag handles within an 8 px proximity zone.**
  Show the node's handles and permit their intended drag interaction
  when the pointer is within **8 pixels of the node boundary**, even
  when not directly over the node body. **Dragging the node body
  itself** must still start only when the pointer actually lands on
  the node. Distinguish handle hit targets from body-move hit targets;
  verify near corners, adjacent nodes, connection ports, pointer
  transitions and graph scrolling. See [UI workflow](user-interface-workflows.md).

## Synth editor — live parameter audition

- [ ] **Apply *all non-structural Synth property changes* to ongoing preview
  notes in realtime, without restarting them.** Already-playing Synth preview
  voices currently retain their prepared graph snapshot after edits. The
  requirement covers every editable property whose change leaves the Synth
  graph structurally compatible, **not merely a selected set of parameters**.
  Oscillator Frequency Hz, Waveform and Curvature, and Filter CutoffHz are
  representative examples, **not a minimum viable subset or exhaustive list**.
  Audit the full Synth inspector's editable properties (across node types,
  including nested/source, filter and Multi parameters) and ensure each
  non-structural property has realtime-update support and regression coverage.
  A property must not be excluded just because applying it requires
  recomputing prepared resources, rebuilding DSP coefficients, or migrating
  compatible per-voice state.

  Introduce versioned, immutable, preprepared parameter updates from
  committed Synth edits to all active held and latched preview voices.
  Preserve continuity of oscillator phase, note/release envelopes,
  child-source playback position and stateful DSP histories wherever
  semantically possible; define explicit, click-safe state migration or
  transition behavior when a non-structural parameter changes the processing
  configuration. Graph **structural** changes (such as adding/removing nodes,
  changing node identity/type, rewiring connections or changing effective
  processing topology) may leave existing voices on the earlier captured
  definition while new notes use the edited graph. Decide structural
  compatibility from actual graph/runtime structure, never merely from
  AudioRevision or whether a parameter has a prepared implementation.
  Preserve graph-generation isolation for subsequent edits. Prepare costly
  resources (including curvature tables and filter/FIR redesign) outside the
  audio callback and publish safely at an audio block boundary.

  Acceptance requires a complete property-by-property audit, automated
  realtime/PCM tests of **every non-structural editable property**, simultaneous
  held and latched notes, rapid successive edits, consistent voice/release
  lifetimes, filter/state transitions, structural-change isolation, latency,
  underruns and physical listening; do not claim the milestone complete after
  testing only the illustrative properties above. Keep live MIDI pitch bend
  as an *independent per-voice/per-MIDI-channel control*, not an authored
  graph mutation, while allowing it to reuse the realtime control/publication
  infrastructure. See [Synth sound source](synth-sound-source.md),
  [UI workflow](user-interface-workflows.md) and [audio output](audio-output.md).

## MIDI keyboard input (deferred until Synth live-parameter infrastructure)

- [ ] **Implement cross-platform MIDI keyboard input, Program Change Source
  control, and continuous microtonal preview pitch bend.** Preserve all
  decisions, routing, device management, acceptance cases and unresolved
  hardware expectations in the [MIDI keyboard design](midi-keyboard-input-design.md).
  Pattern MIDI Note On inserts the pitch at the tracker cursor regardless
  of field focus, redirects focus to Note and advances Skip; Note Off does
  not mutate the pattern. Program Change navigation uses raw base program
  **100**, sends reset Program Change back to the keyboard, moves current
  Source by the signed delta with **clamping**, and is gated by a conditional,
  default-checked **MIDI Control** checkbox. MIDI Synth/Sample audition is
  independent of computer-keyboard Hold/Latch, always Hold, and updates
  ongoing MIDI voices per channel for pitch bend. Capture arbitrary bent
  pitch multipliers in Pattern notes without recording continuous bend
  automation. Implement synthetic and cross-platform virtual-port tests
  before buying or obtaining physical hardware; the MIDIPLUS EK490+ input
  response and program-counter reset semantics remain **unverified**.
  Prefer completing the [Synth live-parameter audition](todo.md) milestone
  first, sharing safe control/publication machinery without conflating
  authored graph parameters with MIDI voice pitch.

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
