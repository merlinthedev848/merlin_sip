using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MerlinSip.Models;

namespace MerlinSip.Services;

public sealed class ChatMessageStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MerlinSIP",
        "messages.dat");

    private readonly string _legacyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MerlinSIP",
        "messages.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<IReadOnlyList<ChatMessageEntry>> LoadAsync()
    {
        if (File.Exists(_path))
        {
            try
            {
                var protectedText = await File.ReadAllTextAsync(_path);
                var plainText = Unprotect(protectedText);
                return JsonSerializer.Deserialize<List<ChatMessageEntry>>(plainText) ?? [];
            }
            catch (Exception error)
            {
                DebugLog.Write($"MESSAGES load failed error={error.Message}");
                return [];
            }
        }

        if (!File.Exists(_legacyPath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_legacyPath);
            var messages = await JsonSerializer.DeserializeAsync<List<ChatMessageEntry>>(stream) ?? [];
            await SaveAsync(messages);
            return messages;
        }
        catch (Exception error)
        {
            DebugLog.Write($"LEGACY MESSAGES load failed error={error.Message}");
            return [];
        }
    }

    public async Task SaveAsync(IEnumerable<ChatMessageEntry> messages)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var plainText = JsonSerializer.Serialize(messages.Take(1000).ToList(), JsonOptions);
            await File.WriteAllTextAsync(_path, Protect(plainText));
        }
        catch (Exception error)
        {
            DebugLog.Write($"MESSAGES save failed error={error.Message}");
        }
    }

    private static string Protect(string value)
    {
        var plainBytes = Encoding.UTF8.GetBytes(value);
        var encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    private static string Unprotect(string value)
    {
        var encryptedBytes = Convert.FromBase64String(value);
        var plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plainBytes);
    }
}
