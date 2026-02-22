using System.ComponentModel;
using System.IO;
using DiskPeek.Models;

namespace DiskPeek.Services;

public class DiskScannerService
{
    /// <summary>How many directory levels to fully enumerate on the initial scan.</summary>
    public const int InitialDepth = 4;

    /// <summary>How many additional levels to enumerate when the user drills into a partial node.</summary>
    public const int DeeperIncrement = 3;

    public event Action<string>? StatusUpdated;

    // ── Full initial scan ─────────────────────────────────────────────────────

    public async Task<ScanResult> ScanAsync(
        string rootPath,
        IEnumerable<string>? excludedFolders = null,
        int maxDepth = 0,
        CancellationToken ct = default)
    {
        var excluded = excludedFolders is not null
            ? new HashSet<string>(excludedFolders, StringComparer.OrdinalIgnoreCase)
            : null;
        int depth = maxDepth > 0 ? maxDepth : InitialDepth;

        var root = await Task.Run(
            () => ScanDirectory(rootPath, parent: null, currentDepth: 0, maxDepth: depth, excluded, ct),
            ct);

        return new ScanResult
        {
            RootPath   = rootPath,
            ScanTime   = DateTime.Now,
            Root       = root,
            TotalSize  = root.Size,
            TotalFiles = root.FileCount,
            TotalDirs  = root.DirectoryCount,
        };
    }

    // ── Deeper on-demand scan ─────────────────────────────────────────────────

    /// <summary>
    /// Re-scans <paramref name="node"/> to <see cref="DeeperIncrement"/> additional levels,
    /// replacing its Children in-place. Safe to call from the UI thread (runs work on Task.Run).
    /// </summary>
    public async Task ScanDeeperAsync(
        FileSystemNode node,
        IEnumerable<string>? excludedFolders = null,
        CancellationToken ct = default)
    {
        StatusUpdated?.Invoke(node.FullPath);

        var excluded = excludedFolders is not null
            ? new HashSet<string>(excludedFolders, StringComparer.OrdinalIgnoreCase)
            : null;

        var scanned = await Task.Run(
            () => ScanDirectory(node.FullPath, parent: null, currentDepth: 0, maxDepth: DeeperIncrement, excluded, ct),
            ct);

        // Apply results back onto the original node object (preserves parent chain above it)
        node.Children.Clear();
        node.FileCount      = scanned.FileCount;
        node.DirectoryCount = scanned.DirectoryCount;
        node.IsFullyLoaded  = scanned.IsFullyLoaded;
        // Size is intentionally kept as-is — it was already accurate from the initial scan
        // and changing it here would require cascading updates through ancestors.

        foreach (var child in scanned.Children)
        {
            child.SetParentReferences(node);
            node.Children.Add(child);
        }
    }

    // ── Core recursive scanner ────────────────────────────────────────────────

    private FileSystemNode ScanDirectory(
        string path, FileSystemNode? parent, int currentDepth, int maxDepth,
        HashSet<string>? excluded, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        DirectoryInfo dirInfo;
        try { dirInfo = new DirectoryInfo(path); }
        catch { return MakeInaccessibleNode(path, parent); }

        var node = new FileSystemNode
        {
            Name          = string.IsNullOrEmpty(dirInfo.Name) ? path.TrimEnd('\\') : dirInfo.Name,
            FullPath      = path,
            IsDirectory   = true,
            Parent        = parent,
            LastModified  = SafeGetLastWrite(dirInfo),
        };

        StatusUpdated?.Invoke(path);

        // ── Depth boundary: compute size without building child nodes ─────────
        if (currentDepth >= maxDepth)
        {
            var (sz, fc, dc) = ComputeSizeOnly(path, excluded, ct);
            node.Size           = sz;
            node.FileCount      = fc;
            node.DirectoryCount = dc;
            node.IsFullyLoaded  = false;
            return node;
        }

        // ── Within depth limit: enumerate children fully ──────────────────────
        try
        {
            foreach (var entry in dirInfo.GetFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();

                if (entry is FileInfo fileInfo)
                {
                    var fileNode = new FileSystemNode
                    {
                        Name         = fileInfo.Name,
                        FullPath     = fileInfo.FullName,
                        IsDirectory  = false,
                        Size         = SafeGetSize(fileInfo),
                        LastModified = SafeGetLastWrite(fileInfo),
                        FileCount    = 1,
                        Parent       = node,
                        IsFullyLoaded = true,
                    };
                    node.Children.Add(fileNode);
                    node.Size      += fileNode.Size;
                    node.FileCount++;
                }
                else if (entry is DirectoryInfo subDir)
                {
                    if (excluded is not null && excluded.Contains(subDir.Name)) continue;
                    var subNode = ScanDirectory(subDir.FullName, node, currentDepth + 1, maxDepth, excluded, ct);
                    node.Children.Add(subNode);
                    node.Size           += subNode.Size;
                    node.FileCount      += subNode.FileCount;
                    node.DirectoryCount += 1 + subNode.DirectoryCount;
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        catch (Win32Exception) { }

        node.IsFullyLoaded = true;
        node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        return node;
    }

    // ── Size-only recursion (no node tree built) ──────────────────────────────

    /// <summary>
    /// Recursively sums file sizes without constructing FileSystemNode objects.
    /// Used at the depth boundary to get accurate sizes cheaply.
    /// </summary>
    private (long Size, int Files, int Dirs) ComputeSizeOnly(
        string path, HashSet<string>? excluded, CancellationToken ct, int depth = 0)
    {
        // Guard against stack overflow on abnormally deep or circular directory structures
        if (depth > 512) return (0, 0, 0);

        long size = 0;
        int  files = 0, dirs = 0;

        try
        {
            foreach (var entry in new DirectoryInfo(path).GetFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();

                if (entry is FileInfo fi)
                {
                    size += SafeGetSize(fi);
                    files++;
                }
                else if (entry is DirectoryInfo sub)
                {
                    if (excluded is not null && excluded.Contains(sub.Name)) continue;
                    // Skip reparse points (symlinks, junctions) to avoid infinite loops
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var (s, f, d) = ComputeSizeOnly(sub.FullName, excluded, ct, depth + 1);
                    size  += s;
                    files += f;
                    dirs  += 1 + d;
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        catch (Win32Exception) { }

        return (size, files, dirs);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static FileSystemNode MakeInaccessibleNode(string path, FileSystemNode? parent) =>
        new()
        {
            Name          = Path.GetFileName(path) is { Length: > 0 } n ? n : path,
            FullPath      = path,
            IsDirectory   = true,
            IsFullyLoaded = false,
            Parent        = parent,
        };

    private static long SafeGetSize(FileInfo fi)
    {
        try { return fi.Length; } catch { return 0; }
    }

    private static DateTime SafeGetLastWrite(FileSystemInfo fsi)
    {
        try { return fsi.LastWriteTime; } catch { return DateTime.MinValue; }
    }
}
