using Avalonia.Controls;
using Avalonia.Media;

namespace LabPlot.Core.Avalonia.Helpers;

/// <summary>
/// Phase 7 Batch 6 step 2: Avalonia 各 Window の文字描画品質を WPF 版に揃える。
/// WPF の <c>TextOptions.TextRenderingMode=ClearType</c> + <c>TextFormattingMode=Ideal</c>
/// に相当する Avalonia 11.3 の API は <see cref="RenderOptions.SetTextRenderingMode"/>
/// と <see cref="RenderOptions.SetEdgeMode"/>。これらは struct 化されているため
/// XAML Style の Setter から書くことはできず、各 Window コンストラクタで明示的に
/// 呼び出して個別の Visual に attached property を載せる必要がある。
/// </summary>
public static class WindowAppearance
{
    /// <summary>
    /// Window の文字 / 図形描画を WPF 版と同等の濃さに揃える。
    /// <list type="bullet">
    ///   <item><see cref="TextRenderingMode.SubpixelAntialias"/>: WPF の ClearType
    ///   相当。Inter / Segoe UI / Yu Gothic UI の Regular ウェイトが thicker かつ
    ///   サブピクセル境界で滲まずに描画される。</item>
    ///   <item><see cref="EdgeMode.Antialias"/>: 既定 (<see cref="EdgeMode.Unspecified"/>)
    ///   は描画バックエンド任せでアイコン Path 等が aliased になりやすい。明示で
    ///   antialias を要求して LabPlot のアイコン群を WPF 版同等の滑らかさに揃える。</item>
    /// </list>
    /// 各 MainWindow / PortalWindow / 子 Dialog の <c>InitializeComponent</c> 直後に
    /// 1 行で呼び出す。子 Visual には継承される (<see cref="RenderOptions"/> は
    /// inherits=true) ので、Window 1 個に当てれば配下全体に効く。
    /// </summary>
    public static void ApplyDefaults(Window window)
    {
        RenderOptions.SetTextRenderingMode(window, TextRenderingMode.SubpixelAntialias);
        RenderOptions.SetEdgeMode(window, EdgeMode.Antialias);
    }
}
