# サンプルデータ

LabPlot の配布物には、動作確認用のサンプルデータが同梱されています。実機データが手元に無くても、このサンプルで操作の流れを試せます。

---

## サンプルの場所

配布物（zip）を解凍したフォルダの中に、各モジュールに対応する `samples/` サブフォルダがあります。中身は publish 時にビルドシステムが自動で同梱したものです（`CopyToPublishDirectory` 経由）。

- Windows: `<解凍したフォルダ>\samples\`
- macOS / Linux: `<解凍したフォルダ>/samples/`

DLS モジュールには合成データのデモが同梱されています（後述）。実機データが手元に無くてもキュムラント解析と Stokes–Einstein 計算の流れを試せます。

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

PNIPAM の振る舞いを想定した合成データを 1 ファイル同梱しています。Zetasizer xlsx エクスポートと同じ列構造（`Size (d.nm)` / `Number (Percent)` / `Intensity (Percent)` / `Volume (Percent)` / `Time (µs)` / `Correlation Coefficient (g₂-1)` のヘッダ規約、3 run 構成）になっているので、実機ファイルとまったく同じ手順で読み込めます。

| ファイル | 内容 |
| --- | --- |
| `demo.xlsx` | (1) 25 °C PNIPAM コイル状態（単峰、d_h ≈ 10 nm）、(2) 35 °C で LCST を越えた凝集状態（コイル + 凝集体の二峰、d_h ≈ 200 nm）、(3) 25–35 °C の温度ランプシリーズ 8 シート（T_c = 31 °C、d_low = 10 nm、d_high = 200 nm の Boltzmann シグモイドからサンプリング）、(4) 25 °C 濃度シリーズ 7 シート（0.5–10 mg/mL、d_h(c=0) = 10 nm、k_D = −25 mL/g の引力性相互作用）、合計 17 シートの合成データ |

試せること:

- データ読み込み → 粒径分布／自己相関 g₂-1 の表示切替
- サイドバーの測定条件に温度 25 °C・粘度 0.890 mPa·s・屈折率 1.330・波長 633 nm・散乱角 173° を入れて、コイルシートで Z-average 径が ≈ 10 nm に出ることを確認
- 35 °C シートで `Number (%)` から `Intensity (%)` に切り替えると、二峰のうち凝集体側ピークが圧倒的に持ち上がる Rayleigh 散乱の効きを観察
- 温度ランプ 8 シートそれぞれを選んで「測定条件」に温度（25, 27, 29, 30, 31, 32, 33, 35 °C）と対応する水の粘度を入力し、「分布の種類」を「温度ランプ T vs d_h」に切り替えると Boltzmann fit から LCST = 31 °C ± 1 °C が取り戻せることを確認
- 濃度シリーズ 7 シートそれぞれの「測定条件」に温度 25 °C・粘度 0.890 mPa·s と濃度（0.5, 1, 2, 4, 6, 8, 10 mg/mL）を入力し、「分布の種類」を「濃度シリーズ c vs D」に切り替えると D vs c の線形 fit から d_h(c→0) = 10 nm ± 1 nm と k_D ≈ −25 mL/g が取り戻せることを確認
- `.dlsjson` に解析条件を保存して、次回起動時に同じ状態で復元できることを確認

合成データの正体は `tools/DlsSampleGenerator/` のコンソールツールで、Stokes–Einstein 式と単一指数の重ね合わせで生成しています。手元で再生成したい場合は `dotnet run --project tools/DlsSampleGenerator` を実行してください。詳しくは同フォルダの README を参照。

実機データを使う場合のエクスポート手順は [Malvern Zetasizer のデータ準備](./data-preparation/malvern-zetasizer.md) に、ファイル形式の詳細は [DLS モジュールの使い方](./dls.md) にまとめています。
