using DiskPeek.Models;
using DiskPeek.Services;
using DiskPeek.ViewModels;
using System.Windows;

namespace DiskPeek.Views;

public partial class OptionsWindow : Window
{
    public OptionsWindow(SettingsService settingsService, AppSettings currentSettings,
                         UserSettingsService userSettingsService)
    {
        InitializeComponent();
        DataContext = new OptionsViewModel(settingsService, currentSettings, userSettingsService, Close);
    }
}
