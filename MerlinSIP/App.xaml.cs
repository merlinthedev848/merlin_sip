using System.Windows;

namespace MerlinSip;

public partial class App : Application
{
    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
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
        MainWindow = mainWindow;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.Show();
    }
}
