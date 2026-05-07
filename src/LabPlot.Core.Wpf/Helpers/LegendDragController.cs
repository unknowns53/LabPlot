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
/// formatting config and the placement controls in
/// <c>GraphFormatPanel</c>.
/// </summary>
/// <remarks>
/// During a drag the controller picks the **best 9-cell anchor** every
/// frame (<see cref="PlotAppearance.ChooseBestLegendAnchor"/>) and
/// re-derives the matching offsets via
/// <see cref="PlotAppearance.ComputeOffsetForLegendPosition"/>. Without
/// this auto-pick, large drags from a corner anchor accumulate giant
/// <c>Legend.Margin</c> values that ScottPlot is happy to consume but
/// that push the legend off the data area visually. By rebasing onto the
/// nearest anchor mid-drag, every offset stays within roughly one third
/// of the data area on each axis — small enough that the legend always
/// lands inside the plot.
///
/// The target legend rectangle is also clamped to <c>DataRect</c> so a
/// runaway drag cannot fling the legend outside the data area at all.
/// Hit-testing is done by mirroring
/// <see cref="PlotAppearance.ComputeLegendMargin"/>'s anchor / center
/// logic — ScottPlot 5.1.58 does not expose a public hit-test for the
/// legend, so we re-derive the rect every press. WPF mouse points
/// (device-independent units) are scaled by <c>WpfPlot.DisplayScale</c>
/// before comparing to the ScottPlot pixel rect, which lives in
/// physical pixels at the current DPI.
///
/// During a drag the controller writes <c>plot.Legend.Alignment</c>
/// and <c>plot.Legend.Margin</c> directly and calls
/// <c>WpfPlot.Refresh()</c> — bypassing the per-app
/// <c>ApplyPlotAppearance</c> rebuild keeps the move responsive even
/// when the host's full refresh path is heavy. The commit callback
/// fires exactly once at <c>MouseUp</c> with the final anchor + offsets
/// so the host can run its full refresh + sync the panel controls.
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

    public LegendDragController(
        WpfPlot wpfPlot,
        Func<string> getPosition,
        Func<(double X, double Y)> getOffset,
        Action<string, double, double> commitPlacement)
    {
        _wpfPlot = wpfPlot ?? throw new ArgumentNullException(nameof(wpfPlot));
        _getPosition = getPosition ?? throw new ArgumentNullException(nameof(getPosition));
        _getOffset = getOffset ?? throw new ArgumentNullException(nameof(getOffset));
        _commitPlacement = commitPlacement ?? throw new ArgumentNullException(nameof(commitPlacement));
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
        _startLegendLeft = rect.Left;
        _startLegendTop = rect.Top;
        _startLegendW = rect.Right - rect.Left;
        _startLegendH = rect.Bottom - rect.Top;
        _draftPosition = _getPosition();
        var (ox, oy) = _getOffset();
        _draftOffsetX = ox;
        _draftOffsetY = oy;
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

        var dxPx = (float)(dxDiu * _wpfPlot.DisplayScale);
        var dyPx = (float)(dyDiu * _wpfPlot.DisplayScale);

        var plot = _wpfPlot.Plot;
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
        // is enough. The commit at MouseUp triggers the host's normal
        // refresh path so any subsequent ApplyAll picks up the same
        // values via ComputeLegendMargin.
        plot.Legend.Alignment = PlotAppearance.MapLegendAlignment(newPosition);
        plot.Legend.Margin = PlotAppearance.ComputeLegendMargin(newPosition, newOffsetX, newOffsetY);
        _wpfPlot.Refresh();

        _draftPosition = newPosition;
        _draftOffsetX = newOffsetX;
        _draftOffsetY = newOffsetY;
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

        _commitPlacement(_draftPosition, _draftOffsetX, _draftOffsetY);
        e.Handled = true;
    }

    private void OnLostCapture(object sender, MouseEventArgs e)
    {
        _maybeDragging = false;
        _dragging = false;
        _wpfPlot.ClearValue(FrameworkElement.CursorProperty);
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
