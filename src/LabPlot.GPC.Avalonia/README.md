# LabPlot.GPC.Avalonia

GPC（ゲル浸透クロマトグラフィー）の測定データを読み込んで、クロマトグラムや分子量分布を表示し、PNG / SVG / Excel / CSV として書き出すための **主流系統** モジュールです。Windows / macOS / Linux 共通で動作し、ポータル `LabPlot.Avalonia` のカード「GPC」から起動されます。

> 保守用の WPF 系統には [`src/LabPlot.GPC`](../LabPlot.GPC/) があり、新機能・バグ修正は本モジュールを優先して受けます。機能仕様は基本的に WPF 版と同等で、UI 操作と機能の詳細は WPF 側 README を参考にできます。

主な機能:

- LabSolutions の TXT エクスポート、`Time, Signal` 形式の CSV / TSV を読み込み
- ScottPlot.Avalonia による即時プレビュー、軸範囲・タイトル・フォントなどの書式調整
- 較正曲線 JSON を読み込んで保持時間 → 分子量変換、Mn / Mw / Ð の表示
- 複数データの重ね描き、線色・線幅・凡例名の個別設定、ListBox 上のドラッグで並び替え（`Helpers/DragGhostController` ベース）
- 300 dpi PNG または SVG でグラフ書き出し、xlsx / csv で解析結果書き出し
- 解析条件（読み込み済みデータ・較正曲線・書式・軸範囲など）を `.gpcjson` として保存・復元

---

## v1.0.x WPF 版との差分

UI レベルの差分は `LabPlot.Core.Avalonia` の README に集約してあります。GPC 固有の差分は次の通り:

- ファイルピッカー: `Microsoft.Win32.OpenFileDialog` → `TopLevel.GetTopLevel(this).StorageProvider.OpenFilePickerAsync` (async)
- ファイル D&D: `DataFormats.FileDrop` の `string[]` → `DataFormats.Files` の `IStorageItem.TryGetLocalPath()`
- ListBox drop indicator: WPF Adorner ベースの `InsertionAdorner` → AXAML sibling `InsertionLine` Grid（Rectangle 線 + Ellipse 端点 2 個 + DropShadowEffect グロー）+ `item.TranslatePoint + Margin.Top` 方式
- ドラッグゴースト: WPF DragDrop のドラッグソース表示 → `Helpers/DragGhostController` の DataTemplate.Build ベース ベクター描画クローン（OverlayLayer 上、`InputHitTest` で PointerCapture 中も正しい drop target 解決）
- ScottPlot ホスト: `ScottPlot.WPF.WpfPlot` → `ScottPlot.Avalonia.AvaPlot`

---

## 開発者向け

ビルド:

```powershell
dotnet build src/LabPlot.GPC.Avalonia/LabPlot.GPC.Avalonia.csproj
```

このモジュールは LabPlot ポータル（`LabPlot.Shell.Avalonia`）から起動するクラスライブラリです。`LabPlot.GPC.Avalonia.csproj` は `WinExe` ではなく library 出力なので、`dotnet run` で直接起動はできません。デバッグ実行する場合は `LabPlot.slnx`（リポジトリ直下）から `LabPlot.Shell.Avalonia` をスタートアップに指定し、ポータルのカードから GPC を起動してください。

配布: 主流配布の publish 手順は [`../LabPlot.Shell.Avalonia/README.md`](../LabPlot.Shell.Avalonia/README.md) を参照。

依存:

- `LabPlot.Core` — 解析ロジック・セッション・エクスポート抽象
- `LabPlot.Core.Avalonia` — 共通 UI 基盤
- `GpcAnalyzer.Core` — GPC 固有のドメインロジック（較正曲線・分子量変換）
- `Avalonia 11.3.14` / `ScottPlot.Avalonia 5.1.58`

機能仕様の詳細は [v1.0.x WPF 版 README](../LabPlot.GPC/README.md) を参照してください（インストール・データ読み込み・較正曲線・グラフ調整・出力・セッションの操作手順は基本的に同じです）。
