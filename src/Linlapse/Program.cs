using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Serilog;
using Linlapse.Services;

namespace Linlapse;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Pre-load native libraries bundled with the application (e.g., libzstd for Sophon)
        PreloadNativeLibraries();
        
        // Configure logging
        var logPath = Path.Combine(SettingsService.GetDataDirectory(), "logs", "linlapse-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File(logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        try
        {
            Log.Information("Starting Linlapse...");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
    
    /// <summary>
    /// Pre-loads native libraries bundled with the application.
    /// This ensures libraries like libzstd are available before Sophon tries to use them.
    /// </summary>
    private static void PreloadNativeLibraries()
    {
        var appDir = AppContext.BaseDirectory;
        
        // List of native libraries to preload
        var librariesToPreload = new[]
        {
            // libzstd - required by Hi3Helper.Sophon for decompression
            ("libzstd.so.1", "zstd"),
            ("libzstd.so", "zstd"),
        };
        
        foreach (var (fileName, libraryName) in librariesToPreload)
        {
            var libraryPath = Path.Combine(appDir, fileName);
            if (File.Exists(libraryPath))
            {
                try
                {
                    // Pre-load the library so it's available for P/Invoke
                    if (NativeLibrary.TryLoad(libraryPath, out var handle))
                    {
                        Console.WriteLine($"Pre-loaded native library: {fileName}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to pre-load native library {fileName}: {ex.Message}");
                }
            }
        }
        
        // Also try to load from system paths if bundled library wasn't found
        TryLoadSystemLibrary("libzstd.so.1");
        TryLoadSystemLibrary("libzstd.so");
    }
    
    private static void TryLoadSystemLibrary(string libraryName)
    {
        try
        {
            if (NativeLibrary.TryLoad(libraryName, out _))
            {
                Console.WriteLine($"Loaded system library: {libraryName}");
            }
        }
        catch
        {
            // Ignore - library might not be installed
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
