using GpcAnalyzer.Core;

namespace LabPlot.GPC.Avalonia;

/// <summary>
/// MainWindow が per-dataset の見た目 (色 / 凡例名 / 線幅 / マーカーサイズ) を
/// 保持する内部 model。元は <c>MainWindow.axaml.cs</c> 内に <c>private sealed
/// class</c> でネストしていたが、<see cref="GpcSessionMapper"/> から参照する
/// ために internal 型として独立ファイルに切り出した。
/// </summary>
internal sealed class DatasetStyle
{
    public string? ColorHex { get; set; }
    public string? LegendName { get; set; }
    public double LineWidth { get; set; } = GraphFormattingConfig.DefaultLineWidth;
    public double MarkerSize { get; set; } = GraphFormattingConfig.DefaultMarkerSize;
}
