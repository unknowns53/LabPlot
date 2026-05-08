# LabPlot.Spectrum.Avalonia

> **利用者向け操作手順は [`docs/user-guide/spectrum.md`](../../docs/user-guide/spectrum.md) を参照してください。** 本書は開発者向けの差分メモです。

UV-Vis 波長スキャン・温度スキャン・FTIR スペクトルの可視化と解析を行う **主流系統** モジュールです。Windows / macOS / Linux 共通で動作し、ポータル `LabPlot.Avalonia` のカード「UV-Vis」から起動されます。

> 保守用の WPF 系統には [`src/LabPlot.Spectrum`](../LabPlot.Spectrum/) があり、新機能・バグ修正は本モジュールを優先して受けます。機能仕様は基本的に WPF 版と同等で、UI 操作と機能の詳細は WPF 側 README を参考にできます。

主な機能:

- JASCO V-750 系の UV-Vis TXT、FTIR CSV を自動判定で読み込み（区切り文字・Shift-JIS フッタの `[測定情報]` / `[付属品情報]` も解釈）
- 重ね描き、線色（プリセット + Custom HSV / hex）・線幅・凡例名の個別設定、ListBox 上のドラッグで並び替え
- 波長スキャン: λmax 自動検出（プロミネンス／高さ閾値、手動追加も可）、領域積分（None / Linear / 凸包 / rubber-band / 多項式ベースライン）、Beer-Lambert 検量線（吸光度・面積モード、外れ値除外）
- 温度スキャン: ヒステリシス（heating / cooling 対）対応、Tc を 中点法 / 1 次微分極大 / 2 次微分極大 / Boltzmann sigmoid fit から選択
- IR: ピーク自動検出（プロミネンス + 窓幅 + 上位 N 個）、手入力ピーク帰属
- 軸範囲・タイトル・フォント・グリッド・プロット枠・アスペクト比の調整
- 300 dpi PNG または SVG で書き出し、xlsx / csv で解析結果（生データ・積分結果・検量線）を出力
- 解析条件を `.specjson` として保存・復元

---

## v1.0.x WPF 版との差分

UI レベルの差分は `LabPlot.Core.Avalonia` の README に集約してあります。Spectrum 固有の差分は次の通り:

- 検量線エディタ（`CalibrationCurveWindow`）: WPF DataGrid → `Avalonia.Controls.DataGrid 11.3.13`（本体より 1 リビジョン下が NuGet 最新）。`Window.Styles` に `<StyleInclude Source="avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml" />` を merge
- 積分領域: WPF `Adorner` ベースのドラッグ overlay → AXAML sibling Grid + `e.GetPosition(plotHost)` ベースの edge resize（左右ハンドル + 全体ドラッグ）
- λmax 手動追加: `MouseLeftButtonDown` クリックで局所極大に snap → `PointerPressedEvent` Tunnel + `e.GetPosition(plot)` でのクリック判定
- AbsorbanceConfirmDialog: WPF `Window.ShowDialog()` → `Window.ShowDialog<T>(Window owner)` の async / generic 版
- ScottPlot ホスト: `ScottPlot.WPF.WpfPlot` → `ScottPlot.Avalonia.AvaPlot`（Plot.Add 系 API は同一）

---

## 開発者向け

ビルド:

```powershell
dotnet build src/LabPlot.Spectrum.Avalonia/LabPlot.Spectrum.Avalonia.csproj
```

このモジュールは LabPlot ポータル（`LabPlot.Shell.Avalonia`）から起動するクラスライブラリです。`LabPlot.Spectrum.Avalonia.csproj` は `WinExe` ではなく library 出力なので、`dotnet run` で直接起動はできません。デバッグ実行する場合は `LabPlot.slnx`（リポジトリ直下）から `LabPlot.Shell.Avalonia` をスタートアップに指定し、ポータルのカードから UV-Vis を起動してください。

配布: 主流配布の publish 手順は [`../LabPlot.Shell.Avalonia/README.md`](../LabPlot.Shell.Avalonia/README.md) を参照。

依存:

- `LabPlot.Core` — 解析ロジック・セッション・エクスポート抽象
- `LabPlot.Core.Avalonia` — 共通 UI 基盤
- `SpectrumAnalyzer.Core` — Spectrum 固有のドメインロジック（λmax / Tc / Beer-Lambert / 積分 / IR ピーク帰属）
- `Avalonia 11.3.14` / `ScottPlot.Avalonia 5.1.58` / `Avalonia.Controls.DataGrid 11.3.13`

機能仕様の詳細は [v1.0.x WPF 版 README](../LabPlot.Spectrum/README.md) を参照してください（インストール・データ読み込み・グラフ調整・解析・出力・セッションの操作手順は基本的に同じです）。
