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
    
    // Runner selection for global settings
    [ObservableProperty]
    private InstalledRunner? _selectedGlobalWineRunner;
    
    [ObservableProperty]
    private InstalledRunner? _selectedGlobalProtonRunner;
    
    public ObservableCollection<InstalledRunner> InstalledWineRunners { get; } = new();
    public ObservableCollection<InstalledRunner> InstalledProtonRunners { get; } = new();

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
        var _configurationService = new GameConfigurationService();
        _gameService = new GameService(_settingsService);
        _downloadService = new DownloadService(_settingsService);
        _installationService = new InstallationService(_settingsService, _downloadService, _gameService);
        _repairService = new RepairService(_gameService, _downloadService);
        _cacheService = new CacheService(_gameService, _settingsService);
        _updateService = new UpdateService(_gameService, _downloadService, _installationService, _configurationService);
        _gameSettingsService = new GameSettingsService(_gameService);
        _launcherService = new GameLauncherService(_settingsService, _gameService);
        _gameDownloadService = new GameDownloadService(_gameService, _downloadService, _installationService, _settingsService, _configurationService);
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



    
    
    partial void OnSelectedGlobalWineRunnerChanged(InstalledRunner? value)
    {
        if (value != null)
        {
            WineExecutablePath = value.ExecutablePath;
        }
    }
    
    partial void OnSelectedGlobalProtonRunnerChanged(InstalledRunner? value)
    {
        if (value != null)
        {
            ProtonPath = value.InstallPath;
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
    
    
    /// <summary>
    /// Refresh games collection and select a specific game by type and region
    /// </summary>
    
    
    
    /// <summary>
    /// Open the wine runner download dialog
    /// </summary>
    
    
    
    private async Task UpdateWineInfoAsync()
    {
        var wineInfo = await _launcherService.GetWineInfoAsync();
        if (wineInfo.IsInstalled)
        {
            WineVersion = wineInfo.IsProton ? $"Proton: {wineInfo.Version.Trim()}" : $"Wine: {wineInfo.Version.Trim()}";
        }
    }
    
}
