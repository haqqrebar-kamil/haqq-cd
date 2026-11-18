using HAQQ.CAD.Core;

namespace HAQQ.CAD.Documents;

/// <summary>
/// The user's own block library: a folder with one small HCAD file per block, so that blocks made in one
/// drawing can be placed in any other. Each file remembers the unit its block was drawn in.
/// </summary>
public sealed class BlockLibraryStore
{
    public const string Category = "My library";
    private const string Extension = ".hcad";

    private readonly string? _folder;

    /// <param name="folder">The library folder; null for a store that keeps nothing (tests, or no user profile).</param>
    public BlockLibraryStore(string? folder = null) => _folder = folder;

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HAQQ CAD", "Blocks");

    /// <summary>Where a block of this name is kept. Characters a file name cannot have become underscores.</summary>
    private string FileFor(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new string(name.Trim().Select(c => invalid.Contains(c) || c == '.' ? '_' : c).ToArray());
        return Path.Combine(_folder!, safe + Extension);
    }

    /// <summary>The blocks in the library with the unit each was drawn in, by name. Files that cannot be read are passed over.</summary>
    public IReadOnlyList<(BlockDefinition Block, LinearUnit Unit)> Load()
    {
        var blocks = new List<(BlockDefinition, LinearUnit)>();
        if (_folder is null || !Directory.Exists(_folder)) return blocks;
        foreach (string file in Directory.EnumerateFiles(_folder, "*" + Extension).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var document = HcadSerializer.LoadFromBytes(File.ReadAllBytes(file)).Document;
                if (document.Blocks.Count > 0) blocks.Add((document.Blocks[0], document.Settings.Unit));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HcadFormatException)
            {
                // Not a block file, or locked by another program.
            }
        }
        return blocks;
    }

    /// <summary>Adds a block to the library, in place of one with the same name. Returns why it could not be kept, or null.</summary>
    public string? Save(BlockDefinition block, LinearUnit unit)
    {
        if (_folder is null) return "There is no library folder on this computer.";
        if (!block.IsValid) return "That block cannot be kept.";
        try
        {
            var document = new DrawingDocument();
            document.Settings.Unit = unit;
            document.AddBlock(new BlockDefinition(block.Name, block.Entities, block.Description, Category));
            Directory.CreateDirectory(_folder);
            string file = FileFor(block.Name), temporary = file + ".tmp";
            File.WriteAllBytes(temporary, HcadSerializer.SaveToBytes(document));
            File.Move(temporary, file, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    /// <summary>Takes a block out of the library. False when it was not there or its file could not be removed.</summary>
    public bool Delete(string name)
    {
        if (_folder is null) return false;
        try
        {
            string file = FileFor(name);
            if (!File.Exists(file)) return false;
            File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
