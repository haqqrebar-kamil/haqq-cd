namespace HAQQ.CAD.Core;

/// <summary>
/// One set of parallel lines of a hatch pattern, in pattern units (1 unit is 1 drawing unit at hatch scale 1).
/// The first line passes through (<see cref="X"/>, <see cref="Y"/>) at <see cref="AngleDeg"/>; each next line is
/// <see cref="Spacing"/> further across and starts <see cref="Shift"/> further along. <see cref="Dashes"/> are
/// lengths along the line: positive drawn, negative a gap, zero a dot; empty for an unbroken line.
/// </summary>
public sealed record HatchLineFamily(double AngleDeg, double X, double Y, double Shift, double Spacing, double[] Dashes);

/// <summary>A hatch pattern: a name, the name shown to the user, a group, and its line families (none = solid fill).</summary>
public sealed record HatchPattern(string Name, string Title, string Category, IReadOnlyList<HatchLineFamily> Families)
{
    public bool IsSolid => Families.Count == 0;
}

/// <summary>
/// The built-in hatch patterns. Every definition here was written for HAQQ CAD: the ANSI names follow the
/// long-standing drafting convention for those line arrangements, and the material patterns are HAQQ CAD's own
/// simple line drawings of each material. No pattern file of another CAD program is used or included.
/// </summary>
public static class HatchPatterns
{
    public const string Solid = "SOLID";
    public const string Default = "ANSI31";
    public const string CategoryAnsi = "ANSI", CategoryLines = "Lines", CategoryMaterials = "Materials", CategorySolid = "Solid";

    private const double D = 0.1767766953;             // moves a 45 degree line 1/8 across

    private static HatchLineFamily F(double angle, double x, double y, double shift, double spacing, params double[] dashes) =>
        new(angle, x, y, shift, spacing, dashes);

    private static HatchPattern P(string name, string title, string category, params HatchLineFamily[] families) =>
        new(name, title, category, families);

    public static IReadOnlyList<HatchPattern> All { get; } = new[]
    {
        P(Solid, "Solid fill", CategorySolid),

        P("ANSI31", "ANSI31 - general, iron, brick", CategoryAnsi, F(45, 0, 0, 0, 0.125)),
        P("ANSI32", "ANSI32 - steel", CategoryAnsi, F(45, 0, 0, 0, 0.375), F(45, D, 0, 0, 0.375)),
        P("ANSI33", "ANSI33 - bronze, brass, copper", CategoryAnsi, F(45, 0, 0, 0, 0.25), F(45, D, 0, 0, 0.25, 0.125, -0.0625)),
        P("ANSI34", "ANSI34 - plastic, rubber", CategoryAnsi, F(45, 0, 0, 0, 0.75), F(45, D, 0, 0, 0.75), F(45, 2 * D, 0, 0, 0.75), F(45, 3 * D, 0, 0, 0.75)),
        P("ANSI35", "ANSI35 - fire brick, refractory", CategoryAnsi, F(45, 0, 0, 0, 0.25), F(45, D, 0, 0, 0.25, 0.3125, -0.0625, 0, -0.0625)),
        P("ANSI36", "ANSI36 - marble, slate, glass", CategoryAnsi, F(45, 0, 0, 0.21875, 0.125, 0.3125, -0.0625, 0, -0.0625)),
        P("ANSI37", "ANSI37 - lead, zinc, insulation", CategoryAnsi, F(45, 0, 0, 0, 0.125), F(135, 0, 0, 0, 0.125)),
        P("ANSI38", "ANSI38 - aluminum", CategoryAnsi, F(45, 0, 0, 0, 0.125), F(135, 0, 0, 0.25, 0.125, 0.3125, -0.1875)),

        P("HORIZONTAL", "Horizontal lines", CategoryLines, F(0, 0, 0, 0, 0.125)),
        P("VERTICAL", "Vertical lines", CategoryLines, F(90, 0, 0, 0, 0.125)),
        P("DIAGONAL", "Diagonal lines", CategoryLines, F(45, 0, 0, 0, 0.25)),
        P("PARALLEL", "Parallel lines (pairs)", CategoryLines, F(0, 0, 0, 0, 0.375), F(0, 0, 0.09375, 0, 0.375)),
        P("CROSS", "Crosshatch", CategoryLines, F(0, 0, 0, 0, 0.25), F(90, 0, 0, 0, 0.25)),
        P("DOTS", "Dots", CategoryLines, F(0, 0, 0, 0.125, 0.125, 0, -0.25)),

        P("CONCRETE", "Concrete", CategoryMaterials,
            F(0, 0, 0, 0.37, 0.29, 0, -0.83), F(0, 0.21, 0.13, 0.53, 0.41, 0, -1.07),
            F(50, 0, 0, 0.33, 0.61, 0.07, -1.21), F(110, 0.3, 0.2, 0.47, 0.67, 0.06, -1.37), F(170, 0.1, 0.4, 0.29, 0.73, 0.08, -1.13)),
        P("RCONCRETE", "Reinforced concrete", CategoryMaterials,
            F(45, 0, 0, 0, 0.5), F(45, D / 2, 0, 0, 0.5),
            F(0, 0, 0, 0.37, 0.29, 0, -0.83), F(0, 0.21, 0.13, 0.53, 0.41, 0, -1.07), F(110, 0.3, 0.2, 0.47, 0.67, 0.06, -1.37)),
        P("BRICK", "Brick", CategoryMaterials,
            F(0, 0, 0, 0, 0.25), F(90, 0, 0, 0, 0.75, 0.25, -0.25), F(90, 0.375, 0.25, 0, 0.75, 0.25, -0.25)),
        P("CMU", "Block / CMU", CategoryMaterials,
            F(0, 0, 0, 0, 0.5), F(90, 0, 0, 0, 1.0, 0.5, -0.5), F(90, 0.5, 0.5, 0, 1.0, 0.5, -0.5)),
        P("STONE", "Stone", CategoryMaterials,
            F(0, 0, 0, 0.35, 0.4, 0.85, -0.12), F(90, 0.2, 0, 0.4, 0.83, 0.4, -0.4), F(90, 0.6, 0.4, 0.4, 1.07, 0.4, -0.4)),
        P("GRAVEL", "Gravel", CategoryMaterials,
            F(20, 0, 0, 0.11, 0.17, 0.09, -0.19), F(80, 0.05, 0.03, 0.13, 0.19, 0.08, -0.23),
            F(140, 0.02, 0.08, 0.09, 0.21, 0.1, -0.17), F(0, 0, 0, 0.07, 0.23, 0, -0.31)),
        P("SAND", "Sand", CategoryMaterials,
            F(0, 0, 0, 0.071, 0.093, 0, -0.211), F(37, 0, 0, 0.083, 0.127, 0, -0.257), F(101, 0.03, 0.05, 0.061, 0.149, 0, -0.293)),
        P("EARTH", "Earth / soil", CategoryMaterials,
            F(0, 0, 0.0417, 0.25, 0.25, 0.25, -0.25), F(0, 0, 0.125, 0.25, 0.25, 0.25, -0.25), F(0, 0, 0.2083, 0.25, 0.25, 0.25, -0.25),
            F(90, 0.0417, 0.25, 0.25, 0.25, 0.25, -0.25), F(90, 0.125, 0.25, 0.25, 0.25, 0.25, -0.25), F(90, 0.2083, 0.25, 0.25, 0.25, 0.25, -0.25)),
        P("ROCK", "Rock", CategoryMaterials,
            F(30, 0, 0, 0.3, 0.45, 0.6, -0.25), F(100, 0, 0, 0.2, 0.6, 0.35, -0.5), F(150, 0.2, 0.1, 0.4, 0.7, 0.3, -0.6)),
        P("ASPHALT", "Asphalt", CategoryMaterials,
            F(0, 0, 0, 0.05, 0.07, 0, -0.13), F(45, 0, 0, 0.09, 0.11, 0, -0.17), F(0, 0, 0, 0.31, 0.37, 0.12, -0.5)),
        P("WOOD", "Wood grain", CategoryMaterials,
            F(0, 0, 0, 0.6, 0.11, 1.4, -0.12), F(0, 0, 0.04, 0.9, 0.29, 0.7, -0.3, 0.3, -0.2), F(0, 0, 0.07, 0.35, 0.47, 2.1, -0.25)),
        P("PLYWOOD", "Plywood", CategoryMaterials,
            F(0, 0, 0, 0, 0.125), F(45, 0, 0, 0.2651650429, 0.0883883476, D, -D)),
        P("INSULATION", "Insulation", CategoryMaterials,
            F(60, 0, 0, -0.125, 0.2165063509, 0.25, -0.25), F(120, 0.25, 0, 0.125, 0.2165063509, 0.25, -0.25)),
        P("STEEL", "Steel", CategoryMaterials, F(45, 0, 0, 0, 0.25), F(45, D / 2, 0, 0, 0.25)),
        P("ALUMINUM", "Aluminum", CategoryMaterials, F(45, 0, 0, 0, 0.375), F(45, 0.75 * D, 0, 0, 0.375, 0.25, -0.125)),
        P("GLASS", "Glass", CategoryMaterials,
            F(45, 0, 0, 0.9, 0.6, 0.5, -1.3), F(45, 0.1, 0, 0.9, 0.6, 0.3, -1.5), F(45, 0.2, 0, 0.9, 0.6, 0.15, -1.65)),
        P("TILE", "Tile", CategoryMaterials, F(0, 0, 0, 0, 0.5), F(90, 0, 0, 0, 0.5)),
    };

    public static IReadOnlyList<string> Categories { get; } = new[] { CategoryAnsi, CategoryLines, CategoryMaterials, CategorySolid };

    public static HatchPattern? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : All.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The narrowest distance between neighbouring lines of the pattern, in pattern units; 0 for solid.</summary>
    public static double FinestSpacing(HatchPattern pattern) =>
        pattern.Families.Count == 0 ? 0 : pattern.Families.Min(f => f.Spacing);
}
