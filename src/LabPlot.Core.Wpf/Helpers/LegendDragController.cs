using System.Windows;
using System.Windows.Input;
using LabPlot.Core;
using ScottPlot.WPF;

namespace LabPlot.Core.Wpf.Helpers;

/// <summary>
/// Adds mouse-drag support for the ScottPlot 5 legend on a
/// <see cref="WpfPlot"/>. Apps wire it up by handing in three lambdas
/// against their <see cref="GraphFormattingConfigBase"/> snapshot:
/// the current anchor (e.g. <c>"UpperRight"</c>), the current
/// <c>(LegendOffsetX, LegendOffsetY)</c>, and a commit callback that
/// fires once when the drag ends so the per-app code can update the
/// formatting config and the offset TextBoxes in <c>GraphFormatPanel</c>.
/// </summary>
/// <remarks>
/// During a drag the controller writes <c>plot.Legend.Margin</c>
/// directly via <see cref="PlotAppearance.ComputeLegendMargin"/> and
/// calls <c>WpfPlot.Refresh()</c> — bypassing the per-app
/// <c>ApplyPlotAppearance</c> rebuild keeps the move responsive even
/// when the host's full refresh path is heavy (the DLS app re-runs
/// scatter selection logic on every refresh, for example). The commit
/// callback is fired exactly once at <c>MouseUp</c>, so the host can
/// run its full refresh + sync the panel TextBoxes there.
///
/// Hit-testing is done against the rectangle the legend would occupy
/// given the current anchor / margin and <c>plot.Legend.GetImage()</c>
/// for size — ScottPlot 5.1.58 does not expose a public hit-test for
/// the legend, so we re-derive the rect every press. WPF mouse points
/// (device-independent units) are scaled by <c>WpfPlot.DisplayScale</c>
/// before comparing to the ScottPlot pixel rect, which lives in
/// physical pixels at the current DPI.
/// </remarks>
public sealed class LegendDragController
{
    /// <summary>
    /// Movement (in WPF DIU) below this threshold counts as a click,
    /// not a drag. Keeps a single click on the legend from accidentally
    /// shifting the anchor by a pixel or two.
    /// </summary>
    private const double DragThresholdDiu = 3.0;

    private readonly WpfPlot _wpfPlot;
    private readonly Func<string> _getPosition;
    private readonly Func<(double X, double Y)> _getOffset;
    private readonly Action<double, double> _commitOffset;

    private bool _attached;
    private bool _maybeDragging;
    private bool _dragging;
    private Point _pressPoint;
    private double _startOffsetX;
    private double _startOffsetY;

    public LegendDragController(
        WpfPlot wpfPlot,
        Func<string> getPosition,
        Func<(double X, double Y)> getOffset,
        Action<double, double> commitOffset)
    {
        _wpfPlot = wpfPlot ?? throw new ArgumentNullException(nameof(wpfPlot));
        _getPosition = getPosition ?? throw new ArgumentNullException(nameof(getPosition));
        _getOffset = getOffset ?? throw new ArgumentNullException(nameof(getOffset));
        _commitOffset = commitOffset ?? throw new ArgumentNullException(nameof(commitOffset));
    }

    /// <summary>
    /// Subscribe to the plot's mouse events. Idempotent — calling
    /// <c>Attach</c> twice has the same effect as calling it once.
    /// </summary>
    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _wpfPlot.PreviewMouseLeftButtonDown += OnPress;
        _wpfPlot.PreviewMouseMove += OnMove;
        _wpfPlot.PreviewMouseLeftButtonUp += OnRelease;
        _wpfPlot.LostMouseCapture += OnLostCapture;
    }

    /// <summary>
    /// Unsubscribe from the plot's mouse events. Safe to call without
    /// a prior <see cref="Attach"/>.
    /// </summary>
    public void Detach()
    {
        if (!_attached) return;
        _wpfPlot.PreviewMouseLeftButtonDown -= OnPress;
        _wpfPlot.PreviewMouseMove -= OnMove;
        _wpfPlot.PreviewMouseLeftButtonUp -= OnRelease;
        _wpfPlot.LostMouseCapture -= OnLostCapture;
        _attached = false;
    }

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        if (!TryGetLegendPixelRect(out var rect)) return;

        var p = e.GetPosition(_wpfPlot);
        var pixelX = (float)(p.X * _wpfPlot.DisplayScale);
        var pixelY = (float)(p.Y * _wpfPlot.DisplayScale);

        if (!Contains(rect, pixelX, pixelY)) return;

        _maybeDragging = true;
        _dragging = false;
        _pressPoint = p;
        var (ox, oy) = _getOffset();
        _startOffsetX = ox;
        _startOffsetY = oy;
        _wpfPlot.CaptureMouse();
        _wpfPlot.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_maybeDragging) return;

        var p = e.GetPosition(_wpfPlot);
        var dxDiu = p.X - _pressPoint.X;
        var dyDiu = p.Y - _pressPoint.Y;

        if (!_dragging)
        {
            if (Math.Abs(dxDiu) < DragThresholdDiu && Math.Abs(dyDiu) < DragThresholdDiu) return;
            _dragging = true;
        }

        var (newX, newY) = ProjectMouseToOffset(dxDiu, dyDiu);

        // Skip the per-app full refresh: the legend's geometry is the
        // only thing changing during a drag, so writing Margin and
        // re-blitting is enough. The commit at MouseUp triggers the
        // host's normal refresh path.
        var plot = _wpfPlot.Plot;
        plot.Legend.Margin = PlotAppearance.ComputeLegendMargin(_getPosition(), newX, newY);
        _wpfPlot.Refresh();
        e.Handled = true;
    }

    private void OnRelease(object sender, MouseButtonEventArgs e)
    {
        if (_wpfPlot.IsMouseCaptured) _wpfPlot.ReleaseMouseCapture();
        _wpfPlot.ClearValue(FrameworkElement.CursorProperty);

        if (!_maybeDragging) return;

        var wasDragging = _dragging;
        _maybeDragging = false;
        _dragging = false;

        if (!wasDragging) return;

        var p = e.GetPosition(_wpfPlot);
        var dxDiu = p.X - _pressPoint.X;
        var dyDiu = p.Y - _pressPoint.Y;
        var (finalX, finalY) = ProjectMouseToOffset(dxDiu, dyDiu);

        _commitOffset(finalX, finalY);
        e.Handled = true;
    }

    private void OnLostCapture(object sender, MouseEventArgs e)
    {
        _maybeDragging = false;
        _dragging = false;
        _wpfPlot.ClearValue(FrameworkElement.CursorProperty);
    }

    private (double X, double Y) ProjectMouseToOffset(double dxDiu, double dyDiu)
    {
        var dxPx = dxDiu * _wpfPlot.DisplayScale;
        var dyPx = dyDiu * _wpfPlot.DisplayScale;
        var newX = ClampOffset(_startOffsetX + dxPx);
        var newY = ClampOffset(_startOffsetY + dyPx);
        return (newX, newY);
    }

    private static double ClampOffset(double value)
    {
        if (value < -GraphFormattingConfigBase.LegendOffsetLimit) return -GraphFormattingConfigBase.LegendOffsetLimit;
        if (value > GraphFormattingConfigBase.LegendOffsetLimit) return GraphFormattingConfigBase.LegendOffsetLimit;
        return value;
    }

    private bool TryGetLegendPixelRect(out (float Left, float Right, float Top, float Bottom) rect)
    {
        rect = default;
        var plot = _wpfPlot.Plot;
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
