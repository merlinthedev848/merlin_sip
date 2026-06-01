using System.IO;
using System.Text.Json;
using MerlinSip.Models;

namespace MerlinSip.Services;

public sealed class CallHistoryStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MerlinSIP",
        "call-history.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<IReadOnlyList<CallHistoryEntry>> LoadAsync()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<List<CallHistoryEntry>>(stream) ?? [];
        }
        catch (Exception error)
        {
            DebugLog.Write($"CALL HISTORY load failed error={error.Message}");
            return [];
        }
    }

    public async Task SaveAsync(IEnumerable<CallHistoryEntry> calls)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using var stream = File.Create(_path);
            await JsonSerializer.SerializeAsync(stream, calls.Take(500).ToList(), JsonOptions);
        }
        catch (Exception error)
        {
            DebugLog.Write($"CALL HISTORY save failed error={error.Message}");
        }
    }
}
