using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Core;

/// <summary>
/// The block library that comes with HAQQ CAD: plan symbols for furniture, plumbing fixtures, kitchen
/// appliances and doors. They are HAQQ's own drawings, made here from lines, arcs and circles to common
/// sizes (in inches); nothing is taken from another program's symbol files. Sizes are typical ones: always
/// check them against the product that will really be installed.
/// The base point of a symbol is the middle of its back edge (the side that goes against the wall), so it
/// can be put on a wall line and turned; tables, drains and the water heater have it at their centre.
/// </summary>
public static class StandardBlocks
{
    public const string Furniture = "Furniture";
    public const string Bedroom = "Bedroom";
    public const string Plumbing = "Plumbing";
    public const string Kitchen = "Kitchen and laundry";
    public const string Doors = "Doors";

    private static readonly Lazy<IReadOnlyList<BlockDefinition>> Library = new(Build);

    /// <summary>Every symbol, drawn in inches.</summary>
    public static IReadOnlyList<BlockDefinition> All => Library.Value;

    public static IReadOnlyList<string> Categories { get; } = new[] { Furniture, Bedroom, Plumbing, Kitchen, Doors };

    /// <summary>A symbol at the size it has in a drawing with the given unit.</summary>
    public static BlockDefinition ForUnit(BlockDefinition block, LinearUnit unit) =>
        block.Scaled(UnitConverter.Convert(1.0, LinearUnit.Inch, unit));

    // ------------------------------------------------------------------ shapes

    /// <summary>Collects the objects of one symbol. X is along the wall, Y is out from it (negative = into the room).</summary>
    private sealed class Shape
    {
        public List<Entity> Items { get; } = new();

        public Shape Line(double x1, double y1, double x2, double y2)
        {
            Items.Add(new LineEntity(new Point2D(x1, y1), new Point2D(x2, y2)));
            return this;
        }

        /// <summary>A rectangle from its lower-left corner.</summary>
        public Shape Rect(double x, double y, double w, double h)
        {
            Items.Add(new PolylineEntity(new[] { new Point2D(x, y), new Point2D(x + w, y), new Point2D(x + w, y + h), new Point2D(x, y + h) }, true));
            return this;
        }

        /// <summary>A rectangle with rounded corners.</summary>
        public Shape Round(double x, double y, double w, double h, double r)
        {
            r = Math.Min(r, Math.Min(w, h) / 2.0 - 1e-6);
            if (!(r > 1e-6)) return Rect(x, y, w, h);
            double b = Math.Tan(Math.PI / 8.0);          // a quarter turn
            var points = new[]
            {
                new Point2D(x + r, y), new Point2D(x + w - r, y), new Point2D(x + w, y + r), new Point2D(x + w, y + h - r),
                new Point2D(x + w - r, y + h), new Point2D(x + r, y + h), new Point2D(x, y + h - r), new Point2D(x, y + r),
            };
            Items.Add(new PolylineEntity(points, true, new[] { 0, b, 0, b, 0, b, 0, b }));
            return this;
        }

        public Shape Circle(double x, double y, double r)
        {
            Items.Add(new CircleEntity(new Point2D(x, y), r));
            return this;
        }

        /// <summary>An arc, angles in degrees, counter-clockwise from start to end.</summary>
        public Shape Arc(double x, double y, double r, double startDegrees, double endDegrees)
        {
            Items.Add(new ArcEntity(new Point2D(x, y), r, startDegrees * Math.PI / 180.0, endDegrees * Math.PI / 180.0));
            return this;
        }

        /// <summary>An oval, as a closed outline of short straight pieces.</summary>
        public Shape Oval(double cx, double cy, double rx, double ry)
        {
            const int n = 36;
            var points = new Point2D[n];
            for (int i = 0; i < n; i++)
            {
                double a = i * 2.0 * Math.PI / n;
                points[i] = new Point2D(cx + rx * Math.Cos(a), cy + ry * Math.Sin(a));
            }
            Items.Add(new PolylineEntity(points, true));
            return this;
        }

        /// <summary>A chair seen from above: seat with a back rail on the side given by the angle (0 = back towards +Y).</summary>
        public Shape Chair(double cx, double cy, double size, int quarterTurns)
        {
            double h = size / 2.0, rail = size * 0.18;
            // Seat and back in the chair's own axes, then turned by quarter turns about its middle.
            (double X, double Y) Turn(double x, double y) => (quarterTurns & 3) switch
            {
                0 => (x, y),
                1 => (-y, x),
                2 => (-x, -y),
                _ => (y, -x),
            };
            void Box(double x1, double y1, double x2, double y2)
            {
                var a = Turn(x1, y1);
                var b = Turn(x2, y2);
                Rect(cx + Math.Min(a.X, b.X), cy + Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
            }
            Box(-h, -h, h, h - rail);
            Box(-h, h - rail, h, h);
            return this;
        }
    }

    private static BlockDefinition Make(string name, string category, string description, Action<Shape> draw)
    {
        var shape = new Shape();
        draw(shape);
        return new BlockDefinition(name, shape.Items, description, category);
    }

    /// <summary>A seat with arms and a back against the wall: sofa, loveseat, armchair.</summary>
    private static void Seating(Shape s, double width, double depth, int cushions)
    {
        const double arm = 6, back = 7;
        s.Round(-width / 2, -depth, width, depth, 3);
        s.Line(-width / 2, -back, width / 2, -back);                              // the back
        s.Line(-width / 2 + arm, -back, -width / 2 + arm, -depth);                // the arms
        s.Line(width / 2 - arm, -back, width / 2 - arm, -depth);
        double seat = (width - 2 * arm) / cushions;
        for (int i = 1; i < cushions; i++) s.Line(-width / 2 + arm + i * seat, -back, -width / 2 + arm + i * seat, -depth);
    }

    private static void Bed(Shape s, double width, double length, int pillows)
    {
        s.Rect(-width / 2, -length, width, length);
        s.Line(-width / 2, -length * 0.28, width / 2, -length * 0.28);            // the turned-down cover
        double pillowWidth = pillows == 1 ? width * 0.6 : width * 0.42, gap = (width - pillows * pillowWidth) / (pillows + 1);
        for (int i = 0; i < pillows; i++) s.Round(-width / 2 + gap + i * (pillowWidth + gap), -length * 0.22, pillowWidth, length * 0.16, 2);
    }

    private static void Basin(Shape s, double cx, double cy, double rx, double ry)
    {
        s.Oval(cx, cy, rx, ry);
        s.Circle(cx, cy, 0.9);                                                    // the waste
    }

    /// <summary>A mixer tap seen from above: body on the back edge, spout into the room, two handles.</summary>
    private static void Faucet(Shape s, double cx, double cy)
    {
        s.Round(cx - 0.75, cy - 5, 1.5, 5, 0.7);                                  // spout
        s.Circle(cx - 3.5, cy - 0.5, 1.1);
        s.Circle(cx + 3.5, cy - 0.5, 1.1);
        s.Line(cx - 2.4, cy - 0.5, cx - 0.75, cy - 0.5);
        s.Line(cx + 0.75, cy - 0.5, cx + 2.4, cy - 0.5);
    }

    private static void SwingDoor(Shape s, double width)
    {
        // The opening runs from 0,0 along +X; the leaf is shown open at a right angle, with its swing.
        s.Rect(0, 0, 1.75, width);
        s.Arc(0, 0, width, 0, 90);
        s.Line(-2, 0, 0, 0);
        s.Line(width, 0, width + 2, 0);
    }

    // ----------------------------------------------------------------- library

    private static IReadOnlyList<BlockDefinition> Build()
    {
        var all = new List<BlockDefinition>
        {
            // ---- Furniture
            Make("Sofa 84x36", Furniture, "Three-seat sofa, 84 x 36 in", s => Seating(s, 84, 36, 3)),
            Make("Loveseat 60x36", Furniture, "Two-seat sofa, 60 x 36 in", s => Seating(s, 60, 36, 2)),
            Make("Armchair 36x36", Furniture, "Armchair, 36 x 36 in", s => Seating(s, 36, 36, 1)),
            Make("Coffee table 48x24", Furniture, "Coffee table, 48 x 24 in (base point at its centre)", s => s.Round(-24, -12, 48, 24, 2)),
            Make("Chair 18x18", Furniture, "Side chair, 18 x 18 in", s => s.Chair(0, -9, 18, 0)),
            Make("Dining table 72x36 - 6 chairs", Furniture, "Dining table 72 x 36 in with six chairs (base point at its centre)", s =>
            {
                s.Rect(-36, -18, 72, 36);
                foreach (double x in new[] { -20.0, 20.0 })
                {
                    s.Chair(x, 18 + 6, 18, 0);
                    s.Chair(x, -18 - 6, 18, 2);
                }
                s.Chair(-36 - 6, 0, 18, 1);
                s.Chair(36 + 6, 0, 18, 3);
            }),
            Make("Round table 48 - 4 chairs", Furniture, "Round table 48 in across with four chairs (base point at its centre)", s =>
            {
                s.Circle(0, 0, 24);
                s.Chair(0, 24 + 6, 18, 0);
                s.Chair(-24 - 6, 0, 18, 1);
                s.Chair(0, -24 - 6, 18, 2);
                s.Chair(24 + 6, 0, 18, 3);
            }),
            Make("Desk 60x30 with chair", Furniture, "Desk 60 x 30 in with a chair", s =>
            {
                s.Rect(-30, -30, 60, 30);
                s.Chair(0, -30 - 8, 18, 2);
            }),
            Make("Bookcase 36x12", Furniture, "Bookcase, 36 x 12 in", s =>
            {
                s.Rect(-18, -12, 36, 12);
                s.Line(-18, -12, 18, 0);
            }),
            Make("TV console 60x18", Furniture, "Media console 60 x 18 in with a screen", s =>
            {
                s.Rect(-30, -18, 60, 18);
                s.Rect(-24, -11, 48, 2);
            }),

            // ---- Bedroom
            Make("Bed twin 38x75", Bedroom, "Twin bed, 38 x 75 in", s => Bed(s, 38, 75, 1)),
            Make("Bed full 54x75", Bedroom, "Full (double) bed, 54 x 75 in", s => Bed(s, 54, 75, 2)),
            Make("Bed queen 60x80", Bedroom, "Queen bed, 60 x 80 in", s => Bed(s, 60, 80, 2)),
            Make("Bed king 76x80", Bedroom, "King bed, 76 x 80 in", s => Bed(s, 76, 80, 2)),
            Make("Nightstand 20x18", Bedroom, "Nightstand, 20 x 18 in", s =>
            {
                s.Rect(-10, -18, 20, 18);
                s.Circle(0, -9, 4);                                               // a lamp
            }),
            Make("Dresser 60x20", Bedroom, "Dresser, 60 x 20 in", s =>
            {
                s.Rect(-30, -20, 60, 20);
                s.Line(0, -20, 0, 0);
            }),
            Make("Wardrobe 48x24", Bedroom, "Wardrobe, 48 x 24 in, with its hanging rail", s =>
            {
                s.Rect(-24, -24, 48, 24);
                s.Line(-22, -12, 22, -12);
                foreach (double x in new[] { -15.0, -5.0, 5.0, 15.0 }) s.Line(x - 2, -18, x + 2, -6);   // hangers
            }),

            // ---- Plumbing
            Make("Toilet 20x28", Plumbing, "Water closet with tank, 20 x 28 in", s =>
            {
                s.Round(-10, -8, 20, 8, 1.5);                                     // the tank, against the wall
                s.Oval(0, -18.5, 7.5, 9.5);                                       // the bowl
                s.Oval(0, -18.5, 5, 7);
                s.Line(-4, -8, -4, -9.6);
                s.Line(4, -8, 4, -9.6);
            }),
            Make("Lavatory 20x18", Plumbing, "Wall-hung lavatory (hand basin), 20 x 18 in", s =>
            {
                s.Round(-10, -18, 20, 18, 3);
                Basin(s, 0, -10.5, 7, 5.5);
                Faucet(s, 0, -1.5);
            }),
            Make("Vanity 30x22", Plumbing, "Vanity cabinet 30 x 22 in with one basin", s =>
            {
                s.Rect(-15, -22, 30, 22);
                Basin(s, 0, -12, 8, 6);
                Faucet(s, 0, -2);
            }),
            Make("Vanity 60x22 double", Plumbing, "Vanity cabinet 60 x 22 in with two basins", s =>
            {
                s.Rect(-30, -22, 60, 22);
                foreach (double x in new[] { -15.0, 15.0 })
                {
                    Basin(s, x, -12, 8, 6);
                    Faucet(s, x, -2);
                }
            }),
            Make("Bathtub 60x30", Plumbing, "Bathtub, 60 x 30 in, drain at the right", s =>
            {
                s.Rect(-30, -30, 60, 30);
                s.Round(-27, -27, 54, 24, 8);
                s.Circle(21, -15, 1.2);
            }),
            Make("Shower 36x36", Plumbing, "Shower base, 36 x 36 in", s =>
            {
                s.Rect(-18, -36, 36, 36);
                s.Line(-18, -36, 18, 0);
                s.Line(-18, 0, 18, -36);
                s.Circle(0, -18, 1.75);
            }),
            Make("Shower 60x32", Plumbing, "Shower base, 60 x 32 in", s =>
            {
                s.Rect(-30, -32, 60, 32);
                s.Line(-30, -32, 30, 0);
                s.Line(-30, 0, 30, -32);
                s.Circle(0, -16, 1.75);
            }),
            Make("Urinal 18x14", Plumbing, "Wall-hung urinal, 18 x 14 in", s =>
            {
                s.Line(-9, 0, 9, 0);
                s.Arc(0, 0, 9, 180, 360);
                s.Line(-9, 0, -9, -5);
                s.Line(9, 0, 9, -5);
                s.Oval(0, -6.5, 5.5, 5);
            }),
            Make("Faucet", Plumbing, "Mixer tap seen from above (spout and two handles), about 9 in wide", s => Faucet(s, 0, 0)),
            Make("Floor drain 6", Plumbing, "Round floor drain, 6 in across (base point at its centre)", s =>
            {
                s.Circle(0, 0, 3);
                s.Circle(0, 0, 2);
                s.Line(-1.4, -1.4, 1.4, 1.4);
                s.Line(-1.4, 1.4, 1.4, -1.4);
            }),
            Make("Water heater 22", Plumbing, "Tank water heater, 22 in across (base point at its centre)", s =>
            {
                s.Circle(0, 0, 11);
                s.Circle(0, 0, 9.5);
                s.Circle(-4, 4, 1);
                s.Circle(4, 4, 1);
            }),
            Make("Hose bibb", Plumbing, "Outside tap on a wall, plan symbol", s =>
            {
                s.Line(0, 0, 0, -4);
                s.Line(-2.5, -4, 2.5, -4);
                s.Circle(0, -5.5, 1.5);
            }),

            // ---- Kitchen and laundry
            Make("Kitchen sink 33x22 double", Kitchen, "Double-bowl kitchen sink, 33 x 22 in", s =>
            {
                s.Round(-16.5, -22, 33, 22, 1.5);
                s.Round(-14.5, -20, 13.5, 15.5, 2);
                s.Round(1, -20, 13.5, 15.5, 2);
                s.Circle(-7.75, -12.25, 1.5);
                s.Circle(7.75, -12.25, 1.5);
                Faucet(s, 0, -1.5);
            }),
            Make("Kitchen sink 25x22 single", Kitchen, "Single-bowl kitchen sink, 25 x 22 in", s =>
            {
                s.Round(-12.5, -22, 25, 22, 1.5);
                s.Round(-10.5, -20, 21, 15.5, 2);
                s.Circle(0, -12.25, 1.5);
                Faucet(s, 0, -1.5);
            }),
            Make("Range 30x26", Kitchen, "Range (cooker) with four burners, 30 x 26 in", s =>
            {
                s.Rect(-15, -26, 30, 26);
                s.Line(-15, -3, 15, -3);                                          // the control panel at the back
                foreach (double x in new[] { -7.5, 7.5 })
                    foreach (double y in new[] { -9.0, -19.5 })
                    {
                        s.Circle(x, y, 3.8);
                        s.Circle(x, y, 1.6);
                    }
            }),
            Make("Refrigerator 36x30", Kitchen, "Refrigerator, 36 x 30 in, with the swing of its doors", s =>
            {
                s.Rect(-18, -30, 36, 30);
                s.Line(-18, -27, 18, -27);
                s.Line(0, -27, 0, -30);
                s.Arc(-18, -30, 18, 270, 360);
                s.Arc(18, -30, 18, 180, 270);
            }),
            Make("Dishwasher 24x24", Kitchen, "Dishwasher, 24 x 24 in", s =>
            {
                s.Rect(-12, -24, 24, 24);
                s.Line(-12, -21, 12, -21);
                s.Line(-9, -3, 9, -3);
                s.Line(-9, -3, -9, -18);
                s.Line(9, -3, 9, -18);
                s.Line(-9, -18, 9, -18);
            }),
            Make("Washer 27x30", Kitchen, "Clothes washer, 27 x 30 in", s =>
            {
                s.Rect(-13.5, -30, 27, 30);
                s.Line(-13.5, -4, 13.5, -4);
                s.Circle(0, -17, 9.5);
                s.Circle(0, -17, 2);
            }),
            Make("Dryer 27x30", Kitchen, "Clothes dryer, 27 x 30 in", s =>
            {
                s.Rect(-13.5, -30, 27, 30);
                s.Line(-13.5, -4, 13.5, -4);
                s.Circle(0, -17, 9.5);
                s.Rect(-4, -21, 8, 8);
            }),

            // ---- Doors
            Make("Door 30 swing", Doors, "Hinged door, 30 in opening. Base point at the hinge; the opening runs to the right", s => SwingDoor(s, 30)),
            Make("Door 32 swing", Doors, "Hinged door, 32 in opening. Base point at the hinge; the opening runs to the right", s => SwingDoor(s, 32)),
            Make("Door 36 swing", Doors, "Hinged door, 36 in opening. Base point at the hinge; the opening runs to the right", s => SwingDoor(s, 36)),
            Make("Door 72 double", Doors, "Pair of hinged doors, 72 in opening. Base point at the left hinge", s =>
            {
                s.Rect(0, 0, 1.75, 36);
                s.Arc(0, 0, 36, 0, 90);
                s.Rect(72 - 1.75, 0, 1.75, 36);
                s.Arc(72, 0, 36, 90, 180);
                s.Line(-2, 0, 0, 0);
                s.Line(72, 0, 74, 0);
            }),
            Make("Door 60 sliding", Doors, "Sliding door, 60 in opening, two panels. Base point at the left jamb", s =>
            {
                s.Rect(0, 0, 31, 1.5);
                s.Rect(29, -1.5, 31, 1.5);
                s.Line(-2, 0, 0, 0);
                s.Line(60, 0, 62, 0);
            }),
        };
        return all;
    }
}
