using System.Globalization;
using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Commands;

/// <summary>UNITS: length display type, drawing unit and precision. Geometry is not rescaled.</summary>
public sealed class UnitsCommand : CadCommandBase
{
    private int _stage;
    private DrawingSettings _settings = new();

    public override string Name => "UNITS";

    protected override CommandResult OnStart()
    {
        _settings = Document.Settings.Clone();
        return CommandResult.Continue;
    }

    public override string Prompt => _stage switch
    {
        0 => $"UNITS  Enter length type [Decimal/Architectural/Fractional] <{_settings.Format}>:",
        1 => $"UNITS  Enter drawing unit [Inches/Feet/MM/CM/Meters] <{UnitName(_settings.Unit)}>:",
        _ => UsesFractions
            ? $"UNITS  Enter smallest fraction 1/n, n = 1 to 256 <{_settings.FractionDenominator}>:"
            : $"UNITS  Enter number of decimal places, 0 to 8 <{_settings.DecimalPrecision}>:",
    };

    public override IReadOnlyList<string> Options => _stage switch
    {
        0 => new[] { "Decimal", "Architectural", "Fractional" },
        1 => new[] { "Inches", "Feet", "MM", "CM", "Meters" },
        _ => Array.Empty<string>(),
    };

    private bool UsesFractions => _settings.Format != LengthFormat.Decimal && UnitConverter.IsImperial(_settings.Unit);

    private static string UnitName(LinearUnit unit) => unit switch
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
                    LinearUnit? chosen = unit.Keyword switch
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
                        _settings.Unit = chosen.Value;
                        // Sensible grid and snap increments for the new unit.
                        (_settings.GridSpacing, _settings.SnapSpacing) = chosen.Value switch
                        {
                            LinearUnit.Inch => (12.0, 1.0),
                            LinearUnit.Foot => (1.0, 1.0 / 12.0),
                            LinearUnit.Millimeter => (100.0, 10.0),
                            LinearUnit.Centimeter => (10.0, 1.0),
                            _ => (1.0, 0.01),
                        };
                    }
                }
                else if (input is not CommandInput.EnterInput) return Invalid("Enter Inches, Feet, MM, CM or Meters.");
                _stage = 2;
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

                Session.Execute(new ModifySettingsAction("UNITS", Document.Settings, _settings));
                Session.Post($"Units: {UnitName(_settings.Unit)}, {_settings.Format}. Existing geometry keeps its numeric size.");
                return CommandResult.Finished;
        }
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
        : $"GRID  Specify grid spacing <{UnitFormatter.FormatLength(Current, Document.Settings)}>:";

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
