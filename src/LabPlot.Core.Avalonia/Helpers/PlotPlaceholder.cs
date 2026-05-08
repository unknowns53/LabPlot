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
        // SetState を呼ぶ側は「文言を出したい」のが意図なので、IsVisible も同時に立てる。
        // データ描画開始時は Hide(...) を呼んで明示的に消す。
        target.IsVisible = true;
    }

    /// <summary>
    /// データ描画開始など、placeholder を消したい場面で呼ぶ。SetState(...) と対称。
    /// </summary>
    public static void Hide(TextBlock? target)
    {
        if (target is null) return;
        target.IsVisible = false;
    }
}
