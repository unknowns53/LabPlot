using Avalonia.Controls;

namespace LabPlot.Core.Avalonia.Helpers;

/// <summary>
/// Switches the text on the plot-area placeholder TextBlock between three
/// well-known states. WPF 版と同じ wording を Avalonia の TextBlock
/// (Avalonia.Controls.TextBlock) に当てる。
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
