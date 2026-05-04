# LabPlot.Core.Wpf

LabPlot の WPF アプリ（GPC / Spectrum / DLS）が共有する UI コンポーネント・スタイル・ヘルパをまとめたライブラリです。WPF 依存はここに閉じ込め、`LabPlot.Core` と組み合わせて使う前提で設計しています。

含まれる主なコンポーネント:

- `Themes/CommonStyles.xaml` — Section / Expander / TextBox / ComboBox / CheckBox / Button / IconButton 等の共通 ControlTemplate と Style。`SectionStyle` / `SectionHeaderStyle` / `GroupLabelStyle` / `FieldLabelStyle` / `InputTextBoxStyle` / `InputComboBoxStyle` / `PrimaryButtonStyle` / `SecondaryButtonStyle` / `IconButtonStyle` を `x:Key` で公開しています。各アプリの `App.xaml` でマージするだけで、3 アプリの見た目が揃います。
- `Controls/AxisRangePanel` — X/Y min・max のテキストボックスと「自動範囲に戻す」ボタンを 1 セットにした再利用パネル。空欄＝自動レンジ、値ありで固定窓。Enter / フォーカス離脱 / 自動リセットで `AxisRangeCommitted` イベントが発火するので、ホスト側でプロット更新を呼ぶ実装になります。
- `Controls/ColorPickerPanel` — 名前付きプリセット（"Auto", "Indigo", "Crimson" …, "Custom"）の ComboBox とプレビュースウォッチを 1 行に並べ、"Custom" 選択時だけ hex 入力 + HSV パレット（彩度×明度の四角＋色相スライダー）を展開する 1 行ピッカ。`AllowAuto` を立てると "Auto (palette)" 項目を出します。
- `Controls/GraphFormatPanel` — フォント／目盛り／枠＆グリッド／背景／凡例位置の Expander を 1 つにまとめたサブパネル。`GraphFormattingConfigBase` の Capture / Apply に対応した依存プロパティを公開しているので、各アプリの MainWindow からは共有プロパティをバインドするだけで済みます。Spectrum 固有の「軸の表示」ペインなどは外側に置く方針なので、本コントロールにはアプリ固有スイッチを増やしません。
- `Helpers/GraphSaveHelpers` — グラフ保存ダイアログの PNG / SVG ポリシー（既定 3600×2160 / 300 dpi）と、PNG の `pHYs` チャンクに DPI メタデータを埋め込むパッチ処理。GPC / Spectrum / DLS が同一の解像度・密度設定で書き出せるよう一本化しています。
- `Helpers/PlotHostAspectRatio` — `WpfPlot` を内包する `Border` のサイズを動的に再計算して、選択中のアスペクト比（16:9 / 4:3 / 3:2 / 1:1 / 自動）を維持するヘルパ。
- `Helpers/FormattingDefaultsStore` — `%AppData%\<App>\formatting_config.json` の読み書きと、保存済みの「既定の出力フォルダ」を返す小ヘルパを集約。3 アプリで重複していた IO + 例外処理を 1 箇所にしてあります。
- `FormatHelpers` — 数値パース、不変カルチャ整形、hex カラー正規化、ComboBox の `Tag` 読み書きなどを集めた static ユーティリティ。`using static LabPlot.Core.Wpf.FormatHelpers;` で取り込むと、各アプリの呼び出しがプライベートヘルパだったときと同じ書き味になります。

---

## 使い方

`App.xaml` で共通スタイルをマージします:

```xml
<Application.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <ResourceDictionary Source="pack://application:,,,/LabPlot.Core.Wpf;component/Themes/CommonStyles.xaml" />
        </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
</Application.Resources>
```

`MainWindow.xaml` では、ネームスペースを取り込んで共通コントロールを直接使えます:

```xml
<Window xmlns:controls="clr-namespace:LabPlot.Core.Wpf.Controls;assembly=LabPlot.Core.Wpf"
        ...>
    <controls:AxisRangePanel x:Name="AxisRangePanel"
                             AxisRangeCommitted="AxisRangePanel_AxisRangeCommitted" />
    <controls:GraphFormatPanel x:Name="GraphFormatPanel" />
    <controls:ColorPickerPanel x:Name="LineColorPicker"
                               AllowAuto="True"
                               ColorChanged="LineColorPicker_ColorChanged" />
</Window>
```

PNG / SVG 保存と書式のデフォルト永続化は、ヘルパを直接呼ぶ形になります:

```csharp
using LabPlot.Core.Wpf.Helpers;

// PNG/SVG: 3600x2160, 300 dpi
GraphSaveHelpers.Save(plot, format: GraphSaveFormat.Png, filePath);

// formatting defaults
FormattingDefaultsStore.Save(appName: "Spectrum_Visualization", config);
var loaded = FormattingDefaultsStore.Load<MyFormattingConfig>("Spectrum_Visualization");
```

---

## ファイル構成

```text
Themes/CommonStyles.xaml              共通 Style / ControlTemplate（Section, Expander, Button, TextBox, ComboBox …）
Controls/AxisRangePanel.xaml(.cs)     X/Y min・max + 自動レンジ復帰ボタン
Controls/ColorPickerPanel.xaml(.cs)   プリセット + Custom HSV / hex の 1 行ピッカ
Controls/GraphFormatPanel.xaml(.cs)   フォント・目盛り・枠＆グリッド・背景・凡例の共通サブパネル
Helpers/GraphSaveHelpers.cs           PNG/SVG 書き出しと PNG pHYs パッチ
Helpers/PlotHostAspectRatio.cs        WpfPlot コンテナのアスペクト比保持
Helpers/FormattingDefaultsStore.cs    %AppData% 配下の formatting_config.json 永続化
FormatHelpers.cs                      数値パース・hex 正規化・ComboBox Tag ヘルパ
```

---

## 開発者向け

ビルド:

```powershell
dotnet build src/LabPlot.Core.Wpf/LabPlot.Core.Wpf.csproj
```

依存:

- `LabPlot.Core` — 書式設定の基底（`GraphFormattingConfigBase`）と ScottPlot ヘルパ（`PlotAppearance`）を経由する経路があるため。
- `ScottPlot.WPF 5.1.x` — `WpfPlot` を直接触る `PlotHostAspectRatio` / `GraphSaveHelpers` のため。

ターゲット: `net10.0-windows10.0.19041`（WPF 有効）。Avalonia への移植は将来の選択肢として残してありますが、現在は WPF 固定です。共有スタイルは `Themes/CommonStyles.xaml` 1 ファイルに集約してあるので、ControlTemplate を Avalonia 用に書き直す範囲だけが移植対象になります。
