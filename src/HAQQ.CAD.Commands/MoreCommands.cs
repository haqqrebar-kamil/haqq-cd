using System.Globalization;
using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Commands;

/// <summary>
/// UNITS: how lengths are written (Decimal, Architectural, Fractional), the drawing unit (inches, feet, mm, cm,
/// meters) and the precision. When the unit of a drawing that has objects is changed, it asks whether the
/// objects keep their real size (their numbers are converted) or keep their numbers.
/// </summary>
public sealed class UnitsCommand : CadCommandBase
{
    private int _stage;                             // 0 length type, 1 unit, 2 convert?, 3 precision
    private DrawingSettings _settings = new();
    private bool _keepRealSize = true;

    public override string Name => "UNITS";

    protected override CommandResult OnStart()
    {
        _settings = Document.Settings.Clone();
        return CommandResult.Continue;
    }

    private bool UnitChanged => _settings.Unit != Document.Settings.Unit;
    private bool HasObjects => Document.AllEntities.Count > 0 || Document.Blocks.Count > 0;

    public override string Prompt => _stage switch
    {
        0 => $"UNITS  Enter length type [Decimal/Architectural/Fractional] <{_settings.Format}>:",
        1 => $"UNITS  Enter drawing unit [Inches/Feet/MM/CM/Meters] <{UnitName(_settings.Unit)}>:",
        2 => $"UNITS  The drawing has objects. Keep their real size (numbers are converted to {UnitName(_settings.Unit)}) or keep their numbers? [Size/Numbers] <Size>:",
        _ => UsesFractions
            ? $"UNITS  Enter smallest fraction 1/n, n = 1 to 256 <{_settings.FractionDenominator}>:"
            : $"UNITS  Enter number of decimal places, 0 to 8 <{_settings.DecimalPrecision}>:",
    };

    public override IReadOnlyList<string> Options => _stage switch
    {
        0 => new[] { "Decimal", "Architectural", "Fractional" },
        1 => new[] { "Inches", "Feet", "MM", "CM", "Meters" },
        2 => new[] { "Size", "Numbers" },
        _ => Array.Empty<string>(),
    };

    private bool UsesFractions => _settings.Format != LengthFormat.Decimal && UnitConverter.IsImperial(_settings.Unit);

    public static string UnitName(LinearUnit unit) => unit switch
    {
        LinearUnit.Inch => "Inches",
        LinearUnit.Foot => "Feet",
        LinearUnit.Millimeter => "MM",
        LinearUnit.Centimeter => "CM",
        _ => "Meters",
    };

    public override CommandResult OnInput(CommandInput input)
    {
        switch (_stage)
        {
            case 0:
                if (input is CommandInput.KeywordInput type)
                {
                    if (Matches(type.Keyword, "DECIMAL")) _settings.Format = LengthFormat.Decimal;
                    else if (Matches(type.Keyword, "ARCHITECTURAL")) _settings.Format = LengthFormat.Architectural;
                    else if (Matches(type.Keyword, "FRACTIONAL")) _settings.Format = LengthFormat.Fractional;
                    else return Invalid("Enter Decimal, Architectural or Fractional.");
                }
                else if (input is not CommandInput.EnterInput) return Invalid("Enter Decimal, Architectural or Fractional.");
                _stage = 1;
                return CommandResult.Continue;

            case 1:
                if (input is CommandInput.KeywordInput unit)
                {
                    LinearUnit? chosen = unit.Keyword.ToUpperInvariant() switch
                    {
                        "MM" => LinearUnit.Millimeter,
                        "CM" => LinearUnit.Centimeter,
                        "M" => LinearUnit.Meter,
                        "IN" => LinearUnit.Inch,
                        "FT" => LinearUnit.Foot,
                        var k when Matches(k, "INCHES") => LinearUnit.Inch,
                        var k when Matches(k, "FEET") => LinearUnit.Foot,
                        var k when Matches(k, "MILLIMETERS") => LinearUnit.Millimeter,
                        var k when Matches(k, "CENTIMETERS") => LinearUnit.Centimeter,
                        var k when Matches(k, "METERS") => LinearUnit.Meter,
                        _ => null,
                    };
                    if (chosen is null) return Invalid("Enter Inches, Feet, MM, CM or Meters.");
                    if (chosen.Value != _settings.Unit)
                    {
                        // The way of writing lengths, the precision and the grid that suit the new unit.
                        var format = _settings.Format;
                        _settings = _settings.ForUnit(chosen.Value);
                        // A length type chosen a moment ago is kept where the unit allows it.
                        if (UnitConverter.IsImperial(chosen.Value)) _settings.Format = format;
                    }
                }
                else if (input is not CommandInput.EnterInput) return Invalid("Enter Inches, Feet, MM, CM or Meters.");
                _stage = UnitChanged && HasObjects ? 2 : 3;
                return CommandResult.Continue;

            case 2:
                if (input is CommandInput.KeywordInput how)
                {
                    if (Matches(how.Keyword, "SIZE")) _keepRealSize = true;
                    else if (Matches(how.Keyword, "NUMBERS")) _keepRealSize = false;
                    else return Invalid("Enter Size (objects stay as large as they are) or Numbers (every number stays as typed).");
                }
                else if (input is not CommandInput.EnterInput) return Invalid("Enter Size or Numbers.");
                _stage = 3;
                return CommandResult.Continue;

            default:
                if (input is CommandInput.NumberInput n)
                {
                    int value = (int)Math.Round(n.Value);
                    if (Math.Abs(n.Value - value) > 1e-9) return Invalid("Enter a whole number.");
                    if (UsesFractions)
                    {
                        if (value < 1 || value > 256 || (value & (value - 1)) != 0) return Invalid("Enter 1, 2, 4, 8, 16, 32, 64, 128 or 256.");
                        _settings.FractionDenominator = value;
                    }
                    else
                    {
                        if (value < 0 || value > 8) return Invalid("Enter a number from 0 to 8.");
                        _settings.DecimalPrecision = value;
                    }
                }
                else if (input is not CommandInput.EnterInput) return Invalid("Enter a number.");

                bool changed = UnitChanged, hadObjects = HasObjects;
                Session.ChangeUnits(_settings, _keepRealSize);
                Session.Post($"Units: {UnitName(_settings.Unit)}, {_settings.Format}." + (!changed ? ""
                    : !hadObjects ? " Dimension text, arrows and dashes were set to suit the new unit."
                    : _keepRealSize ? " Everything keeps its real size; the numbers were converted."
                    : " Every number stays as it was typed, so objects are now a different real size."));
                return CommandResult.Finished;
        }
    }
}

/// <summary>NEWUNITS: the unit that new drawings start in. The open drawing is not touched.</summary>
public sealed class NewDrawingUnitsCommand : CadCommandBase
{
    public override string Name => "NEWUNITS";
    public override string Prompt => $"NEWUNITS  Unit for new drawings [Inches/Feet/MM/CM/Meters] <{UnitsCommand.UnitName(Session.NewDrawingUnit)}>:";
    public override IReadOnlyList<string> Options => new[] { "Inches", "Feet", "MM", "CM", "Meters" };

    public override CommandResult OnInput(CommandInput input)
    {
        if (input is CommandInput.EnterInput) return CommandResult.Finished;
        if (input is not CommandInput.KeywordInput k) return Invalid("Enter Inches, Feet, MM, CM or Meters.");
        LinearUnit? unit = k.Keyword.ToUpperInvariant() switch
        {
            "MM" => LinearUnit.Millimeter,
            "CM" => LinearUnit.Centimeter,
            "M" => LinearUnit.Meter,
            "IN" => LinearUnit.Inch,
            "FT" => LinearUnit.Foot,
            var x when Matches(x, "INCHES") => LinearUnit.Inch,
            var x when Matches(x, "FEET") => LinearUnit.Foot,
            var x when Matches(x, "MILLIMETERS") => LinearUnit.Millimeter,
            var x when Matches(x, "CENTIMETERS") => LinearUnit.Centimeter,
            var x when Matches(x, "METERS") => LinearUnit.Meter,
            _ => null,
        };
        if (unit is null) return Invalid("Enter Inches, Feet, MM, CM or Meters.");
        Session.SetNewDrawingUnit(unit.Value);
        return CommandResult.Finished;
    }
}

/// <summary>SNAP and GRID: set the snap increment or the grid spacing, or switch SNAP on and off.</summary>
public sealed class SpacingCommand : CadCommandBase
{
    private readonly bool _snap;

    public SpacingCommand(bool snap) => _snap = snap;

    public override string Name => _snap ? "SNAP" : "GRID";

    private double Current => _snap ? Document.Settings.SnapSpacing : Document.Settings.GridSpacing;

    public override string Prompt => _snap
        ? $"SNAP  Specify snap spacing or [ON/OFF] <{UnitFormatter.FormatLength(Current, Document.Settings)}>:"
        : $"GRID  Enter the spacing of the background grid lines <{UnitFormatter.FormatLength(Current, Document.Settings)}>:";

    public override IReadOnlyList<string> Options => _snap ? new[] { "ON", "OFF" } : Array.Empty<string>();

    public override CommandResult OnInput(CommandInput input)
    {
        switch (input)
        {
            case CommandInput.KeywordInput k when _snap && k.Keyword == "ON":
                Session.GridSnapEnabled = true;
                Session.Post("<Snap on>");
                return CommandResult.Finished;

            case CommandInput.KeywordInput k when _snap && k.Keyword == "OFF":
                Session.GridSnapEnabled = false;
                Session.Post("<Snap off>");
                return CommandResult.Finished;

            case CommandInput.NumberInput n:
                if (!(n.Value > 0)) return Invalid("The spacing must be positive.");
                var after = Document.Settings.Clone();
                if (_snap) after.SnapSpacing = n.Value; else after.GridSpacing = n.Value;
                Session.Execute(new ModifySettingsAction(Name, Document.Settings, after));
                Session.Post(Name + " spacing: " + n.Value.ToString("0.####", CultureInfo.InvariantCulture));
                return CommandResult.Finished;

            case CommandInput.EnterInput:
                return CommandResult.Finished;

            default:
                return Invalid("Enter a spacing.");
        }
    }
}

/// <summary>CURSORSIZE: length of the crosshair as a percentage of the drawing area.</summary>
public sealed class CursorSizeCommand : CadCommandBase
{
    public override string Name => "CURSORSIZE";
    public override string Prompt => $"CURSORSIZE  Enter crosshair size, 1 to 100 percent of the screen <{Session.CrosshairSizePercent}>:";

    public override CommandResult OnInput(CommandInput input)
    {
        switch (input)
        {
            case CommandInput.NumberInput n:
                if (n.Value < 1 || n.Value > 100 || Math.Abs(n.Value - Math.Round(n.Value)) > 1e-9)
                    return Invalid("Enter a whole number from 1 to 100.");
                Session.CrosshairSizePercent = (int)Math.Round(n.Value);
                Session.Post($"Crosshair size: {Session.CrosshairSizePercent}%.");
                return CommandResult.Finished;

            case CommandInput.EnterInput:
                return CommandResult.Finished;

            default:
                return Invalid("Enter a whole number from 1 to 100.");
        }
    }
}

/// <summary>LINEMODE: whether LINE ends at the second point (Single) or keeps drawing connected lines (Continuous).</summary>
public sealed class LineModeCommand : CadCommandBase
{
    public override string Name => "LINEMODE";
    public override string Prompt => $"LINEMODE  Enter line mode [Single/Continuous] <{(Session.ContinuousLines ? "Continuous" : "Single")}>:";
    public override IReadOnlyList<string> Options => new[] { "Single", "Continuous" };

    public override CommandResult OnInput(CommandInput input)
    {
        if (input is CommandInput.KeywordInput k)
        {
            if (Matches(k.Keyword, "SINGLE")) Session.ContinuousLines = false;
            else if (Matches(k.Keyword, "CONTINUOUS")) Session.ContinuousLines = true;
            else return Invalid("Enter Single or Continuous.");
        }
        else if (input is not CommandInput.EnterInput) return Invalid("Enter Single or Continuous.");

        Session.Post(Session.ContinuousLines
            ? "LINE now keeps drawing connected lines until Enter."
            : "LINE now ends at the second point. Press Enter or Space to draw the next line.");
        return CommandResult.Finished;
    }
}

/// <summary>
/// -LINETYPE (the typed version; LINETYPE opens the Linetype Manager window): lists the linetypes of the drawing, creates a custom one (name, then dash and gap lengths) or
/// deletes one that is not in use.
/// </summary>
public sealed class LinetypeCommand : CadCommandBase, IRawTextCommand
{
    private enum Stage { Option, NewName, NewPattern, DeleteName }

    private Stage _stage = Stage.Option;
    private string _name = "";

    public override string Name => "LINETYPE";

    public override string Prompt => _stage switch
    {
        Stage.Option => "LINETYPE  Enter an option [New/Delete/List] <List>:",
        Stage.NewName => "LINETYPE  Enter a name for the new linetype:",
        Stage.NewPattern => $"LINETYPE  Enter dash and gap lengths for \"{_name}\" in turn, e.g. 0.5, 0.25  (0 = dot):",
        _ => "LINETYPE  Enter the name of the linetype to delete:",
    };

    public override IReadOnlyList<string> Options => _stage == Stage.Option ? new[] { "New", "Delete", "List" } : Array.Empty<string>();

    public bool WantsRawText => _stage != Stage.Option;

    public override CommandResult OnInput(CommandInput input)
    {
        if (_stage != Stage.Option) return CommandResult.Finished;            // Enter with nothing typed: leave
        switch (input)
        {
            case CommandInput.KeywordInput k when Matches(k.Keyword, "NEW"):
                _stage = Stage.NewName;
                return CommandResult.Continue;
            case CommandInput.KeywordInput k when Matches(k.Keyword, "DELETE"):
                _stage = Stage.DeleteName;
                return CommandResult.Continue;
            case CommandInput.KeywordInput k when Matches(k.Keyword, "LIST"):
            case CommandInput.EnterInput:
                foreach (var linetype in Document.LinetypeTable) Session.Post("  " + linetype);
                Session.Post($"  Linetype scale: {Document.Settings.LinetypeScale:0.####} (change it with LTSCALE)");
                return CommandResult.Finished;
            default:
                return Invalid("Enter New, Delete or List.");
        }
    }

    public CommandResult OnRawText(string text)
    {
        switch (_stage)
        {
            case Stage.NewName:
                if (Linetypes.IsReserved(text) || Document.FindLinetype(text) is not null)
                    return Invalid($"There is already a linetype named \"{text}\". Enter another name.");
                if (text.Contains(',')) return Invalid("A linetype name cannot contain a comma.");
                _name = text;
                _stage = Stage.NewPattern;
                return CommandResult.Continue;

            case Stage.NewPattern:
                if (!LinetypeDefinition.TryParsePattern(text, out var pattern) || pattern.Length == 0)
                    return Invalid("Enter pairs of dash and gap lengths, for example 0.5, 0.25 or 1, 0.25, 0, 0.25. Gaps must be more than 0.");
                return Session.AddLinetype(_name, "Custom", pattern) ? CommandResult.Finished : CommandResult.Continue;

            default:
                Session.DeleteLinetype(text);
                return CommandResult.Finished;
        }
    }
}

/// <summary>LTSCALE: the drawing's linetype scale. Larger makes every dash and gap longer.</summary>
public sealed class LinetypeScaleCommand : CadCommandBase
{
    public override string Name => "LTSCALE";
    public override string Prompt => $"LTSCALE  Enter new linetype scale <{Document.Settings.LinetypeScale:0.####}>:";

    public override CommandResult OnInput(CommandInput input)
    {
        if (input is CommandInput.EnterInput) return CommandResult.Finished;
        if (input is not CommandInput.NumberInput n) return Invalid("Type a number more than 0.");
        return Session.SetLinetypeScale(n.Value) ? CommandResult.Finished : CommandResult.Continue;
    }
}
