using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiskPeek.Models;
using DiskPeek.Services;
using DiskPeek.ViewModels;

namespace DiskPeek;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly UserSettingsService _settingsService = new();

    // Column indices in FileTable.Columns
    private const int ColName    = 1;
    private const int ColSize    = 2;
    private const int ColItems   = 3;
    private const int ColPercent = 4;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        // Treemap double-click → drill down (async: may trigger deeper scan)
        Treemap.NodeDoubleClicked += node =>
        {
            if (node.IsDirectory) _ = _vm.NavigateToAsync(node);
        };

        // After an in-place deeper scan the treemap's bound object reference doesn't
        // change, so force a full redraw when the ViewModel signals children changed.
        _vm.CurrentNodeChildrenChanged += (_, _) => Treemap.ForceRefresh();

        Loaded += (_, _) => RestoreColumnWidths();
    }

    // ── View tab toggles ──────────────────────────────────────────────────────

    private void TreemapTabClick(object sender, RoutedEventArgs e)
        => _vm.IsTreemapView = true;

    private void TableTabClick(object sender, RoutedEventArgs e)
        => _vm.IsTreemapView = false;

    // ── DataGrid double-click → drill-down ────────────────────────────────────

    private void Table_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileTable.SelectedItem is FileSystemNode node && node.IsDirectory)
            _ = _vm.NavigateToAsync(node);
    }

    // ── Column width persistence ──────────────────────────────────────────────

    private void RestoreColumnWidths()
    {
        var settings = _settingsService.Load();
        double windowWidth = ActualWidth;
        ApplyWidth(ColName,    "Name",    settings, windowWidth);
        ApplyWidth(ColSize,    "Size",    settings, windowWidth);
        ApplyWidth(ColItems,   "Items",   settings, windowWidth);
        ApplyWidth(ColPercent, "Percent", settings, windowWidth);
    }

    private void ApplyWidth(int index, string key, UserSettings settings, double windowWidth)
    {
        if (settings.ColumnWidths.TryGetValue(key, out double pct) && pct > 0)
            FileTable.Columns[index].Width = new DataGridLength(pct / 100.0 * windowWidth);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        SaveColumnWidths();
    }

    private void SaveColumnWidths()
    {
        var settings = _settingsService.Load();
        double windowWidth = ActualWidth;
        TrySaveWidth(ColName,    "Name",    settings, windowWidth);
        TrySaveWidth(ColSize,    "Size",    settings, windowWidth);
        TrySaveWidth(ColItems,   "Items",   settings, windowWidth);
        TrySaveWidth(ColPercent, "Percent", settings, windowWidth);
        _settingsService.Save(settings);
    }

    private void TrySaveWidth(int index, string key, UserSettings settings, double windowWidth)
    {
        double w = FileTable.Columns[index].ActualWidth;
        if (w > 0 && windowWidth > 0)
            settings.ColumnWidths[key] = w / windowWidth * 100.0;
    }
}
