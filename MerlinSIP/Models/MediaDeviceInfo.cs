namespace MerlinSip.Models;

public sealed record MediaDeviceInfo(string Id, string Name)
{
    public override string ToString()
    {
        return Name;
    }
}
