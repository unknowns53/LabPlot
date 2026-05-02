using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace GPC_Visualization;

/// <summary>
/// Draws a horizontal accent line above or below a ListBoxItem to show the
/// drop target during drag-and-drop reordering.
/// </summary>
internal sealed class InsertionAdorner : Adorner
{
    private static readonly Pen LinePen = CreatePen();

    public bool IsAbove { get; }

    public InsertionAdorner(UIElement adornedElement, bool isAbove) : base(adornedElement)
    {
        IsAbove = isAbove;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = AdornedElement.RenderSize.Width;
        var height = AdornedElement.RenderSize.Height;
        var y = IsAbove ? 0d : height;

        drawingContext.DrawLine(LinePen, new Point(0, y), new Point(width, y));

        var marker = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
        drawingContext.DrawEllipse(marker, null, new Point(0, y), 3, 3);
        drawingContext.DrawEllipse(marker, null, new Point(width, y), 3, 3);
    }

    private static Pen CreatePen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)), 2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        return pen;
    }
}
