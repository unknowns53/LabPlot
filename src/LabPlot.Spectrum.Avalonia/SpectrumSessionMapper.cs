using LabPlot.Core;
using SpectrumAnalyzer.Core;

namespace LabPlot.Spectrum.Avalonia;

/// <summary>
/// Spectrum のセッション保存 / 復元で、実行時の <see cref="SpectrumDataset"/> +
/// <see cref="DatasetStyle"/> と永続化用の <see cref="AnalysisSessionDataset"/>
/// を双方向に変換する Mapper。DLS / GPC 側 (<c>DlsSessionMapper</c> /
/// <c>GpcSessionMapper</c>) と同形で「Save と Load の per-dataset プロパティ
/// 転写を 1 箇所にまとめる」設計。新しい Style フィールドを足すときは本
/// ファイル 1 箇所だけ更新すれば Save / Load 両経路で漏れない。
/// </summary>
internal static class SpectrumSessionMapper
{
    /// <summary>Save 経路: 実行時 dataset/style → セッション側エントリ。</summary>
    public static AnalysisSessionDataset ToSessionDataset(SpectrumDataset dataset, DatasetStyle style)
        => new()
        {
            SourceFilePath = dataset.SourceFilePath ?? string.Empty,
            Style = ToSessionStyle(style),
        };

    /// <summary>Load 経路: セッション側 Style → 実行時 DatasetStyle を新規生成。</summary>
    public static DatasetStyle ToDatasetStyle(AnalysisSessionStyle src) => new()
    {
        ColorHex = src.ColorHex,
        LegendName = src.LegendName,
        LineWidth = src.LineWidth,
        MarkerSize = src.MarkerSize,
    };

    // ----- Style (4 properties) -----

    private static AnalysisSessionStyle ToSessionStyle(DatasetStyle src) => new()
    {
        ColorHex = src.ColorHex,
        LegendName = src.LegendName,
        LineWidth = src.LineWidth,
        MarkerSize = src.MarkerSize,
    };
}
