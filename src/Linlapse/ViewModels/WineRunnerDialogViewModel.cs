using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linlapse.Models;
using Linlapse.Services;
using Serilog;

namespace Linlapse.ViewModels;

/// <summary>
/// ViewModel for wine/proton runner download dialog
/// </summary>
public partial class WineRunnerDialogViewModel : ViewModelBase
{
    private readonly WineRunnerService _runnerService;
    private CancellationTokenSource? _downloadCts;
    
    [ObservableProperty]
    private bool _isDownloading;
    
    [ObservableProperty]
    private double _downloadProgress;
    
    [ObservableProperty]
    private string _statusMessage = "Select a runner to download";
    
    [ObservableProperty]
    private WineRunnerViewModel? _selectedRunner;
    
    public ObservableCollection<WineRunnerViewModel> WineRunners { get; } = new();
    public ObservableCollection<WineRunnerViewModel> ProtonRunners { get; } = new();
    
    public event EventHandler? DialogClosed;
    public event EventHandler? RunnersUpdated;
    
    public WineRunnerDialogViewModel(WineRunnerService runnerService)
    {
        _runnerService = runnerService;
        _runnerService.DownloadProgressChanged += OnDownloadProgressChanged;
        
        LoadRunners();
    }
    
    private void OnDownloadProgressChanged(object? sender, (string RunnerId, double Progress) e)
    {
        if (SelectedRunner?.Id == e.RunnerId)
        {
            DownloadProgress = e.Progress;
            StatusMessage = e.Progress < 50 
                ? $"Downloading: {e.Progress:F1}%" 
                : $"Extracting: {(e.Progress - 50) * 2:F1}%";
        }
    }
    
    private void LoadRunners()
    {
        WineRunners.Clear();
        ProtonRunners.Clear();
        
        var availableRunners = _runnerService.GetAvailableRunners();
        
        foreach (var runner in availableRunners)
        {
            var vm = new WineRunnerViewModel(runner);
            
            if (runner.Type == WineRunnerType.Wine)
            {
                WineRunners.Add(vm);
            }
            else
            {
                ProtonRunners.Add(vm);
            }
        }
    }
    
    [RelayCommand]
    private async Task DownloadRunnerAsync(WineRunnerViewModel runner)
    {
        if (IsDownloading || runner.IsInstalled)
        {
            return;
        }
        
        try
        {
            SelectedRunner = runner;
            IsDownloading = true;
            DownloadProgress = 0;
            StatusMessage = $"Downloading {runner.Name} {runner.Version}...";
            
            _downloadCts = new CancellationTokenSource();
            
            var progress = new Progress<double>(p =>
            {
                DownloadProgress = p;
                StatusMessage = p < 50 
                    ? $"Downloading {runner.Name}: {p:F1}%" 
                    : $"Extracting {runner.Name}: {(p - 50) * 2:F1}%";
            });
            
            var success = await _runnerService.InstallRunnerAsync(runner.Id, progress, _downloadCts.Token);
            
            if (success)
            {
                runner.IsInstalled = true;
                StatusMessage = $"{runner.Name} {runner.Version} installed successfully!";
                RunnersUpdated?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                StatusMessage = $"Failed to install {runner.Name}";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Download cancelled";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download runner {RunnerId}", runner.Id);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }
    
    [RelayCommand]
    private async Task UninstallRunnerAsync(WineRunnerViewModel runner)
    {
        if (IsDownloading || !runner.IsInstalled)
        {
            return;
        }
        
        try
        {
            StatusMessage = $"Uninstalling {runner.Name}...";
            
            var success = await _runnerService.UninstallRunnerAsync(runner.Id);
            
            if (success)
            {
                runner.IsInstalled = false;
                runner.InstallPath = null;
                StatusMessage = $"{runner.Name} uninstalled";
                RunnersUpdated?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                StatusMessage = $"Failed to uninstall {runner.Name}";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to uninstall runner {RunnerId}", runner.Id);
            StatusMessage = $"Error: {ex.Message}";
        }
    }
    
    [RelayCommand]
    private void CancelDownload()
    {
        _downloadCts?.Cancel();
    }
    
    [RelayCommand]
    private void Close()
    {
        _downloadCts?.Cancel();
        _runnerService.DownloadProgressChanged -= OnDownloadProgressChanged;
        DialogClosed?.Invoke(this, EventArgs.Empty);
    }
    
    public void Cleanup()
    {
        _runnerService.DownloadProgressChanged -= OnDownloadProgressChanged;
        _downloadCts?.Dispose();
    }
}

/// <summary>
/// ViewModel wrapper for WineRunner with observable properties
/// </summary>
public partial class WineRunnerViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _id;
    
    [ObservableProperty]
    private string _name;
    
    [ObservableProperty]
    private string _version;
    
    [ObservableProperty]
    private string _description;
    
    [ObservableProperty]
    private WineRunnerType _type;
    
    [ObservableProperty]
    private long _size;
    
    [ObservableProperty]
    private bool _isInstalled;
    
    [ObservableProperty]
    private string? _installPath;
    
    public string SizeText => FormatSize(Size);
    
    public WineRunnerViewModel(WineRunner runner)
    {
        _id = runner.Id;
        _name = runner.Name;
        _version = runner.Version;
        _description = runner.Description;
        _type = runner.Type;
        _size = runner.Size;
        _isInstalled = runner.IsInstalled;
        _installPath = runner.InstallPath;
    }
    
    private static string FormatSize(long bytes)
    {
        if (bytes >= 1_000_000_000)
            return $"{bytes / 1_000_000_000.0:F1} GB";
        if (bytes >= 1_000_000)
            return $"{bytes / 1_000_000.0:F0} MB";
        return $"{bytes / 1_000.0:F0} KB";
    }
}
