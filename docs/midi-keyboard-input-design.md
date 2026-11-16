# MIDI keyboard input — proposed design (not yet implemented)

**Status:** Design captured 8 November 2026 and refined 16 November 2026 with Pattern Editor recording transport and velocity semantics; pending implementation and physical MIDI hardware acceptance. This document preserves decisions and test obligations across chat migrations; it is **not** a description of implemented behavior. The intended hardware candidate is the **MIDIPLUS EK490+** USB MIDI keyboard. Its published specifications list USB MIDI IN/OUT, 49 velocity-sensitive keys, 128 GM sounds, a pitch-bend wheel, and Windows XP-through-modern/macOS compatibility. Linux USB MIDI plug-and-play is likely if the device presents standard USB MIDI interfaces, but must be physically verified. Its ability to accept incoming MIDI is documented; the precise instrument-selector reset effect of incoming Program Change must be proven on hardware.

## Scope and sequencing

The three main surfaces are (1) Pattern Editor note entry, shared keyboard/MIDI transport and musical-time recording, (2) relative current Source selection driven by Program Change, and (3) always-Hold Synth/Sample live preview, with arbitrary microtonal pitches and continuous pitch bend. MIDI input is an additional external controller, not a General MIDI mapping for song Sources and not a replacement for physical-computer-keyboard editing. No MIDI hardware is yet available, so favor a pure event/state layer, fake ports, deterministic tests, and virtual-port acceptance before physical verification.

**Dependency/order:** Realtime Synth parameter audition was completed under PR #12 and provides the safe-audio-boundary publication/control infrastructure that MIDI can build on. MIDI live pitch bend should reuse suitable transport and existing live pitch trajectory facilities, but remain independent of authored graph parameters and target the correct MIDI voices. Implement generalized continuous preview-voice pitch control, then MIDI device I/O and editor routing, including the Pattern Editor's shared MIDI/keyboard input state machine.

## Application-level MIDI transport

- Provide an application-owned cross-platform MIDI service with device enumeration, input/output port open/close, connection and disconnection notifications, and ordered normalized event delivery. Use a native MIDI library or host APIs below Avalonia; input must not depend on whether the app is running under Windows, macOS, X11, XWayland, Xephyr or native Wayland. **RtMidi** (ALSA/CoreMIDI/Windows) and **libremidi** (broader backend options) are candidates, not yet a committed dependency. Assess native packaging, hotplug, platform compatibility, and threading before selecting. Make MIDI entirely optional; a missing library or device must not prevent Heresy from starting.
- Native MIDI callbacks must not touch Avalonia controls or mutate documents directly. Marshal normalized events onto the UI/editor dispatch queue, maintaining order per input device/channel (especially Note On, Pitch Bend, Note Off). Keep slow graph/DSP preparation outside realtime and MIDI callback threads. Device reconnection, port selection, stale callbacks and unexpected disconnect must have predictable cleanup.
- Initially support one explicitly selected MIDI input and a separately determined/paired output port for Program Change reset, with visible device/status selection. Port matching and per-channel filtering are policy decisions to refine during implementation; preserve original device/channel identities. A selected device controls only the active editing context; do not write notes to a Pattern while another page/editor has focus. No MIDI Control checkbox if no MIDI input is active.
- Parse MIDI 1.0 channel messages (Note On, Note Off, Program Change, Pitch Bend). Treat Note On with velocity zero as Note Off. Normalize 7-bit program/note/velocity and 14-bit bend with validation; ignore unsupported events safely. Preserve ordering and distinguish two devices/channels with otherwise identical notes. **Optional MIDI velocity-to-volume conversion is now in scope for the Pattern Editor**, although its numeric curve is still to be specified. Sustain pedal, aftertouch, modulation and MIDI clock remain outside the agreed initial behavior; reserve room to add them without inventing rules.

## Pattern Editor: direct pitched note entry

- A valid MIDI Note On with nonzero velocity, while a Pattern Editor is active, is routed according to the Pattern Editor's **Stop / Pause / Record / Play+Record mode** defined below. Stop auditions only; Pause inserts at the **current tracker cursor row and channel regardless of which field or child control currently owns keyboard focus**, then advances according to **Skip** (including Skip=0 and flattened Sequence boundaries); Record and Play+Record overwrite at the record head's current row **without applying Skip**. In insertion modes, resolve focus/selection into the **Note** field, collapsing expanded field state as needed, and perform cursor/selection/scroll/label updates synchronously before awaiting realtime audition. Honor the ordinary Pattern edit mask and Source/Volume preservation rules rather than fabricating an Avalonia physical key or bypassing the pattern command pathway.
- MIDI Note Off does **not** insert a pattern Note Off. Each Note On is a separate entry gesture (including possible repeated equal pitches); normal piano-key repeat suppression is irrelevant. Chord-tool interaction, selection clearing, and mask-dependent behavior should follow the existing pattern note-entry contract explicitly, rather than being guessed from keyboard event simulation.
- MIDI note numbers are absolute pitches independent of the Pattern Editor's **Base Octave** selector: MIDI 60 = Heresy's reference **C4**, pitch multiplier 1. For note number `n`, bend `b`, bend sensitivity `R` semitones, entry pitch is `2^((n - 60 + R*(b - 8192)/8192)/12)`. Values 0..16383 are valid, center is 8192; maximum upward deflection is just short of +R using the conventional signed scaling. Preserve the exact positive finite floating point multiplier in `StartPatternNote`. The renderer already supports non-semitone pitches; current visual fallback is a numeric pitch multiplier (a cents display could be a separate UI improvement).
- Pitch bend is tracked per MIDI input/channel. The current bend is **sampled at Note On** and persisted in that note's pitch multiplier. **Later bend messages do not retroactively edit already-entered pattern notes.** Continuous bend automation recording is explicitly out of scope; it would need its own song representation and design.

## Pattern Editor: input modes, transport, velocity and record head (refined 16 November 2026)

Controls sit **at the top of the Pattern Editor** and apply to MIDI and
the physical computer-keyboard tracker piano alike. The editor has four
**mutually exclusive operating modes**, not two independent record/play flags:

| Mode | Incoming MIDI / tracker-piano note | Cursor / record head | Playback |
| --- | --- | --- | --- |
| **Stop** | Audition only; no Pattern-document edits or note-driven cursor changes | Stationary | No pattern-row playback |
| **Pause** | Enter notes subject to the Pattern edit mask | Advance according to the configured **Skip** after an entry | No pattern-row playback |
| **Record** | Enter notes at the current record-head row, with **Skip ignored** | Record head advances on the musical clock | Rows remain silent, but their **tempo/speed changes still affect clock advancement** |
| **Play + Record** | Same record-head entry semantics, with Skip ignored | Record head advances on the musical clock | Full row playback, across all channels and their effects |

- The control group consists of a **Stop** button, a **Pause** button, and
  one two-half **Play / Record** compound button. Maintain a **remembered
  resting mode** of Stop or Pause, updated whenever either resting mode is
  explicitly selected. Entering Record or Play+Record from Stop or Pause
  captures that mode; toggling between Record and Play+Record **does not**
  overwrite it. Pressing Play from Stop or Pause depresses **both** Play and
  Record. Pressing Record from Stop or Pause depresses Record alone.
  Pressing Play while only Record is depressed adds Play. Clicking either
  already-depressed half releases **both** halves and restores the remembered
  Stop/Pause resting mode. Clicking Stop or Pause explicitly selects that
  mode and updates the remembered resting mode.
- **Ctrl+Space** selects Play+Record, and **Ctrl+Shift+Space** selects
  Record without Play; while already recording, either shortcut changes
  the active recording variant **without replacing the captured resting mode**.
  **Escape in Record or Play+Record returns directly to the remembered
  Stop/Pause mode**. **Escape while in Stop or Pause switches to the other
  resting mode**, making it the newly remembered mode for any subsequent
  recording session. Thus Stop → Record → Escape → Stop and
  Pause → Record → Escape → Pause, even over repeated start/stop cycles;
  Stop → Escape → Pause → Record → Escape returns to Pause. These shortcuts
  choose modes rather than generating note events; standard text editing and
  unrelated modal contexts must not be hijacked.
- Stop is **audition-only for notes**: MIDI and the computer piano preview
  sounding notes with no Pattern-document edit, Note-field focus move or
  Skip/cursor advance. **Program Change Source navigation is still active in
  Stop if the separate MIDI Control checkbox permits it**. Stop is not a ban
  on all editor-state changes, only on note-driven document editing.
- In Record and Play+Record, the record head moves through rows using the
  song's **real effective musical row durations**, including tempo and speed
  changes from the Pattern or Sequence. **Record without Play must still
  evaluate tempo/speed changes**, even though the musical rows are not
  sounded. Do not implement silent recording as a fixed-interval timer.
  MIDI notes (and ordinary computer-keyboard piano notes) arriving before
  the next head tick replace the currently edited note in turn, so the last
  event at that row survives; they do **not** themselves advance the head.
- During record-head modes, keep the selected channel while locking the
  cursor/focus **field** to the **Note** column; arrow navigation or a click
  selecting another channel lands in that channel's Note column. Cursor
  progression follows the record head rather than the Skip setting.
  Maintain correct alignment between playback and the displayed selected row,
  including the pattern editor's projected Sequence view.
- The adjacent **P / S** two-button selector is exclusive: **P** is selected
  by default; clicking P selects only P, clicking S selects only S, and
  neither click deselects both. **P** wraps the record head to the beginning
  of the *same Pattern* even when opened in a Sequence. **S** advances through
  the Sequence's next Pattern at the current Pattern's end; reaching the end
  of the Sequence **stops the record head**. The existing Sequence projection
  may contain non-editable script/missing separators or repeated references;
  traversing these cases must be explicitly handled without silently
  writing an uneditable row. P/S remains selected in Stop and Pause.
- The independent **Velocity** toggle governs how an incoming pitched note
  obtains audition volume and what value is *offered* for the Pattern's
  explicit Volume field:
  - **Velocity off:** MIDI and computer-keyboard piano audition at the
    **current channel volume**, and a newly inserted note provides a
    **blank Volume value**, rather than baking in that loudness.
  - **Velocity on, MIDI:** convert incoming MIDI velocity into a normalized
    volume. Use it for audition and, when recording/entering, as the note's
    proposed explicit Volume value. The exact 1..127-to-00..64 conversion
    curve and rounding must be defined and tested before implementation.
  - **Velocity on, computer-keyboard piano:** there is no hardware velocity,
    so use the **current channel volume** for audition **and** provide that
    value as the note's explicit Volume.
  - **The Pattern edit mask always wins.** Both normal Skip entry and
    record-head entry modify **only fields enabled by the mask**, with the
    same semantics as existing tracker-piano entry. When Volume is masked
    **on**, a proposed blank value clears that field and a proposed explicit
    value writes it; when Volume is masked **off**, retain the existing row's
    Volume untouched, regardless of velocity setting. Unmasked Source and
    effects remain intact. If Note itself is masked off, the normal
    keyboard-piano rules for the selected edit mask must govern the event.
- A MIDI Note Off is still only a preview-voice release and never writes a
  tracker Note Off event. MIDI Program Change navigation continues to be
  governed separately by **MIDI Control** below, including in Stop. MIDI
  input is optional; computer-keyboard recording with the same transport
  controls must remain usable without an attached MIDI device.
- **Record-only versus Play+Record must share one timing authority**; Play
  adds audible *whole-row* playback, not just auditioning the selected
  channel. The scheduling policy for a note recorded exactly on the playing
  row boundary, and re-entry when changing modes, requires a deterministic
  implementation/test contract rather than an implied UI-timer race.

## Pattern Editor: Program Change controls current Source

- A **MIDI Control** checkbox sits immediately **below the Source ComboBox**, visible only when an active MIDI input is in use, and defaults **checked** when input is first enabled/connected. It enables/disables *Program Change Source navigation*, **not MIDI note input**; its enabled behavior applies in **Stop** as well as other Pattern Editor transport modes. When disabled, program changes do not change the Source and Heresy sends no resets; what happens when reenabled is to be handled with an explicit baseline synchronization.
- The numbers below are **raw MIDI Program Change values 0..127**, not the keyboard's possibly 1-based 1..128 printed patch numbers. The configured recentering base is **100**.
- On enabling MIDI Control with an output path, send **Program Change(100)** to the keyboard (on the appropriate MIDI channel). The hardware is expected to update its internal instrument selection accordingly. On reception of Program Change(`p`) from the keyboard, compute `delta = p - 100`, move the selected Source **by that signed delta with clamping at the ends**, and send Program Change(100) again. This moves through Heresy's ordered sound-Source catalog; it **does not** map General MIDI instrument IDs to Heresy Source IDs and does **not** wrap the Source list.
- Generalize the existing `PatternSourceNavigation.Move` to accept arbitrary signed deltas while preserving its **existing clamping semantics** for keyboard shortcuts (which use ±1). A zero delta leaves Source unchanged. Treat a received Program Change(100) as a reset/echo no-op and **do not issue another reset in response**, avoiding feedback loops. Ignore or appropriately reconcile self-echoed reset messages and asynchronous program events; do not allow rapid events or reconnects to produce unwanted deltas.
- A successive-received-program approach was considered and **rejected as the default**: some MIDI keyboards do not guarantee program-counter wrap from 0↔127 and a large jump is ambiguous. Inbound reset-to-100 is **a device-behavior hypothesis requiring physical verification**. The MIDIPLUS EK490+ has documented MIDI receive capability and user research indicates its instrument selection responds to incoming Program Change, but verify that *its subsequent transmitted increment/decrement* really operates from the new baseline. If the device lacks usable MIDI output or does not rebias outgoing program selection, report the missing capability and discuss an alternate optional mode instead of silently claiming correct relative navigation.
- In Synth/Sample editors, Program Change causes no Source navigation. It must not alter song data. Recenter MIDI Control only when that behavior is enabled and routed to the Pattern Editor.

## Synth/Sample Preview: independent Hold voices

- MIDI Note On in the **currently active Synth or Sample Editor** starts a live note of the editor's source, **regardless of focus within the editor**. Do not move focus. MIDI Note Off releases its corresponding note using Hold-style **Note Off**, regardless of Caps Lock and computer-keyboard Hold/Latch mode. Note Off outside a matching active press must not release an unrelated voice.
- MIDI preview voices are logically independent of the existing Test piano keyboard and use distinct press identities and virtual channel/voice namespaces. MIDI notes can overlap with PC-keyboard held and latched notes, including identical pitches. Computer-keyboard Latch neighbour suppression must **not cut MIDI notes**. F8 emergency stop should stop all preview voices; closing an editor or disconnecting its MIDI input should release/cut its device-owned voices and clean up mappings.
- Every newly started MIDI preview voice captures the channel bend currently in effect. Pitch Bend then retunes **all currently active MIDI voices belonging to that input/channel**, without retriggering, resetting oscillator phase, envelope, Sample position or compatible DSP state. Preserve each voice's **unbent MIDI note number** for absolute target recomputation, rather than repeatedly multiplying the last bent pitch. Consider audible release tails where still live. Bend does not affect PC-keyboard voices, other MIDI channels/devices, or authored graph parameters.
- Use the same 14-bit pitch formula as Pattern entry. Default bend sensitivity is **±2 semitones** (consistent with the EK490+ wheel's described whole-step bend), but expose/configure sensitivity and optionally recognize RPN 0 bend-sensitivity messages. Do not assume device/controller range is synchronized unless observed or configured.
- Heresy's realtime render pipeline already has `SetPlaybackFrequencyCommand` and `PlaybackVoice.SetPlaybackFrequency`, plus `PitchTrajectory`, suitable as part of a safe live-pitch implementation. The **explicit virtual-channel command dispatcher currently rejects that command**: extend it (and tests) to retune the exact owning virtual voice. Do not reuse graph-definition parameter edits as MIDI pitch bend. Sample preview restart after loop edits must retain the currently bent pitch; account for fast wheel events, stop/release ordering and unusual MIDI Note On/Off repetitions.
- Pattern Editor MIDI velocity support is specified below, including preview loudness and optional baked Volume-column values. Whether the same velocity policy should be exposed in Synth/Sample audition is not yet specified; sustain pedal and other MIDI expression controls remain deferred.

## Hardware-independent tests and pending physical acceptance

**Deterministic/unit/desktop CI using fake MIDI ports:**

1. Input parsing, 14-bit bend extremes/center and velocity-zero Note On; per-device/channel ordering; thread-safe dispatch and disconnect.
2. Pattern Note On regardless of selected field/textbox focus; Stop audition/no note editing, Pause Skip=0/1/>1, Record/Play+Record ignoring Skip, edit-mask behavior (including Volume clear-versus-preserve), record-head Note-column lock and channel switching, and last-note-wins at a row; last row/Sequence P-wrap/S-advance-and-stop, tempo and speed changes advancing a **silent** Record head at the same musical rate as Play+Record; undo/Source and no accidental Note Off entries; arbitrary bent pitch multipliers.
3. Stop/Pause/Record/Play+Record state-machine transitions including two-half button behavior, Ctrl+Space/Ctrl+Shift+Space, **remembering the Stop/Pause state on entry to recording and restoring it with Escape**, preserving that memory when switching Record ↔ Play+Record, repeated start/Escape cycles from both resting modes, clicking an already-active recording half, Escape alternating Stop/Pause and updating the remembered state, and P/S exclusivity/default; MIDI velocity on/off, computer-piano fallback and preview-versus-baked volume. Current Source selection clamps for arbitrary signed delta and retains ±1 keyboard shortcut behavior; zero delta and empty catalog; exact raw program 100 convention; Program Change operating in Stop and controlled independently by the checkbox, input/output handshakes, no Program Change echo loop, hotplug behavior.
4. Synth/Sample preview concurrent notes, Hold release, independent PC/MIDI voice namespace, Latch isolation, per-channel bend of all active notes without retrigger or accumulated error, release tails, F8, editor closure/disconnect, Sample loop restarts and PCM continuity/underruns.
5. Native virtual-port integration where available without depending on a physical keyboard: ALSA sequencer on Linux, IAC on macOS, appropriate loopback on Windows; package/native ABI startup cases.

**Requires the actual keyboard:** USB enumeration and OS permissions on Linux/Windows/macOS; bidirectional USB MIDI and channel mapping; program 100 inbound actually changes the internal patch counter used by subsequent outbound program changes (including direction and edge behavior); wheel sensitivity/centering; disconnect/reconnect, multiple notes and physical listening. Record real observations as acceptance evidence. Linux compatibility, the exact EK490+ reset mechanism, and hardware performance are **unverified** at design-capture time.

## Notes on existing implementation

- `PatternNoteKeyboardEditor` is tied to computer key mapping, but `StartPatternNote.PitchMultiplier` supports arbitrary positive doubles. Add a MIDI-pitched entry path rather than synthesizing Avalonia KeyDown.
- `PatternSourceNavigation.Move` currently accepts only ±1 and clamps. Expand to signed deltas, preserve clamp semantics.
- Shared `PianoTestControl` and `PianoPreviewPlaybackController` already have separate keyboard, voice and live-audition responsibilities; add MIDI ownership without sharing computer-keyboard Held/Latch press state.
- `PlaybackSessionAudioSource` queues live events for audio-frame application. Generalized safe publication for ongoing Synth parameter changes should be developed before MIDI and reused where appropriate for pitch-control message ordering.

## References

- MIDIPLUS official EK490+ product: https://www.midiplus.com.tw/en/product-detail/EK490plus/
- MIDIPLUS EK490+ owner manual: https://www.midiplus.com.tw/cn/MIDIPLUS-Download-detail/0/Manual_Of_EK490plus_EN/
- [Completed Synth realtime audition and remaining TODOs](todo.md)
- [Pattern/Synth/Sample UI workflow](user-interface-workflows.md)
- [Audio output](audio-output.md)
