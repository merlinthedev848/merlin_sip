using System;
using System.Net.NetworkInformation;

namespace MerlinSip.Services;

public class PingResult
{
	public DateTime Timestamp { get; set; }

	public string Target { get; set; } = string.Empty;

	public long? LatencyMs { get; set; }

	public IPStatus Status { get; set; }

	public string LatencyDisplay
	{
		get
		{
			if (!LatencyMs.HasValue)
			{
				return "Timeout";
			}
			return $"{LatencyMs.Value} ms";
		}
	}
}
