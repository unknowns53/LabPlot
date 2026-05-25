# Changelog

All notable changes to LabPlot are tracked in this file. The mainline
distribution is the Avalonia portal (`LabPlot.Avalonia`); the WPF portal
(`LabPlot.exe`) is maintained on the v1.0.x line and is not updated here.

The format is loosely based on [Keep a Changelog](https://keepachangelog.com).

## [1.3.0] - 2026-05-25

Follow-up to the v1.2.0 Avalonia mainline. This release lands a wide
data-processing correctness sweep driven by parallel Codex static reviews,
restructures the DLS module around a dedicated AnalysisWindow with four
analysis tabs (cumulant / temperature ramp / concentration series / CONTIN),
adds a polish pass over the desktop UX (status bar, toasts, F1 cheat-sheet,
recent-files menu, result-copy buttons, animated result readouts), embeds
the LabPlot app icon across windows, and ships the first end-user-facing
documentation set under `docs/user-guide/`.

### Added

- **DLS AnalysisWindow** (`src/LabPlot.DLS.Avalonia/AnalysisWindow.axaml`)
  hosts cumulant fit, Boltzmann temperature-ramp, concentration-series
  (D₀ / k_D / d_h via Stokes–Einstein), and CONTIN-style size-distribution
  inversion as a vertical expander stack — multiple sections can be open
  simultaneously, each runs `RecomputeAllSections` on data changes, and
  chrome is aligned with the main window's `CustomTitleBar`.
- **NNLS-based size distribution inverter** (CONTIN Phase 1+2) with
  α / R² / β / free-bin diagnostics surfaced in the AnalysisWindow.
- **Boltzmann temperature-ramp analysis** for cloud-point / coil-globule
  transitions on DLS temperature scans (T_c / w / plateaus / R²).
- **Concentration-series analysis** for diffusion coefficient
  extrapolation and hydrodynamic radius determination.
- **DLS demo workbook** (`tools/DlsSampleGenerator`) committed at
  `samples/demo.xlsx` so users can exercise every analysis path without
  proprietary Zetasizer data.
- **Solution-side DLS metadata** is shared across sheets and committed
  live, so concentration / solvent / temperature entered in one sheet
  propagate to every related sheet immediately.
- **Status bar control** with severity-aware icon and color states.
- **ToastHost** for instant save / reset feedback.
- **F1 keyboard-shortcut cheat-sheet** available on the portal, the three
  analysis windows, and the GPC calibration-curve window.
- **Result-copy buttons** in GPC (representative-peak Mn / Mw / Đ chip)
  and Spectrum (cloud-point Tc / k / R² block) plus DLS (cumulant fit).
- **Recent-files menu** persists the five most-recently-opened files per
  app across sessions.
- **AnalysisWindow expander state** is persisted between sessions in DLS
  so users return to the same open sections they left.
- **Animated result numbers** (`NumberCountUp` helper) — analysis result
  values ease over 200 ms when they recompute, parsing only the leading
  numeric token so unit-bearing strings like `123.4 nm` stay formatted.
- **Reset-to-defaults confirm dialog** (`Core.Avalonia ConfirmDialog`)
  guards the three "既定値に戻す" buttons behind a destructive-color
  modal so a misclick no longer wipes title / axis labels / range /
  line styles in one shot.
- **File name in title bar**: the subtitle and OS task-bar window title
  reflect the currently-open session / data file.
- **End-user documentation** under `docs/user-guide/` covering
  installation, portal usage, quick-start walkthrough, per-module guides
  (GPC / Spectrum / DLS), device-specific data preparation skeletons,
  a troubleshooting guide, and FAQs. Avalonia mainline READMEs and the
  root README link into it.
- **App icon** embedded across every Avalonia window and the portal
  launcher (refreshed with stronger color saturation in a follow-up).

### Changed

- **DLS sidebar reduction**: the DLS main window's sidebar was compressed
  by moving the four analysis sections and measurement-condition editor
  into the AnalysisWindow tabs. `IDlsAnalysisHost` (with the new
  `RequestAnalysisDataChanged` signal) keeps the two windows in sync.
- **AnalysisWindow tab layout** evolved from a `TabControl` to a vertical
  `Expander` stack (`SectionStyle ×5`) so users can compare multiple
  analyses side-by-side without re-clicking tabs.
- **Section header style** is now unified between sidebar and
  AnalysisWindow for a single visual vocabulary.
- **Cumulant numerical stability**: τ is centred and scaled before the
  Cramer solve to avoid ill-conditioning on long-tau acquisitions.
- **Cumulant auto-threshold** now restricts itself to a contiguous τ
  window instead of accepting disjoint regions that produced unstable
  fits on noisy ACFs.
- **Cloud-point detection** is gated on signal amplitude and plateau
  contrast so flat / noise-only spectra no longer report spurious Tc.
- **λ-max detection** gained a `MinimumProminence` filter that suppresses
  near-flat shoulders being reported as peaks.
- **Flat-top plateau peaks** are now collapsed to a single representative
  point in both λ-max and IR detection paths.
- **GPC calibration extrapolation** is detected and flagged when the
  elution time falls outside the curve's calibrated MW range; the
  `Math.Pow` MW evaluation guards against double overflow on extreme
  extrapolation.
- **GPC statistics** correctly area-weight Mn / Mw across overlay peaks
  (the previous implementation summed unweighted values).
- **Beer-Lambert calibration** now flags absorbance values that fall
  outside the linear-response range used when the calibration was built.
- **JASCO V-750 reader** tracks `NPOINTS` from the file header and
  detects truncated exports where the actual point count diverges.
- **Zetasizer reader** handles narrow integration regions and
  seconds-axis exports (older firmware variants).
- **IR parabolic apex** direction sign was corrected for descending
  baselines.

### Fixed

- **AnalysisWindow first-open NRE** on DLS when no run was selected
  before opening the analysis tab.
- **Cloud-point false positives** on noise-dominated temperature scans.
- **Cumulant Cramer instability** on long-τ acquisitions (companion to
  the Changed-section centring/scaling).
- Numerous P1 / P2 data-processing bugs surfaced by parallel Codex
  reviews of DLS / GPC / Spectrum (372 unit tests now pass green:
  DLS 179 + Spectrum 167 + GPC 26).

### Misc

- **`*.lscache` ignored** so local lint / Codex cache files stop landing
  in `git status`.
- **Codex review artifact exclusion** and image directory tracking added
  to repository hygiene.

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
