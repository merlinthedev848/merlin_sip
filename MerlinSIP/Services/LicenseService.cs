namespace MerlinSip.Services;

public sealed class LicenseService
{
    public const string ProductId = "merlin-sip";

    private const string TestLicenseKey = "TEST-BFC2-DF38-F81D-F08E-135A-9058";

    public string Status { get; private set; } = "Licensed";

    public bool Activate(string token)
    {
        if (!string.Equals(token?.Trim(), TestLicenseKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Status = "Licensed";
        return true;
    }
}
