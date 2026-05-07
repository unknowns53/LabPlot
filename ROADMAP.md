# LabPlot Roadmap

LabPlot 全体の今後の機能追加・拡張計画をまとめたメモです。優先度や着手時期は流動的で、必要が具体化したものから順次着手する方針です。

最終更新: 2026-05-07（Phase 7 Avalonia 移植本体 Batch 1–5 完了、Batch 6 macOS / Linux publish 検証へ）

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

## 5. クロスプラットフォーム展開（Phase 7、進行中）

WPF + win-x64 single-file exe では macOS / Linux ユーザーに配れないので、2026-05-07 から **Avalonia UI への並行移植** に着手。

戦略は「並行ビルド + WPF feature freeze」。既存の WPF プロジェクト（`LabPlot.Shell` / `GPC_Visualization` / `Spectrum_Visualization` / `LabPlot.DLS` / `LabPlot.Core.Wpf`）は v1.0.x の完成形として凍結し、横に Avalonia 版（`LabPlot.Shell.Avalonia` / `LabPlot.GPC.Avalonia` / `LabPlot.Spectrum.Avalonia` / `LabPlot.DLS.Avalonia` / `LabPlot.Core.Avalonia`）を新規追加。WPF 版と Avalonia 版が同じ `LabPlot.Core` / `*Analyzer.Core` を参照する構造なので、ロジック層の進化は二重化なしで両系統に乗ります。

採用バージョン:

- .NET 10
- Avalonia 11.3.14（11 系最新安定版。Avalonia 12 は ScottPlot.Avalonia がまだ追従していないため見送り）
- ScottPlot.Avalonia 5.1.58（既存 ScottPlot.WPF 5.1.58 と同番号）
- Avalonia.Themes.Fluent 11.3.14、Avalonia.Controls.DataGrid 11.3.13（本体より 1 リビジョン下が NuGet 最新）

進捗:

- **Batch 0** ✅: 採用バージョン確定、XAML 規模把握（合計 6049 行 / 17 ファイル）、WPF→Avalonia 差分マッピング整備
- **Batch 1** ✅: `LabPlot.Core.Avalonia` 立ち上げ。CommonStyles / ImplicitStyles / 7 UserControl（CustomTitleBar / AxisRangePanel / ColorPickerPanel / GraphFormatPanel / Error/Success/WarningBanner / BusyOverlay）/ FormatHelpers の Avalonia 版 / Storyboard 起源演出（Chevron 回転、CheckMark slide-in、Expander slide+fade、ToolTip BoxShadow）を Avalonia の Transitions + Animations で再現（Batch 1a `289b83a` / 1b `aee3659` / 1c `df1fca5` / 1d `0002fa6` / 1e `d1075d2` / 1f `1486532` / 1g `610e5e2`）
- **Batch 2** ✅: `LabPlot.Shell.Avalonia` 立ち上げ。PortalWindow を AXAML 化、ログパスを Linux: `~/.local/share/LabPlot/Logs/`、macOS: `~/Library/Application Support/LabPlot/Logs/`、Windows: `%LocalAppData%\LabPlot\Logs\` に振り分け、3 経路で例外をログ集約（commit `691bad6`）
- **Batch 3** ✅: `LabPlot.DLS.Avalonia` 移植。XAML 1 個（982 行）+ code-behind 2167 行を完全実装、xlsx 読み込み・キュムラント解析・Stokes-Einstein 計算・セッション保存復元まで WPF 版と同形（commit `8854311` / `041f2d2`）
- **Batch 4** ✅: `GPC_Visualization.Avalonia` 移植。XAML 928 行 + code-behind 3131 行を完全実装、CSV/TXT 読み込み・較正曲線適用・分子量変換・統計 chip まで稼働（commit `f25d49e` / `e32f9e9`）
- **Batch 5** ✅: `Spectrum_Visualization.Avalonia` 移植。XAML 5 個（最大 MainWindow 1431 行）+ code-behind 計 4849 行を完全実装、JASCO TXT 読み込み・線スタイル・軸書式・X 反転 / Y 表示モード・IR ピーク帰属・λmax / Tc 自動 + 手動検出・温度スキャン Tc（4 method + sigmoid fit）・積分領域・Beer-Lambert 検量線エディタまで稼働（commit `117e08e` / `76f5292`）
- **Batch 6** （次）: macOS / Linux self-contained publish と動作検証。`dotnet publish src/LabPlot.Shell.Avalonia -r osx-arm64` / `-r linux-x64` で配布物を生成、起動・サンプル読み込み・PNG 出力・セッション保存復元を OS 横断で確認。Linux は WSL2 + WSLg（Windows 11 標準）で実機相当のチェック、macOS は GitHub Actions の `macos-latest` ランナーで起動スモークまで自動化、本格的な GUI 検証は実機所有者に依頼する運用を想定

CLI / ライブラリ化（`LabPlot.Core` の薄い CLI ラッパーで CSV → PNG / xlsx 変換だけ提供）は補助的な選択肢として引き続き残します。

---

## 6. 既知の制限・改善余地

- **テストカバレッジ**: 各アプリで単体テストはあるが、不正ファイル（ヘッダ欠損・データ行混入）に対する挙動テストの拡充余地あり
- **サンプルデータ**: `samples/` を各装置・測定種ごとに整備。エッジケース（極端に小さい・大きいデータ）の追加も
- **ドキュメント**: スクリーンショット込みの README は GPC が先行整備済み、Spectrum / DLS も同様に整える
- **パフォーマンスベンチマーク**: 体感で重さを感じるケースが具体化したら BenchmarkDotNet で計測

---

## 取り組み順序の参考

おおまかな優先度は以下を想定:

1. **共通基盤化（1）** ✅: `LabPlot.Core` / `LabPlot.Core.Wpf` / `LabPlot.Core.Avalonia` を切り出し済み
2. **LabPlot.DLS 新規開発（2-DLS）** ✅: WPF 版・Avalonia 版とも完了
3. **クロスプラットフォーム（5）** 進行中: Phase 7 Avalonia 移植本体（Batch 1–5）完了、残るは Batch 6 macOS / Linux publish & 動作検証
4. **Spectrum 残課題（2-Spectrum）**: ブランク差し引き・濃度逆算・Boltzmann fit など、利用ニーズに合わせて随時
5. **新規フォーマット対応（3）**: 共同研究者・研究室メンバーの要望が具体化したら
6. **GPC パフォーマンス最適化（2-GPC）**: 体感で困るケースが出てきたら
7. **新規アプリ候補（4）**: 必要が具体化してから
