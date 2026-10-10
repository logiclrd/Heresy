# Application identity: window and executable icons

The supplied multi-resolution `Heresy.UserInterface/Images/Icon.ico` is
the **single source of truth** for the app icon. No image conversion, extra
rasterization or copied/renamed branding asset is needed.

## Running desktop window

`Heresy.UserInterface.csproj` declares
`<AvaloniaResource Include="Images/Icon.ico" />`. In
`MainWindow`'s constructor, `Window.Icon` is initialized from
`avares://Heresy.UserInterface/Images/Icon.ico` using
`Avalonia.Platform.AssetLoader`. This works from compiled assemblies
regardless of working directory, app installation layout and platform;
the window manager can use the supplied sizes for title-bar, taskbar
and window-switcher surfaces supported by the host.

## Native Windows application host

`Heresy/Heresy.csproj` sets the standard .NET SDK
`<ApplicationIcon>../Heresy.UserInterface/Images/Icon.ico</ApplicationIcon>`
property. This applies to the Windows `Heresy.exe` native apphost as
well as the managed executable metadata without bundling a separate
visible `.ico` file next to the executable. Non-Windows SDK builds
remain supported.

The GitHub Actions workflow retains its cross-platform build checks
and additionally cross-publishes `win-x64` from the Linux runner
(`--self-contained false -p:UseAppHost=true`). The pure-stdlib script
`build/verify-windows-apphost-icon.py` parses the published PE header
and root resource directory to require both RT_ICON (3) and
RT_GROUP_ICON (14). This verifies actual apphost embedding; an
`ApplicationIcon` property alone or a nearby icon file is insufficient.

`ApplicationBrandingTests` also opens the bundled Avalonia asset using
`StandardAssetLoader` in the uninitialized unit-test environment and
validates that it is a multi-image ICO. It intentionally doesn't
create an Avalonia `Window` in the headless test runner.

## Startup splash (implemented)

The supplied `Heresy.UserInterface/Images/Logo.axaml` is used unchanged
as the content of `StartupSplashWindow`, with no raster conversion.
After `MainWindow.Opened`, `App` schedules creation at Avalonia's
`DispatcherPriority.Loaded` (and checks the main window remains visible).
It calls the nonmodal `Show(mainWindow)` overload, making the splash
an **owned** auxiliary window rather than the lifetime's main window.
The splash uses `WindowDecorations.None`, `CanResize = false`,
`ShowInTaskbar = false`, and **always** requests
`WindowStartupLocation.CenterScreen` when created. The initial
`Opacity = 0` hides provisional placement while the native window
manager completes creation. On `Opened`, a UI-thread timer advances
the window opacity linearly from 0 to 1 over **250 ms**, using a monotonic
stopwatch and approximately 16 ms timer ticks. Closing the window
stops the fade; it never delays the main document or the audio backend.

On coordinate-capable windowing backends (notably X11 and Windows),
the splash is explicitly centered over its owner's physical-pixel
rectangle after opening. A maximized owner anchors to its current
display's working area; different display positions and scaling are
accounted for. On certain KDE Plasma 6/X11 configurations, the window
manager can relocate a newly created window under the mouse cursor
after an initial placement request. To accommodate this, the splash
also observes native `PositionChanged` for **both the owner and the
splash**, and `Resized` for the owner, during the **first 50 ms after
opening**. These events request a coalesced UI-thread recenter using
the latest available native coordinates. Owner maximization/restoration
state changes continue to trigger recentering throughout the splash's
lifetime, not just during the initial 50 ms.

On Linux Wayland, ordinary top-level coordinates cannot reliably
be chosen by a client; the owned splash requests `CenterScreen`
as a best-effort compositor hint and does not attempt explicit moves.
The compositor may ignore the hint or choose a display. An explicitly
reported `XDG_SESSION_TYPE=x11` overrides an incidental
`WAYLAND_DISPLAY` variable to avoid suppressing X11 positioning.
Native placement failures are nonfatal (visible in development
diagnostics); the CenterScreen hint remains the fallback.

**Verification status:** The earlier post-open centering implementation
passed geometry unit tests but failed a user desktop check on KDE
Plasma 6/X11 with under-mouse window placement. The 50 ms follow-up
and 250 ms fade are implemented and covered by headless event-policy
and fade-math tests, but this revision still requires a **real desktop
check in normal and maximized states** before declaring placement
correct. CI cannot verify compositor-enforced positions.

Once the splash raises `Opened`, a UI-thread `DispatcherTimer` starts
with a four-second interval and the splash activates for keyboard input.
Routed `KeyDown` and `PointerPressed` are observed during the tunneling
phase (including previously handled child events), **both on the splash and
its still-interactive owner**. Any input in either window immediately
dismisses the splash without consuming the owner's normal event processing.
The owner input subscriptions are removed when the splash closes.
A timer tick or owner shutdown also requests immediate dismissal.
`StartupSplashDismissal` arbitrates all four reasons, marks itself
dismissed **before** closing the window, and stops the timeout exactly
once. An external window-manager close also disarms the timer without
closing twice. `StartupSplashWindow.Closed` releases the owner and timer
subscriptions, so it cannot survive closing the main application window.

`StartupSplashDismissalTests` cover the exact four-second interval,
each input/timeout/owner close, external close, and synchronous
reentrant close events without depending on a live desktop environment.
The full desktop host is still built and cross-published on CI; the
headless tests intentionally test the lifecycle contract separately
from window-manager-specific focus rendering.

## Main window maximization preference (implemented)

The desktop `App` now uses `MainWindowStatePreference` for the
binary **maximized vs normal** state, not for size/position or song
content. It stores a short version-one token (`v1:maximized` or
`v1:normal`) under the user's
`Environment.SpecialFolder.LocalApplicationData/Heresy/` folder.
Writing uses an atomic temporary-file rename. Missing, unreadable
or malformed preferences are nonfatal, and unwritable storage is
treated as best effort.

At application startup, the preference is read, requested on the
main window and reapplied when it opens (some platform window
implementations ignore pre-open state setters). This happens
**before** the dispatcher posts the splash, so the splash
can use the restored maximized work area. State changes observed
through Avalonia's `Window.WindowStateProperty` are written
immediately, only when the binary preference actually changes.
Minimized and fullscreen states never write a false/normal
preference by themselves.

`MainWindowStatePreferenceTests` cover missing/malformed files,
round-trip writes, unavailable storage and transition deduplication.
`StartupSplashPlacementTests` cover coordinate centering, negative
secondary-monitor coordinates, maximized work areas, pixel scaling,
Wayland/X11 detection, early owner/splash move notifications,
late maximize changes and the 250 ms fade progression. The CI
runner is headless: actual compositor positioning and window-manager
maximize notifications require a desktop smoke test.
