using System.Text.Json;
using System.Text.Json.Serialization;
using Linlapse.Models;
using Serilog;

namespace Linlapse.Services;

/// <summary>
/// Service for managing application settings
/// </summary>
public class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _settingsPath;
    private AppSettings _settings;

    public AppSettings Settings => _settings;

    public event EventHandler<AppSettings>? SettingsChanged;

    public SettingsService()
    {
        var configDir = GetConfigDirectory();
        Directory.CreateDirectory(configDir);
        _settingsPath = Path.Combine(configDir, "settings.json");
        _settings = LoadSettings();
    }

    private static string GetConfigDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configBase = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") 
                         ?? Path.Combine(home, ".config");
        return Path.Combine(configBase, "linlapse");
    }

    public static string GetDataDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataBase = Environment.GetEnvironmentVariable("XDG_DATA_HOME") 
                       ?? Path.Combine(home, ".local", "share");
        var dataDir = Path.Combine(dataBase, "linlapse");
        Directory.CreateDirectory(dataDir);
        return dataDir;
    }

    public static string GetCacheDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cacheBase = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") 
                        ?? Path.Combine(home, ".cache");
        var cacheDir = Path.Combine(cacheBase, "linlapse");
        Directory.CreateDirectory(cacheDir);
        return cacheDir;
    }

    private AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings != null)
                {
                    Log.Information("Settings loaded from {Path}", _settingsPath);
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load settings from {Path}", _settingsPath);
        }

        Log.Information("Using default settings");
        return CreateDefaultSettings();
    }

    private AppSettings CreateDefaultSettings()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new AppSettings
        {
            DefaultGameInstallPath = Path.Combine(home, "Games"),
            WinePrefixPath = Path.Combine(home, ".local", "share", "linlapse", "wine-prefix"),
            UseSystemWine = true,
            EnableDiscordRpc = true,
            MinimizeToTray = true,
            CheckUpdatesOnStartup = true,
            EnableLogging = true,
            Theme = ThemeMode.System,
            Language = "en-US"
        };
    }

    public void SaveSettings()
    {
        try
        {
            var json = JsonSerializer.Serialize(_settings, JsonOptions);
            File.WriteAllText(_settingsPath, json);
            Log.Information("Settings saved to {Path}", _settingsPath);
            SettingsChanged?.Invoke(this, _settings);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save settings to {Path}", _settingsPath);
            throw;
        }
    }

    public void UpdateSettings(Action<AppSettings> updateAction)
    {
        updateAction(_settings);
        SaveSettings();
    }
}
