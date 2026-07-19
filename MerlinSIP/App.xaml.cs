using System.Windows;

namespace MerlinSip;

public partial class App : System.Windows.Application
{
    private Services.SingleInstanceService? _singleInstanceService;

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += (s, args) =>
        {
            Services.DebugLog.Write($"UNHANDLED DISPATCHER EXCEPTION: {args.Exception}");
            args.Handled = true;
            System.Windows.MessageBox.Show($"An unexpected error occurred: {args.Exception.Message}\n\nCheck debug.log for details.", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            Services.DebugLog.Write($"UNHANDLED APPDOMAIN EXCEPTION: {args.ExceptionObject}");
        };

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
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceService?.Dispose();
        _singleInstanceService = null;
        base.OnExit(e);
    }
}
