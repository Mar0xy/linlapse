using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linlapse.Models;
using Linlapse.Services;
using Serilog;

namespace Linlapse.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;

    [ObservableProperty]
    private string? _defaultGameInstallPath;

    [ObservableProperty]
    private string? _winePrefixPath;

    [ObservableProperty]
    private string? _wineExecutablePath;

    [ObservableProperty]
    private bool _useSystemWine;

    [ObservableProperty]
    private bool _enableDiscordRpc;

    [ObservableProperty]
    private bool _minimizeToTray;

    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private bool _checkUpdatesOnStartup;

    [ObservableProperty]
    private bool _enableLogging;

    [ObservableProperty]
    private int _downloadSpeedLimit;

    [ObservableProperty]
    private int _maxConcurrentDownloads;

    [ObservableProperty]
    private ThemeMode _selectedTheme;

    public SettingsViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService;
        LoadSettings();
    }

    private void LoadSettings()
    {
        var settings = _settingsService.Settings;
        DefaultGameInstallPath = settings.DefaultGameInstallPath;
        WinePrefixPath = settings.WinePrefixPath;
        WineExecutablePath = settings.WineExecutablePath;
        UseSystemWine = settings.UseSystemWine;
        EnableDiscordRpc = settings.EnableDiscordRpc;
        MinimizeToTray = settings.MinimizeToTray;
        StartMinimized = settings.StartMinimized;
        CheckUpdatesOnStartup = settings.CheckUpdatesOnStartup;
        EnableLogging = settings.EnableLogging;
        DownloadSpeedLimit = settings.DownloadSpeedLimit;
        MaxConcurrentDownloads = settings.MaxConcurrentDownloads;
        SelectedTheme = settings.Theme;
    }

    [RelayCommand]
    private void SaveSettings()
    {
        _settingsService.UpdateSettings(settings =>
        {
            settings.DefaultGameInstallPath = DefaultGameInstallPath;
            settings.WinePrefixPath = WinePrefixPath;
            settings.WineExecutablePath = WineExecutablePath;
            settings.UseSystemWine = UseSystemWine;
            settings.EnableDiscordRpc = EnableDiscordRpc;
            settings.MinimizeToTray = MinimizeToTray;
            settings.StartMinimized = StartMinimized;
            settings.CheckUpdatesOnStartup = CheckUpdatesOnStartup;
            settings.EnableLogging = EnableLogging;
            settings.DownloadSpeedLimit = DownloadSpeedLimit;
            settings.MaxConcurrentDownloads = MaxConcurrentDownloads;
            settings.Theme = SelectedTheme;
        });

        Log.Information("Settings saved");
    }

    [RelayCommand]
    private void ResetSettings()
    {
        _settingsService.UpdateSettings(settings =>
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            settings.DefaultGameInstallPath = Path.Combine(home, "Games");
            settings.WinePrefixPath = Path.Combine(home, ".local", "share", "linlapse", "wine-prefix");
            settings.WineExecutablePath = null;
            settings.UseSystemWine = true;
            settings.EnableDiscordRpc = true;
            settings.MinimizeToTray = true;
            settings.StartMinimized = false;
            settings.CheckUpdatesOnStartup = true;
            settings.EnableLogging = true;
            settings.DownloadSpeedLimit = 0;
            settings.MaxConcurrentDownloads = 4;
            settings.Theme = ThemeMode.System;
        });

        LoadSettings();
        Log.Information("Settings reset to defaults");
    }
}
