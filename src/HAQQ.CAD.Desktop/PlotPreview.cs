using System.Windows;
using System.Windows.Media;
using HAQQ.CAD.Core;
using HAQQ.CAD.Rendering;
using CadRenderOptions = HAQQ.CAD.Rendering.RenderOptions;

namespace HAQQ.CAD.Desktop;

/// <summary>
/// Shows on a white page what a plot of the active space will look like: black or coloured lines with their
/// lineweights, without the layers whose Plot switch is off.
/// </summary>
internal sealed class PlotPreview : FrameworkElement
{
    private static readonly Brush PageBrush = Brushes.White;
    private static readonly Pen PageEdge = CreateEdge();

    private readonly SceneRenderer _renderer = new();
    private readonly WpfRenderSurface _surface = new();
    private DrawingDocument? _document;
    private bool _monochrome = true;

    private static Pen CreateEdge()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0x66, 0x70)), 1);
        pen.Freeze();
        return pen;
    }

    public DrawingDocument? Document
    {
        get => _document;
        set { _document = value; InvalidateVisual(); }
    }

    public bool Monochrome
    {
        get => _monochrome;
        set { _monochrome = value; InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_document is null || ActualWidth < 20 || ActualHeight < 20) return;

        // The page: the sheet's proportions for a layout, a landscape or portrait page for the model.
        var area = _document.ActiveLayout?.Sheet ?? _document.GetExtents();
        double aspect = _document.ActiveLayout is not null && area.Height > 0 ? area.Width / area.Height
            : PlotPlanner.PrefersLandscape(_document) ? 11.0 / 8.5 : 8.5 / 11.0;
        double w = ActualWidth - 16, h = ActualHeight - 16;
        if (w / h > aspect) w = h * aspect; else h = w / aspect;
        double x = (ActualWidth - w) / 2, y = (ActualHeight - h) / 2;

        var page = new Rect(x, y, w, h);
        dc.DrawRectangle(PageBrush, PageEdge, page);

        var plan = PlotPlanner.Plan(_document, w, h, fitToPage: true);
        var options = new CadRenderOptions { Plot = true, PlotMonochrome = _monochrome, ShowGrid = false, PixelsPerMm = plan.PixelsPerMm };

        dc.PushClip(new RectangleGeometry(page));
        dc.PushTransform(new TranslateTransform(x, y));
        _surface.Target = dc;
        try
        {
            _renderer.Render(_surface, _document, plan.View, options);
        }
        finally
        {
            _surface.Target = null;
            dc.Pop();
            dc.Pop();
        }
    }
}
