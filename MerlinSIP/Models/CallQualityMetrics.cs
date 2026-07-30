namespace MerlinSip.Models;

/// <summary>
/// Represents the level of call quality.
/// </summary>
public enum CallQualityLevel
{
    Poor,
    Fair,
    Good,
    Excellent
}

/// <summary>
/// Contains metrics related to call quality, including MOS score.
/// </summary>
public record CallQualityMetrics
{
    /// <summary>
    /// Mean Opinion Score (1.0 to 5.0).
    /// </summary>
    public double MosScore { get; init; }

    /// <summary>
    /// The categorized call quality level.
    /// </summary>
    public CallQualityLevel Level { get; init; }

    /// <summary>
    /// Jitter in milliseconds.
    /// </summary>
    public double Jitter { get; init; }

    /// <summary>
    /// Packet loss percentage (0.0 to 100.0).
    /// </summary>
    public double PacketLoss { get; init; }

    /// <summary>
    /// Round-trip latency in milliseconds.
    /// </summary>
    public double Latency { get; init; }
}
