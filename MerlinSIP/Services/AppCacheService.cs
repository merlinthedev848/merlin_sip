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
        var settings = await JsonSerializer.DeserializeAsync<SavedAppSettings>(stream);
        if (settings is null)
        {
            return null;
        }

        return new AppStartupConfig(
            AppStartupConfig.FixedSipServer,
            AppStartupConfig.FixedSipPort,
            AppStartupConfig.FixedSipServer,
            settings.Extension,
            settings.Username,
            settings.Password,
            settings.LicenseKey,
            settings.LicenseStatus,
            settings.AudioInput,
            settings.AudioOutput,
            settings.VideoSource).WithFixedSipEndpoint();
    }

    public async Task SaveSettingsAsync(AppStartupConfig config)
    {
        var settings = new SavedAppSettings(
            config.Extension,
            config.Username,
            config.Password,
            config.LicenseKey,
            config.LicenseStatus,
            config.AudioInput,
            config.AudioOutput,
            config.VideoSource);

        Directory.CreateDirectory(_root);
        await using var stream = File.Create(SettingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
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

    private sealed record SavedAppSettings(
        string Extension,
        string Username,
        string Password,
        string LicenseKey,
        string LicenseStatus,
        MediaDeviceInfo AudioInput,
        MediaDeviceInfo AudioOutput,
        MediaDeviceInfo VideoSource);
}
