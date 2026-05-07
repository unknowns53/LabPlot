# LabPlot

研究室向けの測定データ可視化・解析アプリ群をまとめたモノレポです。Shimadzu LabSolutions（GPC）/ JASCO V-750（UV-Vis・FTIR）/ Malvern Zetasizer（DLS）など、ラボの測定装置から出力されたデータを WPF アプリで読み込んで、ScottPlot による可視化・書式調整・解析・PNG / SVG / Excel / CSV 書き出しを行います。共通の解析・UI 基盤は `LabPlot.Core` / `LabPlot.Core.Wpf` に集約しています。

## 含まれるアプリ

- [`src/LabPlot.GPC`](src/LabPlot.GPC/README.md) — GPC（ゲル浸透クロマトグラフィー）データ可視化・分子量分布解析。Shimadzu LabSolutions の TXT エクスポートおよび `Time, Signal` 形式の CSV / TSV に対応
- [`src/LabPlot.Spectrum`](src/LabPlot.Spectrum/README.md) — UV-Vis 波長スキャン / 温度スキャン / FTIR 解析。JASCO V-750 対応、ベースライン補正・ピーク積分・Beer-Lambert 検量線・λmax / Tc 自動抽出・IR ピーク検出を搭載
- [`src/LabPlot.DLS`](src/LabPlot.DLS/README.md) — DLS 粒径分布・自己相関関数解析。Malvern Zetasizer の xlsx エクスポート対応、キュムラント解析と Stokes–Einstein 計算を搭載

## 共有ライブラリ

- [`src/LabPlot.Core`](src/LabPlot.Core/README.md) — 各アプリ共通の解析ロジック（書式設定、エクスポート、セッション保存、ScottPlot セットアップ補助など）。WPF 非依存
- [`src/LabPlot.Core.Wpf`](src/LabPlot.Core.Wpf/README.md) — 各アプリ共通の WPF コンポーネント（`Themes/CommonStyles.xaml`、AxisRange / GraphFormat / ColorPicker パネル、ScottPlot ホストヘルパ、書式設定の永続化）

## ロードマップ

今後の機能追加予定や既知の課題は [ROADMAP.md](ROADMAP.md) を参照してください。

## 開発者向け

各アプリは `src/<AppName>/` 配下で個別にビルド・テストできます。詳細は各アプリの `README.md` を参照してください。

### ビルドの前提

- .NET 10 SDK（各 WPF アプリは `net10.0-windows10.0.19041` をターゲットにしています）
- Windows 10 / 11 上での実行を想定（WPF + ScottPlot ベースのため、Linux / macOS では実行不可）

### モノレポ構成と slnx

リポジトリ全体を束ねる slnx は持たず、アプリごとに 3 つの slnx を分けています。任意の slnx 単体を開けば、そのアプリ＋共有 Core ライブラリだけをビルド・デバッグできます。

| アプリ | slnx | 実行プロジェクト |
| --- | --- | --- |
| GPC | `src/LabPlot.GPC/GPC_Visualization.slnx` | `src/LabPlot.GPC/GPC_Visualization/GPC_Visualization.csproj` |
| Spectrum | `src/LabPlot.Spectrum/Spectrum_Visualization.slnx` | `src/LabPlot.Spectrum/Spectrum_Visualization/Spectrum_Visualization.csproj` |
| DLS | `src/LabPlot.DLS/LabPlot.DLS.slnx` | `src/LabPlot.DLS/LabPlot.DLS/LabPlot.DLS.csproj` |

### コマンドラインからのビルド・テスト・実行

```powershell
# Build（GPC の例。他アプリも slnx を差し替えるだけ）
dotnet build src/LabPlot.GPC/GPC_Visualization.slnx -c Debug

# Tests（各アプリの xUnit テスト）
dotnet test src/LabPlot.GPC/GpcAnalyzer.Tests/GpcAnalyzer.Tests.csproj
dotnet test src/LabPlot.Spectrum/SpectrumAnalyzer.Tests/SpectrumAnalyzer.Tests.csproj
dotnet test src/LabPlot.DLS/DlsAnalyzer.Tests/DlsAnalyzer.Tests.csproj

# Run（dotnet run でアプリを起動）
dotnet run --project src/LabPlot.GPC/GPC_Visualization/GPC_Visualization.csproj
dotnet run --project src/LabPlot.Spectrum/Spectrum_Visualization/Spectrum_Visualization.csproj
dotnet run --project src/LabPlot.DLS/LabPlot.DLS/LabPlot.DLS.csproj
```

Visual Studio で開く場合は、対応する `.slnx` を直接開けばテストエクスプローラから xUnit を実行できます。

### 配布用の単一 exe を作成

非エンジニア向けに配布するときは、各アプリの `Properties/PublishProfiles/win-x64.pubxml` を使った publish プロファイル経由でビルドします。`Release` 構成・`win-x64` ランタイム・`SelfContained=true`・`PublishSingleFile=true` がプロファイル側で固定されているので、コマンド側で `-c` や `-r` を重ねて指定する必要はありません（指定するとプロファイルと矛盾するため非推奨）。.NET ランタイムが入っていない PC でも、生成された exe をダブルクリックするだけで起動できます。

```powershell
# GPC
dotnet publish src/LabPlot.GPC/GPC_Visualization/GPC_Visualization.csproj -p:PublishProfile=win-x64

# Spectrum
dotnet publish src/LabPlot.Spectrum/Spectrum_Visualization/Spectrum_Visualization.csproj -p:PublishProfile=win-x64

# DLS
dotnet publish src/LabPlot.DLS/LabPlot.DLS/LabPlot.DLS.csproj -p:PublishProfile=win-x64
```

成果物の出力先は各アプリの `<実行プロジェクト>/bin/Release/net10.0-windows10.0.19041/win-x64/publish/` 以下です。GPC / Spectrum はこのフォルダに `samples/` も同梱されるので、フォルダごと zip にして配布してください。DLS は `LabPlot.DLS.exe` 単体を配布するだけで動作します。

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
