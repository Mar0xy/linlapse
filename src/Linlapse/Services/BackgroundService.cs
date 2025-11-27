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
        // These APIs return launcher content including backgrounds
        return game.GameType switch
        {
            GameType.HonkaiImpact3rd => game.Region switch
            {
                GameRegion.Global => "https://sdk-os-static.mihoyo.com/bh3_global/mdk/launcher/api/content?key=dpz65xJ3&launcher_id=10",
                GameRegion.SEA => "https://sdk-os-static.mihoyo.com/bh3_global/mdk/launcher/api/content?key=tEGNtVhN&launcher_id=9",
                _ => null
            },
            GameType.GenshinImpact => game.Region switch
            {
                GameRegion.Global => "https://sdk-os-static.mihoyo.com/hk4e_global/mdk/launcher/api/content?key=gcStgarh&launcher_id=10",
                GameRegion.China => "https://sdk-static.mihoyo.com/hk4e_cn/mdk/launcher/api/content?key=eYd89JmJ&launcher_id=18",
                _ => null
            },
            GameType.HonkaiStarRail => game.Region switch
            {
                GameRegion.Global => "https://hkrpg-launcher-static.hoyoverse.com/hkrpg_global/mdk/launcher/api/content?key=vplOVX8Vn7cwG8yb&launcher_id=35",
                GameRegion.China => "https://api-launcher.mihoyo.com/hkrpg_cn/mdk/launcher/api/content?key=6KcVuOkbcqjJomjZ&launcher_id=33",
                _ => null
            },
            GameType.ZenlessZoneZero => game.Region switch
            {
                GameRegion.Global => "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getGameContent?launcher_id=VYTpXlbWo8&game_id=x6znKlJ0xK",
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

            if (!root.TryGetProperty("data", out var data))
                return null;

            var backgroundInfo = new BackgroundInfo
            {
                GameId = game.Id
            };

            // Try to find background in different response formats

            // Format 1: adv -> background
            if (data.TryGetProperty("adv", out var adv) && adv.ValueKind == JsonValueKind.Object)
            {
                if (adv.TryGetProperty("background", out var bg))
                {
                    backgroundInfo.Url = bg.GetString() ?? "";
                    backgroundInfo.Type = BackgroundType.Image;
                }

                // Check for video background (usually in bg_checksum or separate field)
                if (adv.TryGetProperty("bg_checksum", out _))
                {
                    // Has video background capability
                }
            }

            // Format 2: backgrounds array
            if (data.TryGetProperty("backgrounds", out var backgrounds) && 
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

                // Check if it's a video
                if (firstBg.TryGetProperty("video", out var video) && !string.IsNullOrEmpty(video.GetString()))
                {
                    backgroundInfo.VideoUrl = video.GetString();
                    backgroundInfo.Type = BackgroundType.Video;
                }
            }

            // Format 3: content -> backgrounds
            if (data.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object)
            {
                if (content.TryGetProperty("backgrounds", out var contentBgs) && 
                    contentBgs.ValueKind == JsonValueKind.Array && contentBgs.GetArrayLength() > 0)
                {
                    var firstBg = contentBgs[0];

                    if (firstBg.TryGetProperty("background", out var bgImg) && bgImg.ValueKind != JsonValueKind.Null)
                    {
                        backgroundInfo.Url = bgImg.ValueKind == JsonValueKind.Object && bgImg.TryGetProperty("url", out var imgUrl)
                            ? imgUrl.GetString() ?? ""
                            : bgImg.GetString() ?? "";
                    }

                    if (firstBg.TryGetProperty("video", out var videoObj) && videoObj.ValueKind != JsonValueKind.Null)
                    {
                        var videoUrl = videoObj.ValueKind == JsonValueKind.Object && videoObj.TryGetProperty("url", out var vUrl)
                            ? vUrl.GetString()
                            : videoObj.GetString();

                        if (!string.IsNullOrEmpty(videoUrl))
                        {
                            backgroundInfo.VideoUrl = videoUrl;
                            backgroundInfo.Type = BackgroundType.Video;
                        }
                    }
                }
            }

            // Format 4: game_content -> backgrounds (ZZZ style)
            if (data.TryGetProperty("game_content", out var gameContent) && gameContent.ValueKind == JsonValueKind.Object)
            {
                if (gameContent.TryGetProperty("backgrounds", out var gcBgs) && 
                    gcBgs.ValueKind == JsonValueKind.Array && gcBgs.GetArrayLength() > 0)
                {
                    var firstBg = gcBgs[0];

                    if (firstBg.TryGetProperty("background", out var bgData) && bgData.ValueKind != JsonValueKind.Null)
                    {
                        backgroundInfo.Url = bgData.ValueKind == JsonValueKind.Object && bgData.TryGetProperty("url", out var bUrl)
                            ? bUrl.GetString() ?? ""
                            : "";
                    }
                    else if (firstBg.TryGetProperty("url", out var directUrl))
                    {
                        backgroundInfo.Url = directUrl.GetString() ?? "";
                    }

                    if (firstBg.TryGetProperty("video", out var videoData) && videoData.ValueKind != JsonValueKind.Null)
                    {
                        var videoUrl = videoData.ValueKind == JsonValueKind.Object && videoData.TryGetProperty("url", out var vdUrl)
                            ? vdUrl.GetString()
                            : "";

                        if (!string.IsNullOrEmpty(videoUrl))
                        {
                            backgroundInfo.VideoUrl = videoUrl;
                            backgroundInfo.Type = BackgroundType.Video;
                        }
                    }
                }
            }

            // Format 5: icon (fallback to game icon as background if no bg found)
            if (string.IsNullOrEmpty(backgroundInfo.Url) && data.TryGetProperty("icon", out var icon) && icon.ValueKind == JsonValueKind.String)
            {
                backgroundInfo.Url = icon.GetString() ?? "";
                backgroundInfo.Type = BackgroundType.Image;
            }

            // Prefer video URL if available
            if (backgroundInfo.Type == BackgroundType.Video && !string.IsNullOrEmpty(backgroundInfo.VideoUrl))
            {
                backgroundInfo.Url = backgroundInfo.VideoUrl;
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
