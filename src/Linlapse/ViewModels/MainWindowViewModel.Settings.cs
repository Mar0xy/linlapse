using CommunityToolkit.Mvvm.Input;
using Linlapse.Models;
using Linlapse.Services;
using Serilog;

namespace Linlapse.ViewModels;

/// <summary>
/// Settings management for MainWindowViewModel
/// </summary>
public partial class MainWindowViewModel
{
    [RelayCommand]
    private void ToggleSettings()
    {
        if (!IsSettingsVisible)
        {
            // Load current settings when opening
            LoadSettingsFromService();
        }
        IsSettingsVisible = !IsSettingsVisible;
    }

    private void LoadSettingsFromService()
    {
        var settings = _settingsService.Settings;
        UseSystemWine = settings.UseSystemWine;
        WineExecutablePath = settings.WineExecutablePath;
        WinePrefixPath = settings.WinePrefixPath;
        UseProton = settings.UseProton;
        ProtonPath = settings.ProtonPath;
        DefaultGameInstallPath = settings.DefaultGameInstallPath;
        MaxConcurrentDownloads = settings.MaxConcurrentDownloads;
        CheckUpdatesOnStartup = settings.CheckUpdatesOnStartup;
        EnableLogging = settings.EnableLogging;

        // Load voice language selections
        foreach (var option in VoiceLanguageOptions)
        {
            option.IsSelected = settings.SelectedVoiceLanguages.Contains(option.Code);
        }
        
        // Load installed runners and set selections based on current paths
        LoadInstalledRunners();
    }

    private void LoadInstalledRunners()
    {
        InstalledWineRunners.Clear();
        InstalledProtonRunners.Clear();
        
        var installedRunners = _wineRunnerService.GetInstalledRunners();
        
        foreach (var runner in installedRunners)
        {
            if (runner.Type == WineRunnerType.Wine)
            {
                InstalledWineRunners.Add(runner);
            }
            else
            {
                InstalledProtonRunners.Add(runner);
            }
        }
        
        // Set selected runners based on current paths
        var settings = _settingsService.Settings;
        
        if (!string.IsNullOrEmpty(settings.WineExecutablePath))
        {
            SelectedGlobalWineRunner = InstalledWineRunners.FirstOrDefault(r => 
                settings.WineExecutablePath.StartsWith(r.InstallPath, StringComparison.OrdinalIgnoreCase) ||
                r.ExecutablePath.Equals(settings.WineExecutablePath, StringComparison.OrdinalIgnoreCase));
        }
        
        if (!string.IsNullOrEmpty(settings.ProtonPath))
        {
            SelectedGlobalProtonRunner = InstalledProtonRunners.FirstOrDefault(r => 
                settings.ProtonPath.StartsWith(r.InstallPath, StringComparison.OrdinalIgnoreCase) ||
                r.InstallPath.Equals(settings.ProtonPath, StringComparison.OrdinalIgnoreCase));
        }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        _settingsService.UpdateSettings(settings =>
        {
            settings.UseSystemWine = UseSystemWine;
            settings.WineExecutablePath = WineExecutablePath;
            settings.WinePrefixPath = WinePrefixPath;
            settings.UseProton = UseProton;
            settings.ProtonPath = ProtonPath;
            settings.DefaultGameInstallPath = DefaultGameInstallPath;
            settings.MaxConcurrentDownloads = MaxConcurrentDownloads;
            settings.CheckUpdatesOnStartup = CheckUpdatesOnStartup;
            settings.EnableLogging = EnableLogging;
            settings.SelectedVoiceLanguages = VoiceLanguageOptions
                .Where(v => v.IsSelected)
                .Select(v => v.Code)
                .ToList();
        });

        StatusMessage = "Settings saved successfully";
        Log.Information("Settings saved");
    }

    [RelayCommand]
    private void ResetSettings()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        
        _settingsService.UpdateSettings(settings =>
        {
            settings.DefaultGameInstallPath = Path.Combine(home, "Games");
            settings.WinePrefixPath = Path.Combine(home, ".local", "share", "linlapse", "wine-prefix");
            settings.WineExecutablePath = null;
            settings.ProtonPath = null;
            settings.UseSystemWine = true;
            settings.UseProton = false;
            settings.SelectedVoiceLanguages = new List<string> { "en-us" };
            settings.MaxConcurrentDownloads = 4;
            settings.CheckUpdatesOnStartup = true;
            settings.EnableLogging = true;
        });

        LoadSettingsFromService();
        StatusMessage = "Settings reset to defaults";
        Log.Information("Settings reset to defaults");
    }

    [RelayCommand]
    private async Task DownloadJadeiteAsync()
    {
        if (IsDownloadingJadeite) return;

        try
        {
            IsDownloadingJadeite = true;
            StatusMessage = "Downloading Jadeite...";

            var progress = new Progress<double>(p =>
            {
                ProgressPercent = p;
                ProgressText = $"Downloading Jadeite: {p:F1}%";
            });

            var success = await _launcherService.DownloadJadeiteAsync(progress);

            if (success)
            {
                IsJadeiteAvailable = true;
                StatusMessage = "Jadeite downloaded successfully! HSR and HI3 can now be launched.";
            }
            else
            {
                StatusMessage = "Failed to download Jadeite";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download Jadeite");
            StatusMessage = $"Failed to download Jadeite: {ex.Message}";
        }
        finally
        {
            IsDownloadingJadeite = false;
            ProgressPercent = 0;
            ProgressText = string.Empty;
        }
    }

    [RelayCommand]
    private void CloseGameSettings()
    {
        if (GameSettingsViewModel != null)
        {
            GameSettingsViewModel.SettingsSaved -= OnGameSettingsSaved;
            GameSettingsViewModel.SettingsClosed -= OnGameSettingsClosed;
            GameSettingsViewModel = null;
        }
        IsGameSettingsVisible = false;
    }

    [RelayCommand]
    private void OpenWineRunnerDialog()
    {
        // Create the wine runner dialog view model
        WineRunnerDialogViewModel = new WineRunnerDialogViewModel(_wineRunnerService);
        WineRunnerDialogViewModel.DialogClosed += OnWineRunnerDialogClosed;
        WineRunnerDialogViewModel.RunnersUpdated += OnRunnersUpdated;
        
        IsWineRunnerDialogVisible = true;
        StatusMessage = "Download custom Wine/Proton runners";
    }

    [RelayCommand]
    private void CloseWineRunnerDialog()
    {
        if (WineRunnerDialogViewModel != null)
        {
            WineRunnerDialogViewModel.DialogClosed -= OnWineRunnerDialogClosed;
            WineRunnerDialogViewModel.RunnersUpdated -= OnRunnersUpdated;
            WineRunnerDialogViewModel.Cleanup();
            WineRunnerDialogViewModel = null;
        }
        IsWineRunnerDialogVisible = false;
    }

}
