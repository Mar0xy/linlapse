using CommunityToolkit.Mvvm.Input;
using Linlapse.Models;
using Linlapse.Services;
using Serilog;

namespace Linlapse.ViewModels;

/// <summary>
/// Download management for MainWindowViewModel
/// </summary>
public partial class MainWindowViewModel
{
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

}
