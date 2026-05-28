# 新モジュール追加 scaffold 手順

LabPlot に新しい分析モジュール（例: NMR / Raman / TGA など）を追加するときの定型作業
チェックリスト。v1.3.5 時点での Avalonia 主流系統 (`LabPlot.Shell.Avalonia` から
3 モジュール = GPC / Spectrum / DLS を起動する構造) を前提とする。

WPF 版 (`LabPlot.Shell` / 各 WPF モジュール) は v1.0.x 保守ラインのため、新モジュール
は **Avalonia 側だけ** に追加する。

## 前提

- 解析ロジックを持つ Core 層 (`LabPlot.<Module>/<Module>Analyzer.Core/`) は別途用意してある
  こと。Avalonia モジュールはあくまで UI レイヤで、Core が公開するエントリポイント
  (例: `XxxReader.Read(string path)`、`XxxAnalyzer.Analyze(...)`) を呼ぶだけにする。
- `LabPlot.Core.Avalonia` の共通基盤 (CustomTitleBar / WindowStateStore / CommonStyles
  / CommonTokens) を再利用する。新モジュール固有のコントロールやスタイルがある場合は
  `<Module>.Avalonia/Themes/<Module>Styles.axaml` のような separate file に置く。

## チェックリスト

### 1. csproj 新規作成

`src/LabPlot.<Module>.Avalonia/LabPlot.<Module>.Avalonia.csproj` を作る。
既存 3 モジュール (`LabPlot.GPC.Avalonia.csproj` など) と同じ template:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>LabPlot.<Module>.Avalonia</RootNamespace>
    <AssemblyName>LabPlot.<Module>.Avalonia</AssemblyName>
    <Version>1.3.5</Version>
    <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\LabPlot.Core\LabPlot.Core.csproj" />
    <ProjectReference Include="..\LabPlot.Core.Avalonia\LabPlot.Core.Avalonia.csproj" />
    <ProjectReference Include="..\LabPlot.<Module>\<Module>Analyzer.Core\<Module>Analyzer.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Avalonia" Version="11.3.14" />
    <PackageReference Include="Avalonia.Themes.Fluent" Version="11.3.14" />
    <PackageReference Include="ScottPlot.Avalonia" Version="5.1.58" />
  </ItemGroup>
</Project>
```

DataGrid を使う場合は `Avalonia.Controls.DataGrid` 11.3.13 を、xlsx を読む場合は
`ClosedXML` 0.105.0 を追加 (Spectrum / DLS の csproj 参照)。

### 2. MainWindow.axaml + .axaml.cs テンプレート

3 モジュールいずれかの MainWindow を雛形にコピーして編集する。最低限必要な構造:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:controls="using:LabPlot.Core.Avalonia.Controls"
        x:Class="LabPlot.<Module>.Avalonia.MainWindow"
        Title="<Module> Analyzer"
        Icon="avares://LabPlot.Core.Avalonia/Assets/app-icon.png"
        Height="860" MinHeight="660" Width="1280" MinWidth="960"
        Background="{DynamicResource MainBgSurfaceBrush}"
        FontFamily="Segoe UI, Yu Gothic UI, Meiryo UI, sans-serif"
        FontSize="13"
        UseLayoutRounding="True"
        WindowStartupLocation="CenterScreen"
        ExtendClientAreaToDecorationsHint="True"
        ExtendClientAreaChromeHints="NoChrome"
        ExtendClientAreaTitleBarHeightHint="-1"
        SystemDecorations="Full">
  <Border Theme="{DynamicResource WindowChromeRootBorderStyle}">
    <Grid RowDefinitions="Auto,*">
      <controls:CustomTitleBar Grid.Row="0"
                               Name="MainTitleBar"
                               AppName="<Module> Analyzer"
                               Subtitle=""
                               AppIconData="..." />
      <!-- 本体 UI をここに -->
    </Grid>
  </Border>
</Window>
```

`.axaml.cs` 側の必須 boilerplate:

```csharp
protected override void OnOpened(EventArgs e)
{
    base.OnOpened(e);
    WindowStateStore.ApplyTo(this, "<Module>.MainWindow");
}

protected override void OnClosing(WindowClosingEventArgs e)
{
    WindowStateStore.PersistFrom(this, "<Module>.MainWindow");
    base.OnClosing(e);
}
```

`WindowStateStore.ApplyTo` / `PersistFrom` の呼び出しを忘れると Window 位置 / サイズ /
最大化状態の永続化が効かない。PR #15 で固定サイズ Window 向けの分岐も入っているので、
`CanResize=False` の Window でも安全に呼べる。

### 3. PortalWindow にカード追加

`src/LabPlot.Shell.Avalonia/PortalWindow.axaml` の UniformGrid 内に、既存 GPC / UV-Vis /
DLS カードと同形で 1 つ追加:

```xml
<Button Theme="{DynamicResource PortalCardStyle}"
        Click="OpenNmr_Click"
        ToolTip.Tip="NMR スペクトル解析">
  <Grid ColumnDefinitions="Auto,*">
    <Border Grid.Column="0" Theme="{DynamicResource PortalCardBadgeStyle}">
      <Path Theme="{DynamicResource PortalCardIconStyle}"
            Data="<新モジュールを表すアイコン Path>" />
    </Border>
    <StackPanel Grid.Column="1" Margin="0,2,0,0">
      <TextBlock Text="NMR" FontWeight="SemiBold" FontSize="14" Foreground="#0F172A" />
      <TextBlock Text="核磁気共鳴スペクトル&#x0a;化学シフト / 積分比"
                 FontSize="11" Foreground="#475569" TextWrapping="Wrap" />
    </StackPanel>
  </Grid>
</Button>
```

### 4. PortalWindow.axaml.cs に Open ハンドラ + ショートカット追加

```csharp
private void OpenNmr_Click(object? sender, RoutedEventArgs e)
    => OpenSingleton<global::LabPlot.NMR.Avalonia.MainWindow>();

protected override void OnKeyDown(KeyEventArgs e)
{
    var cmd = e.HasCommandModifier();
    if (cmd)
    {
        switch (e.Key)
        {
            case Key.D1: case Key.NumPad1: OpenGpc_Click(this, new()); break;
            case Key.D2: case Key.NumPad2: OpenSpectrum_Click(this, new()); break;
            case Key.D3: case Key.NumPad3: OpenDls_Click(this, new()); break;
            // 新モジュール用ショートカット (Ctrl/Cmd + 4)
            case Key.D4: case Key.NumPad4: OpenNmr_Click(this, new()); break;
            ...
        }
    }
}
```

### 5. KeyboardShortcutsWindow.axaml.cs に AppKind + groups 追加

`src/LabPlot.Core.Avalonia/KeyboardShortcutsWindow.axaml.cs`:

```csharp
public enum AppKind { Dls, Gpc, Spectrum, Calibration, Portal, Nmr }
```

`BuildShortcutGroups()` の switch にも新 case を追加し、F1 ヘルプから新モジュールの
ショートカット一覧を出せるようにする。Portal の `AppKind.Portal` の "アプリ起動"
グループにも `Ctrl/Cmd + 4` 行を追加。

### 6. LabPlot.slnx に csproj を登録

```xml
<Project Path="src/LabPlot.NMR.Avalonia/LabPlot.NMR.Avalonia.csproj" />
```

### 7. Shell.Avalonia.csproj に ProjectReference 追加

```xml
<ProjectReference Include="..\LabPlot.NMR.Avalonia\LabPlot.NMR.Avalonia.csproj" />
```

これがないと `OpenSingleton<global::LabPlot.NMR.Avalonia.MainWindow>()` がコンパイル
通らない。

### 8. macOS .app バンドルへの追従 (任意)

`src/LabPlot.Shell.Avalonia/LabPlot.Shell.Avalonia.csproj` の `MacOSAppBundle` Target
は publish 出力ディレクトリ全体を `Contents/MacOS/` に再帰移動するので、新モジュール
固有のリソース (samples フォルダなど) も自動で .app バンドルに入る。追加修正不要。

## Verification

1. `dotnet build src/LabPlot.Shell.Avalonia/LabPlot.Shell.Avalonia.csproj -c Release` が green
2. `dotnet run --project src/LabPlot.Shell.Avalonia/LabPlot.Shell.Avalonia.csproj` で Portal を起動し、新カードが表示される
3. 新カードをクリック / Ctrl/Cmd+4 で新モジュール MainWindow が開く
4. 新モジュールの F1 でショートカット一覧が表示される
5. Window 閉じる → 再起動で位置 / サイズが復元される (WindowStateStore 動作確認)

## 参照する既存実装

3 モジュールいずれかをコピーベースにする際の参考:

- GPC: 較正曲線 JSON 連携 / ComboBox + 数値入力の多い UI が必要なら
- Spectrum: メタデータ Expander / ピーク / 領域 / λmax / 曇点マーカー overlay が必要なら
- DLS: 複数 sheet/run の multi-select ListBox / AnalysisWindow 子 Window が必要なら

色 / 寸法 / フォントは全て CommonTokens.axaml から `{StaticResource ...}` 経由で参照
すること。`#2563EB` などの直書きは将来 Dark theme 対応時に追従漏れを起こす。
