using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MerlinSip.Models;

namespace MerlinSip.Services;

public sealed class AppCacheService
{
    public static AppStartupConfig? ActiveConfig { get; set; }
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MerlinSIP");

    private readonly string SettingsPath;
    private readonly string ContactsPath;
    private readonly string HistoryPath;

    public AppCacheService()
    {
        SettingsPath = Path.Combine(_root, "settings.json");
        ContactsPath = Path.Combine(_root, "contacts.json");
        HistoryPath = Path.Combine(_root, "call-history.json");
    }

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

        var licenseKey = Unprotect(settings.LicenseKey, settings.EncryptedLicenseKey);
        var customServer = Unprotect(settings.Server, settings.EncryptedServer);
        var server = licenseKey.StartsWith("PR", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(customServer)
            ? customServer
            : AppStartupConfig.FixedSipServer;

        return new AppStartupConfig(
            server,
            settings.Port is > 0 ? settings.Port.Value : AppStartupConfig.FixedSipPort,
            server,
            Unprotect(settings.Extension, settings.EncryptedExtension),
            Unprotect(settings.Username, settings.EncryptedUsername),
            Unprotect(settings.Password, settings.EncryptedPassword),
            licenseKey,
            settings.LicenseStatus ?? "Licensed",
            settings.AudioInput,
            settings.AudioOutput,
            string.IsNullOrWhiteSpace(settings.Ringtone) ? AppStartupConfig.DefaultRingtone : settings.Ringtone,
            ClampVolume(settings.MicrophoneVolume),
            ClampVolume(settings.HeadphoneVolume),
            settings.SipAlgCompatibilityMode ?? false,
            NormalizeTransport(settings.SipSignallingTransport),
            Unprotect(settings.LicenseLocalKey, settings.EncryptedLicenseLocalKey),
            settings.MobileNumber ?? "",
            settings.DndMode ?? "Off",
            settings.DeclineIncomingAction ?? "Send busy",
            settings.CallWaitingEnabled ?? false,
            settings.InternalBusyAction ?? "Send busy",
            settings.InternalNoAnswerSeconds ?? 90,
            settings.InternalNoAnswerAction ?? "Send busy",
            settings.ExternalBusyAction ?? "Send busy",
            settings.ExternalNoAnswerSeconds ?? 90,
            settings.ExternalNoAnswerAction ?? "Send busy",
            settings.QueuePickupEnabled ?? false,
            settings.FlashCallState ?? true,
            settings.MaxConcurrentCalls ?? 2,
            settings.ShowCallStatistics ?? false,
            settings.SingleClickBlindTransfer ?? false,
            settings.CombineContactsInSearch ?? true,
            settings.IncomingNotificationSeconds ?? 30,
            settings.FailedCallDisplaySeconds ?? 5,
            settings.ShowFavouriteExtensionsOnTransfer ?? true,
            settings.CallPickupPrefix ?? "*8",
            settings.QueueLoginLogoutCode ?? "*62",
            settings.CallRecordingCode ?? "*1",
            settings.EnableAdvancedDiagnostics ?? false,
            settings.StunServer ?? "stun.l.google.com:19302",
            settings.RegistrationExpiry ?? 3600,
            settings.HeartbeatInterval ?? 30,
            settings.LocalSipPort ?? 5060,
            settings.PreferredCodecs ?? "PCMA,PCMU,G722",
            settings.IgnoreSslErrors ?? false).WithFixedSipEndpoint();
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
            null,
            Protect(config.Server),
            config.Port,
            null,
            Protect(config.LicenseKey),
            config.LicenseStatus,
            config.AudioInput,
            config.AudioOutput,
            new MediaDeviceInfo("default-video", "Default camera"),
            config.Ringtone,
            config.MicrophoneVolume,
            config.HeadphoneVolume,
            config.SipAlgCompatibilityMode,
            NormalizeTransport(config.SipSignallingTransport),
            null,
            Protect(config.LicenseLocalKey),
            config.MobileNumber,
            config.DndMode,
            config.DeclineIncomingAction,
            config.CallWaitingEnabled,
            config.InternalBusyAction,
            config.InternalNoAnswerSeconds,
            config.InternalNoAnswerAction,
            config.ExternalBusyAction,
            config.ExternalNoAnswerSeconds,
            config.ExternalNoAnswerAction,
            config.QueuePickupEnabled,
            config.FlashCallState,
            config.MaxConcurrentCalls,
            config.ShowCallStatistics,
            config.SingleClickBlindTransfer,
            config.CombineContactsInSearch,
            config.IncomingNotificationSeconds,
            config.FailedCallDisplaySeconds,
            config.ShowFavouriteExtensionsOnTransfer,
            config.CallPickupPrefix,
            config.QueueLoginLogoutCode,
            config.CallRecordingCode,
            config.EnableAdvancedDiagnostics,
            config.StunServer,
            config.RegistrationExpiry,
            config.HeartbeatInterval,
            config.LocalSipPort,
            config.PreferredCodecs,
            config.IgnoreSslErrors);

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
        if (OperatingSystem.IsWindows())
        {
#pragma warning disable CA1416
            var encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
#pragma warning restore CA1416
        }
        else
        {
            // Simple Base64 encode for non-Windows for now
            return Convert.ToBase64String(plainBytes);
        }
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
            if (OperatingSystem.IsWindows())
            {
#pragma warning disable CA1416
                var plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
#pragma warning restore CA1416
            }
            else
            {
                // Simple Base64 decode for non-Windows for now
                return Encoding.UTF8.GetString(encryptedBytes);
            }
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

    private static string NormalizeTransport(string? transport)
    {
        return string.Equals(transport, AppStartupConfig.TransportTcp, StringComparison.OrdinalIgnoreCase)
            ? AppStartupConfig.TransportTcp
            : string.Equals(transport, AppStartupConfig.TransportTls, StringComparison.OrdinalIgnoreCase)
                ? AppStartupConfig.TransportTls
                : AppStartupConfig.TransportUdp;
    }

    private sealed record SavedAppSettings(
        string? Extension,
        string? Username,
        string? Password,
        string? EncryptedExtension,
        string? EncryptedUsername,
        string? EncryptedPassword,
        string? Server,
        string? EncryptedServer,
        int? Port,
        string? LicenseKey,
        string? EncryptedLicenseKey,
        string? LicenseStatus,
        MediaDeviceInfo AudioInput,
        MediaDeviceInfo AudioOutput,
        MediaDeviceInfo VideoSource,
        string? Ringtone = null,
        double? MicrophoneVolume = null,
        double? HeadphoneVolume = null,
        bool? SipAlgCompatibilityMode = null,
        string? SipSignallingTransport = null,
        string? LicenseLocalKey = null,
        string? EncryptedLicenseLocalKey = null,
        string? MobileNumber = null,
        string? DndMode = null,
        string? DeclineIncomingAction = null,
        bool? CallWaitingEnabled = null,
        string? InternalBusyAction = null,
        int? InternalNoAnswerSeconds = null,
        string? InternalNoAnswerAction = null,
        string? ExternalBusyAction = null,
        int? ExternalNoAnswerSeconds = null,
        string? ExternalNoAnswerAction = null,
        bool? QueuePickupEnabled = null,
        bool? FlashCallState = null,
        int? MaxConcurrentCalls = null,
        bool? ShowCallStatistics = null,
        bool? SingleClickBlindTransfer = null,
        bool? CombineContactsInSearch = null,
        int? IncomingNotificationSeconds = null,
        int? FailedCallDisplaySeconds = null,
        bool? ShowFavouriteExtensionsOnTransfer = null,
        string? CallPickupPrefix = null,
        string? QueueLoginLogoutCode = null,
        string? CallRecordingCode = null,
        bool? EnableAdvancedDiagnostics = null,
        string? StunServer = null,
        int? RegistrationExpiry = null,
        int? HeartbeatInterval = null,
        int? LocalSipPort = null,
        string? PreferredCodecs = null,
        bool? IgnoreSslErrors = null);
}
