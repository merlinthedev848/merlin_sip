namespace MerlinSip.Services;

public class PingStats
{
	public long Current { get; set; }

	public long Min { get; set; }

	public long Max { get; set; }

	public double Average { get; set; }

	public double LossPercentage { get; set; }

	public double Jitter { get; set; }
}
