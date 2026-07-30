namespace MerlinSip.Models;

using System;

/// <summary>
/// Represents the current voicemail status for the user.
/// </summary>
public record VoicemailStatus
{
    /// <summary>
    /// Gets whether there are any new or old messages.
    /// </summary>
    public bool HasMessages { get; init; }

    /// <summary>
    /// Gets the number of new messages.
    /// </summary>
    public int NewMessages { get; init; }

    /// <summary>
    /// Gets the number of old messages.
    /// </summary>
    public int OldMessages { get; init; }

    /// <summary>
    /// Gets the time the status was last checked.
    /// </summary>
    public DateTime LastChecked { get; init; }
}
