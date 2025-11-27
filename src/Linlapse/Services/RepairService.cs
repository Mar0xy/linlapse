using System.Security.Cryptography;
using System.Text.Json;
using Linlapse.Models;
using Serilog;

namespace Linlapse.Services;

/// <summary>
/// Service for verifying and repairing game files
/// </summary>
public class RepairService
{
    private readonly GameService _gameService;
    private readonly DownloadService _downloadService;

    public event EventHandler<RepairProgress>? RepairProgressChanged;
    public event EventHandler<string>? RepairCompleted;
    public event EventHandler<(string GameId, Exception Error)>? RepairFailed;

    public RepairService(GameService gameService, DownloadService downloadService)
    {
        _gameService = gameService;
        _downloadService = downloadService;
    }

    /// <summary>
    /// Verify game file integrity
    /// </summary>
    public async Task<List<FileVerificationResult>> VerifyGameFilesAsync(
        string gameId,
        IProgress<RepairProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null || !game.IsInstalled || string.IsNullOrEmpty(game.InstallPath))
        {
            Log.Error("Game not found or not installed: {GameId}", gameId);
            return new List<FileVerificationResult>();
        }

        var results = new List<FileVerificationResult>();
        var repairProgress = new RepairProgress
        {
            State = RepairState.Scanning
        };

        try
        {
            _gameService.UpdateGameState(gameId, GameState.Repairing);

            // Get manifest file if exists
            var manifestPath = Path.Combine(game.InstallPath, "pkg_version");
            var manifest = await LoadManifestAsync(manifestPath);

            if (manifest.Count == 0)
            {
                // No manifest - scan all files and report sizes
                var files = Directory.GetFiles(game.InstallPath, "*", SearchOption.AllDirectories);
                repairProgress.TotalFiles = files.Length;

                foreach (var filePath in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var relativePath = Path.GetRelativePath(game.InstallPath, filePath);
                    var fileInfo = new FileInfo(filePath);
                    
                    results.Add(new FileVerificationResult
                    {
                        FilePath = relativePath,
                        ActualSize = fileInfo.Length,
                        IsValid = true // No manifest to verify against
                    });

                    repairProgress.ProcessedFiles++;
                    repairProgress.CurrentFile = relativePath;
                    progress?.Report(repairProgress);
                    RepairProgressChanged?.Invoke(this, repairProgress);
                }
            }
            else
            {
                repairProgress.TotalFiles = manifest.Count;

                foreach (var entry in manifest)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var filePath = Path.Combine(game.InstallPath, entry.Key);
                    var result = await VerifyFileAsync(filePath, entry.Value);
                    result.FilePath = entry.Key;
                    results.Add(result);

                    if (!result.IsValid)
                    {
                        repairProgress.BrokenFiles++;
                        repairProgress.TotalBytesToRepair += result.ExpectedSize;
                    }

                    repairProgress.ProcessedFiles++;
                    repairProgress.CurrentFile = entry.Key;
                    progress?.Report(repairProgress);
                    RepairProgressChanged?.Invoke(this, repairProgress);
                }

                // Check for extra files
                var gameFiles = Directory.GetFiles(game.InstallPath, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(game.InstallPath, f))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var manifestFiles = manifest.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var extraFiles = gameFiles.Except(manifestFiles);

                foreach (var extraFile in extraFiles)
                {
                    // Skip certain files that are expected to exist
                    if (ShouldIgnoreFile(extraFile))
                        continue;

                    results.Add(new FileVerificationResult
                    {
                        FilePath = extraFile,
                        IsValid = false,
                        Issue = FileIssueType.Extra
                    });
                }
            }

            repairProgress.State = RepairState.Completed;
            progress?.Report(repairProgress);
            
            _gameService.UpdateGameState(gameId, GameState.Ready);
            
            Log.Information("Verification completed for {GameId}: {Total} files, {Broken} issues found",
                gameId, repairProgress.TotalFiles, repairProgress.BrokenFiles);

            return results;
        }
        catch (Exception ex)
        {
            _gameService.UpdateGameState(gameId, GameState.Ready);
            Log.Error(ex, "Verification failed for {GameId}", gameId);
            RepairFailed?.Invoke(this, (gameId, ex));
            throw;
        }
    }

    /// <summary>
    /// Repair broken game files
    /// </summary>
    public async Task<bool> RepairGameFilesAsync(
        string gameId,
        List<FileVerificationResult> brokenFiles,
        string? baseDownloadUrl = null,
        IProgress<RepairProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null || !game.IsInstalled || string.IsNullOrEmpty(game.InstallPath))
        {
            Log.Error("Game not found or not installed: {GameId}", gameId);
            return false;
        }

        var filesToRepair = brokenFiles.Where(f => !f.IsValid && f.Issue != FileIssueType.Extra).ToList();
        if (filesToRepair.Count == 0)
        {
            Log.Information("No files to repair for {GameId}", gameId);
            return true;
        }

        var repairProgress = new RepairProgress
        {
            State = RepairState.Repairing,
            TotalFiles = filesToRepair.Count,
            TotalBytesToRepair = filesToRepair.Sum(f => f.ExpectedSize)
        };

        try
        {
            _gameService.UpdateGameState(gameId, GameState.Repairing);

            foreach (var file in filesToRepair)
            {
                cancellationToken.ThrowIfCancellationRequested();

                repairProgress.CurrentFile = file.FilePath;
                progress?.Report(repairProgress);
                RepairProgressChanged?.Invoke(this, repairProgress);

                var filePath = Path.Combine(game.InstallPath, file.FilePath);

                switch (file.Issue)
                {
                    case FileIssueType.Missing:
                    case FileIssueType.HashMismatch:
                    case FileIssueType.SizeMismatch:
                    case FileIssueType.Corrupted:
                        if (!string.IsNullOrEmpty(baseDownloadUrl))
                        {
                            var fileUrl = $"{baseDownloadUrl.TrimEnd('/')}/{file.FilePath}";
                            var success = await _downloadService.DownloadFileAsync(
                                fileUrl, 
                                filePath, 
                                cancellationToken: cancellationToken);
                            
                            if (success)
                            {
                                repairProgress.RepairedFiles++;
                                repairProgress.BytesRepaired += file.ExpectedSize;
                            }
                        }
                        else
                        {
                            Log.Warning("Cannot repair {File} - no download URL provided", file.FilePath);
                        }
                        break;
                }

                repairProgress.ProcessedFiles++;
                progress?.Report(repairProgress);
                RepairProgressChanged?.Invoke(this, repairProgress);
            }

            // Handle extra files (optional deletion)
            var extraFiles = brokenFiles.Where(f => f.Issue == FileIssueType.Extra).ToList();
            if (extraFiles.Count > 0)
            {
                Log.Information("Found {Count} extra files in {GameId}", extraFiles.Count, gameId);
                // Note: We don't delete extra files by default as they might be user data
            }

            repairProgress.State = RepairState.Completed;
            progress?.Report(repairProgress);
            
            _gameService.UpdateGameState(gameId, GameState.Ready);
            RepairCompleted?.Invoke(this, gameId);
            
            Log.Information("Repair completed for {GameId}: {Repaired}/{Total} files repaired",
                gameId, repairProgress.RepairedFiles, repairProgress.TotalFiles);

            return repairProgress.RepairedFiles == repairProgress.TotalFiles;
        }
        catch (Exception ex)
        {
            _gameService.UpdateGameState(gameId, GameState.Ready);
            Log.Error(ex, "Repair failed for {GameId}", gameId);
            RepairFailed?.Invoke(this, (gameId, ex));
            return false;
        }
    }

    private async Task<Dictionary<string, FileManifestEntry>> LoadManifestAsync(string manifestPath)
    {
        var manifest = new Dictionary<string, FileManifestEntry>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(manifestPath))
            return manifest;

        try
        {
            var lines = await File.ReadAllLinesAsync(manifestPath);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Parse format: {"remoteName":"path","md5":"hash","fileSize":size}
                try
                {
                    var entry = JsonSerializer.Deserialize<FileManifestEntry>(line);
                    if (entry != null && !string.IsNullOrEmpty(entry.RemoteName))
                    {
                        manifest[entry.RemoteName] = entry;
                    }
                }
                catch
                {
                    // Try alternative format: path:hash:size
                    var parts = line.Split(':');
                    if (parts.Length >= 3)
                    {
                        manifest[parts[0]] = new FileManifestEntry
                        {
                            RemoteName = parts[0],
                            Md5 = parts[1],
                            FileSize = long.TryParse(parts[2], out var size) ? size : 0
                        };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load manifest: {Path}", manifestPath);
        }

        return manifest;
    }

    private async Task<FileVerificationResult> VerifyFileAsync(string filePath, FileManifestEntry expected)
    {
        var result = new FileVerificationResult
        {
            ExpectedHash = expected.Md5,
            ExpectedSize = expected.FileSize
        };

        if (!File.Exists(filePath))
        {
            result.IsValid = false;
            result.Issue = FileIssueType.Missing;
            return result;
        }

        var fileInfo = new FileInfo(filePath);
        result.ActualSize = fileInfo.Length;

        if (fileInfo.Length != expected.FileSize)
        {
            result.IsValid = false;
            result.Issue = FileIssueType.SizeMismatch;
            return result;
        }

        if (!string.IsNullOrEmpty(expected.Md5))
        {
            try
            {
                await using var stream = File.OpenRead(filePath);
                using var md5 = MD5.Create();
                var hash = await md5.ComputeHashAsync(stream);
                result.ActualHash = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();

                if (!result.ActualHash.Equals(expected.Md5, StringComparison.OrdinalIgnoreCase))
                {
                    result.IsValid = false;
                    result.Issue = FileIssueType.HashMismatch;
                    return result;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to compute hash for {Path}", filePath);
                result.IsValid = false;
                result.Issue = FileIssueType.Corrupted;
                return result;
            }
        }

        result.IsValid = true;
        result.Issue = FileIssueType.None;
        return result;
    }

    private bool ShouldIgnoreFile(string relativePath)
    {
        var ignorePatterns = new[]
        {
            "config.ini",
            "launcher.ini",
            "log",
            "logs",
            "crash",
            "crashdump",
            "screenshot",
            "screenshots",
            ".log",
            ".txt"
        };

        var lowerPath = relativePath.ToLowerInvariant();
        return ignorePatterns.Any(p => lowerPath.Contains(p));
    }
}

/// <summary>
/// File manifest entry from pkg_version
/// </summary>
public class FileManifestEntry
{
    public string RemoteName { get; set; } = string.Empty;
    public string Md5 { get; set; } = string.Empty;
    public long FileSize { get; set; }
}
