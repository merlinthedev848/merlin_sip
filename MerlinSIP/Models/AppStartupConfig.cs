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
    string Ringtone = AppStartupConfig.DefaultRingtone,
    double MicrophoneVolume = 1.0,
    double HeadphoneVolume = 1.0,
    bool SipAlgCompatibilityMode = false,
    string LicenseLocalKey = "")
{
    public const string FixedSipServer = "pbx.chriskendall.media";
    public const int FixedSipPort = 5060;
    public const string DefaultRingtone = "merlin";

    public bool AllowsCustomSipEndpoint =>
        LicenseKey.StartsWith("PR", StringComparison.OrdinalIgnoreCase);

    public AppStartupConfig WithFixedSipEndpoint()
    {
        if (AllowsCustomSipEndpoint && !string.IsNullOrWhiteSpace(Server))
        {
            return this with
            {
                Domain = string.IsNullOrWhiteSpace(Domain) ? Server : Domain,
                Port = Port > 0 ? Port : FixedSipPort
            };
        }

        return this with
        {
            Server = FixedSipServer,
            Port = FixedSipPort,
            Domain = FixedSipServer
        };
    }
}
