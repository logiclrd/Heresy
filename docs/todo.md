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

- [ ] **Refine the arrangement-layer boundaries of Duplicate (deep)**
  (follow-up to merged PR #38; owner decision 2026-11-21).
  **Data Sequences and Script Sequences** must both offer
  `Duplicate (deep)`. Cloning a Sequence deeply duplicates the
  *referenced Sequences and Patterns* reachable through supported
  arrangement-layer links. A Data Sequence traverses every Pattern
  in its entry list (its current entries are Pattern IDs, not nested
  Sequence IDs). A Script Sequence traverses semantically recognized
  literal `_O(id)` references to Patterns and Sequences, where valid
  within its supported scripting API; do not add unsupported
  Sequence-to-Sequence `Play()` runtime semantics. Use one global
  original-to-new ObjectId map: shared Patterns, repeated entries,
  cross-links and cycles produce exactly one duplicate per reached
  object, with every cloned Sequence reference to a reached object
  correctly remapped. Original authoring data remains untouched.

  **Data Patterns and Script Patterns are terminal** in an arrangement
  deep-clone traversal: clone each reached Pattern's owned data/source
  independently, but retain its references to sound-generating objects,
  Filters, Envelopes and other playback sources instead of duplicating
  that stratum. Remove `Duplicate (deep)` from the **Script Pattern**
  context menu; ordinary `Duplicate` for both Pattern kinds remains.
  Pattern-contained `_O(id)` references do not recursively expand
  into sound-generation objects when a Sequence deep clone reaches that
  Pattern. Do **not** change the independently agreed deep-clone
  traversal of Instruments, Script Instruments, Synths or Script
  Envelopes, nor shallow-duplicate semantics.

  Preserve semantic Roslyn reference discovery and span-only rewrites
  for references actually remapped; do not rewrite strings, comments,
  shadowed calls, or external IDs outside the clone set. Keep atomic
  refusal for genuinely ambiguous/unrewritable references, source asset
  checks and existing deterministic selected-sibling tree placement.
  Regression coverage: Data Sequence with repeated/shared Patterns;
  Script Sequence referring to Data/Script Patterns and arrangement
  objects; both Pattern kinds stopping at the sound-generation boundary;
  removal of Script Pattern deep menu; shared/cyclic script links;
  shallow-vs-deep consistency, selected occurrence placement and
  `.hm`/`.hm.json` persistence. Update the historical
  [Duplicate Object specification](duplicate-object-plan.md) to
  supersede its now-obsolete terminal-Data-Sequence and
  Script-Pattern-deep decisions before implementation. **Separate
  future PR; not part of merged PR #42.**

## Undo/Redo — tiered redesign

- [ ] **Implement two-tier Undo/Redo** as specified in
  [tiered Undo/Redo design](tiered-undo-redo-design.md): song-level view
  boundary checkpoints plus editor-session-local domain snapshots with
  five-second idle batching, forced grid/source flush boundaries,
  complete state restoration, focus metadata and dirty-state
  saved-content identity. A Pattern-grid interaction outside the grid
  closes the note batch even if it changes only Octave/Skip; those settings
  themselves remain outside Pattern Undo state. A script text timer
  checkpoints its *buffer* only and never auto-applies to the song.
  Replace the old one-level Synth import history rather than extending it,
  and integrate tree-level Duplicate/import as ordinary song checkpoints.
  **Separate future project, not part of PR #38.**

## Script objects and editor UX

- [ ] **Add opt-in Script Pattern generation trace (#68).** See
  [discovery and design](script-generation-trace-design.md) and the
  [owner-approved UI/instrumentation contract](https://github.com/logiclrd/Heresy/issues/68).
  Three ordering projections, stable event selection and authored-source
  navigation; capture loop and group provenance and partial failed generation
  without publishing any audio. No diagnostic-specific instructions or
  bookkeeping in ordinary or F6 playback. Discovery committed; compiler/
  collector proof, provenance, projection model, UI and acceptance remain.

- [ ] **Polish script-object editors and generated return behavior.**
  Apply consistently to Script Sequences, Script Patterns, Script Envelopes,
  Script Instruments and every applicable script-source field. For scripts
  whose generated entry point requires a return value, provide an implicit
  `return default;` when execution would otherwise fall off the end of
  the user's text; preserve authored explicit returns and actionable
  diagnostics for unrelated compilation errors. Replace the existing
  introductory placeholder text about `_O()` and implementation technologies
  with a short, valid, editable initial source example *for each script
  kind*, matched to the supported API. Generate examples **once, when the
  scripted object is first created**, never on reopening or subsequent
  updates. Prefer existing, compatible song objects for example references:
  Script Sequences must pick actual Patterns for `Play(_O(id))` (not an
  arbitrary Sequence or sound source); Script Patterns should use a
  suitable sound source where available; other expression kinds should
  use actual allowed operands. Provide valid, meaningful, non-referencing
  fallbacks for an otherwise empty song. A Script Sequence can use a
  `switch` on `sequenceIndex` with one actual Pattern and a valid
  terminal/default branch. The initial source is normal mutable authored
  text from creation onward; never regenerate it because referenced
  objects are renamed/deleted. Keep **initial source generation** separate
  from compiler-injected implicit returns.

  **Explicit help remains independent of example source.** Replace the
  current literal technical description under the source editor with
  concise **object-kind-specific explanatory text** plus a full,
  **separately scrollable, read-only but selectable/copyable reference**
  for every supported script API (including parameter types/defaults,
  execution lifecycle, available variables, restrictions, and example
  statements). This is not a second editable code buffer. Script
  Sequence intro: `_O(35)` references object 35; each next-order
  lookup returns `Play(_O(patternId))` or
  `Play(_O(patternId), startRow)`, with accurate end-of-sequence
  behavior. Script Pattern intro: `_O(35)` refers to object 35;
  each Pattern invocation emits note and control commands; document
  physical and virtual channels, `Note`, `NoteVirtual`,
  the owned `VirtualNote` and `NoteGroup` controls (formerly numeric
  virtual helpers; superseded by #54),
  `Random` and **all other currently supported methods** with
  working call signatures. Match the actual runtime and incremental
  compiler, not illustrative invented APIs. In particular `Note`
  receives `pitchMultiplier` and can use the shared
  `Semitone(n)`/`Cent(n)`/`Octave(n)` pitch conversions, while
  `note.Volume` sets note/voice volume
  on its scoped virtual channel rather than generalized song volume.
  Do not present optional bracket notation as copy/pasteable C#;
  distinguish signatures from usage samples. Script Envelope and
  Instrument editors have narrower **expression** contracts and
  require their own shorter, appropriate help/reference content.
  Keep long reference content inside its own scroll viewport instead
  of making the overall document/editor difficult to navigate.

  Use a **monospace editor font** via the first installed candidate in
  exactly this preference order: Commit Mono, Hack, Fira Mono, Noto Mono,
  Liberation Mono, IBM Plex Mono, Ubuntu Mono, Cascadia Mono, Consolas,
  Lucida Console, SF Mono, Menlo, Monaco; then any sans-serif monospace
  family; then any other monospace family; then Courier New, Courier;
  finally the normal UI font if none is available. Do not assume any one
  named font exists on Windows, Linux or macOS.

  Rename the script editor's **Apply source** button to **Save**. Keep
  Save disabled until the text differs from the last applied/saved source
  (and disable it again if the edits are reverted); enabling is driven by
  text changes rather than other object settings. Do not silently lose
  pending edits: attempting to dismiss the editor through Back/navigation,
  close or Cancel with unsaved source text must offer **Save**, **Discard**
  and **Cancel (stay in editor)**. Reconcile the new caption/commit boundary
  with the approved [tiered Undo/Redo design](tiered-undo-redo-design.md),
  including pending 5-second text-undo batches and preserving editor-local
  history rather than introducing an unrelated history mechanism.
  Regression/GUI checks should cover fallthrough vs explicit return,
  valid editable initial examples for each script type, font fallbacks on
  all desktop platforms, Save dirty-state enable/disable and each
  unsaved-dismissal choice.
  **Owner refinement (2026-11-21):** Keep this as a separate TODO; PR #38
  currently implements only the signed `_O(int)` compatibility fix for
  D6-G, not this broader script-editor redesign.

- [ ] **Revisit Instrument model, common tone editor, and procedural
  Script Instrument authoring (exploratory design; no implementation yet).**
  Currently `InstrumentDefinition` (Data Instrument) and
  `ScriptInstrumentDefinition` are distinct types. Script Instruments
  use restricted expression-based tone selectors returning indices into
  manually authored `ToneSpecifications`, a separate pitch-scaling
  expression, and an NNA expression. One-at-a-time tone creation and
  fixed predeclared ratios make meaningful physical playback testing
  cumbersome; this was observed while accepting PR #47 (math helpers
  compiled in Instrument expressions but were not auditioned through
  a fully configured Script Instrument).

  **Updated future design (2026-11-22):** Consider richer procedural
  control over the generated Instrument/tone object, including an
  **initialization/setup block** preparing data for reuse by a
  **per-note handler**. Setup allocations are permitted **without warning**;
  per-note allocations are permitted **with an advisory warning**, not
  rejected merely for allocating. Define initialization lifetime,
  output-object ownership, generation isolation and advisory detection
  during discovery. Do not apply these semantics to the current three
  Script Instrument expressions. The general engine allocation policy
  remains strict for systematically hot paths (>~100 calls/row) and
  advisory/avoid-when-practical elsewhere.
  **Planning contract (not current API):**
  [Script Instrument rework](script-instrument-rework-plan.md).

  Investigate whether a Script Instrument is effectively a **superset**
  of a Data Instrument, should reuse the **same fully featured editable
  tone grid**, and whether these can converge into **one Instrument
  object type** with an **optional front-end mapping script**. Compare
  shared editor-only reuse, shared underlying tone-model ownership,
  and complete type consolidation. Do not assume type unification is
  safe or necessary before reviewing actual persisted types, data
  Instrument divisions/offset lookup, Script Instrument selector and
  per-tone NNA/filter/envelope semantics, and compatibility.

  Independently investigate a **procedural Script Instrument program**
  (akin to Script Sequences) in place of the current isolated scalar
  expressions: can it choose or construct/derive tone definitions based
  on incoming `pitchMultiplier`, express dynamic pitch/NNA policy,
  and remain bounded, safe, deterministic and understandable? Compare
  it with declarative helpers and the current indexed tone table.
  Account explicitly for static sound dependency discovery, source
  selection/remapping under import and deep duplicate, snapshot
  isolation, compilation outside realtime playback, audio publication,
  historical song deserialization, upgrades and compatibility for
  existing Script Instruments and Data Instruments. Produce a design
  and acceptance contract **before** modifying either Instrument
  editor or model. Schedule a practical real-instrument test of the
  PR #47 pitch helpers once tone authoring is improved.

- [ ] **Make Script `_O(id)` visual projection edit-local and separately
  undoable.** The semantic Roslyn analysis still checks the complete
  underlying source; visual object-name tokens are **stateful presentation
  projections**, not replacements for the canonical `_O(id)` source.
  Retain the set of currently projected tokens and source-anchored spans,
  rather than rebuilding the token list from every full-document analysis.
  A **caret move alone** must not create, remove, or re-create tokens, even
  when a previously caret-blocked match becomes unblocked. Do not maintain
  a blacklist of ineligible spans: an otherwise valid but undisplayed
  reference remains plain text until a **directly adjacent** qualifying
  text edit or a complete editor reload. A candidate containing the
  current caret is not translated.
  
  Only an **insertion**, including Paste or replacing text with nonempty
  inserted characters, may create new visual tokens. The candidate must
  contain inserted characters or be completed by an insertion directly
  at its boundary; unrelated insertions, even elsewhere on the same line,
  must not re-tokenize it. **Deletion and Cut never create tokens.**
  Deleting inside an existing visual token removes the affected projection
  and reveals canonical source. Adjust unaffected token positions across
  document edits. Re-evaluate projected tokens for semantic invalidation
  (e.g., intrinsic shadowing) without interpreting semantic analysis as
  permission to create new projections. Opening/reloading the editor may
  perform a complete initial projection pass.

  **Known accepted edge case from PR #38:** displayed
  `return Play(New Pattern|);` represents
  `return Play(_O(1)|);`. Backspace on the token's right edge removes
  its own closing `)`, yielding raw `return Play(_O(1|);`.
  Currently the outer `)` immediately re-forms a token and moves the
  caret incorrectly. With this improvement, the expression remains
  unprojected after Backspace, after caret movement, and after unrelated
  edits; re-inserting `)` adjacent to the expression can re-project it.

  **Undo/Redo contract:** Automatic projection is a **separate visual
  Undo checkpoint** after the triggering text edit. Typing the final
  `)` makes a token; first Ctrl+Z undoes **only the translation**,
  revealing the same `_O(1)` source with the caret, and must not
  re-project it; second Ctrl+Z undoes the typed `)`. Redo reverses
  both operations. **Group all token translations caused by one Paste
  into a single visual Undo checkpoint**, not one per reference. A
  visual-only Undo/Redo must never masquerade as a document text edit
  or trigger retranslation. Coordinate these operations with the
  separate [tiered Undo/Redo design](tiered-undo-redo-design.md);
  preserve text-Undo batching boundaries and caret/selection placement.
  Test nested `))`, edits elsewhere on the same line, insert/paste/cut,
  multi-token Paste, caret-only moves, initial reload, semantic invalidation,
  and Undo/Redo ordering. **Separate future work; accepted PR #38 D6-G
  must not be reopened solely for this known issue.**

## Playback preparation feedback

- [ ] **Show script compilation activity at the far right of the main
  status bar.** During Roslyn script compilation/preparation (including
  compilation delaying the *first* preview note), display a distinct,
  readily noticeable status such as **Compiling scripts…** in bold.
  Show the activity while compilation is actually in progress and clear
  it on completion or failure without overwriting the existing status/
  diagnostics or claiming playback has started. Keep compilation off
  the realtime audio/PCM thread and preserve responsive UI behavior.
  Check both short and slow compilations and failure/retry transitions.
  **Context:** Owner-tested [PR #40](https://github.com/logiclrd/Heresy/pull/40)
  fixes an unrelated invalid Script Sequence blocking Synth preview; this
  separate UX request addresses the initial note-playback latency noticed
  during that test.

## MIDI keyboard input (deferred until Synth live-parameter infrastructure)

- [ ] **Implement cross-platform MIDI keyboard input, Program Change Source
  control, and continuous microtonal preview pitch bend.** Preserve all
  decisions, routing, device management, acceptance cases and unresolved
  hardware expectations in the [MIDI keyboard design](midi-keyboard-input-design.md).
  Provide the Pattern Editor's mutually exclusive **Stop, Pause, Record,
  Play+Record** modes and adjacent exclusive **P/S** traversal selector
  (P default). Stop auditions notes without document edits, but still
  allows Program Change Source navigation; Pause inserts notes with Skip.
  Record/Play+Record advance a Note-column-locked record head at the
  **actual effective tempo and speed** (including muted Record-only rows),
  ignoring Skip so repeated incoming notes overwrite the same row under
  the existing edit mask. Play+Record plays **all channels** of each row.
  P wraps within the same Pattern; S advances the Sequence and stops at
  its end. Ctrl+Space selects Play+Record; Ctrl+Shift+Space selects Record.
  Entering either record-head mode **remembers the current Stop/Pause state**;
  switching Record ↔ Play+Record does not change this memory. Escape in a
  recording mode **restores that remembered state** (Stop or Pause), including
  after repeated start/stop cycles. In Stop or Pause, Escape switches to
  the other resting state and updates the memory used by the next recording
  session. Clicking a depressed Play/Record half also returns to the
  remembered resting state.
  Include a **Velocity** toggle for MIDI and the physical keyboard piano:
  off uses current-channel volume for preview without writing it; on
  uses translated MIDI velocity (or keyboard current-channel volume) for
  both preview and Volume insertion, with edit-mask precedence. Preserve
  Note/Volume/Source/effect mask semantics, source navigation and playback
  scheduling. Numeric MIDI velocity mapping and a few boundary policies
  remain for explicit implementation decisions in the design.
  Program Change navigation uses raw base program **100**, sends reset
  Program Change back to the keyboard, moves Source by signed delta with
  **clamping**, and is gated by an independent, default-checked
  **MIDI Control** checkbox even in Stop mode. MIDI Synth/Sample audition
  is independent of computer-keyboard Hold/Latch, always Hold, and
  updates ongoing MIDI voices per channel for pitch bend. Capture
  arbitrary bent pitch multipliers in Pattern notes without recording
  continuous bend automation. Use the completed Synth realtime-parameter
  audition infrastructure without conflating authored graph parameters
  with per-voice MIDI pitch. Implement synthetic and cross-platform
  virtual-port tests before physical hardware acceptance; the MIDIPLUS
  EK490+ input response and program-counter reset semantics remain
  **unverified**.

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
