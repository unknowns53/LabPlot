# LabPlot.Core

LabPlot の各アプリ（GPC / Spectrum / DLS、今後の追加アプリも含む）が共有する WPF 非依存の解析・セッション・エクスポート基盤です。`net10.0` をターゲットにし、ScottPlot 5.x への薄い依存だけを持ちます。WPF を参照していないので、将来の Avalonia 移植や CLI ラッパからも同じコードパスを呼べます。

含まれる主なコンポーネント:

- `IDataReader<TDataset>` — ファイルパス → アプリ固有のデータセット型を返す汎用リーダ契約。各アプリは `IGpcDataReader` / `ISpectrumDataReader` / `IDlsDataReader` のようなマーカーインターフェースで派生させ、フォーマットの選択（LabSolutions TXT、JASCO TXT/CSV、Zetasizer xlsx …）を WPF 層に漏らさない構造にしています。
- `IAnalysisExporter` と `AnalysisExport` / `AnalysisExportEntry` — CSV / xlsx エクスポートの統一窓口。アプリは自分のデータ形に合わせた `IAnalysisExporter` 実装（GPC は `XlsxAnalysisExporter`、Spectrum は `XlsxAnalysisExporter`、DLS は `DlsXlsxAnalysisExporter` …）を提供し、ホスト側は `AnalysisExport` を組み立てて `Export(data, filePath)` を呼ぶだけです。
- `AnalysisSession` 系 — セッション JSON のスキーマを構成するベースクラス群。`AnalysisSession` はメタデータとプレゼンテーション状態（overlay フラグ・選択中インデックス・軸ラベル）、`AnalysisSessionDataset` / `AnalysisSessionAxes` / `AnalysisSessionLabels` / `AnalysisSessionStyle` がデータセット・軸範囲・ラベル・線スタイルの最小共通集合を持ちます。各アプリは派生クラスでアプリ固有のフィールド（GPC の較正曲線、Spectrum のピーク・積分領域、DLS の測定条件など）を足します。
- `AnalysisSessionStore<TSession>` — UTF-8（BOM 付き）のプリティ JSON でセッションを読み書きする汎用ストア。デシリアライズ後に `EnsureDefaults` を呼ぶので、部分 JSON で `null` が返ったコンテナはサブクラスが自分で再構築できます。
- `GraphFormattingConfigBase` と `ConfigNormalizer` — フォント・枠・グリッド・背景・凡例位置などのアプリ共通書式項目の基底クラスと、`GraphFormattingConfig.Normalize()` から呼ぶ正規化ヘルパ（正の double、有限レンジ、7 文字 hex カラー、不変カルチャの数値整形）。
- `PlotAppearance` — `ScottPlot.Plot` に書式スナップショット（`GraphFormattingConfigBase`）を適用する WPF 非依存ヘルパ。プレビュー（96 dpi, scale=1）と PNG 書き出し（300 dpi, scale≈3.125）でメジャー／マイナー目盛りや線幅の見た目を一致させるため、`ConfigureTickMarkStyle` などは常に `base × scale` で再計算します。

---

## 使い方の最小例

新しいアプリを `LabPlot.Core` の上に乗せるときは、おおよそ以下のように継承します:

```csharp
using LabPlot.Core;

public sealed class MyAppDataset { /* ... */ }

public interface IMyAppDataReader : IDataReader<MyAppDataset> { }

public sealed class MyAppFormattingConfig : GraphFormattingConfigBase
{
    // App-specific fields here
    public override void Normalize()
    {
        base.Normalize();
        // ...
    }
}

public sealed class MyAppSession : AnalysisSession
{
    public MyAppSession() { GeneratorName = "MyApp"; }
    public List<AnalysisSessionDataset> Datasets { get; set; } = new();
    public AnalysisSessionAxes Axes { get; set; } = new();
    public MyAppFormattingConfig? Formatting { get; set; }

    public override void EnsureDefaults()
    {
        Datasets ??= new();
        Axes ??= new();
        Labels ??= new();
        Formatting?.Normalize();
    }
}

// Save / Load
var store = new AnalysisSessionStore<MyAppSession>();
store.Save(session, @"C:\path\to\session.myappjson");
var loaded = store.Load(@"C:\path\to\session.myappjson");
```

WPF レイヤとの接続は `LabPlot.Core.Wpf` が担当します（共通スタイル、AxisRange / GraphFormat / ColorPicker パネル、ScottPlot ホスト周りのアスペクト比、PNG/SVG 保存ダイアログ）。

---

## ファイル構成

```text
AnalysisExport.cs                エクスポート入力コンテナ（エントリ列・タイムスタンプ・ジェネレータ名）
AnalysisExportEntry.cs           エクスポートの 1 データセット相当の抽象ベース
AnalysisSession.cs               セッション基底（バージョン・タイムスタンプ・ジェネレータ・overlay・active index・labels）
AnalysisSessionAxes.cs           軸範囲オーバーライド（X/Y min・max）
AnalysisSessionDataset.cs        データセットエントリ（ソースパス＋スタイル）
AnalysisSessionLabels.cs         タイトル・X/Y ラベル
AnalysisSessionStore.cs          JSON 永続化（UTF-8 BOM、`EnsureDefaults` 呼び出し）
AnalysisSessionStyle.cs          線スタイル（色 hex・線幅・マーカーサイズ・凡例名）
ConfigNormalizer.cs              書式設定の値ガード／正規化ヘルパ
GraphFormattingConfigBase.cs     書式設定の共通基底（フォント・枠・グリッド・背景・凡例）
IAnalysisExporter.cs             エクスポータ契約
IDataReader.cs                   データリーダ契約
PlotAppearance.cs                ScottPlot.Plot に書式を適用する WPF 非依存ヘルパ
```

---

## 開発者向け

ビルドとテスト:

```powershell
dotnet build src/LabPlot.Core/LabPlot.Core.csproj
```

このプロジェクト単体には xUnit テストは付いていません。各アプリの Tests プロジェクト（`GpcAnalyzer.Tests` / `SpectrumAnalyzer.Tests` / `DlsAnalyzer.Tests`）が `LabPlot.Core` の主要な経路を間接的に検証する形になっています。

依存:

- `ScottPlot 5.1.x` — `PlotAppearance` から `ScottPlot.Plot` を直接触っているため。それ以外の機能（セッション・リーダ抽象・エクスポータ抽象・ConfigNormalizer）は ScottPlot を参照しないので、CLI から呼ぶ場合はそれらだけ取り出して利用できます。
