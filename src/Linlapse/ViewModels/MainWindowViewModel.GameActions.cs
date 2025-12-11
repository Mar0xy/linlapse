using CommunityToolkit.Mvvm.Input;
using Linlapse.Models;
using Linlapse.Services;
using Serilog;

namespace Linlapse.ViewModels;

/// <summary>
/// Game action commands for MainWindowViewModel
/// </summary>
public partial class MainWindowViewModel
{
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

}
