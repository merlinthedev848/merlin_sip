namespace MerlinSip.Models;

public sealed record AppStartupConfig(
    string Server,
    int Port,
    string Domain,
    string Extension,
    string Username,
    string Password,
    string LicenseKey,
    string LicenseStatus,
    MediaDeviceInfo AudioInput,
    MediaDeviceInfo AudioOutput,
    MediaDeviceInfo VideoSource,
    string Ringtone = AppStartupConfig.DefaultRingtone)
{
    public const string FixedSipServer = "pbx.chriskendall.media";
    public const int FixedSipPort = 5060;
    public const string DefaultRingtone = "classic";

    public AppStartupConfig WithFixedSipEndpoint()
    {
        return this with
        {
            Server = FixedSipServer,
            Port = FixedSipPort,
            Domain = FixedSipServer
        };
    }
}
