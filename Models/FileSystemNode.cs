using System.Text.Json.Serialization;

namespace DiskPeek.Models;

public class FileSystemNode
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public long Size { get; set; }
    public bool IsDirectory { get; set; }
    public int FileCount { get; set; }
    public int DirectoryCount { get; set; }
    public DateTime LastModified { get; set; }
    public List<FileSystemNode> Children { get; set; } = [];

    /// <summary>
    /// False when this node was at the depth boundary during scanning —
    /// its size is correct but Children is empty. Drilling into it triggers a deeper scan.
    /// </summary>
    public bool IsFullyLoaded { get; set; } = true;

    [JsonIgnore]
    public FileSystemNode? Parent { get; set; }

    [JsonIgnore]
    public double PercentOfParent =>
        Parent is { Size: > 0 } p ? (double)Size / p.Size * 100.0 : 0;

    /// <summary>Rebuilds Parent references after deserialization.</summary>
    public void SetParentReferences(FileSystemNode? parent = null)
    {
        Parent = parent;
        foreach (var child in Children)
            child.SetParentReferences(this);
    }
}

public class ScanResult
{
    public string RootPath { get; set; } = string.Empty;
    public DateTime ScanTime { get; set; }
    public FileSystemNode Root { get; set; } = new();
    public long TotalSize { get; set; }
    public int TotalFiles { get; set; }
    public int TotalDirs { get; set; }
}
