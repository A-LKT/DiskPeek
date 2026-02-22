using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using DiskPeek.Models;

namespace DiskPeek.Services;

public class CacheService
{
    private const string CacheFileName = "diskpeek.cache";

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskPeek", "cache");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    // ── Save ──────────────────────────────────────────────────────────────────

    public async Task SaveAsync(ScanResult result)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            using var fs = File.Create(GetCachePath(result.RootPath));
            await JsonSerializer.SerializeAsync(fs, result, JsonOptions);
        }
        catch { /* ignore cache failures — a missing cache is harmless */ }
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    public async Task<ScanResult?> LoadAsync(string rootPath)
    {
        var path = GetCachePath(rootPath);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            var result = await JsonSerializer.DeserializeAsync<ScanResult>(fs, JsonOptions);
            result?.Root.SetParentReferences();
            return result;
        }
        catch { return null; }
    }

    // ── Exists / Time ─────────────────────────────────────────────────────────

    public bool HasCache(string rootPath) => File.Exists(GetCachePath(rootPath));

    public DateTime? GetCacheTime(string rootPath)
    {
        var path = GetCachePath(rootPath);
        return File.Exists(path) ? File.GetLastWriteTime(path) : null;
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public void DeleteCache(string rootPath)
    {
        var path = GetCachePath(rootPath);
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ── Path helpers ──────────────────────────────────────────────────────────

    private static string GetCachePath(string rootPath) =>
        Path.Combine(CacheDir, $"{GetDriveId(rootPath)}_{CacheFileName}");

    // ── Drive identification ───────────────────────────────────────────────────

    /// <summary>
    /// Returns a unique-enough ID for the drive, e.g. "C-A1B2C3D4".
    /// Uses the Win32 volume serial number; falls back to drive letter + total-size.
    /// </summary>
    private static string GetDriveId(string rootPath)
    {
        char letter = char.ToUpperInvariant(rootPath[0]);

        if (TryGetVolumeSerialNumber(rootPath, out uint serial))
            return $"{letter}-{serial:X8}";

        try
        {
            long mb = new DriveInfo(rootPath).TotalSize / (1024 * 1024);
            return $"{letter}-{mb}MB";
        }
        catch
        {
            return letter.ToString();
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(
        string lpRootPathName,
        System.Text.StringBuilder? lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        System.Text.StringBuilder? lpFileSystemNameBuffer,
        int nFileSystemNameSize);

    private static bool TryGetVolumeSerialNumber(string rootPath, out uint serial)
    {
        serial = 0;
        try { return GetVolumeInformation(rootPath, null, 0, out serial, out _, out _, null, 0); }
        catch { return false; }
    }
}
