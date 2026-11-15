using System.Globalization;

namespace HAQQ.CAD.Geometry;

/// <summary>Numeric helpers shared by the geometry engine.</summary>
public static class GeoMath
{
    /// <summary>Default absolute tolerance for geometric comparisons, in model units.</summary>
    public const double Epsilon = 1e-9;

    public static bool NearlyEqual(double a, double b, double tolerance = Epsilon) => Math.Abs(a - b) <= tolerance;

    public static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    public static double RadiansToDegrees(double radians) => radians * 180.0 / Math.PI;

    public static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
}

/// <summary>A displacement in model space (double precision).</summary>
public readonly record struct Vector2D(double X, double Y)
{
    public static readonly Vector2D Zero = new(0, 0);

    public double Length => Math.Sqrt(X * X + Y * Y);
    public double LengthSquared => X * X + Y * Y;

    /// <summary>Angle from the +X axis in radians, range (-pi, pi].</summary>
    public double Angle => Math.Atan2(Y, X);

    public Vector2D Normalized()
    {
        double len = Length;
        return len < GeoMath.Epsilon ? Zero : new Vector2D(X / len, Y / len);
    }

    public double Dot(Vector2D other) => X * other.X + Y * other.Y;

    /// <summary>Z component of the 3D cross product (signed parallelogram area).</summary>
    public double Cross(Vector2D other) => X * other.Y - Y * other.X;

    public Vector2D Rotate(double radians)
    {
        double c = Math.Cos(radians), s = Math.Sin(radians);
        return new Vector2D(X * c - Y * s, X * s + Y * c);
    }

    public static Vector2D FromPolar(double length, double radians) =>
        new(length * Math.Cos(radians), length * Math.Sin(radians));

    public static Vector2D operator +(Vector2D a, Vector2D b) => new(a.X + b.X, a.Y + b.Y);
    public static Vector2D operator -(Vector2D a, Vector2D b) => new(a.X - b.X, a.Y - b.Y);
    public static Vector2D operator -(Vector2D a) => new(-a.X, -a.Y);
    public static Vector2D operator *(Vector2D a, double k) => new(a.X * k, a.Y * k);
    public static Vector2D operator *(double k, Vector2D a) => new(a.X * k, a.Y * k);
    public static Vector2D operator /(Vector2D a, double k) => new(a.X / k, a.Y / k);
}

/// <summary>A location in model space. Model coordinates are always doubles; never pixels.</summary>
public readonly record struct Point2D(double X, double Y)
{
    public static readonly Point2D Origin = new(0, 0);

    public bool IsFinite => GeoMath.IsFinite(X) && GeoMath.IsFinite(Y);

    public double DistanceTo(Point2D other)
    {
        double dx = other.X - X, dy = other.Y - Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public bool NearlyEquals(Point2D other, double tolerance = GeoMath.Epsilon) =>
        Math.Abs(X - other.X) <= tolerance && Math.Abs(Y - other.Y) <= tolerance;

    public static Point2D Midpoint(Point2D a, Point2D b) => new((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);

    public static Point2D operator +(Point2D p, Vector2D v) => new(p.X + v.X, p.Y + v.Y);
    public static Point2D operator -(Point2D p, Vector2D v) => new(p.X - v.X, p.Y - v.Y);
    public static Vector2D operator -(Point2D a, Point2D b) => new(a.X - b.X, a.Y - b.Y);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({X:R}, {Y:R})");
}

/// <summary>Axis-aligned bounding box in model space. <see cref="Empty"/> contains nothing.</summary>
public readonly record struct BoundingBox(double MinX, double MinY, double MaxX, double MaxY)
{
    public static readonly BoundingBox Empty =
        new(double.PositiveInfinity, double.PositiveInfinity, double.NegativeInfinity, double.NegativeInfinity);

    public bool IsEmpty => MinX > MaxX || MinY > MaxY;
    public double Width => IsEmpty ? 0 : MaxX - MinX;
    public double Height => IsEmpty ? 0 : MaxY - MinY;
    public Point2D Center => new((MinX + MaxX) / 2.0, (MinY + MaxY) / 2.0);

    public static BoundingBox FromCorners(Point2D a, Point2D b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    public static BoundingBox FromPoints(IEnumerable<Point2D> points)
    {
        var box = Empty;
        foreach (var p in points) box = box.Include(p);
        return box;
    }

    public static BoundingBox FromCenter(Point2D center, double halfSize) =>
        new(center.X - halfSize, center.Y - halfSize, center.X + halfSize, center.Y + halfSize);

    public BoundingBox Include(Point2D p) =>
        new(Math.Min(MinX, p.X), Math.Min(MinY, p.Y), Math.Max(MaxX, p.X), Math.Max(MaxY, p.Y));

    public BoundingBox Union(BoundingBox other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return new(Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY),
                   Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY));
    }

    public BoundingBox Inflate(double amount) =>
        IsEmpty ? this : new(MinX - amount, MinY - amount, MaxX + amount, MaxY + amount);

    public bool Contains(Point2D p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;

    /// <summary>True when <paramref name="other"/> lies entirely inside (or on the edge of) this box.</summary>
    public bool Contains(BoundingBox other) =>
        !other.IsEmpty && other.MinX >= MinX && other.MaxX <= MaxX && other.MinY >= MinY && other.MaxY <= MaxY;

    public bool Intersects(BoundingBox other) =>
        !IsEmpty && !other.IsEmpty &&
        other.MinX <= MaxX && other.MaxX >= MinX && other.MinY <= MaxY && other.MaxY >= MinY;
}

/// <summary>
/// 2D affine transform: x' = M11*x + M12*y + Dx, y' = M21*x + M22*y + Dy.
/// </summary>
public readonly record struct Transform2D(double M11, double M12, double M21, double M22, double Dx, double Dy)
{
    public static readonly Transform2D Identity = new(1, 0, 0, 1, 0, 0);

    public static Transform2D Translation(Vector2D v) => new(1, 0, 0, 1, v.X, v.Y);

    public static Transform2D Rotation(double radians, Point2D about)
    {
        double c = Math.Cos(radians), s = Math.Sin(radians);
        return new(c, -s, s, c,
                   about.X - c * about.X + s * about.Y,
                   about.Y - s * about.X - c * about.Y);
    }

    public static Transform2D Scaling(double factor, Point2D about) =>
        new(factor, 0, 0, factor, about.X * (1 - factor), about.Y * (1 - factor));

    public double Determinant => M11 * M22 - M12 * M21;

    /// <summary>Uniform length scale of the transform (exact for similarity transforms).</summary>
    public double ScaleFactor => Math.Sqrt(Math.Abs(Determinant));

    public Point2D Apply(Point2D p) => new(M11 * p.X + M12 * p.Y + Dx, M21 * p.X + M22 * p.Y + Dy);

    /// <summary>Returns the transform that applies <paramref name="first"/> and then this one.</summary>
    public Transform2D After(Transform2D first) => new(
        M11 * first.M11 + M12 * first.M21, M11 * first.M12 + M12 * first.M22,
        M21 * first.M11 + M22 * first.M21, M21 * first.M12 + M22 * first.M22,
        M11 * first.Dx + M12 * first.Dy + Dx, M21 * first.Dx + M22 * first.Dy + Dy);
}
