using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Linlapse.Models;
using Serilog;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace Linlapse.Services;

/// <summary>
/// Service for installing game files
/// </summary>
public partial class InstallationService
{
    private readonly SettingsService _settingsService;
    private readonly DownloadService _downloadService;
    private readonly GameService _gameService;

    // Regex for parsing 7z progress output - compiled once and reused
    // Matches percentage followed by optional filename, terminated by end of string, whitespace, or backspaces
    [GeneratedRegex(@"(\d+)%\s*(?:-\s*(.+?))?(?:\s*$|[\x08]+|\s+\x08)", RegexOptions.Compiled)]
    private static partial Regex SevenZipProgressRegex();

    public event EventHandler<InstallProgress>? InstallProgressChanged;
    public event EventHandler<string>? InstallCompleted;
    public event EventHandler<(string GameId, Exception Error)>? InstallFailed;

    public InstallationService(
        SettingsService settingsService,
        DownloadService downloadService,
        GameService gameService)
    {
        _settingsService = settingsService;
        _downloadService = downloadService;
        _gameService = gameService;
    }

    /// <summary>
    /// Install a game from a zip archive
    /// </summary>
    public async Task<bool> InstallFromArchiveAsync(
        string gameId,
        string archivePath,
        string installPath,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null)
        {
            Log.Error("Game not found: {GameId}", gameId);
            return false;
        }

        try
        {
            _gameService.UpdateGameState(gameId, GameState.Installing);

            var installProgress = new InstallProgress
            {
                GameId = gameId,
                State = InstallState.Extracting
            };

            Directory.CreateDirectory(installPath);

            // Determine archive type and extract
            var extension = Path.GetExtension(archivePath).ToLowerInvariant();

            switch (extension)
            {
                case ".zip":
                    await ExtractZipAsync(archivePath, installPath, installProgress, progress, cancellationToken);
                    break;
                case ".7z":
                    await Extract7zAsync(archivePath, installPath, installProgress, progress, cancellationToken);
                    break;
                default:
                    throw new NotSupportedException($"Archive format not supported: {extension}");
            }

            // Update game info - UpdateGameInstallPath will set the state based on whether
            // the game executable exists
            _gameService.UpdateGameInstallPath(gameId, installPath);

            installProgress.State = InstallState.Completed;
            progress?.Report(installProgress);
            InstallCompleted?.Invoke(this, gameId);

            Log.Information("Installation completed: {GameId}", gameId);
            return true;
        }
        catch (OperationCanceledException)
        {
            _gameService.UpdateGameState(gameId, GameState.NotInstalled);
            Log.Information("Installation cancelled: {GameId}", gameId);
            return false;
        }
        catch (Exception ex)
        {
            _gameService.UpdateGameState(gameId, GameState.NotInstalled);
            Log.Error(ex, "Installation failed: {GameId}", gameId);
            InstallFailed?.Invoke(this, (gameId, ex));
            return false;
        }
    }

    private async Task ExtractZipAsync(
        string archivePath,
        string destinationPath,
        InstallProgress installProgress,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var entries = archive.Entries.ToList();
            installProgress.TotalFiles = entries.Count;
            installProgress.TotalBytes = entries.Sum(e => e.Length);

            // Get the full destination path for security validation
            var fullDestinationPath = Path.GetFullPath(destinationPath);

            // First pass: create all directories (must be sequential to avoid race conditions)
            var directories = entries
                .Where(e => string.IsNullOrEmpty(e.Name))
                .Select(e => Path.Combine(destinationPath, e.FullName))
                .Distinct()
                .ToList();

            foreach (var dir in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                // Security: validate directory path
                var fullDir = Path.GetFullPath(dir);
                if (!fullDir.StartsWith(fullDestinationPath, StringComparison.Ordinal))
                {
                    Log.Warning("Skipping potentially malicious directory entry: {Dir}", dir);
                    continue;
                }
                Directory.CreateDirectory(dir);
            }

            // Also pre-create directories for files
            var fileDirectories = entries
                .Where(e => !string.IsNullOrEmpty(e.Name))
                .Select(e => Path.GetDirectoryName(Path.Combine(destinationPath, e.FullName)))
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct()
                .ToList();

            foreach (var dir in fileDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                // Security: validate directory path
                var fullDir = Path.GetFullPath(dir!);
                if (!fullDir.StartsWith(fullDestinationPath, StringComparison.Ordinal))
                {
                    continue;
                }
                Directory.CreateDirectory(dir!);
            }

            // Second pass: extract files in parallel for better performance
            var fileEntries = entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
            var processedFiles = 0;
            var processedBytes = 0L;
            var progressLock = new object();
            var lastProgressReport = DateTime.UtcNow;
            var progressReportInterval = TimeSpan.FromMilliseconds(100);

            // Use parallel extraction with degree of parallelism optimized for I/O-bound operations
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount * 2),
                CancellationToken = cancellationToken
            };

            Parallel.ForEach(fileEntries, parallelOptions, entry =>
            {
                var destinationFileName = Path.Combine(destinationPath, entry.FullName);
                
                // Security: validate file path
                var fullPath = Path.GetFullPath(destinationFileName);
                if (!fullPath.StartsWith(fullDestinationPath, StringComparison.Ordinal))
                {
                    Log.Warning("Skipping potentially malicious archive entry: {Entry}", entry.FullName);
                    return;
                }

                entry.ExtractToFile(destinationFileName, overwrite: true);

                lock (progressLock)
                {
                    processedFiles++;
                    processedBytes += entry.Length;
                    installProgress.ProcessedFiles = processedFiles;
                    installProgress.ProcessedBytes = processedBytes;
                    installProgress.CurrentFile = entry.FullName;
                    
                    // Rate-limited progress reporting for better performance
                    var now = DateTime.UtcNow;
                    if (now - lastProgressReport >= progressReportInterval)
                    {
                        progress?.Report(installProgress);
                        InstallProgressChanged?.Invoke(this, installProgress);
                        lastProgressReport = now;
                    }
                }
            });

            // Final progress report
            installProgress.ProcessedFiles = processedFiles;
            installProgress.ProcessedBytes = processedBytes;
            progress?.Report(installProgress);
            InstallProgressChanged?.Invoke(this, installProgress);
        }, cancellationToken);
    }

    private async Task Extract7zAsync(
        string archivePath,
        string destinationPath,
        InstallProgress installProgress,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Try to use native 7z command for much faster extraction (10-50x faster for large archives)
        var sevenZipPath = Find7zExecutable();
        if (!string.IsNullOrEmpty(sevenZipPath))
        {
            await ExtractWith7zCommandAsync(sevenZipPath, archivePath, destinationPath, installProgress, progress, cancellationToken);
            return;
        }

        // Fallback to SharpCompress if 7z is not available
        Log.Warning("Native 7z not found, falling back to SharpCompress (slower). Install p7zip-full for better performance.");
        await ExtractWith7zSharpCompressAsync(archivePath, destinationPath, installProgress, progress, cancellationToken);
    }

    /// <summary>
    /// Find the 7z executable on the system
    /// </summary>
    private static string? Find7zExecutable()
    {
        // Common 7z executable names on Linux
        var candidates = new[] { "7z", "7za", "7zr" };
        
        foreach (var candidate in candidates)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "which",
                        Arguments = candidate,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                var path = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                
                if (process.ExitCode == 0 && !string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    return path;
                }
            }
            catch
            {
                // Ignore and try next candidate
            }
        }

        // Check common paths directly
        var commonPaths = new[] { "/usr/bin/7z", "/usr/bin/7za", "/usr/local/bin/7z" };
        foreach (var path in commonPaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Extract using native 7z command (much faster for large archives)
    /// </summary>
    private async Task ExtractWith7zCommandAsync(
        string sevenZipPath,
        string archivePath,
        string destinationPath,
        InstallProgress installProgress,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Get archive info first for progress reporting
        var archiveInfo = await Get7zArchiveInfoAsync(sevenZipPath, archivePath, cancellationToken);
        installProgress.TotalFiles = archiveInfo.FileCount;
        installProgress.TotalBytes = archiveInfo.TotalSize;

        progress?.Report(installProgress);
        InstallProgressChanged?.Invoke(this, installProgress);

        // Validate destination path
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(fullDestinationPath);

        // Run 7z extraction with multi-threading enabled
        // -mmt=on enables multi-threading, -y answers yes to all prompts, -o specifies output directory
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = sevenZipPath,
                Arguments = $"x \"{archivePath}\" -o\"{fullDestinationPath}\" -mmt=on -y -bsp1",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();

            // Read stdout in a background task to handle 7z's in-place progress updates
            // 7z uses backspaces (\b) to erase and rewrite progress, not newlines
            // BeginOutputReadLine only fires on newlines, so we need to read in chunks
            var stdoutTask = Task.Run(async () =>
            {
                const int ReadBufferSize = 4096; // Larger buffer for better I/O performance
                var lastProgressReport = DateTime.UtcNow;
                var progressReportInterval = TimeSpan.FromMilliseconds(250);
                var buffer = new char[ReadBufferSize];
                var reader = process.StandardOutput;
                var percentRegex = SevenZipProgressRegex();
                var accumulated = new System.Text.StringBuilder(ReadBufferSize);
                var lastPercent = -1;

                try
                {
                    int charsRead;
                    while ((charsRead = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                    {
                        // Check for cancellation
                        cancellationToken.ThrowIfCancellationRequested();
                        
                        // Accumulate the chunk
                        accumulated.Append(buffer, 0, charsRead);
                        
                        // Only process when we have enough data or hit a potential progress boundary
                        // 7z progress lines are typically short, so process when we see backspaces or have > 256 chars
                        var content = accumulated.ToString();
                        if (content.Length > 256 || content.Contains('\x08') || content.Contains('\n'))
                        {
                            // Find all percentage matches in accumulated content
                            var matches = percentRegex.Matches(content);
                            foreach (Match match in matches)
                            {
                                if (int.TryParse(match.Groups[1].Value, out var percent) && percent != lastPercent)
                                {
                                    lastPercent = percent;
                                    var estimatedProcessedBytes = (long)(installProgress.TotalBytes * percent / 100.0);
                                    installProgress.ProcessedBytes = estimatedProcessedBytes;
                                    installProgress.ProcessedFiles = (int)(installProgress.TotalFiles * percent / 100.0);

                                    // Extract filename if present (group 2)
                                    if (match.Groups[2].Success && !string.IsNullOrWhiteSpace(match.Groups[2].Value))
                                    {
                                        installProgress.CurrentFile = match.Groups[2].Value.Trim();
                                    }

                                    var now = DateTime.UtcNow;
                                    if (now - lastProgressReport >= progressReportInterval)
                                    {
                                        progress?.Report(installProgress);
                                        InstallProgressChanged?.Invoke(this, installProgress);
                                        lastProgressReport = now;
                                    }
                                }
                            }
                            
                            // Clear accumulated content after processing
                            accumulated.Clear();
                        }
                    }
                    
                    // Process any remaining content
                    if (accumulated.Length > 0)
                    {
                        var content = accumulated.ToString();
                        var matches = percentRegex.Matches(content);
                        foreach (Match match in matches)
                        {
                            if (int.TryParse(match.Groups[1].Value, out var percent) && percent != lastPercent)
                            {
                                lastPercent = percent;
                                installProgress.ProcessedBytes = (long)(installProgress.TotalBytes * percent / 100.0);
                                installProgress.ProcessedFiles = (int)(installProgress.TotalFiles * percent / 100.0);
                                if (match.Groups[2].Success && !string.IsNullOrWhiteSpace(match.Groups[2].Value))
                                {
                                    installProgress.CurrentFile = match.Groups[2].Value.Trim();
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Error reading 7z progress output");
                }
            }, cancellationToken);

            // Wait for process to complete or cancellation
            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                    }
                }
                catch { }
            });

            await process.WaitForExitAsync(cancellationToken);
            
            // Wait for stdout reading to complete (exceptions are already logged inside the task)
            await stdoutTask;

            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
                throw new Exception($"7z extraction failed with exit code {process.ExitCode}: {stderr}");
            }

            // Final progress report
            installProgress.ProcessedFiles = installProgress.TotalFiles;
            installProgress.ProcessedBytes = installProgress.TotalBytes;
            progress?.Report(installProgress);
            InstallProgressChanged?.Invoke(this, installProgress);

            Log.Information("7z extraction completed using native 7z for {Archive}", archivePath);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(true); } catch { }
            }
            process.Dispose();
        }
    }

    /// <summary>
    /// Get archive info (file count and total size) using 7z list command
    /// </summary>
    private static async Task<(int FileCount, long TotalSize)> Get7zArchiveInfoAsync(
        string sevenZipPath,
        string archivePath,
        CancellationToken cancellationToken)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = sevenZipPath,
                Arguments = $"l \"{archivePath}\" -slt",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var fileCount = 0;
            long totalSize = 0;

            // Parse the output to get file count and sizes
            var lines = output.Split('\n');
            foreach (var line in lines)
            {
                if (line.StartsWith("Size = ", StringComparison.Ordinal))
                {
                    if (long.TryParse(line[7..].Trim(), out var size))
                    {
                        totalSize += size;
                        fileCount++;
                    }
                }
            }

            // If parsing failed, estimate from file size
            if (fileCount == 0)
            {
                var fileInfo = new FileInfo(archivePath);
                totalSize = fileInfo.Length * 3; // Rough estimate: compressed size * 3
                fileCount = 1000; // Default estimate
            }

            return (fileCount, totalSize);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(true); } catch { }
            }
            process.Dispose();
        }
    }

    /// <summary>
    /// Fallback extraction using SharpCompress (slower but works without native 7z)
    /// </summary>
    private async Task ExtractWith7zSharpCompressAsync(
        string archivePath,
        string destinationPath,
        InstallProgress installProgress,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            using var archive = ArchiveFactory.Open(archivePath);
            var fileEntries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            installProgress.TotalFiles = fileEntries.Count;
            installProgress.TotalBytes = fileEntries.Sum(e => e.Size);

            // Get the full path of destination for validation
            var fullDestinationPath = Path.GetFullPath(destinationPath);

            // Pre-create all directories to avoid repeated creation during extraction
            var directories = fileEntries
                .Select(e => Path.GetDirectoryName(Path.Combine(destinationPath, e.Key ?? string.Empty)))
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct()
                .ToList();

            foreach (var dir in directories)
            {
                // Security: validate directory path
                var fullDir = Path.GetFullPath(dir!);
                if (fullDir.StartsWith(fullDestinationPath, StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(dir!);
                }
            }

            var lastProgressReport = DateTime.UtcNow;
            var progressReportInterval = TimeSpan.FromMilliseconds(100); // Report every 100ms

            foreach (var entry in fileEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entryKey = entry.Key ?? string.Empty;
                
                // Validate entry key to prevent path traversal attacks
                // Use case-sensitive comparison (Ordinal) for proper security on Linux file systems
                var destinationFileName = Path.GetFullPath(Path.Combine(destinationPath, entryKey));
                if (!destinationFileName.StartsWith(fullDestinationPath, StringComparison.Ordinal))
                {
                    Log.Warning("Skipping potentially malicious archive entry: {EntryKey}", entryKey);
                    continue;
                }

                entry.WriteToFile(destinationFileName, new ExtractionOptions
                {
                    ExtractFullPath = false,
                    Overwrite = true
                });

                installProgress.ProcessedFiles++;
                installProgress.ProcessedBytes += entry.Size;
                installProgress.CurrentFile = entryKey;
                
                // Rate-limited progress reporting for better performance
                var now = DateTime.UtcNow;
                if (now - lastProgressReport >= progressReportInterval)
                {
                    progress?.Report(installProgress);
                    InstallProgressChanged?.Invoke(this, installProgress);
                    lastProgressReport = now;
                }
            }

            // Final progress report
            progress?.Report(installProgress);
            InstallProgressChanged?.Invoke(this, installProgress);
        }, cancellationToken);
    }

    /// <summary>
    /// Download and install a game
    /// </summary>
    public async Task<bool> DownloadAndInstallAsync(
        string gameId,
        string downloadUrl,
        string installPath,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null)
        {
            Log.Error("Game not found: {GameId}", gameId);
            return false;
        }

        var installProgress = new InstallProgress
        {
            GameId = gameId,
            State = InstallState.Downloading
        };

        try
        {
            _gameService.UpdateGameState(gameId, GameState.Installing);

            // Create temp directory for download
            var tempDir = Path.Combine(SettingsService.GetCacheDirectory(), "downloads", gameId);
            Directory.CreateDirectory(tempDir);
            var archivePath = Path.Combine(tempDir, "game.zip");

            // Download
            var downloadProgress = new Progress<DownloadProgress>(dp =>
            {
                installProgress.DownloadProgress = dp;
                installProgress.ProcessedBytes = dp.BytesDownloaded;
                installProgress.TotalBytes = dp.TotalBytes;
                progress?.Report(installProgress);
                InstallProgressChanged?.Invoke(this, installProgress);
            });

            var downloadSuccess = await _downloadService.DownloadFileAsync(
                downloadUrl,
                archivePath,
                downloadProgress,
                cancellationToken);

            if (!downloadSuccess)
            {
                _gameService.UpdateGameState(gameId, GameState.NotInstalled);
                return false;
            }

            // Install
            installProgress.State = InstallState.Extracting;
            progress?.Report(installProgress);

            var installSuccess = await InstallFromArchiveAsync(
                gameId,
                archivePath,
                installPath,
                progress,
                cancellationToken);

            // Cleanup temp file
            try
            {
                File.Delete(archivePath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to cleanup temp file: {Path}", archivePath);
            }

            return installSuccess;
        }
        catch (Exception ex)
        {
            _gameService.UpdateGameState(gameId, GameState.NotInstalled);
            Log.Error(ex, "Download and install failed: {GameId}", gameId);
            InstallFailed?.Invoke(this, (gameId, ex));
            return false;
        }
    }

    /// <summary>
    /// Uninstall a game
    /// </summary>
    public async Task<bool> UninstallGameAsync(string gameId, bool deleteFiles = true)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null)
        {
            Log.Error("Game not found: {GameId}", gameId);
            return false;
        }

        try
        {
            if (deleteFiles && !string.IsNullOrEmpty(game.InstallPath) && Directory.Exists(game.InstallPath))
            {
                await Task.Run(() =>
                {
                    Directory.Delete(game.InstallPath, recursive: true);
                });
                Log.Information("Deleted game files: {Path}", game.InstallPath);
            }

            _gameService.UpdateGameInstallPath(gameId, string.Empty);
            _gameService.UpdateGameState(gameId, GameState.NotInstalled);

            Log.Information("Game uninstalled: {GameId}", gameId);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to uninstall game: {GameId}", gameId);
            return false;
        }
    }
}

/// <summary>
/// Installation progress information
/// </summary>
public class InstallProgress
{
    public string GameId { get; set; } = string.Empty;
    public InstallState State { get; set; } = InstallState.Idle;
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public long TotalBytes { get; set; }
    public long ProcessedBytes { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public DownloadProgress? DownloadProgress { get; set; }
    public double PercentComplete => TotalBytes > 0 ? (double)ProcessedBytes / TotalBytes * 100 : 0;
}

/// <summary>
/// Installation state
/// </summary>
public enum InstallState
{
    Idle,
    Downloading,
    Verifying,
    Extracting,
    Configuring,
    Completed,
    Failed,
    Cancelled
}
