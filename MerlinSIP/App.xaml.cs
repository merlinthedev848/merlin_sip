using System.Windows;

namespace MerlinSip;

public partial class App : System.Windows.Application
{
    private Services.SingleInstanceService? _singleInstanceService;

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _singleInstanceService = new Services.SingleInstanceService();
        if (!_singleInstanceService.IsPrimaryInstance)
        {
            await Services.SingleInstanceService.NotifyExistingInstanceAsync();
            Shutdown();
            return;
        }

        var cache = new Services.AppCacheService();
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

        var mainWindow = new MainWindow(config);
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
