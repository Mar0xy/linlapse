namespace Linlapse.Models;

/// <summary>
/// Represents download progress information
/// </summary>
public class DownloadProgress
{
    public string FileName { get; set; } = string.Empty;
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public double SpeedBytesPerSecond { get; set; }
    public TimeSpan EstimatedTimeRemaining { get; set; }
    public double PercentComplete => TotalBytes > 0 ? (double)BytesDownloaded / TotalBytes * 100 : 0;
    public DownloadState State { get; set; } = DownloadState.Pending;
}

/// <summary>
/// Download state
/// </summary>
public enum DownloadState
{
    Pending,
    Downloading,
    Paused,
    Completed,
    Failed,
    Cancelled,
    Verifying,
    Extracting
}

/// <summary>
/// File verification result
/// </summary>
public class FileVerificationResult
{
    public string FilePath { get; set; } = string.Empty;
    public bool IsValid { get; set; }
    public string? ExpectedHash { get; set; }
    public string? ActualHash { get; set; }
    public long ExpectedSize { get; set; }
    public long ActualSize { get; set; }
    public FileIssueType? Issue { get; set; }
}

/// <summary>
/// Type of file issue detected during verification
/// </summary>
public enum FileIssueType
{
    None,
    Missing,
    SizeMismatch,
    HashMismatch,
    Corrupted,
    Extra
}

/// <summary>
/// Repair operation status
/// </summary>
public class RepairProgress
{
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public int BrokenFiles { get; set; }
    public int RepairedFiles { get; set; }
    public long TotalBytesToRepair { get; set; }
    public long BytesRepaired { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public RepairState State { get; set; } = RepairState.Idle;
    public double PercentComplete => TotalFiles > 0 ? (double)ProcessedFiles / TotalFiles * 100 : 0;
}

/// <summary>
/// Repair operation state
/// </summary>
public enum RepairState
{
    Idle,
    Scanning,
    Repairing,
    Completed,
    Failed,
    Cancelled
}
