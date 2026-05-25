using DlsAnalyzer.Core;
using LabPlot.Core;

namespace LabPlot.DLS.Avalonia;

/// <summary>
/// DLS のセッション保存 / 復元で、実行時の <see cref="DlsDatasetItem"/> と永続化用の
/// <see cref="DlsAnalysisSessionDataset"/> を双方向に変換する Mapper。Metadata 7 / Cumulant 2 /
/// Style 4 = 計 13 プロパティの転写を、Save と Load の 2 箇所に重複して書かない。
/// 新フィールドを追加するときは本ファイル 1 箇所だけ更新すれば save / load 両経路で漏れない。
/// </summary>
internal static class DlsSessionMapper
{
    /// <summary>Save 経路: 実行時 item → セッション側エントリ。</summary>
    public static DlsAnalysisSessionDataset ToSessionDataset(
        DlsDatasetItem item, string sourceFilePath, bool selected)
        => new()
        {
            SheetName = item.Dataset.SheetName,
            SourceFilePath = sourceFilePath,
            Selected = selected,
            Style = ToSessionStyle(item.Style),
            Metadata = ToSessionMetadata(item.Metadata),
            CumulantSettings = ToSessionCumulant(item.Cumulant),
        };

    /// <summary>Load 経路: セッション側エントリ → 実行時 item へ in-place 反映。</summary>
    public static void ApplyToItem(DlsAnalysisSessionDataset sessionDs, DlsDatasetItem item)
    {
        ApplyStyle(sessionDs.Style, item.Style);
        ApplyMetadata(sessionDs.Metadata, item.Metadata);
        ApplyCumulant(sessionDs.CumulantSettings, item.Cumulant);
    }

    // ----- Style (4 properties) -----

    private static AnalysisSessionStyle ToSessionStyle(DlsDatasetStyle src) => new()
    {
        ColorHex = src.ColorHex,
        LegendName = src.LegendName,
        LineWidth = src.LineWidth,
        MarkerSize = src.MarkerSize,
    };

    private static void ApplyStyle(AnalysisSessionStyle src, DlsDatasetStyle dst)
    {
        dst.ColorHex = src.ColorHex;
        dst.LegendName = src.LegendName;
        dst.LineWidth = src.LineWidth;
        dst.MarkerSize = src.MarkerSize;
    }

    // ----- Metadata (7 properties) -----

    private static DlsAnalysisSessionMetadata ToSessionMetadata(DlsDatasetMetadataState src) => new()
    {
        TemperatureCelsius = src.TemperatureCelsius,
        Solvent = src.Solvent,
        ConcentrationMgPerMl = src.ConcentrationMgPerMl,
        RefractiveIndex = src.RefractiveIndex,
        ViscosityMpas = src.ViscosityMpas,
        WavelengthNm = src.WavelengthNm,
        ScatteringAngleDegrees = src.ScatteringAngleDegrees,
    };

    private static void ApplyMetadata(DlsAnalysisSessionMetadata src, DlsDatasetMetadataState dst)
    {
        dst.TemperatureCelsius = src.TemperatureCelsius;
        dst.Solvent = src.Solvent;
        dst.ConcentrationMgPerMl = src.ConcentrationMgPerMl;
        dst.RefractiveIndex = src.RefractiveIndex;
        dst.ViscosityMpas = src.ViscosityMpas;
        dst.WavelengthNm = src.WavelengthNm;
        dst.ScatteringAngleDegrees = src.ScatteringAngleDegrees;
    }

    // ----- Cumulant (2 properties) -----

    private static DlsAnalysisSessionCumulantSettings ToSessionCumulant(DlsDatasetCumulantSettings src) => new()
    {
        FitRangeMinMicroseconds = src.FitRangeMinMicroseconds,
        FitRangeMaxMicroseconds = src.FitRangeMaxMicroseconds,
    };

    private static void ApplyCumulant(DlsAnalysisSessionCumulantSettings src, DlsDatasetCumulantSettings dst)
    {
        dst.FitRangeMinMicroseconds = src.FitRangeMinMicroseconds;
        dst.FitRangeMaxMicroseconds = src.FitRangeMaxMicroseconds;
    }
}
