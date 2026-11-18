using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HAQQ.CAD.Documents.Pdf;

/// <summary>One piece of an outline on a PDF page: a straight line to <see cref="End"/>, or a Bezier curve with two control points.</summary>
public readonly record struct PdfSegment(double X1, double Y1, double X2, double Y2, bool IsCurve, double C1X, double C1Y, double C2X, double C2Y);

/// <summary>A run of connected segments.</summary>
public sealed class PdfSubpath
{
    public List<PdfSegment> Segments { get; } = new();
    public bool Closed { get; set; }
}

/// <summary>Line work drawn on a page with one pen: its outlines, and how it was painted. Coordinates are in points (1/72 inch), Y up, from the page's lower-left corner.</summary>
public sealed class PdfPath
{
    public List<PdfSubpath> Subpaths { get; } = new();
    public bool Stroked { get; init; }
    public bool Filled { get; init; }

    /// <summary>Pen width in points (0 = the thinnest line the printer can make).</summary>
    public double LineWidth { get; init; }
    public bool Dashed { get; init; }
    public byte R { get; init; }
    public byte G { get; init; }
    public byte B { get; init; }
}

/// <summary>A piece of text on a page: where it starts (left end of its baseline), how tall its letters are and its angle.</summary>
public sealed record PdfText(string Text, double X, double Y, double Height, double Angle, double Width);

/// <summary>What was found on one page.</summary>
public sealed class PdfPageContent
{
    /// <summary>Page size in points, after any rotation of the page.</summary>
    public double Width { get; init; }
    public double Height { get; init; }
    public List<PdfPath> Paths { get; } = new();
    public List<PdfText> Texts { get; } = new();

    /// <summary>Pictures on the page (a scan is one large picture). Their content is not read.</summary>
    public int ImageCount { get; set; }

    /// <summary>Text that could not be turned into letters (fonts without a table for it).</summary>
    public int UnreadableTextCount { get; set; }

    /// <summary>True when the page is mostly a picture: nothing, or next to nothing, is drawn as lines.</summary>
    public bool LooksScanned => ImageCount > 0 && Paths.Sum(p => p.Subpaths.Sum(s => s.Segments.Count)) < 20;
}

/// <summary>
/// A PDF file opened for copying drawings out of it: finds its pages and reads the line work and text on
/// them. HAQQ's own reader. It does not show pictures, and it refuses files locked with a password.
/// </summary>
public sealed partial class PdfFile
{
    private readonly byte[] _data;
    private readonly Dictionary<int, object?> _objects = new();
    private readonly List<PdfDict> _pages = new();
    private const int MaxDepth = 12;

    [GeneratedRegex(@"(\d{1,9})[\x00\t\n\f\r ]+(\d{1,5})[\x00\t\n\f\r ]+obj")]
    private static partial Regex ObjectHeader();

    private PdfFile(byte[] data) => _data = data;

    public int PageCount => _pages.Count;

    /// <summary>Opens a PDF. Throws <see cref="PdfReadException"/> with a plain reason when it cannot be used.</summary>
    public static PdfFile Open(byte[] data)
    {
        if (data is null || data.Length < 16) throw new PdfReadException("This is not a PDF file.");
        string head = Encoding.Latin1.GetString(data, 0, Math.Min(1024, data.Length));
        if (!head.Contains("%PDF-")) throw new PdfReadException("This is not a PDF file.");
        var file = new PdfFile(data);
        file.IndexObjects();
        file.FindPages();
        if (file._pages.Count == 0) throw new PdfReadException("No pages were found in this PDF.");
        return file;
    }

    // ------------------------------------------------------------- structure

    /// <summary>
    /// Finds every object by going through the file from the start (later ones replace earlier ones of the same
    /// number, as PDF updates work), then unpacks the objects kept inside object streams.
    /// </summary>
    private void IndexObjects()
    {
        // Latin1 maps each byte to one character, so positions in the text are positions in the file.
        string text = Encoding.Latin1.GetString(_data);
        var lexer = new PdfLexer(_data);
        var packed = new List<PdfStream>();
        bool encrypted = false;
        int position = 0;

        while (position < text.Length)
        {
            var match = ObjectHeader().Match(text, position);
            if (!match.Success) break;
            // The number must start a word (not be the tail of a longer one).
            if (match.Index > 0 && !char.IsWhiteSpace(text[match.Index - 1]) && text[match.Index - 1] is not ('\0' or '>' or ']' or ')'))
            {
                position = match.Index + 1;
                continue;
            }
            int number = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            lexer.Position = match.Index + match.Length;
            object? value;
            try
            {
                value = lexer.ReadObject();
                if (value is PdfDict dict)
                {
                    if (lexer.ReadStreamBody(dict, v => v is PdfRef ? null : v) is { } body)
                    {
                        var stream = new PdfStream(dict, body);
                        value = stream;
                        string type = (dict.Get("Type") as PdfName)?.Value ?? "";
                        if (type == "ObjStm") packed.Add(stream);
                        if (type == "XRef" && dict.ContainsKey("Encrypt")) encrypted = true;
                    }
                }
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or FormatException)
            {
                position = match.Index + match.Length;
                continue;
            }
            if (!ReferenceEquals(value, PdfLexer.End)) _objects[number] = value;
            position = Math.Max(lexer.Position, match.Index + match.Length);
        }

        // A password-locked file names its lock in the trailer.
        foreach (Match trailer in Regex.Matches(text, @"trailer\s*<<"))
        {
            var t = new PdfLexer(_data, trailer.Index + 7).ReadObject() as PdfDict;
            if (t is not null && t.ContainsKey("Encrypt")) encrypted = true;
        }
        if (encrypted) throw new PdfReadException("This PDF is locked (encrypted). Save an unlocked copy and use that.");

        // Stream lengths written as references could not be followed above; they are found by the word "endstream" instead.
        foreach (var stream in packed)
        {
            try
            {
                byte[] content = PdfFilters.Decode(stream, Resolve);
                int count = (int)(Resolve(stream.Dictionary.Get("N")) as double? ?? 0);
                int first = (int)(Resolve(stream.Dictionary.Get("First")) as double? ?? 0);
                var header = new PdfLexer(content);
                var entries = new List<(int Number, int Offset)>();
                for (int i = 0; i < count; i++)
                {
                    if (header.ReadObject() is not double n || header.ReadObject() is not double offset) break;
                    entries.Add(((int)n, (int)offset));
                }
                foreach (var (n, offset) in entries)
                {
                    if (first + offset >= content.Length) continue;
                    var value = new PdfLexer(content, first + offset).ReadObject();
                    // An object written in the open after this stream was made is the newer one; streams made later win over earlier streams.
                    if (!ReferenceEquals(value, PdfLexer.End)) _packedObjects[n] = value;
                }
            }
            catch (PdfReadException)
            {
                // An object stream that cannot be unpacked: its objects are simply missing.
            }
        }
        foreach (var (n, value) in _packedObjects) _objects.TryAdd(n, value);
    }

    private readonly Dictionary<int, object?> _packedObjects = new();

    /// <summary>Follows a reference to the object it names (a value that is not a reference comes back as it is).</summary>
    private object? Resolve(object? value)
    {
        for (int i = 0; i < 32 && value is PdfRef reference; i++)
            value = _objects.TryGetValue(reference.Number, out var found) ? found : null;
        return value is PdfRef ? null : value;
    }

    private PdfDict? Dict(object? value) => Resolve(value) switch
    {
        PdfDict d => d,
        PdfStream s => s.Dictionary,
        _ => null,
    };

    private void FindPages()
    {
        // The catalog names the page tree; the newest catalog is the one in force.
        PdfDict? catalog = null;
        foreach (var (_, value) in _objects.OrderBy(p => p.Key))
            if (value is PdfDict d && (d.Get("Type") as PdfName)?.Value == "Catalog" && d.ContainsKey("Pages")) catalog = d;

        var seen = new HashSet<PdfDict>();
        if (catalog is not null) Walk(Dict(catalog.Get("Pages")), new PdfDict(), 0, seen);
        if (_pages.Count > 0) return;

        // No usable catalog: take every page object there is, in file order.
        foreach (var (_, value) in _objects.OrderBy(p => p.Key))
            if (value is PdfDict d && (d.Get("Type") as PdfName)?.Value == "Page") _pages.Add(d);
    }

    private static readonly string[] Inherited = { "MediaBox", "CropBox", "Resources", "Rotate" };

    private void Walk(PdfDict? node, PdfDict inherited, int depth, HashSet<PdfDict> seen)
    {
        if (node is null || depth > 64 || !seen.Add(node) || _pages.Count >= 5000) return;
        var passed = new PdfDict();
        foreach (var (k, v) in inherited) passed[k] = v;
        foreach (string key in Inherited) if (node.ContainsKey(key)) passed[key] = node[key];

        if (Resolve(node.Get("Kids")) is List<object?> kids && (node.Get("Type") as PdfName)?.Value != "Page")
        {
            foreach (var kid in kids) Walk(Dict(kid), passed, depth + 1, seen);
            return;
        }
        // A page: give it what its parents hold for it.
        var page = new PdfDict();
        foreach (var (k, v) in passed) page[k] = v;
        foreach (var (k, v) in node) page[k] = v;
        _pages.Add(page);
    }

    private double[]? Numbers(object? value, int count)
    {
        if (Resolve(value) is not List<object?> list || list.Count < count) return null;
        var numbers = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (Resolve(list[i]) is not double d) return null;
            numbers[i] = d;
        }
        return numbers;
    }

    // --------------------------------------------------------------- content

    /// <summary>Reads the line work and the text of a page (first page = 0).</summary>
    public PdfPageContent ReadPage(int index)
    {
        if (index < 0 || index >= _pages.Count) throw new PdfReadException("There is no such page.");
        var page = _pages[index];
        var box = Numbers(page.Get("CropBox"), 4) ?? Numbers(page.Get("MediaBox"), 4) ?? new[] { 0.0, 0, 612, 792 };
        double x0 = Math.Min(box[0], box[2]), y0 = Math.Min(box[1], box[3]);
        double width = Math.Abs(box[2] - box[0]), height = Math.Abs(box[3] - box[1]);
        double unit = Resolve(page.Get("UserUnit")) as double? ?? 1.0;
        int rotate = (((int)(Resolve(page.Get("Rotate")) as double? ?? 0) % 360) + 360) % 360;

        // From the page's own coordinates to: origin at the lower-left corner, upright, in points.
        var toPage = new PdfMatrix(1, 0, 0, 1, -x0, -y0);
        toPage = rotate switch
        {
            90 => toPage.Then(new PdfMatrix(0, -1, 1, 0, 0, width)),
            180 => toPage.Then(new PdfMatrix(-1, 0, 0, -1, width, height)),
            270 => toPage.Then(new PdfMatrix(0, 1, -1, 0, height, 0)),
            _ => toPage,
        };
        toPage = toPage.Then(new PdfMatrix(unit, 0, 0, unit, 0, 0));
        bool turned = rotate is 90 or 270;

        var content = new PdfPageContent { Width = (turned ? height : width) * unit, Height = (turned ? width : height) * unit };
        var interpreter = new PdfInterpreter(this, content);
        interpreter.Run(ContentBytes(page.Get("Contents")), Dict(page.Get("Resources")) ?? new PdfDict(), toPage, 0);
        return content;
    }

    /// <summary>The page's content: one stream, or several that read as one.</summary>
    private byte[] ContentBytes(object? contents)
    {
        var parts = new List<byte[]>();
        void Add(object? item)
        {
            if (Resolve(item) is PdfStream stream)
            {
                try { parts.Add(PdfFilters.Decode(stream, Resolve)); }
                catch (PdfReadException ex) when (ex.Message != "a picture") { throw; }
                catch (PdfReadException) { }
            }
        }
        if (Resolve(contents) is List<object?> list) foreach (var item in list) Add(item);
        else Add(contents);

        int total = parts.Sum(p => p.Length + 1);
        var all = new byte[total];
        int at = 0;
        foreach (var part in parts)
        {
            Array.Copy(part, 0, all, at, part.Length);
            at += part.Length;
            all[at++] = (byte)'\n';
        }
        return all;
    }

    internal object? ResolveForInterpreter(object? value) => Resolve(value);
    internal PdfDict? DictForInterpreter(object? value) => Dict(value);
    internal double[]? NumbersForInterpreter(object? value, int count) => Numbers(value, count);
    internal byte[] DecodeForInterpreter(PdfStream stream) => PdfFilters.Decode(stream, Resolve);
    internal const int MaxFormDepth = MaxDepth;
}

/// <summary>A PDF transformation: x' = A x + C y + E, y' = B x + D y + F.</summary>
internal readonly record struct PdfMatrix(double A, double B, double C, double D, double E, double F)
{
    public static readonly PdfMatrix Identity = new(1, 0, 0, 1, 0, 0);

    /// <summary>This transformation followed by <paramref name="next"/>.</summary>
    public PdfMatrix Then(PdfMatrix next) => new(
        A * next.A + B * next.C, A * next.B + B * next.D,
        C * next.A + D * next.C, C * next.B + D * next.D,
        E * next.A + F * next.C + next.E, E * next.B + F * next.D + next.F);

    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    /// <summary>How much lengths grow (the mean of both directions).</summary>
    public double Scale => Math.Sqrt(Math.Abs(A * D - B * C));
}
