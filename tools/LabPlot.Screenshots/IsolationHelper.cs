using System;
using System.IO;

namespace LabPlot.Tools.Screenshots;

/// <summary>
/// <c>LABPLOT_APPDATA_OVERRIDE</c> を新しい一時ディレクトリへ差し替えるためのヘルパー。
/// <see cref="LabPlot.Core.Avalonia.Helpers.AppDataPaths.GetApplicationDataPath"/> は
/// 呼び出しのたびにこの環境変数を読み直すので、MRU (RecentFilesStore) や window state
/// (WindowStateStore) は各シナリオの冒頭でこれを呼ぶだけで前のシナリオの状態から
/// 独立させられる。
///
/// 例外は GPC.Avalonia.MainWindow の formatting_config.json パス
/// (<c>FormattingConfigPath</c>) で、こちらは static readonly のためプロセス内で最初に
/// MainWindow が構築された瞬間の値に固定される。較正曲線の既定値注入は
/// <c>GpcScenarios.GetOrCreateCalibrationRoot</c> 側で別管理している。
/// </summary>
internal static class IsolationHelper
{
    private const string OverrideEnvironmentVariableName = "LABPLOT_APPDATA_OVERRIDE";

    public static string UseFreshAppData(string label)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "LabPlotScreenshots",
            $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        Environment.SetEnvironmentVariable(OverrideEnvironmentVariableName, path);
        return path;
    }
}
