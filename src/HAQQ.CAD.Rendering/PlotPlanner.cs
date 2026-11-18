using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Rendering;

/// <summary>How a space of the drawing is placed on a page: the view to draw it with and what that means.</summary>
public sealed record PlotPlan(ViewTransform View, BoundingBox Area, bool ActualSize, double Shrink, double PixelsPerMm);

/// <summary>
/// Works out how the active space goes on a page. A layout is plotted at its true size, the sheet in the middle
/// of the page, and is only made smaller when the page is smaller than the sheet. The model has no size on paper,
/// so it is fitted to the page.
/// </summary>
public static class PlotPlanner
{
    /// <summary>Page units (WPF device-independent pixels) per inch.</summary>
    public const double UnitsPerInch = 96.0;

    /// <param name="pageWidth">Width of the page, 96 per inch.</param>
    /// <param name="pageHeight">Height of the page, 96 per inch.</param>
    /// <param name="fitToPage">Fill the page whatever the sheet size (a preview), instead of true size.</param>
    /// <param name="unitsPerInch">Page units per inch: 96 for the screen and Windows printing, 72 (points) for a PDF.</param>
    public static PlotPlan Plan(DrawingDocument document, double pageWidth, double pageHeight, bool fitToPage = false, double unitsPerInch = UnitsPerInch)
    {
        var view = new ViewTransform();
        view.SetViewport(Math.Max(pageWidth, 1), Math.Max(pageHeight, 1));

        if (document.ActiveLayout is { } layout)
        {
            double trueScale = unitsPerInch * UnitConverter.Convert(1.0, document.Settings.Unit, LinearUnit.Inch);
            double fitScale = Math.Min(pageWidth / layout.PaperWidth, pageHeight / layout.PaperHeight);
            // A hair of tolerance: a 36 x 24 sheet on a 36 x 24 page is true size.
            bool fits = trueScale <= fitScale * (1 + 1e-6);
            double scale = fitToPage || !fits ? fitScale : trueScale;
            view.SetView(layout.Sheet.Center, scale);
            return new PlotPlan(view, layout.Sheet, !fitToPage && fits, scale / trueScale, unitsPerInch / 25.4 * (scale / trueScale));
        }

        var extents = document.GetExtents();
        if (!extents.IsEmpty) view.ZoomExtents(extents, 0.04);
        return new PlotPlan(view, extents, false, 1.0, unitsPerInch / 25.4);
    }

    /// <summary>The page orientation that suits the active space: true for landscape.</summary>
    public static bool PrefersLandscape(DrawingDocument document)
    {
        var area = document.ActiveLayout?.Sheet ?? document.GetExtents();
        return area.IsEmpty || area.Width >= area.Height;
    }
}
