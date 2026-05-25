namespace LabPlot.DLS.Avalonia;

/// <summary>
/// DLS 解析ウィンドウの溶媒名 AutoCompleteBox で提示するプリセット 1 件。
/// 屈折率 (無次元) と粘度 (mPa·s) は 25°C 既定値で、選択時に
/// <c>MetadataRefractiveIndexTextBox</c> / <c>MetadataViscosityTextBox</c> に
/// 無条件で書き込まれる (鷹栖くん 2026-05-25 合意)。組み込みは 9 種、
/// ユーザー追加分は <see cref="SolventPresetStore"/> 経由で JSON に永続化。
/// </summary>
internal sealed record SolventPreset(
    string Name,
    double RefractiveIndex,
    double ViscosityMpas,
    bool IsBuiltIn)
{
    public override string ToString() => Name;
}
