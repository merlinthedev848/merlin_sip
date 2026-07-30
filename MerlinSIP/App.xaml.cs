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

        var telArg = e.Args.FirstOrDefault(arg => arg.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("callto:", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("sip:", StringComparison.OrdinalIgnoreCase));
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _singleInstanceService = new Services.SingleInstanceService();
        if (!_singleInstanceService.IsPrimaryInstance)
        {
            await Services.SingleInstanceService.NotifyExistingInstanceAsync(telArg ?? "activate");
            Shutdown();
            return;
        }

        Services.ProtocolHandlerService.RegisterProtocolHandlers();
        var cache = new Services.AppCacheService();
        var config = await cache.LoadSettingsAsync();
        Services.AppCacheService.ActiveConfig = config;

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
            Services.AppCacheService.ActiveConfig = config;
            await cache.SaveSettingsAsync(config);
        }

        var mainWindow = new MainWindow(config);
        _singleInstanceService.ActivationRequested += (_, msg) => 
        {
            mainWindow.RestoreFromTray();
            if (!string.IsNullOrWhiteSpace(msg) && msg != "activate")
            {
                mainWindow.HandleTelProtocolLaunch(msg);
            }
        };
        _singleInstanceService.StartListening(Dispatcher);
        MainWindow = mainWindow;
        mainWindow.Show();
        if (!string.IsNullOrWhiteSpace(telArg))
        {
            mainWindow.HandleTelProtocolLaunch(telArg);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceService?.Dispose();
        _singleInstanceService = null;
        base.OnExit(e);
    }
}
