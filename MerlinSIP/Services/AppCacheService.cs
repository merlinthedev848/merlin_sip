using System.IO;
using System.Security.Cryptography;
using System.Text;
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

        SavedAppSettings? settings;
        try
        {
            await using var stream = File.OpenRead(SettingsPath);
            settings = await JsonSerializer.DeserializeAsync<SavedAppSettings>(stream);
        }
        catch (Exception error)
        {
            DebugLog.Write($"SETTINGS load failed error={error.Message}");
            return null;
        }

        if (settings is null)
        {
            return null;
        }

        return new AppStartupConfig(
            AppStartupConfig.FixedSipServer,
            AppStartupConfig.FixedSipPort,
            AppStartupConfig.FixedSipServer,
            Unprotect(settings.Extension, settings.EncryptedExtension),
            Unprotect(settings.Username, settings.EncryptedUsername),
            Unprotect(settings.Password, settings.EncryptedPassword),
            settings.LicenseKey,
            settings.LicenseStatus,
            settings.AudioInput,
            settings.AudioOutput,
            settings.VideoSource,
            string.IsNullOrWhiteSpace(settings.Ringtone) ? AppStartupConfig.DefaultRingtone : settings.Ringtone,
            ClampVolume(settings.MicrophoneVolume),
            ClampVolume(settings.HeadphoneVolume),
            settings.SipAlgCompatibilityMode ?? false).WithFixedSipEndpoint();
    }

    public async Task SaveSettingsAsync(AppStartupConfig config)
    {
        var settings = new SavedAppSettings(
            null,
            null,
            null,
            Protect(config.Extension),
            Protect(config.Username),
            Protect(config.Password),
            config.LicenseKey,
            config.LicenseStatus,
            config.AudioInput,
            config.AudioOutput,
            config.VideoSource,
            config.Ringtone,
            config.MicrophoneVolume,
            config.HeadphoneVolume,
            config.SipAlgCompatibilityMode);

        try
        {
            Directory.CreateDirectory(_root);
            await using var stream = File.Create(SettingsPath);
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
        }
        catch (Exception error)
        {
            DebugLog.Write($"SETTINGS save failed error={error.Message}");
        }
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

    private static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var plainBytes = Encoding.UTF8.GetBytes(value);
        var encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    private static string Unprotect(string? legacyPlainText, string? encrypted)
    {
        if (string.IsNullOrWhiteSpace(encrypted))
        {
            return legacyPlainText ?? string.Empty;
        }

        try
        {
            var encryptedBytes = Convert.FromBase64String(encrypted);
            var plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception error)
        {
            DebugLog.Write($"SETTINGS decrypt failed error={error.Message}");
            return legacyPlainText ?? string.Empty;
        }
    }

    private static double ClampVolume(double? value)
    {
        return Math.Clamp(value ?? 1.0, 0.25, 2.0);
    }

    private sealed record SavedAppSettings(
        string? Extension,
        string? Username,
        string? Password,
        string? EncryptedExtension,
        string? EncryptedUsername,
        string? EncryptedPassword,
        string LicenseKey,
        string LicenseStatus,
        MediaDeviceInfo AudioInput,
        MediaDeviceInfo AudioOutput,
        MediaDeviceInfo VideoSource,
        string? Ringtone = null,
        double? MicrophoneVolume = null,
        double? HeadphoneVolume = null,
        bool? SipAlgCompatibilityMode = null);
}
