# PR #12 — C5(f) observed realtime load envelope (Linux-first)

**Status (2026-11-15): BASELINE RECORDED / TRIAL METHODOLOGY AUTOMATED; LOAD-ENVELOPE SIGNOFF OPEN.** C5(a–d) have bounded automated acceptance. C5(e)'s first **actual Linux** device test established that an original held note recovered after overload, but did not establish the highest sustainable load. Windows is unavailable to the tester and **not tested**, not failed. C6 listening, C7 authorization, overall C5 and the all-nonstructural Synth TODO remain open. Do not merge PR #12.

## Scope: one machine, graph, output device, and snapshot of host conditions

The measured machine is an **8-core / 16-thread AMD Ryzen 7 7840HS**, ~30.65 GiB RAM, Linux kernel **7.0.0-38-generic**, PulseAudio compatibility on **PipeWire 1.6.2**, default sink **`alsa_output.pci-0000_c1_00.6.analog-stereo`**. The previously uploaded physical SDL session used **48,000 Hz stereo**, a **2,048-frame PCM ring** and **256-frame (5.33-ms) producer quantum**. The user's `dotnet --version` gave SDK **11.0.100-rc.2.26504.105**; this does **not** establish the runtime actually running the net10.0 Heresy application.

The screenshot's Synth was **Sine Oscillator (440 Hz) → Filter → Multi [OUTPUT]**, with the pictured Multi at **Spread / 17 voices / 12 total detune cents / Randomize Phase ON / Gain Compensation ON**. The exact Filter stages/parameters and exact CPU frequency under load were not logged. Keep the **same graph, filter, detune, randomize-phase and gain-compensation settings** for comparisons; changing other knobs makes this a different envelope.

### Already observed on real hardware — do not repeat just to complete the checklist

| Tested condition | Evidence | Classification |
| --- | --- | --- |
| Return to a one-note three-voice configuration after 17 voices | Original held note audibly recovered; no new underruns across a long subsequent CSV interval | **Observed recovery PASS**, not a full repeated 12-second three-voice standalone benchmark |
| One sounding 17-voice note | Tester heard warbling/discontinuity; ~91 new underruns and 1,826 over-budget PCM blocks in the first stress window | **Unsupported for reliable realtime playback** on this graph/host |
| Three sounding notes at nine or more Multi voices each | Tester heard overload; ~141 new underruns and 363 over-budget blocks in the second stress window | **Unsupported** for reliable realtime playback on this graph/host |
| Return to three voices after multi-note overload | Same original playback promptly recovered; no further underruns for ~20 seconds | **Observed recovery PASS**, not a universal voice-limit guarantee |
| Seven voices at initial note start | The initial first note used the default seven, before count was reduced to three; no independently marked load interval | **Unknown**; no inference of safety from this note start |
| One-note five/seven/nine; two or three notes at three/five/seven | No isolated, time-marked controlled physical result yet | **Unknown; priority of C5(f) short follow-up** |

The original physical log had **236 total ring underrun events** (4 startup, 91 in the first heavy interval, 141 in the second) and **2,191 producer blocks over their nominal budget**. A quick SDL callback did not compensate for an overloaded PCM producer. C5(e) already documents this [in detail](synth-live-pr12-c5-realtime-qualification.md#c5e--first-tester-observed-linux-sdl-device-run-physical-recovery-verified-with-overload-2026-11-15).

This does **not** imply a threshold at 9, 12 or 17 for *other* Synth graphs, filters, output rates, CPU workloads or operating systems. A source count alone is not the relevant cost metric; multiply the active note count by the actual nested constituent count and the DSP work per constituent, plus shared overhead. Even a stable trial is a **bounded observation**, not a worst-case audio deadline guarantee.

## New trial-marker utility (no changes to normal playback)

`build/c5f_load_envelope.py` uses **Python standard library only**, and has two modes:

- `record`: from a separate terminal, creates a small **UTC trial marker CSV** with start/end times for your named trial configurations. Each trial starts **3 seconds after you press Enter** (to switch focus) and records for **12 seconds** with no audio changes during that interval. No IPC to Heresy, device callbacks, source changes or privileged access.
- `report`: reads the existing `HERESY_C5_AUDIO_CSV` capture plus trial markers, pairs each interval to **exactly one real SDL session**, validates nondecreasing per-session counters, and prints per-trial **new underruns, over-budget producer blocks/percentage, average measured PCM render time per block, minimum sampled buffered frames, new worker-prepared Multi children and faults**. It also summarizes entire sessions. The report checks for malformed markers, invalid counters and insufficient samples instead of fabricating zeros.

**Crucial limitations:** counters are sampled every 250 ms, so a trial excludes at most a sample interval near either boundary. Minimum sampled buffer is **not** the guaranteed instantaneous minimum. The `producer_elapsed_ms` average is instrumented wall time, which includes host preemption; it is not a deterministic CPU cost. The CLI **does not automatically pass** an apparently quiet trial: the operator must report whether the note sounded steady, whether there was a pop/warble/temporary silence, and whether UI warnings appeared. SDL stream-request nominal-duration counters do **not** measure actual hardware deadlines. The marker utility does not inspect sample/voice state, so record the number of notes and constituent voices truthfully in each trial label.

### One focused Linux run

1. In **terminal A** at the current PR branch, launch Heresy with a new physical output CSV. Use the same real PipeWire/analog-stereo device and 48-kHz stereo settings as the first run.

   ```bash
   git fetch origin
   git switch feat/synth-live-parameter-foundation
   git pull --ff-only
   export HERESY_C5_AUDIO_CSV="$HOME/heresy-c5f-audio-$(date +%Y%m%d-%H%M%S).csv"
   echo "$HERESY_C5_AUDIO_CSV"
   dotnet run --project Heresy/Heresy.csproj -c Release
   ```

2. In **terminal B**, make a fresh marker file, then start the independent marker utility:

   ```bash
   export HERESY_C5_MARKERS="$HOME/heresy-c5f-markers-$(date +%Y%m%d-%H%M%S).csv"
   python3 build/c5f_load_envelope.py record --markers "$HERESY_C5_MARKERS"
   ```

   For each stage, **first make the edit in Heresy**, verify it is committed and the number of sounding notes is correct, then return to terminal B and enter the named trial label, followed by Enter to start. The program provides a 3-second settle/focus window and a 12-second measurement; **do not change voices or press F8 during the measured 12 seconds**. Return to terminal B afterward for the next stage.

3. Use the same **latched** original Synth with the same Filter and Multi settings as in the first test. Start the **single-note staircase**: `1x3`, `1x5`, `1x7`, `1x9` (voice count is the number after `x`). Stop increasing at the **first configuration with sustained audible degradation or new underruns**; we already know 17 fails and do not need to repeat it. If a stage overloads, reduce **that same note** back to three and mark `recovered-1x3`; report whether the existing note recovers without a new Note On. If all four are quiet, mark `1x3-return` after returning to three. Keep sound levels low.

4. Cut all with **F8**. Reset Multi to three; start two latched notes at least **three semitones apart** (so Latch doesn't suppress a nearby note) and mark `2x3`. Add a third similarly spaced note and mark `3x3`. If both are stable, optionally try `2x5` and `3x5` while preserving the original notes. Do **not** continue to heavier configurations after a failure. Return to three per note and mark `recovered-3x3` if an overload occurs; then F8. It is fine to stop after finding the first unsafe combination. Avoid the already-confirmed `3x9+` overload.

5. Optionally **repeat only the last apparently clean and first unreliable configurations** for one more 12-second trial each (name them `1x7-repeat`, etc.), to guard against scheduler jitter. Listen throughout. Note the actual Filter stages/parameter values, any ambient host load and what Runtime Diagnostics said. Leave Windows for a future machine-access opportunity.

6. Exit Heresy, then run in terminal B:

   ```bash
   python3 build/c5f_load_envelope.py report \
     --csv "$HERESY_C5_AUDIO_CSV" \
     --markers "$HERESY_C5_MARKERS" \
     --output "$HOME/heresy-c5f-report.md"
   ```

   The report will show one line per marked configuration, e.g. `1x3`, `1x5`, etc. **Upload both CSV files and the generated Markdown report**, along with a short description of audible behavior per trial. Files should be retained locally; they are not uploaded into GitHub by the tool. If the marker utility was accidentally stopped during a trial, rerun with a *fresh marker file* to avoid an unpaired timestamp.

The utility also works with **the previous unmarked C5(e) CSV**:

```bash
python3 build/c5f_load_envelope.py report \
  --csv "$HOME/heresy-c5e-linux-20261115-130443.csv"
```

This reproduces the whole-session totals (**236 underruns, 2,191 over-budget blocks, 18,401 blocks, 29 prepared children**) but correctly **does not** attribute them to individual voice-count settings without markers.

## Acceptance rule and future C5(f) signoff

For this specific host/backend/graph, classify each tested interval as **realtime-clean candidate** only if it has **zero new ring underruns, no recorded faults and no audible glitches**, including after settling, and ideally has ample producer headroom. Then consider a second run for borderline loads. Any audible failure or incrementing ring underruns is **not realtime-clean** even if SDL callbacks were individually fast. Preserve the measured producer over-budget percentage and ring-minimum context; a single over-budget block is a warning, not necessarily audible failure. Document the *largest demonstrated clean configuration and smallest demonstrated failing configuration* **within each tested note-count group**, with untested ranges explicitly **unknown**, not presumed safe.

A load that was briefly clean under idle OS conditions is not a guaranteed maximum. No code-level enforcement of a universal voice-count cap is justified by this single machine/graph. A CPU optimization may be considered later, separate from the C5 physical recovery correctness gate, if the user wants to improve the unsupported envelope.

**C5(f) remains OPEN until marked real-device trials and listening outcomes arrive.** The existing C5(e) recovery test should not need repeating. The optional Windows cross-backend pass, C6 listening, C7 final signoff and PR merge remain distinct.
