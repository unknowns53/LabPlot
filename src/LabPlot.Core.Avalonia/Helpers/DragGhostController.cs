using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace LabPlot.Core.Avalonia.Helpers;

/// <summary>
/// PointerCapture 経由の手動ドラッグ並べ替えで、カーソルに追従する半透明ゴーストを
/// 描画するヘルパー。Avalonia 11.3 の <see cref="DragDrop.DoDragDrop"/> は OS シェル
/// による自動ゴースト描画があるが、本プロジェクトでは drop イベントの不安定さを回避
/// するため自前 PointerCapture に切り替えている。その代わりに失われる「行が掴める感」
/// を取り戻すための軽量な追従 Visual を提供する。
///
/// <para>
/// <see cref="OverlayLayer"/> を使用するため Window の Content 構造に依存せず、
/// MainWindow / Dialog / どの構造の Window でも上位 z-order に確実に乗る。
/// </para>
/// </summary>
public sealed class DragGhostController
{
    private Border? _ghost;
    private TextBlock? _label;
    private OverlayLayer? _overlay;

    /// <summary>
    /// ゴーストを表示開始する。<paramref name="pointerPosInWindow"/> は Window 座標系。
    /// 既に表示中ならラベルを更新するだけで再生成しない。
    /// </summary>
    public void Show(Window owner, string label, Point pointerPosInWindow)
    {
        _overlay ??= OverlayLayer.GetOverlayLayer(owner);
        if (_overlay is null)
        {
            return;
        }

        if (_ghost is null)
        {
            _label = new TextBlock
            {
                Text = label,
                FontSize = 12,
                FontWeight = FontWeight.Medium,
                Foreground = new SolidColorBrush(Color.Parse("#0F172A")),
            };

            _ghost = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#FFFFFF")),
                BorderBrush = new SolidColorBrush(Color.Parse("#94A3B8")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 4),
                Opacity = 0.92,
                IsHitTestVisible = false,
                BoxShadow = new BoxShadows(new BoxShadow
                {
                    OffsetX = 0,
                    OffsetY = 2,
                    Blur = 6,
                    Color = Color.Parse("#33000000"),
                }),
                Child = _label,
            };

            _overlay.Children.Add(_ghost);
        }
        else if (_label is not null)
        {
            _label.Text = label;
        }

        Move(pointerPosInWindow);
    }

    /// <summary>
    /// カーソル追従位置を更新する。Show より前に呼ぶと no-op。
    /// </summary>
    public void Move(Point pointerPosInWindow)
    {
        if (_ghost is null)
        {
            return;
        }

        // ゴーストはカーソル右下に少しオフセットして配置する。
        // OverlayLayer は Canvas-like で、Canvas.Left / Canvas.Top でなく
        // Margin で配置する。 Margin.Left/Top のセットでも z-order は保たれる。
        _ghost.Margin = new Thickness(pointerPosInWindow.X + 12, pointerPosInWindow.Y + 12, 0, 0);
        _ghost.HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left;
        _ghost.VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Top;
    }

    /// <summary>
    /// ゴーストを破棄する。Release / CaptureLost / ESC キャンセルなど終端で必ず呼ぶ。
    /// </summary>
    public void Hide()
    {
        if (_ghost is not null && _overlay is not null)
        {
            _overlay.Children.Remove(_ghost);
        }
        _ghost = null;
        _label = null;
        // _overlay は保持し続ける (同じ Window で次回再利用)。
    }
}
