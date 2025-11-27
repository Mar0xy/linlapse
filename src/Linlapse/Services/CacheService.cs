using Linlapse.Models;
using Serilog;

namespace Linlapse.Services;

/// <summary>
/// Service for managing game caches
/// </summary>
public class CacheService
{
    private readonly GameService _gameService;
    private readonly SettingsService _settingsService;

    public event EventHandler<CacheProgress>? CacheProgressChanged;
    public event EventHandler<string>? CacheCleared;

    public CacheService(GameService gameService, SettingsService settingsService)
    {
        _gameService = gameService;
        _settingsService = settingsService;
    }

    /// <summary>
    /// Get cache information for a game
    /// </summary>
    public async Task<CacheInfo> GetCacheInfoAsync(string gameId)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null || !game.IsInstalled || string.IsNullOrEmpty(game.InstallPath))
        {
            return new CacheInfo { GameId = gameId };
        }

        var cacheInfo = new CacheInfo { GameId = gameId };

        await Task.Run(() =>
        {
            var cacheDirectories = GetCacheDirectories(game);

            foreach (var (name, path) in cacheDirectories)
            {
                if (Directory.Exists(path))
                {
                    var dirInfo = new DirectoryInfo(path);
                    var files = dirInfo.GetFiles("*", SearchOption.AllDirectories);
                    var size = files.Sum(f => f.Length);

                    cacheInfo.CacheEntries.Add(new CacheEntry
                    {
                        Name = name,
                        Path = path,
                        Size = size,
                        FileCount = files.Length
                    });

                    cacheInfo.TotalSize += size;
                    cacheInfo.TotalFiles += files.Length;
                }
            }
        });

        return cacheInfo;
    }

    /// <summary>
    /// Clear all caches for a game
    /// </summary>
    public async Task<bool> ClearAllCachesAsync(
        string gameId,
        IProgress<CacheProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null || !game.IsInstalled || string.IsNullOrEmpty(game.InstallPath))
        {
            Log.Error("Game not found or not installed: {GameId}", gameId);
            return false;
        }

        var cacheProgress = new CacheProgress { GameId = gameId };

        try
        {
            var cacheDirectories = GetCacheDirectories(game);
            cacheProgress.TotalDirectories = cacheDirectories.Count;

            foreach (var (name, path) in cacheDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (Directory.Exists(path))
                {
                    cacheProgress.CurrentCache = name;
                    progress?.Report(cacheProgress);
                    CacheProgressChanged?.Invoke(this, cacheProgress);

                    await ClearDirectoryAsync(path, cancellationToken);
                    cacheProgress.BytesCleared += new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                    cacheProgress.ClearedDirectories++;
                }

                progress?.Report(cacheProgress);
                CacheProgressChanged?.Invoke(this, cacheProgress);
            }

            CacheCleared?.Invoke(this, gameId);
            Log.Information("Cleared all caches for {GameId}: {Bytes} bytes", gameId, cacheProgress.BytesCleared);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to clear caches for {GameId}", gameId);
            return false;
        }
    }

    /// <summary>
    /// Clear a specific cache for a game
    /// </summary>
    public async Task<bool> ClearSpecificCacheAsync(
        string gameId,
        string cacheName,
        CancellationToken cancellationToken = default)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null || !game.IsInstalled || string.IsNullOrEmpty(game.InstallPath))
        {
            Log.Error("Game not found or not installed: {GameId}", gameId);
            return false;
        }

        var cacheDirectories = GetCacheDirectories(game);
        var cache = cacheDirectories.FirstOrDefault(c => c.Name.Equals(cacheName, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrEmpty(cache.Path) || !Directory.Exists(cache.Path))
        {
            Log.Warning("Cache not found: {CacheName} for {GameId}", cacheName, gameId);
            return false;
        }

        try
        {
            await ClearDirectoryAsync(cache.Path, cancellationToken);
            Log.Information("Cleared {CacheName} cache for {GameId}", cacheName, gameId);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to clear {CacheName} cache for {GameId}", cacheName, gameId);
            return false;
        }
    }

    /// <summary>
    /// Clear launcher cache
    /// </summary>
    public async Task<long> ClearLauncherCacheAsync(CancellationToken cancellationToken = default)
    {
        var cacheDir = SettingsService.GetCacheDirectory();
        if (!Directory.Exists(cacheDir))
            return 0;

        var size = await Task.Run(() =>
        {
            var dirInfo = new DirectoryInfo(cacheDir);
            return dirInfo.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }, cancellationToken);

        await ClearDirectoryAsync(cacheDir, cancellationToken);
        Log.Information("Cleared launcher cache: {Bytes} bytes", size);
        return size;
    }

    private List<(string Name, string Path)> GetCacheDirectories(GameInfo game)
    {
        var cacheDirectories = new List<(string Name, string Path)>();
        var installPath = game.InstallPath;

        switch (game.GameType)
        {
            case GameType.HonkaiImpact3rd:
                cacheDirectories.Add(("Game Cache", Path.Combine(installPath, "Games", "BH3_Data", "Cache")));
                cacheDirectories.Add(("Shader Cache", Path.Combine(installPath, "Games", "BH3_Data", "ShaderCache")));
                cacheDirectories.Add(("Web Cache", Path.Combine(installPath, "Games", "BH3_Data", "webCaches")));
                cacheDirectories.Add(("Update Files", Path.Combine(installPath, "Games", "BH3_Data", "StreamingAssets", "Video")));
                break;

            case GameType.GenshinImpact:
                cacheDirectories.Add(("Game Cache", Path.Combine(installPath, "GenshinImpact_Data", "Persistent", "AssetBundles")));
                cacheDirectories.Add(("Shader Cache", Path.Combine(installPath, "GenshinImpact_Data", "ShaderCache")));
                cacheDirectories.Add(("Web Cache", Path.Combine(installPath, "GenshinImpact_Data", "webCaches")));
                cacheDirectories.Add(("Audio Cache", Path.Combine(installPath, "GenshinImpact_Data", "Persistent", "audio")));
                break;

            case GameType.HonkaiStarRail:
                cacheDirectories.Add(("Game Cache", Path.Combine(installPath, "StarRail_Data", "Persistent")));
                cacheDirectories.Add(("Shader Cache", Path.Combine(installPath, "StarRail_Data", "ShaderCache")));
                cacheDirectories.Add(("Web Cache", Path.Combine(installPath, "StarRail_Data", "webCaches")));
                break;

            case GameType.ZenlessZoneZero:
                cacheDirectories.Add(("Game Cache", Path.Combine(installPath, "ZenlessZoneZero_Data", "Persistent")));
                cacheDirectories.Add(("Shader Cache", Path.Combine(installPath, "ZenlessZoneZero_Data", "ShaderCache")));
                cacheDirectories.Add(("Web Cache", Path.Combine(installPath, "ZenlessZoneZero_Data", "webCaches")));
                break;

            default:
                // Generic cache paths
                cacheDirectories.Add(("Cache", Path.Combine(installPath, "Cache")));
                cacheDirectories.Add(("Temp", Path.Combine(installPath, "Temp")));
                break;
        }

        return cacheDirectories;
    }

    private async Task ClearDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
            return;

        await Task.Run(() =>
        {
            var dirInfo = new DirectoryInfo(path);

            foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    file.Delete();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to delete file: {Path}", file.FullName);
                }
            }

            foreach (var dir in dirInfo.EnumerateDirectories("*", SearchOption.AllDirectories).Reverse())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!dir.EnumerateFiles("*", SearchOption.AllDirectories).Any())
                    {
                        dir.Delete(recursive: false);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to delete directory: {Path}", dir.FullName);
                }
            }
        }, cancellationToken);
    }
}

/// <summary>
/// Cache information for a game
/// </summary>
public class CacheInfo
{
    public string GameId { get; set; } = string.Empty;
    public long TotalSize { get; set; }
    public int TotalFiles { get; set; }
    public List<CacheEntry> CacheEntries { get; set; } = new();
}

/// <summary>
/// Individual cache entry
/// </summary>
public class CacheEntry
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public int FileCount { get; set; }
}

/// <summary>
/// Cache operation progress
/// </summary>
public class CacheProgress
{
    public string GameId { get; set; } = string.Empty;
    public string CurrentCache { get; set; } = string.Empty;
    public int TotalDirectories { get; set; }
    public int ClearedDirectories { get; set; }
    public long BytesCleared { get; set; }
}
