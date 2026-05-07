using System.Windows.Controls;

namespace LabPlot.Core.Wpf.Helpers;

/// <summary>
/// Switches the text on the plot-area placeholder TextBlock between three
/// well-known states. Centralised so GPC / Spectrum / DLS share one wording
/// per state instead of each app duplicating "グラフ表示の初期化に失敗しました。"
/// in its own catch block.
/// </summary>
public static class PlotPlaceholder
{
    public enum State
    {
        Initializing,
        EmptyReady,
        InitFailed,
    }

    private const string InitializingText = "グラフを初期化しています…";
    private const string EmptyReadyText = "ファイルを読み込むとここに表示されます";
    private const string InitFailedText = "グラフ表示の初期化に失敗しました。";

    /// <summary>
    /// Update the placeholder text. Pass the same TextBlock that is layered
    /// over the plot skeleton (typically named PlotPlaceholderTextBlock).
    /// Null is a no-op so call sites can run before XAML is fully built.
    /// </summary>
    public static void SetState(TextBlock? target, State state)
    {
        if (target is null) return;
        target.Text = state switch
        {
            State.Initializing => InitializingText,
            State.EmptyReady => EmptyReadyText,
            State.InitFailed => InitFailedText,
            _ => target.Text,
        };
    }
}
