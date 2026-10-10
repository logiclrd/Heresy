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

## Not part of this milestone

The chromeless, four-second, dismiss-on-input startup splash screen
remains a separate item. The supplied `Images/Logo.axaml` logo is
unchanged, ready for that work.
