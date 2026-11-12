# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

SlideDock is a WPF dock that replaces the Windows 10/11 taskbar. The whole app is **one file, `SlideDock.cs`** (~11k lines), compiled by the `csc.exe` that ships with .NET Framework 4.8. There is no project file, no NuGet, no test suite and no linter.

## Build, run, release

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1                    # -> SlideDock.exe next to the source
powershell -ExecutionPolicy Bypass -File build.ps1 -Out <path>\X.exe  # compile check without touching the running copy
powershell -ExecutionPolicy Bypass -File packaging\pack.ps1           # release files + Store .msix in dist\
```

- **C# 5 only** (Framework `csc`): no string interpolation, `?.`, `nameof`, expression-bodied members, `out var`, tuples. Anonymous `delegate { }` is the house style for handlers.
- The user usually runs `SlideDock.exe` from the repo root. A running copy locks the file, so stop the `SlideDock` process before building to the default path, then start it again. To only check that it compiles, build with `-Out` into a scratch folder.
- Verification is manual: build, restart the app, try it. For Win32 effects, measure from outside the app (for example the work area via `SystemParametersInfo(SPI_GETWORKAREA)` in a fresh DPI-aware process; `Screen.WorkingArea` in PowerShell is cached and DPI-virtualized).
- Version comes from `VERSION`. **Release = bump `VERSION`, commit, push to `main`.** The GitHub workflow builds and publishes `v<VERSION>`; installed copies offer the update. Commit subject is `SlideDock X.Y.Z: <user-facing summary>`, and the body is `- ` bullets (wrapped lines indented two spaces). The workflow turns those bullets into the release notes shown inside the app, so write them for end users. Pushes without a `VERSION` change only produce artifacts. `gh` is not authenticated on this machine.
- Code comments are in Indonesian; all UI strings are English. Keep both conventions. The working copy uses LF line endings.

## Architecture

**Entry and wiring (`Program.Main`, end of file).** Creates one `DockWindow` per monitor plus the shared popups (`StartWindow`, `SearchWindow`, `QuickPop`, `CalendarPop`, `TrayPop`), wires dock callbacks (`OnToggle`, `OnQuick`, `OnClock`, ...) to them, and builds the tray menu, the dock right-click menu (`dockMenu`) and the **Dock settings** list (`start.SettingsProvider`). Changing the dock position or the number of screens calls `rebuildDocks`: new `DockWindow`s are created and the old ones closed. Code that holds a `DockWindow` must survive that.

**Settings.**
- `Prefs` is a `key=int` store in `%APPDATA%\SlideDock\prefs.txt`, with typed properties for every option.
- Other state has its own files in the same folder: `PinStore` (dock pins), `StartStore` (Start apps), `Layout` (dock side), `theme.txt`, and the calendar data.
- Dock settings rows are `DockSetting` objects (`Choices` = segmented control, `Toggle`, `Click` = link row, `Input` = textbox plus Save; a row with only `Label` is a gray info line). They are rendered by `StartWindow.RenderSettings`, which redraws the whole list after every change.
- To add an option: add a `Prefs` property, add a `DockSetting` in `SettingsProvider`, and make its `Set` call `applyDock()`. That calls `DockWindow.ApplyPrefs()` on every dock, which runs `ApplyContents`, `ApplyMode`, `UpdateClock` and `ForceRefresh`.

**`DockWindow`.**
- **Window and surfaces.** A transparent window that spans the screen edge (full width × 150 DIP, or 160 DIP wide on a side dock), so the dock can morph without the window moving. It holds two surfaces: `fullCard` (the dock) and `notch` (an idle pill). `SetCompact` animates one shape between them. Content sits in `host` (carries `hideShift` for Auto-hide) → `rootGrid` (carries the dock-size `LayoutTransform`).
- **Timers.**
  - `collapseTimer` → `HoverPoll` (150 ms) decides notch, peek, full dock or auto-hide from the cursor position and from open popups and menus.
  - `tickTimer` → `Tick` (1 s) updates the clock, fullscreen hiding, `KeepOnTop`, and `UpdateItems`.
- **App icons.**
  - `UpdateItems` gets the list from `BuildItems` (pins from `PinStore` plus running windows, matched by AUMID, exe, title or description).
  - `FilterShown` then applies the Show apps and Show open apps from options.
  - The row is rebuilt only when `SigOf(list)` changes.
  - The notch always reflects every app open on this screen, unfiltered.
- **Dock modes** (`Prefs.DockMode`, applied in `ApplyMode`):
  - 0 Floating: notch allowed (`Prefs.NotchOn`). `FitAboveDock` pushes new windows above the dock.
  - 1 Fixed: registers an app bar (`SetupAppBarNow`/`UpdateAppBar`, `SHAppBarMessage`) on the dock's own monitor and edge, so Windows shrinks the work area.
  - 2 Auto-hide: `AutoHidePoll` slides `host` off-screen and reveals it when the pointer touches the screen edge.
  - Popup anchors (`SurfaceTop`, `SurfaceRect`, `CenterOnScreen`) subtract `hideShift`.
- **Win32 hooks and taskbar.** `KeepOnTop` and the WinEvent hooks keep the dock above app windows. `TaskbarCtl` hides the real Windows taskbar while SlideDock runs and must restore it on exit, uninstall, crash and session end. Don't break those paths.

**Popups (`Popover` base).**
- Shared slide-in and close behavior. Positioning follows the static `Popover.DockSpan`, `Scr` and `SideGap`, which the `use(dock)` lambda in `Main` sets before a popup opens.
- `Popover` also gives every element with a **local `Cursor = Cursors.Hand`** a press-and-bounce effect (`UI.PressTarget`, `UI.PressIn`, `UI.PressOut`):
  - The effect creates a `ScaleTransform` only when the element has no `RenderTransform`.
  - An element that needs its own transform must use a `TransformGroup` that contains a `ScaleTransform`, as the Start tiles do.
  - `FlatSlider` is excluded.
- `QuickPop` is a single popup with modes `wifi`, `sound`, `battery` and `all`. Its side views are rebuilt in `SwitchMode` when the layout or the Simple/Detailed panel style changes.

**Drag and animation.**
- Dragging a Start tile uses `GhostWin`: a small topmost, click-through window that carries the dragged icon, so the big panel doesn't redraw every frame. The other tiles slide aside with `TranslateTransform` animations, and the new order is saved with `StartStore.Reorder`.
- Dock pin reordering (`ReorderPress`, `ReorderMove`, `ReorderStep`) and most other motion runs per frame on `CompositionTarget.Rendering` with `AnimClock`.

**Theming.**
- Themes are `glass` (dark glass), `glass2` (light/white glass, dark text) and `dark`.
- Use `UI.Fg`, `UI.Ink(alpha)` and `UI.AccentInk`. They are shared brushes that get recolored in place when the theme changes.
- Hard-coded white or `#60CDFF` has poor contrast on Glass 2. The dock restyles on theme change in `DockWindow.ApplyTheme` (see `StyleStartIcon`).

**DPI.** The manifest declares PerMonitorV2. WPF code works in DIPs. Win32 calls (`SetWindowPos`, app bars, `GetCursorPos`, monitor rectangles) use physical pixels; get pixels from WPF with `PointToScreen`.

## Other files

- `packaging/` holds the Store `AppxManifest.xml` and `pack.ps1`.
- `tools/make-assets.*` regenerates the icon and Store logos.
- `bug-finding/` is the user's local diagnostics folder and is not tracked; don't commit it.
- Logs are `%APPDATA%\SlideDock\startup.log` and `error.log`.
