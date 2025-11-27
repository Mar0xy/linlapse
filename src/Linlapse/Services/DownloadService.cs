using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using Linlapse.Models;
using Serilog;

namespace Linlapse.Services;

/// <summary>
/// Service for downloading game files with multi-session support
/// </summary>
public class DownloadService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly SettingsService _settingsService;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeDownloads = new();
    private readonly SemaphoreSlim _downloadSemaphore;

    public event EventHandler<DownloadProgress>? DownloadProgressChanged;
    public event EventHandler<string>? DownloadCompleted;
    public event EventHandler<(string FileName, Exception Error)>? DownloadFailed;

    public DownloadService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Linlapse/1.0");
        _downloadSemaphore = new SemaphoreSlim(_settingsService.Settings.MaxConcurrentDownloads);
    }

    /// <summary>
    /// Download a file with progress reporting and resume support
    /// </summary>
    public async Task<bool> DownloadFileAsync(
        string url, 
        string destinationPath, 
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fileName = Path.GetFileName(destinationPath);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeDownloads[fileName] = cts;

        try
        {
            await _downloadSemaphore.WaitAsync(cts.Token);
            
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Check if partial download exists for resume
            long existingLength = 0;
            var tempPath = destinationPath + ".partial";
            if (File.Exists(tempPath))
            {
                existingLength = new FileInfo(tempPath).Length;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (existingLength > 0)
            {
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingLength, null);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
            {
                throw new HttpRequestException($"Failed to download: {response.StatusCode}");
            }

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            if (existingLength > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent)
            {
                totalBytes += existingLength;
            }
            else
            {
                existingLength = 0; // Server doesn't support resume
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }

            var downloadProgress = new DownloadProgress
            {
                FileName = fileName,
                TotalBytes = totalBytes,
                BytesDownloaded = existingLength,
                State = DownloadState.Downloading
            };

            await using var contentStream = await response.Content.ReadAsStreamAsync(cts.Token);
            await using var fileStream = new FileStream(tempPath, 
                existingLength > 0 ? FileMode.Append : FileMode.Create, 
                FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            var bytesRead = 0;
            var lastProgressReport = DateTime.UtcNow;
            var startTime = DateTime.UtcNow;
            var bytesAtStart = existingLength;

            while ((bytesRead = await contentStream.ReadAsync(buffer, cts.Token)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cts.Token);
                downloadProgress.BytesDownloaded += bytesRead;

                // Report progress every 100ms
                if ((DateTime.UtcNow - lastProgressReport).TotalMilliseconds >= 100)
                {
                    var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                    if (elapsed > 0)
                    {
                        downloadProgress.SpeedBytesPerSecond = (downloadProgress.BytesDownloaded - bytesAtStart) / elapsed;
                        if (downloadProgress.SpeedBytesPerSecond > 0)
                        {
                            var remainingBytes = totalBytes - downloadProgress.BytesDownloaded;
                            downloadProgress.EstimatedTimeRemaining = TimeSpan.FromSeconds(remainingBytes / downloadProgress.SpeedBytesPerSecond);
                        }
                    }

                    progress?.Report(downloadProgress);
                    DownloadProgressChanged?.Invoke(this, downloadProgress);
                    lastProgressReport = DateTime.UtcNow;
                }

                // Apply speed limit if configured
                var speedLimit = _settingsService.Settings.DownloadSpeedLimit;
                if (speedLimit > 0)
                {
                    var targetTime = bytesRead / (double)speedLimit;
                    var actualTime = (DateTime.UtcNow - lastProgressReport).TotalSeconds;
                    if (actualTime < targetTime)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(targetTime - actualTime), cts.Token);
                    }
                }
            }

            // Move temp file to final destination
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
            File.Move(tempPath, destinationPath);

            downloadProgress.State = DownloadState.Completed;
            progress?.Report(downloadProgress);
            DownloadCompleted?.Invoke(this, fileName);
            
            Log.Information("Download completed: {FileName}", fileName);
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.Information("Download cancelled: {FileName}", fileName);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Download failed: {FileName}", fileName);
            DownloadFailed?.Invoke(this, (fileName, ex));
            return false;
        }
        finally
        {
            _activeDownloads.TryRemove(fileName, out _);
            _downloadSemaphore.Release();
        }
    }

    /// <summary>
    /// Download multiple files concurrently
    /// </summary>
    public async Task<int> DownloadFilesAsync(
        IEnumerable<(string Url, string DestinationPath)> files,
        IProgress<(int Completed, int Total, DownloadProgress? Current)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fileList = files.ToList();
        var completed = 0;
        var total = fileList.Count;
        var successful = 0;

        var tasks = fileList.Select(async file =>
        {
            var fileProgress = new Progress<DownloadProgress>(p =>
            {
                progress?.Report((completed, total, p));
            });

            var success = await DownloadFileAsync(file.Url, file.DestinationPath, fileProgress, cancellationToken);
            
            Interlocked.Increment(ref completed);
            if (success) Interlocked.Increment(ref successful);
            
            progress?.Report((completed, total, null));
        });

        await Task.WhenAll(tasks);
        return successful;
    }

    /// <summary>
    /// Cancel a specific download
    /// </summary>
    public void CancelDownload(string fileName)
    {
        if (_activeDownloads.TryGetValue(fileName, out var cts))
        {
            cts.Cancel();
        }
    }

    /// <summary>
    /// Cancel all active downloads
    /// </summary>
    public void CancelAllDownloads()
    {
        foreach (var cts in _activeDownloads.Values)
        {
            cts.Cancel();
        }
    }

    /// <summary>
    /// Verify file integrity using hash
    /// </summary>
    public async Task<bool> VerifyFileHashAsync(string filePath, string expectedHash, HashAlgorithmName algorithm)
    {
        if (!File.Exists(filePath))
            return false;

        try
        {
            await using var stream = File.OpenRead(filePath);
            using var hashAlgorithm = algorithm.Name switch
            {
                "MD5" => (HashAlgorithm)MD5.Create(),
                "SHA256" => SHA256.Create(),
                "SHA1" => SHA1.Create(),
                _ => throw new ArgumentException($"Unsupported algorithm: {algorithm.Name}")
            };

            var hash = await hashAlgorithm.ComputeHashAsync(stream);
            var actualHash = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            return actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error verifying hash for {FilePath}", filePath);
            return false;
        }
    }

    public void Dispose()
    {
        CancelAllDownloads();
        _httpClient.Dispose();
        _downloadSemaphore.Dispose();
    }
}
