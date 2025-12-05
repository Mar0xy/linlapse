using System.Net.Http;
using System.Text.Json;
using Linlapse.Models;
using Serilog;
using SharpCompress.Archives;
using SharpCompress.Common;
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
    private readonly GameConfigurationService _configurationService;

    private static readonly string[] VersionPrefixes = { "version", "ver", "v" };

    public event EventHandler<UpdateInfo>? UpdateAvailable;
    public event EventHandler<UpdateProgress>? UpdateProgressChanged;
    public event EventHandler<string>? UpdateCompleted;
    public event EventHandler<(string GameId, Exception Error)>? UpdateFailed;

    public UpdateService(
        GameService gameService,
        DownloadService downloadService,
        InstallationService installationService,
        GameConfigurationService configurationService)
    {
        _gameService = gameService;
        _downloadService = downloadService;
        _installationService = installationService;
        _configurationService = configurationService;
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

            if (updateInfo != null)
            {
                Log.Debug("Version comparison for {GameId}: Current={CurrentVersion}, Latest={LatestVersion}, HasUpdate={HasUpdate}",
                    gameId, game.Version, updateInfo.LatestVersion, updateInfo.HasUpdate);
                
                if (updateInfo.HasUpdate)
                {
                    _gameService.UpdateGameState(gameId, GameState.NeedsUpdate);
                    UpdateAvailable?.Invoke(this, updateInfo);
                    Log.Information("Update available for {GameId}: {CurrentVersion} -> {LatestVersion}",
                        gameId, game.Version, updateInfo.LatestVersion);
                }
                else if (game.State == GameState.NeedsUpdate)
                {
                    // Reset state if game was marked as needing update but no longer does
                    _gameService.UpdateGameState(gameId, GameState.Ready);
                    Log.Debug("Reset game state for {GameId} - no update needed", gameId);
                }
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
                    // Check for cancellation before starting
                    cancellationToken.ThrowIfCancellationRequested();

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

                    // Check for cancellation after patch completes
                    cancellationToken.ThrowIfCancellationRequested();

                    // Get the full path of install directory for path traversal validation
                    var fullInstallPath = Path.GetFullPath(game.InstallPath);

                    // Move the patched output back to the install path
                    if (Directory.Exists(tempOutputPath))
                    {
                        // Directory output - move all files back to install path
                        foreach (var file in Directory.GetFiles(tempOutputPath, "*", SearchOption.AllDirectories))
                        {
                            // Check for cancellation during file operations
                            cancellationToken.ThrowIfCancellationRequested();

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

            // Extract update using SharpCompress to support additional compression methods
            // (LZMA, Deflate64, PPMd, BZip2, etc.) commonly used in game archives
            updateProgress.State = UpdateState.Extracting;
            progress?.Report(updateProgress);

            await Task.Run(() =>
            {
                using var archive = ArchiveFactory.Open(updatePath);
                var fileEntries = archive.Entries.Where(e => !e.IsDirectory).ToList();
                var totalFiles = fileEntries.Count;
                var processed = 0;

                // Get the full path of install directory for path traversal validation
                var fullInstallPath = Path.GetFullPath(game.InstallPath);

                // Pre-create all directories
                var directories = fileEntries
                    .Select(e => Path.GetDirectoryName(Path.Combine(game.InstallPath, e.Key ?? string.Empty)))
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Distinct()
                    .ToList();

                foreach (var dir in directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fullDir = Path.GetFullPath(dir!);
                    if (!fullDir.StartsWith(fullInstallPath, StringComparison.Ordinal))
                    {
                        Log.Warning("Skipping potentially malicious directory entry: {Dir}", dir);
                        continue;
                    }
                    Directory.CreateDirectory(dir!);
                }

                foreach (var entry in fileEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var entryKey = entry.Key ?? string.Empty;
                    var destPath = Path.GetFullPath(Path.Combine(game.InstallPath, entryKey));

                    // Security: validate path traversal
                    if (!destPath.StartsWith(fullInstallPath, StringComparison.Ordinal))
                    {
                        Log.Warning("Skipping potentially malicious archive entry: {EntryKey}", entryKey);
                        continue;
                    }

                    entry.WriteToFile(destPath, new ExtractionOptions
                    {
                        ExtractFullPath = false,
                        Overwrite = true
                    });

                    processed++;
                    updateProgress.CurrentFile = entryKey;
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
        return _configurationService.GetApiUrl(game.Id);
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
                                       !string.IsNullOrEmpty(game.Version) &&
                                       IsNewerVersion(updateInfo.LatestVersion, game.Version);

                return updateInfo;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse update response for {GameId}", game.Id);
        }

        return null;
    }

    /// <summary>
    /// Compare two version strings to determine if the new version is newer than the current version.
    /// Handles semantic versions like "8.5.0", "7.8.0", "1.0.0.1234", "v1.2.3", etc.
    /// </summary>
    private static bool IsNewerVersion(string newVersion, string currentVersion)
    {
        if (string.IsNullOrEmpty(newVersion) || string.IsNullOrEmpty(currentVersion))
            return false;

        // Normalize both versions for comparison
        var normalizedNew = NormalizeVersion(newVersion);
        var normalizedCurrent = NormalizeVersion(currentVersion);

        // Try to parse as System.Version first (handles most cases)
        if (Version.TryParse(normalizedNew, out var newVer) &&
            Version.TryParse(normalizedCurrent, out var currentVer))
        {
            return newVer > currentVer;
        }

        // Fallback: compare version parts manually using only dot separator
        // This handles semantic versioning more correctly
        var newParts = normalizedNew.Split('.');
        var currentParts = normalizedCurrent.Split('.');
        var maxLength = Math.Max(newParts.Length, currentParts.Length);

        for (int i = 0; i < maxLength; i++)
        {
            var newPart = i < newParts.Length ? newParts[i] : "0";
            var currentPart = i < currentParts.Length ? currentParts[i] : "0";

            // Try numeric comparison first
            if (int.TryParse(newPart, out var newNum) && int.TryParse(currentPart, out var currentNum))
            {
                if (newNum > currentNum) return true;
                if (newNum < currentNum) return false;
            }
            else
            {
                // Fall back to string comparison for non-numeric parts
                var comparison = string.Compare(newPart, currentPart, StringComparison.OrdinalIgnoreCase);
                if (comparison > 0) return true;
                if (comparison < 0) return false;
            }
        }

        return false; // Versions are equal
    }

    /// <summary>
    /// Normalize a version string for comparison.
    /// Removes common prefixes and extracts the numeric version parts.
    /// </summary>
    private static string NormalizeVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return "0.0";

        // Remove common version prefixes (case-insensitive)
        var normalized = version.Trim();
        
        foreach (var prefix in VersionPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[prefix.Length..].TrimStart();
                break; // Only remove one prefix
            }
        }
        
        // Split on dots only for the main version parts
        // Handle pre-release identifiers (e.g., "1.0.0-beta") by taking only the version part
        var dashIndex = normalized.IndexOf('-');
        if (dashIndex > 0)
        {
            normalized = normalized[..dashIndex];
        }
        
        var parts = normalized.Split('.');
        var numericParts = new List<string>();
        
        foreach (var part in parts)
        {
            // Extract leading numeric portion from each part
            var numericPart = new string(part.TakeWhile(char.IsDigit).ToArray());
            if (!string.IsNullOrEmpty(numericPart))
            {
                numericParts.Add(numericPart);
            }
            else
            {
                break; // Stop at first non-numeric part
            }
        }

        // System.Version requires at least 2 parts (major.minor)
        while (numericParts.Count < 2)
        {
            numericParts.Add("0");
        }

        // System.Version supports at most 4 parts (major.minor.build.revision)
        if (numericParts.Count > 4)
        {
            numericParts = numericParts.Take(4).ToList();
        }

        return string.Join(".", numericParts);
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
