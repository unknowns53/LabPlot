using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using DlsAnalyzer.Core;
using LabPlot.Core;

namespace LabPlot.DLS.Avalonia;

// Phase 7 で MainWindow のネスト型として置いたが、AnalysisWindow からも触る必要が
// 出たため Top-level に昇格。`vm:DlsDatasetItem` などとして XAML から x:DataType で
// 参照する形は維持する (旧 `vm:MainWindow+DlsDatasetItem` 構文は不要に)。

internal sealed class DlsDatasetStyle
{
    public string? ColorHex { get; set; }
    public string? LegendName { get; set; }
    public double LineWidth { get; set; } = GraphFormattingConfigBase.DefaultLineWidth;
    public double MarkerSize { get; set; } = GraphFormattingConfigBase.DefaultMarkerSize;
}

internal sealed class DlsDatasetMetadataState
{
    public const double DefaultWavelengthNm = 633.0;
    public const double DefaultScatteringAngleDegrees = 173.0;

    public double? TemperatureCelsius { get; set; }
    public string? Solvent { get; set; }
    public double? ConcentrationMgPerMl { get; set; }
    public double? RefractiveIndex { get; set; }
    public double? ViscosityMpas { get; set; }
    public double? WavelengthNm { get; set; } = DefaultWavelengthNm;
    public double? ScatteringAngleDegrees { get; set; } = DefaultScatteringAngleDegrees;
}

internal sealed class DlsDatasetCumulantSettings
{
    public double? FitRangeMinMicroseconds { get; set; }
    public double? FitRangeMaxMicroseconds { get; set; }
}

internal sealed class DlsDatasetItem : INotifyPropertyChanged
{
    public DlsDataset Dataset { get; }
    public DlsDatasetStyle Style { get; } = new();
    public DlsDatasetMetadataState Metadata { get; } = new();
    public DlsDatasetCumulantSettings Cumulant { get; } = new();
    public string SheetName => Dataset.SheetName;

    private SolidColorBrush _colorBrush = new(Colors.Gray);
    public SolidColorBrush ColorBrush
    {
        get => _colorBrush;
        set
        {
            if (_colorBrush.Color == value.Color) return;
            _colorBrush = value;
            OnPropertyChanged();
        }
    }

    public DlsDatasetItem(DlsDataset dataset)
    {
        Dataset = dataset;
        Metadata.TemperatureCelsius = dataset.Metadata.TemperatureCelsius;
        Metadata.Solvent = dataset.Metadata.Solvent;
        Metadata.ConcentrationMgPerMl = dataset.Metadata.ConcentrationMgPerMl;
        Metadata.RefractiveIndex = dataset.Metadata.RefractiveIndex;
        Metadata.ViscosityMpas = dataset.Metadata.ViscosityMpas;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
