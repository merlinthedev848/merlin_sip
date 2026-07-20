using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MerlinSip.Services;
using MerlinSip.Models;

namespace MerlinSIP.Mobile.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly AppCacheService _appCacheService;
    private SipRegistrationService? _sipService;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Initializing...";

    [ObservableProperty]
    public partial string TargetNumber { get; set; } = "";

    [ObservableProperty]
    public partial bool IsRegistered { get; set; }

    public ObservableCollection<string> CallLogs { get; } = new();

    public MainViewModel()
    {
        _appCacheService = new AppCacheService();
        _ = InitializeSipAsync();
    }

    private async Task InitializeSipAsync()
    {
        try
        {
            StatusMessage = "Loading config...";
            var config = _appCacheService.GetConfiguration();

            // Setup default config for mobile testing if empty
            if (string.IsNullOrEmpty(config.Server))
            {
                config.Server = "sip.example.com";
                config.Username = "1000";
                config.Password = "password";
                _appCacheService.SaveConfiguration(config);
            }

            _sipService = new SipRegistrationService(config, new SipLogger());
            _sipService.RegistrationStateChanged += (sender, state) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    StatusMessage = state;
                    IsRegistered = state.Contains("Registered");
                });
            };

            await _sipService.StartAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void AppendNumber(string digit)
    {
        TargetNumber += digit;
    }

    [RelayCommand]
    private void ClearNumber()
    {
        TargetNumber = "";
    }

    [RelayCommand]
    private async Task CallAsync()
    {
        if (string.IsNullOrWhiteSpace(TargetNumber) || _sipService == null) return;
        
        CallLogs.Add($"Calling {TargetNumber}...");
        await _sipService.CallAsync(TargetNumber, CancellationToken.None);
    }
}

internal class SipLogger : Microsoft.Extensions.Logging.ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
    }
}
