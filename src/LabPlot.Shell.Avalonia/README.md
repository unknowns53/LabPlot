# LabPlot.Shell.Avalonia

> **利用者向け操作手順は [`docs/user-guide/portal.md`](../../docs/user-guide/portal.md) を参照してください。** 本書は開発者向けです。

LabPlot の **主流系統** ポータルアプリです。3 つの解析モジュール（GPC / Spectrum / DLS）を 1 本の self-contained 実行ファイル `LabPlot.Avalonia` にまとめ、Windows / macOS / Linux 共通のカード型ランチャー画面から起動できます。

> 保守用の WPF 系統には [`src/LabPlot.Shell`](../LabPlot.Shell/)（`LabPlot.exe`、Windows 専用）があり、新機能・バグ修正は本プロジェクトを優先して受けます。

---

## 1. ポータルの動作

`LabPlot.Avalonia(.exe)` をダブルクリックすると 540×620 のカード型ランチャー（2×2 UniformGrid）が開きます。GPC / UV-Vis / DLS のいずれかをクリックするとその解析ウィンドウが立ち上がります。各解析モジュールはクラスライブラリとして組み込まれているので、ポータルが唯一の実行可能アプリです。

- 同じモジュールを 2 回開こうとすると既存ウィンドウをアクティブ化（`OpenSingleton<TWindow>` / `TryActivateExistingWindow<TWindow>`）
- Portal を × で閉じると子ウィンドウもまとめて閉じる（`ShutdownMode = OnMainWindowClose`）
- WindowChrome は `ExtendClientAreaToDecorationsHint=True + ExtendClientAreaChromeHints=NoChrome` で外し、`Controls/CustomTitleBar` で自前再現

例外ハンドラとログ出力はポータル側に集約しています:

| OS | ログパス |
| --- | --- |
| Windows | `%LocalAppData%\LabPlot\Logs\shell-error.log` |
| Linux | `~/.local/share/LabPlot/Logs/shell-error.log` |
| macOS | `~/Library/Application Support/LabPlot/Logs/shell-error.log` |

ハンドラは `Dispatcher.UIThread.UnhandledException` + `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException` の 3 経路を購読しています。

---

## 2. 開発者向け

### ビルドと起動

```powershell
dotnet build src/LabPlot.Shell.Avalonia/LabPlot.Shell.Avalonia.csproj -c Debug
dotnet run --project src/LabPlot.Shell.Avalonia/LabPlot.Shell.Avalonia.csproj
```

### `tools/run-avalonia.ps1` ヘルパ

リポジトリ直下の `tools/run-avalonia.ps1` は、`dotnet run` ではなく build 済みの exe を直接起動するためのヘルパです。dotnet 子プロセスツリーが消えずに残る orphan 問題と、`dotnet build` が立ち上げる MSBuild worker / Roslyn server の常駐（dotnet 亡霊プロセス）を防ぐ運用フラグを入れてあります。

```powershell
# Build Debug + launch
.\tools\run-avalonia.ps1

# Skip build, launch existing exe
.\tools\run-avalonia.ps1 -NoBuild

# Stop existing LabPlot.Avalonia processes and shut down dotnet build server
.\tools\run-avalonia.ps1 -KillOnly
```

ビルド時に `-nodeReuse:false /p:UseSharedCompilation=false` が常に付くので、ビルド完了時の `dotnet.exe` プロセス数が積み上がりません（commit `cd31ddb`）。

### デバッグ実行

`LabPlot.slnx`（リポジトリ直下）を Visual Studio で開き、`LabPlot.Shell.Avalonia` をスタートアップに指定すると、ポータル経由で各アプリを起動しながらブレークポイントが効きます。

---

## 3. 配布用の単一ファイルを作成

主流配布は self-contained single-file で OS 別に publish します:

```powershell
# Windows x64
dotnet publish src/LabPlot.Shell.Avalonia/LabPlot.Shell.Avalonia.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true

# macOS Apple Silicon
dotnet publish src/LabPlot.Shell.Avalonia/LabPlot.Shell.Avalonia.csproj -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true

# Linux x64
dotnet publish src/LabPlot.Shell.Avalonia/LabPlot.Shell.Avalonia.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
```

成果物は `bin/Release/net10.0/<rid>/publish/LabPlot.Avalonia(.exe)` に出力されます。GPC / Spectrum / DLS の `samples/` は ProjectReference 経由でこの publish フォルダに同梱されるので、`publish/` フォルダごと zip にして配布してください。DLS の合成データ（`demo.xlsx`）は `tools/DlsSampleGenerator` で生成され `src/LabPlot.DLS/samples/` にコミット済みなので、`dotnet publish` から見ると他モジュールと同じパターンで取り込まれます。

動作検証:

- **Linux**: WSL2 + WSLg（Windows 11 標準）で実機相当の検証が取れます。日本語フォントは `apt install fonts-noto-cjk` を推奨。
- **macOS**: 手元に実機が無い場合は GitHub Actions の `macos-latest` ランナーで起動スモークまでに留め、Gatekeeper / コードサイン / ファイルダイアログ実挙動 / フォント / .app バンドル化は実機所有者に依頼。

---

## 4. プロジェクト構成

```text
App.axaml(.cs)              FluentTheme + Core.Avalonia の CommonStyles/ImplicitStyles を merge
Program.cs                  AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont()
                            .StartWithClassicDesktopLifetime
PortalWindow.axaml(.cs)     2×2 UniformGrid のカード型ランチャー
WindowSingletonHelper.cs    OpenSingleton<TWindow> / TryActivateExistingWindow<TWindow>
ErrorLogWriter.cs           OS 別ログパスへの例外ログ書き出し
```

依存:

- `LabPlot.Core` / `LabPlot.Core.Avalonia` — 共通解析・UI 基盤
- `LabPlot.GPC.Avalonia` / `LabPlot.Spectrum.Avalonia` / `LabPlot.DLS.Avalonia` — 各解析モジュール（library）
- `Avalonia 11.3.14` / `Avalonia.Themes.Fluent 11.3.14` / `Avalonia.Desktop 11.3.14`

ターゲット: `net10.0`、`OutputType=WinExe`、`AssemblyName=LabPlot.Avalonia`（Windows / macOS / Linux 共通バイナリ）。
