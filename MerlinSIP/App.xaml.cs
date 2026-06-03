using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using MerlinSip.ViewModels;

namespace MerlinSip;

public partial class App : System.Windows.Application
{
    public IServiceProvider Services { get; }
    public new static App Current => (App)System.Windows.Application.Current;

    private MerlinSip.Services.SingleInstanceService? _singleInstanceService;

    public App()
    {
        Services = ConfigureServices();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        
        // Services
        services.AddSingleton<MerlinSip.Services.ContactStore>();
        services.AddSingleton<MerlinSip.Services.CallHistoryStore>();
        services.AddSingleton<MerlinSip.Services.ChatMessageStore>();
        services.AddSingleton<MerlinSip.Services.AppCacheService>();
        services.AddSingleton<MerlinSip.Services.DeviceDiscoveryService>();
        services.AddSingleton<MerlinSip.Services.SipRegistrationService>();
        services.AddSingleton<MerlinSip.Services.LicenseService>();
        services.AddSingleton<MerlinSip.Services.RingtonePlayer>();
        services.AddSingleton<MerlinSip.Services.UpdateService>();
        services.AddSingleton<MerlinSip.Services.ProvisioningService>();
        
        // ViewModels
        services.AddTransient<MainViewModel>();
        
        return services.BuildServiceProvider();
    }

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _singleInstanceService = new MerlinSip.Services.SingleInstanceService();
        if (!_singleInstanceService.IsPrimaryInstance)
        {
            await MerlinSip.Services.SingleInstanceService.NotifyExistingInstanceAsync();
            Shutdown();
            return;
        }

        var cache = new MerlinSip.Services.AppCacheService();
        var config = await cache.LoadSettingsAsync();

        if (config is null)
        {
            var startupWindow = new StartupWindow();
            var accepted = startupWindow.ShowDialog() == true;
            if (!accepted || startupWindow.Config is null)
            {
                Shutdown();
                return;
            }

            config = startupWindow.Config;
            await cache.SaveSettingsAsync(config);
        }

        var viewModel = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<MainViewModel>(Services);
        var mainWindow = new MainWindow(config, viewModel);
        _singleInstanceService.ActivationRequested += (_, _) => mainWindow.RestoreFromTray();
        _singleInstanceService.StartListening(Dispatcher);
        MainWindow = mainWindow;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceService?.Dispose();
        _singleInstanceService = null;
        base.OnExit(e);
    }
}
