# PR #12 — C5(e) physical audio-device qualification playbook

**Status (2026-11-15): FIRST LINUX DEVICE RUN RECEIVED — RECOVERY PASS / HEAVIER LOAD FAILS; C5(e) PARTIALLY QUALIFIED.** C5(a–d) are accepted only under their bounded automated contracts. C5(e) must be performed with an actual Heresy SDL3 output stream on the tester's Linux system (and Windows when practical). C5(f) supported/unsupported load envelope, C6 hands-on listening, C7 final authorization, the all-nonstructural Synth TODO and PR merge remain open.

## First Linux tester observation (do not require this run again)

The tester has **already completed** the central Linux **same-original-note 3→17→3** and three-note nine-plus-voice stress/recovery exercises, and supplied a complete 99.8-second real SDL CSV plus an Output Scope image. Both scenarios caused actual ring underruns and audible warbling, **but the original playback recovered promptly after reducing voices to 3**. See the [dated, quantitative C5(e) physical observation](synth-live-pr12-c5-realtime-qualification.md#c5e--first-tester-observed-linux-sdl-device-run-physical-recovery-verified-with-overload-2026-11-15): 236 underrun events (4 startup, 91 first heavy window, 141 second), **zero new underruns in the two long subsequent recovery windows**, 2,191 producer blocks over budget, seven completed worker expansions without an observed worker fault. This is real physical-device evidence and should **not** be repeated merely to satisfy this playbook.

**Remaining information to request from the same machine:** CPU model, actual Linux audio server/default sink and physical output device (`lscpu`, `pactl info` if available), .NET runtime and Linux version, plus whether the UI Runtime Diagnostics reported anything. **Optional** Windows cross-check can follow on a device capable of running .NET 10. C5(f) should characterize the *configuration-specific* safe/unsafe polyphony range with lighter 3/5/7/9-count short runs, if desired; no global claim about 17 voices or three simultaneous notes is warranted.

## What was added, and what it does not establish

Set the opt-in environment variable `HERESY_C5_AUDIO_CSV` to an **absolute file path before starting Heresy**. Every real SDL audio session appends a unique session ID and **250-ms** off-audio-thread counter snapshots to that CSV (single header). Session-start/stop snapshots are also written. When the variable is absent/empty, there is **no recorder, no new timer, no SDL callback instrumentation, and no producer-profiling timestamps**. The normal status-bar **Audio underruns** and Runtime Diagnostics remain available without the variable.

The recorder runs on a timer, **never inside the SDL callback or PCM producer**, and captures:

- UTC timestamp, session ID, session elapsed milliseconds, configured sample rate/channel count, ring capacity/producer quantum, current buffered frames, and cumulative ring **underrun events**.
- PCM-producer block/frame totals, cumulative blocks exceeding their nominal PCM-frame render budget, peak and total render time.
- Multi-preparation worker completed/cancelled expansions, prepared child count, maximum waiting queue depth, worker allocation/abandoned allocation totals, worker preparation elapsed time, and fault *type* when present.
- SDL stream-callback request count, cumulative requested frames, maximum single request, observed peak callback execution milliseconds, and count of callbacks that took longer than the **requested audio frames' nominal duration**.

**Important measurement limits:** SDL3's `additionalAmount` is *stream demand*, not the sound device's known hardware interrupt interval. Comparing callback wall time to `additionalAmount / sampleRate` is only a workload diagnostic, **not proof of a missed device deadline**. The recorder does not know or claim end-to-end device latency, hardware buffer depth, resampler/OS queue depth, callback scheduling jitter or total CPU utilisation. Producer timings include operating-system preemption; worker jobs are separate from the PCM producer. A controlled/prebuffered zero-underrun regression is **not** a physical audio result. A cumulative underrun count may stay nonzero after recovery; use its **change over time**, not return to zero, to judge recovery. C5(e) remains OPEN until actual user machine observations are supplied.

The CSV contains **counter metadata only**, not PCM recordings, project file contents, personal song names or song-document identifiers. It is append-only for sessions in the same process. If the path is unwritable, a one-time error is printed to standard error and playback continues; check that CSV rows were actually recorded.

## Linux instructions (primary evidence)

From the Git checkout (the PR branch, not `main`):

```bash
git fetch origin
git switch feat/synth-live-parameter-foundation
git pull --ff-only

# A new file per application run; use the same terminal to launch Heresy.
export HERESY_C5_AUDIO_CSV="$HOME/heresy-c5e-linux-$(date +%Y%m%d-%H%M%S).csv"
dotnet run --project Heresy/Heresy.csproj -c Release

# After exiting Heresy, print the file path and check its header/last rows:
printf 'C5(e) CSV: %s\n' "$HERESY_C5_AUDIO_CSV"
head -n 2 "$HERESY_C5_AUDIO_CSV"
tail -n 4 "$HERESY_C5_AUDIO_CSV"
```

Use a normal desktop audio device—not a headless or `SDL_AUDIODRIVER=dummy` run. In the application's **Options → Audio Output** choose **48,000 Hz stereo**, no master/speaker Filter, unless your device requires a different supported layout. Note the exact output settings if changed. Start with system volume low: 17 aligned oscillators can otherwise be unexpectedly loud.

Capture the host/software context once (copy to your test report; `pactl` is optional):

```bash
date --iso-8601=seconds
uname -a
dotnet --info
lscpu | grep -E 'Model name|CPU\(s\)|Thread\(s\) per core' || true
pactl info 2>/dev/null | grep -E 'Server Name|Default Sink' || true
```

Use the visible **Test** control in the Synth editor. Click it, use `Z S X D C ...` as the piano, switch **Caps Lock** to **Latch** (confirm the displayed mode), and use **F8** to cut all preview voices. Latch persists across focus changes so you can edit the graph while a note is sounding. Newly latched notes fewer than three semitones apart replace neighbours; choose separated notes for polyphony. Node numeric text is committed by **Enter** or focus loss. Watch **Audio underruns** in the main status bar and the Runtime Diagnostics/Warnings history for an actual worker or SDL fault.

### A. Single latched voice: 3 → 17 → 3 without restarting

Create or use a **Synth** with a continuous audible path such as **Sine Oscillator → OnePole Filter → Multi**, starting with **3 Multi voices**. Aim for a modest signal level (e.g. Oscillator Vmin/Vmax around **−0.02/+0.02**, Gain Compensation off, Detune 0, Randomize Phase off). Keep the same nodes and wiring while changing Voice Count. A reusable 2–3-stage OnePole Filter ahead of Multi is appropriate if you already have one; record the graph/filter details in your results. Do not use a zero-output Constant as the only source for an *audible* test.

1. Clear old previews with **F8**. Confirm Latch and start **one note**. Hold the 3-voice configuration for ~10 seconds. Note audible character, underrun counter and any runtime fault.
2. With that **same note still sounding**, commit Multi Voice Count **17**. Listen/observe for ~10 seconds. Record clicking, dropouts, sustained silence, CPU distress, fresh underruns or a visible older-audio warning.
3. Without restarting the note, commit Voice Count **3**. Listen/observe for ~10 seconds. **The original held note must resume/continue normally**, including after a prior overload; accumulating underrun count should stop increasing at the lighter workload.
4. Start a **new, separate** note at the current 3-voice configuration (at least 3 semitones away), verify that both original and new notes sound, then cut the new note if you can do so independently. If only F8 is practical, report that rather than claiming individual cut was tested. End with **F8**.

Because the original regression C5(c) concerns a particularly expensive 17-voice filtered Multi, repeat A with a heavier Filter (or a few simultaneous notes) **only if the first pass is stable**. Do not force audio overload through an unnecessarily extreme level. The goal is **recovery after reduced load**, not a universal assertion that seventeen voices are safe.

### B. Concurrent notes, rapid edits and interruption

Using the same Synth, or a nested Multi where available, start **three latched, well-spaced notes** (for example `Z`, `C` and a third key at least three semitones above `C`). Wait ~10 seconds. While notes sound, make two rapid, valid, nonstructural Multi/Filter edits (for example Voice Count **3→9→12**, then **12→3**, or a filter cutoff change). Verify that the current values eventually take effect, no older-audio warning appears for compatible changes, and lighter playback recovers. If the UI supports individually releasing/cutting one preview voice, test that; otherwise use **F8** and report the limitation. Record whether a *fresh* new note starts correctly afterward.

If CPU load is already high or you encounter sustained silence, **reduce count immediately, then F8 if necessary**; do not leave a prolonged runaway DSP workload. If the same held note cannot recover after a count reduction while a newly started note can, that is an important **failure**, not an expected result.

### C. Baseline and alternate configuration

Do a short **new playback session** at the original light settings. Confirm the status-bar underrun counter reset for the new session and that the CSV has another session ID. If practicable, repeat A on a second sample rate (**44,100 Hz**) or a different Linux audio backend/device and note which one; this is optional, not a requirement to complete the first Linux pass.

## Windows follow-up (only when convenient)

Use the same current PR branch and a Windows system with a working **.NET 10 SDK/runtime** and an actual playback device. Launch from PowerShell:

```powershell
$env:HERESY_C5_AUDIO_CSV = Join-Path $HOME ("heresy-c5e-windows-{0}.csv" -f (Get-Date -Format yyyyMMdd-HHmmss))
dotnet run --project Heresy/Heresy.csproj -c Release
Write-Host "C5(e) CSV: $env:HERESY_C5_AUDIO_CSV"
```

Use 48-kHz stereo if available and repeat at least A. Record Windows version, CPU, actual output device/backend and audio settings. If Windows .NET 10 is unavailable, **do not treat Windows as tested**: report that limitation and keep its qualification pending.

## Results to return

Please send:

1. The generated **CSV file** (or its text) with at least one full device session; include the exact git commit and whether the file contains multiple session IDs.
2. Linux OS/version, CPU, .NET version, SDL/default audio backend and physical output device, actual sample rate/channel layout and Filter/Multi graph used.
3. For A and B: pass/fail, whether any click/pop/dropout/silence occurred; whether **the same note** recovered on **17→3** and after rapid/concurrent edits; any **Audio underruns** increase; and whether Runtime Diagnostics reported a fault.
4. Whether Windows was run; if so its equivalent report/CSV, otherwise simply **not yet tested**.

Screenshots of the status bar or Output Scope are helpful for anomalies, but not required if the report and CSV capture the issue. The CSV is a sampled diagnostic, so if an audio defect occurred between samples, the **human listening result is important independent evidence**.

Only then can C5(e) be marked as **physical Linux pass, Linux fail requiring repair, or explicitly partially qualified**, with machine-specific limitations preserved for C5(f).
