using System.Text.Json.Serialization;

namespace MerlinSip.Models;

public sealed record ContactEntry
{
    public string Name { get; init; } = "";
    public string Number { get; init; } = "";
    public string Company { get; init; } = "";
    public string Notes { get; init; } = "";

    [JsonIgnore]
    public string Presence { get; init; } = "Offline";

    [JsonIgnore]
    public string PresenceLabel => Presence switch
    {
        "Available" => "Available",
        "Ringing" => "Ringing",
        "Busy" => "Busy",
        "Offline" => "Offline",
        _ => "Offline"
    };

    [JsonIgnore]
    public string PresenceBrush => Presence switch
    {
        "Available" => "#10B981",
        "Ringing" => "#F59E0B",
        "Busy" => "#EF4444",
        "Offline" => "#94A3B8",
        _ => "#94A3B8"
    };

    [JsonIgnore]
    public string PresenceBackground => Presence switch
    {
        "Available" => "#DFF8EE",
        "Ringing" => "#FFF3D6",
        "Busy" => "#FFE2E2",
        "Offline" => "#EEF2F7",
        _ => "#EEF2F7"
    };

    [JsonIgnore]
    public string PresenceForeground => Presence switch
    {
        "Available" => "#106247",
        "Ringing" => "#8A4F08",
        "Busy" => "#9B1C1C",
        "Offline" => "#475569",
        _ => "#475569"
    };
}
