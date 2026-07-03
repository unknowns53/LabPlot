# JASCO V-750 のデータ準備（Spectrum 用）

JASCO V-750 系（UV-Vis）および JASCO の FTIR 装置で取得したデータを LabPlot の [Spectrum モジュール](../spectrum.md) で開くためのガイドです。

> 対象モジュール: [Spectrum](../spectrum.md)
> 対象装置: JASCO V-750 系 UV-Vis、JASCO FTIR（Spectra Manager 経由）

---

## LabPlot 側が期待するファイル形式

Spectrum モジュールは JASCO Spectra Manager の TXT / CSV エクスポートを自動判定で読み込みます。Shift-JIS（CP932）でも UTF-8 でも、タブ区切りでもカンマ区切りでも構いません。区切り文字は行ごとに自動判定されます。

ヘッダ部の `DATA TYPE` 行を見て、UV-Vis 波長スキャン / 温度スキャン / FTIR を自動的に判別します。利用者がファイル種別を切り替える必要はありません。

ヘッダ末尾の `[測定情報]` / `[付属品情報]` セクションがあれば、メタデータ（測定波長・スリット幅・スキャンスピードなど）として保持され、グラフ内に表示できます。

参考になる同梱サンプル:

- UV-Vis 波長スキャン: `samples/1-16 HO-Ph-acetylene 1.0mg.txt`
- UV-Vis 温度スキャン（heating / cooling 対）: `samples/2_heating.txt`、`samples/2_cooling.txt`
- FTIR: `samples/20240420_1-97_poly(N-butyl-4-ethynylbenzamide).csv`、`samples/2-046_before_process.csv`、`samples/2-046_after_process.csv`

---

## 装置側でのエクスポート手順

> TODO: 実機で加筆予定
>
> このセクションには JASCO Spectra Manager 上での具体的な操作手順を追記する予定です。実機環境での確認後に埋めます。次の 3 種類の出力に分けて手順を載せる想定です。
>
> - UV-Vis 波長スキャンの TXT / CSV エクスポート
> - UV-Vis 温度スキャンの TXT エクスポート（heating / cooling を別ファイルとして対で出す）
> - FTIR スペクトルの TXT / CSV エクスポート
>
> 暫定的には、Spectra Manager の標準的なテキストエクスポート機能を使えば LabPlot で読み込める形式が出力されるはずです。出力時に `[測定情報]` / `[付属品情報]` を含める設定にしておくと、グラフ内にメタデータを表示できます。

---

## モード別のチェックポイント

**UV-Vis 波長スキャン**

- ヘッダの `DATA TYPE` が UV-Vis 系（`ABSORBANCE` / `%T` など）になっていることを確認してください。
- 波長範囲の単位が nm になっているのが標準です。波数（cm⁻¹）にしていると λmax 検出が想定外の値を返すことがあります。

**UV-Vis 温度スキャン**

- 温度スキャンの `DATA TYPE` 行から温度方向（加熱 / 冷却）を判別します。同じサンプルを加熱と冷却で取った場合は、別ファイルとして 2 つエクスポートしてから LabPlot 側で同時に読み込んでください。ヒステリシスの可視化は heating / cooling を対にすることで自動的にできます。
- ファイル名に `heating` / `cooling` のようなキーワードを含めておくと、解析時に取り違えにくくなります（同梱サンプルがその形式です）。

**FTIR**

- FTIR の TXT は Shift-JIS で出力されることが多く、`[測定情報]` / `[付属品情報]` のセクション名も日本語のまま入っています。LabPlot 側はこの構造を前提に解釈します。
- 測定範囲（4000–400 cm⁻¹ など）と、X 軸の方向（高波数→低波数のままか、反転するか）の確認をしてください。LabPlot は FTIR の慣例に従って高波数を左にする向きで自動描画します。

---

## よくあるエラー

**文字化けする / `[測定情報]` 行で読み込みが止まる**

- Spectra Manager から出力した TXT を Excel で開いて上書き保存すると、Shift-JIS が UTF-8 に変換されたり、改行コードが変わったりして読み込めなくなることがあります。LabPlot で開く前に、装置から出力したオリジナルファイルを使ってください。

**`DATA TYPE` を認識せず、別のモードで開かれる**

- ヘッダ部の `DATA TYPE` 行が削除されていると自動判定が効きません。装置から出力した状態のヘッダをそのまま残してください。
- Spectra Manager のバージョンが大きく異なる場合、ヘッダ形式が違う可能性があります。読み込めない場合は GitHub Issues に報告してください。

**温度スキャンを読み込んでも Tc 解析が動かない**

- データセットが UV-Vis 波長スキャンとして判定されている可能性があります。`DATA TYPE` 行を確認してください。
- 温度スキャンであっても、Y 軸が Reflectance や %T のままでは Boltzmann sigmoid fit が動かない場合があります。Absorbance 形式での出力を推奨します。

そのほかのトラブルは [トラブルシューティング](../troubleshooting.md) を参照してください。
