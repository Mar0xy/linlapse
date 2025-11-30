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
    private readonly BackgroundService _backgroundService;
    private readonly WineRunnerService _wineRunnerService;

    // Dictionary to track cancellation tokens for each downloading game
    private readonly Dictionary<string, CancellationTokenSource> _downloadCancellationTokens = new();
    // Dictionary to track download progress for each downloading game
    private readonly Dictionary<string, (double ProgressPercent, string ProgressText)> _downloadProgressByGame = new();
    // Dictionary to track pause state for each downloading game
    private readonly Dictionary<string, bool> _pauseStateByGame = new();
    // Lock object to synchronize progress updates across multiple downloads
    private readonly object _progressUpdateLock = new();
    private bool _isRestoringSelection;

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
    private string? _downloadingGameId;

    [ObservableProperty]
    private bool _isPaused;

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
    private bool _isPreloadAvailable;

    [ObservableProperty]
    private CacheInfo? _cacheInfo;

    [ObservableProperty]
    private GameDownloadInfo? _downloadInfo;

    [ObservableProperty]
    private string _downloadSizeText = string.Empty;

    [ObservableProperty]
    private string? _backgroundSource;

    [ObservableProperty]
    private bool _isVideoBackground;

    [ObservableProperty]
    private string _backgroundColor = "#1a1a2e";

    [ObservableProperty]
    private string? _themeOverlaySource;

    [ObservableProperty]
    private bool _isSettingsVisible;

    // Settings properties (delegated from SettingsViewModel)
    [ObservableProperty]
    private bool _useSystemWine;

    [ObservableProperty]
    private string? _wineExecutablePath;

    [ObservableProperty]
    private string? _winePrefixPath;

    [ObservableProperty]
    private bool _useProton;

    [ObservableProperty]
    private string? _protonPath;

    [ObservableProperty]
    private string? _defaultGameInstallPath;

    [ObservableProperty]
    private int _maxConcurrentDownloads = 4;

    [ObservableProperty]
    private bool _checkUpdatesOnStartup = true;

    [ObservableProperty]
    private bool _enableLogging = true;

    [ObservableProperty]
    private bool _isJadeiteAvailable;

    [ObservableProperty]
    private bool _isDownloadingJadeite;
    
    // Game Settings Dialog
    [ObservableProperty]
    private bool _isGameSettingsVisible;
    
    [ObservableProperty]
    private GameSettingsViewModel? _gameSettingsViewModel;
    
    // Wine Runner Dialog
    [ObservableProperty]
    private bool _isWineRunnerDialogVisible;
    
    [ObservableProperty]
    private WineRunnerDialogViewModel? _wineRunnerDialogViewModel;

    // Voice language options for settings
    public ObservableCollection<VoiceLanguageOption> VoiceLanguageOptions { get; } = new()
    {
        new VoiceLanguageOption { Code = "en-us", DisplayName = "English", IsSelected = true },
        new VoiceLanguageOption { Code = "ja-jp", DisplayName = "Japanese" },
        new VoiceLanguageOption { Code = "zh-cn", DisplayName = "Chinese (Simplified)" },
        new VoiceLanguageOption { Code = "ko-kr", DisplayName = "Korean" }
    };

    // Games collection based on per-game region preferences
    public ObservableCollection<GameInfo> Games { get; } = new();

    public string AppVersion => "1.0.0";
    public string AppTitle => "Linlapse";

    /// <summary>
    /// Returns true if the currently selected game is being downloaded
    /// </summary>
    public bool IsSelectedGameDownloading => SelectedGame?.IsDownloading == true;

    /// <summary>
    /// Returns true if any game is currently being downloaded
    /// </summary>
    public bool IsAnyGameDownloading => _downloadCancellationTokens.Count > 0;

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
        _backgroundService = new BackgroundService(_gameService, _settingsService);
        _wineRunnerService = new WineRunnerService(_settingsService, _downloadService);

        // Subscribe to events
        _gameService.GamesListChanged += (_, _) => RefreshGamesCollection();
        _gameService.GameStateChanged += (_, game) => UpdateGameInCollection(game);
        _launcherService.GameStarted += (_, game) => OnGameStarted(game);
        _launcherService.GameStopped += (_, game) => OnGameStopped(game);

        // Note: We don't subscribe to _downloadService.DownloadProgressChanged because game downloads
        // use Progress<GameDownloadProgress> callbacks which provide per-game progress tracking.
        // Subscribing to the global event would cause progress bar glitching when multiple downloads are active.
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

            // Check Wine/Proton installation
            var wineInfo = await _launcherService.GetWineInfoAsync();
            if (wineInfo.IsInstalled)
            {
                if (wineInfo.IsProton)
                {
                    WineVersion = $"Proton: {wineInfo.Version.Trim()}";
                }
                else
                {
                    WineVersion = $"Wine: {wineInfo.Version.Trim()}";
                }
            }
            else
            {
                // Check if Proton is configured but not found
                var settings = _settingsService.Settings;
                if (settings.UseProton && !string.IsNullOrEmpty(settings.ProtonPath))
                {
                    WineVersion = $"Proton not found at: {settings.ProtonPath}";
                }
                else
                {
                    WineVersion = "Wine not found - Please install Wine";
                }
            }

            // Scan for installed games
            await _gameService.ScanForInstalledGamesAsync();

            // Check Jadeite availability
            IsJadeiteAvailable = _launcherService.IsJadeiteAvailable();

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
        // Remember the currently selected game type (not ID, since ID changes with region)
        var selectedGameType = SelectedGame?.GameType;

        Games.Clear();
        
        // Get saved region preferences per game type
        var settings = _settingsService.Settings;
        var gameTypes = new[] { GameType.HonkaiImpact3rd, GameType.GenshinImpact, GameType.HonkaiStarRail, GameType.ZenlessZoneZero };
        
        foreach (var gameType in gameTypes)
        {
            // Use the saved region preference for this game type, default to Global
            GameRegion regionToUse = GameRegion.Global;
            if (settings.SelectedRegionPerGame.TryGetValue(gameType.ToString(), out var savedRegion) &&
                Enum.TryParse<GameRegion>(savedRegion, out var parsedRegion))
            {
                regionToUse = parsedRegion;
            }
            
            var game = _gameService.GetGameByTypeAndRegion(gameType, regionToUse);
            if (game != null)
            {
                Games.Add(game);
                // Load icon in background
                _ = LoadGameIconAsync(game);
            }
        }

        // Restore selection by finding the game with the same type
        if (selectedGameType != null)
        {
            var gameToSelect = Games.FirstOrDefault(g => g.GameType == selectedGameType);
            if (gameToSelect != null)
            {
                SelectedGame = gameToSelect;
                return;
            }
        }

        // If no previous selection or game not found, select first game
        if (SelectedGame == null && Games.Count > 0)
        {
            SelectedGame = Games[0];
        }
    }

    private async Task LoadGameIconAsync(GameInfo game)
    {
        try
        {
            var iconPath = await _backgroundService.GetCachedGameIconAsync(game.Id);
            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            {
                // Update the game's logo path - INotifyPropertyChanged handles UI updates
                game.LogoImagePath = iconPath;
                Log.Debug("Loaded icon for {GameId}: {Path}", game.Id, iconPath);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load icon for {GameId}", game.Id);
        }
    }

    private void UpdateGameInCollection(GameInfo updatedGame)
    {
        var existingGame = Games.FirstOrDefault(g => g.Id == updatedGame.Id);
        if (existingGame != null)
        {
            var index = Games.IndexOf(existingGame);
            var wasSelected = SelectedGame?.Id == updatedGame.Id;
            
            // Set flag BEFORE replacing the item to prevent selection change side effects
            // When we replace the item, the UI may fire selection change events
            _isRestoringSelection = true;
            try
            {
                Games[index] = updatedGame;

                // Restore selection if this was the selected game
                if (wasSelected)
                {
                    SelectedGame = updatedGame;
                }
            }
            finally
            {
                _isRestoringSelection = false;
            }
        }
    }

    private void OnGameStarted(GameInfo game)
    {
        // Only update IsGameRunning if the started game is the currently selected one
        if (SelectedGame?.Id == game.Id)
        {
            IsGameRunning = true;
        }
    }

    private void OnGameStopped(GameInfo game)
    {
        // Only update IsGameRunning if the stopped game is the currently selected one
        if (SelectedGame?.Id == game.Id)
        {
            IsGameRunning = false;
        }
    }

    private void OnInstallProgress(object? sender, InstallProgress progress)
    {
        ProgressPercent = progress.PercentComplete;
        
        // For extraction, just show "Extracting..." without file counts since 7z progress is unreliable
        if (progress.State == InstallState.Extracting)
        {
            ProgressText = "Extracting...";
        }
        else
        {
            ProgressText = $"Installing: {progress.ProcessedFiles}/{progress.TotalFiles} files";
        }
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
            IsPreloadAvailable = !string.IsNullOrEmpty(AvailableUpdate?.PreloadVersion);

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
            IsPreloadAvailable = false;
        }
    }

    [RelayCommand]
    private async Task ApplyUpdateAsync()
    {
        if (SelectedGame == null || AvailableUpdate == null) return;

        // Prevent updating if already downloading
        if (SelectedGame.IsDownloading)
        {
            StatusMessage = $"{SelectedGame.DisplayName} is already being downloaded";
            return;
        }

        var gameId = SelectedGame.Id;
        var gameName = SelectedGame.DisplayName;
        var game = SelectedGame;

        try
        {
            var cts = new CancellationTokenSource();
            _downloadCancellationTokens[gameId] = cts;
            
            game.IsDownloading = true;
            IsDownloading = true;
            DownloadingGameId = gameId;
            OnPropertyChanged(nameof(IsSelectedGameDownloading));
            OnPropertyChanged(nameof(IsAnyGameDownloading));
            
            StatusMessage = $"Downloading update for {gameName}...";

            var progress = new Progress<UpdateProgress>(p =>
            {
                // Use lock to prevent race conditions when multiple downloads update progress
                lock (_progressUpdateLock)
                {
                    var progressText = p.State switch
                    {
                        UpdateState.DownloadingPatch => "Downloading delta patch...",
                        UpdateState.DownloadingFull => "Downloading full update...",
                        UpdateState.ApplyingPatch => "Applying patch...",
                        UpdateState.Extracting => "Extracting files...",
                        _ => "Updating..."
                    };

                    // Store progress for this game
                    _downloadProgressByGame[gameId] = (p.PercentComplete, progressText);

                    // Capture selected game ID once inside the lock
                    var currentSelectedGameId = SelectedGame?.Id;
                    
                    // Update UI only if this game is the currently selected game
                    if (currentSelectedGameId == gameId)
                    {
                        ProgressPercent = p.PercentComplete;
                        ProgressText = progressText;
                        StatusMessage = progressText;
                    }
                }
            });

            var success = await _updateService.ApplyUpdateAsync(gameId, progress);

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
            game.IsDownloading = false;
            _downloadProgressByGame.Remove(gameId);
            _pauseStateByGame.Remove(gameId);
            
            if (_downloadCancellationTokens.TryGetValue(gameId, out var oldCts))
            {
                oldCts.Dispose();
                _downloadCancellationTokens.Remove(gameId);
            }
            
            if (_downloadCancellationTokens.Count == 0)
            {
                IsDownloading = false;
                DownloadingGameId = null;
            }
            
            if (SelectedGame?.Id == gameId)
            {
                IsPaused = false;
                ProgressPercent = 0;
                ProgressText = string.Empty;
            }
            AvailableUpdate = null;
            IsPreloadAvailable = false;
            OnPropertyChanged(nameof(IsSelectedGameDownloading));
            OnPropertyChanged(nameof(IsAnyGameDownloading));
        }
    }

    [RelayCommand]
    private async Task DownloadPreloadAsync()
    {
        if (SelectedGame == null || !SelectedGame.IsInstalled) return;

        // Prevent preloading if already downloading
        if (SelectedGame.IsDownloading)
        {
            StatusMessage = $"{SelectedGame.DisplayName} is already being downloaded";
            return;
        }

        var gameId = SelectedGame.Id;
        var gameName = SelectedGame.DisplayName;
        var game = SelectedGame;

        try
        {
            var cts = new CancellationTokenSource();
            _downloadCancellationTokens[gameId] = cts;
            
            game.IsDownloading = true;
            IsDownloading = true;
            DownloadingGameId = gameId;
            OnPropertyChanged(nameof(IsSelectedGameDownloading));
            OnPropertyChanged(nameof(IsAnyGameDownloading));
            
            StatusMessage = $"Downloading preload for {gameName}...";

            var progress = new Progress<UpdateProgress>(p =>
            {
                // Use lock to prevent race conditions when multiple downloads update progress
                lock (_progressUpdateLock)
                {
                    var progressText = $"Preloading: {p.PercentComplete:F1}%";

                    // Store progress for this game
                    _downloadProgressByGame[gameId] = (p.PercentComplete, progressText);

                    // Capture selected game ID once inside the lock
                    var currentSelectedGameId = SelectedGame?.Id;
                    
                    // Update UI only if this game is the currently selected game
                    if (currentSelectedGameId == gameId)
                    {
                        ProgressPercent = p.PercentComplete;
                        ProgressText = progressText;
                    }
                }
            });

            var success = await _updateService.DownloadPreloadAsync(gameId, progress);

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
            game.IsDownloading = false;
            _downloadProgressByGame.Remove(gameId);
            _pauseStateByGame.Remove(gameId);
            
            if (_downloadCancellationTokens.TryGetValue(gameId, out var oldCts))
            {
                oldCts.Dispose();
                _downloadCancellationTokens.Remove(gameId);
            }
            
            if (_downloadCancellationTokens.Count == 0)
            {
                IsDownloading = false;
                DownloadingGameId = null;
            }
            
            if (SelectedGame?.Id == gameId)
            {
                IsPaused = false;
                ProgressPercent = 0;
                ProgressText = string.Empty;
            }
            OnPropertyChanged(nameof(IsSelectedGameDownloading));
            OnPropertyChanged(nameof(IsAnyGameDownloading));
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task InstallGameAsync()
    {
        if (SelectedGame == null || SelectedGame.IsInstalled) return;

        // Prevent downloading the same game twice
        if (SelectedGame.IsDownloading)
        {
            StatusMessage = $"{SelectedGame.DisplayName} is already being downloaded";
            return;
        }

        var gameId = SelectedGame.Id;
        var gameName = SelectedGame.DisplayName;
        var game = SelectedGame;

        try
        {
            var cts = new CancellationTokenSource();
            _downloadCancellationTokens[gameId] = cts;
            
            // Mark game as downloading
            game.IsDownloading = true;
            IsDownloading = true;
            DownloadingGameId = gameId;
            OnPropertyChanged(nameof(IsSelectedGameDownloading));
            OnPropertyChanged(nameof(IsAnyGameDownloading));
            
            StatusMessage = $"Fetching download information for {gameName}...";

            // First, get download info to show size
            var downloadInfo = await _gameDownloadService.GetGameDownloadInfoAsync(gameId, cts.Token);
            if (downloadInfo == null)
            {
                StatusMessage = "Failed to get download information. Game may not be available for download.";
                return;
            }

            // Only update DownloadInfo for the currently selected game
            if (SelectedGame?.Id == gameId)
            {
                DownloadInfo = downloadInfo;
                var sizeMb = downloadInfo.TotalSize / 1024.0 / 1024.0;
                var sizeGb = sizeMb / 1024.0;
                DownloadSizeText = sizeGb >= 1 ? $"{sizeGb:F2} GB" : $"{sizeMb:F0} MB";
            }

            StatusMessage = $"Downloading {gameName}...";

            var progress = new Progress<GameDownloadProgress>(p =>
            {
                // Use lock to prevent race conditions when multiple downloads update progress
                lock (_progressUpdateLock)
                {
                    var speedMb = p.SpeedBytesPerSecond / 1024.0 / 1024.0;

                    var progressText = p.State switch
                    {
                        GameDownloadState.FetchingInfo => "Fetching download information...",
                        GameDownloadState.Downloading => $"Downloading: {p.PercentComplete:F1}% ({speedMb:F1} MB/s)",
                        GameDownloadState.DownloadingVoicePacks => $"Downloading voice packs: {p.PercentComplete:F1}%",
                        GameDownloadState.Verifying => "Verifying downloaded files...",
                        GameDownloadState.Extracting => "Extracting...",
                        GameDownloadState.Cleanup => "Cleaning up...",
                        GameDownloadState.Completed => "Installation complete!",
                        GameDownloadState.Failed => $"Failed: {p.ErrorMessage}",
                        _ => $"{p.State}"
                    };

                    // Store progress for this game
                    _downloadProgressByGame[gameId] = (p.PercentComplete, progressText);

                    // Capture selected game ID once inside the lock
                    var currentSelectedGameId = SelectedGame?.Id;
                    
                    // Update UI only if this game is the currently selected game
                    if (currentSelectedGameId == gameId)
                    {
                        ProgressPercent = p.PercentComplete;
                        ProgressText = progressText;
                        StatusMessage = progressText;
                    }
                }
            });

            var success = await _gameDownloadService.DownloadAndInstallGameAsync(
                gameId,
                progress: progress,
                cancellationToken: cts.Token);

            if (success)
            {
                StatusMessage = $"{gameName} installed successfully!";
                if (SelectedGame?.Id == gameId)
                {
                    ProgressText = "Installation complete!";
                }
            }
            else if (cts.IsCancellationRequested)
            {
                StatusMessage = $"{gameName} installation cancelled";
            }
            else
            {
                StatusMessage = $"{gameName} installation failed";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"{gameName} installation cancelled";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Install failed for {GameId}", gameId);
            StatusMessage = $"Installation failed: {ex.Message}";
        }
        finally
        {
            // Clean up for this specific game
            game.IsDownloading = false;
            _downloadProgressByGame.Remove(gameId);
            _pauseStateByGame.Remove(gameId);
            
            if (_downloadCancellationTokens.TryGetValue(gameId, out var oldCts))
            {
                oldCts.Dispose();
                _downloadCancellationTokens.Remove(gameId);
            }
            
            // Update global state only if no more downloads are in progress
            if (_downloadCancellationTokens.Count == 0)
            {
                IsDownloading = false;
                DownloadingGameId = null;
            }
            
            // Clear progress UI only if this was the selected game
            if (SelectedGame?.Id == gameId)
            {
                IsPaused = false;
                ProgressPercent = 0;
                ProgressText = string.Empty;
                DownloadInfo = null;
            }
            
            OnPropertyChanged(nameof(IsSelectedGameDownloading));
            OnPropertyChanged(nameof(IsAnyGameDownloading));
        }
    }

    [RelayCommand]
    private void CancelDownload()
    {
        // Cancel the download for the currently selected game
        if (SelectedGame != null && _downloadCancellationTokens.TryGetValue(SelectedGame.Id, out var cts))
        {
            var gameId = SelectedGame.Id;
            
            // If this game is paused, we need to resume its downloads first so the cancellation can be processed
            if (_pauseStateByGame.TryGetValue(gameId, out var wasPaused) && wasPaused)
            {
                _downloadService.ResumeDownloadsContaining(gameId);
            }
            
            cts.Cancel();
            IsPaused = false;
            _pauseStateByGame.Remove(gameId);
            StatusMessage = $"Cancelling download for {SelectedGame.DisplayName}...";
        }
    }

    [RelayCommand]
    private void PauseDownload()
    {
        if (SelectedGame == null || !_downloadCancellationTokens.ContainsKey(SelectedGame.Id)) return;
        
        var gameId = SelectedGame.Id;
        
        // Use the new per-game pause method that only pauses downloads for this game
        // The gameId is part of the download path, so this will only affect this game's downloads
        _downloadService.PauseDownloadsContaining(gameId);
        
        // Track pause state only for this game
        _pauseStateByGame[gameId] = true;
        IsPaused = true;
        StatusMessage = $"Download paused for {SelectedGame.DisplayName}";
    }

    [RelayCommand]
    private void ResumeDownload()
    {
        if (SelectedGame == null || !_downloadCancellationTokens.ContainsKey(SelectedGame.Id)) return;
        
        var gameId = SelectedGame.Id;
        
        // Use the new per-game resume method that only resumes downloads for this game
        _downloadService.ResumeDownloadsContaining(gameId);
        
        // Track pause state only for this game
        _pauseStateByGame[gameId] = false;
        IsPaused = false;
        StatusMessage = $"Download resumed for {SelectedGame.DisplayName}";
    }

    [RelayCommand]
    private async Task UninstallGameAsync(GameInfo? game)
    {
        var targetGame = game ?? SelectedGame;
        if (targetGame == null || !targetGame.IsInstalled) return;

        try
        {
            StatusMessage = $"Uninstalling {targetGame.DisplayName}...";
            
            var success = await _installationService.UninstallGameAsync(targetGame.Id, deleteFiles: true);
            
            if (success)
            {
                StatusMessage = $"{targetGame.DisplayName} has been uninstalled";
                // Refresh the game list to update the UI
                await RefreshGamesAsync();
            }
            else
            {
                StatusMessage = $"Failed to uninstall {targetGame.DisplayName}";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to uninstall game {GameId}", targetGame.Id);
            StatusMessage = $"Failed to uninstall: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenInstallFolder(GameInfo? game)
    {
        var targetGame = game ?? SelectedGame;
        if (targetGame == null || !targetGame.IsInstalled || string.IsNullOrEmpty(targetGame.InstallPath)) return;

        try
        {
            if (Directory.Exists(targetGame.InstallPath))
            {
                // Open the folder in the default file manager
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = targetGame.InstallPath,
                    UseShellExecute = true
                });
            }
            else
            {
                StatusMessage = "Install folder does not exist";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open install folder for {GameId}", targetGame.Id);
            StatusMessage = $"Failed to open folder: {ex.Message}";
        }
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
        // Notify computed properties that depend on SelectedGame
        OnPropertyChanged(nameof(IsSelectedGameDownloading));
        OnPropertyChanged(nameof(IsAnyGameDownloading));

        // Always update IsGameRunning when selection changes (even during restore)
        if (value != null)
        {
            IsGameRunning = _launcherService.IsGameRunning(value.Id);
            
            // Update progress display for the newly selected game if it's downloading
            // Use lock to prevent race conditions with progress callbacks
            lock (_progressUpdateLock)
            {
                // Check if this game is downloading by looking at our cancellation token dictionary
                // This is more reliable than checking value.IsDownloading which might not be in sync
                var isGameDownloading = _downloadCancellationTokens.ContainsKey(value.Id);
                
                if (isGameDownloading && _downloadProgressByGame.TryGetValue(value.Id, out var progress))
                {
                    // Restore the stored progress for this game
                    ProgressPercent = progress.ProgressPercent;
                    ProgressText = progress.ProgressText;
                }
                else if (isGameDownloading)
                {
                    // Game is downloading but no progress stored yet - show initial state
                    ProgressPercent = 0;
                    ProgressText = "Starting download...";
                }
                else
                {
                    // Game is not downloading - reset progress display
                    ProgressPercent = 0;
                    ProgressText = string.Empty;
                }
                
                // Restore pause state for this game
                if (isGameDownloading && _pauseStateByGame.TryGetValue(value.Id, out var isPaused))
                {
                    IsPaused = isPaused;
                }
                else
                {
                    IsPaused = false;
                }
            }
        }
        else
        {
            IsGameRunning = false;
        }

        // Skip heavy operations if we're just restoring selection after a game state update
        if (_isRestoringSelection)
        {
            return;
        }

        if (value != null)
        {
            AvailableUpdate = null;
            IsPreloadAvailable = false;
            CacheInfo = null;
            DownloadInfo = null;
            DownloadSizeText = string.Empty;

            // Load background for the selected game
            _ = LoadBackgroundAsync(value.Id);

            if (value.IsInstalled)
            {
                // Load cache info in background
                _ = LoadCacheInfoAsync();

                // Check for updates in background
                _ = CheckForUpdatesAsync();
            }
            else if (!value.IsDownloading)
            {
                // Get download info for non-installed, non-downloading games
                _ = GetDownloadInfoAsync();
            }
        }
    }

    partial void OnIsDownloadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSelectedGameDownloading));
        OnPropertyChanged(nameof(IsAnyGameDownloading));
    }

    partial void OnDownloadingGameIdChanged(string? value)
    {
        OnPropertyChanged(nameof(IsSelectedGameDownloading));
        OnPropertyChanged(nameof(IsAnyGameDownloading));
    }

    private async Task LoadBackgroundAsync(string gameId)
    {
        try
        {
            var backgroundInfo = await _backgroundService.GetBackgroundInfoAsync(gameId);

            if (backgroundInfo != null)
            {
                // Reset theme overlay
                ThemeOverlaySource = null;

                if (backgroundInfo.Type == BackgroundType.Color && !string.IsNullOrEmpty(backgroundInfo.Color))
                {
                    // Just use color background
                    BackgroundSource = null;
                    IsVideoBackground = false;
                    BackgroundColor = backgroundInfo.Color;
                }
                else
                {
                    // Download and cache the background
                    var cachedPath = await _backgroundService.GetCachedBackgroundAsync(gameId);

                    if (!string.IsNullOrEmpty(cachedPath))
                    {
                        BackgroundSource = cachedPath;
                        IsVideoBackground = backgroundInfo.Type == BackgroundType.Video;
                        Log.Debug("Set background for {GameId}: {Path} (Video: {IsVideo})",
                            gameId, cachedPath, IsVideoBackground);
                    }
                    else if (!string.IsNullOrEmpty(backgroundInfo.Url))
                    {
                        // Use URL directly if caching failed
                        BackgroundSource = backgroundInfo.Url;
                        IsVideoBackground = backgroundInfo.Type == BackgroundType.Video;
                    }

                    // Load theme overlay for video backgrounds
                    if (backgroundInfo.Type == BackgroundType.Video && !string.IsNullOrEmpty(backgroundInfo.ThemeUrl))
                    {
                        var cachedThemePath = await _backgroundService.GetCachedThemeImageAsync(gameId);
                        if (!string.IsNullOrEmpty(cachedThemePath))
                        {
                            ThemeOverlaySource = cachedThemePath;
                            Log.Debug("Set theme overlay for {GameId}: {Path}", gameId, cachedThemePath);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load background for {GameId}", gameId);
            // Keep default background
            BackgroundSource = null;
            IsVideoBackground = false;
            ThemeOverlaySource = null;
        }
    }

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

    /// <summary>
    /// Check if the selected game requires Jadeite
    /// </summary>
    public bool SelectedGameRequiresJadeite =>
        SelectedGame != null && GameLauncherService.RequiresJadeite(SelectedGame.GameType);

    /// <summary>
    /// Open game settings dialog for a specific game or the currently selected game
    /// </summary>
    [RelayCommand]
    private void OpenGameSettings(GameInfo? game)
    {
        var targetGame = game ?? SelectedGame;
        if (targetGame == null) return;
        
        // Create the game settings view model
        GameSettingsViewModel = new GameSettingsViewModel(targetGame, _settingsService, _wineRunnerService);
        GameSettingsViewModel.SettingsSaved += OnGameSettingsSaved;
        GameSettingsViewModel.SettingsClosed += OnGameSettingsClosed;
        
        IsGameSettingsVisible = true;
        StatusMessage = $"Configuring settings for {targetGame.DisplayName}";
    }
    
    private void OnGameSettingsSaved(object? sender, GameSettingsSavedEventArgs e)
    {
        StatusMessage = "Game settings saved successfully";
        
        // Switch to the game variant for the selected region
        var newGame = _gameService.GetGameByTypeAndRegion(e.GameType, e.SelectedRegion);
        if (newGame != null)
        {
            // Refresh games collection and select the new game variant
            RefreshGamesCollectionForRegion(e.GameType, e.SelectedRegion);
            
            // Load the background for the new game
            _ = LoadBackgroundAsync(newGame.Id);
        }
        
        // Close the settings dialog
        CloseGameSettings();
    }
    
    /// <summary>
    /// Refresh games collection and select a specific game by type and region
    /// </summary>
    private void RefreshGamesCollectionForRegion(GameType gameType, GameRegion region)
    {
        Games.Clear();
        
        // Get saved region preferences for each game type
        var settings = _settingsService.Settings;
        var gameTypes = new[] { GameType.HonkaiImpact3rd, GameType.GenshinImpact, GameType.HonkaiStarRail, GameType.ZenlessZoneZero };
        
        GameInfo? gameToSelect = null;
        
        foreach (var gt in gameTypes)
        {
            // For the changed game type, use the new region; for others, use saved preferences
            GameRegion regionToUse;
            if (gt == gameType)
            {
                regionToUse = region;
            }
            else if (settings.SelectedRegionPerGame.TryGetValue(gt.ToString(), out var savedRegion) &&
                     Enum.TryParse<GameRegion>(savedRegion, out var parsedRegion))
            {
                regionToUse = parsedRegion;
            }
            else
            {
                regionToUse = GameRegion.Global;
            }
            
            var game = _gameService.GetGameByTypeAndRegion(gt, regionToUse);
            if (game != null)
            {
                Games.Add(game);
                _ = LoadGameIconAsync(game);
                
                // Track the game we want to select
                if (gt == gameType)
                {
                    gameToSelect = game;
                }
            }
        }
        
        // Select the game for the changed type
        if (gameToSelect != null)
        {
            SelectedGame = gameToSelect;
        }
        else if (Games.Count > 0)
        {
            SelectedGame = Games[0];
        }
    }
    
    private void OnGameSettingsClosed(object? sender, EventArgs e)
    {
        CloseGameSettings();
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
    
    /// <summary>
    /// Open the wine runner download dialog
    /// </summary>
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
    
    private void OnWineRunnerDialogClosed(object? sender, EventArgs e)
    {
        CloseWineRunnerDialog();
    }
    
    private void OnRunnersUpdated(object? sender, EventArgs e)
    {
        // Re-check wine version when runners are updated
        _ = UpdateWineInfoAsync();
    }
    
    private async Task UpdateWineInfoAsync()
    {
        var wineInfo = await _launcherService.GetWineInfoAsync();
        if (wineInfo.IsInstalled)
        {
            WineVersion = wineInfo.IsProton ? $"Proton: {wineInfo.Version.Trim()}" : $"Wine: {wineInfo.Version.Trim()}";
        }
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
