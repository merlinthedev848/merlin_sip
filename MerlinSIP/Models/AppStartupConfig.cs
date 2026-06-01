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
    bool SipAlgCompatibilityMode = false)
{
    public const string FixedSipServer = "pbx.chriskendall.media";
    public const int FixedSipPort = 5060;
    public const string DefaultRingtone = "merlin";

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
