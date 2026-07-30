namespace MerlinSip.Services;

public class PortProbeResult
{
	public int Port { get; set; }

	public string Protocol { get; set; } = string.Empty;

	public string ServiceName { get; set; } = string.Empty;

	public string Target { get; set; } = string.Empty;

	public string Status { get; set; } = "Closed";

	public double? RttMs { get; set; }

	public string PortDisplay => $"{Protocol} {Port}";

	public string RttDisplay
	{
		get
		{
			if (!RttMs.HasValue)
			{
				return "-";
			}
			return $"{RttMs.Value:F1} ms";
		}
	}
}
