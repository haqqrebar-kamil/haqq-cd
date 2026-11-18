using System.Collections.ObjectModel;
using System.Globalization;
using HAQQ.CAD.Commands;
using HAQQ.CAD.Core;
using HAQQ.CAD.Documents;

namespace HAQQ.CAD.Presentation;

/// <summary>Where a block of the library window comes from.</summary>
public enum BlockSource
{
    Drawing,
    UserLibrary,
    Standard,
}

/// <summary>One block in the library window, at the size it has in this drawing.</summary>
public sealed class BlockItemViewModel
{
    public BlockItemViewModel(BlockDefinition block, BlockSource source, string size, int uses)
    {
        Block = block;
        Source = source;
        Size = size;
        Uses = uses;
    }

    public BlockDefinition Block { get; }
    public BlockSource Source { get; }
    public string Name => Block.Name;

    /// <summary>What it is; for a block of the drawing, how often it is placed.</summary>
    public string Description => Source == BlockSource.Drawing
        ? (Uses == 0 ? "Not placed in the drawing" : $"Placed {Uses} time(s) in the drawing")
        : Block.Description;

    /// <summary>Width x height of its contents, in the drawing's units.</summary>
    public string Size { get; }
    public int Uses { get; }

    /// <summary>The objects to draw in the picture of the block.</summary>
    public IReadOnlyList<Entity> Entities => Block.Entities;
}

/// <summary>
/// The Block Library window: the blocks of this drawing, the user's own library (kept on the computer for
/// every drawing) and the HAQQ library of furniture, plumbing, kitchen and door symbols. A block is chosen,
/// given a scale and an angle, and placed by clicks.
/// </summary>
public sealed class BlockLibraryViewModel : ObservableObject
{
    public const string DrawingCategory = "In this drawing";

    private readonly EditorSession _session;
    private readonly BlockLibraryStore _store;
    private string _category, _filter = "", _scaleText = "1", _rotationText = "0", _message = "";
    private BlockItemViewModel? _selected;

    public BlockLibraryViewModel(EditorSession session, BlockLibraryStore? store = null)
    {
        _session = session;
        _store = store ?? new BlockLibraryStore();
        Categories = new[] { DrawingCategory, BlockLibraryStore.Category }.Concat(StandardBlocks.Categories).ToList();
        // Open where there is something to see: the drawing's own blocks, else the first HAQQ group.
        _category = session.Document.Blocks.Count > 0 ? DrawingCategory : StandardBlocks.Categories[0];
        Reload();
    }

    public IReadOnlyList<string> Categories { get; }
    public ObservableCollection<BlockItemViewModel> Items { get; } = new();

    public string Category
    {
        get => _category;
        set { if (!string.IsNullOrEmpty(value) && SetProperty(ref _category, value)) Reload(); }
    }

    /// <summary>Part of a name to look for, in every group at once.</summary>
    public string Filter
    {
        get => _filter;
        set { if (SetProperty(ref _filter, value ?? "")) Reload(); }
    }

    public BlockItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(CanInsert));
            OnPropertyChanged(nameof(CanSaveToLibrary));
            OnPropertyChanged(nameof(CanDelete));
        }
    }

    public string ScaleText { get => _scaleText; set => SetProperty(ref _scaleText, value ?? ""); }
    public string RotationText { get => _rotationText; set => SetProperty(ref _rotationText, value ?? ""); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    public bool CanInsert => Selected is not null;
    public bool CanSaveToLibrary => Selected is { Source: BlockSource.Drawing };
    public bool CanDelete => Selected is { Source: BlockSource.Drawing or BlockSource.UserLibrary };

    private IEnumerable<BlockItemViewModel> ItemsOf(string category)
    {
        var doc = _session.Document;
        var settings = doc.Settings;
        string SizeOf(BlockDefinition b) => $"{UnitFormatter.FormatLength(b.Bounds.Width, settings)} x {UnitFormatter.FormatLength(b.Bounds.Height, settings)}";

        if (category == DrawingCategory)
            return doc.Blocks.Select(b => new BlockItemViewModel(b, BlockSource.Drawing, SizeOf(b), doc.CountBlockReferences(b.Name)));
        if (category == BlockLibraryStore.Category)
            return _store.Load().Select(entry =>
            {
                var block = entry.Block.Scaled(UnitConverter.Convert(1.0, entry.Unit, settings.Unit));
                return new BlockItemViewModel(block, BlockSource.UserLibrary, SizeOf(block), 0);
            });
        return StandardBlocks.All.Where(b => b.Category == category).Select(b =>
        {
            var block = StandardBlocks.ForUnit(b, settings.Unit);
            return new BlockItemViewModel(block, BlockSource.Standard, SizeOf(block), 0);
        });
    }

    /// <summary>Reads the blocks of the chosen group again (or of every group, while a name is being looked for).</summary>
    public void Reload()
    {
        string? keep = Selected?.Name;
        Items.Clear();
        string filter = Filter.Trim();
        var items = filter.Length == 0
            ? ItemsOf(Category)
            : Categories.SelectMany(ItemsOf).Where(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || i.Block.Description.Contains(filter, StringComparison.OrdinalIgnoreCase));
        foreach (var item in items) Items.Add(item);
        Selected = Items.FirstOrDefault(i => i.Name == keep) ?? Items.FirstOrDefault();
    }

    /// <summary>Starts placing the chosen block (the window then closes). False, with a message, when a value cannot be used.</summary>
    public bool Insert()
    {
        if (Selected is not { } item) { Message = "Choose a block first."; return false; }
        if (!double.TryParse(ScaleText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double scale) || !(scale > 1e-6) || !double.IsFinite(scale))
        {
            Message = "The scale must be a number more than 0 (1 = the size shown).";
            return false;
        }
        if (!double.TryParse(RotationText.Trim().TrimEnd('°'), NumberStyles.Float, CultureInfo.InvariantCulture, out double degrees) || !double.IsFinite(degrees))
        {
            Message = "The angle must be a number of degrees.";
            return false;
        }
        // A block of the same name already in the drawing is the one that is placed: a name means one block per drawing.
        if (item.Source != BlockSource.Drawing && _session.Document.FindBlock(item.Name) is not null)
            _session.Post($"The drawing already has a block named \"{item.Name}\": that one is placed.");
        bool ok = _session.BeginBlockInsert(item.Block, scale, degrees);
        Message = ok ? "" : "That block cannot be placed.";
        return ok;
    }

    /// <summary>Keeps the chosen block of the drawing in the user's library, for other drawings.</summary>
    public bool SaveToLibrary()
    {
        if (Selected is not { Source: BlockSource.Drawing } item) { Message = "Choose a block of this drawing first."; return false; }
        if (_store.Save(item.Block, _session.Document.Settings.Unit) is { } problem)
        {
            Message = "The block could not be kept in your library: " + problem;
            return false;
        }
        Message = $"\"{item.Name}\" is now in your library, for every drawing.";
        return true;
    }

    /// <summary>Deletes the chosen block: from the drawing (only when it is not placed) or from the user's library.</summary>
    public bool Delete()
    {
        if (Selected is not { } item) return false;
        bool ok;
        if (item.Source == BlockSource.Drawing)
        {
            ok = _session.DeleteBlock(item.Name);
            if (!ok) Message = item.Uses > 0
                ? $"\"{item.Name}\" is placed {item.Uses} time(s) in the drawing. Erase or explode those first."
                : "The block could not be deleted.";
        }
        else if (item.Source == BlockSource.UserLibrary)
        {
            ok = _store.Delete(item.Name);
            if (!ok) Message = "The block could not be removed from your library.";
        }
        else
        {
            Message = "The HAQQ library cannot be changed.";
            return false;
        }
        if (ok)
        {
            Message = "";
            Reload();
        }
        return ok;
    }
}
