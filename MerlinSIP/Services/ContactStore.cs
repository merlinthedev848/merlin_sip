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
        return contacts.FirstOrDefault(contact => Normalize(contact.Number) == normalized);
    }

    private static string Normalize(string value)
    {
        return new string(value.Where(char.IsDigit).ToArray());
    }
}
