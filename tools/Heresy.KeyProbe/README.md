# Avalonia/X11 keyboard event probe

This is a **standalone diagnostic executable**, not a modification of Heresy's
input handling. It uses the same **Avalonia 12.1.3** packages as Heresy but
does not reference Heresy's assemblies, the PR #22 workaround, or any other
application input hooks.

## Run two comparable sessions

From the repository root, with .NET 10 installed:

```bash
# A normal Xorg desktop (or X11/XWayland without Xephyr)
dotnet run --project tools/Heresy.KeyProbe -c Release -- \
  --label xorg --output "$PWD/keyprobe-xorg.log"

# The SAME command launched inside the existing Xephyr environment
dotnet run --project tools/Heresy.KeyProbe -c Release -- \
  --label xephyr --output "$PWD/keyprobe-xephyr.log"
```

The process inherits `DISPLAY`, so **launch it in the actual target
session**. Do not merely give a label "xephyr" while still using the Xorg
display. For instance, if Xephyr listens on `:2`, run in its session or use
`DISPLAY=:2 dotnet run ...` with the correct Xauthority context.
The tool does **not** change any XKB settings or install an input workaround.

The window contains two **ordinary, unchanged Avalonia TextBoxes**. Use the
first for a consistent test sequence, with pauses between groups:

1. Caps Lock **off**: `' , . ;` and digits `0 2`.
2. Caps Lock **on**: repeat exactly the same keys.
3. Caps Lock **on** **with physical Shift held**: repeat the four keys.
4. Caps Lock **off** again, then repeat.
5. Repeat a subset in the second (multiline) TextBox.

Use **the same physical keyboard, input layout and key sequence** in both
sessions. Save both `.log` files. Restart with a fresh output path for each
attempt; existing files are not overwritten. The log streams to disk while
you type and includes text entered only in the two dedicated test fields.
Do not type passwords, private information or other sensitive text there.

## What the applet records

- UTC and elapsed timestamps; source control/route stage
  (window/tunnel, target/tunnel, target/bubble, window/bubble).
- Avalonia `KeyDown`/`KeyUp`: virtual `Key`, `PhysicalKey`,
  `KeySymbol` (and Unicode scalars), `KeyModifiers`,
  `KeyDeviceType`, and `Handled`.
- Avalonia `TextInput`: exact `Text` and Unicode scalar values, with the
  preceding physical key (when Avalonia delivered one).
- The real TextBox `TextChanged` result and caret/selection positions.
- A **separately connected native X11/XKB snapshot** for each key/text event:
  effective, base, latched, locked, lookup, grab and compatibility modifier
  masks, plus groups and pointer-button mask. In particular it distinguishes
  `ShiftMask=0x01` from `LockMask=0x02`.
- A startup (and manually refreshable) native X11 server/vendor/version,
  XKB extension version, X11 modifier keycode map, **every native keycode's
  group-0/group-1 unshifted/shifted keysyms**, and `XkbLookupKeySym`
  under none/Shift/Lock/Shift+Lock for the four affected punctuation keys.
- Environment `DISPLAY`, `XDG_SESSION_TYPE`, `WAYLAND_DISPLAY`, XKB
  layout defaults, .NET runtime and Avalonia assembly version.

The XKB snapshots are queried on a **second, read-only X11 connection**.
They do not expose the exact native X event keycode consumed by Avalonia.
The physical-key value reported by Avalonia and the native keyboard map are
independent observations. If those two layers do not explain the discrepancy,
use a raw XInput2 trace as outlined below.

The log window shows recent messages (capped for responsiveness), while the
log file keeps **the full session**. The button "Snapshot native XKB..." adds
another full snapshot to the log during a test, useful immediately after
turning Caps Lock on/off.

## Optional independent X11 and raw XInput2 collection

To distinguish a Xephyr XKB map problem from an Avalonia conversion problem,
capture the X11 tools' observations in each session too:

```bash
bash tools/Heresy.KeyProbe/collect-x11-session.sh xorg
bash tools/Heresy.KeyProbe/collect-x11-session.sh xephyr
```

The script saves `x11-xorg-*.txt`/`x11-xephyr-*.txt`, plus a complete
`*.xkb` keymap if `xkbcomp` is installed. Missing commands are reported
but do not prevent a diagnostic run. It does not alter server settings.

For the **exact native XI2 key events**, optionally launch in a *separate*
terminal bound to the *same* DISPLAY, then type the four keys into the probe:

```bash
xinput test-xi2 --root | tee xinput-xephyr.log
# End capture with Ctrl+C.
```

Do the equivalent under Xorg if available. `xinput`, `xkbcomp`,
`setxkbmap`, `xmodmap`, and `xdpyinfo` may require optional X11 utility
packages. `xev` can also test keys in its **own** window, but cannot
substitute for Avalonia's event trace.

## How to interpret results

- If **native XKB LockMask is on but BaseMods/LatchedMods has no Shift**,
  and the Avalonia `KeySymbol`/`TextInput` is unexpectedly shifted,
  focus investigation on the Avalonia-X11 key translation and XKB layout
  lookup; compare the native `XkbLookupKeySym` results.
- If **native XKB mapping or XkbLookupKeySym itself** resolves Caps+punctuation
  to the shifted keysym, examine Xephyr's actual keymap and compatibility
  state (full `.xkb` is especially valuable).
- If `KeySymbol` is correct but `TextInput` is wrong, investigate
  Avalonia's XIM/IME text commit path rather than its key mapping.
- If native state differs between KeyDown and TextInput, record both;
  a second X11 client can observe a **later** server state than the event
  that Avalonia is processing, so these snapshots are observations, not
  proof of an event's exact modifier mask.

Do **not** infer an Avalonia bug solely from the problem reproducing in
Controls Showcase: both apps could be affected by the same Xephyr/XKB
configuration. Compare the full logs and native map first. Please attach
the two probe logs and native XKB snapshots to the investigation; raw XI2
logs are optional.

The probe never alters the user's Caps Lock mode or Heresy's Hold/Latch
behavior. Existing draft PR #22 is intentionally left **unmerged**.
