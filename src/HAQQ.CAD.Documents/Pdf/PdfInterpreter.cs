using System.Text;

namespace HAQQ.CAD.Documents.Pdf;

/// <summary>
/// Goes through the drawing instructions of a page and collects what they draw: outlines with their pen, and
/// text with its place and size. Pictures are counted, not read.
/// </summary>
internal sealed class PdfInterpreter
{
    private const int MaxSegments = 4_000_000;

    private readonly PdfFile _file;
    private readonly PdfPageContent _page;
    private int _segments;

    public PdfInterpreter(PdfFile file, PdfPageContent page)
    {
        _file = file;
        _page = page;
    }

    private sealed class State
    {
        public PdfMatrix Ctm = PdfMatrix.Identity;
        public double LineWidth = 1.0;
        public bool Dashed;
        public (byte R, byte G, byte B) Stroke = (0, 0, 0), Fill = (0, 0, 0);
        // Text
        public PdfFont? Font;
        public double FontSize, Leading, CharSpace, WordSpace, HorizontalScale = 1.0, Rise;

        public State Clone() => (State)MemberwiseClone();
    }

    /// <summary>What is needed of a font to read its text: the letter for each code and how wide each is.</summary>
    private sealed class PdfFont
    {
        public bool TwoByte;
        public Dictionary<int, string>? ToUnicode;
        public double[]? Widths;
        public int FirstChar;
        public double DefaultWidth = 500;
        public bool Readable = true;
    }

    private readonly Dictionary<PdfDict, PdfFont> _fonts = new();

    public void Run(byte[] content, PdfDict resources, PdfMatrix start, int depth)
    {
        var state = new State { Ctm = start };
        var stack = new Stack<State>();
        var lexer = new PdfLexer(content);
        var operands = new List<object?>();

        var subpaths = new List<PdfSubpath>();
        PdfSubpath? current = null;
        double cx = 0, cy = 0, sx = 0, sy = 0;          // current point and start of the subpath, in user space
        bool inText = false;
        PdfMatrix textMatrix = PdfMatrix.Identity, lineMatrix = PdfMatrix.Identity;

        double N(int i) => i < operands.Count && operands[i] is double d ? d : 0;

        void MoveTo(double x, double y)
        {
            current = null;
            (cx, cy, sx, sy) = (x, y, x, y);
        }

        PdfSubpath Sub()
        {
            if (current is null)
            {
                current = new PdfSubpath();
                subpaths.Add(current);
            }
            return current;
        }

        void LineTo(double x, double y)
        {
            if (++_segments > MaxSegments) throw new PdfReadException("This page has more than 4 million lines: too many to copy in one go.");
            var (x1, y1) = state.Ctm.Apply(cx, cy);
            var (x2, y2) = state.Ctm.Apply(x, y);
            Sub().Segments.Add(new PdfSegment(x1, y1, x2, y2, false, 0, 0, 0, 0));
            (cx, cy) = (x, y);
        }

        void CurveTo(double ax, double ay, double bx, double by, double x, double y)
        {
            if (++_segments > MaxSegments) throw new PdfReadException("This page has more than 4 million lines: too many to copy in one go.");
            var (x1, y1) = state.Ctm.Apply(cx, cy);
            var (c1x, c1y) = state.Ctm.Apply(ax, ay);
            var (c2x, c2y) = state.Ctm.Apply(bx, by);
            var (x2, y2) = state.Ctm.Apply(x, y);
            Sub().Segments.Add(new PdfSegment(x1, y1, x2, y2, true, c1x, c1y, c2x, c2y));
            (cx, cy) = (x, y);
        }

        void ClosePath()
        {
            if (current is null) return;
            if (Math.Abs(cx - sx) > 1e-9 || Math.Abs(cy - sy) > 1e-9) LineTo(sx, sy);
            current.Closed = true;
            current = null;
            (cx, cy) = (sx, sy);
        }

        void Paint(bool stroke, bool fill)
        {
            var drawn = subpaths.Where(s => s.Segments.Count > 0).ToList();
            if (drawn.Count > 0 && (stroke || fill))
            {
                var color = stroke ? state.Stroke : state.Fill;
                var path = new PdfPath
                {
                    Stroked = stroke,
                    Filled = fill,
                    LineWidth = state.LineWidth * state.Ctm.Scale,
                    Dashed = stroke && state.Dashed,
                    R = color.R,
                    G = color.G,
                    B = color.B,
                };
                path.Subpaths.AddRange(drawn);
                _page.Paths.Add(path);
            }
            subpaths = new List<PdfSubpath>();
            current = null;
        }

        (byte, byte, byte) Color(int count)
        {
            static byte To(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
            switch (count)
            {
                case 1: { byte g = To(N(0)); return (g, g, g); }
                case 3: return (To(N(0)), To(N(1)), To(N(2)));
                case 4:
                {
                    double k = N(3);
                    return (To((1 - N(0)) * (1 - k)), To((1 - N(1)) * (1 - k)), To((1 - N(2)) * (1 - k)));
                }
                default: return (0, 0, 0);
            }
        }

        void ShowText(byte[] bytes)
        {
            if (state.Font is not { } font) return;
            var text = new StringBuilder();
            double advance = 0;                           // in text space units (before the font size)
            int step = font.TwoByte ? 2 : 1;
            bool unreadable = false;
            for (int i = 0; i + step <= bytes.Length; i += step)
            {
                int code = step == 2 ? bytes[i] * 256 + bytes[i + 1] : bytes[i];
                string? letters = null;
                if (font.ToUnicode is not null) font.ToUnicode.TryGetValue(code, out letters);
                if (letters is null)
                {
                    if (font.ToUnicode is null && !font.TwoByte && font.Readable) letters = Latin(code);
                    else unreadable = true;
                }
                if (letters is not null) text.Append(letters);
                double w = font.Widths is not null && code - font.FirstChar >= 0 && code - font.FirstChar < font.Widths.Length
                    ? font.Widths[code - font.FirstChar] : font.DefaultWidth;
                advance += w / 1000.0 * state.FontSize + state.CharSpace + (step == 1 && code == 32 ? state.WordSpace : 0);
            }
            advance *= state.HorizontalScale;

            var whole = textMatrix.Then(state.Ctm);
            string shown = text.ToString();
            if (shown.Trim().Length > 0)
            {
                var (x, y) = whole.Apply(0, state.Rise);
                var (ex, ey) = whole.Apply(advance, state.Rise);
                var (ux, uy) = whole.Apply(0, state.Rise + state.FontSize);
                double height = Math.Sqrt((ux - x) * (ux - x) + (uy - y) * (uy - y));
                var (dx, dy) = whole.Apply(1, state.Rise);
                double angle = Math.Atan2(dy - y, dx - x);
                double width = Math.Sqrt((ex - x) * (ex - x) + (ey - y) * (ey - y));
                if (height > 1e-6 && _page.Texts.Count < 200_000) _page.Texts.Add(new PdfText(shown, x, y, height, angle, width));
            }
            else if (unreadable && text.Length == 0) _page.UnreadableTextCount++;
            textMatrix = new PdfMatrix(1, 0, 0, 1, advance, 0).Then(textMatrix);
        }

        while (true)
        {
            object? token;
            try { token = lexer.ReadObject(); }
            catch (IndexOutOfRangeException) { break; }
            if (ReferenceEquals(token, PdfLexer.End)) break;
            if (token is not string op)
            {
                if (operands.Count < 64) operands.Add(token);
                continue;
            }

            switch (op)
            {
                // ---- graphics state
                case "q": stack.Push(state.Clone()); break;
                case "Q": if (stack.Count > 0) state = stack.Pop(); break;
                case "cm": state.Ctm = new PdfMatrix(N(0), N(1), N(2), N(3), N(4), N(5)).Then(state.Ctm); break;
                case "w": state.LineWidth = Math.Abs(N(0)); break;
                case "d":
                    state.Dashed = operands.Count > 0 && operands[0] is List<object?> dashes && dashes.Any(v => v is double d && d > 0);
                    break;
                case "gs":
                    if (operands.Count > 0 && operands[0] is PdfName gsName &&
                        _file.DictForInterpreter(_file.DictForInterpreter(resources.Get("ExtGState"))?.Get(gsName.Value)) is { } gs)
                    {
                        if (_file.ResolveForInterpreter(gs.Get("LW")) is double lw) state.LineWidth = Math.Abs(lw);
                        if (_file.ResolveForInterpreter(gs.Get("D")) is List<object?> d && d.Count > 0 && _file.ResolveForInterpreter(d[0]) is List<object?> pattern)
                            state.Dashed = pattern.Any(v => v is double x && x > 0);
                    }
                    break;

                // ---- colours
                case "G": state.Stroke = Color(1); break;
                case "g": state.Fill = Color(1); break;
                case "RG": state.Stroke = Color(3); break;
                case "rg": state.Fill = Color(3); break;
                case "K": state.Stroke = Color(4); break;
                case "k": state.Fill = Color(4); break;
                case "SC" or "SCN":
                {
                    int n = operands.Count(o => o is double);
                    if (n is 1 or 3 or 4) state.Stroke = Color(n);
                    break;
                }
                case "sc" or "scn":
                {
                    int n = operands.Count(o => o is double);
                    if (n is 1 or 3 or 4) state.Fill = Color(n);
                    break;
                }

                // ---- outlines
                case "m": MoveTo(N(0), N(1)); break;
                case "l": LineTo(N(0), N(1)); break;
                case "c": CurveTo(N(0), N(1), N(2), N(3), N(4), N(5)); break;
                case "v": CurveTo(cx, cy, N(0), N(1), N(2), N(3)); break;
                case "y": CurveTo(N(0), N(1), N(2), N(3), N(2), N(3)); break;
                case "h": ClosePath(); break;
                case "re":
                {
                    double x = N(0), y = N(1), w = N(2), h = N(3);
                    MoveTo(x, y);
                    LineTo(x + w, y);
                    LineTo(x + w, y + h);
                    LineTo(x, y + h);
                    ClosePath();
                    break;
                }
                case "S": Paint(true, false); break;
                case "s": ClosePath(); Paint(true, false); break;
                case "f" or "F" or "f*": Paint(false, true); break;
                case "B" or "B*": Paint(true, true); break;
                case "b" or "b*": ClosePath(); Paint(true, true); break;
                case "n": Paint(false, false); break;      // a clipping outline, not drawn

                // ---- text
                case "BT":
                    inText = true;
                    textMatrix = lineMatrix = PdfMatrix.Identity;
                    break;
                case "ET": inText = false; break;
                case "Tf":
                    state.FontSize = N(1);
                    state.Font = operands.Count > 0 && operands[0] is PdfName fontName
                        ? FontOf(_file.DictForInterpreter(_file.DictForInterpreter(resources.Get("Font"))?.Get(fontName.Value)))
                        : null;
                    break;
                case "TL": state.Leading = N(0); break;
                case "Tc": state.CharSpace = N(0); break;
                case "Tw": state.WordSpace = N(0); break;
                case "Tz": state.HorizontalScale = N(0) / 100.0; break;
                case "Ts": state.Rise = N(0); break;
                case "Td":
                    lineMatrix = new PdfMatrix(1, 0, 0, 1, N(0), N(1)).Then(lineMatrix);
                    textMatrix = lineMatrix;
                    break;
                case "TD":
                    state.Leading = -N(1);
                    lineMatrix = new PdfMatrix(1, 0, 0, 1, N(0), N(1)).Then(lineMatrix);
                    textMatrix = lineMatrix;
                    break;
                case "Tm":
                    lineMatrix = textMatrix = new PdfMatrix(N(0), N(1), N(2), N(3), N(4), N(5));
                    break;
                case "T*":
                    lineMatrix = new PdfMatrix(1, 0, 0, 1, 0, -state.Leading).Then(lineMatrix);
                    textMatrix = lineMatrix;
                    break;
                case "Tj":
                    if (inText && operands.Count > 0 && operands[0] is PdfString shown) ShowText(shown.Bytes);
                    break;
                case "'" or "\"":
                    if (op == "\"") { state.WordSpace = N(0); state.CharSpace = N(1); }
                    lineMatrix = new PdfMatrix(1, 0, 0, 1, 0, -state.Leading).Then(lineMatrix);
                    textMatrix = lineMatrix;
                    if (inText && operands.LastOrDefault() is PdfString quoted) ShowText(quoted.Bytes);
                    break;
                case "TJ":
                    if (inText && operands.Count > 0 && operands[0] is List<object?> pieces)
                    {
                        // One run of text written in pieces with small shifts between letters: put it back together.
                        var joined = new List<byte>();
                        double shiftBefore = 0;
                        foreach (var piece in pieces)
                        {
                            if (piece is PdfString part) joined.AddRange(part.Bytes);
                            else if (piece is double shift)
                            {
                                // A large shift to the right is a gap between words.
                                if (-shift > 200 && joined.Count > 0 && state.Font is { TwoByte: false }) joined.Add(32);
                                else if (joined.Count == 0) shiftBefore += shift;
                            }
                        }
                        if (shiftBefore != 0)
                            textMatrix = new PdfMatrix(1, 0, 0, 1, -shiftBefore / 1000.0 * state.FontSize * state.HorizontalScale, 0).Then(textMatrix);
                        ShowText(joined.ToArray());
                    }
                    break;

                // ---- pictures and nested drawings
                case "Do":
                    if (operands.Count > 0 && operands[0] is PdfName xName &&
                        _file.ResolveForInterpreter(_file.DictForInterpreter(resources.Get("XObject"))?.Get(xName.Value)) is PdfStream xObject)
                    {
                        string subtype = (_file.ResolveForInterpreter(xObject.Dictionary.Get("Subtype")) as PdfName)?.Value ?? "";
                        if (subtype == "Image") _page.ImageCount++;
                        else if (subtype == "Form" && depth < PdfFile.MaxFormDepth)
                        {
                            var m = _file.NumbersForInterpreter(xObject.Dictionary.Get("Matrix"), 6);
                            var form = m is null ? PdfMatrix.Identity : new PdfMatrix(m[0], m[1], m[2], m[3], m[4], m[5]);
                            byte[] body;
                            try { body = _file.DecodeForInterpreter(xObject); }
                            catch (PdfReadException) { break; }
                            Run(body, _file.DictForInterpreter(xObject.Dictionary.Get("Resources")) ?? resources, form.Then(state.Ctm), depth + 1);
                        }
                    }
                    break;
                case "BI":
                {
                    // A small picture written inside the instructions: pass over it to the word EI.
                    _page.ImageCount++;
                    int id = lexer.IndexOf("ID", lexer.Position);
                    int at = id < 0 ? content.Length : id + 2;
                    while (true)
                    {
                        int ei = lexer.IndexOf("EI", at);
                        if (ei < 0) { lexer.Position = content.Length; break; }
                        bool before = ei == 0 || content[ei - 1] is 10 or 13 or 32 or 9;
                        bool after = ei + 2 >= content.Length || content[ei + 2] is 10 or 13 or 32 or 9;
                        if (before && after) { lexer.Position = ei + 2; break; }
                        at = ei + 2;
                    }
                    break;
                }
            }
            operands.Clear();
        }
        // Outlines left unpainted at the end are not drawn.
    }

    private static string Latin(int code) => code switch
    {
        < 32 => "",
        0x91 or 0x92 => "'",
        0x93 or 0x94 => "\"",
        0x96 or 0x97 => "-",
        0x95 => "•",
        0xA0 => " ",
        >= 0x80 and < 0xA0 => "",
        _ => ((char)code).ToString(),
    };

    private PdfFont? FontOf(PdfDict? dict)
    {
        if (dict is null) return null;
        if (_fonts.TryGetValue(dict, out var cached)) return cached;
        var font = new PdfFont();
        string subtype = (_file.ResolveForInterpreter(dict.Get("Subtype")) as PdfName)?.Value ?? "";
        font.TwoByte = subtype == "Type0";

        if (_file.ResolveForInterpreter(dict.Get("ToUnicode")) is PdfStream map)
        {
            try { font.ToUnicode = ParseToUnicode(_file.DecodeForInterpreter(map)); }
            catch (PdfReadException) { }
        }
        if (font.TwoByte)
        {
            font.DefaultWidth = 1000;
            if (_file.ResolveForInterpreter(dict.Get("DescendantFonts")) is List<object?> { Count: > 0 } descendants &&
                _file.DictForInterpreter(descendants[0]) is { } descendant &&
                _file.ResolveForInterpreter(descendant.Get("DW")) is double dw) font.DefaultWidth = dw;
        }
        else
        {
            font.FirstChar = (int)(_file.ResolveForInterpreter(dict.Get("FirstChar")) as double? ?? 0);
            if (_file.ResolveForInterpreter(dict.Get("Widths")) is List<object?> widths)
                font.Widths = widths.Select(w => _file.ResolveForInterpreter(w) as double? ?? 0).ToArray();
            // A symbol font, or one with its own table of letters, cannot be read as ordinary letters.
            var encoding = _file.ResolveForInterpreter(dict.Get("Encoding"));
            string baseFont = (_file.ResolveForInterpreter(dict.Get("BaseFont")) as PdfName)?.Value ?? "";
            if (font.ToUnicode is null && (baseFont.Contains("Symbol") || baseFont.Contains("Dingbats") ||
                (encoding is PdfDict e && e.ContainsKey("Differences") && subtype == "Type3"))) font.Readable = false;
        }
        _fonts[dict] = font;
        return font;
    }

    /// <summary>Reads a font's table from codes to letters (the "bfchar" and "bfrange" lists of a ToUnicode map).</summary>
    private static Dictionary<int, string> ParseToUnicode(byte[] data)
    {
        var map = new Dictionary<int, string>();
        var lexer = new PdfLexer(data);
        var operands = new List<object?>();

        static int Code(PdfString s)
        {
            int value = 0;
            foreach (byte b in s.Bytes) value = value * 256 + b;
            return value;
        }

        static string Letters(PdfString s) => s.Bytes.Length >= 2 ? Encoding.BigEndianUnicode.GetString(s.Bytes) : Encoding.Latin1.GetString(s.Bytes);

        while (true)
        {
            var token = lexer.ReadObject();
            if (ReferenceEquals(token, PdfLexer.End)) break;
            if (token is not string op)
            {
                if (operands.Count < 100_000) operands.Add(token);
                continue;
            }
            if (op == "endbfchar")
            {
                for (int i = 0; i + 1 < operands.Count; i += 2)
                    if (operands[i] is PdfString source && operands[i + 1] is PdfString target) map[Code(source)] = Letters(target);
            }
            else if (op == "endbfrange")
            {
                for (int i = 0; i + 2 < operands.Count; i += 3)
                {
                    if (operands[i] is not PdfString low || operands[i + 1] is not PdfString high) continue;
                    int from = Code(low), to = Math.Min(Code(high), from + 65535);
                    if (operands[i + 2] is PdfString first)
                    {
                        string letters = Letters(first);
                        if (letters.Length == 0) continue;
                        for (int code = from; code <= to; code++)
                            map[code] = letters[..^1] + (char)(letters[^1] + (code - from));
                    }
                    else if (operands[i + 2] is List<object?> each)
                    {
                        for (int code = from; code <= to && code - from < each.Count; code++)
                            if (each[code - from] is PdfString one) map[code] = Letters(one);
                    }
                }
            }
            if (op.StartsWith("begin", StringComparison.Ordinal) || op.StartsWith("end", StringComparison.Ordinal)) operands.Clear();
        }
        return map;
    }
}
