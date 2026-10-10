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
The splash is centered on its owner, with `WindowDecorations.None`,
`CanResize = false` and `ShowInTaskbar = false`. The main document
window and lazy SDL playback transport initialize independently; neither
is delayed by a four-second await/sleep.

Once the splash raises `Opened`, a UI-thread `DispatcherTimer` starts
with a four-second interval and the splash activates for keyboard input.
Routed `KeyDown` and `PointerPressed` are handled during the tunneling
phase (including previously handled child events). They request immediate
dismissal, as does the timer tick or a close of the owner window.
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
