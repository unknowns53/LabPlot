using System.Collections.Generic;
using LabPlot.Core;
using NMRAnalyzer.Core;

namespace LabPlot.NMR.Avalonia;

/// <summary>
/// Converts the NMR window's in-memory state to / from the persisted
/// <see cref="NmrAnalysisSession"/>. Same shape as Spectrum/DLS/GPC's session
/// mappers: only the source file paths and per-dataset styles are stored, so
/// loading re-reads each .jdf and re-applies the styling. Display-only
/// YScale / YOffset are intentionally not persisted (re-applied via the
/// normalize / stack buttons).
/// </summary>
internal static class NmrSessionMapper
{
    public static NmrAnalysisSession ToSession(
        IReadOnlyList<NmrDataset> datasets,
        IReadOnlyList<DatasetStyle> styles,
        IReadOnlyList<NmrIntegrationRegion> regions,
        bool overlay,
        int activeIndex,
        double referenceShiftPpm)
    {
        var session = new NmrAnalysisSession
        {
            Overlay = overlay,
            ActiveDatasetIndex = activeIndex,
            ReferenceShiftPpm = referenceShiftPpm,
        };

        for (var i = 0; i < datasets.Count; i++)
        {
            var style = i < styles.Count ? styles[i] : new DatasetStyle();
            session.Datasets.Add(new AnalysisSessionDataset
            {
                SourceFilePath = datasets[i].SourceFilePath ?? string.Empty,
                Style = new AnalysisSessionStyle
                {
                    ColorHex = style.ColorHex,
                    LegendName = style.LegendName,
                    LineWidth = style.LineWidth,
                    MarkerSize = style.MarkerSize,
                },
            });
        }

        foreach (var region in regions)
        {
            session.IntegrationRegions.Add(region);
        }

        return session;
    }

    public static DatasetStyle ToStyle(AnalysisSessionStyle? style)
    {
        if (style is null)
        {
            return new DatasetStyle();
        }

        return new DatasetStyle
        {
            ColorHex = style.ColorHex,
            LegendName = style.LegendName,
            LineWidth = style.LineWidth,
            MarkerSize = style.MarkerSize,
        };
    }
}
