namespace MerlinSip.Services;

public sealed class LicenseService
{
    public const string PlaceholderKey = "TEST-MERLIN-SIP";

    public string Status { get; private set; } = "Trial mode";

    public bool Activate(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        Status = token.Trim().Equals(PlaceholderKey, StringComparison.OrdinalIgnoreCase)
            ? "Test license"
            : "Pending online validation";
        return true;
    }
}
