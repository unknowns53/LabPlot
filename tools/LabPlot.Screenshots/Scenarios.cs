using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LabPlot.Tools.Screenshots;

/// <summary>1 シナリオ = 出力相対パス (artifacts/screenshots/ 起点) + 非同期実行デリゲート。</summary>
internal sealed record ScreenshotScenario(string RelativePath, Func<ShotContext, Task> RunAsync);

/// <summary>
/// 全モジュール分のシナリオを集約する。モジュールごとに <see cref="PortalScenarios"/> /
/// <see cref="GpcScenarios"/> / <see cref="SpectrumScenarios"/> など別ファイルへ分割してあるので、
/// ここでは単純に連結するだけ。
/// </summary>
internal static class Scenarios
{
    public static IReadOnlyList<ScreenshotScenario> All { get; } =
        PortalScenarios.All
            .Concat(GpcScenarios.All)
            .Concat(SpectrumScenarios.All)
            .Concat(DlsScenarios.All)
            .Concat(ViewerScenarios.All)
            .Concat(NmrScenarios.All)
            .ToArray();
}
