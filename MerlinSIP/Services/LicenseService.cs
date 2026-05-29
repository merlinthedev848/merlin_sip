namespace MerlinSip.Services;

public sealed class LicenseService
{
    public string Status { get; private set; } = "Licensed";

    public bool Activate(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        Status = "Licensed";
        return true;
    }
}
