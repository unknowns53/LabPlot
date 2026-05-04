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

```powershell
# 例: GPC アプリ
dotnet build src/LabPlot.GPC/GPC_Visualization.slnx
dotnet test src/LabPlot.GPC/GpcAnalyzer.Tests/GpcAnalyzer.Tests.csproj
```

## ライセンス

[MIT License](LICENSE)
