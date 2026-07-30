namespace MerlinSip.Services;

public class SrvRecord
{
	public ushort Priority { get; set; }

	public ushort Weight { get; set; }

	public ushort Port { get; set; }

	public string Target { get; set; } = string.Empty;
}
