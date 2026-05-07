# LabPlot

研究室向けの測定データ可視化・解析アプリ群をまとめたモノレポです。Shimadzu LabSolutions（GPC）/ JASCO V-750（UV-Vis・FTIR）/ Malvern Zetasizer（DLS）など、ラボの測定装置から出力されたデータを読み込んで、ScottPlot による可視化・書式調整・解析・PNG / SVG / Excel / CSV 書き出しを行います。Windows 向けの WPF 実装と、macOS / Linux にも展開できる Avalonia 実装を並行して提供しています。共通の解析・UI 基盤は `LabPlot.Core` / `LabPlot.Core.Wpf` / `LabPlot.Core.Avalonia` に集約しています。

## ポータル（単一 exe 配布）

非エンジニア向けには、3 つの解析モジュールを 1 本の exe にまとめた **LabPlot ポータル**で配布します。Windows 向けには WPF 版 `src/LabPlot.Shell`（`LabPlot.exe`）、macOS / Linux にも展開する場合は Avalonia 版 `src/LabPlot.Shell.Avalonia`（`LabPlot.Avalonia`）を使います。いずれもダブルクリックするとカード型のランチャー画面が開き、GPC / UV-Vis / DLS のいずれかをクリックするとその解析ウィンドウが立ち上がる構成で、各解析モジュールはクラスライブラリとして組み込まれており、ポータルが唯一の実行可能アプリです。

- 例外ハンドラとログ出力はポータル側に集約。ログパスは Windows: `%LocalAppData%\LabPlot\Logs\shell-error.log`、Linux: `~/.local/share/LabPlot/Logs/shell-error.log`、macOS: `~/Library/Application Support/LabPlot/Logs/shell-error.log`
- 同じモジュールを 2 回開こうとすると既存ウィンドウをアクティブ化（重複起動の抑止）
- 配布手順は後述の「[配布用の単一 exe を作成](#配布用の単一-exe-を作成)」を参照
- WPF 版は v1.0.x で feature freeze し、新機能は Avalonia 版で受ける方針です（並行ビルド）

## 含まれるアプリ

- [`src/LabPlot.GPC`](src/LabPlot.GPC/README.md) — GPC（ゲル浸透クロマトグラフィー）データ可視化・分子量分布解析。Shimadzu LabSolutions の TXT エクスポートおよび `Time, Signal` 形式の CSV / TSV に対応
- [`src/LabPlot.Spectrum`](src/LabPlot.Spectrum/README.md) — UV-Vis 波長スキャン / 温度スキャン / FTIR 解析。JASCO V-750 対応、ベースライン補正・ピーク積分・Beer-Lambert 検量線・λmax / Tc 自動抽出・IR ピーク検出を搭載
- [`src/LabPlot.DLS`](src/LabPlot.DLS/README.md) — DLS 粒径分布・自己相関関数解析。Malvern Zetasizer の xlsx エクスポート対応、キュムラント解析と Stokes–Einstein 計算を搭載

## 共有ライブラリ

- [`src/LabPlot.Core`](src/LabPlot.Core/README.md) — 各アプリ共通の解析ロジック（書式設定、エクスポート、セッション保存、ScottPlot セットアップ補助など）。WPF / Avalonia 非依存で双方の UI 層から参照
- [`src/LabPlot.Core.Wpf`](src/LabPlot.Core.Wpf/README.md) — WPF 版アプリ共通のコンポーネント（`Themes/CommonStyles.xaml`、AxisRange / GraphFormat / ColorPicker パネル、ScottPlot ホストヘルパ、書式設定の永続化）
- `src/LabPlot.Core.Avalonia` — Avalonia 版アプリ共通のコンポーネント（`Themes/CommonStyles.axaml` + `Themes/ImplicitStyles.axaml`、Core.Wpf と同形 API の AxisRange / ColorPicker / GraphFormat / CustomTitleBar / Banner 群、IStorageProvider 経由のヘルパ）

## ロードマップ

今後の機能追加予定や既知の課題は [ROADMAP.md](ROADMAP.md) を参照してください。

## 開発者向け

各アプリは `src/<AppName>/` 配下で個別にビルド・テストできます。詳細は各アプリの `README.md` を参照してください。

### ビルドの前提

- .NET 10 SDK（WPF アプリは `net10.0-windows10.0.19041`、Avalonia アプリは `net10.0` をターゲット）
- Windows 10 / 11 上で WPF 版 / Avalonia 版どちらもビルド可能。Avalonia 版は macOS / Linux にも `dotnet publish -r osx-arm64 / linux-x64` で配布できる
- Linux / macOS 上で Avalonia 版を直接ビルドする場合は対応する .NET 10 SDK のみで十分（ネイティブの GUI スタックは Avalonia 側が抱えている）

### モノレポ構成と slnx

リポジトリ全体を束ねるトップレベル `LabPlot.slnx` と、アプリごとの `.slnx` の二層構成です。トップレベルを開けば WPF / Avalonia 両系統のポータル + 3 解析モジュール + 共有ライブラリ（合計 15 プロジェクト）を一括でビルド・テストできます。各アプリ単体の `.slnx` は library 化後も保持していて、そのアプリだけに集中して触りたいときに使えます（ただし WPF / Avalonia とも各モジュール csproj は `WinExe` ではなくクラスライブラリとして出力されるため、単体 `dotnet run` はできません）。

| 区分 | slnx | 主なプロジェクト |
| --- | --- | --- |
| ポータル + 全モジュール | `LabPlot.slnx` | `src/LabPlot.Shell/LabPlot.Shell.csproj`（WPF、`WinExe`）/ `src/LabPlot.Shell.Avalonia/LabPlot.Shell.Avalonia.csproj`（Avalonia、cross-platform `WinExe`） |
| GPC | `src/LabPlot.GPC/GPC_Visualization.slnx` | `src/LabPlot.GPC/GPC_Visualization/GPC_Visualization.csproj`（WPF library）+ `src/LabPlot.GPC.Avalonia/LabPlot.GPC.Avalonia.csproj`（Avalonia library） |
| Spectrum | `src/LabPlot.Spectrum/Spectrum_Visualization.slnx` | `src/LabPlot.Spectrum/Spectrum_Visualization/Spectrum_Visualization.csproj`（WPF library）+ `src/LabPlot.Spectrum.Avalonia/LabPlot.Spectrum.Avalonia.csproj`（Avalonia library） |
| DLS | `src/LabPlot.DLS/LabPlot.DLS.slnx` | `src/LabPlot.DLS/LabPlot.DLS/LabPlot.DLS.csproj`（WPF library）+ `src/LabPlot.DLS.Avalonia/LabPlot.DLS.Avalonia.csproj`（Avalonia library） |

### コマンドラインからのビルド・テスト・実行

```powershell
# Build（トップレベル slnx で 15 プロジェクトを一括ビルド）
dotnet build LabPlot.slnx -c Debug

# Tests（トップレベルから全テストを一括実行。GPC 23 + Spectrum 160 + DLS 141 = 324 件）
dotnet test LabPlot.slnx -c Debug

# 特定モジュールだけテストしたい場合
dotnet test src/LabPlot.GPC/GpcAnalyzer.Tests/GpcAnalyzer.Tests.csproj
dotnet test src/LabPlot.Spectrum/SpectrumAnalyzer.Tests/SpectrumAnalyzer.Tests.csproj
dotnet test src/LabPlot.DLS/DlsAnalyzer.Tests/DlsAnalyzer.Tests.csproj

# Run（WPF 版ポータルを起動）
dotnet run --project src/LabPlot.Shell/LabPlot.Shell.csproj

# Run（Avalonia 版ポータルを起動。Windows / macOS / Linux 共通）
dotnet run --project src/LabPlot.Shell.Avalonia/LabPlot.Shell.Avalonia.csproj
```

Visual Studio で開く場合は、`LabPlot.slnx` を直接開けばテストエクスプローラから全 xUnit を実行できます。アプリ単独に集中したいときはアプリ側の `.slnx` を開けば、そのアプリ＋共有ライブラリだけがロードされます。

### 配布用の単一 exe を作成

#### Windows 向け（WPF 版）

非エンジニア向けの Windows 配布物は、`src/LabPlot.Shell/Properties/PublishProfiles/win-x64.pubxml` を使った publish プロファイル経由でビルドします。`Release` 構成・`win-x64` ランタイム・`SelfContained=true`・`PublishSingleFile=true` がプロファイル側で固定されているので、コマンド側で `-c` や `-r` を重ねて指定する必要はありません（指定するとプロファイルと矛盾するため非推奨）。.NET ランタイムが入っていない PC でも、生成された exe をダブルクリックするだけで起動できます。

```powershell
dotnet publish src/LabPlot.Shell/LabPlot.Shell.csproj -p:PublishProfile=win-x64
```

成果物は `src/LabPlot.Shell/bin/Release/net10.0-windows10.0.19041/win-x64/publish/LabPlot.exe` に出力されます。GPC / Spectrum の `samples/` は各 csproj の `CopyToPublishDirectory` 設定で publish フォルダに同梱されるので、`publish/` フォルダごと zip にして配布してください。DLS は `samples/` を持たないため、追加同梱物はありません。

#### macOS / Linux 向け（Avalonia 版）

cross-platform 配布物は Avalonia 版ポータルを `dotnet publish` で self-contained にします。`-r` でランタイム識別子を指定し、`PublishSingleFile=true` + `SelfContained=true` を渡すのが基本形です。

```powershell
# Windows 上から macOS Apple Silicon 向け
dotnet publish src/LabPlot.Shell.Avalonia/LabPlot.Shell.Avalonia.csproj -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true

# Windows 上から Linux x64 向け
dotnet publish src/LabPlot.Shell.Avalonia/LabPlot.Shell.Avalonia.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
```

成果物は `src/LabPlot.Shell.Avalonia/bin/Release/net10.0/<rid>/publish/LabPlot.Avalonia` に出力されます。GPC / Spectrum の `samples/` は WPF 版と同様 `CopyToPublishDirectory` で publish フォルダに同梱されます。動作検証は WSL（Linux x64、Windows 11 標準の WSLg で GUI 表示可能）で実機相当のチェックが取れます。macOS 側の本格的な GUI 検証は実機が必要なので、手元に環境が無い場合は GitHub Actions の `macos-latest` ランナーで起動スモークまでに留めて、対面検証は実機所有者に依頼する運用が現実的です。

## デフォルト書式の格納場所

LabPlot のグラフ書式既定値は「ソースコード上の出荷時デフォルト」と「ユーザーごとに永続化される既定値」の二層に分かれています。アプリ起動時はまず後者を読み、ファイルが無ければ前者で初期化します。「リセット」ボタンはここで保持している保存済み既定値（無ければ出荷時デフォルト）にコントロールを戻すためのスナップショットです。

### 1. 出荷時デフォルト（ソース上の定数）

新規インストール直後や `formatting_config.json` を削除した直後はこの値が効きます。

- 全アプリ共通: [`src/LabPlot.Core/GraphFormattingConfigBase.cs`](src/LabPlot.Core/GraphFormattingConfigBase.cs)
  - フォントサイズ (12 pt)・線幅 (1.5 px)・マーカーサイズ (0)・枠線色 `#475569`・背景色 `#FFFFFF`・凡例位置 `UpperRight`・目盛密度 0.5x など、すべてのアプリ共通の値
- GPC: [`src/LabPlot.GPC/GpcAnalyzer.Core/GraphFormattingConfig.cs`](src/LabPlot.GPC/GpcAnalyzer.Core/GraphFormattingConfig.cs) — 共通項目に加えて、最後に読み込んだ検量線ファイルパスを保持
- Spectrum: [`src/LabPlot.Spectrum/SpectrumAnalyzer.Core/GraphFormattingConfig.cs`](src/LabPlot.Spectrum/SpectrumAnalyzer.Core/GraphFormattingConfig.cs) — X 軸方向 / Y 軸 A・T モード・λmax / 雲点 / IR ピーク検出・Beer-Lambert 検量線・積分領域 等
- DLS: [`src/LabPlot.DLS/DlsAnalyzer.Core/GraphFormattingConfig.cs`](src/LabPlot.DLS/DlsAnalyzer.Core/GraphFormattingConfig.cs) — X / Y 軸範囲モード、初期分布種 (`Number`)、初期 Run インデックス 等

サブクラスはいずれも `GraphFormattingConfigBase.Normalize()` を呼び戻して値域チェックをおこない、範囲外の値は既定値へスナップされます。

### 2. ユーザー保存の永続化先

アプリ起動中に「既定値として保存」ボタンを押すと、共通ヘルパ [`FormattingDefaultsStore`](src/LabPlot.Core.Wpf/Helpers/FormattingDefaultsStore.cs) を介して JSON ファイルが以下のパスに書き出されます。

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
