namespace MerlinSip.Services;

public class HardwareItem
{
	public string ComponentType { get; set; } = string.Empty;

	public string Name { get; set; } = string.Empty;

	public string Status { get; set; } = string.Empty;

	public string Details { get; set; } = string.Empty;

	public bool IsHealthy { get; set; } = true;
}
