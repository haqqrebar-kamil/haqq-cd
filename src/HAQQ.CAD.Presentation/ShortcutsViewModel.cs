using System.Collections.ObjectModel;
using HAQQ.CAD.Commands;

namespace HAQQ.CAD.Presentation;

/// <summary>One command in the Shortcuts window.</summary>
public sealed class ShortcutRowViewModel : ObservableObject
{
    private string _text;

    internal ShortcutRowViewModel(CommandInfo info, IReadOnlyList<string> current)
    {
        Command = info.Name;
        Description = info.Description;
        DefaultText = string.Join(", ", info.DefaultShortcuts);
        _text = string.Join(", ", current);
    }

    public string Command { get; }
    public string Description { get; }

    /// <summary>The shortcuts the command comes with.</summary>
    public string DefaultText { get; }

    /// <summary>The shortcuts as typed: separated by commas or spaces. Empty = none (the full name still works).</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (!SetProperty(ref _text, value ?? "")) return;
            OnPropertyChanged(nameof(IsChanged));
        }
    }

    public bool IsChanged => !Parse().SequenceEqual(ShortcutsViewModel.Split(DefaultText), StringComparer.OrdinalIgnoreCase);

    internal List<string> Parse() => ShortcutsViewModel.Split(_text);

    public void Reset() => Text = DefaultText;
}

/// <summary>
/// The Shortcuts window: every command with the short names that start it. The user changes them here; they are
/// kept per user, not in the drawing. The full command name always works.
/// </summary>
public sealed class ShortcutsViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private readonly List<ShortcutRowViewModel> _all;
    private string _filter = "";
    private string _message = "";

    public ShortcutsViewModel(MainViewModel owner)
    {
        _owner = owner;
        _all = owner.Session.CommandCatalog.Select(c => new ShortcutRowViewModel(c, owner.Session.ShortcutsOf(c.Name))).ToList();
        ApplyFilter();
    }

    /// <summary>The commands shown (all of them, or the ones matching <see cref="Filter"/>).</summary>
    public ObservableCollection<ShortcutRowViewModel> Rows { get; } = new();

    /// <summary>Text to look for in the command name, its description or its shortcuts.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value ?? "")) return;
            ApplyFilter();
        }
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    private void ApplyFilter()
    {
        string f = _filter.Trim();
        Rows.Clear();
        foreach (var row in _all)
        {
            if (f.Length == 0 || row.Command.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                row.Description.Contains(f, StringComparison.OrdinalIgnoreCase) || row.Text.Contains(f, StringComparison.OrdinalIgnoreCase))
                Rows.Add(row);
        }
    }

    /// <summary>Puts every command back to the shortcuts it came with (not saved until <see cref="Save"/>).</summary>
    public void ResetAll()
    {
        foreach (var row in _all) row.Reset();
        Message = "All shortcuts are back to the standard ones. Press Save to keep that.";
    }

    /// <summary>Checks and applies the shortcuts. False, with <see cref="Message"/> saying why, when they cannot be used.</summary>
    public bool Save()
    {
        var custom = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _all) custom[row.Command] = row.Parse();
        if (_owner.ApplyShortcuts(custom) is { } problem)
        {
            Message = problem;
            return false;
        }
        Message = "";
        return true;
    }

    internal static List<string> Split(string text) =>
        text.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().ToUpperInvariant())
            .Distinct()
            .ToList();
}
