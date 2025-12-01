using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Linlapse.Models;
using Serilog;
using SharpCompress.Archives;

namespace Linlapse.Services;

/// <summary>
/// Service for managing Wine/Proton runner downloads and installations
/// </summary>
public class WineRunnerService
{
    private readonly SettingsService _settingsService;
    private readonly DownloadService _downloadService;
    private readonly string _runnersDirectory;
    
    // Use a single shared HttpClient for all downloads to avoid socket exhaustion
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(30)
    };
    
    // Progress constants for clarity
    private const double DownloadProgressMax = 45.0;
    private const double VerificationProgress = 50.0;
    private const double ExtractionProgressStart = 50.0;
    private const double ExtractionProgressMax = 100.0;

    /// <summary>
    /// Predefined list of popular wine/proton runners available for download.
    /// Note: Versions are pinned for stability. Check GitHub releases for newer versions.
    /// MD5 checksums should be updated when versions change.
    /// </summary>
    private static readonly List<WineRunner> AvailableRunners = new()
    {
        // Wine runners
        new WineRunner
        {
            Id = "wine-spritz",
            Name = "Spritz Wine",
            Version = "10.15",
            Description = "Wine with additional patches",
            Type = WineRunnerType.Wine,
            DownloadUrl = "https://github.com/NelloKudo/Wine-Builds/releases/download/wine-tkg-aagl-v10.15-7/spritz-wine-tkg-staging-wow64-10.15-7-x86_64.tar.xz",
            Md5Checksum = "fe2f5559dcb832bab7c9044a88973267"
        },
        // Proton runners
        new WineRunner
        {
            Id = "proton-cachyos",
            Name = "CachyOS Proton",
            Version = "10.0-20251126",
            Description = "CachyOS optimized Proton build with performance improvements",
            Type = WineRunnerType.Proton,
            DownloadUrl = "https://github.com/CachyOS/proton-cachyos/releases/download/cachyos-10.0-20251126-slr/proton-cachyos-10.0-20251126-slr-x86_64.tar.xz",
            Md5Checksum = "a04d7e99dd1bf577605c8ca21e3b9348"
        },
        new WineRunner
        {
            Id = "proton-dw",
            Name = "dwproton",
            Version = "10.0-9",
            Description = "Special proton made by dawn winery",
            Type = WineRunnerType.Proton,
            DownloadUrl = "https://dawn.wine/dawn-winery/dwproton/releases/download/dwproton-10.0-9/dwproton-10.0-9-x86_64.tar.xz",
            Md5Checksum = "e2bc79e8fc669fa4dc1027feeae25b7b"
        }
    };

    public event EventHandler<(string RunnerId, double Progress)>? DownloadProgressChanged;

    public WineRunnerService(SettingsService settingsService, DownloadService downloadService)
    {
        _settingsService = settingsService;
        _downloadService = downloadService;
        _runnersDirectory = GetRunnersDirectory();
    }

    private static string GetRunnersDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(home, ".local", "share", "linlapse", "runners");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Get list of available runners with installation status updated
    /// </summary>
    public List<WineRunner> GetAvailableRunners()
    {
        var installedRunners = _settingsService.Settings.InstalledRunners;
        var runners = new List<WineRunner>();

        foreach (var runner in AvailableRunners)
        {
            var copy = new WineRunner
            {
                Id = runner.Id,
                Name = runner.Name,
                Version = runner.Version,
                Description = runner.Description,
                Type = runner.Type,
                DownloadUrl = runner.DownloadUrl,
                Md5Checksum = runner.Md5Checksum
            };

            var installed = installedRunners.FirstOrDefault(r => r.Id == runner.Id);
            if (installed != null && Directory.Exists(installed.InstallPath))
            {
                copy.IsInstalled = true;
                copy.InstallPath = installed.InstallPath;
            }

            runners.Add(copy);
        }

        return runners;
    }

    /// <summary>
    /// Get list of installed runners from various sources:
    /// - Runners installed via the launcher
    /// - Runners from Steam's compatibilitytools.d folder
    /// </summary>
    public List<InstalledRunner> GetInstalledRunners()
    {
        var runners = new List<InstalledRunner>();
        // Use resolved real paths for deduplication to handle symlinks
        // (e.g., ~/.steam/root and ~/.steam/steam often point to the same directory)
        var seenRealPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        // 1. Get runners installed via the launcher (from settings)
        foreach (var runner in _settingsService.Settings.InstalledRunners)
        {
            if (Directory.Exists(runner.InstallPath))
            {
                var realPath = GetRealPath(runner.InstallPath);
                if (seenRealPaths.Add(realPath))
                {
                    runners.Add(runner);
                }
            }
        }
        
        // 2. Scan Steam's compatibilitytools.d directories
        var steamCompatDirs = GetSteamCompatibilityToolsDirectories();
        foreach (var compatDir in steamCompatDirs)
        {
            if (Directory.Exists(compatDir))
            {
                var detectedRunners = ScanForRunners(compatDir, "steam");
                foreach (var runner in detectedRunners)
                {
                    var realPath = GetRealPath(runner.InstallPath);
                    if (seenRealPaths.Add(realPath))
                    {
                        runners.Add(runner);
                    }
                }
            }
        }
        
        // 3. Scan the launcher's runners directory for any manually added runners
        if (Directory.Exists(_runnersDirectory))
        {
            var detectedRunners = ScanForRunners(_runnersDirectory, "linlapse");
            foreach (var runner in detectedRunners)
            {
                var realPath = GetRealPath(runner.InstallPath);
                if (seenRealPaths.Add(realPath))
                {
                    runners.Add(runner);
                }
            }
        }
        
        return runners;
    }
    
    /// <summary>
    /// Get the real path by resolving symlinks
    /// </summary>
    private static string GetRealPath(string path)
    {
        try
        {
            // On Linux, use realpath to resolve symlinks
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "realpath",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            
            process.Start();
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(1000);
            
            if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
            {
                return output;
            }
        }
        catch
        {
            // Fallback if realpath is not available
        }
        
        // Fallback: return the original path
        return path;
    }
    
    /// <summary>
    /// Get possible Steam compatibilitytools.d directory paths
    /// </summary>
    private static List<string> GetSteamCompatibilityToolsDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new List<string>
        {
            // Standard Steam location
            Path.Combine(home, ".steam", "root", "compatibilitytools.d"),
            Path.Combine(home, ".steam", "steam", "compatibilitytools.d"),
            // Flatpak Steam
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam", "compatibilitytools.d"),
            // Snap Steam
            Path.Combine(home, "snap", "steam", "common", ".steam", "steam", "compatibilitytools.d"),
            // System-wide
            "/usr/share/steam/compatibilitytools.d"
        };
    }
    
    /// <summary>
    /// Scan a directory for wine/proton runners
    /// </summary>
    private static List<InstalledRunner> ScanForRunners(string baseDir, string source)
    {
        var runners = new List<InstalledRunner>();
        
        try
        {
            foreach (var dir in Directory.GetDirectories(baseDir))
            {
                var runner = DetectRunner(dir, source);
                if (runner != null)
                {
                    runners.Add(runner);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to scan directory for runners: {Dir}", baseDir);
        }
        
        return runners;
    }
    
    /// <summary>
    /// Detect if a directory contains a wine or proton runner
    /// </summary>
    private static InstalledRunner? DetectRunner(string dir, string source)
    {
        var dirName = Path.GetFileName(dir);
        
        // Check for Proton (has proton script and toolmanifest.vdf)
        var protonScript = Path.Combine(dir, "proton");
        var toolmanifest = Path.Combine(dir, "toolmanifest.vdf");
        
        if (File.Exists(protonScript) || File.Exists(toolmanifest))
        {
            // This is a Proton runner
            var version = ExtractVersionFromName(dirName);
            return new InstalledRunner
            {
                Id = $"{source}-proton-{dirName.ToLowerInvariant().Replace(" ", "-")}",
                Name = dirName,
                Version = version,
                Type = WineRunnerType.Proton,
                InstallPath = dir,
                ExecutablePath = File.Exists(protonScript) ? protonScript : dir
            };
        }
        
        // Check for Wine (has bin/wine or bin/wine64)
        var wineBin = Path.Combine(dir, "bin", "wine");
        var wine64Bin = Path.Combine(dir, "bin", "wine64");
        
        if (File.Exists(wineBin) || File.Exists(wine64Bin))
        {
            var version = ExtractVersionFromName(dirName);
            return new InstalledRunner
            {
                Id = $"{source}-wine-{dirName.ToLowerInvariant().Replace(" ", "-")}",
                Name = dirName,
                Version = version,
                Type = WineRunnerType.Wine,
                InstallPath = dir,
                ExecutablePath = File.Exists(wine64Bin) ? wine64Bin : wineBin
            };
        }
        
        // Check for nested structure (e.g., some Proton builds have files/ subfolder)
        var filesDir = Path.Combine(dir, "files");
        if (Directory.Exists(filesDir))
        {
            var nestedWine = Path.Combine(filesDir, "bin", "wine");
            var nestedWine64 = Path.Combine(filesDir, "bin", "wine64");
            
            if (File.Exists(nestedWine) || File.Exists(nestedWine64))
            {
                var version = ExtractVersionFromName(dirName);
                return new InstalledRunner
                {
                    Id = $"{source}-wine-{dirName.ToLowerInvariant().Replace(" ", "-")}",
                    Name = dirName,
                    Version = version,
                    Type = WineRunnerType.Wine,
                    InstallPath = dir,
                    ExecutablePath = File.Exists(nestedWine64) ? nestedWine64 : nestedWine
                };
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Try to extract version from runner directory name
    /// </summary>
    private static string ExtractVersionFromName(string name)
    {
        // Common patterns: GE-Proton9-25, wine-lutris-GE-Proton9-25-x86_64, Proton-9.0-4
        var versionPatterns = new[]
        {
            @"(\d+[\.\-]\d+[\.\-]?\d*)",  // Match version numbers like 9.0-4, 9-25, 10.0
            @"Proton[\-\s]?(\d+[\.\-]\d+)",  // Proton specific
            @"GE[\-\s]?Proton[\-\s]?(\d+[\.\-]\d+)",  // GE-Proton specific
        };
        
        foreach (var pattern in versionPatterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(name, pattern);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }
        
        return "Unknown";
    }

    /// <summary>
    /// Download and install a runner
    /// </summary>
    public async Task<bool> InstallRunnerAsync(string runnerId, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var runner = AvailableRunners.FirstOrDefault(r => r.Id == runnerId);
        if (runner == null)
        {
            Log.Error("Runner not found: {RunnerId}", runnerId);
            return false;
        }

        var installDir = Path.Combine(_runnersDirectory, runner.Type.ToString().ToLower(), runnerId);

        try
        {
            Log.Information("Downloading runner: {Name} {Version}", runner.Name, runner.Version);

            // Create download directory
            Directory.CreateDirectory(installDir);

            // Determine file extension and download path
            var fileName = Path.GetFileName(new Uri(runner.DownloadUrl).AbsolutePath);
            var downloadPath = Path.Combine(_runnersDirectory, fileName);

            // Download the runner archive using the shared HttpClient
            using var request = new HttpRequestMessage(HttpMethod.Get, runner.DownloadUrl);
            request.Headers.Add("User-Agent", "Linlapse/1.0");

            using var response = await SharedHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            var downloadedBytes = 0L;

            await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var fileStream = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                var buffer = new byte[8192];
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    downloadedBytes += bytesRead;

                    // Calculate progress - use actual content length if available, otherwise show incremental progress
                    double downloadProgress;
                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        downloadProgress = (double)downloadedBytes / totalBytes.Value * DownloadProgressMax;
                    }
                    else
                    {
                        // If content length is unknown, show slow incremental progress up to 40%
                        downloadProgress = Math.Min(40.0, downloadedBytes / 10_000_000.0);
                    }
                    progress?.Report(downloadProgress);
                    DownloadProgressChanged?.Invoke(this, (runnerId, downloadProgress));
                }
            }

            // Verify MD5 checksum if available
            Log.Information("Verifying MD5 checksum for {Name}", runner.Name);
            progress?.Report(VerificationProgress);
            DownloadProgressChanged?.Invoke(this, (runnerId, VerificationProgress));
            
            var computedMd5 = await ComputeMd5ChecksumAsync(downloadPath, cancellationToken);
            Log.Information("Computed MD5 checksum: {Checksum} for {Name}", computedMd5, runner.Name);
            
            if (!string.IsNullOrEmpty(runner.Md5Checksum))
            {
                if (!string.Equals(computedMd5, runner.Md5Checksum, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Error("MD5 checksum mismatch for {Name}. Expected: {Expected}, Got: {Actual}", 
                        runner.Name, runner.Md5Checksum, computedMd5);
                    
                    // Clean up the downloaded file
                    if (File.Exists(downloadPath))
                    {
                        File.Delete(downloadPath);
                    }
                    
                    return false;
                }
                Log.Information("MD5 checksum verified successfully for {Name}", runner.Name);
            }
            else
            {
                Log.Information("No MD5 checksum configured for {Name}, skipping verification. Computed: {Checksum}", 
                    runner.Name, computedMd5);
            }

            Log.Information("Extracting runner to {Path}", installDir);

            // Extract the archive
            await ExtractArchiveAsync(downloadPath, installDir, progress, cancellationToken);

            // Find the executable path
            var executablePath = FindRunnerExecutable(installDir, runner.Type);
            if (string.IsNullOrEmpty(executablePath))
            {
                Log.Warning("Could not find executable in extracted runner at {Path}", installDir);
                // Still continue as the executable might be in a subdirectory
            }

            // Register the installed runner
            var installedRunner = new InstalledRunner
            {
                Id = runner.Id,
                Name = runner.Name,
                Version = runner.Version,
                Type = runner.Type,
                InstallPath = installDir,
                ExecutablePath = executablePath ?? installDir
            };

            _settingsService.UpdateSettings(settings =>
            {
                settings.InstalledRunners.RemoveAll(r => r.Id == runnerId);
                settings.InstalledRunners.Add(installedRunner);
            });

            // Clean up download file
            if (File.Exists(downloadPath))
            {
                File.Delete(downloadPath);
            }

            Log.Information("Runner installed successfully: {Name} {Version}", runner.Name, runner.Version);
            progress?.Report(100);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to install runner: {RunnerId}", runnerId);

            // Clean up on failure
            if (Directory.Exists(installDir))
            {
                try { Directory.Delete(installDir, true); } catch { }
            }

            return false;
        }
    }

    private async Task ExtractArchiveAsync(string archivePath, string destDir, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            var extension = Path.GetExtension(archivePath).ToLowerInvariant();
            var fileName = Path.GetFileName(archivePath).ToLowerInvariant();
            
            // Extract to a temporary directory first, then strip single top-level directory if present
            var tempExtractDir = Path.Combine(Path.GetTempPath(), $"linlapse_extract_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempExtractDir);
            
            try
            {
                bool extracted = false;
                
                // Try native tools first for common archive formats
                // This is more reliable and faster than SharpCompress for most formats
                
                // Try tar for .tar.* archives (most wine/proton runners)
                if (fileName.EndsWith(".tar.xz") || fileName.EndsWith(".tar.gz") || 
                    fileName.EndsWith(".tar.zst") || fileName.EndsWith(".tar.bz2") ||
                    extension == ".tar" || extension == ".tgz" || extension == ".txz")
                {
                    try
                    {
                        Log.Information("Extracting with native tar: {Path}", archivePath);
                        ExtractWithSystemTar(archivePath, tempExtractDir);
                        extracted = true;
                    }
                    catch (Exception tarEx)
                    {
                        Log.Warning(tarEx, "Native tar extraction failed, will try other methods");
                    }
                }
                
                // Try 7z for various archive formats
                if (!extracted && TryExtractWith7z(archivePath, tempExtractDir))
                {
                    Log.Information("Extracted with native 7z: {Path}", archivePath);
                    extracted = true;
                }
                
                // Try unzip for .zip files
                if (!extracted && extension == ".zip")
                {
                    try
                    {
                        Log.Information("Extracting with native unzip: {Path}", archivePath);
                        ExtractWithUnzip(archivePath, tempExtractDir);
                        extracted = true;
                    }
                    catch (Exception unzipEx)
                    {
                        Log.Warning(unzipEx, "Native unzip extraction failed, will try SharpCompress");
                    }
                }
                
                // Fallback to SharpCompress for other formats or if native tools failed
                if (!extracted)
                {
                    try
                    {
                        Log.Information("Extracting with SharpCompress: {Path}", archivePath);
                        using var archive = ArchiveFactory.Open(archivePath);
                        var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
                        var totalEntries = entries.Count;
                        var processedEntries = 0;

                        foreach (var entry in entries)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var destinationPath = Path.Combine(tempExtractDir, entry.Key ?? "");
                            var directory = Path.GetDirectoryName(destinationPath);
                            if (!string.IsNullOrEmpty(directory))
                            {
                                Directory.CreateDirectory(directory);
                            }

                            entry.WriteToDirectory(tempExtractDir, new SharpCompress.Common.ExtractionOptions
                            {
                                ExtractFullPath = true,
                                Overwrite = true
                            });

                            processedEntries++;
                            var extractProgress = ExtractionProgressStart + (double)processedEntries / totalEntries * (ExtractionProgressMax - ExtractionProgressStart) * 0.8;
                            progress?.Report(extractProgress);
                        }
                        extracted = true;
                    }
                    catch (Exception sharpCompressEx)
                    {
                        Log.Error(sharpCompressEx, "All extraction methods failed for {Path}", archivePath);
                        throw new Exception($"Failed to extract archive '{archivePath}'. Tried native tools and SharpCompress. Error: {sharpCompressEx.Message}", sharpCompressEx);
                    }
                }
                
                // Now move files to destination, stripping single top-level directory if present
                MoveExtractedFilesToDestination(tempExtractDir, destDir);
                progress?.Report(ExtractionProgressMax);
            }
            finally
            {
                // Clean up temp directory
                try
                {
                    if (Directory.Exists(tempExtractDir))
                    {
                        Directory.Delete(tempExtractDir, true);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to clean up temp extraction directory: {Path}", tempExtractDir);
                }
            }
        }, cancellationToken);
    }
    
    /// <summary>
    /// Move extracted files to destination, stripping single top-level directory if present
    /// </summary>
    private static void MoveExtractedFilesToDestination(string sourceDir, string destDir)
    {
        var topLevelEntries = Directory.GetFileSystemEntries(sourceDir);
        
        // Check if there's a single top-level directory (common for wine/proton archives)
        if (topLevelEntries.Length == 1 && Directory.Exists(topLevelEntries[0]))
        {
            var singleTopDir = topLevelEntries[0];
            Log.Information("Stripping single top-level directory: {Dir}", Path.GetFileName(singleTopDir));
            
            // Move contents of the single directory to destination
            foreach (var entry in Directory.GetFileSystemEntries(singleTopDir))
            {
                var entryName = Path.GetFileName(entry);
                var destPath = Path.Combine(destDir, entryName);
                
                if (Directory.Exists(entry))
                {
                    MoveDirectoryContents(entry, destPath);
                }
                else
                {
                    if (File.Exists(destPath))
                        File.Delete(destPath);
                    File.Move(entry, destPath);
                }
            }
        }
        else
        {
            // No single top-level directory, move everything as-is
            foreach (var entry in topLevelEntries)
            {
                var entryName = Path.GetFileName(entry);
                var destPath = Path.Combine(destDir, entryName);
                
                if (Directory.Exists(entry))
                {
                    MoveDirectoryContents(entry, destPath);
                }
                else
                {
                    if (File.Exists(destPath))
                        File.Delete(destPath);
                    File.Move(entry, destPath);
                }
            }
        }
    }
    
    /// <summary>
    /// Recursively move directory contents, handling symlinks properly
    /// </summary>
    private static void MoveDirectoryContents(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        
        // First, try using native mv command which handles symlinks correctly
        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "sh",
                    Arguments = $"-c \"mv -f \\\"{sourceDir}\\\"/* \\\"{destDir}\\\"/\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            
            process.Start();
            process.WaitForExit(60000);
            
            if (process.ExitCode == 0)
            {
                return;
            }
            
            Log.Warning("Native mv failed, falling back to manual move");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Native mv not available, using manual move");
        }
        
        // Fallback: manual move with symlink handling
        foreach (var entry in Directory.GetFileSystemEntries(sourceDir))
        {
            var name = Path.GetFileName(entry);
            var destPath = Path.Combine(destDir, name);
            
            try
            {
                var fileInfo = new FileInfo(entry);
                
                // Check if it's a symlink
                if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // It's a symlink - recreate it at the destination
                    if (File.Exists(destPath) || Directory.Exists(destPath))
                    {
                        try { File.Delete(destPath); } catch { }
                        try { Directory.Delete(destPath, true); } catch { }
                    }
                    
                    // Read symlink target and recreate
                    var target = ReadSymlinkTarget(entry);
                    if (!string.IsNullOrEmpty(target))
                    {
                        CreateSymlink(target, destPath);
                    }
                }
                else if (Directory.Exists(entry))
                {
                    // It's a directory - recurse
                    MoveDirectoryContents(entry, destPath);
                }
                else if (File.Exists(entry))
                {
                    // It's a regular file
                    if (File.Exists(destPath))
                        File.Delete(destPath);
                    File.Move(entry, destPath);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to move {Entry}, skipping", entry);
            }
        }
    }
    
    /// <summary>
    /// Read the target of a symlink
    /// </summary>
    private static string? ReadSymlinkTarget(string path)
    {
        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "readlink",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            
            process.Start();
            var target = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            
            return process.ExitCode == 0 ? target : null;
        }
        catch
        {
            return null;
        }
    }
    
    /// <summary>
    /// Create a symlink
    /// </summary>
    private static void CreateSymlink(string target, string linkPath)
    {
        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ln",
                    Arguments = $"-sf \"{target}\" \"{linkPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            
            process.Start();
            process.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to create symlink from {Target} to {Link}", target, linkPath);
        }
    }

    private static void ExtractWithSystemTar(string archivePath, string destDir)
    {
        var fileName = Path.GetFileName(archivePath).ToLowerInvariant();
        
        // Determine tar arguments based on compression type
        string args;
        if (fileName.EndsWith(".tar.zst"))
        {
            args = $"--zstd -xf \"{archivePath}\" -C \"{destDir}\"";
        }
        else if (fileName.EndsWith(".tar.xz") || fileName.EndsWith(".txz"))
        {
            args = $"-xJf \"{archivePath}\" -C \"{destDir}\"";
        }
        else if (fileName.EndsWith(".tar.gz") || fileName.EndsWith(".tgz"))
        {
            args = $"-xzf \"{archivePath}\" -C \"{destDir}\"";
        }
        else if (fileName.EndsWith(".tar.bz2"))
        {
            args = $"-xjf \"{archivePath}\" -C \"{destDir}\"";
        }
        else
        {
            args = $"-xf \"{archivePath}\" -C \"{destDir}\"";
        }

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "tar",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd();
            throw new Exception($"tar extraction failed (exit code {process.ExitCode}): {error}");
        }
    }
    
    private static bool TryExtractWith7z(string archivePath, string destDir)
    {
        // Check if 7z is available
        try
        {
            var checkProcess = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "7z",
                    Arguments = "--help",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            
            checkProcess.Start();
            checkProcess.WaitForExit(1000);
            
            if (checkProcess.ExitCode != 0)
            {
                return false;
            }
        }
        catch
        {
            // 7z not available
            return false;
        }
        
        try
        {
            Log.Information("Extracting with native 7z: {Path}", archivePath);
            
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "7z",
                    Arguments = $"x \"{archivePath}\" -o\"{destDir}\" -y",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                var error = process.StandardError.ReadToEnd();
                Log.Warning("7z extraction failed (exit code {ExitCode}): {Error}", process.ExitCode, error);
                return false;
            }
            
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "7z extraction failed");
            return false;
        }
    }
    
    private static void ExtractWithUnzip(string archivePath, string destDir)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "unzip",
                Arguments = $"-o \"{archivePath}\" -d \"{destDir}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd();
            throw new Exception($"unzip extraction failed (exit code {process.ExitCode}): {error}");
        }
    }

    private static string? FindRunnerExecutable(string installDir, WineRunnerType type)
    {
        // Look for common executable locations
        var searchPatterns = type == WineRunnerType.Wine
            ? new[] { "**/bin/wine", "**/bin/wine64" }
            : new[] { "**/proton", "**/proton.py" };

        foreach (var pattern in searchPatterns)
        {
            var files = Directory.EnumerateFiles(installDir, Path.GetFileName(pattern), SearchOption.AllDirectories);
            var executable = files.FirstOrDefault();
            if (!string.IsNullOrEmpty(executable))
            {
                return executable;
            }
        }

        // Direct search in subdirectories
        if (type == WineRunnerType.Wine)
        {
            var wineExe = Directory.EnumerateFiles(installDir, "wine*", SearchOption.AllDirectories)
                .FirstOrDefault(f => Path.GetFileName(f) == "wine" || Path.GetFileName(f) == "wine64");
            if (!string.IsNullOrEmpty(wineExe))
                return wineExe;
        }
        else
        {
            var protonExe = Directory.EnumerateFiles(installDir, "proton", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(protonExe))
                return protonExe;
        }

        return null;
    }

    /// <summary>
    /// Uninstall a runner
    /// </summary>
    public async Task<bool> UninstallRunnerAsync(string runnerId)
    {
        var installed = _settingsService.Settings.InstalledRunners.FirstOrDefault(r => r.Id == runnerId);
        if (installed == null)
        {
            Log.Warning("Runner not installed: {RunnerId}", runnerId);
            return false;
        }

        try
        {
            Log.Information("Uninstalling runner: {Name}", installed.Name);

            // Delete the installation directory
            if (Directory.Exists(installed.InstallPath))
            {
                await Task.Run(() => Directory.Delete(installed.InstallPath, true));
            }

            // Remove from settings
            _settingsService.UpdateSettings(settings =>
            {
                settings.InstalledRunners.RemoveAll(r => r.Id == runnerId);
            });

            Log.Information("Runner uninstalled: {Name}", installed.Name);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to uninstall runner: {RunnerId}", runnerId);
            return false;
        }
    }

    /// <summary>
    /// Get the executable path for a specific installed runner
    /// </summary>
    public string? GetRunnerExecutablePath(string runnerId)
    {
        var installed = _settingsService.Settings.InstalledRunners.FirstOrDefault(r => r.Id == runnerId);
        if (installed == null || !Directory.Exists(installed.InstallPath))
        {
            return null;
        }

        // If we have a stored executable path, verify it exists
        if (!string.IsNullOrEmpty(installed.ExecutablePath) && File.Exists(installed.ExecutablePath))
        {
            return installed.ExecutablePath;
        }

        // Try to find the executable
        var runner = AvailableRunners.FirstOrDefault(r => r.Id == runnerId);
        if (runner != null)
        {
            return FindRunnerExecutable(installed.InstallPath, runner.Type);
        }

        return null;
    }
    
    /// <summary>
    /// Compute MD5 checksum of a file
    /// </summary>
    private static async Task<string> ComputeMd5ChecksumAsync(string filePath, CancellationToken cancellationToken = default)
    {
        using var md5 = MD5.Create();
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        var hash = await md5.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
