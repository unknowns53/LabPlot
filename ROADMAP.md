# LabPlot Roadmap

LabPlot 全体の今後の機能追加・拡張計画をまとめたメモです。優先度や着手時期は流動的で、必要が具体化したものから順次着手する方針です。

最終更新: 2026-05-26（**v1.3.2 で macOS first-class 化**。Apple Silicon Mac での実機 smoke test を経て、`.app` バンドルを `dotnet publish` から自動生成 + `scripts/publish-macos.sh` で codesign + notarytool + stapler を 1 コマンドにまとめた。これ以前に v1.3.0 で DLS AnalysisWindow を 4 タブに再構成 + 横断的な UI polish、v1.3.1 で DLS 溶媒プリセット / Window 状態永続化 / 不正入力 toast / recent-files 履歴クリア UI / ~640 行の GPC リファクタリング、v1.3.2 で macOS 由来でないプロット残存バグと凡例最上段見切れも併せて修正済み）

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

## 5. クロスプラットフォーム展開（Phase 7、Avalonia 主流化）

WPF + win-x64 single-file exe では macOS / Linux ユーザーに配れないので、2026-05-07 から **Avalonia UI への並行移植** に着手。Phase 7 Batch 1–6 の完了と実機検証を経て、2026-05-08 に **Avalonia 版を主流系統に切り替え**ました。

切り替え後の建付け: 主流系統は `LabPlot.Shell.Avalonia` / `LabPlot.GPC.Avalonia` / `LabPlot.Spectrum.Avalonia` / `LabPlot.DLS.Avalonia` / `LabPlot.Core.Avalonia` の 5 プロジェクトで Windows / macOS / Linux 共通バイナリを生成します。既存の WPF プロジェクト（`LabPlot.Shell` / `GPC_Visualization` / `Spectrum_Visualization` / `LabPlot.DLS` / `LabPlot.Core.Wpf`）は v1.0.x の保守版として並行維持し、研究室内で運用中の Windows ユーザーが望めば従来構成も使い続けられます。両系統が同じ `LabPlot.Core` / `*Analyzer.Core` を参照する構造なので、ロジック層の修正は二重化なしで両系統に反映できます。新機能・バグ修正は主流の Avalonia 版を優先し、必要なときだけ保守用の WPF 版にバックポートする運用です。

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
- **Batch 6** ✅: Windows 実機での 3 アプリ動作検証で Expander / ColorPicker / Avalonia.Generators 系の実装課題 3 件を fix、ファイル D&D / 凡例 D&D / 太字でないフォントの読みづらさも順次解決。GPC のドラッグ並べ替えを DataTemplate ベースのゴースト + InputHitTest 方式に整理して DLS / Spectrum へ横展開。`dotnet build` で MSBuild worker / Roslyn server が常駐する亡霊プロセス問題は `tools/run-avalonia.ps1` の `-nodeReuse:false /p:UseSharedCompilation=false` で根治
- **Phase 7 主流化（2026-05-08）** ✅: Avalonia 版を主流系統に切り替え。README / ROADMAP / 各アプリドキュメントを「主流 = Avalonia、保守 = WPF」の建付けに書き換え
- **Phase 7 後始末 Batch 7a（2026-05-08）** ✅: 3 アプリの外部ファイル D&D ハンドラを Avalonia 11.3 の新 API（`DragEventArgs.DataTransfer` / `DataFormat.File` / `IAsyncDataTransfer.TryGetFilesAsync`）に移行、`#pragma warning disable CS0618` 対 3 組を削除。DLS の `OnDatasetDrop` も `async void` に揃えた
- **Phase 7 後始末 Batch 7e（2026-05-08）** ✅: 3 アプリ MainWindow + Spectrum CalibrationCurveWindow の AXAML を `{ReflectionBinding}` × 40 件 → `{CompiledBinding}` に格上げ。各 ItemTemplate / DataGrid に `x:DataType="vm:Window+Vm"` を付与し、Spectrum の `ManualLambdaMaxEntryVm` / `ManualIrPeakEntryVm` を `private` → `internal` に昇格
- **v1.3.0（2026-05-25）** ✅: DLS AnalysisWindow を 4 タブ（cumulant / ramp / series / CONTIN）に再構成、NNLS ベース粒径分布インバータ、データ処理の正則性スイープ、status bar / toast / F1 cheat-sheet / recent-files menu / 結果コピー / アニメーション読み出しなど横断 UI polish、`docs/user-guide/` 初版
- **v1.3.1（2026-05-25）** ✅: DLS 溶媒プリセット（9 種 × 5 温度の n / η テーブル + 線形補間）、Window 状態永続化（4 ウィンドウ × 位置 / サイズ / 最大化）、不正入力 toast in DLS metadata editor、recent-files ComboBox 右クリックで履歴クリア、cross-module refactor sweep（GPC ~640 行削減）
- **v1.3.2（2026-05-26）** ✅: macOS first-class support。`dotnet publish -r osx-arm64` / `-r osx-x64` で `.app` バンドル自動生成（Info.plist / .icns / Contents/MacOS 配置）、Apple Silicon 実機 smoke test 完走、`scripts/publish-macos.sh` で `dotnet publish` → deep codesign → ditto zip → `xcrun notarytool --wait` → `xcrun stapler` までを 1 コマンド化（資格情報は env 経由）、Hardened Runtime 用 entitlements.plist 同梱、`docs/macOS_開発環境構築.md` 整備。併せて発見した既存バグ 2 件（プロット残存 / 凡例最上段見切れ）と macOS 固有 2 件（AnalysisWindow 最小化 / Z-average ベースラインずれ）を修正

残課題（主流化後に着手）:

- **GitHub Actions による 3 platform publish の自動化**: タグ push をトリガーに `win-x64` / `osx-arm64` / `linux-x64` の self-contained single-file を作成し、Release zip 添付まで。現状は手動 `dotnet publish` を 3 回回している（v1.3.2 リリース時の運用）
- **macOS Developer ID 加入後の codesign + notarytool 実機検証 → 正式署名リリース**: `scripts/publish-macos.sh` は dry-run 検証済み。Apple Developer Program 加入 + Developer ID Application 証明書取得 + app-specific password 発行で end-to-end 通せる状態
- **osx-x64 (Intel Mac) を release pipeline に追加**: csproj の `.app` バンドル target は既に `osx-x64` も対応済み。あとは CI / リリース手順に matrix 追加するだけ
- **macOS arm64 publish の起動スモーク CI 化**（GitHub Actions `macos-latest` ランナー）— PR #1 で実機検証は完了したので、回帰防止の自動化フェーズに移行
- **アプリメニューバー (`NSMenu`) / Dock メニューの整備**: Cmd+Q / Preferences / About の標準受け口は macOS だけアプリメニュー側にぶら下げる慣習。現状未実装
- **WSL2 + WSLg での Linux x64 publish 実機相当検証の手順 docs 化**

CLI / ライブラリ化（`LabPlot.Core` の薄い CLI ラッパーで CSV → PNG / xlsx 変換だけ提供）は補助的な選択肢として引き続き残します。

---

## 6. 既知の制限・改善余地

- **テストカバレッジ**: 各アプリで単体テストはあるが、不正ファイル（ヘッダ欠損・データ行混入）に対する挙動テストの拡充余地あり
- **サンプルデータ**: `samples/` を各装置・測定種ごとに整備。エッジケース（極端に小さい・大きいデータ）の追加も
- **ドキュメント**: スクリーンショット込みの README は GPC が先行整備済み、Spectrum / DLS も同様に整える
- **macOS UX 細部**: アプリメニューバー（"About LabPlot" / "Preferences..." を `NSMenu` 経由で macOS 慣習に揃える）/ Dock メニュー右クリック対応が未整備。`Cmd+` 系ショートカットとファイルダイアログ既定パスは feature/macos-ux-shortcuts-and-paths で対応済み
- **CI / リリース自動化**: 現状はリリースのたびに手動で 3 回 `dotnet publish` → `zip` / `ditto` → `gh release create` を走らせている。GitHub Actions matrix で自動化したい
- **パフォーマンスベンチマーク**: 体感で重さを感じるケースが具体化したら BenchmarkDotNet で計測

---

## 取り組み順序の参考

おおまかな優先度は以下を想定:

1. **共通基盤化（1）** ✅: `LabPlot.Core` / `LabPlot.Core.Avalonia`（主流） / `LabPlot.Core.Wpf`（保守）を切り出し済み
2. **LabPlot.DLS 新規開発（2-DLS）** ✅: 主流・保守の両系統とも完了
3. **クロスプラットフォーム（5）** ✅: Phase 7 Batch 1–6 + v1.3.2 で macOS first-class（`.app` バンドル / codesign + notarytool パイプライン / 実機 smoke test）まで完了
4. **macOS UX 細部**: `Cmd+O` / `Cmd+S` ショートカット対応とファイルダイアログ既定パスの macOS 対応は feature/macos-ux-shortcuts-and-paths で着手中。残るはアプリメニューバー / Dock メニュー整備
5. **CI / リリース自動化**: GitHub Actions matrix で 3 platform publish を自動化、タグ push で Release zip 添付まで自動。回帰防止と運用負荷削減の両面で効く
6. **Apple Developer Program 加入後の正式署名リリース**: 加入後に `scripts/publish-macos.sh` を実走、`spctl --assess` が "Notarized Developer ID" を返すことを確認、v1.3.x の patch 番号 or v1.4.0 で正式署名版を出し直す
7. **Spectrum 残課題（2-Spectrum）**: ブランク差し引き・濃度逆算・Boltzmann fit など、利用ニーズに合わせて随時
8. **新規フォーマット対応（3）**: 共同研究者・研究室メンバーの要望が具体化したら（JCAMP-DX が汎用性最大）
9. **GPC パフォーマンス最適化（2-GPC）**: 体感で困るケースが出てきたら
10. **新規アプリ候補（4）**: 必要が具体化してから
