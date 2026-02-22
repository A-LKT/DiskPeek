using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using DiskPeek.Converters;
using DiskPeek.Models;
using DiskPeek.Services;
using DiskPeek.Views;

namespace DiskPeek.ViewModels;

// ── Breadcrumb item ───────────────────────────────────────────────────────────

public class BreadcrumbItem
{
    public FileSystemNode Node { get; init; } = null!;
    public bool IsFirst { get; init; }

    public string DisplayName =>
        Node.Parent is null
            ? Node.FullPath.TrimEnd('\\', '/')  // root
            : Node.Name;
}

// ── Main ViewModel ────────────────────────────────────────────────────────────

public class MainViewModel : INotifyPropertyChanged
{
    // ── App version ───────────────────────────────────────────────────────────

    public static string AppVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "v?" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    // ── Services ──────────────────────────────────────────────────────────────

    private readonly DiskScannerService _scanner = new();
    private readonly CacheService _cache = new();
    public  readonly SettingsService SettingsService = new();
    private readonly UserSettingsService _userSettingsService = new();
    private AppSettings _settings;
    private int _maxChildrenDisplay;
    private CancellationTokenSource? _cts;       // full scans
    private CancellationTokenSource? _deeperCts; // on-demand deeper scans

    // ── Backing fields ────────────────────────────────────────────────────────

    private DriveInfo? _selectedDrive;
    private ScanResult? _scanResult;
    private FileSystemNode? _currentNode;
    private bool _isScanning;
    private bool _isTreemapView = true;
    private bool _isCacheStale;
    private string _statusText    = "Select a drive and click Scan.";
    private string _scanStatus    = string.Empty;
    private string _cacheInfo     = string.Empty;
    private string _cacheStaleMsg = string.Empty;
    private ICollectionView? _childrenView;

    // ── Constructor ───────────────────────────────────────────────────────────

    public MainViewModel()
    {
        _settings = SettingsService.Load();
        _maxChildrenDisplay = _userSettingsService.Load().MaxChildrenDisplay;

        _scanner.StatusUpdated += path =>
            App.Current?.Dispatcher.BeginInvoke(() => ScanStatus = Truncate(path));

        ScanCommand    = new RelayCommand(async () => await RunScanAsync(false),
                             () => SelectedDrive is not null && !IsScanning);

        RescanCommand  = new RelayCommand(async () => await RunScanAsync(true),
                             () => SelectedDrive is not null && !IsScanning && _scanResult is not null);

        CancelCommand  = new RelayCommand(
            () => { _cts?.Cancel(); _deeperCts?.Cancel(); },
            () => IsScanning);

        DrillDownCommand = new RelayCommand<FileSystemNode>(node =>
        {
            if (node?.IsDirectory == true) _ = NavigateToAsync(node);
        });

        NavigateUpCommand = new RelayCommand(NavigateUp, () => _currentNode?.Parent is not null);

        SwitchViewCommand = new RelayCommand<string>(v => IsTreemapView = v == "treemap");

        OptionsCommand = new RelayCommand(OpenOptions);

        DismissStaleWarningCommand = new RelayCommand(() => IsCacheStale = false);

        OpenInExplorerCommand = new RelayCommand<FileSystemNode>(node =>
        {
            if (node is null) return;
            string args = node.Parent is null
                ? $"\"{node.FullPath}\""
                : $"/select,\"{node.FullPath}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        });

        RefreshDrives();
    }

    /// <summary>Called by MainWindow code-behind after the Options dialog closes.</summary>
    public void ReloadSettings()
    {
        _settings = SettingsService.Load();

        _maxChildrenDisplay = _userSettingsService.Load().MaxChildrenDisplay;
        OnPropertyChanged(nameof(MaxChildrenDisplay));
        if (_currentNode is not null) RefreshChildrenView();

        // Re-evaluate stale status with updated threshold
        if (SelectedDrive is not null)
            CheckCacheStaleness(SelectedDrive.RootDirectory.FullName);
    }

    private void OpenOptions()
    {
        var dlg = new OptionsWindow(SettingsService, _settings, _userSettingsService)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        dlg.ShowDialog();
        ReloadSettings();
    }

    // ── Drive list ────────────────────────────────────────────────────────────

    public ObservableCollection<DriveInfo> AvailableDrives { get; } = [];

    public DriveInfo? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (!Set(ref _selectedDrive, value)) return;
            _scanResult  = null;
            _currentNode = null;
            Breadcrumbs.Clear();
            ChildrenView = null;
            OnPropertyChanged(nameof(CurrentNode));
            IsCacheStale = false;
            StatusText   = "Select a drive and click Scan.";
            UpdateCacheInfo();
            CommandManager.InvalidateRequerySuggested();

            if (value is not null)
                _ = TryLoadCacheForDriveAsync(value.RootDirectory.FullName);
        }
    }

    private void RefreshDrives()
    {
        AvailableDrives.Clear();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
            AvailableDrives.Add(d);
        if (AvailableDrives.Count > 0)
            SelectedDrive = AvailableDrives[0];
    }

    // ── Scan ──────────────────────────────────────────────────────────────────

    private async Task RunScanAsync(bool forceRescan)
    {
        if (SelectedDrive is null) return;
        string root = SelectedDrive.RootDirectory.FullName;

        if (forceRescan)
            _cache.DeleteCache(root);

        // Load from cache first (unless forced)
        if (!forceRescan && _cache.HasCache(root))
        {
            IsScanning = true;
            ScanStatus = "Loading from cache…";
            try
            {
                var cached = await _cache.LoadAsync(root);
                if (cached is not null)
                {
                    ApplyScanResult(cached);
                    CheckCacheStaleness(root);
                    return;
                }
            }
            finally { IsScanning = false; ScanStatus = string.Empty; }
        }

        IsCacheStale = false;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsScanning = true;
        ScanStatus = "Scanning…";
        StatusText = "Scanning…";

        try
        {
            var result = await _scanner.ScanAsync(
                root,
                _settings.ExcludedFolders.Count > 0 ? _settings.ExcludedFolders : null,
                _settings.MaxScanDepth,
                _cts.Token);
            await _cache.SaveAsync(result);
            ApplyScanResult(result);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            ScanStatus = string.Empty;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task TryLoadCacheForDriveAsync(string root)
    {
        if (!_cache.HasCache(root)) return;

        IsScanning = true;
        ScanStatus = "Loading from cache…";
        try
        {
            var cached = await _cache.LoadAsync(root);
            // Guard: user may have switched drives while we were loading
            if (cached is not null && _selectedDrive?.RootDirectory.FullName == root)
            {
                ApplyScanResult(cached);
                CheckCacheStaleness(root);
            }
        }
        catch (Exception ex) { StatusText = $"Error loading cache: {ex.Message}"; }
        finally
        {
            IsScanning = false;
            ScanStatus = string.Empty;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ApplyScanResult(ScanResult result)
    {
        _scanResult = result;
        IsTreemapView = _settings.DefaultView != "Table";
        NavigateTo(result.Root);

        string size = FileSizeConverter.FormatBytes(result.TotalSize);
        string age  = result.ScanTime != default ? $"  ·  Scanned {FormatAge(result.ScanTime)}" : string.Empty;
        StatusText = $"{result.TotalFiles:N0} files  ·  {result.TotalDirs:N0} dirs  ·  {size}{age}";

        UpdateCacheInfo();
        CommandManager.InvalidateRequerySuggested();
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [];

    /// <summary>
    /// Raised after a deeper scan completes and Children have been updated in-place.
    /// The treemap must call ForceRefresh() in response since the bound object reference
    /// hasn't changed, so the DependencyProperty callback won't fire automatically.
    /// </summary>
    public event EventHandler? CurrentNodeChildrenChanged;

    /// <summary>
    /// Navigate to <paramref name="node"/>. If the node is only partially loaded,
    /// a deeper scan is triggered automatically in the background.
    /// </summary>
    public async Task NavigateToAsync(FileSystemNode node)
    {
        _currentNode = node;
        RebuildBreadcrumbs(node);
        RefreshChildrenView();
        OnPropertyChanged(nameof(CurrentNode));
        CommandManager.InvalidateRequerySuggested();

        if (!node.IsFullyLoaded)
            await LoadDeeperAsync(node);
    }

    /// <summary>Synchronous overload used by ApplyScanResult (root is always fully loaded).</summary>
    public void NavigateTo(FileSystemNode node) => _ = NavigateToAsync(node);

    private void NavigateUp()
    {
        if (_currentNode?.Parent is { } parent)
            _ = NavigateToAsync(parent);
    }

    private async Task LoadDeeperAsync(FileSystemNode node)
    {
        // Cancel any previous deeper scan that's still running
        _deeperCts?.Cancel();
        _deeperCts = new CancellationTokenSource();
        var ct = _deeperCts.Token;

        IsScanning = true;
        ScanStatus = $"Loading {node.Name}…";
        CommandManager.InvalidateRequerySuggested();

        try
        {
            await _scanner.ScanDeeperAsync(
                node,
                _settings.ExcludedFolders.Count > 0 ? _settings.ExcludedFolders : null,
                ct);

            // Only apply if the user is still looking at this node
            if (_currentNode == node)
            {
                RefreshChildrenView();
                CurrentNodeChildrenChanged?.Invoke(this, EventArgs.Empty);

                // Persist the enriched tree back to cache
                if (_scanResult is not null)
                    _ = _cache.SaveAsync(_scanResult);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = $"Error loading deeper: {ex.Message}"; }
        finally
        {
            IsScanning = false;
            ScanStatus = string.Empty;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void RebuildBreadcrumbs(FileSystemNode node)
    {
        var chain = new Stack<FileSystemNode>();
        var cur = node;
        while (cur is not null) { chain.Push(cur); cur = cur.Parent; }

        Breadcrumbs.Clear();
        bool first = true;
        foreach (var n in chain)
        {
            Breadcrumbs.Add(new BreadcrumbItem { Node = n, IsFirst = first });
            first = false;
        }
    }

    // ── Children view for the table ───────────────────────────────────────────

    public ICollectionView? ChildrenView
    {
        get => _childrenView;
        private set => Set(ref _childrenView, value);
    }

    private void RefreshChildrenView()
    {
        if (_currentNode is null) { ChildrenView = null; return; }

        // When a limit is set, take the top-N by size; otherwise use the full list.
        IList<FileSystemNode> source;
        if (_maxChildrenDisplay > 0 && _currentNode.Children.Count > _maxChildrenDisplay)
        {
            source = _currentNode.Children
                .OrderByDescending(n => n.Size)
                .Take(_maxChildrenDisplay)
                .ToList();
        }
        else
        {
            source = _currentNode.Children;
        }

        var view = CollectionViewSource.GetDefaultView(source);
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(nameof(FileSystemNode.Size), ListSortDirection.Descending));
        ChildrenView = view;
    }

    // ── Observable properties ─────────────────────────────────────────────────

    public int MaxChildrenDisplay => _maxChildrenDisplay;

    public FileSystemNode? CurrentNode => _currentNode;

    public bool IsScanning
    {
        get => _isScanning;
        private set => Set(ref _isScanning, value);
    }

    public bool IsTreemapView
    {
        get => _isTreemapView;
        set
        {
            if (Set(ref _isTreemapView, value))
                OnPropertyChanged(nameof(IsTableView));
        }
    }

    public bool IsTableView => !_isTreemapView;

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string ScanStatus
    {
        get => _scanStatus;
        private set => Set(ref _scanStatus, value);
    }

    public string CacheInfo
    {
        get => _cacheInfo;
        private set => Set(ref _cacheInfo, value);
    }

    public bool IsCacheStale
    {
        get => _isCacheStale;
        private set => Set(ref _isCacheStale, value);
    }

    public string CacheStaleMessage
    {
        get => _cacheStaleMsg;
        private set => Set(ref _cacheStaleMsg, value);
    }

    private void UpdateCacheInfo()
    {
        if (SelectedDrive is null) { CacheInfo = string.Empty; return; }
        var t = _cache.GetCacheTime(SelectedDrive.RootDirectory.FullName);
        CacheInfo = t.HasValue ? $"Cached  {FormatAge(t.Value)}" : "No cache";
    }

    private void CheckCacheStaleness(string root)
    {
        IsCacheStale = false;
        if (_settings.CacheMaxAgeDays <= 0) return;

        var t = _cache.GetCacheTime(root);
        if (t is null) return;

        var age = DateTime.Now - t.Value;
        if (age.TotalDays >= _settings.CacheMaxAgeDays)
        {
            int days = (int)age.TotalDays;
            CacheStaleMessage = days == 1
                ? "Cache is 1 day old — consider rescanning for fresh data."
                : $"Cache is {days} days old — consider rescanning for fresh data.";
            IsCacheStale = true;
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ICommand ScanCommand               { get; }
    public ICommand RescanCommand             { get; }
    public ICommand CancelCommand             { get; }
    public ICommand DrillDownCommand          { get; }
    public ICommand NavigateUpCommand         { get; }
    public ICommand SwitchViewCommand         { get; }
    public ICommand OptionsCommand            { get; }
    public ICommand DismissStaleWarningCommand { get; }
    public ICommand OpenInExplorerCommand     { get; }

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(n);
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string FormatAge(DateTime dt)
    {
        var s = DateTime.Now - dt;
        if (s.TotalSeconds < 90)  return "just now";
        if (s.TotalMinutes < 60)  return $"{(int)s.TotalMinutes} min ago";
        if (s.TotalHours   < 24)  return $"{(int)s.TotalHours}h ago";
        return dt.ToString("yyyy-MM-dd HH:mm");
    }

    private static string Truncate(string p, int max = 80)
        => p.Length <= max ? p : "…" + p[^(max - 1)..];
}

// ── Commands ──────────────────────────────────────────────────────────────────

public class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add    => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
    public bool CanExecute(object? _) => canExecute?.Invoke() ?? true;
    public void Execute(object? _)    => execute();
}

public class RelayCommand<T>(Action<T?> execute, Func<T?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add    => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
    public bool CanExecute(object? p) => canExecute?.Invoke(p is T t ? t : default) ?? true;
    public void Execute(object? p)    => execute(p is T t ? t : default);
}
