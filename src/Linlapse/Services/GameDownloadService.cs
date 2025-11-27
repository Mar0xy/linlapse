using System.Net.Http;
using System.Text.Json;
using Linlapse.Models;
using Serilog;

namespace Linlapse.Services;

/// <summary>
/// Service for downloading and installing games from official sources
/// </summary>
public class GameDownloadService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly GameService _gameService;
    private readonly DownloadService _downloadService;
    private readonly InstallationService _installationService;
    private readonly SettingsService _settingsService;

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
    /// Download and install a game
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

            var downloadTasks = new List<Task<bool>>();
            var downloadedFiles = new List<string>();

            // Download main game package
            var mainPackagePath = Path.Combine(tempDir, $"game_package{Path.GetExtension(downloadInfo.DownloadUrl)}");

            var fileProgress = new Progress<DownloadProgress>(dp =>
            {
                downloadProgress.DownloadedBytes = dp.BytesDownloaded;
                downloadProgress.SpeedBytesPerSecond = dp.SpeedBytesPerSecond;
                downloadProgress.EstimatedTimeRemaining = dp.EstimatedTimeRemaining;
                progress?.Report(downloadProgress);
                DownloadProgressChanged?.Invoke(this, downloadProgress);
            });

            var mainDownloadSuccess = await _downloadService.DownloadFileAsync(
                downloadInfo.DownloadUrl,
                mainPackagePath,
                fileProgress,
                cancellationToken);

            if (!mainDownloadSuccess)
            {
                throw new Exception("Failed to download game package");
            }

            downloadedFiles.Add(mainPackagePath);

            // Download voice packs if available and selected
            if (downloadInfo.VoicePacks.Count > 0)
            {
                downloadProgress.State = GameDownloadState.DownloadingVoicePacks;
                progress?.Report(downloadProgress);

                // Default to English voice pack
                var selectedVoicePack = downloadInfo.VoicePacks.FirstOrDefault(v =>
                    v.Language.Contains("en", StringComparison.OrdinalIgnoreCase) ||
                    v.Language.Contains("English", StringComparison.OrdinalIgnoreCase));

                if (selectedVoicePack != null && !string.IsNullOrEmpty(selectedVoicePack.DownloadUrl))
                {
                    var voicePackPath = Path.Combine(tempDir, $"voice_{selectedVoicePack.Language}{Path.GetExtension(selectedVoicePack.DownloadUrl)}");

                    var voiceDownloadSuccess = await _downloadService.DownloadFileAsync(
                        selectedVoicePack.DownloadUrl,
                        voicePackPath,
                        cancellationToken: cancellationToken);

                    if (voiceDownloadSuccess)
                    {
                        downloadedFiles.Add(voicePackPath);
                    }
                    else
                    {
                        Log.Warning("Failed to download voice pack for {Language}", selectedVoicePack.Language);
                    }
                }
            }

            // Verify downloads
            downloadProgress.State = GameDownloadState.Verifying;
            progress?.Report(downloadProgress);

            if (!string.IsNullOrEmpty(downloadInfo.PackageMd5))
            {
                var isValid = await _downloadService.VerifyFileHashAsync(
                    mainPackagePath,
                    downloadInfo.PackageMd5,
                    System.Security.Cryptography.HashAlgorithmName.MD5);

                if (!isValid)
                {
                    throw new Exception("Downloaded file verification failed - file may be corrupted");
                }
            }

            // Extract/Install
            downloadProgress.State = GameDownloadState.Extracting;
            progress?.Report(downloadProgress);

            Directory.CreateDirectory(installPath);

            foreach (var downloadedFile in downloadedFiles)
            {
                var extension = Path.GetExtension(downloadedFile).ToLowerInvariant();

                if (extension == ".zip" || extension == ".7z")
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
                        downloadedFile,
                        installPath,
                        installProgress,
                        cancellationToken);
                }
            }

            // Update game info
            _gameService.UpdateGameInstallPath(gameId, installPath);
            game.Version = downloadInfo.Version;
            _gameService.UpdateGameState(gameId, GameState.Ready);

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
        // Note: These are the official launcher API endpoints
        // Keys are public and used by the official launchers
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

            // Alternative format for newer APIs
            if (data.TryGetProperty("game_packages", out var gamePackages))
            {
                foreach (var package in gamePackages.EnumerateArray())
                {
                    if (package.TryGetProperty("game", out var pkgGame))
                    {
                        var gameId = pkgGame.TryGetProperty("id", out var id) ? id.GetString() : "";

                        // Match by game type
                        if (package.TryGetProperty("main", out var main))
                        {
                            if (main.TryGetProperty("major", out var major))
                            {
                                downloadInfo.Version = major.TryGetProperty("version", out var ver) ? ver.GetString() ?? "" : "";

                                if (major.TryGetProperty("game_pkgs", out var gamePkgs))
                                {
                                    var firstPkg = gamePkgs.EnumerateArray().FirstOrDefault();
                                    if (firstPkg.ValueKind != JsonValueKind.Undefined)
                                    {
                                        downloadInfo.DownloadUrl = firstPkg.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "";
                                        downloadInfo.TotalSize = firstPkg.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                                        downloadInfo.PackageMd5 = firstPkg.TryGetProperty("md5", out var m) ? m.GetString() : null;
                                    }
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
                                            Size = audio.TryGetProperty("size", out var aSize) ? aSize.GetInt64() : 0,
                                            Md5 = audio.TryGetProperty("md5", out var aMd5) ? aMd5.GetString() : null
                                        };
                                        downloadInfo.VoicePacks.Add(voicePack);
                                    }
                                }
                            }
                        }
                        break; // Use first matching package
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
    public List<VoicePackDownloadInfo> VoicePacks { get; set; } = new();
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
