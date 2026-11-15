using HAQQ.CAD.Geometry;
using Xunit;
using static HAQQ.CAD.Tests.T;

namespace HAQQ.CAD.Tests;

public class GeometryTests
{
    [Fact]
    public void Distance_and_midpoint()
    {
        Assert.Equal(5.0, P(0, 0).DistanceTo(P(3, 4)), 12);
        Assert.Equal(P(1.5, 2), Point2D.Midpoint(P(0, 0), P(3, 4)));
    }

    [Fact]
    public void Vector_operations()
    {
        var v = new Vector2D(3, 4);
        Assert.Equal(5.0, v.Length, 12);
        Assert.Equal(1.0, v.Normalized().Length, 12);
        Assert.Equal(0.0, v.Dot(new Vector2D(-4, 3)), 12);
        Assert.Equal(25.0, v.Cross(new Vector2D(-4, 3)), 12);
        var r = new Vector2D(1, 0).Rotate(Math.PI / 2);
        Assert.Equal(0.0, r.X, 12);
        Assert.Equal(1.0, r.Y, 12);
        Assert.Equal(Vector2D.Zero, Vector2D.Zero.Normalized());
    }

    [Fact]
    public void Closest_point_is_clamped_to_the_segment()
    {
        Assert.Equal(P(5, 0), SegmentMath.ClosestPoint(P(0, 0), P(10, 0), P(5, 7)));
        Assert.Equal(P(0, 0), SegmentMath.ClosestPoint(P(0, 0), P(10, 0), P(-4, 3)));
        Assert.Equal(P(10, 0), SegmentMath.ClosestPoint(P(0, 0), P(10, 0), P(14, 3)));
        Assert.Equal(5.0, SegmentMath.DistanceToPoint(P(0, 0), P(10, 0), P(14, 3)), 12);
        // Degenerate segment behaves like a point.
        Assert.Equal(5.0, SegmentMath.DistanceToPoint(P(1, 1), P(1, 1), P(4, 5)), 12);
    }

    [Fact]
    public void Segment_intersection_point()
    {
        Assert.True(SegmentMath.TryIntersect(P(0, 0), P(10, 10), P(0, 10), P(10, 0), out var hit));
        Assert.Equal(5.0, hit.X, 12);
        Assert.Equal(5.0, hit.Y, 12);

        // Touching at an endpoint counts.
        Assert.True(SegmentMath.TryIntersect(P(0, 0), P(10, 0), P(10, 0), P(10, 5), out hit));
        Assert.Equal(P(10, 0), hit);

        // Parallel and separated segments do not intersect.
        Assert.False(SegmentMath.TryIntersect(P(0, 0), P(10, 0), P(0, 1), P(10, 1), out _));
        Assert.False(SegmentMath.TryIntersect(P(0, 0), P(1, 1), P(5, 0), P(6, -1), out _));
    }

    [Fact]
    public void Segment_intersection_is_accurate_for_large_coordinates()
    {
        // Typical site coordinates: millions of units from the origin.
        double ox = 2_500_000, oy = 7_250_000;
        Assert.True(SegmentMath.TryIntersect(P(ox, oy), P(ox + 10, oy + 10), P(ox, oy + 10), P(ox + 10, oy), out var hit));
        Assert.Equal(ox + 5, hit.X, 6);
        Assert.Equal(oy + 5, hit.Y, 6);
    }

    [Fact]
    public void Segments_intersect_predicate_handles_collinear_overlap()
    {
        Assert.True(SegmentMath.SegmentsIntersect(P(0, 0), P(10, 0), P(5, 0), P(15, 0)));
        Assert.False(SegmentMath.SegmentsIntersect(P(0, 0), P(10, 0), P(11, 0), P(15, 0)));
        Assert.True(SegmentMath.SegmentsIntersect(P(0, 0), P(10, 10), P(0, 10), P(10, 0)));
        Assert.False(SegmentMath.SegmentsIntersect(P(0, 0), P(1, 1), P(3, 0), P(4, -1)));
    }

    [Fact]
    public void Segment_box_intersection()
    {
        var box = new BoundingBox(0, 0, 10, 10);
        Assert.True(SegmentMath.IntersectsBox(P(-5, 5), P(15, 5), box));   // passes through, no endpoint inside
        Assert.True(SegmentMath.IntersectsBox(P(2, 2), P(3, 3), box));     // entirely inside
        Assert.True(SegmentMath.IntersectsBox(P(5, 5), P(50, 50), box));   // one endpoint inside
        Assert.False(SegmentMath.IntersectsBox(P(-5, 12), P(15, 12), box)); // passes above
        Assert.False(SegmentMath.IntersectsBox(P(11, -5), P(25, 9), box));  // bounding boxes overlap but no contact
    }

    [Fact]
    public void Circle_circumference_box_intersection()
    {
        var c = P(0, 0);
        Assert.True(CircleMath.CircumferenceIntersectsBox(c, 10, new BoundingBox(8, -1, 12, 1)));   // straddles the curve
        Assert.False(CircleMath.CircumferenceIntersectsBox(c, 10, new BoundingBox(-2, -2, 2, 2)));  // box inside the circle
        Assert.False(CircleMath.CircumferenceIntersectsBox(c, 10, new BoundingBox(20, 20, 30, 30))); // box outside
        Assert.True(CircleMath.CircumferenceIntersectsBox(c, 10, new BoundingBox(-20, -20, 20, 20))); // circle inside the box
        Assert.Equal(2.0, CircleMath.DistanceToCircumference(c, 10, P(8, 0)), 12);
        Assert.Equal(3.0, CircleMath.DistanceToCircumference(c, 10, P(0, 13)), 12);
    }

    [Fact]
    public void Bounding_box_operations()
    {
        Assert.True(BoundingBox.Empty.IsEmpty);
        Assert.Equal(0.0, BoundingBox.Empty.Width);

        var a = BoundingBox.FromCorners(P(5, 8), P(1, 2));
        Assert.Equal(new BoundingBox(1, 2, 5, 8), a);
        Assert.Equal(4.0, a.Width);
        Assert.Equal(6.0, a.Height);
        Assert.Equal(P(3, 5), a.Center);

        var b = new BoundingBox(4, 7, 9, 12);
        Assert.True(a.Intersects(b));
        Assert.False(a.Intersects(new BoundingBox(6, 0, 7, 1)));
        Assert.Equal(new BoundingBox(1, 2, 9, 12), a.Union(b));
        Assert.Equal(a, BoundingBox.Empty.Union(a));
        Assert.True(a.Contains(P(1, 2)));
        Assert.False(a.Contains(P(0.99, 2)));
        Assert.True(a.Contains(new BoundingBox(2, 3, 4, 7)));
        Assert.False(a.Contains(b));
        Assert.False(a.Intersects(BoundingBox.Empty));
        Assert.Equal(new BoundingBox(0, 1, 6, 9), a.Inflate(1));
    }

    [Fact]
    public void Transforms()
    {
        var move = Transform2D.Translation(new Vector2D(3, -2));
        Assert.Equal(P(4, -1), move.Apply(P(1, 1)));

        var rotate = Transform2D.Rotation(Math.PI / 2, P(1, 1));
        var r = rotate.Apply(P(2, 1));
        Assert.Equal(1.0, r.X, 12);
        Assert.Equal(2.0, r.Y, 12);

        var scale = Transform2D.Scaling(2, P(1, 1));
        Assert.Equal(P(3, 1), scale.Apply(P(2, 1)));
        Assert.Equal(2.0, scale.ScaleFactor, 12);
        Assert.Equal(1.0, rotate.ScaleFactor, 12);

        // "move then rotate" composed into one transform.
        var combined = rotate.After(move);
        var expected = rotate.Apply(move.Apply(P(5, 5)));
        var actual = combined.Apply(P(5, 5));
        Assert.Equal(expected.X, actual.X, 12);
        Assert.Equal(expected.Y, actual.Y, 12);
    }

    [Fact]
    public void Polyline_length_and_area()
    {
        var rect = new[] { P(0, 0), P(4, 0), P(4, 3), P(0, 3) };
        Assert.Equal(11.0, PolylineMath.Length(rect, closed: false), 12);
        Assert.Equal(14.0, PolylineMath.Length(rect, closed: true), 12);
        Assert.Equal(12.0, PolylineMath.SignedArea(rect), 12);
        Assert.Equal(-12.0, PolylineMath.SignedArea(rect.Reverse().ToArray()), 12);
    }
}
