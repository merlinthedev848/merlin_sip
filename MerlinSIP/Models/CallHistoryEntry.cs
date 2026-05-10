namespace MerlinSip.Models;

public sealed record CallHistoryEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Direction { get; init; } = "";
    public string Name { get; init; } = "";
    public string Number { get; init; } = "";
    public string StartedAt { get; init; } = DateTimeOffset.Now.ToString("s");
    public string EndedAt { get; init; } = "";
    public string Duration { get; init; } = "";
    public string Result { get; init; } = "";
    public string Detail { get; init; } = "";
}
