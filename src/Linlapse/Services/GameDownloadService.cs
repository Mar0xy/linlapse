using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Linlapse.Models;
using Serilog;

namespace Linlapse.Services;

/// <summary>
/// Service for downloading and installing games from official sources.
/// Uses Sophon downloads when available for supported games (Genshin Impact, ZZZ),
/// otherwise falls back to traditional archive-based downloads.
/// </summary>
public partial class GameDownloadService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly GameService _gameService;
    private readonly DownloadService _downloadService;
    private readonly InstallationService _installationService;
    private readonly SettingsService _settingsService;
    private SophonDownloadService? _sophonDownloadService;

    // Regex for detecting multi-part archive extensions (e.g., .001, .002, .0001, etc.)
    [GeneratedRegex(@"^\.(\d+)$", RegexOptions.Compiled)]
    private static partial Regex MultiPartExtensionRegex();

    public event EventHandler<GameDownloadInfo>? DownloadInfoRetrieved;
    public event EventHandler<GameDownloadProgress>? DownloadProgressChanged;
    public event EventHandler<string>? GameInstallCompleted;
    public event EventHandler<(string GameId, Exception Error)>? GameInstallFailed;

    public GameDownloadService(
        GameService gameService,
        DownloadService downloadService,
        InstallationService installationService,
        SettingsService settingsService)
    {
        _gameService = gameService;
        _downloadService = downloadService;
        _installationService = installationService;
        _settingsService = settingsService;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Linlapse/1.0");
    }
    
    /// <summary>
    /// Gets or creates the SophonDownloadService instance
    /// </summary>
    private SophonDownloadService GetSophonService()
    {
        return _sophonDownloadService ??= new SophonDownloadService(_settingsService, _gameService);
    }

    /// <summary>
    /// Get download information for a game
    /// </summary>
    public async Task<GameDownloadInfo?> GetGameDownloadInfoAsync(string gameId, CancellationToken cancellationToken = default)
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
                Log.Warning("No API URL for game: {GameId}", gameId);
                return null;
            }

            var response = await _httpClient.GetStringAsync(apiUrl, cancellationToken);
            var downloadInfo = ParseDownloadResponse(game, response);

            if (downloadInfo != null)
            {
                DownloadInfoRetrieved?.Invoke(this, downloadInfo);
                Log.Information("Retrieved download info for {GameId}: {Version}, {Size} bytes",
                    gameId, downloadInfo.Version, downloadInfo.TotalSize);
            }

            return downloadInfo;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get download info for {GameId}", gameId);
            return null;
        }
    }

    /// <summary>
    /// Download and install a game. Uses Sophon for supported games (Genshin Impact, ZZZ)
    /// for faster, more efficient chunk-based downloads. Falls back to traditional archive
    /// downloads for other games.
    /// </summary>
    public async Task<bool> DownloadAndInstallGameAsync(
        string gameId,
        string? installPath = null,
        IProgress<GameDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null)
        {
            Log.Error("Game not found: {GameId}", gameId);
            return false;
        }

        // Use default install path if not specified
        installPath ??= Path.Combine(
            _settingsService.Settings.DefaultGameInstallPath ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games"),
            game.Name);

        // Try Sophon download for supported games (Genshin Impact, ZZZ)
        if (SophonDownloadService.SupportsSophon(game))
        {
            Log.Information("Attempting Sophon download for {GameId}", gameId);
            try
            {
                var sophonService = GetSophonService();
                
                // Wrap Sophon progress into GameDownloadProgress
                var sophonProgress = new Progress<SophonDownloadProgress>(sp =>
                {
                    var gameProgress = new GameDownloadProgress
                    {
                        GameId = sp.GameId,
                        State = sp.State switch
                        {
                            SophonDownloadState.FetchingInfo => GameDownloadState.FetchingInfo,
                            SophonDownloadState.Downloading => GameDownloadState.Downloading,
                            SophonDownloadState.Completed => GameDownloadState.Completed,
                            SophonDownloadState.Failed => GameDownloadState.Failed,
                            SophonDownloadState.Cancelled => GameDownloadState.Cancelled,
                            _ => GameDownloadState.Downloading
                        },
                        TotalSize = sp.TotalSize,
                        DownloadedBytes = sp.DownloadedBytes,
                        TotalFiles = sp.TotalFiles,
                        ExtractedFiles = sp.ProcessedFiles,
                        CurrentFile = sp.CurrentFile,
                        ErrorMessage = sp.ErrorMessage
                    };
                    progress?.Report(gameProgress);
                    DownloadProgressChanged?.Invoke(this, gameProgress);
                });
                
                var sophonResult = await sophonService.DownloadAndInstallGameAsync(
                    gameId,
                    installPath,
                    sophonProgress,
                    cancellationToken);
                
                if (sophonResult)
                {
                    Log.Information("Sophon download completed successfully for {GameId}", gameId);
                    GameInstallCompleted?.Invoke(this, gameId);
                    return true;
                }
                
                Log.Warning("Sophon download returned false for {GameId}, falling back to traditional download", gameId);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Sophon download failed for {GameId}, falling back to traditional download", gameId);
            }
        }
        
        // Fall back to traditional archive-based download
        return await DownloadAndInstallGameTraditionalAsync(gameId, installPath, progress, cancellationToken);
    }
    
    /// <summary>
    /// Traditional download and install using archive files
    /// </summary>
    private async Task<bool> DownloadAndInstallGameTraditionalAsync(
        string gameId,
        string installPath,
        IProgress<GameDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null)
        {
            Log.Error("Game not found: {GameId}", gameId);
            return false;
        }

        var downloadProgress = new GameDownloadProgress
        {
            GameId = gameId,
            State = GameDownloadState.FetchingInfo
        };

        try
        {
            _gameService.UpdateGameState(gameId, GameState.Installing);
            progress?.Report(downloadProgress);

            // Get download info
            var downloadInfo = await GetGameDownloadInfoAsync(gameId, cancellationToken);
            if (downloadInfo == null || string.IsNullOrEmpty(downloadInfo.DownloadUrl))
            {
                throw new Exception("Failed to retrieve download information");
            }

            downloadProgress.TotalSize = downloadInfo.TotalSize;
            downloadProgress.Version = downloadInfo.Version;

            // Create temp directory for download
            var tempDir = Path.Combine(SettingsService.GetCacheDirectory(), "downloads", gameId);
            Directory.CreateDirectory(tempDir);

            // Download game package(s)
            downloadProgress.State = GameDownloadState.Downloading;
            progress?.Report(downloadProgress);
            DownloadProgressChanged?.Invoke(this, downloadProgress);

            var downloadedFiles = new List<string>();
            long totalDownloaded = 0;

            // Download all package segments (supports split downloads)
            var segments = downloadInfo.PackageSegments.Count > 0 
                ? downloadInfo.PackageSegments 
                : new List<GamePackageSegment> 
                { 
                    new() 
                    { 
                        DownloadUrl = downloadInfo.DownloadUrl, 
                        Size = downloadInfo.TotalSize, 
                        Md5 = downloadInfo.PackageMd5,
                        PartNumber = 1 
                    } 
                };

            downloadProgress.TotalFiles = segments.Count;
            Log.Information("Downloading {Count} package segment(s) for {GameId}", segments.Count, gameId);

            for (int i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                downloadProgress.CurrentFile = $"Part {i + 1} of {segments.Count}";
                
                // Extract original filename from URL to preserve proper naming for split archive extraction
                var uri = new Uri(segment.DownloadUrl);
                var originalFileName = Path.GetFileName(uri.AbsolutePath);
                if (string.IsNullOrEmpty(originalFileName))
                {
                    // Fallback to generic naming if URL doesn't contain filename
                    var extension = Path.GetExtension(uri.AbsolutePath);
                    if (string.IsNullOrEmpty(extension)) extension = ".zip";
                    originalFileName = $"game_package_part{segment.PartNumber}{extension}";
                }

                var segmentPath = Path.Combine(tempDir, originalFileName);

                var segmentProgress = new Progress<DownloadProgress>(dp =>
                {
                    downloadProgress.DownloadedBytes = totalDownloaded + dp.BytesDownloaded;
                    downloadProgress.SpeedBytesPerSecond = dp.SpeedBytesPerSecond;
                    downloadProgress.EstimatedTimeRemaining = dp.EstimatedTimeRemaining;
                    progress?.Report(downloadProgress);
                    DownloadProgressChanged?.Invoke(this, downloadProgress);
                });

                // Check if segment already exists and is valid (for resume support)
                bool segmentAlreadyComplete = false;
                if (File.Exists(segmentPath) && !string.IsNullOrEmpty(segment.Md5))
                {
                    var existingFileInfo = new FileInfo(segmentPath);
                    if (existingFileInfo.Length == segment.Size)
                    {
                        var isValid = await _downloadService.VerifyFileHashAsync(
                            segmentPath,
                            segment.Md5,
                            System.Security.Cryptography.HashAlgorithmName.MD5);
                        
                        if (isValid)
                        {
                            Log.Information("Segment {Part}/{Total} already downloaded and verified, skipping", 
                                segment.PartNumber, segments.Count);
                            segmentAlreadyComplete = true;
                        }
                    }
                }

                if (!segmentAlreadyComplete)
                {
                    Log.Information("Downloading segment {Part}/{Total}: {Url}", 
                        segment.PartNumber, segments.Count, segment.DownloadUrl);

                    var segmentSuccess = await _downloadService.DownloadFileAsync(
                        segment.DownloadUrl,
                        segmentPath,
                        segmentProgress,
                        cancellationToken);

                    if (!segmentSuccess)
                    {
                        throw new Exception($"Failed to download game package segment {segment.PartNumber}");
                    }

                    // Verify segment if MD5 is available
                    if (!string.IsNullOrEmpty(segment.Md5))
                    {
                        var isValid = await _downloadService.VerifyFileHashAsync(
                            segmentPath,
                            segment.Md5,
                            System.Security.Cryptography.HashAlgorithmName.MD5);

                        if (!isValid)
                        {
                            throw new Exception($"Downloaded segment {segment.PartNumber} verification failed - file may be corrupted");
                        }
                        Log.Debug("Segment {Part} MD5 verified successfully", segment.PartNumber);
                    }
                }

                downloadedFiles.Add(segmentPath);
                totalDownloaded += segment.Size;
            }

            // Download voice packs based on user's selected languages from settings
            if (downloadInfo.VoicePacks.Count > 0)
            {
                downloadProgress.State = GameDownloadState.DownloadingVoicePacks;
                progress?.Report(downloadProgress);

                // Get selected voice languages from settings
                var selectedLanguages = _settingsService.Settings.SelectedVoiceLanguages;
                if (selectedLanguages.Count == 0)
                {
                    selectedLanguages = new List<string> { "en-us" }; // Default to English
                }

                foreach (var selectedLang in selectedLanguages)
                {
                    // Find matching voice pack (case-insensitive, partial match)
                    var voicePack = downloadInfo.VoicePacks.FirstOrDefault(v =>
                        v.Language.Contains(selectedLang.Replace("-", ""), StringComparison.OrdinalIgnoreCase) ||
                        v.Language.Replace("-", "").Contains(selectedLang.Replace("-", ""), StringComparison.OrdinalIgnoreCase) ||
                        selectedLang.Replace("-", "").Contains(v.Language.Replace("-", ""), StringComparison.OrdinalIgnoreCase));

                    if (voicePack != null && !string.IsNullOrEmpty(voicePack.DownloadUrl))
                    {
                        Log.Information("Downloading voice pack: {Language}", voicePack.Language);
                        downloadProgress.CurrentFile = $"Voice: {voicePack.Language}";
                        progress?.Report(downloadProgress);

                        var voicePackPath = Path.Combine(tempDir, $"voice_{voicePack.Language}{Path.GetExtension(voicePack.DownloadUrl)}");

                        var voiceDownloadSuccess = await _downloadService.DownloadFileAsync(
                            voicePack.DownloadUrl,
                            voicePackPath,
                            cancellationToken: cancellationToken);

                        if (voiceDownloadSuccess)
                        {
                            downloadedFiles.Add(voicePackPath);
                            Log.Information("Voice pack downloaded: {Language}", voicePack.Language);
                        }
                        else
                        {
                            Log.Warning("Failed to download voice pack for {Language}", voicePack.Language);
                        }
                    }
                    else
                    {
                        Log.Debug("No voice pack found for language: {Language}", selectedLang);
                    }
                }
            }

            // Verification already done per-segment above
            downloadProgress.State = GameDownloadState.Verifying;
            progress?.Report(downloadProgress);

            // Extract/Install
            downloadProgress.State = GameDownloadState.Extracting;
            progress?.Report(downloadProgress);

            Directory.CreateDirectory(installPath);

            // Separate multi-part archives from regular archives
            // Multi-part archives have extensions like .zip.001, .zip.002, etc.
            var multiPartFirstFiles = new HashSet<string>();
            var regularArchives = new List<string>();

            foreach (var downloadedFile in downloadedFiles)
            {
                var extension = Path.GetExtension(downloadedFile).ToLowerInvariant();

                // Check if this is a multi-part archive segment (e.g., .zip.001, .7z.001, .zip.0001)
                var match = MultiPartExtensionRegex().Match(extension);
                if (match.Success)
                {
                    // This is a split archive part - only extract from the first part (.001 or .000)
                    // Some archive tools use .000 as the first part, others use .001
                    if (int.TryParse(match.Groups[1].Value, out var partNum) && partNum <= 1)
                    {
                        multiPartFirstFiles.Add(downloadedFile);
                    }
                    // Skip other parts (.002, .003, etc.) - 7z will find them automatically
                }
                else if (extension == ".zip" || extension == ".7z")
                {
                    regularArchives.Add(downloadedFile);
                }
            }

            // Extract multi-part archives (from their first segment)
            foreach (var firstPart in multiPartFirstFiles)
            {
                Log.Information("Extracting multi-part archive starting from: {File}", Path.GetFileName(firstPart));;
                
                var installProgress = new Progress<InstallProgress>(ip =>
                {
                    downloadProgress.ExtractedFiles = ip.ProcessedFiles;
                    downloadProgress.TotalFiles = ip.TotalFiles;
                    downloadProgress.CurrentFile = ip.CurrentFile;
                    progress?.Report(downloadProgress);
                });

                await _installationService.InstallFromArchiveAsync(
                    gameId,
                    firstPart,
                    installPath,
                    installProgress,
                    cancellationToken);
            }

            // Extract regular single-file archives
            foreach (var archiveFile in regularArchives)
            {
                var installProgress = new Progress<InstallProgress>(ip =>
                {
                    downloadProgress.ExtractedFiles = ip.ProcessedFiles;
                    downloadProgress.TotalFiles = ip.TotalFiles;
                    downloadProgress.CurrentFile = ip.CurrentFile;
                    progress?.Report(downloadProgress);
                });

                await _installationService.InstallFromArchiveAsync(
                    gameId,
                    archiveFile,
                    installPath,
                    installProgress,
                    cancellationToken);
            }

            // Update game info - UpdateGameInstallPath will set the state based on whether
            // the game executable exists
            _gameService.UpdateGameInstallPath(gameId, installPath);
            game.Version = downloadInfo.Version;

            // Cleanup temp files
            downloadProgress.State = GameDownloadState.Cleanup;
            progress?.Report(downloadProgress);

            foreach (var file in downloadedFiles)
            {
                try { File.Delete(file); } catch { }
            }

            downloadProgress.State = GameDownloadState.Completed;
            progress?.Report(downloadProgress);
            GameInstallCompleted?.Invoke(this, gameId);

            Log.Information("Game installed successfully: {GameId} at {Path}", gameId, installPath);
            return true;
        }
        catch (OperationCanceledException)
        {
            _gameService.UpdateGameState(gameId, GameState.NotInstalled);
            Log.Information("Game download cancelled: {GameId}", gameId);
            return false;
        }
        catch (Exception ex)
        {
            _gameService.UpdateGameState(gameId, GameState.NotInstalled);
            downloadProgress.State = GameDownloadState.Failed;
            downloadProgress.ErrorMessage = ex.Message;
            progress?.Report(downloadProgress);

            Log.Error(ex, "Game download failed: {GameId}", gameId);
            GameInstallFailed?.Invoke(this, (gameId, ex));
            return false;
        }
    }

    /// <summary>
    /// Download voice pack for an installed game
    /// </summary>
    public async Task<bool> DownloadVoicePackAsync(
        string gameId,
        string language,
        IProgress<GameDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null || !game.IsInstalled)
        {
            Log.Error("Game not found or not installed: {GameId}", gameId);
            return false;
        }

        try
        {
            var downloadInfo = await GetGameDownloadInfoAsync(gameId, cancellationToken);
            if (downloadInfo == null)
            {
                return false;
            }

            var voicePack = downloadInfo.VoicePacks.FirstOrDefault(v =>
                v.Language.Contains(language, StringComparison.OrdinalIgnoreCase));

            if (voicePack == null || string.IsNullOrEmpty(voicePack.DownloadUrl))
            {
                Log.Warning("Voice pack not found for language: {Language}", language);
                return false;
            }

            var downloadProgress = new GameDownloadProgress
            {
                GameId = gameId,
                State = GameDownloadState.DownloadingVoicePacks,
                TotalSize = voicePack.Size
            };

            var tempDir = Path.Combine(SettingsService.GetCacheDirectory(), "downloads", gameId);
            Directory.CreateDirectory(tempDir);
            var voicePackPath = Path.Combine(tempDir, $"voice_{language}.zip");

            var fileProgress = new Progress<DownloadProgress>(dp =>
            {
                downloadProgress.DownloadedBytes = dp.BytesDownloaded;
                downloadProgress.SpeedBytesPerSecond = dp.SpeedBytesPerSecond;
                progress?.Report(downloadProgress);
            });

            var success = await _downloadService.DownloadFileAsync(
                voicePack.DownloadUrl,
                voicePackPath,
                fileProgress,
                cancellationToken);

            if (!success)
            {
                return false;
            }

            // Extract voice pack to game directory
            downloadProgress.State = GameDownloadState.Extracting;
            progress?.Report(downloadProgress);

            await _installationService.InstallFromArchiveAsync(
                gameId,
                voicePackPath,
                game.InstallPath,
                cancellationToken: cancellationToken);

            // Cleanup
            try { File.Delete(voicePackPath); } catch { }

            Log.Information("Voice pack installed: {Language} for {GameId}", language, gameId);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download voice pack {Language} for {GameId}", language, gameId);
            return false;
        }
    }

    private string? GetGameApiUrl(GameInfo game)
    {
        // Use the new HoYoPlay API endpoints for game packages
        // These provide consistent download information across all games
        var launcherId = game.Region switch
        {
            GameRegion.Global => "VYTpXlbWo8",  // Global/OS launcher
            GameRegion.China => "jGHBHlcOq1",   // CN launcher
            GameRegion.SEA => "VYTpXlbWo8",    // SEA uses global
            _ => "VYTpXlbWo8"
        };

        var baseUrl = game.Region == GameRegion.China
            ? "https://hyp-api.mihoyo.com"
            : "https://sg-hyp-api.hoyoverse.com";

        return $"{baseUrl}/hyp/hyp-connect/api/getGamePackages?launcher_id={launcherId}";
    }

    private string GetGameBizFromType(GameInfo game)
    {
        return game.GameType switch
        {
            GameType.GenshinImpact => game.Region == GameRegion.China ? "hk4e_cn" : "hk4e_global",
            GameType.HonkaiStarRail => game.Region == GameRegion.China ? "hkrpg_cn" : "hkrpg_global",
            GameType.HonkaiImpact3rd => game.Region == GameRegion.China ? "bh3_cn" : "bh3_global",
            GameType.ZenlessZoneZero => game.Region == GameRegion.China ? "nap_cn" : "nap_global",
            _ => ""
        };
    }

    private GameDownloadInfo? ParseDownloadResponse(GameInfo game, string response)
    {
        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var data))
                return null;

            var downloadInfo = new GameDownloadInfo
            {
                GameId = game.Id
            };

            // Parse game package info
            if (data.TryGetProperty("game", out var gameData))
            {
                if (gameData.TryGetProperty("latest", out var latest))
                {
                    downloadInfo.Version = latest.GetProperty("version").GetString() ?? "";

                    if (latest.TryGetProperty("path", out var path))
                    {
                        downloadInfo.DownloadUrl = path.GetString() ?? "";
                    }

                    if (latest.TryGetProperty("size", out var size))
                    {
                        downloadInfo.TotalSize = GetInt64FromElement(size);
                    }

                    if (latest.TryGetProperty("package_size", out var packageSize))
                    {
                        downloadInfo.PackageSize = GetInt64FromElement(packageSize);
                    }

                    if (latest.TryGetProperty("md5", out var md5))
                    {
                        downloadInfo.PackageMd5 = md5.GetString();
                    }

                    // Parse voice packs
                    if (latest.TryGetProperty("voice_packs", out var voicePacks))
                    {
                        foreach (var vp in voicePacks.EnumerateArray())
                        {
                            var voicePack = new VoicePackDownloadInfo
                            {
                                Language = vp.GetProperty("language").GetString() ?? "",
                                DownloadUrl = vp.TryGetProperty("path", out var vpPath) ? vpPath.GetString() ?? "" : "",
                                Size = vp.TryGetProperty("size", out var vpSize) ? GetInt64FromElement(vpSize) : 0,
                                Md5 = vp.TryGetProperty("md5", out var vpMd5) ? vpMd5.GetString() : null
                            };
                            downloadInfo.VoicePacks.Add(voicePack);
                        }
                    }
                }
            }

            // Alternative format for newer APIs (HoYoPlay API)
            if (data.TryGetProperty("game_packages", out var gamePackages))
            {
                var targetBiz = GetGameBizFromType(game);
                
                foreach (var package in gamePackages.EnumerateArray())
                {
                    if (package.TryGetProperty("game", out var pkgGame))
                    {
                        var gameBiz = pkgGame.TryGetProperty("biz", out var biz) ? biz.GetString() : "";
                        
                        // Match by game biz identifier
                        if (gameBiz != targetBiz)
                            continue;

                        if (package.TryGetProperty("main", out var main))
                        {
                            if (main.TryGetProperty("major", out var major))
                            {
                                downloadInfo.Version = major.TryGetProperty("version", out var ver) ? ver.GetString() ?? "" : "";

                                if (major.TryGetProperty("game_pkgs", out var gamePkgs))
                                {
                                    int partNumber = 1;
                                    long totalSize = 0;
                                    
                                    foreach (var pkg in gamePkgs.EnumerateArray())
                                    {
                                        var segment = new GamePackageSegment
                                        {
                                            DownloadUrl = pkg.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "",
                                            Size = pkg.TryGetProperty("size", out var sz) ? GetInt64FromElement(sz) : 0,
                                            Md5 = pkg.TryGetProperty("md5", out var m) ? m.GetString() : null,
                                            PartNumber = partNumber++
                                        };
                                        downloadInfo.PackageSegments.Add(segment);
                                        totalSize += segment.Size;
                                    }
                                    
                                    downloadInfo.TotalSize = totalSize;
                                    
                                    // Set first segment as primary download URL for backwards compatibility
                                    if (downloadInfo.PackageSegments.Count > 0)
                                    {
                                        downloadInfo.DownloadUrl = downloadInfo.PackageSegments[0].DownloadUrl;
                                        downloadInfo.PackageMd5 = downloadInfo.PackageSegments[0].Md5;
                                    }
                                    
                                    Log.Debug("Found {Count} package segments for {GameBiz}, total size: {Size} bytes", 
                                        downloadInfo.PackageSegments.Count, gameBiz, totalSize);
                                }

                                // Voice packs
                                if (major.TryGetProperty("audio_pkgs", out var audioPkgs))
                                {
                                    foreach (var audio in audioPkgs.EnumerateArray())
                                    {
                                        var voicePack = new VoicePackDownloadInfo
                                        {
                                            Language = audio.TryGetProperty("language", out var lang) ? lang.GetString() ?? "" : "",
                                            DownloadUrl = audio.TryGetProperty("url", out var aUrl) ? aUrl.GetString() ?? "" : "",
                                            Size = audio.TryGetProperty("size", out var aSize) ? GetInt64FromElement(aSize) : 0,
                                            Md5 = audio.TryGetProperty("md5", out var aMd5) ? aMd5.GetString() : null
                                        };
                                        downloadInfo.VoicePacks.Add(voicePack);
                                    }
                                }
                                
                                Log.Debug("Parsed download info for {GameBiz}: Version={Version}, Segments={SegmentCount}", 
                                    gameBiz, downloadInfo.Version, downloadInfo.PackageSegments.Count);
                            }
                        }
                        break; // Found our game
                    }
                }
            }

            return downloadInfo;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse download response for {GameId}", game.Id);
            return null;
        }
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

    public void Dispose()
    {
        _httpClient.Dispose();
        _sophonDownloadService?.Dispose();
    }
}

/// <summary>
/// Download information for a game
/// </summary>
public class GameDownloadInfo
{
    public string GameId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public long TotalSize { get; set; }
    public long PackageSize { get; set; }
    public string? PackageMd5 { get; set; }
    public List<GamePackageSegment> PackageSegments { get; set; } = new();
    public List<VoicePackDownloadInfo> VoicePacks { get; set; } = new();
}

/// <summary>
/// A single segment/part of a game package download
/// </summary>
public class GamePackageSegment
{
    public string DownloadUrl { get; set; } = string.Empty;
    public long Size { get; set; }
    public string? Md5 { get; set; }
    public int PartNumber { get; set; }
}

/// <summary>
/// Voice pack download information
/// </summary>
public class VoicePackDownloadInfo
{
    public string Language { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public long Size { get; set; }
    public string? Md5 { get; set; }
}

/// <summary>
/// Game download progress
/// </summary>
public class GameDownloadProgress
{
    public string GameId { get; set; } = string.Empty;
    public GameDownloadState State { get; set; } = GameDownloadState.Idle;
    public string Version { get; set; } = string.Empty;
    public long TotalSize { get; set; }
    public long DownloadedBytes { get; set; }
    public double SpeedBytesPerSecond { get; set; }
    public TimeSpan EstimatedTimeRemaining { get; set; }
    public int TotalFiles { get; set; }
    public int ExtractedFiles { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public double PercentComplete => TotalSize > 0 ? (double)DownloadedBytes / TotalSize * 100 : 0;
}

/// <summary>
/// Game download state
/// </summary>
public enum GameDownloadState
{
    Idle,
    FetchingInfo,
    Downloading,
    DownloadingVoicePacks,
    Verifying,
    Extracting,
    Cleanup,
    Completed,
    Failed,
    Cancelled
}
