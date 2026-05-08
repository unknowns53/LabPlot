# LabPlot.DLS.Avalonia

Malvern Zetasizer の DLS（動的光散乱）測定ブックを読み込んで、粒径分布と自己相関関数を可視化し、キュムラント解析で Z-average / PdI / 流体力学半径を算出する **主流系統** モジュールです。Windows / macOS / Linux 共通で動作し、ポータル `LabPlot.Avalonia` のカード「DLS」から起動されます。

> 保守用の WPF 系統には [`src/LabPlot.DLS`](../LabPlot.DLS/) があり、新機能・バグ修正は本モジュールを優先して受けます。機能仕様は基本的に WPF 版と同等で、UI 操作と機能の詳細は WPF 側 README を参考にできます。

主な機能:

- Zetasizer の xlsx エクスポートを ClosedXML 経由で読み込み（1 シート＝1 測定、列ヘッダから Number / Intensity / Volume / 相関関数を自動分類）
- 重ね描き、線色（プリセット + Custom HSV / hex）・線幅・凡例名の個別設定、ListBox 上のドラッグで並び替え
- 分布タイプ（Number / Intensity / Volume / 自己相関 g₂−1）と Run（通常 3 回繰り返し）の切替
- キュムラント解析: フィット範囲指定（空欄で自動検出、g₂−1 ≥ 0.1 を保持）、Z-average 径・PdI・第 1 累積量 Γ・R² を表示
- Stokes–Einstein 式で流体力学径を算出（測定条件をサイドバーで補完）
- 軸範囲・タイトル・フォント・グリッド・プロット枠・アスペクト比の調整
- 300 dpi PNG または SVG で書き出し、xlsx / csv で解析結果を出力
- 解析条件を `.dlsjson` として保存・復元

---

## v1.0.x WPF 版との差分

UI レベルの差分は `LabPlot.Core.Avalonia` の README に集約してあります。DLS 固有の差分は次の通り:

- Zetasizer xlsx の読み取り中も Excel で同じファイルを開いていられる挙動（`FileShare.ReadWrite | FileShare.Delete`）は WPF 版と同じく ClosedXML レイヤで吸収済み
- ListBox 並べ替え: WPF DragDrop + `InsertionAdorner` → `Helpers/DragGhostController`（DataTemplate.Build ベースのベクター描画ゴースト）+ `InputHitTest` ベースの drop target 解決 + AXAML sibling `InsertionLine` Grid（DropShadowEffect グロー入り）
- ファイルピッカー: `Microsoft.Win32.OpenFileDialog` → `TopLevel.GetTopLevel(this).StorageProvider.OpenFilePickerAsync` (async)
- ScottPlot ホスト: `ScottPlot.WPF.WpfPlot` → `ScottPlot.Avalonia.AvaPlot`

---

## 開発者向け

ビルド:

```powershell
dotnet build src/LabPlot.DLS.Avalonia/LabPlot.DLS.Avalonia.csproj
```

このモジュールは LabPlot ポータル（`LabPlot.Shell.Avalonia`）から起動するクラスライブラリです。`LabPlot.DLS.Avalonia.csproj` は `WinExe` ではなく library 出力なので、`dotnet run` で直接起動はできません。デバッグ実行する場合は `LabPlot.slnx`（リポジトリ直下）から `LabPlot.Shell.Avalonia` をスタートアップに指定し、ポータルのカードから DLS を起動してください。

配布: 主流配布の publish 手順は [`../LabPlot.Shell.Avalonia/README.md`](../LabPlot.Shell.Avalonia/README.md) を参照。

依存:

- `LabPlot.Core` — 解析ロジック・セッション・エクスポート抽象
- `LabPlot.Core.Avalonia` — 共通 UI 基盤
- `DlsAnalyzer.Core` — DLS 固有のドメインロジック（xlsx リーダ・キュムラント解析・Stokes–Einstein）
- `Avalonia 11.3.14` / `ScottPlot.Avalonia 5.1.58` / `ClosedXML`

機能仕様の詳細は [v1.0.x WPF 版 README](../LabPlot.DLS/README.md) を参照してください（インストール・データ読み込み・分布タイプ切替・測定条件補完・キュムラント解析・グラフ調整・出力・セッションの操作手順は基本的に同じです）。
