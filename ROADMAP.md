# LabPlot Roadmap

LabPlot 全体の今後の機能追加・拡張計画をまとめたメモです。優先度や着手時期は流動的で、必要が具体化したものから順次着手する方針です。

最終更新: 2026-05-02

---

## 1. 共通基盤化（短期）

GPC・Spectrum・DLS の 3 アプリで共通する解析ロジック・UI 部品を切り出し、保守と一貫性を担保する。

- **`LabPlot.Core`**: 書式設定（`GraphFormattingConfig`）、セッション保存、PNG / SVG / Excel / CSV エクスポート、ScottPlot セットアップ補助、JASCO / LabSolutions / Zetasizer 等のリーダー抽象化（`ISpectrumDataReader` 系）。WPF 非依存とし、xUnit でテスト容易に
- **`LabPlot.Core.Wpf`**: 共有 ResourceDictionary（`Themes/CommonStyles.xaml`）、ScottPlot ホストヘルパ、データセットのドラッグ並び替え支援、共通ダイアログなど

切り出しは GPC / Spectrum を一気に書き換えるのではなく、対象を 1 種類ずつ移して両アプリのビルドが通る状態を維持しながら進める方針。DLS は最初から `LabPlot.Core` ベースで開発する。

---

## 2. アプリ別の今後の機能

### LabPlot.GPC

- **パフォーマンス最適化**: LabSolutions TXT は数万点規模になることがあり、重ね描き枚数が増えると描画更新が重くなる場合がある。BenchmarkDotNet で具体的なボトルネックを特定したうえで、`Span<char>` ベースの自前パーサや読み込み並列化、レンダリングパスの見直しを検討
- **配布後フィードバックに基づく書式の微調整**: 軸ラベル・凡例位置・既定値の保存復元・アスペクト比反映など、研究室メンバーからの報告ベースで対応

### LabPlot.Spectrum

波長スキャン:

- **参照（ブランク）スペクトルの差し引き**: ベースライン補正用の差分機能
- **未知サンプル濃度の逆算**: Beer-Lambert 検量線確定後、別データセットの吸光度から濃度を逆算する機能。`CalibrationCurveWindow` に「未知サンプル」タブを追加する方向

温度スキャン:

- **Boltzmann sigmoid fit による Tc 推定**: 現状の中点法 / 1 次微分極大法 / 2 次微分極大法に加え、シグモイド関数 fit でロバストに推定する 4 つ目の手法

IR:

- **JASCO FTIR の TXT エクスポート対応の検証**: 現状リーダーは区切り文字自動判定で読めるはずだが、実ファイルでの回帰テストを追加
- **IR 異常値（測定装置由来）の扱い**: `-1.17549E-038` のような sentinel 値や負値・極端値の処理方針を確定し、必要なら NaN 化フィルタなどを追加
- **より高度なベースライン補正**: 現状の None / Linear / 凸包 / rubber-band / 多項式に加えて、必要なバリエーションを追加

### LabPlot.DLS（新規）

Malvern Zetasizer の DLS データ可視化・解析を新規開発:

- Zetasizer の CSV / xlsx エクスポート対応（xlsx は ClosedXML を使用予定）
- 粒径分布の表示（intensity / volume / number 切替）
- 自己相関関数 g²(τ) の可視化と diffusion coefficient 計算
- キュムラント解析（Z-average、polydispersity index）
- 多成分フィット（CONTIN 等の正則化逆問題は将来的な検討対象）

---

## 3. 新規対応フォーマット

- **JCAMP-DX (`.jdx` / `.dx`)**: 国際標準フォーマット。XYDATA 形式・圧縮 ASCII（DIF / SQZ など）の解釈が必要でパーサのコストはやや高いが、Bruker / Thermo / Shimadzu のいずれでも吐けるため汎用性が大きく上がる。Spectrum / IR で活用予定
- **Shimadzu UVProbe (`.spc` / `.txt`)**: Shimadzu 機を持つ研究室から要望があれば
- **Agilent Cary、Hitachi U シリーズ**: 必要が出たときに対応。リーダー抽象化（`ISpectrumDataReader` 系）の差し替えで対応可能な構造

---

## 4. 新規アプリ候補

- **DSC**: ガラス転移点・融解ピーク・結晶化ピークの帰属、ベースライン補正、ΔH 積分など。Spectrum 拡張として組み込むか、独立アプリ `LabPlot.DSC` として作るかは未確定。データモデル（昇温・冷却サイクルが対）が UV-Vis / IR と大きく異なる点が論点
- **TGA / NMR**: 研究室での使用頻度・解析ニーズが具体化したら検討

---

## 5. クロスプラットフォーム展開

現状は WPF + win-x64 single-file exe で配布。macOS / Linux ユーザー向けには **Avalonia UI への移植** が選択肢。

- Avalonia は WPF と XAML 構造が近く、`MainWindow.xaml` と code-behind の多くは流用可能
- ScottPlot は Avalonia 用コントロール（`ScottPlot.Avalonia`）が利用可能
- 自前 ControlTemplate（ボタン / コンボボックス / チェックボックス等）の移植は必要
- 想定作業量はアプリあたり 2〜4 日。コードベースが大きい GPC で先行検証してから Spectrum / DLS に展開する方が安全

CLI / ライブラリ化（`LabPlot.Core` の薄い CLI ラッパーで CSV → PNG / xlsx 変換だけ提供）も補助的な選択肢として残す。

---

## 6. 既知の制限・改善余地

- **テストカバレッジ**: 各アプリで単体テストはあるが、不正ファイル（ヘッダ欠損・データ行混入）に対する挙動テストの拡充余地あり
- **サンプルデータ**: `samples/` を各装置・測定種ごとに整備。エッジケース（極端に小さい・大きいデータ）の追加も
- **ドキュメント**: スクリーンショット込みの README は GPC が先行整備済み、Spectrum / DLS も同様に整える
- **パフォーマンスベンチマーク**: 体感で重さを感じるケースが具体化したら BenchmarkDotNet で計測

---

## 取り組み順序の参考

おおまかな優先度は以下を想定:

1. **共通基盤化（1）**: DLS 開発前に `LabPlot.Core` / `LabPlot.Core.Wpf` を切り出し、保守の足場を整える
2. **LabPlot.DLS 新規開発（2-DLS）**: Core が整った時点で着手
3. **Spectrum 残課題（2-Spectrum）**: ブランク差し引き・濃度逆算・Boltzmann fit など、利用ニーズに合わせて随時
4. **新規フォーマット対応（3）**: 共同研究者・研究室メンバーの要望が具体化したら
5. **GPC パフォーマンス最適化（2-GPC）**: 体感で困るケースが出てきたら
6. **新規アプリ候補（4）**: 必要が具体化してから
7. **クロスプラットフォーム（5）**: 必要が出てから
