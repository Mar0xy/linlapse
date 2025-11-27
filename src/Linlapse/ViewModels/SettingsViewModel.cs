using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linlapse.Models;
using Linlapse.Services;
using Serilog;
using System.Collections.ObjectModel;

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
    private string? _protonPath;

    [ObservableProperty]
    private bool _useSystemWine;

    [ObservableProperty]
    private bool _useProton;

    [ObservableProperty]
    private string _preferredVoiceLanguage = "en-us";

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

    // Voice language options
    public ObservableCollection<VoiceLanguageOption> VoiceLanguageOptions { get; } = new()
    {
        new VoiceLanguageOption { Code = "en-us", DisplayName = "English" },
        new VoiceLanguageOption { Code = "ja-jp", DisplayName = "Japanese" },
        new VoiceLanguageOption { Code = "zh-cn", DisplayName = "Chinese (Simplified)" },
        new VoiceLanguageOption { Code = "ko-kr", DisplayName = "Korean" }
    };

    [ObservableProperty]
    private ObservableCollection<string> _selectedVoiceLanguages = new();

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
        ProtonPath = settings.ProtonPath;
        UseSystemWine = settings.UseSystemWine;
        UseProton = settings.UseProton;
        PreferredVoiceLanguage = settings.PreferredVoiceLanguage;
        EnableDiscordRpc = settings.EnableDiscordRpc;
        MinimizeToTray = settings.MinimizeToTray;
        StartMinimized = settings.StartMinimized;
        CheckUpdatesOnStartup = settings.CheckUpdatesOnStartup;
        EnableLogging = settings.EnableLogging;
        DownloadSpeedLimit = settings.DownloadSpeedLimit;
        MaxConcurrentDownloads = settings.MaxConcurrentDownloads;
        SelectedTheme = settings.Theme;

        SelectedVoiceLanguages.Clear();
        foreach (var lang in settings.SelectedVoiceLanguages)
        {
            SelectedVoiceLanguages.Add(lang);
        }

        // Update checkboxes in voice language options
        foreach (var option in VoiceLanguageOptions)
        {
            option.IsSelected = SelectedVoiceLanguages.Contains(option.Code);
        }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        _settingsService.UpdateSettings(settings =>
        {
            settings.DefaultGameInstallPath = DefaultGameInstallPath;
            settings.WinePrefixPath = WinePrefixPath;
            settings.WineExecutablePath = WineExecutablePath;
            settings.ProtonPath = ProtonPath;
            settings.UseSystemWine = UseSystemWine;
            settings.UseProton = UseProton;
            settings.PreferredVoiceLanguage = PreferredVoiceLanguage;
            settings.SelectedVoiceLanguages = VoiceLanguageOptions
                .Where(v => v.IsSelected)
                .Select(v => v.Code)
                .ToList();
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
            settings.ProtonPath = null;
            settings.UseSystemWine = true;
            settings.UseProton = false;
            settings.PreferredVoiceLanguage = "en-us";
            settings.SelectedVoiceLanguages = new List<string> { "en-us" };
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

    [RelayCommand]
    private void ToggleVoiceLanguage(string languageCode)
    {
        var option = VoiceLanguageOptions.FirstOrDefault(v => v.Code == languageCode);
        if (option != null)
        {
            option.IsSelected = !option.IsSelected;
        }
    }
}

public partial class VoiceLanguageOption : ObservableObject
{
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    private bool _isSelected;
}
