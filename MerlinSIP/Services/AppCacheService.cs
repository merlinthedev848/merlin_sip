using System.IO;
using System.Text.Json;
using MerlinSip.Models;

namespace MerlinSip.Services;

public sealed class AppCacheService
{
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MerlinSIP");

    private string SettingsPath => Path.Combine(_root, "settings.json");
    private string ContactsPath => Path.Combine(_root, "contacts.json");
    private string HistoryPath => Path.Combine(_root, "call-history.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<AppStartupConfig?> LoadSettingsAsync()
    {
        if (!File.Exists(SettingsPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(SettingsPath);
        return await JsonSerializer.DeserializeAsync<AppStartupConfig>(stream);
    }

    public async Task SaveSettingsAsync(AppStartupConfig config)
    {
        Directory.CreateDirectory(_root);
        await using var stream = File.Create(SettingsPath);
        await JsonSerializer.SerializeAsync(stream, config, JsonOptions);
    }

    public void Reset()
    {
        DeleteIfExists(SettingsPath);
        DeleteIfExists(ContactsPath);
        DeleteIfExists(HistoryPath);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
