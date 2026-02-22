using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DiskPeek.Models;
using DiskPeek.Services;

namespace DiskPeek.ViewModels;

public class OptionsViewModel : INotifyPropertyChanged
{
    private readonly SettingsService _settingsService;
    private readonly UserSettingsService _userSettingsService;
    private readonly Action _closeDialog;

    private int _cacheMaxAgeDays;
    private int _maxScanDepth;
    private int _maxChildrenDisplay;
    private string _defaultView = "Treemap";
    private string _newFolderName = string.Empty;

    public OptionsViewModel(SettingsService settingsService, AppSettings current,
                            UserSettingsService userSettingsService, Action closeDialog)
    {
        _settingsService     = settingsService;
        _userSettingsService = userSettingsService;
        _closeDialog         = closeDialog;

        // Initialise from current settings
        _cacheMaxAgeDays = current.CacheMaxAgeDays;
        _maxScanDepth    = current.MaxScanDepth;
        _defaultView     = current.DefaultView;
        ExcludedFolders  = new ObservableCollection<string>(current.ExcludedFolders);

        _maxChildrenDisplay = userSettingsService.Load().MaxChildrenDisplay;

        AddFolderCommand    = new RelayCommand(AddFolder, () => !string.IsNullOrWhiteSpace(_newFolderName));
        RemoveFolderCommand = new RelayCommand<string>(f => { if (f is not null) ExcludedFolders.Remove(f); });
        SaveCommand         = new RelayCommand(Save);
        CancelCommand       = new RelayCommand(_closeDialog);
    }

    // ── Properties ────────────────────────────────────────────────────────────

    public int CacheMaxAgeDays
    {
        get => _cacheMaxAgeDays;
        set => Set(ref _cacheMaxAgeDays, Math.Clamp(value, 0, 3650));
    }

    public int MaxScanDepth
    {
        get => _maxScanDepth;
        set => Set(ref _maxScanDepth, Math.Max(0, value));
    }

    public int MaxChildrenDisplay
    {
        get => _maxChildrenDisplay;
        set => Set(ref _maxChildrenDisplay, Math.Max(0, value));
    }

    public bool IsDefaultTreemap
    {
        get => _defaultView == "Treemap";
        set { if (value) { _defaultView = "Treemap"; OnPropertyChanged(); OnPropertyChanged(nameof(IsDefaultTable)); } }
    }

    public bool IsDefaultTable
    {
        get => _defaultView == "Table";
        set { if (value) { _defaultView = "Table"; OnPropertyChanged(); OnPropertyChanged(nameof(IsDefaultTreemap)); } }
    }

    public ObservableCollection<string> ExcludedFolders { get; }

    public string NewFolderName
    {
        get => _newFolderName;
        set
        {
            if (Set(ref _newFolderName, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ICommand AddFolderCommand    { get; }
    public ICommand RemoveFolderCommand { get; }
    public ICommand SaveCommand         { get; }
    public ICommand CancelCommand       { get; }

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

    private void AddFolder()
    {
        var name = NewFolderName.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (!ExcludedFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
            ExcludedFolders.Add(name);
        NewFolderName = string.Empty;
    }

    private void Save()
    {
        _settingsService.Save(new AppSettings
        {
            CacheMaxAgeDays = _cacheMaxAgeDays,
            MaxScanDepth    = _maxScanDepth,
            DefaultView     = _defaultView,
            ExcludedFolders = [.. ExcludedFolders],
        });

        // Preserve existing column widths and save the new limit
        var userSettings = _userSettingsService.Load();
        userSettings.MaxChildrenDisplay = _maxChildrenDisplay;
        _userSettingsService.Save(userSettings);

        _closeDialog();
    }
}
