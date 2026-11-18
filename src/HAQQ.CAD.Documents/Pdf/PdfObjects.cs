using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace HAQQ.CAD.Documents.Pdf;

/// <summary>The PDF could not be read; the message says why in plain words.</summary>
public sealed class PdfReadException : Exception
{
    public PdfReadException(string message) : base(message) { }
}

internal sealed record PdfName(string Value);
internal sealed record PdfRef(int Number, int Generation);
internal sealed record PdfString(byte[] Bytes);

internal sealed class PdfDict : Dictionary<string, object?>
{
    public object? Get(string key) => TryGetValue(key, out var value) ? value : null;
}

internal sealed class PdfStream
{
    public PdfStream(PdfDict dictionary, byte[] raw)
    {
        Dictionary = dictionary;
        Raw = raw;
    }

    public PdfDict Dictionary { get; }
    public byte[] Raw { get; }
}

/// <summary>An operator of a page's content with the values written before it.</summary>
internal readonly record struct PdfOperation(string Operator, List<object?> Operands);

/// <summary>
/// Reads the pieces PDF files are made of: numbers, names, strings, arrays, dictionaries, references and
/// streams. HAQQ's own reader, written for this program.
/// </summary>
internal sealed class PdfLexer
{
    private readonly byte[] _data;
    public int Position;

    public PdfLexer(byte[] data, int position = 0)
    {
        _data = data;
        Position = position;
    }

    public bool AtEnd => Position >= _data.Length;

    private static bool IsWhite(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
    private static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
    private static bool IsRegular(byte b) => !IsWhite(b) && !IsDelimiter(b);

    public void SkipWhite()
    {
        while (Position < _data.Length)
        {
            byte b = _data[Position];
            if (IsWhite(b)) Position++;
            else if (b == (byte)'%')
            {
                while (Position < _data.Length && _data[Position] != 10 && _data[Position] != 13) Position++;
            }
            else break;
        }
    }

    /// <summary>The next bare word (an operator or keyword such as obj, stream, R, true), or null at a delimiter or the end.</summary>
    public string? PeekWord()
    {
        int save = Position;
        SkipWhite();
        int start = Position;
        while (Position < _data.Length && IsRegular(_data[Position])) Position++;
        string? word = Position > start ? Encoding.ASCII.GetString(_data, start, Position - start) : null;
        Position = save;
        return word;
    }

    public string? ReadWord()
    {
        SkipWhite();
        int start = Position;
        while (Position < _data.Length && IsRegular(_data[Position])) Position++;
        return Position > start ? Encoding.ASCII.GetString(_data, start, Position - start) : null;
    }

    /// <summary>
    /// Reads one object. A bare word that is not a number, true, false or null comes back as a string (an
    /// operator of a content stream). Returns <see cref="End"/> when nothing is left.
    /// </summary>
    public object? ReadObject()
    {
        SkipWhite();
        if (Position >= _data.Length) return End;
        byte b = _data[Position];

        if (b == (byte)'/')
        {
            Position++;
            var name = new StringBuilder();
            while (Position < _data.Length && IsRegular(_data[Position]))
            {
                byte c = _data[Position++];
                if (c == (byte)'#' && Position + 1 < _data.Length && IsHex(_data[Position]) && IsHex(_data[Position + 1]))
                {
                    name.Append((char)(HexValue(_data[Position]) * 16 + HexValue(_data[Position + 1])));
                    Position += 2;
                }
                else name.Append((char)c);
            }
            return new PdfName(name.ToString());
        }
        if (b == (byte)'(') return ReadLiteralString();
        if (b == (byte)'<')
        {
            if (Position + 1 < _data.Length && _data[Position + 1] == (byte)'<') return ReadDictionary();
            return ReadHexString();
        }
        if (b == (byte)'[')
        {
            Position++;
            var list = new List<object?>();
            while (true)
            {
                SkipWhite();
                if (Position >= _data.Length) break;
                if (_data[Position] == (byte)']') { Position++; break; }
                var item = ReadObject();
                if (ReferenceEquals(item, End)) break;
                list.Add(item);
            }
            return list;
        }
        if (b is (byte)']' or (byte)')' or (byte)'>' or (byte)'{' or (byte)'}')
        {
            Position++;                                   // stray: pass over it
            return ReadObject();
        }

        string word = ReadWord() ?? "";
        if (word.Length == 0) { Position++; return ReadObject(); }
        if (word == "true") return true;
        if (word == "false") return false;
        if (word == "null") return null;
        if (TryNumber(word, out double number))
        {
            // "12 0 R" is a reference to object 12.
            if (number >= 0 && number == Math.Floor(number) && word.All(char.IsDigit))
            {
                int save = Position;
                string? generation = ReadWord();
                if (generation is not null && generation.All(char.IsDigit) && generation.Length <= 5)
                {
                    SkipWhite();
                    if (Position < _data.Length && _data[Position] == (byte)'R' && (Position + 1 >= _data.Length || !IsRegular(_data[Position + 1])))
                    {
                        Position++;
                        return new PdfRef((int)number, int.Parse(generation, CultureInfo.InvariantCulture));
                    }
                }
                Position = save;
            }
            return number;
        }
        return word;
    }

    /// <summary>Marks the end of the data.</summary>
    public static readonly object End = new();

    private static bool TryNumber(string word, out double number)
    {
        number = 0;
        char first = word[0];
        if (!(char.IsDigit(first) || first is '+' or '-' or '.')) return false;
        // Some writers put "--5" or "5-" by mistake; the plain cases are what matter.
        return double.TryParse(word, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
    }

    private static bool IsHex(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';
    private static int HexValue(byte b) => b <= (byte)'9' ? b - '0' : (b | 0x20) - 'a' + 10;

    private PdfString ReadLiteralString()
    {
        Position++;                                       // (
        var bytes = new List<byte>();
        int depth = 1;
        while (Position < _data.Length)
        {
            byte c = _data[Position++];
            if (c == (byte)'\\' && Position < _data.Length)
            {
                byte e = _data[Position++];
                switch (e)
                {
                    case (byte)'n': bytes.Add(10); break;
                    case (byte)'r': bytes.Add(13); break;
                    case (byte)'t': bytes.Add(9); break;
                    case (byte)'b': bytes.Add(8); break;
                    case (byte)'f': bytes.Add(12); break;
                    case 13:
                        if (Position < _data.Length && _data[Position] == 10) Position++;
                        break;                            // a line continued
                    case 10: break;
                    default:
                        if (e is >= (byte)'0' and <= (byte)'7')
                        {
                            int value = e - '0';
                            for (int i = 0; i < 2 && Position < _data.Length && _data[Position] is >= (byte)'0' and <= (byte)'7'; i++)
                                value = value * 8 + (_data[Position++] - '0');
                            bytes.Add((byte)value);
                        }
                        else bytes.Add(e);
                        break;
                }
            }
            else if (c == (byte)'(') { depth++; bytes.Add(c); }
            else if (c == (byte)')')
            {
                if (--depth == 0) break;
                bytes.Add(c);
            }
            else bytes.Add(c);
        }
        return new PdfString(bytes.ToArray());
    }

    private PdfString ReadHexString()
    {
        Position++;                                       // <
        var bytes = new List<byte>();
        int high = -1;
        while (Position < _data.Length)
        {
            byte c = _data[Position++];
            if (c == (byte)'>') break;
            if (!IsHex(c)) continue;
            if (high < 0) high = HexValue(c);
            else
            {
                bytes.Add((byte)(high * 16 + HexValue(c)));
                high = -1;
            }
        }
        if (high >= 0) bytes.Add((byte)(high * 16));
        return new PdfString(bytes.ToArray());
    }

    private PdfDict ReadDictionary()
    {
        Position += 2;                                    // <<
        var dict = new PdfDict();
        while (true)
        {
            SkipWhite();
            if (Position >= _data.Length) break;
            if (_data[Position] == (byte)'>' && Position + 1 < _data.Length && _data[Position + 1] == (byte)'>')
            {
                Position += 2;
                break;
            }
            var key = ReadObject();
            if (ReferenceEquals(key, End)) break;
            if (key is not PdfName name) continue;        // damaged: look for the next key
            var value = ReadObject();
            if (ReferenceEquals(value, End)) break;
            dict[name.Value] = value;
        }
        return dict;
    }

    /// <summary>After a dictionary: the bytes of its stream when the word "stream" follows, else null.</summary>
    public byte[]? ReadStreamBody(PdfDict dictionary, Func<object?, object?> resolve)
    {
        int save = Position;
        SkipWhite();
        if (Position + 6 > _data.Length || Encoding.ASCII.GetString(_data, Position, 6) != "stream")
        {
            Position = save;
            return null;
        }
        Position += 6;
        if (Position < _data.Length && _data[Position] == 13) Position++;
        if (Position < _data.Length && _data[Position] == 10) Position++;
        int start = Position;

        // The stated length, when it is believable (the word "endstream" follows it); otherwise look for that word.
        int end = -1;
        if (resolve(dictionary.Get("Length")) is double stated && stated >= 0 && start + (long)stated <= _data.Length)
        {
            int candidate = start + (int)stated, probe = candidate;
            while (probe < _data.Length && IsWhite(_data[probe])) probe++;
            if (Matches(probe, "endstream")) end = candidate;
        }
        if (end < 0)
        {
            end = IndexOf("endstream", start);
            if (end < 0) end = _data.Length;
            else
            {
                if (end > start && _data[end - 1] == 10) end--;
                if (end > start && _data[end - 1] == 13) end--;
            }
        }
        var body = new byte[end - start];
        Array.Copy(_data, start, body, 0, body.Length);
        int after = IndexOf("endstream", end);
        Position = after < 0 ? _data.Length : after + 9;
        return body;
    }

    private bool Matches(int at, string word)
    {
        if (at < 0 || at + word.Length > _data.Length) return false;
        for (int i = 0; i < word.Length; i++) if (_data[at + i] != word[i]) return false;
        return true;
    }

    public int IndexOf(string word, int from)
    {
        for (int i = Math.Max(from, 0); i + word.Length <= _data.Length; i++)
            if (_data[i] == word[0] && Matches(i, word)) return i;
        return -1;
    }
}

/// <summary>Undoes the packing of a stream's bytes (Flate, ASCII hex, ASCII 85; with the PNG and TIFF predictors).</summary>
internal static class PdfFilters
{
    public static byte[] Decode(PdfStream stream, Func<object?, object?> resolve)
    {
        var filters = AsList(resolve(stream.Dictionary.Get("Filter")));
        var parameters = AsList(resolve(stream.Dictionary.Get("DecodeParms") ?? stream.Dictionary.Get("DP")));
        byte[] data = stream.Raw;
        for (int i = 0; i < filters.Count; i++)
        {
            string name = (resolve(filters[i]) as PdfName)?.Value ?? "";
            var parms = i < parameters.Count ? resolve(parameters[i]) as PdfDict : null;
            data = name switch
            {
                "FlateDecode" or "Fl" => Predict(Inflate(data), parms, resolve),
                "ASCIIHexDecode" or "AHx" => AsciiHex(data),
                "ASCII85Decode" or "A85" => Ascii85(data),
                "DCTDecode" or "DCT" or "JPXDecode" or "CCITTFaxDecode" or "CCF" or "JBIG2Decode" =>
                    throw new PdfReadException("a picture"),            // images are not unpacked here
                _ => throw new PdfReadException($"This PDF packs its content with \"{name}\", which HAQQ CAD cannot read yet."),
            };
        }
        return data;
    }

    private static List<object?> AsList(object? value) => value switch
    {
        null => new List<object?>(),
        List<object?> list => list,
        _ => new List<object?> { value },
    };

    private static byte[] Inflate(byte[] data)
    {
        try
        {
            using var input = new MemoryStream(data);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[16384];
            try
            {
                int read;
                while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    if (output.Length > 512L * 1024 * 1024) throw new PdfReadException("A part of this PDF unpacks to more than 512 MB.");
                }
            }
            catch (InvalidDataException) when (output.Length > 0)
            {
                // A stream cut short: use what could be unpacked.
            }
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            throw new PdfReadException("A part of this PDF is damaged and could not be unpacked.");
        }
    }

    private static byte[] Predict(byte[] data, PdfDict? parms, Func<object?, object?> resolve)
    {
        if (parms is null) return data;
        int predictor = (int)(resolve(parms.Get("Predictor")) as double? ?? 1);
        if (predictor <= 1) return data;
        int colors = (int)(resolve(parms.Get("Colors")) as double? ?? 1);
        int bits = (int)(resolve(parms.Get("BitsPerComponent")) as double? ?? 8);
        int columns = (int)(resolve(parms.Get("Columns")) as double? ?? 1);
        int bpp = Math.Max(1, colors * bits / 8), rowLength = (colors * bits * columns + 7) / 8;
        if (rowLength <= 0) return data;

        if (predictor == 2)
        {
            if (bits != 8) return data;
            for (int row = 0; row + rowLength <= data.Length; row += rowLength)
                for (int i = bpp; i < rowLength; i++) data[row + i] += data[row + i - bpp];
            return data;
        }

        // PNG predictors: every row starts with a byte that says how it was predicted.
        int rows = data.Length / (rowLength + 1);
        var result = new byte[rows * rowLength];
        var previous = new byte[rowLength];
        for (int r = 0; r < rows; r++)
        {
            int source = r * (rowLength + 1), target = r * rowLength;
            byte type = data[source];
            for (int i = 0; i < rowLength; i++)
            {
                int raw = data[source + 1 + i];
                int left = i >= bpp ? result[target + i - bpp] : 0, up = previous[i], upLeft = i >= bpp ? previous[i - bpp] : 0;
                int value = type switch
                {
                    1 => raw + left,
                    2 => raw + up,
                    3 => raw + (left + up) / 2,
                    4 => raw + Paeth(left, up, upLeft),
                    _ => raw,
                };
                result[target + i] = (byte)value;
            }
            Array.Copy(result, target, previous, 0, rowLength);
        }
        return result;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] AsciiHex(byte[] data)
    {
        var bytes = new List<byte>();
        int high = -1;
        foreach (byte c in data)
        {
            if (c == (byte)'>') break;
            int v = c is >= (byte)'0' and <= (byte)'9' ? c - '0' : (c | 0x20) is >= 'a' and <= 'f' ? (c | 0x20) - 'a' + 10 : -1;
            if (v < 0) continue;
            if (high < 0) high = v;
            else { bytes.Add((byte)(high * 16 + v)); high = -1; }
        }
        if (high >= 0) bytes.Add((byte)(high * 16));
        return bytes.ToArray();
    }

    private static byte[] Ascii85(byte[] data)
    {
        var bytes = new List<byte>();
        var group = new int[5];
        int count = 0;
        foreach (byte c in data)
        {
            if (c == (byte)'~') break;
            if (c is 0 or 9 or 10 or 12 or 13 or 32) continue;
            if (c == (byte)'z' && count == 0) { bytes.AddRange(new byte[4]); continue; }
            if (c < (byte)'!' || c > (byte)'u') continue;
            group[count++] = c - '!';
            if (count == 5) { Emit(group, 4); count = 0; }
        }
        if (count > 1)
        {
            for (int i = count; i < 5; i++) group[i] = 84;
            Emit(group, count - 1);
        }
        return bytes.ToArray();

        void Emit(int[] g, int n)
        {
            long value = 0;
            foreach (int digit in g) value = value * 85 + digit;
            for (int i = 0; i < n; i++) bytes.Add((byte)(value >> (24 - 8 * i)));
        }
    }
}
