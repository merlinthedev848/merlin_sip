namespace MerlinSip.Models;

using System;

/// <summary>
/// Represents the public state of an active call for UI binding.
/// </summary>
public class CallState
{
    public string CallId { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public bool IsOnHold { get; set; }
    public DateTime StartTime { get; set; } = DateTime.UtcNow;

    public TimeSpan Duration => DateTime.UtcNow - StartTime;
}
