using System.Diagnostics;
using Microsoft.Win32;

namespace MerlinSip.Services;

public static class WindowsStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKeyPath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string AppValueName = "Merlin SIP";

    public static void EnableLaunchOnWindowsStartup()
    {
        try
        {
            var executable = GetExecutablePath();
            if (string.IsNullOrWhiteSpace(executable))
            {
                return;
            }

            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key?.SetValue(AppValueName, Quote(executable), RegistryValueKind.String);
            DebugLog.Write("WINDOWS STARTUP enabled");
        }
        catch (Exception error)
        {
            DebugLog.Write($"WINDOWS STARTUP enable failed error={error.Message}");
        }
    }

    public static void QueueLaunchAfterUpdate()
    {
        try
        {
            var executable = GetExecutablePath();
            if (string.IsNullOrWhiteSpace(executable))
            {
                return;
            }

            using var key = Registry.CurrentUser.CreateSubKey(RunOnceKeyPath);
            key?.SetValue(AppValueName, Quote(executable), RegistryValueKind.String);
            DebugLog.Write("WINDOWS STARTUP queued post-update launch");
        }
        catch (Exception error)
        {
            DebugLog.Write($"WINDOWS STARTUP queue post-update failed error={error.Message}");
        }
    }

    public static string? GetExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        using var process = Process.GetCurrentProcess();
        return process.MainModule?.FileName;
    }

    private static string Quote(string path)
    {
        return $"\"{path}\"";
    }
}
