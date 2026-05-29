namespace MerlinSip.Models;

public sealed record UpdateCheckResult(
    bool Success,
    bool UpdateAvailable,
    string Message,
    string? Version = null,
    string? DownloadUrl = null,
    string? Notes = null);
