using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linlapse.Models;
using Linlapse.Services;
using Serilog;

namespace Linlapse.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly GameService _gameService;
    private readonly GameLauncherService _launcherService;
    private readonly DownloadService _downloadService;
    private readonly InstallationService _installationService;
    private readonly RepairService _repairService;
    private readonly CacheService _cacheService;
    private readonly UpdateService _updateService;
    private readonly GameSettingsService _gameSettingsService;
    private readonly GameDownloadService _gameDownloadService;

    private CancellationTokenSource? _downloadCts;

    [ObservableProperty]
    private GameInfo? _selectedGame;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isGameRunning;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private bool _isRepairing;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _wineVersion = "Checking...";

    [ObservableProperty]
    private UpdateInfo? _availableUpdate;

    [ObservableProperty]
    private CacheInfo? _cacheInfo;

    [ObservableProperty]
    private GameDownloadInfo? _downloadInfo;

    [ObservableProperty]
    private string _downloadSizeText = string.Empty;

    public ObservableCollection<GameInfo> Games { get; } = new();

    public string AppVersion => "1.0.0";
    public string AppTitle => "Linlapse";

    public MainWindowViewModel()
    {
        _settingsService = new SettingsService();
        _gameService = new GameService(_settingsService);
        _downloadService = new DownloadService(_settingsService);
        _installationService = new InstallationService(_settingsService, _downloadService, _gameService);
        _repairService = new RepairService(_gameService, _downloadService);
        _cacheService = new CacheService(_gameService, _settingsService);
        _updateService = new UpdateService(_gameService, _downloadService, _installationService);
        _gameSettingsService = new GameSettingsService(_gameService);
        _launcherService = new GameLauncherService(_settingsService, _gameService);
        _gameDownloadService = new GameDownloadService(_gameService, _downloadService, _installationService, _settingsService);

        // Subscribe to events
        _gameService.GamesListChanged += (_, _) => RefreshGamesCollection();
        _gameService.GameStateChanged += (_, game) => UpdateGameInCollection(game);
        _launcherService.GameStarted += (_, _) => IsGameRunning = true;
        _launcherService.GameStopped += (_, _) => IsGameRunning = false;
        
        _downloadService.DownloadProgressChanged += OnDownloadProgress;
        _installationService.InstallProgressChanged += OnInstallProgress;
        _repairService.RepairProgressChanged += OnRepairProgress;
        _updateService.UpdateProgressChanged += OnUpdateProgress;

        // Load initial data
        RefreshGamesCollection();
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Initializing...";

            // Check Wine installation
            var wineInfo = await _launcherService.GetWineInfoAsync();
            WineVersion = wineInfo.IsInstalled 
                ? $"Wine: {wineInfo.Version.Trim()}" 
                : "Wine not found - Please install Wine";

            // Scan for installed games
            await _gameService.ScanForInstalledGamesAsync();

            // Check for updates
            StatusMessage = "Checking for updates...";
            await CheckForUpdatesAsync();

            StatusMessage = "Ready";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Initialization error");
            StatusMessage = "Initialization failed";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void RefreshGamesCollection()
    {
        Games.Clear();
        foreach (var game in _gameService.Games)
        {
            Games.Add(game);
        }

        if (SelectedGame == null && Games.Count > 0)
        {
            SelectedGame = Games[0];
        }
    }

    private void UpdateGameInCollection(GameInfo updatedGame)
    {
        var existingGame = Games.FirstOrDefault(g => g.Id == updatedGame.Id);
        if (existingGame != null)
        {
            var index = Games.IndexOf(existingGame);
            Games[index] = updatedGame;
            
            if (SelectedGame?.Id == updatedGame.Id)
            {
                SelectedGame = updatedGame;
            }
        }
    }

    private void OnDownloadProgress(object? sender, DownloadProgress progress)
    {
        ProgressPercent = progress.PercentComplete;
        var speedMb = progress.SpeedBytesPerSecond / 1024 / 1024;
        ProgressText = $"Downloading: {progress.PercentComplete:F1}% ({speedMb:F1} MB/s)";
    }

    private void OnInstallProgress(object? sender, InstallProgress progress)
    {
        ProgressPercent = progress.PercentComplete;
        ProgressText = $"Installing: {progress.ProcessedFiles}/{progress.TotalFiles} files";
    }

    private void OnRepairProgress(object? sender, RepairProgress progress)
    {
        ProgressPercent = progress.PercentComplete;
        ProgressText = $"Verifying: {progress.ProcessedFiles}/{progress.TotalFiles} files";
    }

    private void OnUpdateProgress(object? sender, UpdateProgress progress)
    {
        ProgressPercent = progress.PercentComplete;
        ProgressText = progress.State switch
        {
            UpdateState.DownloadingPatch => $"Downloading patch: {progress.PercentComplete:F1}%",
            UpdateState.DownloadingFull => $"Downloading update: {progress.PercentComplete:F1}%",
            UpdateState.ApplyingPatch => "Applying patch...",
            UpdateState.Extracting => $"Extracting: {progress.ProcessedFiles}/{progress.TotalFiles} files",
            _ => $"Updating: {progress.PercentComplete:F1}%"
        };
    }

    [RelayCommand]
    private async Task LaunchGameAsync()
    {
        if (SelectedGame == null) return;

        if (SelectedGame.State == GameState.NotInstalled)
        {
            StatusMessage = "Game not installed";
            return;
        }

        if (SelectedGame.State == GameState.Running)
        {
            StatusMessage = "Game is already running";
            return;
        }

        if (SelectedGame.State == GameState.NeedsUpdate)
        {
            StatusMessage = "Update required before playing";
            return;
        }

        StatusMessage = $"Launching {SelectedGame.DisplayName}...";
        var success = await _launcherService.LaunchGameAsync(SelectedGame.Id);
        StatusMessage = success ? $"{SelectedGame.DisplayName} is running" : "Failed to launch game";
    }

    [RelayCommand]
    private void StopGame()
    {
        if (SelectedGame == null) return;
        _launcherService.StopGame(SelectedGame.Id);
        StatusMessage = "Game stopped";
    }

    [RelayCommand]
    private async Task RefreshGamesAsync()
    {
        IsLoading = true;
        StatusMessage = "Scanning for games...";
        await _gameService.ScanForInstalledGamesAsync();
        StatusMessage = "Ready";
        IsLoading = false;
    }

    [RelayCommand]
    private async Task RepairGameAsync()
    {
        if (SelectedGame == null || !SelectedGame.IsInstalled) return;

        try
        {
            IsRepairing = true;
            StatusMessage = $"Verifying {SelectedGame.DisplayName} files...";

            var progress = new Progress<RepairProgress>(p =>
            {
                ProgressPercent = p.PercentComplete;
                ProgressText = $"Verifying: {p.CurrentFile}";
            });

            var results = await _repairService.VerifyGameFilesAsync(SelectedGame.Id, progress);
            var brokenFiles = results.Where(r => !r.IsValid).ToList();

            if (brokenFiles.Count == 0)
            {
                StatusMessage = "All files verified - no issues found!";
            }
            else
            {
                StatusMessage = $"Found {brokenFiles.Count} files with issues. Repair from download not available without game server URL.";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Repair failed");
            StatusMessage = "Repair failed";
        }
        finally
        {
            IsRepairing = false;
            ProgressPercent = 0;
            ProgressText = string.Empty;
        }
    }

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        if (SelectedGame == null || !SelectedGame.IsInstalled) return;

        try
        {
            IsLoading = true;
            StatusMessage = $"Clearing cache for {SelectedGame.DisplayName}...";

            var success = await _cacheService.ClearAllCachesAsync(SelectedGame.Id);
            
            StatusMessage = success 
                ? "Cache cleared successfully!" 
                : "Failed to clear cache";

            // Refresh cache info
            await LoadCacheInfoAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Cache clear failed");
            StatusMessage = "Failed to clear cache";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (SelectedGame == null || !SelectedGame.IsInstalled) return;

        try
        {
            StatusMessage = $"Checking for updates...";
            AvailableUpdate = await _updateService.CheckForUpdatesAsync(SelectedGame.Id);
            
            if (AvailableUpdate?.HasUpdate == true)
            {
                StatusMessage = $"Update available: {AvailableUpdate.LatestVersion}";
            }
            else
            {
                StatusMessage = "Game is up to date";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Update check failed");
            StatusMessage = "Failed to check for updates";
        }
    }

    [RelayCommand]
    private async Task ApplyUpdateAsync()
    {
        if (SelectedGame == null || AvailableUpdate == null) return;

        try
        {
            IsDownloading = true;
            StatusMessage = $"Downloading update for {SelectedGame.DisplayName}...";

            var progress = new Progress<UpdateProgress>(p =>
            {
                ProgressPercent = p.PercentComplete;
                StatusMessage = p.State switch
                {
                    UpdateState.DownloadingPatch => "Downloading delta patch...",
                    UpdateState.DownloadingFull => "Downloading full update...",
                    UpdateState.ApplyingPatch => "Applying patch...",
                    UpdateState.Extracting => "Extracting files...",
                    _ => "Updating..."
                };
            });

            var success = await _updateService.ApplyUpdateAsync(SelectedGame.Id, progress);
            
            StatusMessage = success 
                ? "Update completed successfully!" 
                : "Update failed";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Update failed");
            StatusMessage = "Update failed";
        }
        finally
        {
            IsDownloading = false;
            ProgressPercent = 0;
            AvailableUpdate = null;
        }
    }

    [RelayCommand]
    private async Task DownloadPreloadAsync()
    {
        if (SelectedGame == null || !SelectedGame.IsInstalled) return;

        try
        {
            IsDownloading = true;
            StatusMessage = $"Downloading preload for {SelectedGame.DisplayName}...";

            var progress = new Progress<UpdateProgress>(p =>
            {
                ProgressPercent = p.PercentComplete;
            });

            var success = await _updateService.DownloadPreloadAsync(SelectedGame.Id, progress);
            
            StatusMessage = success 
                ? "Preload completed!" 
                : "No preload available";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Preload failed");
            StatusMessage = "Preload failed";
        }
        finally
        {
            IsDownloading = false;
            ProgressPercent = 0;
        }
    }

    [RelayCommand]
    private async Task InstallGameAsync()
    {
        if (SelectedGame == null || SelectedGame.IsInstalled) return;

        try
        {
            _downloadCts = new CancellationTokenSource();
            IsDownloading = true;
            StatusMessage = $"Fetching download information for {SelectedGame.DisplayName}...";

            // First, get download info to show size
            var downloadInfo = await _gameDownloadService.GetGameDownloadInfoAsync(SelectedGame.Id, _downloadCts.Token);
            if (downloadInfo == null)
            {
                StatusMessage = "Failed to get download information. Game may not be available for download.";
                IsDownloading = false;
                return;
            }

            DownloadInfo = downloadInfo;
            var sizeMb = downloadInfo.TotalSize / 1024.0 / 1024.0;
            var sizeGb = sizeMb / 1024.0;
            DownloadSizeText = sizeGb >= 1 ? $"{sizeGb:F2} GB" : $"{sizeMb:F0} MB";
            
            StatusMessage = $"Downloading {SelectedGame.DisplayName} ({DownloadSizeText})...";

            var progress = new Progress<GameDownloadProgress>(p =>
            {
                ProgressPercent = p.PercentComplete;
                var speedMb = p.SpeedBytesPerSecond / 1024.0 / 1024.0;
                
                ProgressText = p.State switch
                {
                    GameDownloadState.FetchingInfo => "Fetching download information...",
                    GameDownloadState.Downloading => $"Downloading: {p.PercentComplete:F1}% ({speedMb:F1} MB/s)",
                    GameDownloadState.DownloadingVoicePacks => $"Downloading voice packs: {p.PercentComplete:F1}%",
                    GameDownloadState.Verifying => "Verifying downloaded files...",
                    GameDownloadState.Extracting => $"Extracting: {p.ExtractedFiles}/{p.TotalFiles} files",
                    GameDownloadState.Cleanup => "Cleaning up...",
                    GameDownloadState.Completed => "Installation complete!",
                    GameDownloadState.Failed => $"Failed: {p.ErrorMessage}",
                    _ => $"{p.State}"
                };

                StatusMessage = ProgressText;
            });

            var success = await _gameDownloadService.DownloadAndInstallGameAsync(
                SelectedGame.Id, 
                progress: progress, 
                cancellationToken: _downloadCts.Token);

            if (success)
            {
                StatusMessage = $"{SelectedGame.DisplayName} installed successfully!";
                ProgressText = "Installation complete!";
            }
            else if (_downloadCts.IsCancellationRequested)
            {
                StatusMessage = "Installation cancelled";
            }
            else
            {
                StatusMessage = "Installation failed";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Installation cancelled";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Install failed");
            StatusMessage = $"Installation failed: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
            ProgressPercent = 0;
            ProgressText = string.Empty;
            DownloadInfo = null;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    [RelayCommand]
    private void CancelDownload()
    {
        _downloadCts?.Cancel();
        StatusMessage = "Cancelling download...";
    }

    [RelayCommand]
    private async Task GetDownloadInfoAsync()
    {
        if (SelectedGame == null) return;

        try
        {
            IsLoading = true;
            StatusMessage = $"Fetching download info for {SelectedGame.DisplayName}...";

            var info = await _gameDownloadService.GetGameDownloadInfoAsync(SelectedGame.Id);
            if (info != null)
            {
                DownloadInfo = info;
                var sizeMb = info.TotalSize / 1024.0 / 1024.0;
                var sizeGb = sizeMb / 1024.0;
                DownloadSizeText = sizeGb >= 1 ? $"{sizeGb:F2} GB" : $"{sizeMb:F0} MB";
                StatusMessage = $"Version {info.Version} - Download size: {DownloadSizeText}";
            }
            else
            {
                StatusMessage = "Could not retrieve download information";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get download info");
            StatusMessage = "Failed to get download information";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoadCacheInfoAsync()
    {
        if (SelectedGame == null || !SelectedGame.IsInstalled) return;

        try
        {
            CacheInfo = await _cacheService.GetCacheInfoAsync(SelectedGame.Id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load cache info");
        }
    }

    [RelayCommand]
    private void SelectGame(GameInfo? game)
    {
        if (game != null)
        {
            SelectedGame = game;
        }
    }

    partial void OnSelectedGameChanged(GameInfo? value)
    {
        if (value != null)
        {
            IsGameRunning = _launcherService.IsGameRunning(value.Id);
            AvailableUpdate = null;
            CacheInfo = null;
            DownloadInfo = null;
            DownloadSizeText = string.Empty;
            
            if (value.IsInstalled)
            {
                // Load cache info in background
                _ = LoadCacheInfoAsync();
                
                // Check for updates in background
                _ = CheckForUpdatesAsync();
            }
            else
            {
                // Get download info for non-installed games
                _ = GetDownloadInfoAsync();
            }
        }
    }
}
