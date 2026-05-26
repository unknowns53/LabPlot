# LabPlot

> **利用者向けマニュアルは [`docs/user-guide/`](docs/user-guide/README.md) を参照してください。** 本書はモノレポ全体の構成・ビルド手順・開発者向け情報をまとめたドキュメントです。

研究室向けの測定データ可視化・解析アプリ群をまとめたモノレポです。Shimadzu LabSolutions（GPC）/ JASCO V-750（UV-Vis・FTIR）/ Malvern Zetasizer（DLS）など、ラボの測定装置から出力されたデータを読み込んで、ScottPlot による可視化・書式調整・解析・PNG / SVG / Excel / CSV 書き出しを行います。Windows / macOS / Linux 共通で動作する **Avalonia 実装が主流系統**で、Windows 専用の WPF 実装は v1.1.0 の保守版として並行維持しています。共通の解析基盤は `LabPlot.Core`、UI 基盤は主流の `LabPlot.Core.Avalonia`（保守用に `LabPlot.Core.Wpf`）に集約しています。

## ポータル（単一 exe 配布）

利用者向けには、3 つの解析モジュールを 1 本の exe にまとめた **LabPlot ポータル**で配布します。主流は **Avalonia 版** `src/LabPlot.Shell.Avalonia`（`LabPlot.Avalonia`）で、Windows / macOS / Linux のすべてで同じ系統のバイナリを使えます。Windows で従来から運用してきた WPF 版 `src/LabPlot.Shell`（`LabPlot.exe`）は v1.1.0 の保守用として残してありますが、新機能は Avalonia 版にのみ追加されます。いずれの版もダブルクリックでカード型ランチャーが開き、GPC / UV-Vis / DLS のいずれかをクリックすると解析ウィンドウが立ち上がる構成で、各解析モジュールはクラスライブラリとして組み込まれており、ポータルが唯一の実行可能アプリです。

- 例外ハンドラとログ出力はポータル側に集約。ログパスは Windows: `%LocalAppData%\LabPlot\Logs\shell-error.log`、Linux: `~/.local/share/LabPlot/Logs/shell-error.log`、macOS: `~/Library/Application Support/LabPlot/Logs/shell-error.log`
- 同じモジュールを 2 回開こうとすると既存ウィンドウをアクティブ化（重複起動の抑止）
- 配布手順は後述の「[配布用の単一 exe を作成](#配布用の単一-exe-を作成)」を参照
- WPF 版は v1.1.0 で feature freeze。今後の新機能・バグ修正は主流の Avalonia 版で受ける方針です

## 含まれるアプリ

主流系統（Avalonia、Windows / macOS / Linux 共通）:

- [`src/LabPlot.GPC.Avalonia`](src/LabPlot.GPC.Avalonia/README.md) — GPC（ゲル浸透クロマトグラフィー）データ可視化・分子量分布解析。Shimadzu LabSolutions の TXT エクスポートおよび `Time, Signal` 形式の CSV / TSV に対応
- [`src/LabPlot.Spectrum.Avalonia`](src/LabPlot.Spectrum.Avalonia/README.md) — UV-Vis 波長スキャン / 温度スキャン / FTIR 解析。JASCO V-750 対応、ベースライン補正・ピーク積分・Beer-Lambert 検量線・λmax / Tc 自動抽出・IR ピーク検出を搭載
- [`src/LabPlot.DLS.Avalonia`](src/LabPlot.DLS.Avalonia/README.md) — DLS 粒径分布・自己相関関数解析。Malvern Zetasizer の xlsx エクスポート対応、キュムラント解析と Stokes–Einstein 計算を搭載

保守系統（WPF、Windows 専用）の機能仕様詳細は各 v1.1.0 README ([GPC](src/LabPlot.GPC/README.md) / [Spectrum](src/LabPlot.Spectrum/README.md) / [DLS](src/LabPlot.DLS/README.md)) を参照してください。

## 共有ライブラリ

- [`src/LabPlot.Core`](src/LabPlot.Core/README.md) — 各アプリ共通の解析ロジック（書式設定、エクスポート、セッション保存、ScottPlot セットアップ補助など）。WPF / Avalonia 非依存で主流・保守の双方の UI 層から参照
- [`src/LabPlot.Core.Avalonia`](src/LabPlot.Core.Avalonia/README.md) — **主流系統**の Avalonia 版アプリ共通コンポーネント（`Themes/CommonStyles.axaml` + `Themes/ImplicitStyles.axaml`、AxisRange / ColorPicker / GraphFormat / CustomTitleBar / Banner 群、IStorageProvider 経由のヘルパ、`DragGhostController`）
- [`src/LabPlot.Core.Wpf`](src/LabPlot.Core.Wpf/README.md) — v1.1.0 保守用の WPF 版アプリ共通コンポーネント（`Themes/CommonStyles.xaml`、Core.Avalonia と同形 API の AxisRange / GraphFormat / ColorPicker パネル、ScottPlot ホストヘルパ、書式設定の永続化）

## ロードマップ

今後の機能追加予定や既知の課題は [ROADMAP.md](ROADMAP.md) を参照してください。

## 開発者向け

各アプリは `src/<AppName>/` 配下で個別にビルド・テストできます。詳細は各アプリの `README.md` を参照してください。

### ビルドの前提

- .NET 10 SDK（主流の Avalonia アプリは `net10.0`、保守用の WPF アプリは `net10.0-windows10.0.19041` をターゲット）
- Windows / macOS / Linux いずれの環境でも .NET 10 SDK だけで主流の Avalonia 版をビルド可能（ネイティブの GUI スタックは Avalonia 側が抱えている）。Windows 10 / 11 上ではさらに保守用の WPF 版もビルドできる
- 主流配布は `dotnet publish -r win-x64 / osx-arm64 / linux-x64` で OS 横断に対応

### モノレポ構成と slnx

リポジトリ全体を束ねるトップレベル `LabPlot.slnx` と、アプリごとの `.slnx` の二層構成です。トップレベルを開けば主流の Avalonia 系統 + 保守用の WPF 系統 + 共有ライブラリ（合計 17 プロジェクト）を一括でビルド・テストできます。各アプリ単体の `.slnx` は library 化後も保持していて、そのアプリだけに集中して触りたいときに使えます（ただし主流・保守どちらの系統でも各モジュール csproj は `WinExe` ではなくクラスライブラリとして出力されるため、単体 `dotnet run` はできません）。

| 区分 | slnx | 主流（Avalonia） | 保守（WPF） |
| --- | --- | --- | --- |
| ポータル + 全モジュール | `LabPlot.slnx` | `src/LabPlot.Shell.Avalonia/LabPlot.Shell.Avalonia.csproj`（cross-platform `WinExe`） | `src/LabPlot.Shell/LabPlot.Shell.csproj`（Windows 専用 `WinExe`） |
| GPC | `src/LabPlot.GPC/GPC_Visualization.slnx` | `src/LabPlot.GPC.Avalonia/LabPlot.GPC.Avalonia.csproj`（library） | `src/LabPlot.GPC/GPC_Visualization/GPC_Visualization.csproj`（library） |
| Spectrum | `src/LabPlot.Spectrum/Spectrum_Visualization.slnx` | `src/LabPlot.Spectrum.Avalonia/LabPlot.Spectrum.Avalonia.csproj`（library） | `src/LabPlot.Spectrum/Spectrum_Visualization/Spectrum_Visualization.csproj`（library） |
| DLS | `src/LabPlot.DLS/LabPlot.DLS.slnx` | `src/LabPlot.DLS.Avalonia/LabPlot.DLS.Avalonia.csproj`（library） | `src/LabPlot.DLS/LabPlot.DLS/LabPlot.DLS.csproj`（library） |

### コマンドラインからのビルド・テスト・実行

```powershell
# Build（トップレベル slnx で全 17 プロジェクトを一括ビルド）
dotnet build LabPlot.slnx -c Debug

# Tests（トップレベルから全テストを一括実行。GPC 26 + Spectrum 167 + DLS 179 = 372 件）
dotnet test LabPlot.slnx -c Debug

# 特定モジュールだけテストしたい場合
dotnet test src/LabPlot.GPC/GpcAnalyzer.Tests/GpcAnalyzer.Tests.csproj
dotnet test src/LabPlot.Spectrum/SpectrumAnalyzer.Tests/SpectrumAnalyzer.Tests.csproj
dotnet test src/LabPlot.DLS/DlsAnalyzer.Tests/DlsAnalyzer.Tests.csproj

# Run（主流の Avalonia 版ポータル、Windows / macOS / Linux 共通）
dotnet run --project src/LabPlot.Shell.Avalonia/LabPlot.Shell.Avalonia.csproj

# Run（保守用の WPF 版ポータル、Windows のみ）
dotnet run --project src/LabPlot.Shell/LabPlot.Shell.csproj
```

なお Windows での Avalonia 版開発時に `dotnet build` 後の `dotnet.exe` プロセス（MSBuild worker / Roslyn server）が常駐する場合は、リポジトリ同梱の `tools/run-avalonia.ps1` を使うと nodeReuse 抑止 + exe 直接起動でクリーンに動かせます（`-KillOnly` で残留プロセスの掃除も可）。

Visual Studio で開く場合は、`LabPlot.slnx` を直接開けばテストエクスプローラから全 xUnit を実行できます。アプリ単独に集中したいときはアプリ側の `.slnx` を開けば、そのアプリ＋共有ライブラリだけがロードされます。

### 配布用の単一 exe を作成

#### 主流配布（Avalonia 版、Windows / macOS / Linux 共通）

主流配布は Avalonia 版ポータルを `dotnet publish` で self-contained にします。`-r` でランタイム識別子を指定し、`PublishSingleFile=true` + `SelfContained=true` を渡すのが基本形です。.NET ランタイムが入っていない PC でも、生成された 1 ファイルをダブルクリックするだけで起動できます。

```powershell
# Windows x64 向け
dotnet publish src/LabPlot.Shell.Avalonia/LabPlot.Shell.Avalonia.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true

# macOS Apple Silicon 向け
dotnet publish src/LabPlot.Shell.Avalonia/LabPlot.Shell.Avalonia.csproj -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true

# Linux x64 向け
dotnet publish src/LabPlot.Shell.Avalonia/LabPlot.Shell.Avalonia.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
```

成果物は `src/LabPlot.Shell.Avalonia/bin/Release/net10.0/<rid>/publish/LabPlot.Avalonia(.exe)` に出力されます。GPC / Spectrum / DLS の `samples/` は各 csproj の `CopyToPublishDirectory` 設定で publish フォルダに同梱されるので、`publish/` フォルダごと zip にして配布してください。DLS のサンプル（`demo.xlsx`）は `tools/DlsSampleGenerator` で生成した合成データで、コミット済みのものがそのまま publish に乗ります。動作検証は WSL（Linux x64、Windows 11 標準の WSLg で GUI 表示可能）で実機相当のチェックが取れます。macOS 側の本格的な GUI 検証は実機が必要なので、手元に環境が無い場合は GitHub Actions の `macos-latest` ランナーで起動スモークまでに留めて、対面検証は実機所有者に依頼する運用が現実的です。

#### 保守配布（WPF 版、Windows 専用・v1.1.0 系統）

WPF 版の Windows 配布物は v1.1.0 の保守用として残してあります。新規配布では主流の Avalonia 版を優先してください。WPF 版を必要とする場合は、`src/LabPlot.Shell/Properties/PublishProfiles/win-x64.pubxml` を使った publish プロファイル経由でビルドします。`Release` 構成・`win-x64` ランタイム・`SelfContained=true`・`PublishSingleFile=true` がプロファイル側で固定されているので、コマンド側で `-c` や `-r` を重ねて指定する必要はありません（指定するとプロファイルと矛盾するため非推奨）。

```powershell
dotnet publish src/LabPlot.Shell/LabPlot.Shell.csproj -p:PublishProfile=win-x64
```

成果物は `src/LabPlot.Shell/bin/Release/net10.0-windows10.0.19041/win-x64/publish/LabPlot.exe` に出力されます。`samples/` の同梱挙動は主流配布と同じです。

## デフォルト書式の格納場所

LabPlot のグラフ書式既定値は「ソースコード上の出荷時デフォルト」と「ユーザーごとに永続化される既定値」の二層に分かれています。アプリ起動時はまず後者を読み、ファイルが無ければ前者で初期化します。「リセット」ボタンはここで保持している保存済み既定値（無ければ出荷時デフォルト）にコントロールを戻すためのスナップショットです。

### 1. 出荷時デフォルト（ソース上の定数）

新規インストール直後や `formatting_config.json` を削除した直後はこの値が効きます。

- 全アプリ共通: [`src/LabPlot.Core/GraphFormattingConfigBase.cs`](src/LabPlot.Core/GraphFormattingConfigBase.cs)
  - フォントサイズ (16 pt)・線幅 (1.5 px)・マーカーサイズ (0)・枠線色 `#475569`・背景色 `#FFFFFF`・凡例位置 `UpperRight`・目盛密度 0.5x など、すべてのアプリ共通の値
- GPC: [`src/LabPlot.GPC/GpcAnalyzer.Core/GraphFormattingConfig.cs`](src/LabPlot.GPC/GpcAnalyzer.Core/GraphFormattingConfig.cs) — 共通項目に加えて、最後に読み込んだ検量線ファイルパスを保持
- Spectrum: [`src/LabPlot.Spectrum/SpectrumAnalyzer.Core/GraphFormattingConfig.cs`](src/LabPlot.Spectrum/SpectrumAnalyzer.Core/GraphFormattingConfig.cs) — X 軸方向 / Y 軸 A・T モード・λmax / 雲点 / IR ピーク検出・Beer-Lambert 検量線・積分領域 等
- DLS: [`src/LabPlot.DLS/DlsAnalyzer.Core/GraphFormattingConfig.cs`](src/LabPlot.DLS/DlsAnalyzer.Core/GraphFormattingConfig.cs) — X / Y 軸範囲モード、初期分布種 (`Number`)、初期 Run インデックス 等

サブクラスはいずれも `GraphFormattingConfigBase.Normalize()` を呼び戻して値域チェックをおこない、範囲外の値は既定値へスナップされます。

### 2. ユーザー保存の永続化先

アプリ起動中に「既定値として保存」ボタンを押すと、共通ヘルパ [`FormattingDefaultsStore`](src/LabPlot.Core.Avalonia/Helpers/FormattingDefaultsStore.cs)（保守用 WPF 系統では [`LabPlot.Core.Wpf` 側の同シグネチャ実装](src/LabPlot.Core.Wpf/Helpers/FormattingDefaultsStore.cs)）を介して JSON ファイルが以下のパスに書き出されます。

| アプリ | 保存先 |
| --- | --- |
| GPC | `%AppData%\GPC_Visualization\formatting_config.json` |
| Spectrum | `%AppData%\Spectrum_Visualization\formatting_config.json` |
| DLS | `%AppData%\LabPlot.DLS\formatting_config.json` |

Windows では `%AppData%` は `C:\Users\<ユーザー名>\AppData\Roaming` を指します。出力フォルダや GPC の検量線パスといった環境設定はセッションファイル (`.json` セッション) には含めず、この `formatting_config.json` 側だけに保存される設計です — セッションを別 PC や別ユーザーに渡しても、相手側の環境設定を上書きしないためです。

## デフォルト軸ラベル / タイトルの格納場所

各アプリのプロットタイトル・X / Y 軸ラベルは「ユーザーが書式パネルの `XLabelTextBox` / `YLabelTextBox` / `TitleTextBox` に文字を入力していればそれを優先、未入力ならアプリ側のフォールバック」という共通構造になっています。フォールバック側の文字列は `DefaultLabels` という静的クラスに集約してあるので、リリース前の文言調整はこのファイルだけ書き換えれば全箇所に反映されます。

- GPC: [`src/LabPlot.GPC/GpcAnalyzer.Core/DefaultLabels.cs`](src/LabPlot.GPC/GpcAnalyzer.Core/DefaultLabels.cs) — データ未ロード時のプレースホルダー、`SourceFilePath` 取得失敗時の Title フォールバック、分子量分布（log scale）モードで X 軸に被せる `"{0} (log scale)"` 装飾フォーマット、`GpcDataset` / `GpcDetectorDataset` の `XLabel` / `YLabel` 既定値、`MolecularWeightDataset` の `"Molecular Weight [Da]"` / `"Signal"` 既定値
- Spectrum: [`src/LabPlot.Spectrum/SpectrumAnalyzer.Core/DefaultLabels.cs`](src/LabPlot.Spectrum/SpectrumAnalyzer.Core/DefaultLabels.cs) — データ未ロード時のプレースホルダー、Title フォールバック、`SpectrumDataset` の `XLabel` / `YLabel` 既定値、A↔T 表示切替時に効く `"Absorbance"` / `"Transmittance / %"` の Y ラベル。なお JASCO ファイル由来の単位文字列（"NANOMETERS" → "Wavelength (nm)" など）は `JascoSpectrumReader` 内の `AxisLabelMapper` が装置情報から導く値なので、こちらには集約していません
- DLS: [`src/LabPlot.DLS/LabPlot.DLS/DefaultLabels.cs`](src/LabPlot.DLS/LabPlot.DLS/DefaultLabels.cs) — `DistributionMode`（`Number` / `Intensity` / `Volume` / `Correlation`）ごとのタイトル・X 軸・Y 軸ラベルを `GetPlotTypeLabel` / `GetDefaultXLabel` / `GetModeLabel` の 3 メソッドで返す形にしてあります。データセットが当該モードを持たないときに表示するタイトル末尾の `" データなし"` サフィックスもここ。DLS だけは `DistributionMode` が `LabPlot.DLS` アセンブリ側の `internal enum` なので、`DefaultLabels` も Core ではなく同アセンブリ直下に置いています

`AnalysisSessionLabels`（[`src/LabPlot.Core/AnalysisSessionLabels.cs`](src/LabPlot.Core/AnalysisSessionLabels.cs)）に保持されるのはユーザーが TextBox に入力した上書き文字列だけで、`DefaultLabels` の値はセッションに含めません。フォールバック文字列はあくまでアプリ側の出荷時値として扱う設計です。

## ライセンス

[MIT License](LICENSE)
