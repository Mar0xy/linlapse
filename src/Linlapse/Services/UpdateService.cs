using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using Linlapse.Models;
using Serilog;
using SharpHDiffPatch.Core;

namespace Linlapse.Services;

/// <summary>
/// Service for checking and applying game updates
/// </summary>
public class UpdateService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly GameService _gameService;
    private readonly DownloadService _downloadService;
    private readonly InstallationService _installationService;

    public event EventHandler<UpdateInfo>? UpdateAvailable;
    public event EventHandler<UpdateProgress>? UpdateProgressChanged;
    public event EventHandler<string>? UpdateCompleted;
    public event EventHandler<(string GameId, Exception Error)>? UpdateFailed;

    public UpdateService(
        GameService gameService,
        DownloadService downloadService,
        InstallationService installationService)
    {
        _gameService = gameService;
        _downloadService = downloadService;
        _installationService = installationService;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Linlapse/1.0");
    }

    /// <summary>
    /// Check for game updates
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdatesAsync(string gameId, CancellationToken cancellationToken = default)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null)
        {
            Log.Warning("Game not found: {GameId}", gameId);
            return null;
        }

        try
        {
            var apiUrl = GetGameApiUrl(game);
            if (string.IsNullOrEmpty(apiUrl))
            {
                Log.Debug("No API URL configured for {GameId}", gameId);
                return null;
            }

            var response = await _httpClient.GetStringAsync(apiUrl, cancellationToken);
            var updateInfo = ParseUpdateResponse(game, response);

            if (updateInfo != null && updateInfo.HasUpdate)
            {
                _gameService.UpdateGameState(gameId, GameState.NeedsUpdate);
                UpdateAvailable?.Invoke(this, updateInfo);
                Log.Information("Update available for {GameId}: {CurrentVersion} -> {LatestVersion}",
                    gameId, game.Version, updateInfo.LatestVersion);
            }

            return updateInfo;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to check updates for {GameId}", gameId);
            return null;
        }
    }

    /// <summary>
    /// Check all games for updates
    /// </summary>
    public async Task<List<UpdateInfo>> CheckAllUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var updates = new List<UpdateInfo>();

        foreach (var game in _gameService.Games.Where(g => g.IsInstalled))
        {
            var update = await CheckForUpdatesAsync(game.Id, cancellationToken);
            if (update != null && update.HasUpdate)
            {
                updates.Add(update);
            }
        }

        return updates;
    }

    /// <summary>
    /// Download and apply game update
    /// </summary>
    public async Task<bool> ApplyUpdateAsync(
        string gameId,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null || !game.IsInstalled || string.IsNullOrEmpty(game.InstallPath))
        {
            Log.Error("Game not found or not installed: {GameId}", gameId);
            return false;
        }

        var updateProgress = new UpdateProgress { GameId = gameId };

        try
        {
            _gameService.UpdateGameState(gameId, GameState.Updating);

            // Check for update info
            var updateInfo = await CheckForUpdatesAsync(gameId, cancellationToken);
            if (updateInfo == null || !updateInfo.HasUpdate)
            {
                Log.Information("No updates available for {GameId}", gameId);
                _gameService.UpdateGameState(gameId, GameState.Ready);
                return true;
            }

            // Check if delta patch is available
            if (!string.IsNullOrEmpty(updateInfo.DeltaPatchUrl))
            {
                updateProgress.State = UpdateState.DownloadingPatch;
                progress?.Report(updateProgress);

                var patchSuccess = await ApplyDeltaPatchAsync(game, updateInfo, updateProgress, progress, cancellationToken);
                if (patchSuccess)
                {
                    game.Version = updateInfo.LatestVersion;
                    _gameService.UpdateGameState(gameId, GameState.Ready);
                    UpdateCompleted?.Invoke(this, gameId);
                    return true;
                }
            }

            // Fall back to full update
            if (!string.IsNullOrEmpty(updateInfo.FullPackageUrl))
            {
                updateProgress.State = UpdateState.DownloadingFull;
                progress?.Report(updateProgress);

                var fullSuccess = await ApplyFullUpdateAsync(game, updateInfo, updateProgress, progress, cancellationToken);
                if (fullSuccess)
                {
                    game.Version = updateInfo.LatestVersion;
                    _gameService.UpdateGameState(gameId, GameState.Ready);
                    UpdateCompleted?.Invoke(this, gameId);
                    return true;
                }
            }

            _gameService.UpdateGameState(gameId, GameState.NeedsUpdate);
            return false;
        }
        catch (Exception ex)
        {
            _gameService.UpdateGameState(gameId, game.IsInstalled ? GameState.Ready : GameState.NotInstalled);
            Log.Error(ex, "Update failed for {GameId}", gameId);
            UpdateFailed?.Invoke(this, (gameId, ex));
            return false;
        }
    }

    /// <summary>
    /// Download preload files for upcoming update
    /// </summary>
    public async Task<bool> DownloadPreloadAsync(
        string gameId,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null || !game.IsInstalled || string.IsNullOrEmpty(game.InstallPath))
        {
            Log.Error("Game not found or not installed: {GameId}", gameId);
            return false;
        }

        var updateProgress = new UpdateProgress
        {
            GameId = gameId,
            State = UpdateState.Preloading
        };

        try
        {
            _gameService.UpdateGameState(gameId, GameState.Preloading);

            var preloadInfo = await GetPreloadInfoAsync(gameId, cancellationToken);
            if (preloadInfo == null || string.IsNullOrEmpty(preloadInfo.PreloadUrl))
            {
                Log.Information("No preload available for {GameId}", gameId);
                _gameService.UpdateGameState(gameId, GameState.Ready);
                return false;
            }

            var preloadDir = Path.Combine(game.InstallPath, "preload");
            Directory.CreateDirectory(preloadDir);

            var downloadProgress = new Progress<DownloadProgress>(dp =>
            {
                updateProgress.TotalBytes = dp.TotalBytes;
                updateProgress.ProcessedBytes = dp.BytesDownloaded;
                updateProgress.SpeedBytesPerSecond = dp.SpeedBytesPerSecond;
                progress?.Report(updateProgress);
                UpdateProgressChanged?.Invoke(this, updateProgress);
            });

            var preloadPath = Path.Combine(preloadDir, "preload.zip");
            var success = await _downloadService.DownloadFileAsync(
                preloadInfo.PreloadUrl,
                preloadPath,
                downloadProgress,
                cancellationToken);

            _gameService.UpdateGameState(gameId, GameState.Ready);

            if (success)
            {
                Log.Information("Preload completed for {GameId}", gameId);
            }

            return success;
        }
        catch (Exception ex)
        {
            _gameService.UpdateGameState(gameId, GameState.Ready);
            Log.Error(ex, "Preload failed for {GameId}", gameId);
            return false;
        }
    }

    private async Task<bool> ApplyDeltaPatchAsync(
        GameInfo game,
        UpdateInfo updateInfo,
        UpdateProgress updateProgress,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var patchDir = Path.Combine(SettingsService.GetCacheDirectory(), "patches", game.Id);
            Directory.CreateDirectory(patchDir);
            var patchPath = Path.Combine(patchDir, "delta.hdiff");

            // Download patch
            var downloadProgress = new Progress<DownloadProgress>(dp =>
            {
                updateProgress.TotalBytes = dp.TotalBytes;
                updateProgress.ProcessedBytes = dp.BytesDownloaded;
                updateProgress.SpeedBytesPerSecond = dp.SpeedBytesPerSecond;
                progress?.Report(updateProgress);
                UpdateProgressChanged?.Invoke(this, updateProgress);
            });

            var downloadSuccess = await _downloadService.DownloadFileAsync(
                updateInfo.DeltaPatchUrl!,
                patchPath,
                downloadProgress,
                cancellationToken);

            if (!downloadSuccess)
                return false;

            // Apply HDiff patch
            updateProgress.State = UpdateState.ApplyingPatch;
            progress?.Report(updateProgress);

            await Task.Run(() =>
            {
                // Create a temporary output path for the patched result
                var tempOutputPath = Path.Combine(patchDir, "patched_output");
                EventHandler<SharpHDiffPatch.Core.Event.PatchEvent>? patchEventHandler = null;

                try
                {
                    // Initialize HDiff patcher with the diff file
                    var patcher = new HDiffPatch();
                    patcher.Initialize(patchPath);
                    
                    // Set up progress callback via EventListener
                    patchEventHandler = (sender, e) =>
                    {
                        updateProgress.ProcessedBytes = e.CurrentSizePatched;
                        updateProgress.TotalBytes = e.TotalSizeToBePatched;
                        updateProgress.SpeedBytesPerSecond = e.Speed;
                        progress?.Report(updateProgress);
                        UpdateProgressChanged?.Invoke(this, updateProgress);
                    };
                    SharpHDiffPatch.Core.EventListener.PatchEvent += patchEventHandler;

                    // Apply the patch
                    // The HDiffPatch library patches: input (old) + diff -> output (new)
                    patcher.Patch(game.InstallPath, tempOutputPath, useBufferedPatch: true, cancellationToken);

                    // Get the full path of install directory for path traversal validation
                    var fullInstallPath = Path.GetFullPath(game.InstallPath);

                    // Move the patched output back to the install path
                    if (Directory.Exists(tempOutputPath))
                    {
                        // Directory output - move all files back to install path
                        foreach (var file in Directory.GetFiles(tempOutputPath, "*", SearchOption.AllDirectories))
                        {
                            var relativePath = Path.GetRelativePath(tempOutputPath, file);
                            var destPath = Path.GetFullPath(Path.Combine(game.InstallPath, relativePath));
                            
                            // Validate path traversal - ensure destPath stays within install path
                            if (!destPath.StartsWith(fullInstallPath, StringComparison.Ordinal))
                            {
                                Log.Warning("Skipping potentially malicious patch file: {RelativePath}", relativePath);
                                continue;
                            }
                            
                            var destDir = Path.GetDirectoryName(destPath);
                            if (!string.IsNullOrEmpty(destDir))
                            {
                                Directory.CreateDirectory(destDir);
                            }
                            
                            File.Move(file, destPath, overwrite: true);
                        }
                    }
                    else if (File.Exists(tempOutputPath))
                    {
                        // Single file output - this shouldn't happen for game updates
                        // but handle it just in case
                        Log.Warning("Unexpected single file patch result for {GameId}", game.Id);
                    }

                    Log.Information("Delta patch applied successfully for {GameId}", game.Id);
                }
                finally
                {
                    // Unsubscribe from event to prevent memory leaks
                    if (patchEventHandler != null)
                    {
                        SharpHDiffPatch.Core.EventListener.PatchEvent -= patchEventHandler;
                    }

                    // Cleanup temp output
                    try 
                    { 
                        if (File.Exists(tempOutputPath))
                            File.Delete(tempOutputPath);
                        if (Directory.Exists(tempOutputPath))
                            Directory.Delete(tempOutputPath, recursive: true); 
                    } 
                    catch { }
                }
            }, cancellationToken);

            // Cleanup patch file
            try { File.Delete(patchPath); } catch { }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to apply delta patch for {GameId}", game.Id);
            return false;
        }
    }

    private async Task<bool> ApplyFullUpdateAsync(
        GameInfo game,
        UpdateInfo updateInfo,
        UpdateProgress updateProgress,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var updateDir = Path.Combine(SettingsService.GetCacheDirectory(), "updates", game.Id);
            Directory.CreateDirectory(updateDir);
            var updatePath = Path.Combine(updateDir, "update.zip");

            // Download full package
            var downloadProgress = new Progress<DownloadProgress>(dp =>
            {
                updateProgress.TotalBytes = dp.TotalBytes;
                updateProgress.ProcessedBytes = dp.BytesDownloaded;
                updateProgress.SpeedBytesPerSecond = dp.SpeedBytesPerSecond;
                progress?.Report(updateProgress);
                UpdateProgressChanged?.Invoke(this, updateProgress);
            });

            var downloadSuccess = await _downloadService.DownloadFileAsync(
                updateInfo.FullPackageUrl!,
                updatePath,
                downloadProgress,
                cancellationToken);

            if (!downloadSuccess)
                return false;

            // Extract update
            updateProgress.State = UpdateState.Extracting;
            progress?.Report(updateProgress);

            await Task.Run(() =>
            {
                using var archive = System.IO.Compression.ZipFile.OpenRead(updatePath);
                var totalFiles = archive.Entries.Count;
                var processed = 0;

                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var destPath = Path.Combine(game.InstallPath, entry.FullName);

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destPath);
                    }
                    else
                    {
                        var dir = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(dir))
                            Directory.CreateDirectory(dir);

                        entry.ExtractToFile(destPath, overwrite: true);
                    }

                    processed++;
                    updateProgress.CurrentFile = entry.FullName;
                    updateProgress.ProcessedFiles = processed;
                    updateProgress.TotalFiles = totalFiles;
                    progress?.Report(updateProgress);
                }
            }, cancellationToken);

            // Cleanup
            try { File.Delete(updatePath); } catch { }

            Log.Information("Full update applied for {GameId}", game.Id);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to apply full update for {GameId}", game.Id);
            return false;
        }
    }

    private string? GetGameApiUrl(GameInfo game)
    {
        // These are placeholder URLs - real implementation would use actual game API endpoints
        return game.GameType switch
        {
            GameType.HonkaiImpact3rd => game.Region switch
            {
                GameRegion.Global => "https://sdk-os-static.mihoyo.com/bh3_global/mdk/launcher/api/resource?key=dpz65xJ3&launcher_id=10",
                GameRegion.SEA => "https://sdk-os-static.mihoyo.com/bh3_global/mdk/launcher/api/resource?key=tEGNtVhN&launcher_id=9",
                _ => null
            },
            GameType.GenshinImpact => game.Region switch
            {
                GameRegion.Global => "https://sdk-os-static.mihoyo.com/hk4e_global/mdk/launcher/api/resource?key=gcStgarh&launcher_id=10",
                GameRegion.China => "https://sdk-static.mihoyo.com/hk4e_cn/mdk/launcher/api/resource?key=eYd89JmJ&launcher_id=18",
                _ => null
            },
            GameType.HonkaiStarRail => game.Region switch
            {
                GameRegion.Global => "https://hkrpg-launcher-static.hoyoverse.com/hkrpg_global/mdk/launcher/api/resource?key=vplOVX8Vn7cwG8yb&launcher_id=35",
                GameRegion.China => "https://api-launcher.mihoyo.com/hkrpg_cn/mdk/launcher/api/resource?key=6KcVuOkbcqjJomjZ&launcher_id=33",
                _ => null
            },
            GameType.ZenlessZoneZero => game.Region switch
            {
                GameRegion.Global => "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getGamePackages?launcher_id=VYTpXlbWo8",
                _ => null
            },
            _ => null
        };
    }

    private UpdateInfo? ParseUpdateResponse(GameInfo game, string response)
    {
        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            // Handle different API response formats
            if (root.TryGetProperty("data", out var data))
            {
                var updateInfo = new UpdateInfo
                {
                    GameId = game.Id,
                    CurrentVersion = game.Version
                };

                if (data.TryGetProperty("game", out var gameData))
                {
                    if (gameData.TryGetProperty("latest", out var latest))
                    {
                        updateInfo.LatestVersion = latest.GetProperty("version").GetString() ?? "";

                        if (latest.TryGetProperty("path", out var path))
                        {
                            updateInfo.FullPackageUrl = path.GetString();
                        }
                        if (latest.TryGetProperty("size", out var size))
                        {
                            updateInfo.DownloadSize = GetInt64FromElement(size);
                        }
                    }

                    if (gameData.TryGetProperty("diffs", out var diffs) && diffs.GetArrayLength() > 0)
                    {
                        foreach (var diff in diffs.EnumerateArray())
                        {
                            var fromVersion = diff.GetProperty("version").GetString();
                            if (fromVersion == game.Version)
                            {
                                updateInfo.DeltaPatchUrl = diff.GetProperty("path").GetString();
                                if (diff.TryGetProperty("size", out var diffSize))
                                {
                                    updateInfo.DeltaSize = GetInt64FromElement(diffSize);
                                }
                                break;
                            }
                        }
                    }
                }

                if (data.TryGetProperty("pre_download_game", out var preload) && 
                    preload.ValueKind != JsonValueKind.Null)
                {
                    if (preload.TryGetProperty("latest", out var preloadLatest))
                    {
                        updateInfo.PreloadVersion = preloadLatest.GetProperty("version").GetString();
                    }
                }

                updateInfo.HasUpdate = !string.IsNullOrEmpty(updateInfo.LatestVersion) &&
                                       updateInfo.LatestVersion != game.Version;

                return updateInfo;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse update response for {GameId}", game.Id);
        }

        return null;
    }

    private async Task<PreloadInfo?> GetPreloadInfoAsync(string gameId, CancellationToken cancellationToken)
    {
        // Check if preload is available via the same API
        var updateInfo = await CheckForUpdatesAsync(gameId, cancellationToken);
        if (updateInfo != null && !string.IsNullOrEmpty(updateInfo.PreloadVersion))
        {
            return new PreloadInfo
            {
                GameId = gameId,
                Version = updateInfo.PreloadVersion,
                PreloadUrl = updateInfo.FullPackageUrl // In reality, this would be a different URL
            };
        }
        return null;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    /// <summary>
    /// Helper method to get Int64 from a JsonElement that might be a string or number
    /// </summary>
    private static long GetInt64FromElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.GetInt64();
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            var str = element.GetString();
            if (long.TryParse(str, out var value))
            {
                return value;
            }
        }
        return 0;
    }
}

/// <summary>
/// Update information for a game
/// </summary>
public class UpdateInfo
{
    public string GameId { get; set; } = string.Empty;
    public string CurrentVersion { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public bool HasUpdate { get; set; }
    public string? FullPackageUrl { get; set; }
    public string? DeltaPatchUrl { get; set; }
    public long DownloadSize { get; set; }
    public long DeltaSize { get; set; }
    public string? PreloadVersion { get; set; }
    public string? ReleaseNotes { get; set; }
}

/// <summary>
/// Preload information
/// </summary>
public class PreloadInfo
{
    public string GameId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? PreloadUrl { get; set; }
    public long Size { get; set; }
}

/// <summary>
/// Update progress information
/// </summary>
public class UpdateProgress
{
    public string GameId { get; set; } = string.Empty;
    public UpdateState State { get; set; } = UpdateState.Idle;
    public long TotalBytes { get; set; }
    public long ProcessedBytes { get; set; }
    public double SpeedBytesPerSecond { get; set; }
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public double PercentComplete => TotalBytes > 0 ? (double)ProcessedBytes / TotalBytes * 100 : 0;
}

/// <summary>
/// Update state
/// </summary>
public enum UpdateState
{
    Idle,
    CheckingUpdate,
    DownloadingPatch,
    DownloadingFull,
    ApplyingPatch,
    Extracting,
    Verifying,
    Preloading,
    Completed,
    Failed
}
