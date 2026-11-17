using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Core;

/// <summary>
/// One grid line: a straight line or an arc with a label, shown in a circle at one end, at both ends or not at
/// all. The label circle is part of the object, so it follows when an end is stretched. The grid lines of one
/// grid system share a <see cref="Entity.GroupId"/>.
/// </summary>
public sealed class GridLineEntity : Entity
{
    public const string Type = "gridline";
    public const int MaxLabelLength = 32;

    private GridLineEntity(Guid id, bool isArc) : base(id) => IsArc = isArc;

    /// <summary>A straight grid line.</summary>
    public GridLineEntity(Guid id, Point2D start, Point2D end) : this(id, false)
    {
        Start = start;
        End = end;
    }

    public GridLineEntity(Point2D start, Point2D end) : this(Guid.NewGuid(), start, end) { }

    /// <summary>A grid line along an arc, counter-clockwise from <paramref name="startAngle"/> to <paramref name="endAngle"/> (radians).</summary>
    public GridLineEntity(Guid id, Point2D center, double radius, double startAngle, double endAngle) : this(id, true)
    {
        Center = center;
        Radius = radius;
        StartAngle = ArcMath.NormalizeAngle(startAngle);
        EndAngle = ArcMath.NormalizeAngle(endAngle);
    }

    public GridLineEntity(Point2D center, double radius, double startAngle, double endAngle)
        : this(Guid.NewGuid(), center, radius, startAngle, endAngle) { }

    public bool IsArc { get; }

    // Straight
    public Point2D Start { get; set; }
    public Point2D End { get; set; }

    // Arc
    public Point2D Center { get; set; }
    public double Radius { get; set; }
    public double StartAngle { get; set; }
    public double EndAngle { get; set; }

    /// <summary>The name of the grid line ("A", "3.5"). May be empty.</summary>
    public string Label { get; set; } = "";

    /// <summary>Diameter of the label circle, in drawing units.</summary>
    public double BubbleDiameter { get; set; } = 12;

    /// <summary>Label at the start (the bottom or left end of a grid as the wizard draws it).</summary>
    public bool LabelAtStart { get; set; }

    /// <summary>Label at the end (the top or right end).</summary>
    public bool LabelAtEnd { get; set; } = true;

    public double Sweep => ArcMath.Sweep(StartAngle, EndAngle);
    public Point2D StartPoint => IsArc ? ArcMath.PointAt(Center, Radius, StartAngle) : Start;
    public Point2D EndPoint => IsArc ? ArcMath.PointAt(Center, Radius, StartAngle + Sweep) : End;
    public Point2D MidPoint => IsArc ? ArcMath.PointAt(Center, Radius, StartAngle + Sweep / 2.0) : Point2D.Midpoint(Start, End);

    /// <summary>Height of the label text: as tall as fits in the circle, less for a long label.</summary>
    public double TextHeight => Math.Min(0.45 * BubbleDiameter, 0.8 * BubbleDiameter / (0.6 * Math.Max(Label.Length, 1)));

    /// <summary>Centres of the label circles that are drawn (none when there is no label).</summary>
    public IReadOnlyList<Point2D> BubbleCenters
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Label) || !(BubbleDiameter > 0) || (!LabelAtStart && !LabelAtEnd)) return Array.Empty<Point2D>();
            double r = BubbleDiameter / 2.0;
            Vector2D outAtStart, outAtEnd;
            if (IsArc)
            {
                double end = StartAngle + Sweep;
                outAtStart = new Vector2D(Math.Sin(StartAngle), -Math.Cos(StartAngle));      // backwards along the arc
                outAtEnd = new Vector2D(-Math.Sin(end), Math.Cos(end));                      // onwards along the arc
            }
            else
            {
                var u = (End - Start).Normalized();
                if (u == Vector2D.Zero) return Array.Empty<Point2D>();
                (outAtStart, outAtEnd) = (-u, u);
            }
            var centers = new List<Point2D>(2);
            if (LabelAtStart) centers.Add(StartPoint + outAtStart * r);
            if (LabelAtEnd) centers.Add(EndPoint + outAtEnd * r);
            return centers;
        }
    }

    public override string TypeName => Type;

    public override bool IsValid
    {
        get
        {
            if (Label is null || Label.Length > MaxLabelLength || Label.Any(char.IsControl)) return false;
            if (!GeoMath.IsFinite(BubbleDiameter) || BubbleDiameter < 0) return false;
            return IsArc
                ? Center.IsFinite && GeoMath.IsFinite(Radius) && Radius > GeoMath.Epsilon && GeoMath.IsFinite(StartAngle) && GeoMath.IsFinite(EndAngle) && Sweep > 1e-9
                : Start.IsFinite && End.IsFinite && !Start.NearlyEquals(End);
        }
    }

    public override BoundingBox Bounds
    {
        get
        {
            var box = IsArc ? ArcMath.ArcBounds(Center, Radius, StartAngle, Sweep) : BoundingBox.FromCorners(Start, End);
            foreach (var c in BubbleCenters) box = box.Union(BoundingBox.FromCenter(c, BubbleDiameter / 2.0));
            return box;
        }
    }

    public override double DistanceTo(Point2D point)
    {
        double best = IsArc ? ArcMath.DistanceToArc(Center, Radius, StartAngle, Sweep, point) : SegmentMath.DistanceToPoint(Start, End, point);
        foreach (var c in BubbleCenters) best = Math.Min(best, Math.Max(0, c.DistanceTo(point) - BubbleDiameter / 2.0));   // anywhere in the circle
        return best;
    }

    public override bool IntersectsBox(BoundingBox box)
    {
        if (IsArc ? ArcMath.ArcIntersectsBox(Center, Radius, StartAngle, Sweep, box) : SegmentMath.IntersectsBox(Start, End, box)) return true;
        foreach (var c in BubbleCenters)
            if (CircleMath.CircumferenceIntersectsBox(c, BubbleDiameter / 2.0, box) || box.Contains(c)) return true;
        return false;
    }

    public override void Transform(in Transform2D transform)
    {
        BubbleDiameter *= transform.ScaleFactor;
        if (!IsArc)
        {
            Start = transform.Apply(Start);
            End = transform.Apply(End);
            return;
        }

        bool pureTranslation = transform.M11 == 1 && transform.M12 == 0 && transform.M21 == 0 && transform.M22 == 1;
        if (pureTranslation)
        {
            Center = transform.Apply(Center);
            return;
        }
        var start = transform.Apply(StartPoint);
        var end = transform.Apply(EndPoint);
        Center = transform.Apply(Center);
        Radius *= transform.ScaleFactor;
        double a1 = (start - Center).Angle, a2 = (end - Center).Angle;
        if (transform.Determinant < 0)
        {
            // A mirror reverses the direction of travel, and with it which end is which.
            (a1, a2) = (a2, a1);
            (LabelAtStart, LabelAtEnd) = (LabelAtEnd, LabelAtStart);
        }
        StartAngle = ArcMath.NormalizeAngle(a1);
        EndAngle = ArcMath.NormalizeAngle(a2);
    }

    protected override Entity CloneGeometry(Guid id)
    {
        var copy = IsArc ? new GridLineEntity(id, Center, Radius, StartAngle, EndAngle) : new GridLineEntity(id, Start, End);
        copy.Label = Label;
        copy.BubbleDiameter = BubbleDiameter;
        copy.LabelAtStart = LabelAtStart;
        copy.LabelAtEnd = LabelAtEnd;
        return copy;
    }
}
