using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Linlapse.Models;

/// <summary>
/// Represents a supported game in the launcher
/// </summary>
public class GameInfo : INotifyPropertyChanged
{
    private string _id = string.Empty;
    private string _name = string.Empty;
    private string _displayName = string.Empty;
    private GameType _gameType;
    private GameRegion _region;
    private string _installPath = string.Empty;
    private string _executablePath = string.Empty;
    private string _version = string.Empty;
    private string? _backgroundImagePath;
    private string? _logoImagePath;
    private bool _isInstalled;
    private long _installSize;
    private DateTime? _lastPlayed;
    private TimeSpan _totalPlayTime;
    private GameState _state = GameState.NotInstalled;
    private bool _isDownloading;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public string Id
    {
        get => _id;
        set { if (_id != value) { _id = value; OnPropertyChanged(); } }
    }

    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnPropertyChanged(); } }
    }

    public string DisplayName
    {
        get => _displayName;
        set { if (_displayName != value) { _displayName = value; OnPropertyChanged(); } }
    }

    public GameType GameType
    {
        get => _gameType;
        set { if (_gameType != value) { _gameType = value; OnPropertyChanged(); } }
    }

    public GameRegion Region
    {
        get => _region;
        set { if (_region != value) { _region = value; OnPropertyChanged(); } }
    }

    public string InstallPath
    {
        get => _installPath;
        set { if (_installPath != value) { _installPath = value; OnPropertyChanged(); } }
    }

    public string ExecutablePath
    {
        get => _executablePath;
        set { if (_executablePath != value) { _executablePath = value; OnPropertyChanged(); } }
    }

    public string Version
    {
        get => _version;
        set { if (_version != value) { _version = value; OnPropertyChanged(); } }
    }

    public string? BackgroundImagePath
    {
        get => _backgroundImagePath;
        set { if (_backgroundImagePath != value) { _backgroundImagePath = value; OnPropertyChanged(); } }
    }

    public string? LogoImagePath
    {
        get => _logoImagePath;
        set { if (_logoImagePath != value) { _logoImagePath = value; OnPropertyChanged(); } }
    }

    public bool IsInstalled
    {
        get => _isInstalled;
        set { if (_isInstalled != value) { _isInstalled = value; OnPropertyChanged(); } }
    }

    public long InstallSize
    {
        get => _installSize;
        set { if (_installSize != value) { _installSize = value; OnPropertyChanged(); } }
    }

    public DateTime? LastPlayed
    {
        get => _lastPlayed;
        set { if (_lastPlayed != value) { _lastPlayed = value; OnPropertyChanged(); } }
    }

    public TimeSpan TotalPlayTime
    {
        get => _totalPlayTime;
        set { if (_totalPlayTime != value) { _totalPlayTime = value; OnPropertyChanged(); } }
    }

    public GameState State
    {
        get => _state;
        set { if (_state != value) { _state = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// Indicates if the game is currently being downloaded
    /// This is a UI-only property (not serialized) to track download state
    /// </summary>
    [JsonIgnore]
    public bool IsDownloading
    {
        get => _isDownloading;
        set { if (_isDownloading != value) { _isDownloading = value; OnPropertyChanged(); } }
    }
}

/// <summary>
/// Types of supported games
/// </summary>
public enum GameType
{
    HonkaiImpact3rd,
    GenshinImpact,
    HonkaiStarRail,
    ZenlessZoneZero,
    Custom
}

/// <summary>
/// Game server regions
/// </summary>
public enum GameRegion
{
    Global,
    China,
    SEA,
    Europe,
    America,
    Asia,
    TW_HK_MO
}

/// <summary>
/// Current state of the game
/// </summary>
public enum GameState
{
    NotInstalled,
    NeedsUpdate,
    Ready,
    Installing,
    Updating,
    Repairing,
    Running,
    Preloading
}
