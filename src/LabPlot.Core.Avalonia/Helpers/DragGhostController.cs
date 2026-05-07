using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Media;

namespace LabPlot.Core.Avalonia.Helpers;

/// <summary>
/// PointerCapture 経由の手動ドラッグ並べ替えで、source 行の見た目をクローンした
/// 半透明ゴーストをカーソルに追従させるヘルパー。<see cref="IDataTemplate"/> を
/// <c>Build(dataContext)</c> して新規 Visual を生成し、<see cref="OverlayLayer"/> 上
/// の <see cref="Border"/> に詰める。
///
/// <para>
/// 当初は <c>RenderTargetBitmap</c> でスナップショットして <see cref="Image"/> として
/// 貼っていたが、Skia の RenderTargetBitmap context では <c>SubpixelAntialias</c> が
/// 効かず、テキストが必ず Antialias (グレースケール AA) で焼かれてどうしても
/// ぼやける。DataTemplate クローン方式ならベクター描画のままなので解像度問題が無い。
/// 代償として ListBoxItem container の hover / selected 装飾は再現されない (DataTemplate
/// は ItemContainerStyle 側の装飾を含まないため)。
/// </para>
/// </summary>
public sealed class DragGhostController
{
    private Border? _ghost;
    private OverlayLayer? _overlay;
    private Point _pointerOffset;

    /// <summary>
    /// ゴーストを表示開始する。<paramref name="template"/> を <paramref name="dataContext"/>
    /// で Build して新規 Visual を生成し、<paramref name="sourceSize"/> の枠で表示する。
    /// <paramref name="pointerOffsetInVisual"/> は Press 時の <c>e.GetPosition(item)</c>
    /// を渡すと「掴んだ位置を保ったまま追従」する自然な挙動になる。
    /// </summary>
    public void Show(
        Window owner,
        IDataTemplate? template,
        object dataContext,
        Size sourceSize,
        Point pointerPosInWindow,
        Point pointerOffsetInVisual)
    {
        _overlay ??= OverlayLayer.GetOverlayLayer(owner);
        if (_overlay is null)
        {
            return;
        }

        Hide();
        _pointerOffset = pointerOffsetInVisual;

        Control? content = template?.Build(dataContext) as Control;
        if (content is null)
        {
            // template が無い / Build 結果が Control でない時のフォールバック。
            // データの ToString を表示するだけの簡易版で UX は失わない。
            content = new TextBlock
            {
                Text = dataContext?.ToString() ?? string.Empty,
                FontSize = 12,
                Margin = new Thickness(10, 4),
                Foreground = new SolidColorBrush(Color.Parse("#0F172A")),
            };
        }
        content.DataContext = dataContext;

        _ghost = new Border
        {
            Width = sourceSize.Width,
            Height = sourceSize.Height,
            Background = new SolidColorBrush(Color.Parse("#FFFFFF")),
            BorderBrush = new SolidColorBrush(Color.Parse("#94A3B8")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Opacity = 0.92,
            IsHitTestVisible = false,
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 4,
                Blur = 12,
                Color = Color.Parse("#40000000"),
            }),
            Child = content,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Top,
        };

        _overlay.Children.Add(_ghost);
        Move(pointerPosInWindow);
    }

    /// <summary>
    /// カーソル追従位置を更新する。Show より前の呼び出しは no-op。
    /// </summary>
    public void Move(Point pointerPosInWindow)
    {
        if (_ghost is null)
        {
            return;
        }

        // 掴んだ位置を保持したままゴーストを動かす:
        //   ghostTopLeft = pointer - clickOffset
        var left = pointerPosInWindow.X - _pointerOffset.X;
        var top = pointerPosInWindow.Y - _pointerOffset.Y;
        _ghost.Margin = new Thickness(left, top, 0, 0);
    }

    /// <summary>
    /// ゴーストを破棄する。Release / CaptureLost / 早期 abort で必ず呼ぶ。冪等。
    /// </summary>
    public void Hide()
    {
        if (_ghost is not null && _overlay is not null)
        {
            _overlay.Children.Remove(_ghost);
        }
        _ghost = null;
        _pointerOffset = default;
        // _overlay は同 Window の次回再利用のため保持。
    }
}
