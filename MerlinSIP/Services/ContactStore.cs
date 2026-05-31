using System.IO;
using System.Text.Json;
using MerlinSip.Models;

namespace MerlinSip.Services;

public sealed class ContactStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MerlinSIP",
        "contacts.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<IReadOnlyList<ContactEntry>> LoadAsync()
    {
        if (!File.Exists(_path))
        {
            var starter = new[]
            {
                new ContactEntry { Name = "Reception", Number = "1000", Company = "Internal", Notes = "Main desk" }
            };

            await SaveAsync(starter);
            return starter;
        }

        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<ContactEntry>>(stream) ?? [];
    }

    public async Task SaveAsync(IEnumerable<ContactEntry> contacts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, contacts.OrderBy(contact => contact.Name).ToList(), JsonOptions);
    }

    public ContactEntry? FindByNumber(IEnumerable<ContactEntry> contacts, string number)
    {
        var normalized = Normalize(number);
        var variants = BuildVariants(normalized);
        return contacts.FirstOrDefault(contact => variants.Contains(Normalize(contact.Number)));
    }

    private static string Normalize(string value)
    {
        return new string(value.Where(char.IsDigit).ToArray());
    }

    private static HashSet<string> BuildVariants(string normalized)
    {
        var variants = new HashSet<string> { normalized };
        if (normalized.StartsWith("44", StringComparison.Ordinal) && normalized.Length > 2)
        {
            variants.Add("0" + normalized[2..]);
        }
        else if (normalized.StartsWith("0", StringComparison.Ordinal) && normalized.Length > 1)
        {
            variants.Add("44" + normalized[1..]);
        }

        return variants;
    }
}
