# UI ブラッシュアップ 続編の手順書

3 アプリ（GPC / Spectrum / DLS）の UI 磨き込みを段階的に進めるための作業ノート。

## これまでに済んだこと

直近 4 commit でグローバルコントロール、サイドバーの情報階層、プロット領域、状態フィードバックの土台が入った。新規の作業はこの上に積む形で進める。

| commit | 内容 |
|---|---|
| `8b6d265` | 薄い ScrollBar / 角丸 ToolTip / TextBox・ComboBox の FocusRing / CheckBox のストローク描画アニメ / Expander のスライドアニメ |
| `a767c24` | `GroupHeaderStyle`（ContentControl + 1 px divider）/ サイドバーヘッダの 36 × 36 アプリアイコン / sticky footer の上向き DropShadow |
| `b396ce3` | プロットスケルトン + シマー / info バッジの Path 化 / GPC 統計の chip 化（`SetStatisticsLine` ヘルパー + 複数行 fallback） |
| `f278d1c` | Success / Warning / Error の 12 個の SolidColorBrush / `SuccessBanner` / `WarningBanner`（API は ErrorBanner と同じ `Show(string)` / `Hide()`） |

## 進め方の決まり

各バッチは「実装 → 全 4 プロジェクト build → 個別 `git add` → 件名英文・本文 what + why の 1 commit」の単位で閉じる。複数バッチを 1 commit に詰めない。

ビルド検証コマンド（PowerShell）:

```powershell
dotnet build src\LabPlot.Core.Wpf\LabPlot.Core.Wpf.csproj -nologo -v minimal
dotnet build src\LabPlot.GPC\GPC_Visualization\GPC_Visualization.csproj -nologo -v minimal
dotnet build src\LabPlot.Spectrum\Spectrum_Visualization\Spectrum_Visualization.csproj -nologo -v minimal
dotnet build src\LabPlot.DLS\LabPlot.DLS\LabPlot.DLS.csproj -nologo -v minimal
```

警告が出たら必ず潰す。Core.Wpf に手を入れたら 4 プロジェクト全部、特定アプリだけなら該当アプリと Core.Wpf でいい。

コミットフォーマット（メモリと既存 commit 参照）:

- 件名: 英文・短く・命令形でなくても可（例: `Polish global controls in Core.Wpf CommonStyles`）
- 本文: 何を変えたか / なぜ変えたか の順。箇条書きで具体的に。
- フッター: `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>`

## 既存リソースの再利用

新規 XAML を書く前に必ず確認する。色や角丸はインライン hex を避け、CommonStyles の既存値を踏襲する。

主要キー（`src/LabPlot.Core.Wpf/Themes/CommonStyles.xaml`）:

- レイアウト: `SectionStyle` / `SectionHeaderStyle` / `GroupLabelStyle` / `GroupHeaderStyle` / `FieldLabelStyle`
- 入力: `InputTextBoxStyle` / `InputComboBoxStyle`（FocusRing 入り）
- ボタン: `PrimaryButtonStyle` / `SecondaryButtonStyle` / `IconButtonStyle`
- ScrollBar / ToolTip / Expander / CheckBox は名前なしで全アプリ既定
- 色 Brush: `SuccessBrush` / `SuccessForegroundBrush` / `SuccessBackgroundBrush` / `SuccessBorderBrush` ほか Warning / Error 各 4 セット

主要コントロール（`src/LabPlot.Core.Wpf/Controls/`）:

- `ErrorBanner` / `SuccessBanner` / `WarningBanner` — `Show(string)` / `Hide()`
- `AxisRangePanel` / `ColorPickerPanel` / `GraphFormatPanel`

アプリ独自アクセント色（既存）: `#2563EB`（hover `#1D4ED8`、押下 `#1E3A8A`、無効 `#CBD5E1`）

---

## バッチ A: Section アイコン（推奨優先度 1）

**狙い:** サイドバーの 8〜10 個の Expander ヘッダにテキストだけが並んでいる状態を解消し、視線を縦に滑らせたときの目印を作る。

### 対象セクションと候補アイコン

各 Expander の `Header="..."` の左に、14 × 14 の Path グリフを置く。Stroke は `#475569`、StrokeThickness 1.5、StrokeStartLineCap=Round、Fill=Transparent。Stretch=None で viewBox 0–14 の座標系で描く。

| ヘッダー文言 | 想定アイコン |
|---|---|
| データファイル | 折れ角つき書類: `M 2,1 L 9,1 L 12,4 L 12,13 L 2,13 Z M 9,1 L 9,4 L 12,4` |
| 読み込み済みデータセット | 横 3 本リスト: `M 2,3 L 12,3 M 2,7 L 12,7 M 2,11 L 12,11` |
| 線スタイル | 斜めペン: `M 2,12 L 10,4 L 12,6 L 4,14 Z` |
| 較正曲線と分子量 | 較正カーブ: `M 2,12 Q 6,12 7,7 Q 8,2 12,2` + 小さな点 2〜3 個 |
| 軸範囲 | グリッド 田: `M 1,1 L 13,1 L 13,13 L 1,13 Z M 7,1 L 7,13 M 1,7 L 13,7` |
| グラフラベル | タグ形: `M 2,7 L 7,2 L 13,2 L 13,8 L 8,13 L 2,7 Z` |
| グラフ書式 | ブラシ: 柄と毛先の単純化シルエット |
| 解析条件 | フロッピー: `M 2,2 L 12,2 L 12,12 L 2,12 Z` + 内側のラベル矩形 |
| 環境設定 | 歯車（簡略）: 8 角 + 中央円 |
| （Spectrum）波長スキャン解析（λmax） | ピーク + 矢印: 既存 app glyph と被らないように山型のみ |
| （DLS）平均粒径 / 解析設定 | 横ヒストグラム |

### 実装ステップ

1. `Expander.Header` をテキスト 1 行から `<StackPanel Orientation="Horizontal">` + `<Path>` + `<TextBlock>` に書き換え。
2. アイコン色は `#475569`（FieldLabelStyle と同じ slate-600）。Expander が hover されたときに `#2563EB` に変わるトリガを CommonStyles の Expander スタイル側で追加する（`Path.Stroke` を Trigger で書き換え）。
3. 折りたたみ時のシェブロン（既存）と同じレベルにアイコンを置き、視線が「アイコン → 見出し → シェブロン」の順に流れるようにする。
4. 同種の Expander が複数アプリにある場合（例: 軸範囲）はアイコンを統一する。

### 影響ファイル

- `src/LabPlot.GPC/GPC_Visualization/MainWindow.xaml`（9 セクション）
- `src/LabPlot.Spectrum/Spectrum_Visualization/MainWindow.xaml`（10+ セクション、波長スキャン関連あり）
- `src/LabPlot.DLS/LabPlot.DLS/MainWindow.xaml`（8 セクション程度）
- `src/LabPlot.Core.Wpf/Themes/CommonStyles.xaml`（任意、Expander hover 時のアイコン色変化）

### 注意点

- `Expander.Header` を複合コンテンツにすると `FontWeight` / `FontSize` の継承が切れる場合がある。明示的に `<TextBlock FontWeight="SemiBold" FontSize="13" Foreground="#0F172A" />` を付ける。
- アイコンは 1 commit で全アプリ全セクションぶん入れる（中途半端な状態を残さない）。

---

## バッチ B: 空状態の演出（推奨優先度 2）

**狙い:** 初回起動 / データ未読み込みのときに、ユーザーに次のアクションを示す。

### B-1: DatasetList の placeholder 強化

現状: `MainWindow.xaml` の `DatasetListPlaceholder` TextBlock 1 行で「データ未読み込み」とだけ表示。

変更案: 同じ位置に縦並びの空状態パネルを置く。

- 上に上向き矢印 / クラウドの 32 × 32 Path（slate `#94A3B8` Stroke）
- その下に「ここにファイルをドロップ」（FontSize=12, Foreground=`#64748B`）
- さらに下に「または上のボタンから開く」（FontSize=11, Foreground=`#94A3B8`）

`Border` の枠は今の実線から、ドラッグオーバー時のみ点線 + accent `#2563EB` ハイライトに切り替える（バッチ D と連動）。

### B-2: プロットプレースホルダの文言切替

現状: 初期化中もデータ未読み込み時も「グラフを初期化しています...」のまま。

変更案: 状態に応じて 3 通りに切り替える。

| 状態 | 表示文言 | スケルトン |
|---|---|---|
| プロット初期化中 | グラフを初期化しています… | あり（シマー進行） |
| 初期化済み・データなし | CSV / TXT を読み込むとここに表示されます | あり（シマー停止 or 静止スケルトン） |
| 初期化済み・データあり | （表示しない） | なし |
| 初期化失敗 | グラフ表示の初期化に失敗しました。 | なし |

実装: code-behind に `SetPlaceholderState(PlaceholderState state)` のヘルパーを足し、`PlotHost.Children.Clear()` の前後で呼び分ける。`PlaceholderState` は `Initializing` / `EmptyReady` / `InitFailed` の enum。

### 影響ファイル

- 各 `MainWindow.xaml`（プロット領域 + データセットリスト周辺）
- 各 `MainWindow.xaml.cs`（`InitializePlotControl` 周辺、データクリア時の呼び出し追加）

---

## バッチ C: ダイアログ点検（推奨優先度 3）

**狙い:** MainWindow と並列に開く Window が、CommonStyles の現代的な配色から外れていないかを確認・是正する。

### 対象 Window

リポジトリにある独立 Window:

- `src/LabPlot.Spectrum/Spectrum_Visualization/AbsorbanceConfirmDialog.xaml`
- `src/LabPlot.Spectrum/Spectrum_Visualization/CalibrationCurveWindow.xaml`
- 他にも `xaml` で `<Window` を検索: `Grep -n "<Window" --glob "**/*.xaml"`

### チェックリスト

各 Window について次を確認し、抜けていたら埋める。

1. `Background="#F7F8FA"`（MainWindow と統一）
2. `FontFamily="Segoe UI, Yu Gothic UI, Meiryo UI, sans-serif"`
3. `TextOptions.TextFormattingMode="Ideal" TextRenderingMode="ClearType"` `UseLayoutRounding="True"`
4. ボタンが `PrimaryButtonStyle` / `SecondaryButtonStyle` を使っている
5. 入力欄が `InputTextBoxStyle` / `InputComboBoxStyle` を使っている
6. `App.xaml` 経由で CommonStyles がマージされている（独立 Window でも勝手に効くはず、念のため確認）
7. `WindowStartupLocation="CenterOwner"` でオーナーから派生
8. タイトルバーのアイコンを表示するなら `Icon` を設定

### 影響ファイル

- 上記 Window XAML（1〜数ファイル）

---

## バッチ D: ドラッグ＆ドロップの視覚フィードバック（任意）

**狙い:** `DatasetListBox` の並び替えと外部ファイル投入時に、操作位置がはっきり分かるようにする。

### 改善内容

1. **外部ファイル DragOver 時:** リスト全体を囲む Border の枠を、点線 2 px `#2563EB` + 背景 `#DBEAFE` 30 % にハイライト（`DragOver` ハンドラで切替、`DragLeave` / `Drop` で復帰）。
2. **並び替え時の挿入インジケータ:** マウス Y 座標から挿入位置を計算し、対応する ListBoxItem の上端 / 下端に 2 px の `#2563EB` 横ラインを描く。実装は `DatasetListBox` の Adorner レイヤを使う方法が一番きれい。簡易版なら同じレイヤに `Rectangle` を入れて `Margin` を切り替える方式でも可。
3. **ドラッグ中の cursor:** 既存の `Cursor="SizeAll"` のままで OK。

### 影響ファイル

- 各 `MainWindow.xaml` の `DatasetListBox` 周辺
- 各 `MainWindow.xaml.cs` の `DatasetListBox_DragOver` / `_DragLeave` / `_PreviewMouseMove` / `_Drop`

### 注意点

- 並び替えと外部ドロップを区別する: `e.Data.GetDataPresent(DataFormats.FileDrop)` が true なら外部、false なら並び替え。

---

## バッチ E: BusyOverlay（任意）

**狙い:** 大きいデータの読み込み・解析中に UI スレッドが詰まる瞬間に、操作受付中でないことを伝える。

### 実装

`src/LabPlot.Core.Wpf/Controls/BusyOverlay.xaml` + `.xaml.cs` を新設。API は ErrorBanner と同形式。

- ルート: 半透明 `#FFFFFF` 60 % で全面を覆う `Border` + ドロップシャドウなしの中央パネル
- 中央パネルの中身: 円形 Path（`Stroke="#2563EB"`, StrokeThickness=3, StrokeDashArray で 1/4 だけ可視）+ 下にメッセージ TextBlock
- Path に `RotateTransform` を Storyboard で `RepeatBehavior="Forever"`、1 周 1.0 s で回す
- `Show(string message)` / `Hide()`

### 呼び出し側

- 各アプリの長時間操作（CSV パース、`.dat` 読み込み、較正曲線フィット）の前後で `BusyOverlay.Show("読み込み中…")` / `.Hide()` を呼ぶ
- 該当箇所は `OpenCsvButton_Click` 系の async ハンドラ

### 注意点

- パース処理が同期的だと UI スレッドが固まり Storyboard も動かない。`Task.Run` でバックグラウンドに逃がすか、最低でも `await Task.Yield()` で 1 フレーム挟む。

---

## バッチ F: Window chrome カスタム化（大規模・別セッション推奨）

**狙い:** Windows デフォルトのタイトルバー（灰色フレーム + システムボタン）を消し、サイドバーの `#DBEAFE` バッジ + アプリ名 + サブタイトルを画面上端まで持ち上げる。

### 必要な作業

- `WindowChrome.WindowChrome` を `MainWindow` に設定し、`CaptionHeight`、`ResizeBorderThickness`、`GlassFrameThickness` をチューニング
- 自前タイトルバーの `Grid`（高さ 36〜40 px）にアプリアイコン + タイトル + 最小化 / 最大化 / 閉じるボタン（Path グリフ）
- ホバーカラー: 閉じるは `#DC2626`、それ以外は `#E2E8F0`
- 最大化 / 復元の双状態でアイコンを切り替える
- Aero Snap、ドラッグでスナップ解除、DPI 変更時の再描画、システムメニューを保持
- 3 アプリ全部に同じ `CustomChrome` UserControl を適用

### 注意点

- ハマりどころ多数: `WindowState=Maximized` で枠が画面外にはみ出す問題、フォーカス時 / 非フォーカス時の色違い、タッチ環境でのサイズグリップ
- 1 セッション丸ごと使う覚悟で着手する
- 影響範囲が大きいので、まず 1 アプリで完成させてから他 2 アプリへ展開

---

## おすすめの進め方

A → B → C を 1 セッションに詰める想定でちょうど良い分量（推定 commit 4〜5 本）。D / E はそれぞれ 0.5 セッション、F は単独セッション。

各バッチ完了時に `MEMORY.md` の `project_labplot_status.md` から参照できるよう、必要なら状態メモを更新しておく（バッチ完了の事実は git log で十分なので、ステータス変更が伴うときだけ）。
