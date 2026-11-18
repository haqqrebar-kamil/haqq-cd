using System.Globalization;
using HAQQ.CAD.Commands;
using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Presentation;

/// <summary>Helpers shared by the text and the table windows.</summary>
internal static class AnnotationChoices
{
    public static readonly IReadOnlyList<string> JustifyNames = new[] { "Left", "Center", "Right" };

    public static TextJustify ParseJustify(string? name) =>
        Enum.TryParse(name, ignoreCase: true, out TextJustify justify) && Enum.IsDefined(justify) ? justify : TextJustify.Left;

    /// <summary>The fonts offered: those of the computer, with the font in use added when it is not among them.</summary>
    public static IReadOnlyList<string> Fonts(IReadOnlyList<string>? installed, string current)
    {
        var fonts = (installed is { Count: > 0 } ? installed : new[] { "Arial", "Calibri", "Consolas", "Courier New", "Segoe UI", "Tahoma", "Times New Roman", "Verdana" })
            .Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (!fonts.Contains(current, StringComparer.OrdinalIgnoreCase)) fonts.Insert(0, current);
        return fonts;
    }
}

/// <summary>
/// The Text window: the words (one or more lines), font, letter height, bold, italic, underline, which side of
/// the clicked point the text is on, and its angle. For a new text the result is placed by a click; for a text
/// that was opened by a double-click it is saved back to it.
/// </summary>
public sealed class TextEditorViewModel : ObservableObject
{
    private readonly EditorSession _session;
    private readonly Guid? _editing;
    private string _text = "", _heightText, _font, _justify, _rotationText, _message = "";
    private bool _bold, _italic, _underline;

    public TextEditorViewModel(EditorSession session, Guid? editing = null, IReadOnlyList<string>? installedFonts = null)
    {
        _session = session;
        var settings = session.TextDefaults;
        if (editing is Guid id && session.Document.GetEntity(id) is TextEntity existing)
        {
            _editing = id;
            _text = existing.Text;
            settings = new TextSettings(existing.Height, existing.Font, existing.Bold, existing.Italic, existing.Underline, existing.Justify,
                Math.Round(existing.Angle * 180.0 / Math.PI, 6));
        }
        _heightText = UnitFormatter.FormatLength(settings.Height, session.Document.Settings);
        _font = settings.Font;
        _bold = settings.Bold;
        _italic = settings.Italic;
        _underline = settings.Underline;
        _justify = settings.Justify.ToString();
        _rotationText = settings.AngleDegrees.ToString("0.######", CultureInfo.InvariantCulture);
        Fonts = AnnotationChoices.Fonts(installedFonts, _font);
    }

    public bool IsEditing => _editing is not null;
    public string WindowTitle => IsEditing ? "Edit Text" : "Text";
    public string CommitLabel => IsEditing ? "Save" : "Place in drawing";
    public IReadOnlyList<string> Fonts { get; }
    public IReadOnlyList<string> JustifyNames => AnnotationChoices.JustifyNames;

    public string Text { get => _text; set => SetProperty(ref _text, value ?? ""); }
    public string HeightText { get => _heightText; set => SetProperty(ref _heightText, value ?? ""); }
    public string Font { get => _font; set { if (!string.IsNullOrWhiteSpace(value)) SetProperty(ref _font, value); } }
    public bool Bold { get => _bold; set => SetProperty(ref _bold, value); }
    public bool Italic { get => _italic; set => SetProperty(ref _italic, value); }
    public bool Underline { get => _underline; set => SetProperty(ref _underline, value); }
    public string Justify { get => _justify; set { if (!string.IsNullOrWhiteSpace(value)) SetProperty(ref _justify, value); } }
    public string RotationText { get => _rotationText; set => SetProperty(ref _rotationText, value ?? ""); }

    /// <summary>What has to be put right before the text can be used; empty when all is well.</summary>
    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    /// <summary>
    /// Saves the edited text, or starts placing the new one (the window then closes and the text follows the
    /// cursor). False, with <see cref="Message"/> saying why, when something typed cannot be used.
    /// </summary>
    public bool Commit()
    {
        string text = string.Join("\n", (Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd())).Trim('\n');
        if (string.IsNullOrWhiteSpace(text)) { Message = "Type the text first."; return false; }
        if (text.Length > TextEntity.MaxLength) { Message = $"The text is too long (at most {TextEntity.MaxLength} characters)."; return false; }
        if (text.Split('\n').Length > TextEntity.MaxLines) { Message = $"Too many lines (at most {TextEntity.MaxLines})."; return false; }
        if (text.Any(c => char.IsControl(c) && c != '\n')) { Message = "The text has characters that cannot be shown (a tab, for example). Use spaces instead."; return false; }
        if (!UnitFormatter.TryParseLength(HeightText, _session.Document.Settings, out double height) || !(height > GeoMath.Epsilon))
        {
            Message = "The letter height must be a length more than 0.";
            return false;
        }
        if (!double.TryParse(RotationText.Trim().TrimEnd('°'), NumberStyles.Float, CultureInfo.InvariantCulture, out double degrees) || !double.IsFinite(degrees))
        {
            Message = "The angle must be a number of degrees (0 is left to right).";
            return false;
        }
        if (!TextMeasure.IsValidFont(Font)) { Message = "Choose a font."; return false; }

        var settings = new TextSettings(height, Font.Trim(), Bold, Italic, Underline, AnnotationChoices.ParseJustify(Justify), degrees);
        bool ok = _editing is Guid id ? _session.UpdateText(id, text, settings) : _session.BeginTextPlacement(text, settings);
        Message = ok ? "" : "The text could not be used.";
        return ok;
    }
}

/// <summary>One cell of the table being edited.</summary>
public sealed class TableCellViewModel : ObservableObject
{
    private string _text;
    public TableCellViewModel(string text = "") => _text = text;
    public string Text { get => _text; set => SetProperty(ref _text, value ?? ""); }
}

/// <summary>One column of the table being edited: its width as typed.</summary>
public sealed class TableColumnViewModel : ObservableObject
{
    private string _widthText;
    public TableColumnViewModel(string widthText) => _widthText = widthText;
    public string WidthText { get => _widthText; set => SetProperty(ref _widthText, value ?? ""); }
}

/// <summary>One row of the table being edited.</summary>
public sealed class TableRowViewModel
{
    public TableRowViewModel(double height, IEnumerable<TableCellViewModel> cells)
    {
        Height = height;
        Cells = cells.ToList();
    }

    /// <summary>The row's own height (rows can differ after one was stretched by its grip).</summary>
    public double Height { get; set; }
    public List<TableCellViewModel> Cells { get; }
}

/// <summary>
/// The Table window, for a new table and for one opened by a double-click: title, number of rows and columns,
/// sizes, font, header row, and the cells themselves, with rows and columns added or taken out anywhere.
/// </summary>
public sealed class TableEditorViewModel : ObservableObject
{
    public const string Varies = "Varies";

    private readonly EditorSession _session;
    private readonly Guid? _editing;
    private string _title, _rowCountText, _columnCountText, _textHeightText, _rowHeightText, _font, _justify, _message = "";
    private bool _headerRow;

    public TableEditorViewModel(EditorSession session, Guid? editing = null, IReadOnlyList<string>? installedFonts = null)
    {
        _session = session;
        var settings = session.Document.Settings;
        TableEntity table;
        if (editing is Guid id && session.Document.GetEntity(id) is TableEntity existing)
        {
            _editing = id;
            table = (TableEntity)existing.Clone();
        }
        else
        {
            // A starting size that suits the text height in use: the user changes whatever does not fit.
            double textHeight = session.TextDefaults.Height;
            table = new TableEntity(Point2D.Origin, 4, 3, textHeight * 14, textHeight * 2.6, textHeight) { Font = session.TextDefaults.Font };
        }

        _title = table.Title;
        _font = table.Font;
        _headerRow = table.HeaderRow;
        _justify = table.Justify.ToString();
        _textHeightText = UnitFormatter.FormatLength(table.TextHeight, settings);
        bool uniform = table.RowHeights.All(h => Math.Abs(h - table.RowHeights[0]) <= 1e-9 * Math.Max(1, table.RowHeights[0]));
        _rowHeightText = uniform ? UnitFormatter.FormatLength(table.RowHeights[0], settings) : Varies;
        for (int c = 0; c < table.ColumnCount; c++) Columns.Add(new TableColumnViewModel(UnitFormatter.FormatLength(table.ColumnWidths[c], settings)));
        for (int r = 0; r < table.RowCount; r++)
            Rows.Add(new TableRowViewModel(table.RowHeights[r], Enumerable.Range(0, table.ColumnCount).Select(c => new TableCellViewModel(table.GetCell(r, c)))));
        _rowCountText = Rows.Count.ToString(CultureInfo.InvariantCulture);
        _columnCountText = Columns.Count.ToString(CultureInfo.InvariantCulture);
        Fonts = AnnotationChoices.Fonts(installedFonts, _font);
    }

    public bool IsEditing => _editing is not null;
    public string WindowTitle => IsEditing ? "Edit Table" : "Table";
    public string CommitLabel => IsEditing ? "Save" : "Place in drawing";
    public IReadOnlyList<string> Fonts { get; }
    public IReadOnlyList<string> JustifyNames => AnnotationChoices.JustifyNames;

    public List<TableRowViewModel> Rows { get; } = new();
    public List<TableColumnViewModel> Columns { get; } = new();

    /// <summary>Rows or columns were added or removed: the window lays the cells out again.</summary>
    public event EventHandler? StructureChanged;

    public string Title { get => _title; set => SetProperty(ref _title, value ?? ""); }
    public string RowCountText { get => _rowCountText; set => SetProperty(ref _rowCountText, value ?? ""); }
    public string ColumnCountText { get => _columnCountText; set => SetProperty(ref _columnCountText, value ?? ""); }
    public string TextHeightText { get => _textHeightText; set => SetProperty(ref _textHeightText, value ?? ""); }

    /// <summary>One height for every row, or "Varies" to leave each row as it is.</summary>
    public string RowHeightText { get => _rowHeightText; set => SetProperty(ref _rowHeightText, value ?? ""); }

    public string Font { get => _font; set { if (!string.IsNullOrWhiteSpace(value)) SetProperty(ref _font, value); } }
    public bool HeaderRow { get => _headerRow; set => SetProperty(ref _headerRow, value); }
    public string Justify { get => _justify; set { if (!string.IsNullOrWhiteSpace(value)) SetProperty(ref _justify, value); } }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    private double NewRowHeight(int beside) => Rows[Math.Clamp(beside, 0, Rows.Count - 1)].Height;

    private void Changed()
    {
        RowCountText = Rows.Count.ToString(CultureInfo.InvariantCulture);
        ColumnCountText = Columns.Count.ToString(CultureInfo.InvariantCulture);
        Message = "";
        StructureChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Makes the table as many rows and columns as typed: added ones are empty, at the bottom and the right.</summary>
    public bool ApplySize()
    {
        if (!int.TryParse(RowCountText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int rows) || rows < 1 || rows > TableEntity.MaxRows)
        {
            Message = $"Rows: a whole number from 1 to {TableEntity.MaxRows}.";
            return false;
        }
        if (!int.TryParse(ColumnCountText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int columns) || columns < 1 || columns > TableEntity.MaxColumns)
        {
            Message = $"Columns: a whole number from 1 to {TableEntity.MaxColumns}.";
            return false;
        }
        while (Rows.Count < rows) Rows.Add(new TableRowViewModel(NewRowHeight(Rows.Count - 1), Columns.Select(_ => new TableCellViewModel())));
        while (Rows.Count > rows) Rows.RemoveAt(Rows.Count - 1);
        while (Columns.Count < columns)
        {
            Columns.Add(new TableColumnViewModel(Columns[^1].WidthText));
            foreach (var row in Rows) row.Cells.Add(new TableCellViewModel());
        }
        while (Columns.Count > columns)
        {
            Columns.RemoveAt(Columns.Count - 1);
            foreach (var row in Rows) row.Cells.RemoveAt(row.Cells.Count - 1);
        }
        Changed();
        return true;
    }

    /// <summary>An empty row before <paramref name="index"/> (after the last row when it is the row count).</summary>
    public bool InsertRow(int index)
    {
        if (Rows.Count >= TableEntity.MaxRows) { Message = $"A table has at most {TableEntity.MaxRows} rows."; return false; }
        index = Math.Clamp(index, 0, Rows.Count);
        Rows.Insert(index, new TableRowViewModel(NewRowHeight(index - 1), Columns.Select(_ => new TableCellViewModel())));
        Changed();
        return true;
    }

    public bool DeleteRow(int index)
    {
        if (Rows.Count <= 1) { Message = "A table keeps at least one row."; return false; }
        if (index < 0 || index >= Rows.Count) return false;
        Rows.RemoveAt(index);
        Changed();
        return true;
    }

    /// <summary>An empty column before <paramref name="index"/> (after the last column when it is the column count).</summary>
    public bool InsertColumn(int index)
    {
        if (Columns.Count >= TableEntity.MaxColumns) { Message = $"A table has at most {TableEntity.MaxColumns} columns."; return false; }
        index = Math.Clamp(index, 0, Columns.Count);
        Columns.Insert(index, new TableColumnViewModel(Columns[Math.Clamp(index - 1, 0, Columns.Count - 1)].WidthText));
        foreach (var row in Rows) row.Cells.Insert(index, new TableCellViewModel());
        Changed();
        return true;
    }

    public bool DeleteColumn(int index)
    {
        if (Columns.Count <= 1) { Message = "A table keeps at least one column."; return false; }
        if (index < 0 || index >= Columns.Count) return false;
        Columns.RemoveAt(index);
        foreach (var row in Rows) row.Cells.RemoveAt(index);
        Changed();
        return true;
    }

    /// <summary>The table as typed, or null with <see cref="Message"/> saying what cannot be used.</summary>
    public TableEntity? Build()
    {
        var settings = _session.Document.Settings;
        if (!UnitFormatter.TryParseLength(TextHeightText, settings, out double textHeight) || !(textHeight > GeoMath.Epsilon))
        {
            Message = "The text height must be a length more than 0.";
            return null;
        }
        double? rowHeight = null;
        if (!RowHeightText.Trim().Equals(Varies, StringComparison.OrdinalIgnoreCase))
        {
            if (!UnitFormatter.TryParseLength(RowHeightText, settings, out double parsed) || !(parsed > GeoMath.Epsilon))
            {
                Message = "The row height must be a length more than 0.";
                return null;
            }
            if (parsed < textHeight * 1.2) { Message = "The rows are too low for the text: make the row height at least 1.2 times the text height."; return null; }
            rowHeight = parsed;
        }
        if (!TextMeasure.IsValidFont(Font)) { Message = "Choose a font."; return null; }

        var table = new TableEntity(Point2D.Origin, Rows.Count, Columns.Count, 1, 1, textHeight)
        {
            Font = Font.Trim(),
            Title = (Title ?? "").Trim(),
            HeaderRow = HeaderRow,
            Justify = AnnotationChoices.ParseJustify(Justify),
        };
        for (int c = 0; c < Columns.Count; c++)
        {
            if (!UnitFormatter.TryParseLength(Columns[c].WidthText, settings, out double width) || !(width > GeoMath.Epsilon))
            {
                Message = $"The width of column {c + 1} must be a length more than 0.";
                return null;
            }
            table.SetColumnWidth(c, width);
        }
        for (int r = 0; r < Rows.Count; r++)
        {
            table.SetRowHeight(r, rowHeight ?? Rows[r].Height);
            for (int c = 0; c < Columns.Count; c++) table.SetCell(r, c, Rows[r].Cells[c].Text.Trim());
        }
        if (!table.IsValid)
        {
            Message = $"Something in the table cannot be used: a cell holds at most {TableEntity.MaxCellLength} characters on one line.";
            return null;
        }
        Message = "";
        return table;
    }

    /// <summary>Saves the edited table, or starts placing the new one by a click.</summary>
    public bool Commit()
    {
        if (Build() is not { } table) return false;
        bool ok = _editing is Guid id ? _session.UpdateTable(id, table) : _session.BeginTablePlacement(table);
        if (!ok) Message = "The table could not be used.";
        return ok;
    }
}
