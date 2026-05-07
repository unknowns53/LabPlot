# LabPlot

研究室向けの測定データ可視化・解析アプリ群をまとめたモノレポです。Shimadzu LabSolutions（GPC）/ JASCO V-750（UV-Vis・FTIR）/ Malvern Zetasizer（DLS）など、ラボの測定装置から出力されたデータを WPF アプリで読み込んで、ScottPlot による可視化・書式調整・解析・PNG / SVG / Excel / CSV 書き出しを行います。共通の解析・UI 基盤は `LabPlot.Core` / `LabPlot.Core.Wpf` に集約しています。

## ポータル（単一 exe 配布）

非エンジニア向けには、3 つの解析モジュールを 1 本の exe にまとめた **LabPlot ポータル** (`src/LabPlot.Shell`) で配布します。`LabPlot.exe` をダブルクリックするとカード型のランチャー画面が開き、GPC / UV-Vis / DLS のいずれかをクリックするとその解析ウィンドウが立ち上がる構成です。各解析モジュールはクラスライブラリとして組み込まれており、ポータルが唯一の `WinExe` です。

- 例外ハンドラとログ出力（`%LocalAppData%\LabPlot\Logs\shell-error.log`）はポータル側に集約
- 同じモジュールを 2 回開こうとすると既存ウィンドウをアクティブ化（重複起動の抑止）
- 配布手順は後述の「[配布用の単一 exe を作成](#配布用の単一-exe-を作成)」を参照

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

リポジトリ全体を束ねるトップレベル `LabPlot.slnx` と、アプリごとの `.slnx` の二層構成です。トップレベルを開けばポータル + 3 解析モジュール + 共有ライブラリの 12 プロジェクト全部を一括でビルド・テストできます。各アプリ単体の `.slnx` は library 化後も保持していて、そのアプリだけに集中して触りたいときに使えます（ただし WPF アプリ csproj は `WinExe` ではなくクラスライブラリとして出力されるため、単体 `dotnet run` はできません）。

| 区分 | slnx | 主なプロジェクト |
| --- | --- | --- |
| ポータル + 全モジュール | `LabPlot.slnx` | `src/LabPlot.Shell/LabPlot.Shell.csproj`（唯一の `WinExe`） |
| GPC | `src/LabPlot.GPC/GPC_Visualization.slnx` | `src/LabPlot.GPC/GPC_Visualization/GPC_Visualization.csproj`（library） |
| Spectrum | `src/LabPlot.Spectrum/Spectrum_Visualization.slnx` | `src/LabPlot.Spectrum/Spectrum_Visualization/Spectrum_Visualization.csproj`（library） |
| DLS | `src/LabPlot.DLS/LabPlot.DLS.slnx` | `src/LabPlot.DLS/LabPlot.DLS/LabPlot.DLS.csproj`（library） |

### コマンドラインからのビルド・テスト・実行

```powershell
# Build（トップレベル slnx で 12 プロジェクトを一括ビルド）
dotnet build LabPlot.slnx -c Debug

# Tests（トップレベルから全テストを一括実行）
dotnet test LabPlot.slnx -c Debug

# 特定モジュールだけテストしたい場合
dotnet test src/LabPlot.GPC/GpcAnalyzer.Tests/GpcAnalyzer.Tests.csproj
dotnet test src/LabPlot.Spectrum/SpectrumAnalyzer.Tests/SpectrumAnalyzer.Tests.csproj
dotnet test src/LabPlot.DLS/DlsAnalyzer.Tests/DlsAnalyzer.Tests.csproj

# Run（ポータルを起動。3 つの解析モジュールはここから開く）
dotnet run --project src/LabPlot.Shell/LabPlot.Shell.csproj
```

Visual Studio で開く場合は、`LabPlot.slnx` を直接開けばテストエクスプローラから全 xUnit を実行できます。アプリ単独に集中したいときはアプリ側の `.slnx` を開けば、そのアプリ＋共有ライブラリだけがロードされます。

### 配布用の単一 exe を作成

非エンジニア向けに配布するときは、`src/LabPlot.Shell/Properties/PublishProfiles/win-x64.pubxml` を使った publish プロファイル経由でビルドします。`Release` 構成・`win-x64` ランタイム・`SelfContained=true`・`PublishSingleFile=true` がプロファイル側で固定されているので、コマンド側で `-c` や `-r` を重ねて指定する必要はありません（指定するとプロファイルと矛盾するため非推奨）。.NET ランタイムが入っていない PC でも、生成された exe をダブルクリックするだけで起動できます。

```powershell
dotnet publish src/LabPlot.Shell/LabPlot.Shell.csproj -p:PublishProfile=win-x64
```

成果物は `src/LabPlot.Shell/bin/Release/net10.0-windows10.0.19041/win-x64/publish/LabPlot.exe` に出力されます。GPC / Spectrum の `samples/` は各 csproj の `CopyToPublishDirectory` 設定で publish フォルダに同梱されるので、`publish/` フォルダごと zip にして配布してください。DLS は `samples/` を持たないため、追加同梱物はありません。

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
