using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Core;

/// <summary>
/// A layout: a sheet of paper of a given size, with its own drawing space (paper space). Objects drawn on a
/// layout belong to it (<see cref="Entity.SpaceId"/> is the layout's id); viewports on it show the model at a
/// scale. The sheet's lower-left corner is at 0,0 and its size is in drawing units, so a layout is printed at
/// full size.
/// </summary>
public sealed record Layout(Guid Id, string Name, double PaperWidth, double PaperHeight)
{
    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 64 && !name.Any(char.IsControl) &&
        !name.Trim().Equals("Model", StringComparison.OrdinalIgnoreCase);

    public bool IsValid =>
        Id != Guid.Empty && IsValidName(Name) && double.IsFinite(PaperWidth) && double.IsFinite(PaperHeight) && PaperWidth > 0 && PaperHeight > 0;

    /// <summary>The sheet as a box in paper space.</summary>
    public BoundingBox Sheet => new(0, 0, PaperWidth, PaperHeight);
}

/// <summary>
/// A viewport: a rectangular window on a layout through which the model is seen at a scale. It is an object of
/// the layout (it can be selected, moved, resized by its corners, erased and copied).
/// </summary>
public sealed class ViewportEntity : Entity
{
    public const string Type = "viewport";

    public ViewportEntity(Guid id, Point2D center, double width, double height) : base(id)
    {
        Center = center;
        Width = width;
        Height = height;
    }

    public ViewportEntity(Point2D center, double width, double height) : this(Guid.NewGuid(), center, width, height) { }

    /// <summary>Middle of the window, on the sheet.</summary>
    public Point2D Center { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    /// <summary>The model point shown in the middle of the window.</summary>
    public Point2D ModelCenter { get; set; }

    /// <summary>Size on paper of one drawing unit of the model: 1/48 shows the model at 1/4" = 1'-0".</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>
    /// The name of the view ("FOUNDATION PLAN"), written under the lower-left corner of the window with the
    /// viewport's scale on a second line. The scale line is worked out from <see cref="Scale"/> each time it is
    /// drawn, so it cannot go out of date. Empty for no title.
    /// </summary>
    public string Title { get; set; } = "";

    /// <summary>Letter height of the title on the sheet; the scale line is two thirds of it.</summary>
    public double TitleHeight { get; set; }

    /// <summary>
    /// Locked: the wheel and panning inside the viewport, Fit and a new scale are refused, so the view that was
    /// set up cannot be changed by accident. The frame can still be moved and resized.
    /// </summary>
    public bool Locked { get; set; }

    public bool HasTitle => !string.IsNullOrWhiteSpace(Title) && TitleHeight > GeoMath.Epsilon;

    public BoundingBox Frame => new(Center.X - Width / 2, Center.Y - Height / 2, Center.X + Width / 2, Center.Y + Height / 2);

    /// <summary>The part of the model seen through the window.</summary>
    public BoundingBox ModelBounds
    {
        get
        {
            double w = Width / Scale / 2, h = Height / Scale / 2;
            return new BoundingBox(ModelCenter.X - w, ModelCenter.Y - h, ModelCenter.X + w, ModelCenter.Y + h);
        }
    }

    public Point2D PaperToModel(Point2D paper) => ModelCenter + (paper - Center) / Scale;
    public Point2D ModelToPaper(Point2D model) => Center + (model - ModelCenter) * Scale;
    public bool Contains(Point2D paper) => Frame.Contains(paper);

    /// <summary>Makes the window the given rectangle without moving what is seen in it on the sheet.</summary>
    public void SetFrame(BoundingBox frame)
    {
        var newCenter = frame.Center;
        ModelCenter = PaperToModel(newCenter);
        Center = newCenter;
        Width = frame.Width;
        Height = frame.Height;
    }

    /// <summary>Shows <paramref name="model"/> as large as fits in the window (with a little room round it).</summary>
    public void Fit(BoundingBox model)
    {
        if (model.IsEmpty) return;
        ModelCenter = model.Center;
        double sx = model.Width > 0 ? Width / model.Width : double.PositiveInfinity;
        double sy = model.Height > 0 ? Height / model.Height : double.PositiveInfinity;
        double scale = Math.Min(sx, sy) * 0.95;
        if (double.IsFinite(scale) && scale > 0) Scale = scale;
    }

    private IEnumerable<(Point2D A, Point2D B)> Sides
    {
        get
        {
            var f = Frame;
            var a = new Point2D(f.MinX, f.MinY);
            var b = new Point2D(f.MaxX, f.MinY);
            var c = new Point2D(f.MaxX, f.MaxY);
            var d = new Point2D(f.MinX, f.MaxY);
            yield return (a, b);
            yield return (b, c);
            yield return (c, d);
            yield return (d, a);
        }
    }

    public override string TypeName => Type;
    public override BoundingBox Bounds => Frame;

    public override bool IsValid =>
        Center.IsFinite && ModelCenter.IsFinite && GeoMath.IsFinite(Width) && GeoMath.IsFinite(Height) && Width > GeoMath.Epsilon && Height > GeoMath.Epsilon &&
        GeoMath.IsFinite(Scale) && Scale > 1e-12 &&
        Title is not null && Title.Length <= 128 && !Title.Any(char.IsControl) && GeoMath.IsFinite(TitleHeight) && TitleHeight >= 0;

    /// <summary>Only the border picks the viewport, so what is drawn inside the window can still be clicked past.</summary>
    public override double DistanceTo(Point2D point) => Sides.Min(s => SegmentMath.DistanceToPoint(s.A, s.B, point));

    public override bool IntersectsBox(BoundingBox box) => Sides.Any(s => SegmentMath.IntersectsBox(s.A, s.B, box));

    public override void Transform(in Transform2D transform)
    {
        // A viewport stays upright: it is moved and resized, never turned.
        Center = transform.Apply(Center);
        double k = transform.ScaleFactor;
        Width *= k;
        Height *= k;
        Scale *= k;
        TitleHeight *= k;
    }

    protected override Entity CloneGeometry(Guid id) => new ViewportEntity(id, Center, Width, Height) { ModelCenter = ModelCenter, Scale = Scale, Title = Title, TitleHeight = TitleHeight, Locked = Locked };
}

/// <summary>How a scale is written on a drawing.</summary>
public static class ScaleNames
{
    /// <summary>
    /// A scale (paper size over real size) as people write it: 1/4" = 1'-0" for the usual building scales in
    /// feet and inches, otherwise 1:48.
    /// </summary>
    public static string Name(double scale, DrawingSettings settings)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        if (!(scale > 0) || !double.IsFinite(scale)) return "?";
        if (UnitConverter.IsImperial(settings.Unit) && scale < 1)
        {
            // Inches on paper for one foot of the real thing.
            double perFoot = scale * 12.0;
            foreach (int denominator in new[] { 1, 2, 4, 8, 16, 32 })
            {
                double numerator = perFoot * denominator;
                if (Math.Abs(numerator - Math.Round(numerator)) < 1e-9 && Math.Round(numerator) >= 1)
                {
                    long n = (long)Math.Round(numerator);
                    string inches = denominator == 1 ? n.ToString(invariant) : n >= denominator && n % denominator == 0 ? (n / denominator).ToString(invariant)
                        : n > denominator ? $"{n / denominator} {n % denominator}/{denominator}" : $"{n}/{denominator}";
                    return $"{inches}\" = 1'-0\"";
                }
            }
        }
        double ratio = 1.0 / scale;
        if (Math.Abs(ratio - Math.Round(ratio)) < 1e-9 * Math.Max(1, ratio)) return "1:" + Math.Round(ratio).ToString("0", invariant);
        if (scale > 1 && Math.Abs(scale - Math.Round(scale)) < 1e-9) return Math.Round(scale).ToString("0", invariant) + ":1";
        return "1:" + ratio.ToString("0.###", invariant);
    }
}
