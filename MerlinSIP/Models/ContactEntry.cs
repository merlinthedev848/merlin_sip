namespace MerlinSip.Models;

public sealed record ContactEntry
{
    public string Name { get; init; } = "";
    public string Number { get; init; } = "";
    public string Company { get; init; } = "";
    public string Notes { get; init; } = "";
}
