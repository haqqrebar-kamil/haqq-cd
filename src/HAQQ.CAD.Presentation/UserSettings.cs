using System.Text.Json;

namespace HAQQ.CAD.Presentation;

/// <summary>Per-user program preferences (not stored in drawings).</summary>
public sealed class UserSettings
{
    public const int DefaultCrosshairPercent = 8;

    /// <summary>Crosshair size as a percentage of the drawing area, 1 to 100 (100 reaches the edges).</summary>
    public int CrosshairPercent { get; set; } = DefaultCrosshairPercent;

    /// <summary>The object snaps that act automatically while OSNAP is on, as <c>SnapModes</c> flags; null = program default.</summary>
    public int? SnapModes { get; set; }
}

/// <summary>Loads and saves <see cref="UserSettings"/>. Without a path the settings live only in memory.</summary>
public sealed class UserSettingsStore
{
    private readonly string? _path;

    public UserSettingsStore(string? path = null) => _path = path;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HAQQ CAD", "settings.json");

    /// <summary>Reads the settings. A missing or damaged file gives the defaults.</summary>
    public UserSettings Load()
    {
        var settings = new UserSettings();
        if (_path is null || !File.Exists(_path)) return settings;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(_path));
            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("crosshairPercent", out var c) && c.TryGetInt32(out int percent))
                settings.CrosshairPercent = Math.Clamp(percent, 1, 100);
            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("snapModes", out var m) && m.TryGetInt32(out int modes) && modes >= 0 && modes <= 255)
                settings.SnapModes = modes;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return settings;
    }

    /// <summary>Writes the settings. Returns false when the file could not be written.</summary>
    public bool Save(UserSettings settings)
    {
        if (_path is null) return true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            string snap = settings.SnapModes is { } modes ? ",\n  \"snapModes\": " + modes : "";
            File.WriteAllText(_path, "{\n  \"crosshairPercent\": " + settings.CrosshairPercent + snap + "\n}\n");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
