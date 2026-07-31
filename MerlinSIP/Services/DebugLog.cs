using System.IO;

namespace MerlinSip.Services;

internal static class DebugLog
{
    private static readonly object Sync = new();
    public static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MerlinSIP",
        "debug.log");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging should never break the phone.
        }
    }

    public static List<string> GetRecentLines(int count)
    {
        try
        {
            lock (Sync)
            {
                if (!File.Exists(Path))
                {
                    return [];
                }
                var lines = File.ReadAllLines(Path);
                return lines.Length <= count ? [.. lines] : [.. lines.Skip(lines.Length - count)];
            }
        }
        catch
        {
            return [];
        }
    }
}
