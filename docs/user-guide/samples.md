# サンプルデータ

LabPlot の配布物には、動作確認用のサンプルデータが同梱されています。実機データが手元に無くても、このサンプルで操作の流れを試せます。

---

## サンプルの場所

配布物（zip）を解凍したフォルダの中に、各モジュールに対応する `samples/` サブフォルダがあります。中身は publish 時にビルドシステムが自動で同梱したものです（`CopyToPublishDirectory` 経由）。

- Windows: `<解凍したフォルダ>\samples\`
- macOS / Linux: `<解凍したフォルダ>/samples/`

DLS モジュールには同梱サンプルがありません（後述）。研究室の実機データを直接使ってください。

---

## GPC サンプル

PNIPAM を DMF 溶媒で測定した LabSolutions 由来の TXT エクスポート 2 件と、Chloroform / DMF 用の較正曲線 JSON 1 件です。

| ファイル | 内容 |
| --- | --- |
| `20260116_2-000_C-PNIPAM_DMF.txt` | C-PNIPAM の GPC 測定 |
| `20260116_2-058_S-PNIPAM_DMF.txt` | S-PNIPAM の GPC 測定 |
| `standard_curve.json` | Chloroform / DMF を含む較正曲線データ（複数溶媒・検出器を 1 ファイルにまとめた形式） |

試せること:

- 単発読み込み → クロマトグラム描画 → PNG 保存（[クイックスタート](./quick-start.md) と同じ流れ）
- 「重ね描き」で 2 ファイル同時読み込み → 比較プロット
- `standard_curve.json` をロードして「分子量表示」に切り替え、Mn / Mw / Đ を確認する

詳しい手順は [GPC モジュールの使い方](./gpc.md) を参照してください。

---

## Spectrum サンプル

UV-Vis 波長スキャン、温度スキャン（ヒステリシス対）、FTIR スペクトルの 3 種類が入っています。

| ファイル | 種別 | 内容 |
| --- | --- | --- |
| `20240420_1-97_poly(N-butyl-4-ethynylbenzamide).csv` | UV-Vis 波長スキャン | 共役系ポリマーの UV-Vis スペクトル |
| `2-046_before_process.csv` | UV-Vis 波長スキャン | 反応前のサンプル |
| `2-046_after_process.csv` | UV-Vis 波長スキャン | 反応後のサンプル |
| `2_heating.txt` | UV-Vis 温度スキャン | 加熱方向の測定 |
| `2_cooling.txt` | UV-Vis 温度スキャン | 冷却方向の測定（heating と対で読むとヒステリシスを観察できる） |
| `1-16 HO-Ph-acetylene 1.0mg.txt` | FTIR | フェニルアセチレン誘導体の FTIR スペクトル |

試せること:

- UV-Vis 波長スキャン: λmax 自動検出、Beer-Lambert 検量線（before / after を異なる濃度として扱う）
- 温度スキャン: `2_heating.txt` と `2_cooling.txt` を同時に開いて Tc（曇点温度）推定とヒステリシス可視化
- FTIR: ピーク自動検出、手入力ピーク帰属

詳しい手順は [Spectrum モジュールの使い方](./spectrum.md) を参照してください。

---

## DLS サンプル

DLS モジュールには同梱サンプルがありません。Zetasizer の xlsx を研究室の実機から取り出してください。エクスポート手順は [Malvern Zetasizer のデータ準備](./data-preparation/malvern-zetasizer.md) を参照してください。

LabPlot 側が期待するファイル形式（列ヘッダの命名規則、シート単位での扱いなど）は [DLS モジュールの使い方](./dls.md) と [装置別データ準備](./data-preparation/malvern-zetasizer.md) にまとめています。
