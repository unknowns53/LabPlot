# LabPlot 利用者向けガイド

LabPlot は、Shimadzu LabSolutions（GPC）/ JASCO V-750（UV-Vis・FTIR）/ Malvern Zetasizer（DLS）といった研究室の測定装置から出力されたデータを読み込んで、グラフ表示・書式調整・解析・PNG / SVG / Excel / CSV 書き出しを行うデスクトップアプリです。3 つの解析モジュールが 1 本のポータル（ランチャー）に同梱されており、Windows / macOS / Linux のどれでも同じ感覚で使えます。

このガイドは「LabPlot を使ってグラフを 1 枚作るまで」「装置からデータを取り出す手順」「困ったときの対処」を、初めての利用者でも追えるレベルでまとめたものです。開発者向けのビルド手順や API 仕様は対象外なので、コードに手を入れたい場合はリポジトリ直下の [README.md](../../README.md) と各モジュールの README を参照してください。

---

## 対象バージョン

このガイドは **Avalonia 主流版 v1.2.0（`LabPlot.Avalonia`）** を主軸に書いています。Windows 専用の旧 WPF 版（`LabPlot.exe`、v1.1.0 系）も基本的な操作は同じですが、配布物の入手先と一部の見た目が異なります。違いは [FAQ](./faq.md) の「旧 WPF 版（v1.1.0）と何が違う？」を参照してください。

---

## 目的別ナビ

やりたいことから入口を選んでください。

| やりたいこと | 行き先 |
| --- | --- |
| はじめて使うので、とりあえず動かしたい | [インストール](./installation.md) → [クイックスタート](./quick-start.md) |
| GPC のデータを開いて分子量分布を出したい | [GPC モジュール](./gpc.md) |
| UV-Vis や FTIR のスペクトルを解析したい | [Spectrum モジュール](./spectrum.md) |
| Zetasizer の DLS データから粒径分布を出したい | [DLS モジュール](./dls.md) |
| ポータル（ランチャー）の挙動を知りたい | [ポータルの使い方](./portal.md) |
| LabSolutions / Spectra Manager / Zetasizer 側でのエクスポート手順を確認したい | [装置別データ準備](./data-preparation/README.md) |
| 同梱サンプルデータを試したい | [サンプルデータ一覧](./samples.md) |
| 起動できない、ファイルが開けない、グラフが出ない | [トラブルシューティング](./troubleshooting.md) |
| よくある質問を確認したい | [FAQ](./faq.md) |

---

## ガイドの構成

このフォルダ（`docs/user-guide/`）の中身は、以下の 3 種類に分かれています。

**共通（モジュールに依存しない話題）**

- [installation.md](./installation.md) — インストール、起動、初回セットアップ、ログの場所
- [quick-start.md](./quick-start.md) — 同梱サンプルで 5 分でグラフを 1 枚作るシナリオ
- [portal.md](./portal.md) — ポータル（カード型ランチャー）の動作と、ウィンドウまわりの挙動
- [samples.md](./samples.md) — 同梱サンプルデータの一覧と置き場所
- [faq.md](./faq.md) — よくある質問と回答
- [troubleshooting.md](./troubleshooting.md) — 起動・ファイル読み込み・描画・D&D・フォントなどのつまずきポイント

**モジュール別（各解析アプリの操作手順）**

- [gpc.md](./gpc.md) — GPC（ゲル浸透クロマトグラフィー）
- [spectrum.md](./spectrum.md) — Spectrum（UV-Vis 波長スキャン / 温度スキャン / FTIR）
- [dls.md](./dls.md) — DLS（動的光散乱、粒径分布と自己相関関数）

**装置別（測定装置側でのデータエクスポート手順）**

- [data-preparation/](./data-preparation/README.md) — LabSolutions / Spectra Manager / Zetasizer のエクスポートガイド

---

## このガイドを読む順番

何から読めばいいか迷ったら、以下の順番がおすすめです。

1. [インストール](./installation.md) で配布物を手元に置く
2. [クイックスタート](./quick-start.md) で同梱サンプルを開いて PNG を 1 枚出す
3. 自分が使いたいモジュール（[GPC](./gpc.md) / [Spectrum](./spectrum.md) / [DLS](./dls.md)）の章を頭から流し読みする
4. 自分の装置のデータが手元に来たら、対応する [装置別データ準備](./data-preparation/README.md) ページでエクスポート手順を確認する
5. 困ったら [トラブルシューティング](./troubleshooting.md) と [FAQ](./faq.md) を当たる

---

## 開発者向け情報の在りか

ビルド手順、テストコマンド、ライブラリ間の依存関係、API 仕様などはこのガイドの対象外です。以下を参照してください。

- リポジトリ直下の [README.md](../../README.md)
- 各モジュール csproj 配下の README（`src/LabPlot.GPC.Avalonia/README.md` ほか）
- 今後の機能追加予定: [ROADMAP.md](../../ROADMAP.md)
- リリースノート: [CHANGELOG.md](../../CHANGELOG.md)
