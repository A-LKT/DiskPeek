namespace DiskPeek.Models;

public class AppSettings
{
    /// <summary>Maximum cache age in days before a stale-cache warning is shown. 0 = disabled.</summary>
    public int CacheMaxAgeDays { get; set; } = 7;

    /// <summary>Folder names (case-insensitive) to skip during scanning.</summary>
    public List<string> ExcludedFolders { get; set; } =
        ["$RECYCLE.BIN", "System Volume Information"];

    /// <summary>Maximum recursion depth for the initial scan tree. 0 = use the built-in default.</summary>
    public int MaxScanDepth { get; set; } = 0;

    /// <summary>"Treemap" or "Table".</summary>
    public string DefaultView { get; set; } = "Treemap";
}
