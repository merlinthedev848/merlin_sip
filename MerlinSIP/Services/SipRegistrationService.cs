using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MerlinSip.Models;

namespace MerlinSip.Services;

public sealed class SipRegistrationService : IDisposable
{
    private const int StandardRegisterExpiresSeconds = 3600;
    private const int CompatibilityRegisterExpiresSeconds = 120;
    private static readonly TimeSpan RegisterRefreshInterval = TimeSpan.FromMinutes(10);
    private UdpClient? _client;
    private AppStartupConfig? _config;
    private string _domain = "";
    private string _localAddress = "127.0.0.1";
    private int _localPort;
    private CancellationTokenSource? _listenCancellation;
    private CancellationTokenSource? _registrationRefreshCancellation;
    private CancellationTokenSource? _natKeepAliveCancellation;
    private TaskCompletionSource<SipResponse>? _pendingResponse;
    private readonly SemaphoreSlim _registrationLock = new(1, 1);
    private bool _listenerStarted;
    private bool _registered;
    private int _inviteCseq = 1;
    private int _registerCseq = 1;
    private int _messageCseq = 1;
    private ActiveCall? _activeCall;
    private PendingIncomingCall? _pendingIncomingCall;
    private RtpAudioSession? _audioSession;
    private bool _rejectIncomingCalls;
    private SipResponse? _lastCallFailureResponse;

    public event EventHandler<IncomingCallEventArgs>? IncomingCall;
    public event EventHandler<IncomingMessageEventArgs>? IncomingMessage;
    public event EventHandler<CallProgressEventArgs>? CallProgress;
    public event EventHandler<CallEndedEventArgs>? CallEnded;

    public bool CanControlAudio => _audioSession is not null;

    public bool HasPendingIncomingCall => _pendingIncomingCall is not null;

    public string LastCallFailureReason => _lastCallFailureResponse is null
        ? "No outbound route failure has been recorded in this session."
        : $"{_lastCallFailureResponse.Code} {_lastCallFailureResponse.Reason}".Trim();

    public string RtpStatus
    {
        get
        {
            if (_audioSession is null)
            {
                return "No active RTP session. Place a test call to verify live audio packets.";
            }

            return _audioSession.ReceivedPackets > 0
                ? $"RTP is receiving audio packets. Received {_audioSession.ReceivedPackets}, sent {_audioSession.SentPackets}."
                : $"RTP is active but no inbound audio packets have been received yet. Sent {_audioSession.SentPackets}.";
        }
    }

    public void SetRejectIncomingCalls(bool reject)
    {
        _rejectIncomingCalls = reject;
        DebugLog.Write($"DND rejectIncomingCalls={reject}");
    }

    public void SetMuted(bool muted)
    {
        _audioSession?.SetMuted(muted);
    }

    public async Task<SipCallResult> SetHeldAsync(bool held, CancellationToken cancellationToken = default)
    {
        if (_client is null || _config is null || _activeCall is null || !_activeCall.Established)
        {
            return new SipCallResult(false, "Connect a call before using hold.");
        }

        var cseq = _inviteCseq++;
        var branch = CreateBranch();
        var reInvite = BuildReInvite(_activeCall, cseq, branch, held, null);
        _activeCall = _activeCall with { CSeq = cseq, InviteBranch = branch };
        DebugLog.Write($"SEND HOLD REINVITE callId={_activeCall.CallId} held={held}");
        var response = await SendAndWaitFromListenerAsync(reInvite, cancellationToken);

        if (response.Code is 401 or 407)
        {
            await SendAckForFinalInviteResponseAsync(response, cancellationToken);
            var challengeHeader = response.Headers.TryGetValue("www-authenticate", out var wwwAuthenticate)
                ? wwwAuthenticate
                : response.Headers.GetValueOrDefault("proxy-authenticate", "");
            var authCseq = _inviteCseq++;
            var authBranch = CreateBranch();
            var authorization = BuildDigestAuthorization("INVITE", $"sip:{_activeCall.Target}", challengeHeader);
            var authInvite = BuildReInvite(_activeCall, authCseq, authBranch, held, authorization);
            _activeCall = _activeCall with { CSeq = authCseq, InviteBranch = authBranch };
            DebugLog.Write($"SEND AUTH HOLD REINVITE callId={_activeCall.CallId} held={held}");
            response = await SendAndWaitFromListenerAsync(authInvite, cancellationToken);
        }

        if (response.Code is >= 200 and < 300)
        {
            _activeCall = _activeCall with { RemoteTag = ExtractTag(response.Headers.GetValueOrDefault("to", "")) };
            await SendAckForInviteResponseAsync(response, cancellationToken, false);
            _audioSession?.SetHeld(held);
            DebugLog.Write($"HOLD state accepted held={held} callId={_activeCall.CallId}");
            return new SipCallResult(true, held ? "Call placed on hold." : "Call resumed.");
        }

        if (response.Code >= 300)
        {
            await SendAckForFinalInviteResponseAsync(response, cancellationToken);
        }

        return new SipCallResult(false, $"Hold request failed: {response.Code} {response.Reason}".Trim());
    }

    public void SetHeldLocal(bool held)
    {
        _audioSession?.SetHeld(held);
    }

    public async Task<SipRegistrationResult> RegisterAsync(AppStartupConfig config, CancellationToken cancellationToken = default)
    {
        DisposeClient();

        _config = config;
        _domain = string.IsNullOrWhiteSpace(config.Domain) ? config.Server : config.Domain;
        _localAddress = GetLocalAddress();
        _client = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        _localPort = ((IPEndPoint)_client.Client.LocalEndPoint!).Port;

        SipRegistrationResult result;
        try
        {
            result = await RegisterCurrentSocketAsync(cancellationToken);
        }
        catch (Exception error)
        {
            DebugLog.Write($"REGISTER failed error={error.Message}");
            result = new SipRegistrationResult(false, $"Unable to connect: {error.Message}");
        }
        if (result.Connected)
        {
            _registered = true;
            StartListening();
            StartRegistrationRefresh();
            StartNatKeepAlive();
        }

        return result;
    }

    public async Task<SipCallResult> InviteAsync(string destination, CancellationToken cancellationToken = default)
    {
        if (_client is null || _config is null)
        {
            return new SipCallResult(false, "Register the SIP account first.");
        }

        if (!_registered)
        {
            var registration = await RegisterCurrentSocketAsync(cancellationToken);
            if (!registration.Connected)
            {
                return new SipCallResult(false, $"Registration check failed: {registration.Message}");
            }

            _registered = true;
        }

        var callId = $"{Guid.NewGuid():N}@merlin-sip";
        var target = destination.Contains('@') ? destination : $"{destination}@{_domain}";
        var localTag = Guid.NewGuid().ToString("N")[..12];
        var cseq = _inviteCseq++;
        var inviteBranch = CreateBranch();
        _audioSession?.Dispose();
        _audioSession = new RtpAudioSession(_config.AudioInput, _config.AudioOutput, _config.MicrophoneVolume, _config.HeadphoneVolume);
        try
        {
            _audioSession.PrepareDevices();
        }
        catch (Exception error)
        {
            DebugLog.Write($"RTP prepare failed input={_config.AudioInput.Name} output={_config.AudioOutput.Name} error={error.Message}");
            _audioSession.Dispose();
            _audioSession = null;
            return new SipCallResult(false, $"Audio device failed to open: {error.Message}");
        }

        var invite = BuildInvite(target, callId, localTag, cseq, inviteBranch, null);
        DebugLog.Write($"SEND INVITE target={target} callId={callId}");
        _activeCall = new ActiveCall(callId, target, localTag, cseq, inviteBranch, false, null);
        var firstResponse = await SendAndWaitFromListenerAsync(invite, cancellationToken);
        DebugLog.Write($"INVITE RESPONSE code={firstResponse.Code} reason={firstResponse.Reason}");

        if (firstResponse.Code is >= 100 and < 300)
        {
            _activeCall = _activeCall with { Established = firstResponse.Code >= 200, RemoteTag = ExtractTag(firstResponse.Headers.GetValueOrDefault("to", "")) };
            if (firstResponse.Code >= 200)
            {
                await AcknowledgeAndStartAudioAsync(firstResponse, cancellationToken);
            }

            return new SipCallResult(true, DescribeCallProgress(firstResponse));
        }

        if (firstResponse.Code is 401 or 407)
        {
            _activeCall = _activeCall with { RemoteTag = ExtractTag(firstResponse.Headers.GetValueOrDefault("to", "")) };
            await SendAckForFinalInviteResponseAsync(firstResponse, cancellationToken);

            var challengeHeader = firstResponse.Headers.TryGetValue("www-authenticate", out var wwwAuthenticate)
                ? wwwAuthenticate
                : firstResponse.Headers.GetValueOrDefault("proxy-authenticate", "");
            var authorization = BuildDigestAuthorization("INVITE", $"sip:{target}", challengeHeader);
            var authCseq = _inviteCseq++;
            var authBranch = CreateBranch();
            var secondInvite = BuildInvite(target, callId, localTag, authCseq, authBranch, authorization);
            DebugLog.Write($"SEND AUTH INVITE target={target} callId={callId}");
            _activeCall = new ActiveCall(callId, target, localTag, authCseq, authBranch, false, null);
            var secondResponse = await SendAndWaitFromListenerAsync(secondInvite, cancellationToken);
            DebugLog.Write($"AUTH INVITE RESPONSE code={secondResponse.Code} reason={secondResponse.Reason}");

            if (secondResponse.Code is >= 100 and < 300)
            {
                _activeCall = _activeCall with { Established = secondResponse.Code >= 200, RemoteTag = ExtractTag(secondResponse.Headers.GetValueOrDefault("to", "")) };
                if (secondResponse.Code >= 200)
                {
                    await AcknowledgeAndStartAudioAsync(secondResponse, cancellationToken);
                }
            }

            if (secondResponse.Code is >= 100 and < 300)
            {
                return new SipCallResult(true, DescribeCallProgress(secondResponse));
            }

            _activeCall = _activeCall with { RemoteTag = ExtractTag(secondResponse.Headers.GetValueOrDefault("to", "")) };
            await SendAckForFinalInviteResponseAsync(secondResponse, cancellationToken);
            _lastCallFailureResponse = secondResponse;
            _activeCall = null;
            _audioSession?.Dispose();
            _audioSession = null;
            return new SipCallResult(false, $"Call failed: {secondResponse.Code} {secondResponse.Reason}".Trim());
        }

        _activeCall = _activeCall with { RemoteTag = ExtractTag(firstResponse.Headers.GetValueOrDefault("to", "")) };
        await SendAckForFinalInviteResponseAsync(firstResponse, cancellationToken);
        _lastCallFailureResponse = firstResponse;
        _activeCall = null;
        _audioSession?.Dispose();
        _audioSession = null;
        return new SipCallResult(false, $"Call failed: {firstResponse.Code} {firstResponse.Reason}".Trim());
    }

    public async Task<SipCallResult> SendOptionsAsync(string? destination = null, CancellationToken cancellationToken = default)
    {
        if (_client is null || _config is null)
        {
            return new SipCallResult(false, "Register the SIP account first.");
        }

        var target = string.IsNullOrWhiteSpace(destination)
            ? _domain
            : destination.Contains('@') ? destination : $"{destination}@{_domain}";
        var options = BuildOptions(target, $"{Guid.NewGuid():N}@merlin-sip", _messageCseq++);
        DebugLog.Write($"SEND OPTIONS target={target}");
        var response = await SendAndWaitFromListenerAsync(options, cancellationToken);
        DebugLog.Write($"OPTIONS RESPONSE code={response.Code} reason={response.Reason}");

        return response.Code is >= 200 and < 300
            ? new SipCallResult(true, $"OPTIONS accepted: {response.Code} {response.Reason}".Trim())
            : new SipCallResult(false, $"OPTIONS failed: {response.Code} {response.Reason}".Trim());
    }

    public async Task<SipCallResult> SendMessageAsync(string destination, string message, CancellationToken cancellationToken = default)
    {
        if (_client is null || _config is null)
        {
            return new SipCallResult(false, "Register the SIP account first.");
        }

        if (string.IsNullOrWhiteSpace(destination))
        {
            return new SipCallResult(false, "Choose an extension before sending.");
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return new SipCallResult(false, "Enter a message before sending.");
        }

        if (!_registered)
        {
            var registration = await RegisterCurrentSocketAsync(cancellationToken);
            if (!registration.Connected)
            {
                return new SipCallResult(false, $"Registration check failed: {registration.Message}");
            }

            _registered = true;
        }

        var target = destination.Contains('@') ? destination : $"{destination}@{_domain}";
        var callId = $"{Guid.NewGuid():N}@merlin-sip";
        var localTag = Guid.NewGuid().ToString("N")[..12];
        var first = BuildMessage(target, callId, localTag, _messageCseq++, message, null);
        DebugLog.Write($"SEND MESSAGE target={target} callId={callId} length={message.Length}");
        var firstResponse = await SendAndWaitFromListenerAsync(first, cancellationToken);
        DebugLog.Write($"MESSAGE RESPONSE code={firstResponse.Code} reason={firstResponse.Reason}");

        if (firstResponse.Code is 401 or 407)
        {
            var challengeHeader = firstResponse.Headers.TryGetValue("www-authenticate", out var wwwAuthenticate)
                ? wwwAuthenticate
                : firstResponse.Headers.GetValueOrDefault("proxy-authenticate", "");
            var authorization = BuildDigestAuthorization("MESSAGE", $"sip:{target}", challengeHeader);
            var second = BuildMessage(target, callId, localTag, _messageCseq++, message, authorization);
            DebugLog.Write($"SEND AUTH MESSAGE target={target} callId={callId}");
            var secondResponse = await SendAndWaitFromListenerAsync(second, cancellationToken);
            DebugLog.Write($"AUTH MESSAGE RESPONSE code={secondResponse.Code} reason={secondResponse.Reason}");
            return secondResponse.Code is >= 200 and < 300
                ? new SipCallResult(true, "Message sent.")
                : new SipCallResult(false, $"Message failed: {secondResponse.Code} {secondResponse.Reason}".Trim());
        }

        return firstResponse.Code is >= 200 and < 300
            ? new SipCallResult(true, "Message sent.")
            : new SipCallResult(false, $"Message failed: {firstResponse.Code} {firstResponse.Reason}".Trim());
    }

    public async Task<SipCallResult> AnswerIncomingCallAsync(CancellationToken cancellationToken = default)
    {
        if (_client is null || _config is null)
        {
            return new SipCallResult(false, "Register the SIP account first.");
        }

        if (_pendingIncomingCall is null)
        {
            return new SipCallResult(false, "No incoming call is waiting to be answered.");
        }

        var pendingCall = _pendingIncomingCall;
        if (!TryParseRemoteAudio(pendingCall.Request, out var remoteAddress, out var remotePort, out var payloadType))
        {
            await SendSimpleResponseAsync(pendingCall.Request, pendingCall.RemoteEndPoint, 488, "Not Acceptable Here", cancellationToken, pendingCall.LocalTag);
            _pendingIncomingCall = null;
            DebugLog.Write($"INCOMING ANSWER failed no-remote-audio callId={pendingCall.CallId}");
            return new SipCallResult(false, "Unable to answer because the call did not include usable audio details.");
        }

        _audioSession?.Dispose();
        _audioSession = new RtpAudioSession(_config.AudioInput, _config.AudioOutput, _config.MicrophoneVolume, _config.HeadphoneVolume);
        try
        {
            _audioSession.PrepareDevices();
        }
        catch (Exception error)
        {
            DebugLog.Write($"RTP prepare failed incoming input={_config.AudioInput.Name} output={_config.AudioOutput.Name} error={error.Message}");
            _audioSession.Dispose();
            _audioSession = null;
            await SendSimpleResponseAsync(pendingCall.Request, pendingCall.RemoteEndPoint, 486, "Busy Here", cancellationToken, pendingCall.LocalTag);
            _pendingIncomingCall = null;
            QueueRegistrationRefresh("incoming answer audio failed");
            return new SipCallResult(false, $"Audio device failed to open: {error.Message}");
        }

        var answer = BuildIncomingAnswer(pendingCall, payloadType);
        var payload = Encoding.UTF8.GetBytes(answer);
        await _client.SendAsync(payload, pendingCall.RemoteEndPoint, cancellationToken);
        DebugLog.Write($"SEND INCOMING ANSWER callId={pendingCall.CallId} bytes={payload.Length}");

        _activeCall = new ActiveCall(
            pendingCall.CallId,
            pendingCall.RemoteTarget,
            pendingCall.LocalTag,
            _inviteCseq++,
            CreateBranch(),
            true,
            pendingCall.RemoteTag);
        _pendingIncomingCall = null;

        try
        {
            await _audioSession.StartAsync(remoteAddress, remotePort, payloadType);
        }
        catch (Exception error)
        {
            DebugLog.Write($"RTP start failed incoming remote={remoteAddress}:{remotePort} error={error.Message}");
        }

        CallProgress?.Invoke(this, new CallProgressEventArgs(200, "OK", true, "Call connected."));
        return new SipCallResult(true, "Call answered.");
    }

    public async Task<SipCallResult> EndCallAsync(CancellationToken cancellationToken = default)
    {
        if (_client is null || _config is null)
        {
            return new SipCallResult(false, "No active SIP call to end.");
        }

        if (_pendingIncomingCall is not null)
        {
            await SendSimpleResponseAsync(
                _pendingIncomingCall.Request,
                _pendingIncomingCall.RemoteEndPoint,
                486,
                "Busy Here",
                cancellationToken,
                _pendingIncomingCall.LocalTag);
            DebugLog.Write($"SEND INCOMING REJECT callId={_pendingIncomingCall.CallId}");
            _pendingIncomingCall = null;
            _audioSession?.Dispose();
            _audioSession = null;
            QueueRegistrationRefresh("incoming call rejected");
            return new SipCallResult(true, "Incoming call declined.");
        }

        if (_activeCall is null)
        {
            return new SipCallResult(false, "No active SIP call to end.");
        }

        var message = _activeCall.Established
            ? BuildBye(_activeCall)
            : BuildCancel(_activeCall);

        _audioSession?.Dispose();
        _audioSession = null;
        var payload = Encoding.UTF8.GetBytes(message);
        var method = _activeCall.Established ? "BYE" : "CANCEL";
        DebugLog.Write($"SEND {method} callId={_activeCall.CallId} bytes={payload.Length}");
        try
        {
            await _client.SendAsync(payload, _config.Server, _config.Port, cancellationToken);
        }
        catch (Exception error)
        {
            DebugLog.Write($"SEND {method} failed callId={_activeCall.CallId} error={error.Message}");
            return new SipCallResult(false, $"Unable to end call: {error.Message}");
        }
        _activeCall = null;
        QueueRegistrationRefresh("local hangup");
        return new SipCallResult(true, "Call ended.");
    }

    public async Task<SipCallResult> TransferAsync(string destination, CancellationToken cancellationToken = default)
    {
        if (_client is null || _config is null || _activeCall is null || !_activeCall.Established)
        {
            return new SipCallResult(false, "Connect a call before transferring.");
        }

        var transferTarget = destination.Contains('@') ? destination : $"{destination}@{_domain}";
        var refer = BuildRefer(_activeCall, transferTarget);
        var payload = Encoding.UTF8.GetBytes(refer);
        DebugLog.Write($"SEND REFER callId={_activeCall.CallId} target={transferTarget} bytes={payload.Length}");
        try
        {
            await _client.SendAsync(payload, _config.Server, _config.Port, cancellationToken);
            await SendByeAfterTransferAsync(_activeCall, cancellationToken);
        }
        catch (Exception error)
        {
            DebugLog.Write($"SEND REFER failed callId={_activeCall.CallId} error={error.Message}");
            return new SipCallResult(false, $"Unable to transfer call: {error.Message}");
        }
        _audioSession?.Dispose();
        _audioSession = null;
        _activeCall = null;
        QueueRegistrationRefresh("transfer");
        CallEnded?.Invoke(this, new CallEndedEventArgs("Call transferred."));
        return new SipCallResult(true, $"Transfer requested to {destination}. Call cleared locally.");
    }

    private async Task SendByeAfterTransferAsync(ActiveCall call, CancellationToken cancellationToken)
    {
        if (_client is null || _config is null)
        {
            return;
        }

        var bye = BuildBye(call);
        var payload = Encoding.UTF8.GetBytes(bye);
        DebugLog.Write($"SEND BYE after transfer callId={call.CallId} bytes={payload.Length}");
        await _client.SendAsync(payload, _config.Server, _config.Port, cancellationToken);
    }

    public void Dispose()
    {
        DisposeClient();
    }

    public Task<SipRegistrationResult> RefreshRegistrationAsync(CancellationToken cancellationToken = default)
    {
        return RegisterCurrentSocketAsync(cancellationToken);
    }

    private async Task<SipRegistrationResult> RegisterCurrentSocketAsync(CancellationToken cancellationToken)
    {
        if (_client is null || _config is null)
        {
            return new SipRegistrationResult(false, "SIP client is not initialized.");
        }

        if (_activeCall is not null)
        {
            DebugLog.Write("REGISTER skipped because a call is active");
            return new SipRegistrationResult(true, "Call in progress.");
        }

        if (_pendingResponse is not null)
        {
            DebugLog.Write("REGISTER skipped because a SIP transaction is active");
            return new SipRegistrationResult(true, "Connection check in progress.");
        }

        await _registrationLock.WaitAsync(cancellationToken);
        try
        {
            var callId = $"{Guid.NewGuid():N}@merlin-sip";
            var first = BuildRegister(callId, _registerCseq++, null);
            DebugLog.Write($"SEND REGISTER callId={callId} listener={_listenerStarted}");
            var firstResponse = _listenerStarted
                ? await SendAndWaitFromListenerAsync(first, cancellationToken)
                : await SendAndReceiveDirectAsync(first, cancellationToken);
            DebugLog.Write($"REGISTER RESPONSE code={firstResponse.Code} reason={firstResponse.Reason}");
            if (firstResponse.Code == 200)
            {
                _registered = true;
                return new SipRegistrationResult(true, "Registered and listening for calls.");
            }

            if (firstResponse.Code is not (401 or 407))
            {
                _registered = false;
                return new SipRegistrationResult(false, $"SIP server returned {firstResponse.Code} {firstResponse.Reason}".Trim());
            }

            var challengeHeader = firstResponse.Headers.TryGetValue("www-authenticate", out var wwwAuthenticate)
                ? wwwAuthenticate
                : firstResponse.Headers.GetValueOrDefault("proxy-authenticate", "");

            var authorization = BuildDigestAuthorization("REGISTER", $"sip:{_domain}", challengeHeader);
            var second = BuildRegister(callId, _registerCseq++, authorization);
            DebugLog.Write($"SEND AUTH REGISTER callId={callId} listener={_listenerStarted}");
            var secondResponse = _listenerStarted
                ? await SendAndWaitFromListenerAsync(second, cancellationToken)
                : await SendAndReceiveDirectAsync(second, cancellationToken);
            DebugLog.Write($"AUTH REGISTER RESPONSE code={secondResponse.Code} reason={secondResponse.Reason}");

            _registered = secondResponse.Code == 200;
            return _registered
                ? new SipRegistrationResult(true, "Registered and listening for calls.")
                : new SipRegistrationResult(false, $"SIP registration failed: {secondResponse.Code} {secondResponse.Reason}".Trim());
        }
        finally
        {
            _registrationLock.Release();
        }
    }

    private string BuildRegister(string callId, int cseq, string? authorization)
    {
        var branch = $"z9hG4bK-{Guid.NewGuid():N}";
        var tag = Guid.NewGuid().ToString("N")[..12];
        var contact = $"sip:{_config!.Extension}@{_localAddress}:{_localPort};transport=udp";
        var expires = _config.SipAlgCompatibilityMode
            ? CompatibilityRegisterExpiresSeconds
            : StandardRegisterExpiresSeconds;
        var lines = new List<string>
        {
            $"REGISTER sip:{_domain} SIP/2.0",
            $"Via: SIP/2.0/UDP {_localAddress}:{_localPort};branch={branch};rport",
            "Max-Forwards: 70",
            $"From: <sip:{_config.Extension}@{_domain}>;tag={tag}",
            $"To: <sip:{_config.Extension}@{_domain}>",
            $"Call-ID: {callId}",
            $"CSeq: {cseq} REGISTER",
            $"Contact: <{contact}>",
            $"Expires: {expires}",
            "User-Agent: CK Media Services Merlin SIP",
            "Allow: INVITE, ACK, CANCEL, BYE, OPTIONS, REGISTER, REFER, NOTIFY, MESSAGE",
            "Content-Length: 0"
        };

        if (!string.IsNullOrWhiteSpace(authorization))
        {
            lines.Insert(8, $"Authorization: {authorization}");
        }

        return string.Join("\r\n", lines) + "\r\n\r\n";
    }

    private string BuildInvite(string target, string callId, string localTag, int cseq, string branch, string? authorization)
    {
        var sdp = string.Join("\r\n", [
            "v=0",
            $"o=CKMediaServices 0 0 IN IP4 {_localAddress}",
            "s=CK Media Services call",
            $"c=IN IP4 {_localAddress}",
            "t=0 0",
            $"m=audio {_audioSession?.LocalPort ?? 40000} RTP/AVP 0 8 101",
            "a=rtpmap:0 PCMU/8000",
            "a=rtpmap:8 PCMA/8000",
            "a=rtpmap:101 telephone-event/8000",
            "a=fmtp:101 0-16",
            "a=sendrecv"
        ]) + "\r\n";

        var lines = new List<string>
        {
            $"INVITE sip:{target} SIP/2.0",
            $"Via: SIP/2.0/UDP {_localAddress}:{_localPort};branch={branch};rport",
            "Max-Forwards: 70",
            $"From: <sip:{_config!.Extension}@{_domain}>;tag={localTag}",
            $"To: <sip:{target}>",
            $"Call-ID: {callId}",
            $"CSeq: {cseq} INVITE",
            $"Contact: <sip:{_config.Extension}@{_localAddress}:{_localPort};transport=udp>",
            "User-Agent: CK Media Services Merlin SIP",
            "Allow: INVITE, ACK, CANCEL, BYE, OPTIONS, REGISTER, REFER, NOTIFY, MESSAGE",
            "Supported: replaces, timer",
            "Content-Type: application/sdp",
            $"Content-Length: {Encoding.UTF8.GetByteCount(sdp)}",
            "",
            sdp
        };

        if (!string.IsNullOrWhiteSpace(authorization))
        {
            lines.Insert(8, $"Authorization: {authorization}");
        }

        return string.Join("\r\n", lines);
    }

    private string BuildIncomingAnswer(PendingIncomingCall call, int payloadType)
    {
        var headers = ParseHeaders(call.Request);
        var via = headers.GetValueOrDefault("via", "");
        var from = headers.GetValueOrDefault("from", "");
        var to = EnsureToTag(headers.GetValueOrDefault("to", ""), call.LocalTag);
        var cseq = headers.GetValueOrDefault("cseq", "");
        var codecLine = payloadType == 8
            ? "a=rtpmap:8 PCMA/8000"
            : "a=rtpmap:0 PCMU/8000";

        var sdp = string.Join("\r\n", [
            "v=0",
            $"o=CKMediaServices 0 0 IN IP4 {_localAddress}",
            "s=CK Media Services call",
            $"c=IN IP4 {_localAddress}",
            "t=0 0",
            $"m=audio {_audioSession?.LocalPort ?? 40000} RTP/AVP {payloadType}",
            codecLine,
            "a=sendrecv"
        ]) + "\r\n";

        return string.Join("\r\n", [
            "SIP/2.0 200 OK",
            $"Via: {via}",
            $"From: {from}",
            $"To: {to}",
            $"Call-ID: {call.CallId}",
            $"CSeq: {cseq}",
            $"Contact: <sip:{_config!.Extension}@{_localAddress}:{_localPort};transport=udp>",
            "User-Agent: CK Media Services Merlin SIP",
            "Allow: INVITE, ACK, CANCEL, BYE, OPTIONS, REGISTER, REFER, NOTIFY, MESSAGE",
            "Content-Type: application/sdp",
            $"Content-Length: {Encoding.UTF8.GetByteCount(sdp)}",
            "",
            sdp
        ]);
    }

    private string BuildReInvite(ActiveCall call, int cseq, string branch, bool held, string? authorization)
    {
        var direction = held ? "sendonly" : "sendrecv";
        var sdp = string.Join("\r\n", [
            "v=0",
            $"o=CKMediaServices 0 0 IN IP4 {_localAddress}",
            "s=CK Media Services call",
            $"c=IN IP4 {_localAddress}",
            "t=0 0",
            $"m=audio {_audioSession?.LocalPort ?? 40000} RTP/AVP 0 8 101",
            "a=rtpmap:0 PCMU/8000",
            "a=rtpmap:8 PCMA/8000",
            "a=rtpmap:101 telephone-event/8000",
            "a=fmtp:101 0-16",
            $"a={direction}"
        ]) + "\r\n";

        var to = $"<sip:{call.Target}>";
        if (!string.IsNullOrWhiteSpace(call.RemoteTag))
        {
            to += $";tag={call.RemoteTag}";
        }

        var lines = new List<string>
        {
            $"INVITE sip:{call.Target} SIP/2.0",
            $"Via: SIP/2.0/UDP {_localAddress}:{_localPort};branch={branch};rport",
            "Max-Forwards: 70",
            $"From: <sip:{_config!.Extension}@{_domain}>;tag={call.LocalTag}",
            $"To: {to}",
            $"Call-ID: {call.CallId}",
            $"CSeq: {cseq} INVITE",
            $"Contact: <sip:{_config.Extension}@{_localAddress}:{_localPort};transport=udp>",
            "User-Agent: CK Media Services Merlin SIP",
            "Allow: INVITE, ACK, CANCEL, BYE, OPTIONS, REGISTER, REFER, NOTIFY, MESSAGE",
            "Supported: replaces, timer",
            "Content-Type: application/sdp",
            $"Content-Length: {Encoding.UTF8.GetByteCount(sdp)}",
            "",
            sdp
        };

        if (!string.IsNullOrWhiteSpace(authorization))
        {
            lines.Insert(8, $"Authorization: {authorization}");
        }

        return string.Join("\r\n", lines);
    }

    private string BuildCancel(ActiveCall call)
    {
        return string.Join("\r\n", [
            $"CANCEL sip:{call.Target} SIP/2.0",
            $"Via: SIP/2.0/UDP {_localAddress}:{_localPort};branch={call.InviteBranch};rport",
            "Max-Forwards: 70",
            $"From: <sip:{_config!.Extension}@{_domain}>;tag={call.LocalTag}",
            $"To: <sip:{call.Target}>",
            $"Call-ID: {call.CallId}",
            $"CSeq: {call.CSeq} CANCEL",
            "User-Agent: CK Media Services Merlin SIP",
            "Content-Length: 0",
            "",
            ""
        ]);
    }

    private string BuildBye(ActiveCall call)
    {
        var branch = $"z9hG4bK-{Guid.NewGuid():N}";
        var to = $"<sip:{call.Target}>";
        if (!string.IsNullOrWhiteSpace(call.RemoteTag))
        {
            to += $";tag={call.RemoteTag}";
        }

        return string.Join("\r\n", [
            $"BYE sip:{call.Target} SIP/2.0",
            $"Via: SIP/2.0/UDP {_localAddress}:{_localPort};branch={branch};rport",
            "Max-Forwards: 70",
            $"From: <sip:{_config!.Extension}@{_domain}>;tag={call.LocalTag}",
            $"To: {to}",
            $"Call-ID: {call.CallId}",
            $"CSeq: {_inviteCseq++} BYE",
            "User-Agent: CK Media Services Merlin SIP",
            "Content-Length: 0",
            "",
            ""
        ]);
    }

    private string BuildRefer(ActiveCall call, string transferTarget)
    {
        var branch = CreateBranch();
        var to = $"<sip:{call.Target}>";
        if (!string.IsNullOrWhiteSpace(call.RemoteTag))
        {
            to += $";tag={call.RemoteTag}";
        }

        return string.Join("\r\n", [
            $"REFER sip:{call.Target} SIP/2.0",
            $"Via: SIP/2.0/UDP {_localAddress}:{_localPort};branch={branch};rport",
            "Max-Forwards: 70",
            $"From: <sip:{_config!.Extension}@{_domain}>;tag={call.LocalTag}",
            $"To: {to}",
            $"Call-ID: {call.CallId}",
            $"CSeq: {_inviteCseq++} REFER",
            $"Contact: <sip:{_config.Extension}@{_localAddress}:{_localPort};transport=udp>",
            $"Refer-To: <sip:{transferTarget}>",
            $"Referred-By: <sip:{_config.Extension}@{_domain}>",
            "User-Agent: CK Media Services Merlin SIP",
            "Content-Length: 0",
            "",
            ""
        ]);
    }

    private string BuildMessage(string target, string callId, string localTag, int cseq, string message, string? authorization)
    {
        var body = message.Trim();
        var lines = new List<string>
        {
            $"MESSAGE sip:{target} SIP/2.0",
            $"Via: SIP/2.0/UDP {_localAddress}:{_localPort};branch={CreateBranch()};rport",
            "Max-Forwards: 70",
            $"From: <sip:{_config!.Extension}@{_domain}>;tag={localTag}",
            $"To: <sip:{target}>",
            $"Call-ID: {callId}",
            $"CSeq: {cseq} MESSAGE",
            $"Contact: <sip:{_config.Extension}@{_localAddress}:{_localPort};transport=udp>",
            "User-Agent: CK Media Services Merlin SIP",
            "Content-Type: text/plain; charset=utf-8",
            $"Content-Length: {Encoding.UTF8.GetByteCount(body)}",
            "",
            body
        };

        if (!string.IsNullOrWhiteSpace(authorization))
        {
            lines.Insert(8, $"Authorization: {authorization}");
        }

        return string.Join("\r\n", lines);
    }

    private string BuildOptions(string target, string callId, int cseq)
    {
        return string.Join("\r\n", [
            $"OPTIONS sip:{target} SIP/2.0",
            $"Via: SIP/2.0/UDP {_localAddress}:{_localPort};branch={CreateBranch()};rport",
            "Max-Forwards: 70",
            $"From: <sip:{_config!.Extension}@{_domain}>;tag={Guid.NewGuid():N}",
            $"To: <sip:{target}>",
            $"Call-ID: {callId}",
            $"CSeq: {cseq} OPTIONS",
            $"Contact: <sip:{_config.Extension}@{_localAddress}:{_localPort};transport=udp>",
            "User-Agent: CK Media Services Merlin SIP",
            "Accept: application/sdp",
            "Content-Length: 0",
            "",
            ""
        ]);
    }

    private async Task<SipResponse> SendAndReceiveDirectAsync(string message, CancellationToken cancellationToken)
    {
        if (_client is null || _config is null)
        {
            return new SipResponse(0, "SIP client is not initialized.", new Dictionary<string, string>(), "");
        }

        var payload = Encoding.UTF8.GetBytes(message);
        try
        {
            await _client.SendAsync(payload, _config.Server, _config.Port, cancellationToken);
        }
        catch (Exception error)
        {
            DebugLog.Write($"SEND DIRECT failed error={error.Message}");
            return new SipResponse(0, error.Message, new Dictionary<string, string>(), "");
        }
        DebugLog.Write($"SEND DIRECT bytes={payload.Length}");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            while (!linked.Token.IsCancellationRequested)
            {
                var result = await _client.ReceiveAsync(linked.Token);
                var text = Encoding.UTF8.GetString(result.Buffer);
                if (text.StartsWith("SIP/2.0", StringComparison.OrdinalIgnoreCase))
                {
                    var response = ParseResponse(text);
                    DebugLog.Write($"RECV DIRECT code={response.Code} reason={response.Reason} callId={response.Headers.GetValueOrDefault("call-id", "")}");
                    return response;
                }

                DebugLog.Write($"RECV DIRECT non-response bytes={result.Buffer.Length}");
            }
        }
        catch (OperationCanceledException)
        {
            DebugLog.Write("RECV DIRECT timed out");
            return new SipResponse(0, "Timed out waiting for SIP response.", new Dictionary<string, string>(), "");
        }
        catch (SocketException error)
        {
            DebugLog.Write($"RECV DIRECT socket error={error.Message}");
            return new SipResponse(0, error.Message, new Dictionary<string, string>(), "");
        }

        return new SipResponse(0, "Timed out waiting for SIP response.", new Dictionary<string, string>(), "");
    }

    private async Task<SipResponse> SendAndWaitFromListenerAsync(string message, CancellationToken cancellationToken)
    {
        if (_client is null || _config is null)
        {
            return new SipResponse(0, "SIP client is not initialized.", new Dictionary<string, string>(), "");
        }

        var waitSource = new TaskCompletionSource<SipResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingResponse = waitSource;
        var payload = Encoding.UTF8.GetBytes(message);
        try
        {
            await _client.SendAsync(payload, _config.Server, _config.Port, cancellationToken);
        }
        catch (Exception error)
        {
            DebugLog.Write($"SEND LISTENER failed error={error.Message}");
            return new SipResponse(0, error.Message, new Dictionary<string, string>(), "");
        }
        DebugLog.Write($"SEND LISTENER bytes={payload.Length}");

        var timeoutAt = DateTimeOffset.Now.AddSeconds(12);
        SipResponse lastResponse = new(0, "Timed out waiting for SIP call response.", new Dictionary<string, string>(), "");
        try
        {
            while (DateTimeOffset.Now < timeoutAt)
            {
                var completed = await Task.WhenAny(waitSource.Task, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
                if (completed != waitSource.Task)
                {
                    if (lastResponse.Code > 0)
                    {
                        return lastResponse;
                    }

                    continue;
                }

                var response = await waitSource.Task;
                lastResponse = response;
                if (response.Code >= 200 || response.Code is 401 or 407)
                {
                    return response;
                }

                RaiseCallProgress(response);
                waitSource = new TaskCompletionSource<SipResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingResponse = waitSource;
            }

            return lastResponse;
        }
        finally
        {
            if (ReferenceEquals(_pendingResponse, waitSource))
            {
                _pendingResponse = null;
            }
        }
    }

    private async Task AcknowledgeAndStartAudioAsync(SipResponse response, CancellationToken cancellationToken)
    {
        if (_activeCall is null || _client is null || _config is null || _audioSession is null)
        {
            return;
        }

        var ack = BuildAck(_activeCall, false);
        var ackPayload = Encoding.UTF8.GetBytes(ack);
        await _client.SendAsync(ackPayload, _config.Server, _config.Port, cancellationToken);
        DebugLog.Write($"SEND ACK callId={_activeCall.CallId} bytes={ackPayload.Length}");
        RaiseCallProgress(response);

        if (TryParseRemoteAudio(response.Raw, out var remoteAddress, out var remotePort, out var payloadType))
        {
            try
            {
                await _audioSession.StartAsync(remoteAddress, remotePort, payloadType);
            }
            catch (Exception error)
            {
                DebugLog.Write($"RTP start failed remote={remoteAddress}:{remotePort} error={error.Message}");
            }
        }
        else
        {
            DebugLog.Write($"RTP remote audio parse failed callId={_activeCall.CallId}");
        }
    }

    private async Task SendAckForFinalInviteResponseAsync(SipResponse response, CancellationToken cancellationToken)
    {
        if (_activeCall is null || _client is null || _config is null || response.Code < 300)
        {
            return;
        }

        await SendAckForInviteResponseAsync(response, cancellationToken, true);
    }

    private async Task SendAckForInviteResponseAsync(SipResponse response, CancellationToken cancellationToken, bool reuseInviteBranch)
    {
        if (_activeCall is null || _client is null || _config is null)
        {
            return;
        }

        var ack = BuildAck(_activeCall, reuseInviteBranch);
        var payload = Encoding.UTF8.GetBytes(ack);
        await _client.SendAsync(payload, _config.Server, _config.Port, cancellationToken);
        DebugLog.Write($"SEND ACK callId={_activeCall.CallId} code={response.Code} bytes={payload.Length}");
    }

    private string BuildAck(ActiveCall call, bool reuseInviteBranch)
    {
        var branch = reuseInviteBranch ? call.InviteBranch : CreateBranch();
        var to = $"<sip:{call.Target}>";
        if (!string.IsNullOrWhiteSpace(call.RemoteTag))
        {
            to += $";tag={call.RemoteTag}";
        }

        return string.Join("\r\n", [
            $"ACK sip:{call.Target} SIP/2.0",
            $"Via: SIP/2.0/UDP {_localAddress}:{_localPort};branch={branch};rport",
            "Max-Forwards: 70",
            $"From: <sip:{_config!.Extension}@{_domain}>;tag={call.LocalTag}",
            $"To: {to}",
            $"Call-ID: {call.CallId}",
            $"CSeq: {call.CSeq} ACK",
            $"Contact: <sip:{_config.Extension}@{_localAddress}:{_localPort};transport=udp>",
            "User-Agent: CK Media Services Merlin SIP",
            "Content-Length: 0",
            "",
            ""
        ]);
    }

    private void StartListening()
    {
        if (_listenerStarted)
        {
            return;
        }

        _listenerStarted = true;
        _listenCancellation = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoopAsync(_listenCancellation.Token));
    }

    private void StartRegistrationRefresh()
    {
        _registrationRefreshCancellation?.Cancel();
        _registrationRefreshCancellation?.Dispose();
        _registrationRefreshCancellation = new CancellationTokenSource();
        _ = Task.Run(() => RegistrationRefreshLoopAsync(_registrationRefreshCancellation.Token));
    }

    private void StartNatKeepAlive()
    {
        _natKeepAliveCancellation?.Cancel();
        _natKeepAliveCancellation?.Dispose();
        _natKeepAliveCancellation = new CancellationTokenSource();
        _ = Task.Run(() => NatKeepAliveLoopAsync(_natKeepAliveCancellation.Token));
    }

    private async Task NatKeepAliveLoopAsync(CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes("\r\n\r\n");
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var interval = _config?.SipAlgCompatibilityMode == true
                    ? TimeSpan.FromSeconds(15)
                    : TimeSpan.FromSeconds(25);
                await Task.Delay(interval, cancellationToken);
                if (_client is null || _config is null || _activeCall is not null || _pendingResponse is not null)
                {
                    continue;
                }

                await _client.SendAsync(payload, _config.Server, _config.Port, cancellationToken);
                DebugLog.Write("SEND NAT keepalive");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                DebugLog.Write($"NAT keepalive failed error={error.Message}");
            }
        }
    }

    private async Task RegistrationRefreshLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RegisterRefreshInterval, cancellationToken);
                if (_activeCall is not null || _pendingResponse is not null)
                {
                    DebugLog.Write("REGISTER refresh deferred while SIP transaction is active");
                    continue;
                }

                var result = await RegisterCurrentSocketAsync(cancellationToken);
                DebugLog.Write($"REGISTER refresh result connected={result.Connected} message={result.Message}");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                DebugLog.Write($"REGISTER refresh error={error.Message}");
            }
        }
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _client is not null)
        {
            try
            {
                var result = await _client.ReceiveAsync(cancellationToken);
                var message = Encoding.UTF8.GetString(result.Buffer);

                if (message.StartsWith("SIP/2.0", StringComparison.OrdinalIgnoreCase))
                {
                    var response = ParseResponse(message);
                    DebugLog.Write($"RECV SIP RESPONSE code={response.Code} reason={response.Reason} callId={response.Headers.GetValueOrDefault("call-id", "")}");
                    var isAwaitedResponse = _pendingResponse?.TrySetResult(response) == true;
                    if (isAwaitedResponse)
                    {
                        continue;
                    }

                    if (response.Code >= 200 && _activeCall is not null && response.Headers.GetValueOrDefault("call-id", "") == _activeCall.CallId)
                    {
                        var cseqMethod = ExtractCSeqMethod(response.Headers.GetValueOrDefault("cseq", ""));
                        if (!string.Equals(cseqMethod, "INVITE", StringComparison.OrdinalIgnoreCase))
                        {
                            DebugLog.Write($"RECV dialog response ignored method={cseqMethod} code={response.Code} callId={_activeCall.CallId}");
                            continue;
                        }

                        _activeCall = _activeCall with { Established = response.Code < 300, RemoteTag = ExtractTag(response.Headers.GetValueOrDefault("to", "")) };
                        if (response.Code < 300)
                        {
                            await AcknowledgeAndStartAudioAsync(response, cancellationToken);
                        }
                        else
                        {
                            RaiseCallProgress(response);
                        }
                    }
                    continue;
                }

                if (message.StartsWith("INVITE ", StringComparison.OrdinalIgnoreCase))
                {
                    DebugLog.Write("RECV INVITE");
                    await HandleIncomingInviteAsync(message, result.RemoteEndPoint, cancellationToken);
                }
                else if (message.StartsWith("OPTIONS ", StringComparison.OrdinalIgnoreCase))
                {
                    DebugLog.Write("RECV OPTIONS");
                    await SendSimpleResponseAsync(message, result.RemoteEndPoint, 200, "OK", cancellationToken);
                }
                else if (message.StartsWith("BYE ", StringComparison.OrdinalIgnoreCase))
                {
                    DebugLog.Write("RECV BYE");
                    await SendSimpleResponseAsync(message, result.RemoteEndPoint, 200, "OK", cancellationToken);
                    _audioSession?.Dispose();
                    _audioSession = null;
                    _activeCall = null;
                    _pendingIncomingCall = null;
                    CallEnded?.Invoke(this, new CallEndedEventArgs("Call ended."));
                    QueueRegistrationRefresh("remote BYE");
                }
                else if (message.StartsWith("CANCEL ", StringComparison.OrdinalIgnoreCase))
                {
                    DebugLog.Write("RECV CANCEL");
                    await SendSimpleResponseAsync(message, result.RemoteEndPoint, 200, "OK", cancellationToken);
                    _audioSession?.Dispose();
                    _audioSession = null;
                    _activeCall = null;
                    _pendingIncomingCall = null;
                    CallEnded?.Invoke(this, new CallEndedEventArgs("Incoming call was cancelled."));
                    QueueRegistrationRefresh("remote CANCEL");
                }
                else if (message.StartsWith("NOTIFY ", StringComparison.OrdinalIgnoreCase))
                {
                    DebugLog.Write("RECV NOTIFY");
                    await SendSimpleResponseAsync(message, result.RemoteEndPoint, 200, "OK", cancellationToken);
                }
                else if (message.StartsWith("MESSAGE ", StringComparison.OrdinalIgnoreCase))
                {
                    DebugLog.Write("RECV MESSAGE");
                    await HandleIncomingMessageAsync(message, result.RemoteEndPoint, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Keep the listener alive for later packets.
            }
        }
    }

    private async Task HandleIncomingInviteAsync(string message, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        var headers = ParseHeaders(message);
        var callId = headers.GetValueOrDefault("call-id", "");
        var from = headers.GetValueOrDefault("from", "Unknown caller");
        var localTag = Guid.NewGuid().ToString("N")[..12];

        if (_rejectIncomingCalls)
        {
            await SendSimpleResponseAsync(message, remoteEndPoint, 486, "Busy Here", cancellationToken, localTag);
            DebugLog.Write("RECV INVITE rejected because DND is enabled");
            QueueRegistrationRefresh("DND incoming reject");
            return;
        }

        await SendSimpleResponseAsync(message, remoteEndPoint, 100, "Trying", cancellationToken);
        await SendSimpleResponseAsync(message, remoteEndPoint, 180, "Ringing", cancellationToken, localTag);

        _pendingIncomingCall = new PendingIncomingCall(
            callId,
            message,
            remoteEndPoint,
            localTag,
            ExtractTag(from),
            ExtractRemoteTarget(headers),
            ExtractCSeqNumber(headers.GetValueOrDefault("cseq", "")));
        IncomingCall?.Invoke(this, new IncomingCallEventArgs(ExtractCaller(from), from));
    }

    private async Task HandleIncomingMessageAsync(string message, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        var headers = ParseHeaders(message);
        var from = headers.GetValueOrDefault("from", "Unknown sender");
        var body = ExtractBody(message).Trim();
        await SendSimpleResponseAsync(message, remoteEndPoint, 200, "OK", cancellationToken);

        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        IncomingMessage?.Invoke(this, new IncomingMessageEventArgs(ExtractCaller(from), from, body));
    }

    private async Task SendSimpleResponseAsync(
        string request,
        IPEndPoint remoteEndPoint,
        int code,
        string reason,
        CancellationToken cancellationToken,
        string? localTag = null)
    {
        if (_client is null)
        {
            return;
        }

        var headers = ParseHeaders(request);
        var via = headers.GetValueOrDefault("via", "");
        var from = headers.GetValueOrDefault("from", "");
        var to = headers.GetValueOrDefault("to", "");
        var callId = headers.GetValueOrDefault("call-id", "");
        var cseq = headers.GetValueOrDefault("cseq", "");

        if (code >= 180 && !to.Contains("tag=", StringComparison.OrdinalIgnoreCase))
        {
            to = EnsureToTag(to, localTag ?? Guid.NewGuid().ToString("N")[..12]);
        }

        var response = string.Join("\r\n", [
            $"SIP/2.0 {code} {reason}",
            $"Via: {via}",
            $"From: {from}",
            $"To: {to}",
            $"Call-ID: {callId}",
            $"CSeq: {cseq}",
            "User-Agent: CK Media Services Merlin SIP",
            "Content-Length: 0",
            "",
            ""
        ]);

        var payload = Encoding.UTF8.GetBytes(response);
        await _client.SendAsync(payload, remoteEndPoint, cancellationToken);
        DebugLog.Write($"SEND RESPONSE code={code} reason={reason} callId={callId} bytes={payload.Length}");
    }

    private string BuildDigestAuthorization(string method, string uri, string challengeHeader)
    {
        var challenge = ParseChallenge(challengeHeader);
        var realm = challenge.GetValueOrDefault("realm", _domain);
        var nonce = challenge.GetValueOrDefault("nonce", "");
        var qop = challenge.GetValueOrDefault("qop", "").Split(',').Select(value => value.Trim()).Contains("auth") ? "auth" : "";
        var nc = "00000001";
        var cnonce = Guid.NewGuid().ToString("N")[..16];
        var ha1 = Md5($"{_config!.Username}:{realm}:{_config.Password}");
        var ha2 = Md5($"{method}:{uri}");
        var response = string.IsNullOrWhiteSpace(qop)
            ? Md5($"{ha1}:{nonce}:{ha2}")
            : Md5($"{ha1}:{nonce}:{nc}:{cnonce}:{qop}:{ha2}");

        var fields = new List<string>
        {
            $"username=\"{_config.Username}\"",
            $"realm=\"{realm}\"",
            $"nonce=\"{nonce}\"",
            $"uri=\"{uri}\"",
            $"response=\"{response}\"",
            "algorithm=MD5"
        };

        if (challenge.TryGetValue("opaque", out var opaque))
        {
            fields.Add($"opaque=\"{opaque}\"");
        }

        if (!string.IsNullOrWhiteSpace(qop))
        {
            fields.Add($"qop={qop}");
            fields.Add($"nc={nc}");
            fields.Add($"cnonce=\"{cnonce}\"");
        }

        return $"Digest {string.Join(", ", fields)}";
    }

    private static SipResponse ParseResponse(string raw)
    {
        var lines = raw.Split(["\r\n", "\n"], StringSplitOptions.None);
        var match = Regex.Match(lines.FirstOrDefault() ?? "", @"^SIP/2.0\s+(\d{3})\s*(.*)$", RegexOptions.IgnoreCase);
        var headers = ParseHeaders(raw);

        return match.Success
            ? new SipResponse(int.Parse(match.Groups[1].Value), match.Groups[2].Value, headers, raw)
            : new SipResponse(0, "Invalid SIP response.", headers, raw);
    }

    private static Dictionary<string, string> ParseHeaders(string raw)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in raw.Split(["\r\n", "\n"], StringSplitOptions.None).Skip(1))
        {
            var index = line.IndexOf(':');
            if (index > 0)
            {
                headers[line[..index].Trim().ToLowerInvariant()] = line[(index + 1)..].Trim();
            }
        }

        return headers;
    }

    private static Dictionary<string, string> ParseChallenge(string header)
    {
        var value = Regex.Replace(header, "^Digest\\s+", "", RegexOptions.IgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in Regex.Matches(value, "(\\w+)=(?:\"([^\"]*)\"|([^,]*))"))
        {
            result[match.Groups[1].Value] = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value.Trim();
        }

        return result;
    }

    private static string ExtractCaller(string fromHeader)
    {
        var match = Regex.Match(fromHeader, @"sip:([^@>;]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : fromHeader;
    }

    private static string ExtractBody(string raw)
    {
        var bodyStart = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (bodyStart >= 0)
        {
            return raw[(bodyStart + 4)..];
        }

        bodyStart = raw.IndexOf("\n\n", StringComparison.Ordinal);
        return bodyStart >= 0 ? raw[(bodyStart + 2)..] : "";
    }

    private static string ExtractRemoteTarget(IReadOnlyDictionary<string, string> headers)
    {
        var source = headers.GetValueOrDefault("contact", "");
        if (string.IsNullOrWhiteSpace(source))
        {
            source = headers.GetValueOrDefault("from", "");
        }

        var match = Regex.Match(source, @"sip:([^>;,\s]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : "unknown";
    }

    private static int ExtractCSeqNumber(string cseq)
    {
        var parts = cseq.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && int.TryParse(parts[0], out var value) ? value : 1;
    }

    private static string ExtractCSeqMethod(string cseq)
    {
        var parts = cseq.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[1] : "";
    }

    private static string EnsureToTag(string to, string localTag)
    {
        if (to.Contains("tag=", StringComparison.OrdinalIgnoreCase))
        {
            return to;
        }

        return $"{to};tag={localTag}";
    }

    private static string? ExtractTag(string header)
    {
        var match = Regex.Match(header, @"(?:^|;)tag=([^;>\s]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private bool TryParseRemoteAudio(string response, out string address, out int port, out int payloadType)
    {
        address = _config?.Server ?? "";
        port = 0;
        payloadType = 0;
        var bodyStart = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var sdp = bodyStart >= 0 ? response[(bodyStart + 4)..] : response;

        foreach (var line in sdp.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("c=IN IP4 ", StringComparison.OrdinalIgnoreCase))
            {
                address = line["c=IN IP4 ".Length..].Trim();
            }
            else if (line.StartsWith("m=audio ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4 && int.TryParse(parts[1], out var parsedPort))
                {
                    port = parsedPort;
                    var payloads = parts.Skip(3).ToArray();
                    payloadType = payloads.Contains("0") ? 0 : payloads.Contains("8") ? 8 : 0;
                }
            }
        }

        return port > 0 && !string.IsNullOrWhiteSpace(address);
    }

    private static string Md5(string value)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string CreateBranch()
    {
        return $"z9hG4bK-{Guid.NewGuid():N}";
    }

    private void RaiseCallProgress(SipResponse response)
    {
        var connected = response.Code is >= 200 and < 300;
        CallProgress?.Invoke(this, new CallProgressEventArgs(response.Code, response.Reason, connected, DescribeCallProgress(response)));
    }

    private static string DescribeCallProgress(SipResponse response)
    {
        return response.Code switch
        {
            100 => "Call setup in progress.",
            180 => "Remote phone is ringing.",
            183 => "Remote side is providing early media.",
            >= 200 and < 300 => "Call connected.",
            >= 300 => $"Call failed: {response.Code} {response.Reason}".Trim(),
            _ => $"SIP response: {response.Code} {response.Reason}".Trim()
        };
    }

    private void QueueRegistrationRefresh(string reason)
    {
        _ = Task.Run(async () =>
        {
            await RefreshRegistrationAfterCallAsync(reason);
        });
    }

    private async Task RefreshRegistrationAfterCallAsync(string reason)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(900));

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                if (_client is null || _config is null)
                {
                    DebugLog.Write($"REGISTER post-call refresh abandoned reason={reason} attempt={attempt}");
                    return;
                }

                if (_activeCall is not null || _pendingResponse is not null)
                {
                    DebugLog.Write($"REGISTER post-call refresh waiting reason={reason} attempt={attempt}");
                    await Task.Delay(TimeSpan.FromMilliseconds(750));
                    continue;
                }

                var result = await RegisterCurrentSocketAsync(CancellationToken.None);
                DebugLog.Write($"REGISTER post-call refresh reason={reason} attempt={attempt} connected={result.Connected} message={result.Message}");
                if (result.Connected)
                {
                    return;
                }
            }
            catch (Exception error)
            {
                DebugLog.Write($"REGISTER post-call refresh failed reason={reason} attempt={attempt} error={error.Message}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(900 * attempt));
        }

        _registered = false;
        DebugLog.Write($"REGISTER post-call refresh exhausted reason={reason}");
    }

    private static string GetLocalAddress()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
                {
                    return address.Address.ToString();
                }
            }
        }

        return "127.0.0.1";
    }

    private void DisposeClient()
    {
        _listenCancellation?.Cancel();
        _listenCancellation?.Dispose();
        _listenCancellation = null;
        _listenerStarted = false;
        _registrationRefreshCancellation?.Cancel();
        _registrationRefreshCancellation?.Dispose();
        _registrationRefreshCancellation = null;
        _natKeepAliveCancellation?.Cancel();
        _natKeepAliveCancellation?.Dispose();
        _natKeepAliveCancellation = null;
        _pendingResponse?.TrySetCanceled();
        _pendingResponse = null;
        _pendingIncomingCall = null;
        _client?.Dispose();
        _client = null;
        _registered = false;
        _audioSession?.Dispose();
        _audioSession = null;
    }
}

public sealed record SipRegistrationResult(bool Connected, string Message);

public sealed record SipCallResult(bool Signalled, string Message);

public sealed record IncomingCallEventArgs(string CallerNumber, string RawFromHeader);

public sealed record IncomingMessageEventArgs(string SenderNumber, string RawFromHeader, string Message);

public sealed record CallProgressEventArgs(int Code, string Reason, bool Connected, string Message);

public sealed record CallEndedEventArgs(string Message);

internal sealed record SipResponse(int Code, string Reason, IReadOnlyDictionary<string, string> Headers, string Raw);

internal sealed record ActiveCall(string CallId, string Target, string LocalTag, int CSeq, string InviteBranch, bool Established, string? RemoteTag);

internal sealed record PendingIncomingCall(
    string CallId,
    string Request,
    IPEndPoint RemoteEndPoint,
    string LocalTag,
    string? RemoteTag,
    string RemoteTarget,
    int InviteCSeq);
