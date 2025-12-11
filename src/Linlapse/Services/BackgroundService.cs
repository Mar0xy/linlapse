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
    private readonly GameConfigurationService _configurationService;
    private readonly string _backgroundCacheDir;
    private readonly string _iconCacheDir;
    private readonly string _themeCacheDir;

    public BackgroundService(GameService gameService, SettingsService settingsService, GameConfigurationService configurationService)
    {
        _gameService = gameService;
        _settingsService = settingsService;
        _configurationService = configurationService;
        
        // Configure HttpClient with automatic decompression for gzip/deflate responses
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        };
        _httpClient = new HttpClient(handler);
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Linlapse/1.0");
        
        _backgroundCacheDir = Path.Combine(SettingsService.GetCacheDirectory(), "backgrounds");
        _iconCacheDir = Path.Combine(SettingsService.GetCacheDirectory(), "icons");
        _themeCacheDir = Path.Combine(SettingsService.GetCacheDirectory(), "themes");
        Directory.CreateDirectory(_backgroundCacheDir);
        Directory.CreateDirectory(_iconCacheDir);
        Directory.CreateDirectory(_themeCacheDir);
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
            var backgroundInfo = await ParseBackgroundResponseAsync(game, response, cancellationToken);

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
    /// Download and cache the theme image for a game (overlaid on video backgrounds)
    /// </summary>
    public async Task<string?> GetCachedThemeImageAsync(string gameId, CancellationToken cancellationToken = default)
    {
        var backgroundInfo = await GetBackgroundInfoAsync(gameId, cancellationToken);
        if (backgroundInfo == null || string.IsNullOrEmpty(backgroundInfo.ThemeUrl))
        {
            return null;
        }

        // Determine file extension from URL
        var extension = GetFileExtension(backgroundInfo.ThemeUrl, BackgroundType.Image);
        var cacheFileName = $"{gameId}_theme{extension}";
        var cachePath = Path.Combine(_themeCacheDir, cacheFileName);

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

        // Download the theme image
        try
        {
            var bytes = await _httpClient.GetByteArrayAsync(backgroundInfo.ThemeUrl, cancellationToken);
            await File.WriteAllBytesAsync(cachePath, bytes, cancellationToken);
            Log.Information("Downloaded theme image for {GameId}: {Path}", gameId, cachePath);
            return cachePath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download theme image for {GameId}", gameId);
            // Return cached version if download fails
            return File.Exists(cachePath) ? cachePath : null;
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
            if (Directory.Exists(_themeCacheDir))
            {
                foreach (var file in Directory.GetFiles(_themeCacheDir))
                {
                    File.Delete(file);
                }
                Log.Information("Cleared theme cache");
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

            // Check if this is a direct image URL (e.g., for Kuro games)
            // Direct URLs typically end with image extensions or contain image hosting domains
            if (apiUrl.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                apiUrl.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                apiUrl.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                apiUrl.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
                apiUrl.Contains("imgur.com") ||
                apiUrl.Contains("i.imgur.com"))
            {
                // Direct image URL - return as-is
                Log.Debug("Using direct icon URL for {GameId}: {Url}", game.Id, apiUrl);
                return apiUrl;
            }

            // API endpoint - fetch and parse response
            var response = await _httpClient.GetStringAsync(apiUrl, cancellationToken);
            return ParseGameIconUrl(game, response);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to get icon URL for {GameId}", game.Id);
            return null;
        }
    }

    private string? GetGamesApiUrl(GameInfo game)
    {
        var config = _configurationService.GetConfiguration(game.Id);
        return config?.IconApiUrl;
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
        var config = _configurationService.GetConfiguration(game.Id);
        return config?.BackgroundApiUrl;
    }

    private string? GetIconApiUrl(GameInfo game)
    {
        var config = _configurationService.GetConfiguration(game.Id);
        return config?.IconApiUrl;
    }

    private string? GetGameBiz(GameInfo game)
    {
        var config = _configurationService.GetConfiguration(game.Id);
        return config?.GameBizIdentifier;
    }

    private async Task<BackgroundInfo?> ParseBackgroundResponseAsync(GameInfo game, string response, CancellationToken cancellationToken = default)
    {
        var config = _configurationService.GetConfiguration(game.Id);
        if (config?.BackgroundParser == null)
        {
            Log.Debug("No background parser configured for {GameId}", game.Id);
            return null;
        }

        var parser = config.BackgroundParser;
        
        if (parser.ParserType == BackgroundParserType.None)
        {
            return null;
        }

        try
        {
            // Handle Kuro Games API format (requires two-step fetching)
            if (parser.ParserType == BackgroundParserType.Kuro)
            {
                return await ParseKuroBackgroundResponseAsync(game, response, config, parser, cancellationToken);
            }

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            // Navigate to data root
            if (!string.IsNullOrEmpty(parser.DataRootPath) && 
                !root.TryGetProperty(parser.DataRootPath, out root))
            {
                return null;
            }

            var backgroundInfo = new BackgroundInfo
            {
                GameId = game.Id
            };

            var gameBiz = config.GameBizIdentifier;
            if (gameBiz == null)
            {
                return null;
            }

            // Navigate to game list
            if (!string.IsNullOrEmpty(parser.GameListPath) &&
                root.TryGetProperty(parser.GameListPath, out var gameList) && 
                gameList.ValueKind == JsonValueKind.Array)
            {
                // Find the matching game in the list
                foreach (var gameEntry in gameList.EnumerateArray())
                {
                    // Try to match by game identifier
                    if (!TryGetNestedProperty(gameEntry, parser.GameIdentifierField, out var bizValue))
                        continue;

                    if (bizValue.GetString() != gameBiz)
                        continue;

                    // Found matching game - extract background info
                    ExtractBackgroundUrls(gameEntry, backgroundInfo, parser);
                    break;
                }
            }

            return backgroundInfo.Url != null ? backgroundInfo : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse background response for {GameId}", game.Id);
            return null;
        }
    }

    private async Task<BackgroundInfo?> ParseKuroBackgroundResponseAsync(GameInfo game, string response, GameConfiguration config, BackgroundParserConfig parser, CancellationToken cancellationToken = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            // Navigate to functionCode
            if (!string.IsNullOrEmpty(parser.DataRootPath) && 
                !root.TryGetProperty(parser.DataRootPath, out root))
            {
                Log.Warning("No {Path} found in Kuro launcher response for {GameId}", parser.DataRootPath, game.Id);
                return null;
            }

            // Get the background string
            if (!root.TryGetProperty(parser.BackgroundsArrayPath, out var backgroundString))
            {
                Log.Warning("No {Path} found in functionCode for {GameId}", parser.BackgroundsArrayPath, game.Id);
                return null;
            }

            var backgroundId = backgroundString.GetString();
            if (string.IsNullOrEmpty(backgroundId))
            {
                Log.Warning("Empty background string for {GameId}", game.Id);
                return null;
            }

            // Construct the background metadata URL
            if (!config.ApiEndpoints.TryGetValue("background_base_url", out var baseUrl))
            {
                Log.Warning("No background_base_url configured for {GameId}", game.Id);
                return null;
            }

            var metadataUrl = $"{baseUrl}{backgroundId}/en.json";
            Log.Debug("Fetching Kuro background metadata from: {Url}", metadataUrl);

            // Fetch the background metadata
            var metadataResponse = await _httpClient.GetStringAsync(metadataUrl, cancellationToken);
            using var metadataDoc = JsonDocument.Parse(metadataResponse);
            var metadata = metadataDoc.RootElement;

            var backgroundInfo = new BackgroundInfo
            {
                GameId = game.Id
            };

            // Extract slogan (theme image) - Kuro API returns full URLs
            if (parser.UrlFields.TryGetValue("slogan", out var sloganPath) &&
                metadata.TryGetProperty(sloganPath, out var sloganUrl))
            {
                var sloganUrlStr = sloganUrl.GetString();
                if (!string.IsNullOrEmpty(sloganUrlStr))
                {
                    // URL is already complete, no need to construct
                    backgroundInfo.ThemeUrl = sloganUrlStr;
                }
            }

            // Extract background file (can be video or image) - Kuro API returns full URLs
            if (parser.UrlFields.TryGetValue("backgroundFile", out var bgPath) &&
                metadata.TryGetProperty(bgPath, out var bgFile))
            {
                var bgFileStr = bgFile.GetString();
                if (!string.IsNullOrEmpty(bgFileStr))
                {
                    // URL is already complete, no need to construct
                    backgroundInfo.Url = bgFileStr;
                    
                    // Determine type from file extension
                    var ext = Path.GetExtension(bgFileStr).ToLowerInvariant();
                    if (ext == ".mp4" || ext == ".webm")
                    {
                        backgroundInfo.Type = BackgroundType.Video;
                        backgroundInfo.VideoUrl = bgFileStr;
                    }
                    else
                    {
                        backgroundInfo.Type = BackgroundType.Image;
                    }
                }
            }

            return backgroundInfo.Url != null ? backgroundInfo : null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to parse Kuro background response for {GameId}", game.Id);
            return null;
        }
    }

    private void ExtractBackgroundUrls(JsonElement gameEntry, BackgroundInfo backgroundInfo, BackgroundParserConfig parser)
    {
        // Get backgrounds array if configured
        if (!string.IsNullOrEmpty(parser.BackgroundsArrayPath) &&
            TryGetNestedProperty(gameEntry, parser.BackgroundsArrayPath, out var backgrounds) &&
            backgrounds.ValueKind == JsonValueKind.Array && backgrounds.GetArrayLength() > 0)
        {
            var firstBg = backgrounds[0];

            // Extract video URL
            if (parser.UrlFields.TryGetValue("video", out var videoPath) &&
                TryGetNestedProperty(firstBg, videoPath, out var videoUrl))
            {
                var videoUrlStr = videoUrl.GetString();
                if (!string.IsNullOrEmpty(videoUrlStr))
                {
                    backgroundInfo.VideoUrl = videoUrlStr;
                    backgroundInfo.Type = BackgroundType.Video;
                    backgroundInfo.Url = videoUrlStr;
                }
            }

            // Extract image URL
            if (parser.UrlFields.TryGetValue("image", out var imagePath) &&
                TryGetNestedProperty(firstBg, imagePath, out var imageUrl))
            {
                var imageUrlStr = imageUrl.GetString();
                if (!string.IsNullOrEmpty(imageUrlStr))
                {
                    if (backgroundInfo.Type == BackgroundType.Video)
                    {
                        backgroundInfo.FallbackUrl = imageUrlStr;
                    }
                    else
                    {
                        backgroundInfo.Url = imageUrlStr;
                        backgroundInfo.Type = BackgroundType.Image;
                    }
                }
            }

            // Extract theme URL
            if (parser.UrlFields.TryGetValue("theme", out var themePath) &&
                TryGetNestedProperty(firstBg, themePath, out var themeUrl))
            {
                var themeUrlStr = themeUrl.GetString();
                if (!string.IsNullOrEmpty(themeUrlStr))
                {
                    backgroundInfo.ThemeUrl = themeUrlStr;
                }
            }
        }

        // Extract icon URL from game object
        if (parser.UrlFields.TryGetValue("icon", out var iconPath) &&
            TryGetNestedProperty(gameEntry, iconPath, out var iconUrl))
        {
            backgroundInfo.IconUrl = iconUrl.GetString();
        }
    }

    /// <summary>
    /// Try to get a nested property from a JSON element using dot notation (e.g., "game.icon.url")
    /// </summary>
    private bool TryGetNestedProperty(JsonElement element, string path, out JsonElement value)
    {
        value = element;
        var parts = path.Split('.');
        
        foreach (var part in parts)
        {
            if (!value.TryGetProperty(part, out value))
            {
                return false;
            }
        }
        
        return true;
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
    public string? ThemeUrl { get; set; }
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
