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
    private readonly string _iconCacheDir;

    public BackgroundService(GameService gameService, SettingsService settingsService)
    {
        _gameService = gameService;
        _settingsService = settingsService;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Linlapse/1.0");
        _backgroundCacheDir = Path.Combine(SettingsService.GetCacheDirectory(), "backgrounds");
        _iconCacheDir = Path.Combine(SettingsService.GetCacheDirectory(), "icons");
        Directory.CreateDirectory(_backgroundCacheDir);
        Directory.CreateDirectory(_iconCacheDir);
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

        // Determine file extension from URL or type
        var extension = GetFileExtension(backgroundInfo.Url, backgroundInfo.Type);
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
            Log.Information("Downloaded background for {GameId}: {Path} ({Type})", 
                gameId, cachePath, backgroundInfo.Type);
            return cachePath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download background for {GameId}", gameId);
            // Return cached version if download fails
            return File.Exists(cachePath) ? cachePath : null;
        }
    }

    private static string GetFileExtension(string url, BackgroundType type)
    {
        // Try to get extension from URL
        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            
            if (!string.IsNullOrEmpty(ext))
            {
                return ext; // .webm, .webp, .mp4, .jpg, etc.
            }
        }
        catch { }

        // Fallback based on type
        return type == BackgroundType.Video ? ".webm" : ".webp";
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

    /// <summary>
    /// Get and cache game icon from getGames API
    /// </summary>
    public async Task<string?> GetCachedGameIconAsync(string gameId, CancellationToken cancellationToken = default)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null)
        {
            Log.Warning("Game not found for icon: {GameId}", gameId);
            return null;
        }

        // Get icon URL from the getGames API
        var iconUrl = await GetGameIconUrlAsync(game, cancellationToken);
        if (string.IsNullOrEmpty(iconUrl))
        {
            Log.Debug("No icon URL found for {GameId}", gameId);
            return null;
        }

        // Determine file extension from URL
        var extension = ".png";
        try
        {
            var uri = new Uri(iconUrl);
            var ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            if (!string.IsNullOrEmpty(ext))
            {
                extension = ext;
            }
        }
        catch { }

        var cacheFileName = $"{gameId}_icon{extension}";
        var cachePath = Path.Combine(_iconCacheDir, cacheFileName);

        // Check if we have a cached version
        if (File.Exists(cachePath))
        {
            var fileInfo = new FileInfo(cachePath);
            // Use cached file if less than 7 days old
            if ((DateTime.UtcNow - fileInfo.LastWriteTimeUtc).TotalDays < 7)
            {
                return cachePath;
            }
        }

        // Download the icon
        try
        {
            var bytes = await _httpClient.GetByteArrayAsync(iconUrl, cancellationToken);
            await File.WriteAllBytesAsync(cachePath, bytes, cancellationToken);
            Log.Information("Downloaded icon for {GameId}: {Path}", gameId, cachePath);
            return cachePath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download icon for {GameId}", gameId);
            // Return cached version if download fails
            return File.Exists(cachePath) ? cachePath : null;
        }
    }

    /// <summary>
    /// Get game icon URL from getGames API
    /// </summary>
    private async Task<string?> GetGameIconUrlAsync(GameInfo game, CancellationToken cancellationToken = default)
    {
        try
        {
            var apiUrl = GetGamesApiUrl(game);
            if (string.IsNullOrEmpty(apiUrl))
            {
                return null;
            }

            var response = await _httpClient.GetStringAsync(apiUrl, cancellationToken);
            return ParseGameIconUrl(game, response);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to get icon URL for {GameId}", game.Id);
            return null;
        }
    }

    /// <summary>
    /// Get the getGames API URL for the game's region
    /// </summary>
    private string? GetGamesApiUrl(GameInfo game)
    {
        return game.Region switch
        {
            GameRegion.Global => "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getGames?launcher_id=VYTpXlbWo8&language=en-us",
            GameRegion.China => "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGames?launcher_id=jGHBHlcOq1&language=zh-cn",
            GameRegion.SEA => "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getGames?launcher_id=VYTpXlbWo8&language=en-us",
            _ => null
        };
    }

    /// <summary>
    /// Parse game icon URL from getGames API response
    /// </summary>
    private string? ParseGameIconUrl(GameInfo game, string response)
    {
        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
                return null;

            if (!data.TryGetProperty("games", out var games) || games.ValueKind != JsonValueKind.Array)
                return null;

            var gameBiz = GetGameBiz(game);
            if (gameBiz == null)
                return null;

            foreach (var gameEntry in games.EnumerateArray())
            {
                if (!gameEntry.TryGetProperty("biz", out var biz))
                    continue;

                if (biz.GetString() != gameBiz)
                    continue;

                // Found matching game, get icon from display.icon.url
                if (gameEntry.TryGetProperty("display", out var display) && display.ValueKind == JsonValueKind.Object)
                {
                    if (display.TryGetProperty("icon", out var icon) && icon.ValueKind == JsonValueKind.Object)
                    {
                        if (icon.TryGetProperty("url", out var iconUrl))
                        {
                            var url = iconUrl.GetString();
                            if (!string.IsNullOrEmpty(url))
                            {
                                Log.Debug("Found icon URL for {GameBiz}: {Url}", gameBiz, url);
                                return url;
                            }
                        }
                    }
                }
                break;
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse icon URL for {GameId}", game.Id);
            return null;
        }
    }

    private string? GetBackgroundApiUrl(GameInfo game)
    {
        // Use getAllGameBasicInfo API which returns backgrounds including video backgrounds
        return game.Region switch
        {
            GameRegion.Global => "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getAllGameBasicInfo?launcher_id=VYTpXlbWo8&language=en-us",
            GameRegion.China => "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getAllGameBasicInfo?launcher_id=jGHBHlcOq1&language=zh-cn",
            GameRegion.SEA => "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getAllGameBasicInfo?launcher_id=VYTpXlbWo8&language=en-us",
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

            var gameBiz = GetGameBiz(game);
            if (gameBiz == null)
                return null;

            // getAllGameBasicInfo API format: data.game_info_list[] with game.biz and backgrounds[]
            if (data.TryGetProperty("game_info_list", out var gameInfoList) && gameInfoList.ValueKind == JsonValueKind.Array)
            {
                foreach (var gameInfo in gameInfoList.EnumerateArray())
                {
                    // Match by game.biz identifier
                    if (!gameInfo.TryGetProperty("game", out var gameObj) || gameObj.ValueKind != JsonValueKind.Object)
                        continue;

                    if (!gameObj.TryGetProperty("biz", out var biz))
                        continue;

                    var bizStr = biz.GetString();
                    if (bizStr != gameBiz)
                        continue;

                    // Found matching game, get icon
                    if (gameObj.TryGetProperty("icon", out var iconObj) && iconObj.ValueKind == JsonValueKind.Object)
                    {
                        if (iconObj.TryGetProperty("url", out var iconUrl))
                        {
                            backgroundInfo.IconUrl = iconUrl.GetString();
                        }
                    }

                    // Found matching game, get backgrounds array
                    if (gameInfo.TryGetProperty("backgrounds", out var backgrounds) && 
                        backgrounds.ValueKind == JsonValueKind.Array && backgrounds.GetArrayLength() > 0)
                    {
                        // Get the first background (usually the current/featured one)
                        var firstBg = backgrounds[0];

                        // Check background type - prefer video if available
                        var bgType = "BACKGROUND_TYPE_UNSPECIFIED";
                        if (firstBg.TryGetProperty("type", out var typeElement))
                        {
                            bgType = typeElement.GetString() ?? "BACKGROUND_TYPE_UNSPECIFIED";
                        }

                        // Get video URL if it's a video background
                        if (bgType == "BACKGROUND_TYPE_VIDEO" && 
                            firstBg.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.Object)
                        {
                            if (video.TryGetProperty("url", out var videoUrl))
                            {
                                var videoUrlStr = videoUrl.GetString();
                                if (!string.IsNullOrEmpty(videoUrlStr))
                                {
                                    backgroundInfo.VideoUrl = videoUrlStr;
                                    backgroundInfo.Type = BackgroundType.Video;
                                    backgroundInfo.Url = videoUrlStr;
                                }
                            }
                        }

                        // Get static background image (fallback or for image-only backgrounds)
                        if (firstBg.TryGetProperty("background", out var background) && background.ValueKind == JsonValueKind.Object)
                        {
                            if (background.TryGetProperty("url", out var bgUrl))
                            {
                                var bgUrlStr = bgUrl.GetString();
                                if (!string.IsNullOrEmpty(bgUrlStr))
                                {
                                    // If we already have a video, keep the image as fallback
                                    if (backgroundInfo.Type == BackgroundType.Video)
                                    {
                                        backgroundInfo.FallbackUrl = bgUrlStr;
                                    }
                                    else
                                    {
                                        backgroundInfo.Url = bgUrlStr;
                                        backgroundInfo.Type = BackgroundType.Image;
                                    }
                                }
                            }
                        }
                    }

                    break;
                }
            }

            // Fallback: getGames API format (data.games[] with display.background)
            if (string.IsNullOrEmpty(backgroundInfo.Url) && 
                data.TryGetProperty("games", out var games) && games.ValueKind == JsonValueKind.Array)
            {
                foreach (var gameEntry in games.EnumerateArray())
                {
                    if (!gameEntry.TryGetProperty("biz", out var biz))
                        continue;

                    if (biz.GetString() != gameBiz)
                        continue;

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
                    }
                    break;
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
    public string? FallbackUrl { get; set; }
    public string? Color { get; set; }
    public string? LocalPath { get; set; }
    public string? IconUrl { get; set; }
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
