namespace MerlinSip.Models;

public sealed record ChatMessageEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Direction { get; init; } = "";
    public string Name { get; init; } = "";
    public string Number { get; init; } = "";
    public string Message { get; init; } = "";
    public string SentAt { get; init; } = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
    public string Result { get; init; } = "";
}
