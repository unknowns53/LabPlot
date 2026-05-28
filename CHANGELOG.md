# Changelog

All notable changes to LabPlot are tracked in this file. The mainline
distribution is the Avalonia portal (`LabPlot.Avalonia`); the WPF portal
(`LabPlot.exe`) is maintained on the v1.0.x line and is not updated here.

The format is loosely based on [Keep a Changelog](https://keepachangelog.com).

## [Unreleased]

### Added

- **デザイントークン辞書を独立リソースに分離**
  (`src/LabPlot.Core.Avalonia/Themes/CommonTokens.axaml`). アクセント / 状態
  フィードバック (Success / Warning / Error の 4 色セット) / 中間階調 / フォーカス
  リング / 角丸 / フォントスタックの全 SolidColorBrush / CornerRadius / FontFamily
  を 1 ファイルに集約。`App.axaml` の MergedDictionaries で `CommonStyles.axaml`
  より先に Include する。新しい `MainBgSurfaceBrush` (#F7F8FA) と Error 系の
  Hover / Pressed 階調 (`ErrorHoverBrush` #B91C1C / `ErrorPressedBrush` #991B1B)
  を追加し、Window 背景と DestructiveButtonStyle がリテラル直書きから DynamicResource
  参照に統一された。Dark theme / accent theme 切替の下地ができている。
- **新モジュール追加 scaffold 手順ドキュメント** (`docs/AddModule.md`). csproj /
  MainWindow / PortalWindow カード / KeyboardShortcutsWindow / slnx 登録までの
  8 ステップを既存 3 モジュールの実装に紐づけて記述。NMR / Raman など将来モジュール
  追加時に「どこを編集すれば動くか」を 1 ファイルで追える。

### Changed

- **3 モジュール + Portal の Window 背景 / Portal カード Hover-Focus 枠を DynamicResource 化**
  (`PortalWindow.axaml`, GPC / Spectrum / DLS の `MainWindow.axaml`,
  `CalibrationCurveWindow.axaml`, `AnalysisWindow.axaml`, `SolventPresetManagerDialog.axaml`,
  `KeyboardShortcutsWindow.axaml`, `CustomTitleBar.axaml`). `Background="#F7F8FA"` を
  `{DynamicResource MainBgSurfaceBrush}` に、Portal カードの `BorderBrush="#2563EB"` を
  `{DynamicResource AccentBrush}` に置換。ConfirmDialog / Preferences placeholder
  / ComingSoon dialog の動的 Window でも `Application.Current.FindResource` 経由で
  同じトークンを引いて統一感を出す。データの色 (ComboBoxItem の Tag swatch、Color
  picker preset palette) は意図的にリテラルのまま残し、Dark theme 切替時にユーザー
  データの色が変わらないようにしている。
- **`DestructiveButtonStyle` の硬コード色を Error 系トークン参照に**
  (`src/LabPlot.Core.Avalonia/Themes/CommonStyles.axaml`). PR A で追加した時点では
  `#DC2626` / `#B91C1C` / `#991B1B` を直書きしていたが、`ErrorBrush` /
  `ErrorHoverBrush` / `ErrorPressedBrush` / `ErrorForegroundBrush` を引くように
  書き換え、PrimaryButtonStyle と並ぶ 1 段抽象化が完成した。

- **DLS の「全シート選択 / 全解除」を `Ctrl/Cmd + A` に再割当**
  (`src/LabPlot.DLS.Avalonia/MainWindow.axaml.cs`,
  `src/LabPlot.Core.Avalonia/KeyboardShortcutsWindow.axaml.cs`). GPC / Spectrum
  が `Ctrl/Cmd + L` を「重ね描きの切替」に使うのに対し、DLS だけ同キーで「全選択」
  を担う構成だったため、3 モジュール横断のホットキー意味衝突を解消した。F1 ヘルプ
  内の DLS 行も `Ctrl/Cmd + A` 表記に更新済み。
- **DLS の複数選択 ListBox に「選択中アクセント帯」を追加**
  (`src/LabPlot.DLS.Avalonia/MainWindow.axaml`). 旧 UI では Ctrl/Shift クリックで
  追加選択した sheet/run の選択状態が背景色だけで分かりにくく、いま何が plot に
  反映されているか視認しづらかった。`ListBoxItem:selected /template/
  ContentPresenter` に左 3px のアクセント帯と薄い blue 背景を当て、選択切替で
  レイアウトシフトが起きないよう ContentPresenter 側に枠を固定している。

### Fixed

- **ファイル読み込み失敗時に既存グラフ・データセットを温存**
  (`src/LabPlot.GPC.Avalonia/MainWindow.axaml.cs`,
  `src/LabPlot.Spectrum.Avalonia/MainWindow.axaml.cs`). GPC / Spectrum の
  ファイル open catch では旧実装が `_loadedDatasets.Clear()` /
  `_datasetStyles.Clear()` 等を呼び、IOException や parse 失敗のたびに作業中の
  グラフがすべて消えていた。`await Task.WhenAll` は 1 ファイル失敗で全 Task を
  まとめて throw するため partial 書込は発生しない、ということを確認のうえで
  catch を `ShowError` のみに削減し、失敗時もユーザーが直前まで作業していた
  既存グラフ・データセットを保持する挙動に変えた。DLS は読み込み成功後に Clear
  → Add するため既に同挙動で、修正は不要。
- **「履歴をクリア」操作に確認ダイアログを追加** (GPC / Spectrum / DLS の
  `MainWindow.axaml.cs` の `ClearRecentFilesMenuItem_Click`). 旧 UI では履歴
  ComboBox の右クリック 1 発で「最近開いたファイル一覧」だけでなく現在表示中の
  グラフ・データセットも問答無用で消えていた。Core.Avalonia の `ConfirmDialog`
  を挟み、Toast で事後通知する設計から「実行前に Yes/No を取る」設計に揃え、
  GPC / Spectrum / DLS のクリア系操作の confirmation 有無の非対称を解消した。

## [1.3.4] - 2026-05-27

3 モジュール (GPC / Spectrum / DLS) を横断するパフォーマンス改善 sweep。GPC で確立した
3 つのパターン (パーサ data-row allocation 削減、複数ファイル並列読み込み、`Plot.Clear()`
→ plottable pool 管理) を Spectrum / DLS にもそれぞれの構造に合わせて展開し、BenchmarkDotNet
0.14.0 ベースのベンチマーク基盤を 3 モジュール全部に揃えて v1.3.x の baseline を固定した。
あわせて、`PortalWindow` (固定 540 × 620 / `CanResize=False`) が最大化ボタンを経由すると
declared サイズが Bounds に上書きされて「最大化が戻らない」状態に陥る回帰も修正している。

### Added

- **GPC parser benchmark scaffolding** (`src/LabPlot.GPC/GpcAnalyzer.Benchmarks`).
  BenchmarkDotNet 0.14.0-based project that parses synthetic LabSolutions-style
  TXT files (1k / 10k / 50k points) through `CsvGpcDataReader.Read` with
  `MemoryDiagnoser`. Synthetic data is generated in-process; no large fixtures
  are committed. Baseline on Apple M5 / .NET 10.0.8: 96 μs / 1.05 ms / 8.05 ms
  respectively, allocating ~262 KB / 2.6 MB / 12.5 MB per parse. The numbers
  indicate the parser is not the dominant cost for typical multi-dataset
  loads; future tuning should likely focus on the plot-rebuild path
  (`MainWindow.Plot.cs` `Plot.Clear()` → re-add-all) before the parser.
- **Spectrum parser benchmark scaffolding**
  (`src/LabPlot.Spectrum/SpectrumAnalyzer.Benchmarks`). Same BenchmarkDotNet
  0.14.0 / `MemoryDiagnoser` setup as the GPC one. Writes synthetic JASCO
  V-750 UV-Vis exports (2k / 5k / 10k points, full Shift-JIS header + footer
  including `[測定情報]` section) to a temp file per parameter set and times
  `JascoSpectrumReader.Read`. Identifying fields use neutral placeholders.
  Baseline on Apple M5 / .NET 10.0.8: 218 μs / 571 μs / 1.16 ms allocating
  421 KB / 1.13 MB / 2.24 MB per parse.
- **DLS benchmark scaffolding**
  (`src/LabPlot.DLS/DlsAnalyzer.Benchmarks`). Two BenchmarkDotNet 0.14.0 /
  `MemoryDiagnoser` benches: (a) `ZetasizerXlsxReader` against a synthetic
  ClosedXML workbook (1 sheet vs 5 sheets, each carrying Number / Intensity
  / Volume distributions × 3 runs + the g₂-1 correlation block at 150 τ
  samples) and (b) `SizeDistributionInverter` with a synthetic single-peak
  log-Gaussian distribution at 100 nm — both a fixed-α single-NNLS solve
  and the default 16-point auto-α sweep. v1.3.x baseline (Apple M5 /
  .NET 10.0.8): Reader 2.22 ms (1 sheet, 2.00 MB) / 9.52 ms (5 sheets,
  8.20 MB); Inverter 101.5 μs fixed-α (139 KB) / 1.66 ms auto-α (1.19 MB).
  Reader and CONTIN auto-α sit in the same order of magnitude, so neither
  side is the obvious bottleneck.

### Changed

- **GPC parser hot data-row path is now allocation-aware**. The LabSolutions
  chromatogram loop and the loose-CSV / whitespace-delimited paths no longer
  call `SplitLooseColumns` on every data row; a new `TryParseXyRow` helper
  slices each line via `ReadOnlySpan<char>` and parses the two doubles in
  place, dropping the per-row `string[]` + substring allocations. The rare
  whitespace-delimited fallback also stops going through `Regex.Split` and
  uses a manual tokenizer. Same observable behavior, covered by all 26
  existing `GpcAnalyzer.Tests`. Benchmark deltas vs the v1.3.3 baseline
  (Apple M5 / .NET 10.0.8 Release, mean / allocated):
  - 1k points: 96 μs → 83 μs (−14%); 262 KB → 155 KB (−41%)
  - 10k points: 1.05 ms → 0.79 ms (−25%); 2.6 MB → 1.5 MB (−40%)
  - 50k points: 8.05 ms → 6.57 ms (−18%); 12.5 MB → 7.1 MB (−43%)
- **GPC multi-file open is now parallelized**. `MainWindow.ImportCsvFilesAsync`
  previously chained the `_reader.Read` calls sequentially inside a single
  `Task.Run`; it now dispatches one `Task.Run` per file via `Task.WhenAll`,
  so on a multi-core machine N selected files complete in roughly the time
  of the slowest one rather than the sum. Single-file open is unchanged in
  behavior, and the existing `IOException / InvalidDataException /
  ArgumentException` catch still receives the first failure unwrapped
  (await on `Task.WhenAll` surfaces the first inner exception directly).
- **GPC plot refresh tracks Scatter plottables in a pool** instead of
  calling `Plot.Clear()` on every refresh. `MainWindow.Plot.cs` keeps an
  internal `_scatterPool` list of the Scatters added on the most recent
  pass and removes them one-by-one (via `Plot.Remove`) before adding the
  new set. Title / axis ticks / legend orientation are no longer reset by
  the broader `Plot.Clear()`, which keeps cross-refresh state consistent.
  True in-place data swap (skip Add/Remove entirely on dataset-count-stable
  refreshes) is blocked on ScottPlot 5.1.58 not exposing setters on
  `Scatter.Data` or `ScatterSourceDoubleArray.Xs / Ys`; this lands as
  partial progress against ROADMAP §2-GPC and can be extended once the
  ScottPlot public surface allows mutation.
- **Spectrum parser hot data-row path is now allocation-aware**. The
  `JascoSpectrumReader.TryParseDataRow` per-row `string.Split` is replaced
  by a `ReadOnlySpan<char>` walker (`TryFindFirstTwoFields`) that locates
  the first two non-empty tokens without allocating, and `TryParseLooseDouble`
  gets a `ReadOnlySpan<char>` overload that uses a 64-char stackalloc buffer
  for the decimal-comma fallback so European-style "0,5" parses heap-free.
  Same observable behavior, covered by all 167 existing
  `SpectrumAnalyzer.Tests`. Benchmark deltas vs the new v1.3.x baseline
  (Apple M5 / .NET 10.0.8 Release, mean / allocated):
  - 2k points: 218 μs → 197 μs (−10%); 421 KB → 186 KB (−56%)
  - 5k points: 571 μs → 494 μs (−13%); 1.13 MB → 542 KB (−52%)
  - 10k points: 1.16 ms → 1.02 ms (−12%); 2.24 MB → 1.07 MB (−52%)
- **Spectrum multi-file open is now parallelized**. `MainWindow` previously
  ran the per-file `_reader.Read` calls sequentially inside a single
  `Task.Run`; it now dispatches one `Task.Run` per file via `Task.WhenAll`,
  so N selected JASCO TXT/CSV files complete in roughly the time of the
  slowest one rather than the sum. `JascoSpectrumReader` is thread-safe
  (`Encoding.RegisterProvider` + `static readonly ShiftJis` are
  cache-once-then-read). Single-file open is unchanged in behavior.
- **DLS plot refresh tracks plottables in a pool** instead of calling
  `Plot.Clear()` on every refresh. `MainWindow` keeps an internal
  `_plottablePool` list of the Scatter / ScatterPoints / ScatterLine
  instances added on the most recent refresh and removes them one-by-one
  (via `Plot.Remove`) before re-adding the new set; title / axis ticks /
  legend orientation are no longer reset by the broader `Plot.Clear()`.
  Five refresh paths (Number/Intensity/Volume distribution, temperature
  ramp Boltzmann fit, concentration-series linear fit, CONTIN-like size
  inversion, the empty-state placeholder) all share the same pool. DLS
  does not draw overlay Line / Text / Marker plottables, so a single
  `List<IPlottable>` pool covers every Plot.Add site cleanly. Same
  blocker as GPC PR #12: ScottPlot 5.1.58 doesn't expose setters on
  `Scatter.Data`, so this is pool-management progress, not true in-place
  recycling — future work in ROADMAP §2-DLS.

### Fixed

- **Portal window no longer gets stuck "maximized" after a maximize click.**
  `PortalWindow` is `CanResize="False"` with a hard-coded 540 × 620 chrome,
  but the shared `CustomTitleBar` (used by every Avalonia window in the app)
  was still wiring its maximize button and title-bar double-click to
  `WindowState = Maximized`. On macOS the button would happily flip the
  window to maximized, but restoring it back left the declared `Width` /
  `Height` set to the full-screen `Bounds` from the previous state, so the
  portal stayed full-screen even though `WindowState` had returned to
  `Normal`. `CustomTitleBar` now hides the maximize button entirely and
  ignores the double-click maximize gesture when the parent window has
  `CanResize == false`, matching Avalonia's own behavior for the native
  decorations. `WindowStateStore` was the other half of the bug — it wrote
  `window.Bounds` (the full-screen size) as the persisted "normal" width /
  height for the next launch. It now refuses to persist `Maximized=true`
  or to overwrite the declared `Width` / `Height` for fixed-size windows,
  and on the restore side it only re-applies the saved size and the
  `Maximized` flag when `CanResize` is true. Existing
  `window-portal.json` files with stale full-screen dimensions are now
  ignored cleanly instead of forcing a giant portal.

## [1.3.3] - 2026-05-26

macOS UX 細部の詰めと CI 自動化。v1.3.2 で macOS first-class を打ち出した直後の
follow-up として、`Cmd+O` 系の OS 別ショートカット出し分け、ファイルダイアログ既定パスの
`~/Documents` フォールバック、macOS アプリメニュー (About / Preferences / Quit) と
`dotnet run` 経路の Dock アイコンを整え、`v*` タグ push で 3 platform を自動 publish +
GitHub Release 化する Actions ワークフローを導入した。

### Added

- **macOS アプリメニューバー** (`<NativeMenu.Menu>` in `App.axaml`): macOS で「LabPlot ▸
  About LabPlot / Preferences... / Quit LabPlot (Cmd+Q)」が出るようになった。About は
  バージョンとリポジトリ URL を borderless 小ダイアログで表示。Preferences (Cmd+,) は
  「LabPlot は専用の設定 Window を持たず、各モジュールの軸範囲 / グラフ書式パネルに直結」
  旨のプレースホルダ。Quit / Hide / Hide Others / Show All は AppKit が自動でぶら下げる。
- **`dotnet run` 経路の Dock アイコン**: `MacAppIcon.TrySetDockIcon` で `objc_msgSend` を介し
  `NSApp.setApplicationIconImage:` を呼び、avares 上の `app-icon.png` を NSImage 化して
  Dock に渡す。配布 .app バンドル経路 (Info.plist + .icns) は不変。
- **OS 別 command modifier ヘルパ** (`LabPlot.Core.Avalonia.Helpers.KeyboardShortcuts`):
  `HasCommandModifier()` 拡張メソッドで macOS では Cmd (Meta)、それ以外は Ctrl を返す。
  `LocalizeTooltipsForMac` は logical tree を走査して "Ctrl+" tooltip 文字列を Mac だけ
  "Cmd+" に置換する。
- **macOS ファイルダイアログ既定パス**: `FormattingDefaultsStore.GetEffectiveDefaultOutputDirectory`
  でユーザ設定の `DefaultOutputDirectory` が空のとき macOS だけ `~/Documents` を fallback。
  Avalonia の `SuggestedStartLocation` null → 無音 `~` 落ちを回避する。
- **GitHub Actions release workflow** (`.github/workflows/release.yml`): `v*` タグ push で
  3 platform (win-x64 / osx-arm64 / linux-x64) を自動 publish、`CHANGELOG.md` から該当
  バージョンの節を切り出して Release body にし、zip 3 本を添付して GitHub Release を作成。
  `workflow_dispatch` で dry-run も可。
- **`scripts/publish-all-platforms.sh`**: ローカル / CI 両用の 3 platform 一括 publish
  スクリプト。`LABPLOT_VERSION` env 必須 (未指定なら git describe で推定)。

### Changed

- **4 モジュール `OnKeyDown` ハンドラ** (Portal + GPC / Spectrum / DLS): `KeyModifiers.Control`
  直判定 → `HasCommandModifier()` 経由に統一。macOS では Cmd+O / Cmd+S / Cmd+L / Cmd+R /
  Cmd+G / Cmd+1〜4 / Cmd+Shift+O / Cmd+Shift+S が動く。Windows / Linux 挙動は不変。
- **F1 cheat-sheet** (`KeyboardShortcutsWindow`): "Ctrl + O" 等の表記が macOS では "Cmd + O"
  に動的差し替え。
- **csproj `<Version>`**: 4 Avalonia csproj を 1.2.0 → 1.3.3 に bump。`dotnet publish -p:Version=`
  上書きは引き続き有効だが、Dev 起動でも About が正しいバージョンを表示する。

### Misc

- `docs/macOS_開発環境構築.md` §7.4 / §11 を v1.3.3 の対応状況に追従更新。
- ROADMAP の Phase 7 Batch 0〜7e 詳細を 1 行に圧縮。v1.3.3 進捗エントリと整合化。

## [1.3.2] - 2026-05-26

macOS first-class support. The Avalonia portal now ships as a Finder-launchable
`.app` bundle on Apple Silicon, with two long-standing bugs (plot residue
after file deletion / history clear, legend top-row clipping) fixed along the
way, three macOS-specific issues smoked out during real-hardware verification,
and a one-command Developer ID codesign + notarytool pipeline ready for
distribution once a paid Apple Developer account is in hand.

### Added

- **macOS `.app` bundle** is produced automatically by `dotnet publish -r osx-arm64`
  (and `osx-x64`). The Shell.Avalonia csproj has a post-publish target that
  lays out `Contents/{MacOS, Resources, Info.plist}`, embeds the app icon as
  `.icns`, and substitutes the build version into `Info.plist`. Double-click
  from Finder lands on the LabPlot icon in the Dock.
- **One-command codesign + notarytool pipeline** at `scripts/publish-macos.sh`:
  runs `dotnet publish`, deep-codesigns every dylib and the `.app` itself with
  Hardened Runtime, submits to `xcrun notarytool --wait`, staples the ticket,
  and emits `dist/LabPlot-<version>-<rid>.zip`. Credentials are env-driven
  (`APPLE_DEVELOPER_ID` / `APPLE_ID` / `APPLE_TEAM_ID` / `APPLE_APP_PASSWORD`)
  and missing ones fail fast with a clear message.
- **Hardened Runtime entitlements** (`macOS/entitlements.plist`) covering
  `allow-jit`, `allow-unsigned-executable-memory`, and `disable-library-validation`
  so the .NET CoreCLR JIT survives under notarization without being killed
  at launch.
- **Apple Silicon development setup guide** (`docs/macOS_開発環境構築.md`)
  covering SDK install, repo build, smoke-test list, macOS-specific pitfalls
  (Gatekeeper, AppData path, font fallback), and the full codesign + notarytool
  workflow in section 10.

### Changed

- **Plot legend pinned to Arial** with padding scaled to font size, so the
  top-row text is no longer clipped on macOS where Hiragino Sans's tall
  ascender exceeded the previous padding.
- **DLS AnalysisWindow fit-result rows are vertically centered**, fixing a
  baseline offset where the Z-average value sat above its label.

### Fixed

- **Plot residue after a file is removed from the dataset list, the recent-files
  history is cleared, or the source file is deleted on disk.** GPC and Spectrum
  `InitializeEmptyPlot` now clears the plot first, all three modules clear
  the plot on right-click-clear-history, and a new `MissingFileWatcher` fires
  a UI-thread callback when the loaded file disappears from the filesystem
  so the plot resets automatically.
- **DLS AnalysisWindow could not be minimized on macOS** because Avalonia's
  `Show(owner)` maps to `addChildWindow:` on AppKit, which suppresses the
  child's minimize button. The owner attach is now skipped on macOS only;
  Windows / Linux behavior is unchanged.

### Misc

- macOS setup doc `§5.1` / `§5.4` updated to scope `dotnet restore` / `dotnet test`
  to specific csproj files (avoids `NETSDK1100` from the WPF projects in the
  slnx); `§7.2` corrected to the .NET 5+ AppData path
  (`~/Library/Application Support/LabPlot/`).

## [1.3.1] - 2026-05-25

Maintenance follow-up to v1.3.0 focused on usability rather than new
analysis. Adds a temperature-aware DLS solvent preset system, closes
four constantly-felt UX gaps (window state persisted across sessions,
recent-files selection no longer collapses to placeholder, invalid
numeric input now explains itself, recent-files history is clearable
from the UI), and lands a cross-module refactor sweep that removes
~640 lines of GPC duplication without changing any analysis behavior.

### Added

- **DLS solvent preset system**: the metadata "Solvent" field is now an
  AutoCompleteBox carrying nine built-in presets (Water, MeOH, EtOH,
  DMF, DMSO, THF, Toluene, CHCl₃, Acetone) with per-temperature
  refractive-index and viscosity tables at 5 / 15 / 25 / 35 / 45 °C,
  applied via linear interpolation against the current temperature.
  Temperature changes auto-reinterpolate until the user manually
  overrides n or η. User presets are stored under
  `%APPDATA%/LabPlot/dls-solvent-presets.json`, managed through a
  side-by-side dialog (preset list ↔ per-temperature points), and
  saved via an inline `[+]` button.
- **Window size / position / maximized state are persisted per app**
  across the Portal and the three module main windows under
  `%APPDATA%/LabPlot/window-{appKey}.json`. Off-screen positions (e.g.
  after disconnecting a sub-monitor) fall back to `CenterScreen`.
- **Invalid numeric input is surfaced as a toast** in the DLS metadata
  editor on LostFocus / Enter commit, naming the field, the constraint
  (positive / non-negative / numeric), and echoing the offending text.
- **Recent-files history clear** is now reachable from the UI: right-click
  the recent-files ComboBox in any of the three modules.

### Changed

- **Recent-files ComboBox keeps the selected file name visible** after
  a load completes, instead of collapsing back to the placeholder.
- **Tick-label to axis-title gap** scales with font size so large fonts
  no longer crowd the axis title against the tick labels.
- **Plot chrome polish**: softer grid color, restyled legend chrome,
  unified default window width across modules.

### Refactor (internal)

- Extracted `DlsSessionMapper`, `GpcSessionMapper`, `SpectrumSessionMapper`
  so per-dataset save/load lives in one place per module.
- Extracted `DlsMetadataEditor` to own the three-stage TextBox commit
  for DLS metadata with delayed-TextChanged-echo suppression.
- Consolidated `FormatNullableDouble` / `TryParseDouble` / `Clone`
  helpers across modules.
- Split GPC plot rendering into `MainWindow.Plot.cs` partial (−634
  lines from `MainWindow.axaml.cs`).
- Shared `Style TextBox` commit pattern between GPC and Spectrum.
- Extracted `AnalysisSectionView` to dedupe the four DLS analysis
  result panes.

### Fixed

- **Solvent preset auto-fill regression** where typing a name that
  matched a preset only updated viscosity but not refractive index.
- **Solvent preset temperature follow-up** where auto-reinterpolation
  appeared to fire only on the first character of the new temperature.
  Root cause was Avalonia's delayed TextChanged echo after programmatic
  `Text = ` writes; the fix rounds metadata to match displayed text and
  detects the echo by parsed-value equality against current metadata.
- **Solvent preset manager dialog layout** was awkwardly stacked
  vertically and is now laid out as side-by-side panes.

### Misc

- Removed a stray personal-name reference from several source comments
  and TODO markers; attributions now use neutral wording.

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
- **Smoke-test checklist** for DLS and Spectrum
  (originally `docs/Phase7_smoke_test.md`; renamed to
  `docs/release-smoke-test.md` post v1.3.3 as the generic release smoke
  checklist).

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

[1.3.4]: https://github.com/unknowns53/LabPlot/releases/tag/v1.3.4
[1.3.3]: https://github.com/unknowns53/LabPlot/releases/tag/v1.3.3
[1.3.2]: https://github.com/unknowns53/LabPlot/releases/tag/v1.3.2
[1.3.1]: https://github.com/unknowns53/LabPlot/releases/tag/v1.3.1
[1.3.0]: https://github.com/unknowns53/LabPlot/releases/tag/v1.3.0
[1.2.0]: https://github.com/unknowns53/LabPlot/releases/tag/v1.2.0
[1.1.0]: https://github.com/unknowns53/LabPlot/releases/tag/v1.1.0
