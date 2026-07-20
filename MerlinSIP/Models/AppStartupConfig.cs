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
    string Ringtone = AppStartupConfig.DefaultRingtone,
    double MicrophoneVolume = 1.0,
    double HeadphoneVolume = 1.0,
    bool SipAlgCompatibilityMode = false,
    string SipSignallingTransport = AppStartupConfig.TransportUdp,
    string LicenseLocalKey = "",
    string MobileNumber = "",
    string DndMode = "Off",
    string DeclineIncomingAction = "Send busy",
    bool CallWaitingEnabled = false,
    string InternalBusyAction = "Send busy",
    int InternalNoAnswerSeconds = 90,
    string InternalNoAnswerAction = "Send busy",
    string ExternalBusyAction = "Send busy",
    int ExternalNoAnswerSeconds = 90,
    string ExternalNoAnswerAction = "Send busy",
    bool QueuePickupEnabled = false,
    bool FlashCallState = true,
    int MaxConcurrentCalls = 2,
    bool ShowCallStatistics = false,
    bool SingleClickBlindTransfer = false,
    bool CombineContactsInSearch = true,
    int IncomingNotificationSeconds = 30,
    int FailedCallDisplaySeconds = 5,
    bool ShowFavouriteExtensionsOnTransfer = true)
{
    public const string FixedSipServer = "pbx.chriskendall.media";
    public const int FixedSipPort = 5060;
    public const string DefaultRingtone = "merlin";
    public const string TransportUdp = "UDP";
    public const string TransportTcp = "TCP";
    public const string TransportTls = "TLS";

    public bool UsesTcpSignalling =>
        string.Equals(SipSignallingTransport, TransportTcp, StringComparison.OrdinalIgnoreCase);

    public bool UsesTlsSignalling =>
        string.Equals(SipSignallingTransport, TransportTls, StringComparison.OrdinalIgnoreCase);

    public bool AllowsCustomSipEndpoint =>
        LicenseKey.StartsWith("PR", StringComparison.OrdinalIgnoreCase);

    public AppStartupConfig WithFixedSipEndpoint()
    {
        if (AllowsCustomSipEndpoint && !string.IsNullOrWhiteSpace(Server))
        {
            return this with
            {
                Domain = string.IsNullOrWhiteSpace(Domain) ? Server : Domain,
                Port = Port > 0 ? Port : (UsesTlsSignalling ? 5061 : FixedSipPort)
            };
        }

        return this with
        {
            Server = FixedSipServer,
            Port = UsesTlsSignalling ? 5061 : FixedSipPort,
            Domain = FixedSipServer
        };
    }
}
