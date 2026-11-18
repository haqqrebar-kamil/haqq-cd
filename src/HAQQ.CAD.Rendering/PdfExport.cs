using System.Globalization;
using System.IO.Compression;
using System.Text;
using HAQQ.CAD.Core;

namespace HAQQ.CAD.Rendering;

/// <summary>
/// Draws onto a PDF page: every line, circle, filled shape and piece of text becomes vector content of the
/// page, not a picture. Used by <see cref="PdfExporter"/> with the same renderer that paints the screen, so a
/// PDF shows what a plot shows. HAQQ's own writer.
/// Text is set in the PDF's built-in Helvetica (regular, bold, oblique), whatever font it has in the drawing.
/// </summary>
internal sealed class PdfRenderSurface : IRenderSurface
{
    private const double CapHeight = 0.718;            // Helvetica: height of a capital against the font size
    private readonly StringBuilder _out = new();
    private readonly double _pageHeight;

    public PdfRenderSurface(double pageHeight)
    {
        _pageHeight = pageHeight;
        _out.Append("1 J 1 j\n");                      // round ends and corners, as on screen
    }

    public string Content => _out.ToString();

    private static string N(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
    private static string M(double v) => Math.Round(v, 7).ToString("0.#######", CultureInfo.InvariantCulture);
    private string Y(double y) => N(_pageHeight - y);
    private static string C(byte v) => (v / 255.0).ToString("0.###", CultureInfo.InvariantCulture);

    private void Stroke(in RenderStyle style)
    {
        _out.Append(C(style.R)).Append(' ').Append(C(style.G)).Append(' ').Append(C(style.B)).Append(" RG ");
        _out.Append(N(Math.Max(style.Width, 0.1))).Append(" w ");
        if (style.Pattern is { Length: > 0 } pattern)
        {
            _out.Append('[');
            foreach (double part in pattern) _out.Append(N(Math.Max(Math.Abs(part), 0.01))).Append(' ');
            _out.Append("] 0 d\n");
        }
        else _out.Append(style.Dashed ? "[5 4] 0 d\n" : "[] 0 d\n");
    }

    private void Fill(in RenderStyle style) =>
        _out.Append(C(style.R)).Append(' ').Append(C(style.G)).Append(' ').Append(C(style.B)).Append(" rg\n");

    public void DrawLine(double x1, double y1, double x2, double y2, in RenderStyle style)
    {
        Stroke(style);
        _out.Append(N(x1)).Append(' ').Append(Y(y1)).Append(" m ").Append(N(x2)).Append(' ').Append(Y(y2)).Append(" l S\n");
    }

    private void Path(IReadOnlyList<ScreenPoint> points, bool closed)
    {
        for (int i = 0; i < points.Count; i++)
            _out.Append(N(points[i].X)).Append(' ').Append(Y(points[i].Y)).Append(i == 0 ? " m " : " l ");
        if (closed) _out.Append("h ");
    }

    public void DrawPolyline(IReadOnlyList<ScreenPoint> points, bool closed, in RenderStyle style)
    {
        if (points.Count < 2) return;
        Stroke(style);
        Path(points, closed);
        _out.Append("S\n");
    }

    public void FillPolygon(IReadOnlyList<ScreenPoint> points, in RenderStyle style)
    {
        if (points.Count < 3) return;
        Fill(style);
        Path(points, true);
        _out.Append("f\n");
    }

    public void FillRegion(IReadOnlyList<IReadOnlyList<ScreenPoint>> loops, in RenderStyle style)
    {
        bool any = false;
        foreach (var loop in loops)
        {
            if (loop.Count < 3) continue;
            if (!any) Fill(style);
            any = true;
            Path(loop, true);
        }
        if (any) _out.Append("f*\n");                    // even-odd: a loop inside a loop is a hole
    }

    public void DrawCircle(double cx, double cy, double radius, in RenderStyle style)
    {
        if (!(radius > 0)) return;
        Stroke(style);
        // Four curves; k puts their control points so that they follow the circle to a few parts in ten thousand.
        double k = 0.5522847498 * radius, y = _pageHeight - cy;
        _out.Append(N(cx + radius)).Append(' ').Append(N(y)).Append(" m ");
        void Curve(double ax, double ay, double bx, double by, double ex, double ey) =>
            _out.Append(N(ax)).Append(' ').Append(N(ay)).Append(' ').Append(N(bx)).Append(' ').Append(N(by)).Append(' ').Append(N(ex)).Append(' ').Append(N(ey)).Append(" c ");
        Curve(cx + radius, y + k, cx + k, y + radius, cx, y + radius);
        Curve(cx - k, y + radius, cx - radius, y + k, cx - radius, y);
        Curve(cx - radius, y - k, cx - k, y - radius, cx, y - radius);
        Curve(cx + k, y - radius, cx + radius, y - k, cx + radius, y);
        _out.Append("h S\n");
    }

    public void DrawRectangle(double x, double y, double width, double height, in RenderStyle style, byte fillAlpha)
    {
        string box = $"{N(x)} {N(_pageHeight - y - height)} {N(width)} {N(height)} re ";
        if (fillAlpha == 255)
        {
            Fill(style);
            _out.Append(box).Append("f\n");
        }
        else if (fillAlpha == 0)
        {
            Stroke(style);
            _out.Append(box).Append("S\n");
        }
        // Half-transparent boxes are screen aids (the selection window) and are not part of a sheet.
    }

    public void DrawText(string text, double cx, double cy, double height, double angle, in RenderStyle style) =>
        DrawStyledText(text, cx, cy, height, angle, new TextLook("Helvetica", false, false, false, 0), style);

    public void DrawStyledText(string text, double x, double y, double height, double angle, in TextLook look, in RenderStyle style)
    {
        if (string.IsNullOrEmpty(text) || !(height > 0)) return;
        double size = height / CapHeight;
        double width = HelveticaWidth(text, look.Bold) * size;
        double along = look.Anchor < 0 ? 0 : look.Anchor == 0 ? -width / 2 : -width;
        // (x, y) is the middle of the capitals' height; PDF text starts at the left end of its baseline.
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        double px = x, py = _pageHeight - y;
        double bx = px + along * cos + (height / 2) * sin, by = py + along * sin - (height / 2) * cos;

        Fill(style);
        string font = look.Bold ? (look.Italic ? "F4" : "F2") : look.Italic ? "F3" : "F1";
        _out.Append("BT /").Append(font).Append(' ').Append(N(size)).Append(" Tf ")
            .Append(M(cos)).Append(' ').Append(M(sin)).Append(' ').Append(M(-sin)).Append(' ').Append(M(cos)).Append(' ').Append(N(bx)).Append(' ').Append(N(by)).Append(" Tm (");
        foreach (char c in text)
        {
            // The built-in fonts hold Western letters; anything else is shown as a question mark.
            char w = c is >= ' ' and <= '~' or >= '¡' and <= 'ÿ' ? c : c switch
            {
                '‘' or '’' => '\'',
                '“' or '”' => '"',
                '–' or '—' => '-',
                ' ' => ' ',
                _ => '?',
            };
            if (w is '(' or ')' or '\\') _out.Append('\\');
            _out.Append(w);
        }
        _out.Append(") Tj ET\n");

        if (look.Underline)
        {
            double drop = size * 0.1, ux = bx + drop * sin, uy = by - drop * cos;
            _out.Append(C(style.R)).Append(' ').Append(C(style.G)).Append(' ').Append(C(style.B)).Append(" RG ").Append(N(size * 0.05)).Append(" w [] 0 d ")
                .Append(N(ux)).Append(' ').Append(N(uy)).Append(" m ").Append(N(ux + width * cos)).Append(' ').Append(N(uy + width * sin)).Append(" l S\n");
        }
    }

    public void PushClip(double x, double y, double width, double height) =>
        _out.Append("q ").Append(N(x)).Append(' ').Append(N(_pageHeight - y - height)).Append(' ').Append(N(Math.Max(width, 0))).Append(' ').Append(N(Math.Max(height, 0))).Append(" re W n\n");

    public void PopClip() => _out.Append("Q\n");

    // Widths of Helvetica's letters from space to tilde, in thousandths of the font size (the font's published metrics).
    private static readonly short[] Widths =
    {
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    };

    /// <summary>Width of a line of text for a font size of 1. Bold letters are taken a little wider.</summary>
    internal static double HelveticaWidth(string text, bool bold)
    {
        double total = 0;
        foreach (char c in text) total += c is >= ' ' and <= '~' ? Widths[c - ' '] : 556;
        return total / 1000.0 * (bold ? 1.06 : 1.0);
    }
}

/// <summary>
/// Makes a vector PDF of layouts (and, if wanted, the model): one page per sheet, each page the true size of
/// its sheet, with what a plot of it shows - layers with Plot off left out, lineweights, viewports cut off
/// at their frames. The model has no sheet, so it is fitted to a Letter or A4 page.
/// </summary>
public static class PdfExporter
{
    private const double PointsPerInch = 72.0;

    /// <param name="spaces">The tabs to export, in page order: layout ids, or <see cref="Guid.Empty"/> for the model.</param>
    /// <param name="monochrome">Everything black (true), or in the objects' colours.</param>
    public static byte[] Export(DrawingDocument document, IReadOnlyList<Guid> spaces, bool monochrome)
    {
        var pages = new List<(double Width, double Height, string Content)>();
        Guid before = document.ActiveSpaceId;
        try
        {
            foreach (Guid space in spaces)
            {
                if (space != Guid.Empty && document.GetLayout(space) is null) continue;
                document.ActiveSpaceId = space;                 // which tab is open is not part of the drawing's content

                double width, height;
                if (document.ActiveLayout is { } layout)
                {
                    double perUnit = PointsPerInch * UnitConverter.Convert(1.0, document.Settings.Unit, LinearUnit.Inch);
                    width = layout.PaperWidth * perUnit;
                    height = layout.PaperHeight * perUnit;
                }
                else
                {
                    bool metric = !UnitConverter.IsImperial(document.Settings.Unit), wide = PlotPlanner.PrefersLandscape(document);
                    double a = metric ? 841.89 : 792, b = metric ? 595.28 : 612;
                    (width, height) = wide ? (a, b) : (b, a);
                }
                if (!(width > 1) || !(height > 1) || width > 14400 || height > 14400)
                    throw new InvalidOperationException("A PDF page can be at most 200 inches (5080 mm) on a side.");

                var plan = PlotPlanner.Plan(document, width, height, unitsPerInch: PointsPerInch);
                var surface = new PdfRenderSurface(height);
                new SceneRenderer().Render(surface, document, plan.View, new RenderOptions
                {
                    Plot = true,
                    PlotMonochrome = monochrome,
                    ShowGrid = false,
                    PixelsPerMm = plan.PixelsPerMm,
                    MinTextSize = 0.5,
                });
                pages.Add((width, height, surface.Content));
            }
        }
        finally
        {
            document.ActiveSpaceId = before;
        }
        if (pages.Count == 0) throw new InvalidOperationException("There is nothing to export.");
        return Write(pages);
    }

    private static byte[] Write(List<(double Width, double Height, string Content)> pages)
    {
        var file = new MemoryStream();
        var offsets = new List<long> { 0 };
        void Text(string s)
        {
            var bytes = Encoding.Latin1.GetBytes(s);
            file.Write(bytes, 0, bytes.Length);
        }
        int Begin()
        {
            offsets.Add(file.Position);
            Text($"{offsets.Count - 1} 0 obj\n");
            return offsets.Count - 1;
        }
        static string N(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);

        Text("%PDF-1.4\n%âãÏÓ\n");
        // Objects: 1 catalog, 2 page tree, 3-6 fonts, then a page and its content for every sheet.
        int catalog = Begin();
        Text("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int tree = Begin();
        string kids = string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => $"{7 + i * 2} 0 R"));
        Text($"<< /Type /Pages /Count {pages.Count} /Kids [{kids}] >>\nendobj\n");
        foreach (string font in new[] { "Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique" })
        {
            Begin();
            Text($"<< /Type /Font /Subtype /Type1 /BaseFont /{font} /Encoding /WinAnsiEncoding >>\nendobj\n");
        }
        foreach (var (width, height, content) in pages)
        {
            int page = Begin();
            Text($"<< /Type /Page /Parent {tree} 0 R /MediaBox [0 0 {N(width)} {N(height)}] /Contents {page + 1} 0 R " +
                 "/Resources << /Font << /F1 3 0 R /F2 4 0 R /F3 5 0 R /F4 6 0 R >> >> >>\nendobj\n");
            byte[] packed;
            using (var buffer = new MemoryStream())
            {
                using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                {
                    var raw = Encoding.Latin1.GetBytes(content);
                    zlib.Write(raw, 0, raw.Length);
                }
                packed = buffer.ToArray();
            }
            Begin();
            Text($"<< /Length {packed.Length} /Filter /FlateDecode >>\nstream\n");
            file.Write(packed, 0, packed.Length);
            Text("\nendstream\nendobj\n");
        }

        long xref = file.Position;
        Text($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        for (int i = 1; i < offsets.Count; i++) Text($"{offsets[i]:D10} 00000 n \n");
        Text($"trailer\n<< /Size {offsets.Count} /Root {catalog} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return file.ToArray();
    }
}
