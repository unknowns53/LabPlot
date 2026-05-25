using System.Collections.Generic;

namespace LabPlot.DLS.Avalonia;

/// <summary>
/// DLS 解析ウィンドウの溶媒名 AutoCompleteBox で提示するプリセット 1 件。
/// 屈折率と粘度は温度依存性が大きいので、単一の代表値ではなく温度ごとの
/// 観測点 (<see cref="SolventPresetPoint"/>) の表として持つ。プリセットを
/// 選択した瞬間、AnalysisWindow は現在の温度入力で表を線形補間して n と
/// eta を <c>MetadataRefractiveIndexTextBox</c> /
/// <c>MetadataViscosityTextBox</c> に無条件で書き込む (鷹栖くん 2026-05-25
/// 合意)。ユーザー追加プリセットは <see cref="SolventPresetStore"/> 経由で
/// <c>%APPDATA%/LabPlot/dls-solvent-presets.json</c> に永続化される。
/// </summary>
internal sealed record SolventPreset(
    string Name,
    IReadOnlyList<SolventPresetPoint> Points,
    bool IsBuiltIn)
{
    public override string ToString() => Name;
}

/// <summary>
/// 単一温度における溶媒の光学パラメータ。屈折率 (無次元) と粘度 (mPa·s) は
/// その温度で測られた / 文献に載っている代表値で、補間は線形に行う。
/// </summary>
internal sealed record SolventPresetPoint(
    double TemperatureCelsius,
    double RefractiveIndex,
    double ViscosityMpas);
