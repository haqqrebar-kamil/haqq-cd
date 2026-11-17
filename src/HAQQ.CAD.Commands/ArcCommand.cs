using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Commands;

/// <summary>
/// ARC, with every method of the ribbon drop-down:
/// 3-Point; Start-Center-End / Angle / Length; Start-End-Angle / Direction / Radius;
/// Center-Start-End / Angle / Length; Continue (tangent to the last line or arc drawn).
/// Arcs are stored counter-clockwise. Signed input follows the usual CAD convention: a positive
/// angle runs counter-clockwise from the start point, a negative one clockwise; a negative chord
/// length or radius gives the major arc (more than 180 degrees).
/// </summary>
public sealed class ArcCommand : CadCommandBase, ICreatesObjects
{
    private enum Stage
    {
        Start, Second, ThreeEnd,
        CenterFirst, StartAfterCenter, CenterAfterStart,
        EndOrAngleLength, IncludedAngle, ChordLength,
        EndAfterStart, CenterOrAngleDirectionRadius, EndAngle, EndDirection, EndRadius,
        ContinueEnd,
    }

    /// <summary>A method chosen up front (ribbon drop-down), so the option prompts are skipped.</summary>
    private enum Preset { None, Sce, Sca, Scl, Sea, Sed, Ser, Cse, Csa, Csl }

    /// <summary>An arc plus where the pen ended up, so the next arc can continue from it.</summary>
    private readonly record struct Candidate(ArcEntity Arc, Point2D End, double EndTangent);

    private const double TwoPi = 2.0 * Math.PI;

    private Stage _stage = Stage.Start;
    private Preset _preset = Preset.None;
    private Point2D _start, _second, _center, _end;
    private double _tangent;

    public override string Name => "ARC";

    public override string Prompt => _stage switch
    {
        Stage.Start => _preset == Preset.None
            ? "ARC  Specify start point or [Center] <Continue>:"
            : "ARC  Specify start point of arc:",
        Stage.Second => "ARC  Specify second point of arc or [Center/End]:",
        Stage.ThreeEnd => "ARC  Specify end point of arc:",
        Stage.CenterFirst or Stage.CenterAfterStart => "ARC  Specify center point of arc:",
        Stage.StartAfterCenter => "ARC  Specify start point of arc:",
        Stage.EndOrAngleLength => _preset == Preset.None
            ? "ARC  Specify end point of arc (counter-clockwise from start) or [Angle/Length]:"
            : "ARC  Specify end point of arc (counter-clockwise from start):",
        Stage.IncludedAngle or Stage.EndAngle => "ARC  Specify included angle in degrees (negative = clockwise):",
        Stage.ChordLength => "ARC  Specify length of chord (negative = major arc):",
        Stage.EndAfterStart => "ARC  Specify end point of arc:",
        Stage.CenterOrAngleDirectionRadius => "ARC  Specify center point of arc or [Angle/Direction/Radius]:",
        Stage.EndDirection => "ARC  Specify tangent direction at the start point (click, or angle in degrees):",
        Stage.EndRadius => "ARC  Specify radius of arc (negative = major arc):",
        _ => "ARC  Specify end point of arc (tangent to the previous line or arc):",
    };

    public override IReadOnlyList<string> Options => _stage switch
    {
        Stage.Start when _preset == Preset.None => new[] { "Center", "Continue" },
        Stage.Second => new[] { "Center", "End" },
        Stage.EndOrAngleLength when _preset == Preset.None => new[] { "Angle", "Length" },
        Stage.CenterOrAngleDirectionRadius => new[] { "Angle", "Direction", "Radius" },
        _ => Array.Empty<string>(),
    };

    /// <summary>The point the rubber band, ORTHO, POLAR and <c>@</c> coordinates are measured from.</summary>
    public override Point2D? LastPoint => _stage switch
    {
        Stage.Start or Stage.CenterFirst => null,
        Stage.ThreeEnd => _second,
        Stage.StartAfterCenter or Stage.EndOrAngleLength or Stage.IncludedAngle => _center,
        Stage.CenterOrAngleDirectionRadius or Stage.EndRadius => _end,
        _ => _start,
    };

    public override CommandResult OnInput(CommandInput input)
    {
        if (input is CommandInput.EnterInput)
            return _stage == Stage.Start && _preset == Preset.None ? BeginContinue() : CommandResult.Finished;

        if (input is CommandInput.KeywordInput k) return OnKeyword(k.Keyword);

        // A bare number is a value at the value prompts, and a distance toward the cursor at the point prompts.
        if (input is CommandInput.NumberInput n)
        {
            switch (_stage)
            {
                case Stage.IncludedAngle: return Finish(FromCenterSweep(_center, _start, GeoMath.DegreesToRadians(n.Value)), "The angle must be between -360 and 360 degrees, and not 0.");
                case Stage.EndAngle: return Finish(FromEndsAngle(_start, _end, GeoMath.DegreesToRadians(n.Value)), "The angle must be between -360 and 360 degrees, and not 0.");
                case Stage.ChordLength: return Finish(FromChord(n.Value), "The chord cannot be longer than the diameter, or 0.");
                case Stage.EndRadius: return Finish(FromEndsRadius(n.Value), "The radius must be at least half the distance between the start and end points.");
                case Stage.EndDirection: return Finish(FromEndsDirection(_start, _end, GeoMath.DegreesToRadians(n.Value)), "That direction points straight at the end point; no arc fits.");
            }
            if (LastPoint is not { } from) return Invalid("Specify a point first (click or type x,y).");
            if (n.Value <= 0) return Invalid("Distance must be positive.");
            return OnPoint(Session.PointFromDistance(from, n.Value));
        }

        return input is CommandInput.PointInput p ? OnPoint(p.Point) : Invalid("Invalid input.");
    }

    private CommandResult OnKeyword(string keyword)
    {
        switch (_stage)
        {
            case Stage.Start when _preset == Preset.None:
                if (Enum.TryParse(keyword, ignoreCase: true, out Preset preset) && preset != Preset.None && !int.TryParse(keyword, out _))
                {
                    _preset = preset;
                    if (preset is Preset.Cse or Preset.Csa or Preset.Csl) _stage = Stage.CenterFirst;
                    return CommandResult.Continue;
                }
                if (keyword.Length >= 2 && Matches(keyword, "CONTINUE")) return BeginContinue();
                if (Matches(keyword, "CENTER")) { _stage = Stage.CenterFirst; return CommandResult.Continue; }
                break;

            case Stage.Second:
                if (Matches(keyword, "CENTER")) { _stage = Stage.CenterAfterStart; return CommandResult.Continue; }
                if (Matches(keyword, "END")) { _stage = Stage.EndAfterStart; return CommandResult.Continue; }
                break;

            case Stage.EndOrAngleLength when _preset == Preset.None:
                if (Matches(keyword, "ANGLE")) { _stage = Stage.IncludedAngle; return CommandResult.Continue; }
                if (Matches(keyword, "LENGTH")) { _stage = Stage.ChordLength; return CommandResult.Continue; }
                break;

            case Stage.CenterOrAngleDirectionRadius:
                if (Matches(keyword, "ANGLE")) { _stage = Stage.EndAngle; return CommandResult.Continue; }
                if (Matches(keyword, "DIRECTION")) { _stage = Stage.EndDirection; return CommandResult.Continue; }
                if (Matches(keyword, "RADIUS")) { _stage = Stage.EndRadius; return CommandResult.Continue; }
                break;
        }
        return Invalid("Invalid option.");
    }

    private CommandResult BeginContinue()
    {
        if (Session.ContinueFrom is not { } last)
            return Invalid("There is no previous line or arc to continue from. Specify a start point.");
        _start = last.Point;
        _tangent = last.Angle;
        _stage = Stage.ContinueEnd;
        return CommandResult.Continue;
    }

    private CommandResult OnPoint(Point2D p)
    {
        switch (_stage)
        {
            case Stage.Start:
                _start = p;
                _stage = _preset switch
                {
                    Preset.Sce or Preset.Sca or Preset.Scl => Stage.CenterAfterStart,
                    Preset.Sea or Preset.Sed or Preset.Ser => Stage.EndAfterStart,
                    _ => Stage.Second,
                };
                return CommandResult.Continue;

            case Stage.Second:
                if (p.NearlyEquals(_start)) return Invalid("That point was already given.");
                _second = p;
                _stage = Stage.ThreeEnd;
                return CommandResult.Continue;

            case Stage.ThreeEnd:
                return Finish(FromThreePoints(p), "The three points are in a straight line.");

            case Stage.CenterFirst:
                _center = p;
                _stage = Stage.StartAfterCenter;
                return CommandResult.Continue;

            case Stage.StartAfterCenter:
                if (p.NearlyEquals(_center)) return Invalid("The start point cannot be the center.");
                _start = p;
                _stage = AfterStartAndCenter();
                return CommandResult.Continue;

            case Stage.CenterAfterStart:
                if (p.NearlyEquals(_start)) return Invalid("The center cannot be the start point.");
                _center = p;
                _stage = AfterStartAndCenter();
                return CommandResult.Continue;

            case Stage.EndOrAngleLength:
                return Finish(FromCenterEnd(p), "That end point does not make an arc.");

            case Stage.IncludedAngle:
                // A click gives the angle of the line from the center to the click, measured from the X axis.
                return Finish(FromCenterSweep(_center, _start, ArcMath.NormalizeAngle((p - _center).Angle)), "That point does not give an angle.");

            case Stage.ChordLength:
                return Finish(FromChord(_start.DistanceTo(p)), "The chord cannot be longer than the diameter, or 0.");

            case Stage.EndAfterStart:
                if (p.NearlyEquals(_start)) return Invalid("The end point cannot be the start point.");
                _end = p;
                _stage = _preset switch
                {
                    Preset.Sea => Stage.EndAngle,
                    Preset.Sed => Stage.EndDirection,
                    Preset.Ser => Stage.EndRadius,
                    _ => Stage.CenterOrAngleDirectionRadius,
                };
                return CommandResult.Continue;

            case Stage.CenterOrAngleDirectionRadius:
                return Finish(FromEndsCenter(p), "That center does not make an arc.");

            case Stage.EndAngle:
                return Finish(FromEndsAngle(_start, _end, ArcMath.NormalizeAngle((p - _start).Angle)), "That point does not give an angle.");

            case Stage.EndDirection:
                if (p.NearlyEquals(_start)) return Invalid("Click away from the start point to show a direction.");
                return Finish(FromEndsDirection(_start, _end, (p - _start).Angle), "That direction points straight at the end point; no arc fits.");

            case Stage.EndRadius:
                return Finish(FromEndsRadius(_end.DistanceTo(p)), "The radius must be at least half the distance between the start and end points.");

            default: // ContinueEnd
                return Finish(FromEndsDirection(_start, p, _tangent), "That point is straight ahead; a line, not an arc, would join it.");
        }
    }

    private Stage AfterStartAndCenter() => _preset switch
    {
        Preset.Sca or Preset.Csa => Stage.IncludedAngle,
        Preset.Scl or Preset.Csl => Stage.ChordLength,
        _ => Stage.EndOrAngleLength,
    };

    private CommandResult Finish(Candidate? candidate, string failure)
    {
        if (candidate is not { } c || !c.Arc.IsValid) return Invalid(failure);
        if (!Session.Execute(new AddEntitiesAction("ARC", new[] { Prepare(c.Arc) }))) return CommandResult.Continue;
        Session.ContinueFrom = (c.End, c.EndTangent);
        return CommandResult.Finished;
    }

    // ------------------------------------------------------------ geometry

    /// <summary>Arc around <paramref name="center"/> from <paramref name="start"/>, turning by a signed sweep (positive = counter-clockwise).</summary>
    private static Candidate? FromCenterSweep(Point2D center, Point2D start, double sweep)
    {
        double radius = center.DistanceTo(start);
        double size = Math.Abs(sweep);
        if (radius <= GeoMath.Epsilon || !double.IsFinite(sweep) || size < 1e-9 || size >= TwoPi - 1e-9) return null;
        double a0 = (start - center).Angle, a1 = a0 + sweep;
        var arc = sweep > 0 ? new ArcEntity(center, radius, a0, a1) : new ArcEntity(center, radius, a1, a0);
        return new Candidate(arc, ArcMath.PointAt(center, radius, a1), a1 + Math.Sign(sweep) * Math.PI / 2.0);
    }

    private Candidate? FromCenterEnd(Point2D toward)
    {
        if (toward.NearlyEquals(_center)) return null;
        return FromCenterSweep(_center, _start, ArcMath.Sweep((_start - _center).Angle, (toward - _center).Angle));
    }

    private Candidate? FromChord(double chord)
    {
        double radius = _center.DistanceTo(_start);
        double size = Math.Abs(chord);
        if (radius <= GeoMath.Epsilon || !double.IsFinite(chord) || size <= GeoMath.Epsilon || size > 2.0 * radius * (1 + 1e-12)) return null;
        double minor = 2.0 * Math.Asin(Math.Min(1.0, size / (2.0 * radius)));
        return FromCenterSweep(_center, _start, chord > 0 ? minor : TwoPi - minor);
    }

    /// <summary>Arc from <paramref name="start"/> to <paramref name="end"/> turning by a signed included angle.</summary>
    private static Candidate? FromEndsAngle(Point2D start, Point2D end, double angle)
    {
        double size = Math.Abs(angle);
        double chord = start.DistanceTo(end);
        if (chord <= GeoMath.Epsilon || !double.IsFinite(angle) || size < 1e-9 || size >= TwoPi - 1e-9) return null;

        // Counter-clockwise travel keeps the center on the left of the chord (on the right once past 180 degrees).
        var along = (end - start) * (1.0 / chord);
        var left = new Vector2D(-along.Y, along.X);
        double offset = chord / 2.0 / Math.Tan(size / 2.0) * Math.Sign(angle);
        var mid = start + along * (chord / 2.0);
        var center = mid + left * offset;
        return FromCenterSweep(center, start, angle);
    }

    private Candidate? FromEndsRadius(double radius)
    {
        double size = Math.Abs(radius);
        double half = _start.DistanceTo(_end) / 2.0;
        if (!double.IsFinite(radius) || half <= GeoMath.Epsilon || size < half * (1 - 1e-12)) return null;
        double minor = 2.0 * Math.Asin(Math.Min(1.0, half / size));
        return FromEndsAngle(_start, _end, radius > 0 ? minor : TwoPi - minor);
    }

    /// <summary>Start, End, Center: the radius comes from the start point; the arc ends on the ray from the center through the end point.</summary>
    private Candidate? FromEndsCenter(Point2D center)
    {
        if (center.NearlyEquals(_start) || center.NearlyEquals(_end)) return null;
        return FromCenterSweep(center, _start, ArcMath.Sweep((_start - center).Angle, (_end - center).Angle));
    }

    /// <summary>Arc from <paramref name="start"/> to <paramref name="end"/> that leaves the start point in the given direction.</summary>
    private static Candidate? FromEndsDirection(Point2D start, Point2D end, double tangentAngle)
    {
        var chord = end - start;
        double length = chord.Length;
        if (length <= GeoMath.Epsilon || !double.IsFinite(tangentAngle)) return null;
        var left = Vector2D.FromPolar(1.0, tangentAngle + Math.PI / 2.0);
        double across = left.Dot(chord);
        if (Math.Abs(across) <= 1e-9 * length) return null;      // end point dead ahead or dead behind
        double k = length * length / (2.0 * across);              // signed: positive = center on the left = counter-clockwise
        var center = start + left * k;
        double a0 = (start - center).Angle, a1 = (end - center).Angle;
        double sweep = k > 0 ? ArcMath.Sweep(a0, a1) : -ArcMath.Sweep(a1, a0);
        return FromCenterSweep(center, start, sweep);
    }

    private Candidate? FromThreePoints(Point2D last)
    {
        if (!ArcMath.TryCircumcenter(_start, _second, last, out var c)) return null;
        double a0 = (_start - c).Angle, a1 = (last - c).Angle;
        bool counterClockwise = (_second - _start).Cross(last - _second) > 0;
        return FromCenterSweep(c, _start, counterClockwise ? ArcMath.Sweep(a0, a1) : -ArcMath.Sweep(a1, a0));
    }

    // ------------------------------------------------------------- preview

    public override IReadOnlyList<Entity> GetPreview(Point2D cursor)
    {
        Candidate? candidate = null;
        Point2D? guideFrom = null;
        switch (_stage)
        {
            case Stage.Start or Stage.CenterFirst:
                return Array.Empty<Entity>();
            case Stage.Second or Stage.CenterAfterStart or Stage.EndAfterStart:
                guideFrom = _start;
                break;
            case Stage.StartAfterCenter:
                guideFrom = _center;
                break;
            case Stage.ThreeEnd:
                candidate = FromThreePoints(cursor);
                break;
            case Stage.EndOrAngleLength:
                candidate = FromCenterEnd(cursor);
                guideFrom = _center;
                break;
            case Stage.IncludedAngle:
                candidate = FromCenterSweep(_center, _start, ArcMath.NormalizeAngle((cursor - _center).Angle));
                guideFrom = _center;
                break;
            case Stage.ChordLength:
                candidate = FromChord(_start.DistanceTo(cursor));
                guideFrom = _start;
                break;
            case Stage.CenterOrAngleDirectionRadius:
                candidate = FromEndsCenter(cursor);
                guideFrom = _start;
                break;
            case Stage.EndAngle:
                candidate = FromEndsAngle(_start, _end, ArcMath.NormalizeAngle((cursor - _start).Angle));
                guideFrom = _start;
                break;
            case Stage.EndDirection:
                candidate = cursor.NearlyEquals(_start) ? null : FromEndsDirection(_start, _end, (cursor - _start).Angle);
                guideFrom = _start;
                break;
            case Stage.EndRadius:
                candidate = FromEndsRadius(_end.DistanceTo(cursor));
                guideFrom = _end;
                break;
            default:
                candidate = FromEndsDirection(_start, cursor, _tangent);
                break;
        }

        var preview = new List<Entity>(2);
        if (candidate is { } c && c.Arc.IsValid) preview.Add(c.Arc);
        if (guideFrom is { } from && !cursor.NearlyEquals(from)) preview.Add(new LineEntity(from, cursor));
        return preview;
    }
}
