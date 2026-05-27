# LabPlot Roadmap

LabPlot 全体の今後の機能追加・拡張計画をまとめたメモです。優先度や着手時期は流動的で、必要が具体化したものから順次着手する方針です。

最終更新: 2026-05-26（**v1.3.3 で macOS UX 細部と CI 自動化**。Cmd+O 系の OS 別出し分け、ファイルダイアログ既定パスの `~/Documents` フォールバック、macOS App メニュー (About / Preferences / Quit / Cmd+,) と `dotnet run` 経路の Dock アイコンを整え、`v*` タグ push で 3 platform を自動 publish + GitHub Release 化する Actions ワークフローを導入。直前 v1.3.2 で macOS first-class (.app バンドル + codesign + notarytool パイプライン)、v1.3.1 で DLS 溶媒プリセット / Window 状態永続化 / 不正入力 toast、v1.3.0 で DLS AnalysisWindow 4 タブ化 + 横断 UI polish を完了済み）

---

## 1. 共通基盤化（短期）

GPC・Spectrum・DLS の 3 アプリで共通する解析ロジック・UI 部品を切り出し、保守と一貫性を担保する。

- **`LabPlot.Core`**: 書式設定（`GraphFormattingConfig`）、セッション保存、PNG / SVG / Excel / CSV エクスポート、ScottPlot セットアップ補助、JASCO / LabSolutions / Zetasizer 等のリーダー抽象化（`ISpectrumDataReader` 系）。UI 非依存とし、xUnit でテスト容易に
- **`LabPlot.Core.Avalonia`**: **主流系統**の共有 ResourceDictionary（`Themes/CommonStyles.axaml` + `Themes/ImplicitStyles.axaml`）、AxisRange / GraphFormat / ColorPicker / CustomTitleBar / Banner 群、IStorageProvider 経由のヘルパなど
- **`LabPlot.Core.Wpf`**: v1.0.x 保守用の共有 ResourceDictionary（`Themes/CommonStyles.xaml`）、ScottPlot ホストヘルパ、データセットのドラッグ並び替え支援、共通ダイアログなど。Core.Avalonia と同シグネチャの API 群を維持

切り出しは GPC / Spectrum を一気に書き換えるのではなく、対象を 1 種類ずつ移して両アプリのビルドが通る状態を維持しながら進める方針。DLS は最初から `LabPlot.Core` ベースで開発する。

---

## 2. アプリ別の今後の機能

### LabPlot.GPC

- **パフォーマンス最適化**: LabSolutions TXT は数万点規模になることがあり、重ね描き枚数が増えると描画更新が重くなる場合がある。`src/LabPlot.GPC/GpcAnalyzer.Benchmarks` (BenchmarkDotNet 0.14.0) を導入してパーサのベースラインを取得済み — Apple M5 で 1k 点 96 μs / 10k 点 1.05 ms / 50k 点 8.05 ms、アロケーションは 50k 点で約 12.5 MB。重ね描き 5 dataset でも parse 合計 ~40 ms と人間の知覚閾値内のため、**主因はパーサではなく `MainWindow.Plot.cs` の `Plot.Clear()` → 全 dataset 再追加パスの可能性が高い**。改善優先順は (a) `Plot.Clear` → plottable リサイクル (`MainWindow.Plot.cs` L379 / L432)、(b) `CsvGpcDataReader.SplitLooseColumns` の `Regex.Split` 廃止 (アロケーション削減)、(c) 複数ファイル並列読み込み (`MainWindow.axaml.cs` L705 の LINQ 直列を `Parallel.ForEach` 化)
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

## 5. クロスプラットフォーム展開（Phase 7、Avalonia 主流化）

WPF + win-x64 single-file exe では macOS / Linux ユーザーに配れないので、2026-05-07 から **Avalonia UI への並行移植** に着手。Phase 7 Batch 1–6 の完了と実機検証を経て、2026-05-08 に **Avalonia 版を主流系統に切り替え**ました。

切り替え後の建付け: 主流系統は `LabPlot.Shell.Avalonia` / `LabPlot.GPC.Avalonia` / `LabPlot.Spectrum.Avalonia` / `LabPlot.DLS.Avalonia` / `LabPlot.Core.Avalonia` の 5 プロジェクトで Windows / macOS / Linux 共通バイナリを生成します。既存の WPF プロジェクト（`LabPlot.Shell` / `GPC_Visualization` / `Spectrum_Visualization` / `LabPlot.DLS` / `LabPlot.Core.Wpf`）は v1.0.x の保守版として並行維持し、研究室内で運用中の Windows ユーザーが望めば従来構成も使い続けられます。両系統が同じ `LabPlot.Core` / `*Analyzer.Core` を参照する構造なので、ロジック層の修正は二重化なしで両系統に反映できます。新機能・バグ修正は主流の Avalonia 版を優先し、必要なときだけ保守用の WPF 版にバックポートする運用です。

採用バージョン:

- .NET 10
- Avalonia 11.3.14（11 系最新安定版。Avalonia 12 は ScottPlot.Avalonia がまだ追従していないため見送り）
- ScottPlot.Avalonia 5.1.58（既存 ScottPlot.WPF 5.1.58 と同番号）
- Avalonia.Themes.Fluent 11.3.14、Avalonia.Controls.DataGrid 11.3.13（本体より 1 リビジョン下が NuGet 最新）

進捗:

- **Phase 7 (2026-05-07 〜 2026-05-08)** ✅: WPF → Avalonia への並行移植 → 主流化。採用バージョン確定 (.NET 10 / Avalonia 11.3.14 / ScottPlot.Avalonia 5.1.58) のあと、`LabPlot.Core.Avalonia` + `LabPlot.Shell.Avalonia` + 3 モジュール (DLS / GPC / Spectrum) を Batch 1〜5 で完全移植し、Batch 6 で Windows 実機検証。Avalonia 版を主流系統に切り替え、後始末 Batch 7a で外部ファイル D&D を新 API へ、7e で 40 件の `{ReflectionBinding}` を `{CompiledBinding}` に格上げ。WPF 版は v1.0.x 保守版として並行維持
- **v1.3.0 (2026-05-25)** ✅: DLS AnalysisWindow を 4 タブ (cumulant / ramp / series / CONTIN) に再構成、NNLS ベース粒径分布インバータ、データ処理の正則性スイープ、status bar / toast / F1 cheat-sheet / recent-files menu / 結果コピー / アニメーション読み出しなど横断 UI polish、`docs/user-guide/` 初版
- **v1.3.1 (2026-05-25)** ✅: DLS 溶媒プリセット (9 種 × 5 温度の n / η テーブル + 線形補間)、Window 状態永続化 (4 ウィンドウ × 位置 / サイズ / 最大化)、不正入力 toast in DLS metadata editor、recent-files ComboBox 右クリックで履歴クリア、cross-module refactor sweep (GPC ~640 行削減)
- **v1.3.2 (2026-05-26)** ✅: macOS first-class support。`dotnet publish -r osx-arm64` / `-r osx-x64` で `.app` バンドル自動生成、Apple Silicon 実機 smoke test 完走、`scripts/publish-macos.sh` で `dotnet publish` → deep codesign → ditto zip → `xcrun notarytool --wait` → `xcrun stapler` まで 1 コマンド化、Hardened Runtime 用 entitlements.plist 同梱、`docs/macOS_開発環境構築.md` 整備。併せてプロット残存 / 凡例最上段見切れ / AnalysisWindow 最小化不可 / Z-average ベースラインずれの 4 バグを修正
- **v1.3.3 (2026-05-26)** ✅: macOS UX 細部と CI 自動化。Cmd+O / Cmd+S 系を `KeyboardShortcuts.HasCommandModifier` で OS 別に出し分け、F1 cheat-sheet と ToolTip も "Cmd +" 表記に動的差し替え、ファイルダイアログ既定パスを `FormattingDefaultsStore.GetEffectiveDefaultOutputDirectory` で macOS のみ `~/Documents` フォールバック。`<NativeMenu.Menu>` で macOS アプリメニュー (About / Preferences / Quit / Cmd+,) を整備、`dotnet run` 経路でも Dock アイコンが出るよう `NSApp.setApplicationIconImage:` を objc_msgSend で叩く。`.github/workflows/release.yml` + `scripts/publish-all-platforms.sh` で `v*` タグ push をトリガに 3 platform publish + CHANGELOG 抜き出し + GitHub Release 化を自動化

残課題:

- **macOS Developer ID 加入後の codesign + notarytool 実機検証 → 正式署名リリース**: `scripts/publish-macos.sh` は dry-run 検証済み。Apple Developer Program 加入 + Developer ID Application 証明書取得 + app-specific password 発行で end-to-end 通せる状態
- **osx-x64 (Intel Mac) を release pipeline に追加**: csproj の `.app` バンドル target は既に `osx-x64` も対応済み。`.github/workflows/release.yml` の publish スクリプトを RID 配列にして osx-x64 を加えれば自動 publish される
- **macOS arm64 publish の起動スモーク CI 化** (GitHub Actions `macos-latest` ランナー) — リリース workflow とは別系統で、PR トリガで起動テストする回帰防止用
- **Dock メニュー (右クリック) の整備**: Avalonia は `applicationDockMenu:` の窓口を持たないので NSApplicationDelegate のサブクラス化 + objc 経由の登録が必要。App メニュー本体は v1.3.3 で対応済み
- **WSL2 + WSLg での Linux x64 publish 実機相当検証の手順 docs 化**
- **GitHub Actions の Node.js 20 → 24 移行**: `actions/checkout@v4` / `setup-dotnet@v4` / `upload-artifact@v4` が Node.js 20 で動いている。2026-06-02 以降にデフォルトが 24 に切り替わるので、各 action の `@v5` がリリースされ次第追従

CLI / ライブラリ化（`LabPlot.Core` の薄い CLI ラッパーで CSV → PNG / xlsx 変換だけ提供）は補助的な選択肢として引き続き残します。

---

## 6. 既知の制限・改善余地

- **テストカバレッジ**: 各アプリで単体テストはあるが、不正ファイル（ヘッダ欠損・データ行混入）に対する挙動テストの拡充余地あり
- **サンプルデータ**: `samples/` を各装置・測定種ごとに整備。エッジケース（極端に小さい・大きいデータ）の追加も
- **ドキュメント**: スクリーンショット込みの README は GPC が先行整備済み、Spectrum / DLS も同様に整える
- **macOS Dock メニュー**: 右クリックで「最近開いたファイル」を出す等、Avalonia 11.3 の標準 API には窓口がないので NSApplicationDelegate サブクラス化が必要。App メニュー本体 / Cmd 系ショートカット / ファイルダイアログ既定パスは v1.3.3 で対応済み
- **パフォーマンスベンチマーク**: 体感で重さを感じるケースが具体化したら BenchmarkDotNet で計測

---

## 取り組み順序の参考

おおまかな優先度は以下を想定:

1. **共通基盤化（1）** ✅: `LabPlot.Core` / `LabPlot.Core.Avalonia`（主流） / `LabPlot.Core.Wpf`（保守）を切り出し済み
2. **LabPlot.DLS 新規開発（2-DLS）** ✅: 主流・保守の両系統とも完了
3. **クロスプラットフォーム（5）** ✅: Phase 7 + v1.3.2 macOS first-class + v1.3.3 で macOS UX 細部 + CI 自動 Release まで完了
4. **Apple Developer Program 加入後の正式署名リリース**: 加入後に `scripts/publish-macos.sh` を実走、`spctl --assess` が "Notarized Developer ID" を返すことを確認、v1.3.x patch or v1.4.0 で正式署名版を出し直す
5. **Spectrum 残課題（2-Spectrum）**: ブランク差し引き・濃度逆算・Boltzmann fit など、利用ニーズに合わせて随時
6. **新規フォーマット対応（3）**: 共同研究者・研究室メンバーの要望が具体化したら（JCAMP-DX が汎用性最大）
7. **GPC パフォーマンス最適化（2-GPC）**: 体感で困るケースが出てきたら
8. **新規アプリ候補（4）**: 必要が具体化してから
