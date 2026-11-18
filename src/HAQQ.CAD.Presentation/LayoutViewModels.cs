using System.Collections.ObjectModel;
using System.Globalization;
using HAQQ.CAD.Commands;
using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;
using HAQQ.CAD.Rendering;

namespace HAQQ.CAD.Presentation;

/// <summary>
/// The New Layout window: the name, the paper (a standard sheet or a size of one's own), which way round it
/// lies, what is drawn on it to begin with (nothing, a border, a border and a title block) and whether it
/// gets a viewport onto the model.
/// </summary>
public sealed class NewLayoutViewModel : ObservableObject
{
    public const string CustomPaper = "Custom size";

    private readonly EditorSession _session;
    private string _name, _paper, _template, _company, _widthText = "", _heightText = "", _message = "";
    private bool _landscape = true, _withViewport = true;

    public NewLayoutViewModel(EditorSession session)
    {
        _session = session;
        int n = 1;
        do { _name = "Layout " + n++.ToString(CultureInfo.InvariantCulture); } while (session.Document.FindLayout(_name) is not null);
        Papers = LayoutStandards.Papers.Select(p => p.Name).Append(CustomPaper).ToList();
        _paper = LayoutStandards.DefaultPaper(session.Document.Settings).Name;
        Templates = new[] { "Border and title block", "Border only", "Blank sheet" };
        _company = session.CompanyName;
        _template = Templates[0];
        ShowSize();
    }

    public IReadOnlyList<string> Papers { get; }
    public IReadOnlyList<string> Templates { get; }

    public string Name { get => _name; set => SetProperty(ref _name, value ?? ""); }

    public string Paper
    {
        get => _paper;
        set
        {
            if (string.IsNullOrEmpty(value) || !SetProperty(ref _paper, value)) return;
            OnPropertyChanged(nameof(IsCustom));
            ShowSize();
        }
    }

    public bool IsCustom => Paper == CustomPaper;

    /// <summary>Wide side across (true) or upright (false).</summary>
    public bool Landscape
    {
        get => _landscape;
        set { if (SetProperty(ref _landscape, value)) { OnPropertyChanged(nameof(Portrait)); ShowSize(); } }
    }

    public bool Portrait { get => !_landscape; set => Landscape = !value; }

    /// <summary>The sheet's width and height in the drawing's units; typed for a custom size, shown for a standard sheet.</summary>
    public string WidthText { get => _widthText; set => SetProperty(ref _widthText, value ?? ""); }
    public string HeightText { get => _heightText; set => SetProperty(ref _heightText, value ?? ""); }

    /// <summary>The user's company, for the top line of the title block. Remembered on the computer for the next layout.</summary>
    public string Company { get => _company; set => SetProperty(ref _company, value ?? ""); }

    public string Template { get => _template; set { if (!string.IsNullOrEmpty(value)) SetProperty(ref _template, value); } }
    public bool WithViewport { get => _withViewport; set => SetProperty(ref _withViewport, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    private void ShowSize()
    {
        if (IsCustom) return;                           // what was typed stays
        var paper = LayoutStandards.Papers.First(p => p.Name == Paper);
        var (w, h) = LayoutStandards.SizeOf(paper, _session.Document.Settings);
        if (!Landscape) (w, h) = (h, w);
        WidthText = UnitFormatter.FormatLength(w, _session.Document.Settings);
        HeightText = UnitFormatter.FormatLength(h, _session.Document.Settings);
    }

    /// <summary>Makes the layout and opens it. False, with a message, when something typed cannot be used.</summary>
    public bool Create()
    {
        var settings = _session.Document.Settings;
        string name = Name.Trim();
        if (!Layout.IsValidName(name)) { Message = "Give the layout a name of 1 to 64 characters (not \"Model\")."; return false; }
        if (_session.Document.FindLayout(name) is not null) { Message = $"There is already a layout named \"{name}\"."; return false; }

        double width, height;
        if (IsCustom)
        {
            if (!UnitFormatter.TryParseLength(WidthText, settings, out width) || !UnitFormatter.TryParseLength(HeightText, settings, out height) ||
                !(width > GeoMath.Epsilon) || !(height > GeoMath.Epsilon))
            {
                Message = "Type the sheet's width and height, each more than 0.";
                return false;
            }
            // The orientation says which is the long side.
            if (Landscape != width >= height) (width, height) = (height, width);
        }
        else
        {
            (width, height) = LayoutStandards.SizeOf(LayoutStandards.Papers.First(p => p.Name == Paper), settings);
            if (!Landscape) (width, height) = (height, width);
        }
        if (!_session.SetCompanyName(Company)) { Message = "A company name is at most 64 characters."; return false; }
        var template = Template == Templates[2] ? LayoutTemplate.Blank : Template == Templates[1] ? LayoutTemplate.Border : LayoutTemplate.TitleBlock;
        bool ok = _session.AddLayout(name, width, height, template, WithViewport) is not null;
        Message = ok ? "" : "The layout could not be made.";
        return ok;
    }
}

/// <summary>A tab that can go into a PDF.</summary>
public sealed class PdfSheetViewModel : ObservableObject
{
    private bool _include;

    public PdfSheetViewModel(Guid id, string name, string size, bool include)
    {
        Id = id;
        Name = name;
        Size = size;
        _include = include;
    }

    public Guid Id { get; }
    public string Name { get; }
    public string Size { get; }
    public bool Include { get => _include; set => SetProperty(ref _include, value); }
}

/// <summary>
/// The Export PDF window: which layouts go into the PDF (in tab order), black or colour, and one file with a
/// page per sheet or a file for each sheet.
/// </summary>
public sealed class PdfExportViewModel : ObservableObject
{
    private readonly EditorSession _session;
    private bool _monochrome = true, _separate;
    private string _message = "";

    public PdfExportViewModel(EditorSession session)
    {
        _session = session;
        var doc = session.Document;
        var settings = doc.Settings;
        bool none = doc.Layouts.Count == 0;
        Sheets.Add(new PdfSheetViewModel(Guid.Empty, "Model", "fitted to a Letter or A4 page", none));
        foreach (var layout in doc.Layouts)
            Sheets.Add(new PdfSheetViewModel(layout.Id, layout.Name,
                $"{UnitFormatter.FormatLength(layout.PaperWidth, settings)} x {UnitFormatter.FormatLength(layout.PaperHeight, settings)}", true));
    }

    public ObservableCollection<PdfSheetViewModel> Sheets { get; } = new();
    public bool Monochrome { get => _monochrome; set => SetProperty(ref _monochrome, value); }

    /// <summary>A file for each sheet (true), or one file with every sheet as a page.</summary>
    public bool Separate { get => _separate; set => SetProperty(ref _separate, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    /// <summary>
    /// Makes the PDF, or the PDFs. Each comes with the name of its sheet (empty for the one combined file).
    /// Null, with a message, when nothing is chosen or it cannot be made.
    /// </summary>
    public IReadOnlyList<(string SheetName, byte[] Pdf)>? Build()
    {
        var chosen = Sheets.Where(s => s.Include).ToList();
        if (chosen.Count == 0) { Message = "Tick at least one sheet."; return null; }
        try
        {
            var files = new List<(string, byte[])>();
            if (Separate && chosen.Count > 1)
                foreach (var sheet in chosen) files.Add((sheet.Name, PdfExporter.Export(_session.Document, new[] { sheet.Id }, Monochrome)));
            else files.Add(("", PdfExporter.Export(_session.Document, chosen.Select(s => s.Id).ToList(), Monochrome)));
            Message = "";
            return files;
        }
        catch (InvalidOperationException ex)
        {
            Message = ex.Message;
            return null;
        }
    }

    /// <summary>A file name for a sheet next to the chosen one: "Tower - S-101 Plan.pdf".</summary>
    /// <summary>A file name (no folder, no extension) made from a layout's name: what the save window proposes when one sheet is written.</summary>
    public static string SafeFileName(string sheetName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new string(sheetName.Trim().Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c).ToArray()).Trim(' ', '.');
        return safe.Length > 0 ? safe : "Layout";
    }

    public static string FileNameFor(string chosenPath, string sheetName)
    {
        if (sheetName.Length == 0) return chosenPath;
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new string(sheetName.Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' ? '_' : c).ToArray());
        int slash = chosenPath.LastIndexOfAny(new[] { '/', '\\' }), dot = chosenPath.LastIndexOf('.');
        string stem = dot > slash ? chosenPath[..dot] : chosenPath, extension = dot > slash ? chosenPath[dot..] : ".pdf";
        return $"{stem} - {safe}{extension}";
    }
}
