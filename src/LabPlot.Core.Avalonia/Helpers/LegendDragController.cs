using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using LabPlot.Core;
using ScottPlot.Avalonia;

namespace LabPlot.Core.Avalonia.Helpers;

/// <summary>
/// Phase 7 Batch 6 step 3: WPF 版 <c>LabPlot.Core.Wpf.Helpers.LegendDragController</c>
/// を Avalonia の <see cref="AvaPlot"/> 用に移植したもの。
/// AvaPlot 上でマウスドラッグ → ScottPlot 5 の凡例位置をリアルタイム更新する。
/// 呼び出し側は 3 つの lambda を渡す：
/// <list type="bullet">
///   <item>現在の anchor 文字列 (例 <c>"UpperRight"</c>) を返す getter</item>
///   <item>現在の <c>(LegendOffsetX, LegendOffsetY)</c> を返す getter</item>
///   <item>ドラッグ完了時に最終 anchor + offsets を受け取る commit callback</item>
/// </list>
/// WPF 版とのアルゴリズム差は無し（<see cref="PlotAppearance.ChooseBestLegendAnchor"/>
/// + <see cref="PlotAppearance.ComputeOffsetForLegendPosition"/> による 9 セル auto-pick、
/// <see cref="PlotAppearance.ComputeLegendMargin"/> による hit-test）。
/// </summary>
/// <remarks>
/// WPF → Avalonia の置換要点：
/// <list type="bullet">
///   <item><c>WpfPlot</c> → <see cref="AvaPlot"/>。<c>DisplayScale</c> プロパティは双方に同名で存在。</item>
///   <item><c>PreviewMouseLeftButtonDown / Move / Up</c> →
///         <see cref="InputElement.PointerPressedEvent"/> 等を
///         <see cref="RoutingStrategies.Tunnel"/> 付きの <c>AddHandler</c> で購読
///         （Avalonia には WPF の Preview 系イベントに該当する CLR イベントが無い）。</item>
///   <item><c>WpfPlot.CaptureMouse()</c> → <see cref="IPointer.Capture"/>。
///         capture は PointerPressedEventArgs から取り出した Pointer に対して行う。</item>
///   <item><c>LostMouseCapture</c> → <see cref="InputElement.PointerCaptureLostEvent"/>。</item>
///   <item><c>e.GetPosition(plot)</c> は WPF と同名で同義（DIU 単位の戻り値）。</item>
///   <item><c>Cursors.SizeAll</c> → <c>new Cursor(StandardCursorType.SizeAll)</c>。
///         解除は <c>Cursor = Cursor.Default</c>。</item>
/// </list>
/// </remarks>
public sealed class LegendDragController
{
    /// <summary>
    /// Movement (in DIU) below this threshold counts as a click,
    /// not a drag. Keeps a single click on the legend from accidentally
    /// shifting the anchor by a pixel or two. WPF 版と同値。
    /// </summary>
    private const double DragThresholdDiu = 3.0;

    private static readonly Cursor SizeAllCursor = new(StandardCursorType.SizeAll);

    private readonly AvaPlot _avaPlot;
    private readonly Func<string> _getPosition;
    private readonly Func<(double X, double Y)> _getOffset;
    private readonly Action<string, double, double> _commitPlacement;

    private bool _attached;
    private bool _maybeDragging;
    private bool _dragging;
    private Point _pressPoint;
    private float _startLegendLeft;
    private float _startLegendTop;
    private float _startLegendW;
    private float _startLegendH;
    private string _draftPosition = string.Empty;
    private double _draftOffsetX;
    private double _draftOffsetY;
    private IPointer? _capturedPointer;

    public LegendDragController(
        AvaPlot avaPlot,
        Func<string> getPosition,
        Func<(double X, double Y)> getOffset,
        Action<string, double, double> commitPlacement)
    {
        _avaPlot = avaPlot ?? throw new ArgumentNullException(nameof(avaPlot));
        _getPosition = getPosition ?? throw new ArgumentNullException(nameof(getPosition));
        _getOffset = getOffset ?? throw new ArgumentNullException(nameof(getOffset));
        _commitPlacement = commitPlacement ?? throw new ArgumentNullException(nameof(commitPlacement));
    }

    /// <summary>
    /// Subscribe to the plot's pointer events. Idempotent — calling
    /// <c>Attach</c> twice has the same effect as calling it once.
    /// </summary>
    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        // Tunnel routing は WPF の Preview 系に相当。ScottPlot 自身が PointerPressed を
        // bubble で拾って zoom / pan を始めてしまうため、Tunnel で先に拾って
        // e.Handled=true をつけて伝播を止める。
        _avaPlot.AddHandler(InputElement.PointerPressedEvent, OnPress, RoutingStrategies.Tunnel);
        _avaPlot.AddHandler(InputElement.PointerMovedEvent, OnMove, RoutingStrategies.Tunnel);
        _avaPlot.AddHandler(InputElement.PointerReleasedEvent, OnRelease, RoutingStrategies.Tunnel);
        _avaPlot.AddHandler(InputElement.PointerCaptureLostEvent, OnLostCapture, RoutingStrategies.Bubble);
    }

    /// <summary>
    /// Unsubscribe from the plot's pointer events. Safe to call without
    /// a prior <see cref="Attach"/>.
    /// </summary>
    public void Detach()
    {
        if (!_attached) return;
        _avaPlot.RemoveHandler(InputElement.PointerPressedEvent, OnPress);
        _avaPlot.RemoveHandler(InputElement.PointerMovedEvent, OnMove);
        _avaPlot.RemoveHandler(InputElement.PointerReleasedEvent, OnRelease);
        _avaPlot.RemoveHandler(InputElement.PointerCaptureLostEvent, OnLostCapture);
        _attached = false;
    }

    private void OnPress(object? sender, PointerPressedEventArgs e)
    {
        // 左ボタン以外は無視（右ボタン = ScottPlot 既定の context menu / pan、中央 = autoscale）
        if (!e.GetCurrentPoint(_avaPlot).Properties.IsLeftButtonPressed) return;
        if (!TryGetLegendPixelRect(out var rect)) return;

        var p = e.GetPosition(_avaPlot);
        var pixelX = (float)(p.X * _avaPlot.DisplayScale);
        var pixelY = (float)(p.Y * _avaPlot.DisplayScale);

        if (!Contains(rect, pixelX, pixelY)) return;

        _maybeDragging = true;
        _dragging = false;
        _pressPoint = p;
        _startLegendLeft = rect.Left;
        _startLegendTop = rect.Top;
        _startLegendW = rect.Right - rect.Left;
        _startLegendH = rect.Bottom - rect.Top;
        _draftPosition = _getPosition();
        var (ox, oy) = _getOffset();
        _draftOffsetX = ox;
        _draftOffsetY = oy;

        e.Pointer.Capture(_avaPlot);
        _capturedPointer = e.Pointer;
        _avaPlot.Cursor = SizeAllCursor;
        e.Handled = true;
    }

    private void OnMove(object? sender, PointerEventArgs e)
    {
        if (!_maybeDragging) return;

        var p = e.GetPosition(_avaPlot);
        var dxDiu = p.X - _pressPoint.X;
        var dyDiu = p.Y - _pressPoint.Y;

        if (!_dragging)
        {
            if (Math.Abs(dxDiu) < DragThresholdDiu && Math.Abs(dyDiu) < DragThresholdDiu) return;
            _dragging = true;
        }

        var dxPx = (float)(dxDiu * _avaPlot.DisplayScale);
        var dyPx = (float)(dyDiu * _avaPlot.DisplayScale);

        var plot = _avaPlot.Plot;
        ScottPlot.PixelRect dataRect;
        try
        {
            dataRect = plot.LastRender.DataRect;
        }
        catch
        {
            return;
        }

        // Clamp the target legend rect to the data area so a runaway drag
        // can never park the legend off-canvas. Width / height are taken
        // from the press-time legend rect — the legend's own size doesn't
        // change during a drag, only its position.
        var targetLeft = Clamp(_startLegendLeft + dxPx, dataRect.Left, dataRect.Right - _startLegendW);
        var targetTop = Clamp(_startLegendTop + dyPx, dataRect.Top, dataRect.Bottom - _startLegendH);

        var legendCx = targetLeft + _startLegendW / 2f;
        var legendCy = targetTop + _startLegendH / 2f;
        var newPosition = PlotAppearance.ChooseBestLegendAnchor(legendCx, legendCy, dataRect);

        var (newOffsetX, newOffsetY) = PlotAppearance.ComputeOffsetForLegendPosition(
            newPosition, targetLeft, targetTop, _startLegendW, _startLegendH, dataRect);

        // Skip the per-app full refresh: only the legend's anchor and
        // margin change during a drag, so writing them and re-blitting
        // is enough. The commit at PointerReleased triggers the host's
        // normal refresh path so any subsequent ApplyAll picks up the
        // same values via ComputeLegendMargin.
        plot.Legend.Alignment = PlotAppearance.MapLegendAlignment(newPosition);
        plot.Legend.Margin = PlotAppearance.ComputeLegendMargin(newPosition, newOffsetX, newOffsetY);
        _avaPlot.Refresh();

        _draftPosition = newPosition;
        _draftOffsetX = newOffsetX;
        _draftOffsetY = newOffsetY;
        e.Handled = true;
    }

    private void OnRelease(object? sender, PointerReleasedEventArgs e)
    {
        if (_capturedPointer is { } pointer)
        {
            pointer.Capture(null);
            _capturedPointer = null;
        }
        _avaPlot.Cursor = Cursor.Default;

        if (!_maybeDragging) return;

        var wasDragging = _dragging;
        _maybeDragging = false;
        _dragging = false;

        if (!wasDragging) return;

        _commitPlacement(_draftPosition, _draftOffsetX, _draftOffsetY);
        e.Handled = true;
    }

    private void OnLostCapture(object? sender, PointerCaptureLostEventArgs e)
    {
        _maybeDragging = false;
        _dragging = false;
        _capturedPointer = null;
        _avaPlot.Cursor = Cursor.Default;
    }

    private static float Clamp(float value, float min, float max)
    {
        if (max < min) return min;
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private bool TryGetLegendPixelRect(out (float Left, float Right, float Top, float Bottom) rect)
    {
        rect = default;
        var plot = _avaPlot.Plot;
        if (!plot.Legend.IsVisible) return false;

        ScottPlot.PixelRect dataRect;
        try
        {
            dataRect = plot.LastRender.DataRect;
        }
        catch
        {
            return false;
        }

        if (!float.IsFinite(dataRect.Width) || !float.IsFinite(dataRect.Height) || dataRect.Width <= 0 || dataRect.Height <= 0)
        {
            return false;
        }

        ScottPlot.Image? img;
        try
        {
            img = plot.Legend.GetImage();
        }
        catch
        {
            return false;
        }
        if (img is null) return false;

        float w = img.Width;
        float h = img.Height;

        var position = _getPosition();
        var margin = plot.Legend.Margin;

        // Mirror the anchor / center logic in PlotAppearance.ComputeLegendMargin
        // so the rect we hit-test matches what ScottPlot will draw.
        float left;
        if (position.EndsWith("Right", StringComparison.Ordinal))
        {
            left = dataRect.Right - margin.Right - w;
        }
        else if (position.EndsWith("Left", StringComparison.Ordinal))
        {
            left = dataRect.Left + margin.Left;
        }
        else
        {
            left = (dataRect.Left + margin.Left + dataRect.Right - margin.Right - w) / 2f;
        }

        float top;
        if (position.StartsWith("Upper", StringComparison.Ordinal))
        {
            top = dataRect.Top + margin.Top;
        }
        else if (position.StartsWith("Lower", StringComparison.Ordinal))
        {
            top = dataRect.Bottom - margin.Bottom - h;
        }
        else
        {
            top = (dataRect.Top + margin.Top + dataRect.Bottom - margin.Bottom - h) / 2f;
        }

        rect = (Left: left, Right: left + w, Top: top, Bottom: top + h);
        return true;
    }

    private static bool Contains((float Left, float Right, float Top, float Bottom) rect, float x, float y)
    {
        return x >= rect.Left && x <= rect.Right && y >= rect.Top && y <= rect.Bottom;
    }
}
