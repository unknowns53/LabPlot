# LabPlot.Core.Avalonia

LabPlot の **主流系統** UI 基盤ライブラリです。Avalonia 11.3.x と ScottPlot.Avalonia 5.1.x の上に、3 アプリ（GPC / Spectrum / DLS）が共有する UserControl・スタイル・ヘルパを集約しています。`net10.0` ターゲットなので Windows / macOS / Linux で同一バイナリが動きます。

> 保守用の WPF 系統には [`LabPlot.Core.Wpf`](../LabPlot.Core.Wpf/) があり、API は本ライブラリと同シグネチャを維持しています。新機能の追加先は本ライブラリです。

含まれる主なコンポーネント:

- `Themes/CommonStyles.axaml` + `Themes/ImplicitStyles.axaml` — Section / Expander / TextBox / ComboBox / CheckBox / Button / IconButton 等の共通 ControlTheme と Style。Core.Wpf 側の `CommonStyles.xaml` と同じデザイントークン（アクセント色 `#2563EB` 系、ホバー `#1D4ED8`、押下 `#1E3A8A`、無効 `#CBD5E1`、角丸 6〜8 px、自前 FocusVisual）で揃えています。`CommonStyles.axaml` は `<x:Key>` 付きの ControlTheme を、`ImplicitStyles.axaml` は暗黙適用される `Style Selector` を担当します。
- `Controls/AxisRangePanel` — X/Y min・max のテキストボックスと「自動範囲に戻す」ボタンを 1 セットにした再利用パネル。空欄＝自動レンジ、値ありで固定窓。`AxisRangeCommitted` イベントでホスト側がプロット更新を呼びます。
- `Controls/ColorPickerPanel` — 名前付きプリセット（"Auto", "Indigo", "Crimson" …, "Custom"）の ComboBox とプレビュースウォッチを 1 行に並べ、"Custom" 選択時だけ hex 入力 + HSV パレットを展開します。`AllowAuto` を立てると "Auto (palette)" 項目を出します。
- `Controls/GraphFormatPanel` — フォント／目盛り／枠＆グリッド／背景／凡例位置の Expander を 1 つにまとめたサブパネル。`GraphFormattingConfigBase` の Capture / Apply に対応した依存プロパティを公開しています。
- `Controls/CustomTitleBar` — `ExtendClientAreaToDecorationsHint=True + ExtendClientAreaChromeHints=NoChrome` で消した OS タイトルバーを自前の chrome として再現するコントロール。最小化／最大化／閉じる + ドラッグ移動を実装。
- `Controls/ErrorBanner` / `SuccessBanner` / `WarningBanner` / `BusyOverlay` — 共通バナー / オーバーレイ群。`ShowError` / `ShowSuccess` / `ShowWarning` の入り口として使えます。
- `Helpers/FormattingDefaultsStore` — `%AppData%` / `~/.local/share` / `~/Library/Application Support` 配下の `formatting_config.json` 永続化ヘルパ。Core.Wpf 版と同シグネチャの Load / Save / Clone / GetExistingDefaultOutputDirectory を提供。
- `Helpers/DragGhostController` — ListBox 並べ替え用ゴーストの DataTemplate ベース実装。`DataTemplate.Build(dataContext)` でベクター描画クローンを生成し、`OverlayLayer` 上に表示します。Skia の `RenderTargetBitmap` を使う bitmap 方式と違いサブピクセルアンチエイリアスが効くので、文字がぼやけません。
- `FormatHelpers` — 数値パース、不変カルチャ整形、hex カラー正規化、ComboBox の `Tag` 読み書きの static ユーティリティ。`HexToAvaloniaColor` など Avalonia 用 helper を含みます。

---

## v1.0.x WPF 版との API 差分（実装メモ）

主要な置換ルール:

- ファイルダイアログ: `Microsoft.Win32.OpenFileDialog` / `SaveFileDialog` → `TopLevel.GetTopLevel(this).StorageProvider.OpenFilePickerAsync` / `SaveFilePickerAsync`（async、戻り値は `IStorageFile`）
- ScottPlot 接続: `ScottPlot.WPF.WpfPlot` → `ScottPlot.Avalonia.AvaPlot`（`Plot.Add` 系 API は同一）
- 入力イベント: `PreviewMouseLeftButtonDown` / `Mouse.Capture` → `AddHandler(PointerPressedEvent, ..., RoutingStrategies.Tunnel)` / `e.Pointer.Capture(target)`
- 表示制御: `Visibility.Visible / Collapsed` → `IsVisible`（bool）
- アニメーション: `Storyboard` + `DoubleAnimation` → `Style.Animations` + `Animation` Duration + KeyFrame
- カラー / ブラシ: `System.Windows.Media.Color` / `Brush` → `Avalonia.Media.Color` / `IBrush`（`Color.FromRgb` は同名で存在）
- リソース: `<Style TargetType="Button">` → `<ControlTheme x:Key="..." TargetType="Button">`、状態は `:pointerover` / `:pressed` / `:focus` / `:disabled` 擬似クラス
- Adorner: 廃止（Avalonia には対応 API が無い）。drop indicator 等は AXAML sibling Grid + `TranslatePoint` + Margin.Top 方式に置換
- DataGrid: `Avalonia.Controls.DataGrid 11.3.13`（本体より 1 リビジョン下が NuGet 最新）+ Window.Styles に `<StyleInclude Source="avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml" />`

フォント: WPF の `Segoe UI, Yu Gothic UI, Meiryo UI` 直指定は維持。macOS / Linux で見つからないときは Avalonia の `FontManager` が自動フォールバックします。Linux 配布時は `fonts-noto-cjk` をパッケージ依存に含めるのを推奨。

---

## 使い方

`App.axaml` で共通スタイルをマージします:

```xml
<Application.Styles>
    <FluentTheme />
    <StyleInclude Source="avares://LabPlot.Core.Avalonia/Themes/CommonStyles.axaml" />
    <StyleInclude Source="avares://LabPlot.Core.Avalonia/Themes/ImplicitStyles.axaml" />
</Application.Styles>
```

各アプリの `MainWindow.axaml` では、ネームスペースを取り込んで共通コントロールを直接使えます:

```xml
<Window xmlns:controls="using:LabPlot.Core.Avalonia.Controls"
        ...>
    <controls:AxisRangePanel x:Name="AxisRangePanel"
                             AxisRangeCommitted="AxisRangePanel_AxisRangeCommitted" />
    <controls:GraphFormatPanel x:Name="GraphFormatPanel" />
    <controls:ColorPickerPanel x:Name="LineColorPicker"
                               AllowAuto="True"
                               ColorChanged="LineColorPicker_ColorChanged" />
</Window>
```

---

## ファイル構成

```text
Themes/CommonStyles.axaml             共通 ControlTheme（Section, Expander, Button, TextBox, ComboBox …）
Themes/ImplicitStyles.axaml           暗黙適用の Style Selector
Controls/AxisRangePanel.axaml(.cs)    X/Y min・max + 自動レンジ復帰ボタン
Controls/ColorPickerPanel.axaml(.cs)  プリセット + Custom HSV / hex の 1 行ピッカ
Controls/GraphFormatPanel.axaml(.cs)  フォント・目盛り・枠＆グリッド・背景・凡例の共通サブパネル
Controls/CustomTitleBar.axaml(.cs)    自前 chrome（OS タイトルバー消去時）
Controls/ErrorBanner.axaml(.cs)       エラーバナー（Success / Warning も同形）
Controls/BusyOverlay.axaml(.cs)       処理中オーバーレイ
Helpers/DragGhostController.cs        ListBox 並べ替え用ベクター描画ゴースト
Helpers/FormattingDefaultsStore.cs    formatting_config.json 永続化（OS 別パス対応）
FormatHelpers.cs                      数値パース・hex 正規化・ComboBox Tag ヘルパ
```

---

## 開発者向け

ビルド:

```powershell
dotnet build src/LabPlot.Core.Avalonia/LabPlot.Core.Avalonia.csproj
```

依存:

- `LabPlot.Core` — 書式設定の基底（`GraphFormattingConfigBase`）と ScottPlot ヘルパ（`PlotAppearance`）
- `Avalonia 11.3.14` / `Avalonia.Themes.Fluent 11.3.14`
- `ScottPlot.Avalonia 5.1.58`
- `Avalonia.Controls.DataGrid 11.3.13`（DataGrid を使うアプリ向け）

ターゲット: `net10.0`（Windows / macOS / Linux 共通）。
