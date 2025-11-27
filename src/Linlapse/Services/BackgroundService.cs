using System.Net.Http;
using System.Text.Json;
using Linlapse.Models;
using Serilog;

namespace Linlapse.Services;

/// <summary>
/// Service for fetching and managing game background images and videos
/// </summary>
public class BackgroundService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly GameService _gameService;
    private readonly SettingsService _settingsService;
    private readonly string _backgroundCacheDir;

    public BackgroundService(GameService gameService, SettingsService settingsService)
    {
        _gameService = gameService;
        _settingsService = settingsService;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Linlapse/1.0");
        _backgroundCacheDir = Path.Combine(SettingsService.GetCacheDirectory(), "backgrounds");
        Directory.CreateDirectory(_backgroundCacheDir);
    }

    /// <summary>
    /// Get background information for a game from the API
    /// </summary>
    public async Task<BackgroundInfo?> GetBackgroundInfoAsync(string gameId, CancellationToken cancellationToken = default)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null)
        {
            Log.Warning("Game not found: {GameId}", gameId);
            return null;
        }

        try
        {
            var apiUrl = GetBackgroundApiUrl(game);
            if (string.IsNullOrEmpty(apiUrl))
            {
                Log.Debug("No background API URL for game: {GameId}", gameId);
                return GetDefaultBackground(game);
            }

            var response = await _httpClient.GetStringAsync(apiUrl, cancellationToken);
            var backgroundInfo = ParseBackgroundResponse(game, response);

            if (backgroundInfo != null)
            {
                Log.Information("Retrieved background info for {GameId}: {Type}",
                    gameId, backgroundInfo.Type);
            }

            return backgroundInfo ?? GetDefaultBackground(game);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get background info for {GameId}", gameId);
            return GetDefaultBackground(game);
        }
    }

    /// <summary>
    /// Download and cache the background for a game
    /// </summary>
    public async Task<string?> GetCachedBackgroundAsync(string gameId, CancellationToken cancellationToken = default)
    {
        var backgroundInfo = await GetBackgroundInfoAsync(gameId, cancellationToken);
        if (backgroundInfo == null || string.IsNullOrEmpty(backgroundInfo.Url))
        {
            return null;
        }

        var extension = backgroundInfo.Type == BackgroundType.Video ? ".mp4" : ".jpg";
        var cacheFileName = $"{gameId}_bg{extension}";
        var cachePath = Path.Combine(_backgroundCacheDir, cacheFileName);

        // Check if we have a cached version
        if (File.Exists(cachePath))
        {
            var fileInfo = new FileInfo(cachePath);
            // Use cached file if less than 24 hours old
            if ((DateTime.UtcNow - fileInfo.LastWriteTimeUtc).TotalHours < 24)
            {
                return cachePath;
            }
        }

        // Download the background
        try
        {
            var bytes = await _httpClient.GetByteArrayAsync(backgroundInfo.Url, cancellationToken);
            await File.WriteAllBytesAsync(cachePath, bytes, cancellationToken);
            Log.Information("Downloaded background for {GameId}: {Path}", gameId, cachePath);
            return cachePath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download background for {GameId}", gameId);
            // Return cached version if download fails
            return File.Exists(cachePath) ? cachePath : null;
        }
    }

    /// <summary>
    /// Get all backgrounds for all games (for preloading)
    /// </summary>
    public async Task PreloadAllBackgroundsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var game in _gameService.Games)
        {
            try
            {
                await GetCachedBackgroundAsync(game.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to preload background for {GameId}", game.Id);
            }
        }
    }

    /// <summary>
    /// Clear cached backgrounds
    /// </summary>
    public void ClearCache()
    {
        try
        {
            if (Directory.Exists(_backgroundCacheDir))
            {
                foreach (var file in Directory.GetFiles(_backgroundCacheDir))
                {
                    File.Delete(file);
                }
                Log.Information("Cleared background cache");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to clear background cache");
        }
    }

    private string? GetBackgroundApiUrl(GameInfo game)
    {
        // Use the unified HoYoPlay API which returns all games with backgrounds
        return game.Region switch
        {
            GameRegion.Global => "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getGames?launcher_id=VYTpXlbWo8&language=en-us",
            GameRegion.China => "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGames?launcher_id=jGHBHlcOq1&language=zh-cn",
            GameRegion.SEA => "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getGames?launcher_id=VYTpXlbWo8&language=en-us",
            _ => null
        };
    }

    /// <summary>
    /// Get the game biz identifier for matching API responses
    /// </summary>
    private string? GetGameBiz(GameInfo game)
    {
        return game.GameType switch
        {
            GameType.GenshinImpact => game.Region switch
            {
                GameRegion.Global => "hk4e_global",
                GameRegion.China => "hk4e_cn",
                _ => null
            },
            GameType.HonkaiStarRail => game.Region switch
            {
                GameRegion.Global => "hkrpg_global",
                GameRegion.China => "hkrpg_cn",
                _ => null
            },
            GameType.HonkaiImpact3rd => game.Region switch
            {
                GameRegion.Global => "bh3_global",
                GameRegion.SEA => "bh3_global",
                GameRegion.China => "bh3_cn",
                _ => null
            },
            GameType.ZenlessZoneZero => game.Region switch
            {
                GameRegion.Global => "nap_global",
                GameRegion.China => "nap_cn",
                _ => null
            },
            _ => null
        };
    }

    private BackgroundInfo? ParseBackgroundResponse(GameInfo game, string response)
    {
        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
                return null;

            var backgroundInfo = new BackgroundInfo
            {
                GameId = game.Id
            };

            // HoYoPlay API format: data.games[] array with each game having display.background.url
            if (data.TryGetProperty("games", out var games) && games.ValueKind == JsonValueKind.Array)
            {
                var gameBiz = GetGameBiz(game);
                if (gameBiz == null)
                    return null;

                foreach (var gameEntry in games.EnumerateArray())
                {
                    // Match by biz identifier
                    if (!gameEntry.TryGetProperty("biz", out var biz))
                        continue;

                    var bizStr = biz.GetString();
                    if (bizStr != gameBiz)
                        continue;

                    // Found matching game, get display.background
                    if (gameEntry.TryGetProperty("display", out var display) && display.ValueKind == JsonValueKind.Object)
                    {
                        if (display.TryGetProperty("background", out var background) && background.ValueKind == JsonValueKind.Object)
                        {
                            if (background.TryGetProperty("url", out var bgUrl))
                            {
                                backgroundInfo.Url = bgUrl.GetString() ?? "";
                                backgroundInfo.Type = BackgroundType.Image;
                            }
                        }

                        // Fallback to icon if no background
                        if (string.IsNullOrEmpty(backgroundInfo.Url) && 
                            display.TryGetProperty("icon", out var icon) && icon.ValueKind == JsonValueKind.Object)
                        {
                            if (icon.TryGetProperty("url", out var iconUrl))
                            {
                                backgroundInfo.Url = iconUrl.GetString() ?? "";
                                backgroundInfo.Type = BackgroundType.Image;
                            }
                        }
                    }

                    break;
                }
            }

            // Legacy format support: adv -> background
            if (string.IsNullOrEmpty(backgroundInfo.Url) && 
                data.TryGetProperty("adv", out var adv) && adv.ValueKind == JsonValueKind.Object)
            {
                if (adv.TryGetProperty("background", out var bg))
                {
                    backgroundInfo.Url = bg.GetString() ?? "";
                    backgroundInfo.Type = BackgroundType.Image;
                }
            }

            // Legacy format: backgrounds array
            if (string.IsNullOrEmpty(backgroundInfo.Url) &&
                data.TryGetProperty("backgrounds", out var backgrounds) && 
                backgrounds.ValueKind == JsonValueKind.Array && backgrounds.GetArrayLength() > 0)
            {
                var firstBg = backgrounds[0];
                if (firstBg.TryGetProperty("background", out var bgUrl))
                {
                    backgroundInfo.Url = bgUrl.GetString() ?? "";
                }
                else if (firstBg.TryGetProperty("url", out var url))
                {
                    backgroundInfo.Url = url.GetString() ?? "";
                }
            }

            return string.IsNullOrEmpty(backgroundInfo.Url) ? null : backgroundInfo;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse background response for {GameId}", game.Id);
            return null;
        }
    }

    private BackgroundInfo GetDefaultBackground(GameInfo game)
    {
        // Return a default gradient color based on game type
        var color = game.GameType switch
        {
            GameType.HonkaiImpact3rd => "#1a1a3e",
            GameType.GenshinImpact => "#1a2e1a",
            GameType.HonkaiStarRail => "#2e1a2e",
            GameType.ZenlessZoneZero => "#1a2e2e",
            _ => "#1a1a2e"
        };

        return new BackgroundInfo
        {
            GameId = game.Id,
            Type = BackgroundType.Color,
            Color = color
        };
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

/// <summary>
/// Background information for a game
/// </summary>
public class BackgroundInfo
{
    public string GameId { get; set; } = string.Empty;
    public BackgroundType Type { get; set; } = BackgroundType.Image;
    public string Url { get; set; } = string.Empty;
    public string? VideoUrl { get; set; }
    public string? Color { get; set; }
    public string? LocalPath { get; set; }
}

/// <summary>
/// Type of background
/// </summary>
public enum BackgroundType
{
    Image,
    Video,
    Color
}
