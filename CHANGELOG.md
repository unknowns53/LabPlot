# Changelog

All notable changes to LabPlot are tracked in this file. The mainline
distribution is the Avalonia portal (`LabPlot.Avalonia`); the WPF portal
(`LabPlot.exe`) is maintained on the v1.0.x line and is not updated here.

The format is loosely based on [Keep a Changelog](https://keepachangelog.com).

## [1.2.0] - 2026-05-08

This release ships the **Avalonia mainline portal** for the first time. Phase 7
of the project ported all three analysis modules (GPC, DLS, Spectrum) plus the
launcher to Avalonia 11.3, established the new portal as the primary
distribution, and incorporated user feedback from real-machine validation.

### Added

- **Avalonia mainline portal** (`LabPlot.Avalonia`) covering GPC / DLS /
  Spectrum through the same card launcher and `CustomTitleBar` as the WPF
  portal, but built on `net10.0` so the same exe runs on Windows / macOS /
  Linux.
- **GPC chromatogram header**: solvent / detector badge that reflects the
  currently selected calibration curve.
- **GPC overlay statistics**: per-dataset peak selectors with a scrollable
  multi-row layout (max-height 108px) and Mn / Mw / Đ values rendered as
  `SelectableTextBlock` so individual numbers can be copied.
- **Plot placeholder three-state machine** (`Initializing` / `EmptyReady` /
  `InitFailed`) driven by a small `PlotPlaceholder` helper. The placeholder
  now survives `PlotHost.Children.Clear()` and restores after every dataset
  is removed.
- **Phase 7 smoke test checklist** for DLS and Spectrum
  (`docs/Phase7_smoke_test.md`).

### Changed

- **Mainline / maintenance split**: the Avalonia portal is now the recommended
  build for new users (the root `README.md` was reorganised around it). The
  WPF portal stays available as the `v1.0.x` maintenance branch for
  Windows-only deployments.
- **AXAML bindings**: `MainWindow` and `CalibrationCurveWindow` of every
  Avalonia app, plus `AxisRangePanel` / `ColorPickerPanel`, were promoted
  from `{ReflectionBinding}` to `{CompiledBinding}` with explicit
  `x:DataType`. Binding typos now fail at build time.
- **File drag-and-drop** in all three apps was migrated to the Avalonia 11.3
  `DataTransfer` / `DataFormat.File` API (replacing the `[Obsolete]` legacy
  surface).
- **Dataset reordering**: replaced the OS `DragDrop` reorder with a manual
  `PointerCapture` controller, complete with a cursor-following drag ghost
  in GPC / DLS / Spectrum.
- **Legend drag**: a 9-cell anchor (`PlotAppearance.ChooseBestLegendAnchor`)
  re-anchors the legend mid-drag so it can reach any corner of the data area
  without overflowing.
- **Build hygiene**: `tools/run-avalonia.ps1` and the build instructions now
  use `-nodeReuse:false /p:UseSharedCompilation=false` to stop MSBuild /
  Roslyn from leaving `dotnet.exe` ghost processes.

### UI brushup (seven-commit polish pass)

- **Sidebar / overlay scrollbars** are now always visible at 10px width with
  a custom `{x:Type ScrollBar}` `ControlTheme`, replacing the Avalonia Fluent
  auto-hide overlay that felt dated and made the sidebar look broken on
  first paint.
- **Button hover** restores a 1px `TranslateTransform.Y` lift animation and a
  pressed-state push-down, matching the WPF storyboard that was dropped
  during the migration.
- **Expander chevron** stroke now flips to the accent colour on hover via a
  sibling style selector, working around the Avalonia 11 single-style
  `/template/` nesting limit.
- **AutoCompleteBox** (font picker) gains a proper popup list themed
  identically to `InputComboBoxStyle`.
- **Dialog windows** (`AbsorbanceConfirmDialog`, `CalibrationCurveWindow`)
  drop their ad-hoc button `ControlTheme`s in favour of `PrimaryButtonStyle`
  / `SecondaryButtonStyle`, and now call `WindowAppearance.ApplyDefaults`
  in their constructors so they share the MainWindow's subpixel
  anti-aliasing.
- **Shimmer animation** for the plot skeleton and the chrome-button stroke
  colour are centralised in `ImplicitStyles.axaml` instead of being
  copy-pasted across the three apps.
- **`FocusRingBrush`** (`#60A5FA`) is now a shared resource consumed by every
  Input / Button `ControlTemplate`.

### Fixed

- **Plot placeholder** no longer reads "graph initialising..." forever after
  a successful empty-data startup, and reappears with "load a file to
  display data here" when every dataset is removed.
- **Avalonia runtime crashes** uncovered during Phase 7 Batch 6 Windows
  trials (Expander template part name, ColorPicker `[Content]`, manual
  `InitializeComponent` vs `Avalonia.Generators`).
- **Default font weight** bump and ClearType / SubpixelAntialias rendering
  so Yu Gothic UI text is no longer faint inside Avalonia popups.

### Distribution

- `csproj <Version>` for `LabPlot.Shell.Avalonia` and the three module
  projects (`LabPlot.GPC.Avalonia`, `LabPlot.DLS.Avalonia`,
  `LabPlot.Spectrum.Avalonia`) is now `1.2.0`. The WPF portal
  (`LabPlot.Shell`) and its modules remain on the `v1.0.x` maintenance line
  and are not bumped.
- The win-x64 self-contained build is attached to this release as
  `LabPlot-v1.2.0-win-x64.zip`. Per the README, macOS / Linux builds are
  produced ad-hoc with `dotnet publish ... -r osx-arm64 / linux-x64`.

## [1.1.0] - 2026-05-07

Initial tagged release of the WPF mainline. See `git log v1.1.0` for the full
list of changes leading up to it.

[1.2.0]: https://github.com/unknowns53/LabPlot/releases/tag/v1.2.0
[1.1.0]: https://github.com/unknowns53/LabPlot/releases/tag/v1.1.0
