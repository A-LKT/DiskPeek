using System.IO;
using System.Text.Json;

namespace DiskPeek.Services;

public class UserSettings
{
    public Dictionary<string, double> ColumnWidths { get; set; } = new();

    /// <summary>Maximum number of children to display per directory. 0 = show all.</summary>
    public int MaxChildrenDisplay { get; set; } = 0;
}

public class UserSettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskPeek", "user-settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public UserSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new();
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<UserSettings>(json, JsonOptions) ?? new();
        }
        catch { return new(); }
    }

    public void Save(UserSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch { }
    }
}
