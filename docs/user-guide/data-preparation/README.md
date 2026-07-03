# 装置別データ準備

LabPlot は研究室の測定装置（Shimadzu LabSolutions / JASCO V-750 / Malvern Zetasizer など）から出力されたデータをそのまま読み込めるように設計されていますが、装置側でのエクスポート手順は装置メーカー・ソフトウェアごとに異なります。このセクションでは、各装置ソフトでどのようにデータを取り出して LabPlot に渡すかを案内します。

> 対象: LabPlot v1.4.1（Avalonia 主流版）

---

## 装置とモジュールの対応表

| 装置 | 対応モジュール | データ準備ガイド |
| --- | --- | --- |
| Shimadzu LabSolutions（GPC） | [GPC](../gpc.md) | [Shimadzu LabSolutions](./shimadzu-labsolutions.md) |
| JASCO V-750 / FTIR（Spectra Manager） | [Spectrum](../spectrum.md) | [JASCO V-750](./jasco-v750.md) |
| Malvern Zetasizer（DLS） | [DLS](../dls.md) | [Malvern Zetasizer](./malvern-zetasizer.md) |

---

## どのページから読めばいい？

自分が使う装置のページを直接開いてください。各ページには次の情報が載っています。

1. LabPlot 側が期待するファイル形式（拡張子・列構成・エンコーディング）
2. 装置ソフト側でのエクスポート手順
3. よくあるエラーと対処

サンプルデータで先に試したい場合は、[同梱サンプル一覧](../samples.md) も参照してください。

---

## 共通の注意点

- 装置ソフトから出力するファイルは、LabPlot 側で開く前に **「Excel などで上書き保存していない」** ことを確認してください。Excel が自動で文字コードを変換したり、行末を改変したりすると LabPlot 側で読み取りに失敗することがあります。
- ファイル名に日本語を含めても LabPlot 側は問題なく読めますが、外部ツールとの連携（git・クラウドストレージ・古いソフト）でトラブルになることがあります。可能なら ASCII 文字＋日付＋通し番号の組み合わせを推奨します。
- 装置ソフトのバージョンが上がるとエクスポートの細部（区切り文字・ヘッダ行の有無・メタデータ位置）が変わることがあります。読み込み時にエラーが出る場合は、[トラブルシューティング](../troubleshooting.md) も合わせて確認してください。
