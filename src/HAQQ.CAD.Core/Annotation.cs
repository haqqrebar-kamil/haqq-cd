using HAQQ.CAD.Geometry;

namespace HAQQ.CAD.Core;

/// <summary>Which side of its insertion point a text is on, and how the text of a table cell sits in the cell.</summary>
public enum TextJustify
{
    Left,
    Center,
    Right,
}

/// <summary>
/// How wide text is. The engine has no fonts of its own: until the application supplies a measurer
/// (<see cref="WidthPerHeight"/>), a width of 0.6 of the letter height per character is assumed.
/// </summary>
public static class TextMeasure
{
    /// <summary>Font used when none is named.</summary>
    public const string DefaultFont = "Arial";

    /// <summary>
    /// Supplied by the application: the width of a line of text whose capital letters are 1 high, for a font
    /// name, bold and italic. Null: the estimate is used.
    /// </summary>
    public static Func<string, string, bool, bool, double>? WidthPerHeight { get; set; }

    public static double Width(string text, string font, bool bold, bool italic, double height)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (WidthPerHeight is { } measure)
        {
            double measured = measure(text, font, bold, italic);
            if (double.IsFinite(measured) && measured >= 0) return measured * height;
        }
        return (bold ? 0.65 : 0.6) * height * text.Length;
    }

    /// <summary>True for a name that can be kept as a font: 1 to 64 characters, nothing unprintable.</summary>
    public static bool IsValidFont(string? font) =>
        !string.IsNullOrWhiteSpace(font) && font.Length <= 64 && !font.Any(char.IsControl);
}

/// <summary>
/// Text: one or more lines (separated by a line feed) in a font, optionally bold, italic and underlined, at a
/// height and an angle. The position is the middle of the first line's height, at the left end, the middle or
/// the right end of the lines according to <see cref="Justify"/>.
/// </summary>
public sealed class TextEntity : Entity
{
    public const string Type = "text";
    public const int MaxLength = 4000;
    public const int MaxLines = 200;

    /// <summary>Distance from one line to the next, as a multiple of the letter height.</summary>
    public const double LineSpacing = 1.6;

    private string _text = "";
    private string[] _lines = Array.Empty<string>();

    public TextEntity(Guid id, Point2D position, double height, string text) : base(id)
    {
        Position = position;
        Height = height;
        Text = text;
    }

    public TextEntity(Point2D position, double height, string text) : this(Guid.NewGuid(), position, height, text) { }

    /// <summary>The insertion point (see the class description).</summary>
    public Point2D Position { get; set; }

    /// <summary>Height of a capital letter, in drawing units.</summary>
    public double Height { get; set; }

    /// <summary>Direction of the text in radians, counter-clockwise (0 = left to right).</summary>
    public double Angle { get; set; }

    /// <summary>The text. Lines are separated by a line feed; other line endings are converted.</summary>
    public string Text
    {
        get => _text;
        set
        {
            _text = (value ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            _lines = _text.Split('\n');
        }
    }

    /// <summary>The lines of the text, top to bottom.</summary>
    public IReadOnlyList<string> Lines => _lines;

    public string Font { get; set; } = TextMeasure.DefaultFont;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }

    /// <summary>Where the insertion point is on the lines. Centre for text made before fonts were added.</summary>
    public TextJustify Justify { get; set; } = TextJustify.Center;

    public override string TypeName => Type;

    public override bool IsValid =>
        Position.IsFinite && GeoMath.IsFinite(Height) && Height > GeoMath.Epsilon && GeoMath.IsFinite(Angle) &&
        !string.IsNullOrWhiteSpace(_text) && _text.Length <= MaxLength && _lines.Length <= MaxLines &&
        !_text.Any(c => char.IsControl(c) && c != '\n') && TextMeasure.IsValidFont(Font) && Enum.IsDefined(Justify);

    /// <summary>Width of the widest line.</summary>
    public double Width
    {
        get
        {
            double width = 0;
            foreach (string line in _lines) width = Math.Max(width, TextMeasure.Width(line, Font, Bold, Italic, Height));
            return Math.Max(width, 0.6 * Height);
        }
    }

    /// <summary>Where line <paramref name="index"/> is anchored: the insertion point moved down the lines.</summary>
    public Point2D LineAnchor(int index)
    {
        double drop = index * LineSpacing * Height;
        return new Point2D(Position.X + Math.Sin(Angle) * drop, Position.Y - Math.Cos(Angle) * drop);
    }

    /// <summary>The box round the lines before turning: X along the text from the insertion point, Y up.</summary>
    private (double MinX, double MinY, double MaxX, double MaxY) LocalBox
    {
        get
        {
            double width = Width;
            double left = Justify switch { TextJustify.Left => 0, TextJustify.Center => -width / 2, _ => -width };
            return (left, -(_lines.Length - 1) * LineSpacing * Height - Height / 2, left + width, Height / 2);
        }
    }

    private Point2D ToWorld(double x, double y)
    {
        double cos = Math.Cos(Angle), sin = Math.Sin(Angle);
        return new Point2D(Position.X + x * cos - y * sin, Position.Y + x * sin + y * cos);
    }

    /// <summary>The four corners of the box round the text, turned with it.</summary>
    public Point2D[] Corners
    {
        get
        {
            var b = LocalBox;
            return new[] { ToWorld(b.MinX, b.MinY), ToWorld(b.MaxX, b.MinY), ToWorld(b.MaxX, b.MaxY), ToWorld(b.MinX, b.MaxY) };
        }
    }

    public override BoundingBox Bounds => BoundingBox.FromPoints(Corners);

    /// <summary>Zero anywhere on the text's box, otherwise the distance to it.</summary>
    public override double DistanceTo(Point2D point)
    {
        double cos = Math.Cos(Angle), sin = Math.Sin(Angle);
        double dx = point.X - Position.X, dy = point.Y - Position.Y;
        double x = dx * cos + dy * sin, y = -dx * sin + dy * cos;
        var b = LocalBox;
        double outX = Math.Max(Math.Max(b.MinX - x, x - b.MaxX), 0), outY = Math.Max(Math.Max(b.MinY - y, y - b.MaxY), 0);
        return Math.Sqrt(outX * outX + outY * outY);
    }

    public override bool IntersectsBox(BoundingBox box)
    {
        if (box.IsEmpty) return false;
        var c = Corners;
        for (int i = 0; i < 4; i++)
            if (SegmentMath.IntersectsBox(c[i], c[(i + 1) % 4], box)) return true;
        return DistanceTo(box.Center) <= 0;         // the box lies wholly inside the text
    }

    public override void Transform(in Transform2D transform)
    {
        var direction = new Vector2D(Math.Cos(Angle), Math.Sin(Angle));
        var moved = transform.Apply(Position + direction) - transform.Apply(Position);
        if (moved.Length > GeoMath.Epsilon) Angle = moved.Angle;
        Position = transform.Apply(Position);
        Height *= transform.ScaleFactor;
    }

    /// <summary>Takes the wording and the look (not the place) of another text.</summary>
    public void CopyContentFrom(TextEntity other)
    {
        Text = other.Text;
        Height = other.Height;
        Angle = other.Angle;
        Font = other.Font;
        Bold = other.Bold;
        Italic = other.Italic;
        Underline = other.Underline;
        Justify = other.Justify;
    }

    protected override Entity CloneGeometry(Guid id) =>
        new TextEntity(id, Position, Height, Text) { Angle = Angle, Font = Font, Bold = Bold, Italic = Italic, Underline = Underline, Justify = Justify };
}

/// <summary>
/// A table: rows and columns of text cells between ruled lines, with an optional title across the top and an
/// optional header row. Always upright. The position is its top-left corner.
/// </summary>
public sealed class TableEntity : Entity
{
    public const string Type = "table";
    public const int MaxRows = 200;
    public const int MaxColumns = 50;
    public const int MaxCellLength = 256;

    private readonly List<double> _columnWidths = new();
    private readonly List<double> _rowHeights = new();
    private readonly List<string[]> _cells = new();

    public TableEntity(Guid id, Point2D position, int rows, int columns, double columnWidth, double rowHeight, double textHeight) : base(id)
    {
        Position = position;
        TextHeight = textHeight;
        rows = Math.Clamp(rows, 1, MaxRows);
        columns = Math.Clamp(columns, 1, MaxColumns);
        for (int c = 0; c < columns; c++) _columnWidths.Add(columnWidth);
        for (int r = 0; r < rows; r++)
        {
            _rowHeights.Add(rowHeight);
            _cells.Add(Enumerable.Repeat("", columns).ToArray());
        }
    }

    public TableEntity(Point2D position, int rows, int columns, double columnWidth, double rowHeight, double textHeight)
        : this(Guid.NewGuid(), position, rows, columns, columnWidth, rowHeight, textHeight) { }

    /// <summary>Top-left corner.</summary>
    public Point2D Position { get; set; }

    /// <summary>Height of a capital letter in the cells.</summary>
    public double TextHeight { get; set; }

    public string Font { get; set; } = TextMeasure.DefaultFont;

    /// <summary>Text across the full width above the columns; empty for none.</summary>
    public string Title { get; set; } = "";

    /// <summary>The first row is a header: bold and centred.</summary>
    public bool HeaderRow { get; set; } = true;

    /// <summary>How text sits in the cells below the header.</summary>
    public TextJustify Justify { get; set; } = TextJustify.Left;

    public int RowCount => _rowHeights.Count;
    public int ColumnCount => _columnWidths.Count;
    public IReadOnlyList<double> ColumnWidths => _columnWidths;
    public IReadOnlyList<double> RowHeights => _rowHeights;

    /// <summary>Height of the title row: nothing without a title, otherwise three text heights.</summary>
    public double TitleHeight => string.IsNullOrWhiteSpace(Title) ? 0 : TextHeight * 3.0;

    /// <summary>Height of the title's letters.</summary>
    public double TitleTextHeight => TextHeight * 1.25;

    /// <summary>Space between a cell's edge and its text.</summary>
    public double CellMargin => TextHeight * 0.6;

    public double Width => _columnWidths.Sum();
    public double Height => TitleHeight + _rowHeights.Sum();

    public string GetCell(int row, int column) => _cells[row][column];

    public void SetCell(int row, int column, string? text) => _cells[row][column] = text ?? "";

    public void SetColumnWidth(int column, double width) => _columnWidths[column] = width;
    public void SetRowHeight(int row, double height) => _rowHeights[row] = height;

    /// <summary>Adds an empty row before <paramref name="index"/> (at the end when it is the row count).</summary>
    public bool InsertRow(int index, double? height = null)
    {
        if (RowCount >= MaxRows || index < 0 || index > RowCount) return false;
        _rowHeights.Insert(index, height ?? _rowHeights[Math.Clamp(index - 1, 0, RowCount - 1)]);
        _cells.Insert(index, Enumerable.Repeat("", ColumnCount).ToArray());
        return true;
    }

    public bool RemoveRow(int index)
    {
        if (RowCount <= 1 || index < 0 || index >= RowCount) return false;
        _rowHeights.RemoveAt(index);
        _cells.RemoveAt(index);
        return true;
    }

    /// <summary>Adds an empty column before <paramref name="index"/> (at the end when it is the column count).</summary>
    public bool InsertColumn(int index, double? width = null)
    {
        if (ColumnCount >= MaxColumns || index < 0 || index > ColumnCount) return false;
        _columnWidths.Insert(index, width ?? _columnWidths[Math.Clamp(index - 1, 0, ColumnCount - 1)]);
        for (int r = 0; r < _cells.Count; r++)
        {
            var row = _cells[r].ToList();
            row.Insert(index, "");
            _cells[r] = row.ToArray();
        }
        return true;
    }

    public bool RemoveColumn(int index)
    {
        if (ColumnCount <= 1 || index < 0 || index >= ColumnCount) return false;
        _columnWidths.RemoveAt(index);
        for (int r = 0; r < _cells.Count; r++)
        {
            var row = _cells[r].ToList();
            row.RemoveAt(index);
            _cells[r] = row.ToArray();
        }
        return true;
    }

    /// <summary>X of the left edge of a column (of the right edge of the table for the column count).</summary>
    public double ColumnLeft(int column)
    {
        double x = Position.X;
        for (int c = 0; c < column && c < ColumnCount; c++) x += _columnWidths[c];
        return x;
    }

    /// <summary>Y of the top edge of a row (of the bottom of the table for the row count).</summary>
    public double RowTop(int row)
    {
        double y = Position.Y - TitleHeight;
        for (int r = 0; r < row && r < RowCount; r++) y -= _rowHeights[r];
        return y;
    }

    /// <summary>The rectangle of a cell.</summary>
    public BoundingBox CellBox(int row, int column) =>
        new(ColumnLeft(column), RowTop(row + 1), ColumnLeft(column + 1), RowTop(row));

    /// <summary>The cell at a point: (-1, -1) outside the rows (on the title or off the table).</summary>
    public (int Row, int Column) CellAt(Point2D point)
    {
        if (!Bounds.Contains(point) || point.Y > RowTop(0)) return (-1, -1);
        int row = RowCount - 1, column = ColumnCount - 1;
        for (int r = 0; r < RowCount; r++) if (point.Y >= RowTop(r + 1)) { row = r; break; }
        for (int c = 0; c < ColumnCount; c++) if (point.X <= ColumnLeft(c + 1)) { column = c; break; }
        return (row, column);
    }

    public override string TypeName => Type;

    public override bool IsValid
    {
        get
        {
            if (!Position.IsFinite || !GeoMath.IsFinite(TextHeight) || !(TextHeight > GeoMath.Epsilon)) return false;
            if (RowCount < 1 || RowCount > MaxRows || ColumnCount < 1 || ColumnCount > MaxColumns) return false;
            if (!TextMeasure.IsValidFont(Font) || !Enum.IsDefined(Justify)) return false;
            if (Title is null || Title.Length > MaxCellLength || Title.Any(char.IsControl)) return false;
            foreach (double w in _columnWidths) if (!GeoMath.IsFinite(w) || !(w > GeoMath.Epsilon)) return false;
            foreach (double h in _rowHeights) if (!GeoMath.IsFinite(h) || !(h > GeoMath.Epsilon)) return false;
            foreach (var row in _cells)
            {
                if (row.Length != ColumnCount) return false;
                foreach (string cell in row) if (cell is null || cell.Length > MaxCellLength || cell.Any(char.IsControl)) return false;
            }
            return true;
        }
    }

    public override BoundingBox Bounds => new(Position.X, Position.Y - Height, Position.X + Width, Position.Y);

    /// <summary>Zero anywhere on the table, so a click in a cell picks it.</summary>
    public override double DistanceTo(Point2D point)
    {
        var b = Bounds;
        double outX = Math.Max(Math.Max(b.MinX - point.X, point.X - b.MaxX), 0), outY = Math.Max(Math.Max(b.MinY - point.Y, point.Y - b.MaxY), 0);
        return Math.Sqrt(outX * outX + outY * outY);
    }

    public override bool IntersectsBox(BoundingBox box) => !box.IsEmpty && Bounds.Intersects(box);

    /// <summary>Moves and scales. A table stays upright: turning or mirroring moves its middle only.</summary>
    public override void Transform(in Transform2D transform)
    {
        double factor = transform.ScaleFactor;
        var center = transform.Apply(Bounds.Center);
        for (int c = 0; c < ColumnCount; c++) _columnWidths[c] *= factor;
        for (int r = 0; r < RowCount; r++) _rowHeights[r] *= factor;
        TextHeight *= factor;
        Position = new Point2D(center.X - Width / 2, center.Y + Height / 2);
    }

    /// <summary>Takes the cells, sizes and look (not the place) of another table.</summary>
    public void CopyContentFrom(TableEntity other)
    {
        TextHeight = other.TextHeight;
        Font = other.Font;
        Title = other.Title;
        HeaderRow = other.HeaderRow;
        Justify = other.Justify;
        _columnWidths.Clear();
        _columnWidths.AddRange(other._columnWidths);
        _rowHeights.Clear();
        _rowHeights.AddRange(other._rowHeights);
        _cells.Clear();
        foreach (var row in other._cells) _cells.Add((string[])row.Clone());
    }

    protected override Entity CloneGeometry(Guid id)
    {
        var copy = new TableEntity(id, Position, 1, 1, 1, 1, TextHeight);
        copy.CopyContentFrom(this);
        return copy;
    }
}
