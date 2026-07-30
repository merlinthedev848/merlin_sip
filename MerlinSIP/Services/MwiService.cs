namespace MerlinSip.Services;

using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MerlinSip.Models;

/// <summary>
/// Interface for SIP transport abstraction needed by MWI.
/// </summary>
public interface IMwiSipTransport
{
    /// <summary>
    /// Sends a SIP request and waits for a response.
    /// </summary>
    Task<SipResponse> SendRequestAndWaitAsync(string request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a digest authorization header.
    /// </summary>
    string BuildDigestAuthorization(string method, string uri, string challengeHeader);

    /// <summary>
    /// Generates a unique SIP branch ID.
    /// </summary>
    string CreateBranch();

    /// <summary>
    /// Gets the SIP configuration.
    /// </summary>
    AppStartupConfig? Config { get; }

    string Domain { get; }
    string LocalAddress { get; }
    int LocalPort { get; }
    string SipTransportName { get; }
    string ContactTransport { get; }
}

/// <summary>
/// Service for handling Message Waiting Indicator (MWI) via SIP SUBSCRIBE/NOTIFY.
/// </summary>
public class MwiService : IDisposable
{
    private readonly IMwiSipTransport _transport;
    private CancellationTokenSource? _resubscribeCancellation;
    private int _subscribeCseq = 1;
    private string _callId = Guid.NewGuid().ToString("N") + "@merlin";
    private string _localTag = Guid.NewGuid().ToString("N").Substring(0, 12);
    
    public event EventHandler<VoicemailStatus>? VoicemailCountChanged;

    public MwiService(IMwiSipTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>
    /// Starts subscribing to the MWI event package.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _resubscribeCancellation?.Cancel();
        _resubscribeCancellation = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _resubscribeCancellation.Token);

        _ = Task.Run(() => SubscribeLoopAsync(linkedCts.Token), linkedCts.Token);
    }

    private async Task SubscribeLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SubscribeAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromMinutes(55), cancellationToken); // Resubscribe before 1 hour expiry
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Wait before retrying on failure
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
        }
    }

    private async Task SubscribeAsync(CancellationToken cancellationToken)
    {
        if (_transport.Config == null) return;

        int cseq = _subscribeCseq++;
        string request = BuildSubscribe(cseq, null);
        
        var response = await _transport.SendRequestAndWaitAsync(request, cancellationToken);
        
        if (response.Code == 401 || response.Code == 407)
        {
            string challenge = response.Headers.TryGetValue("www-authenticate", out var wAuth) ? wAuth : response.Headers.GetValueOrDefault("proxy-authenticate", "");
            string authHeader = _transport.BuildDigestAuthorization("SUBSCRIBE", $"sip:{_transport.Config.Extension}@{_transport.Domain}", challenge);
            
            cseq = _subscribeCseq++;
            request = BuildSubscribe(cseq, authHeader);
            response = await _transport.SendRequestAndWaitAsync(request, cancellationToken);
        }

        if (response.Code >= 300)
        {
            throw new Exception($"MWI Subscribe failed with code {response.Code}");
        }
    }

    private string BuildSubscribe(int cseq, string? authHeader)
    {
        var config = _transport.Config!;
        string targetUri = $"sip:{config.Extension}@{_transport.Domain}";
        string branch = _transport.CreateBranch();

        var sb = new System.Text.StringBuilder();
        sb.Append($"SUBSCRIBE {targetUri} SIP/2.0\r\n");
        sb.Append($"Via: SIP/2.0/{_transport.SipTransportName} {_transport.LocalAddress}:{_transport.LocalPort};branch={branch};rport\r\n");
        sb.Append("Max-Forwards: 70\r\n");
        sb.Append($"From: <sip:{config.Extension}@{_transport.Domain}>;tag={_localTag}\r\n");
        sb.Append($"To: <{targetUri}>\r\n");
        sb.Append($"Call-ID: {_callId}\r\n");
        sb.Append($"CSeq: {cseq} SUBSCRIBE\r\n");
        sb.Append("Event: message-summary\r\n");
        sb.Append("Accept: application/simple-message-summary\r\n");
        sb.Append("Expires: 3600\r\n");
        sb.Append($"Contact: <sip:{config.Extension}@{_transport.LocalAddress}:{_transport.LocalPort};transport={_transport.ContactTransport}>\r\n");
        sb.Append("User-Agent: CK Media Services Merlin SIP\r\n");
        
        if (!string.IsNullOrEmpty(authHeader))
        {
            sb.Append($"Proxy-Authorization: {authHeader}\r\n");
        }

        sb.Append("Content-Length: 0\r\n\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Handles an incoming NOTIFY message from the SIP server.
    /// </summary>
    public void HandleNotify(string sipBody)
    {
        // Parse Messages-Waiting and Voice-Message
        bool hasMessages = false;
        int newMsg = 0;
        int oldMsg = 0;

        var waitingMatch = Regex.Match(sipBody, @"Messages-Waiting:\s*(yes|no)", RegexOptions.IgnoreCase);
        if (waitingMatch.Success)
        {
            hasMessages = waitingMatch.Groups[1].Value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        var voiceMessageMatch = Regex.Match(sipBody, @"Voice-Message:\s*(\d+)/(\d+)", RegexOptions.IgnoreCase);
        if (voiceMessageMatch.Success)
        {
            int.TryParse(voiceMessageMatch.Groups[1].Value, out newMsg);
            int.TryParse(voiceMessageMatch.Groups[2].Value, out oldMsg);
        }

        var status = new VoicemailStatus
        {
            HasMessages = hasMessages,
            NewMessages = newMsg,
            OldMessages = oldMsg,
            LastChecked = DateTime.UtcNow
        };

        VoicemailCountChanged?.Invoke(this, status);
    }

    public void Dispose()
    {
        _resubscribeCancellation?.Cancel();
        _resubscribeCancellation?.Dispose();
        GC.SuppressFinalize(this);
    }
}
