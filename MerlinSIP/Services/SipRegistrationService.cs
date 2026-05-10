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
    private UdpClient? _client;
    private AppStartupConfig? _config;
    private string _domain = "";
    private string _localAddress = "127.0.0.1";
    private int _localPort;
    private CancellationTokenSource? _listenCancellation;
    private CancellationTokenSource? _registrationRefreshCancellation;
    private TaskCompletionSource<SipResponse>? _pendingResponse;
    private readonly SemaphoreSlim _registrationLock = new(1, 1);
    private bool _listenerStarted;
    private bool _registered;
    private int _inviteCseq = 1;
    private int _registerCseq = 1;
    private ActiveCall? _activeCall;
    private RtpAudioSession? _audioSession;

    public event EventHandler<IncomingCallEventArgs>? IncomingCall;

    public async Task<SipRegistrationResult> RegisterAsync(AppStartupConfig config, CancellationToken cancellationToken = default)
    {
        DisposeClient();

        _config = config;
        _domain = string.IsNullOrWhiteSpace(config.Domain) ? config.Server : config.Domain;
        _localAddress = GetLocalAddress();
        _client = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        _localPort = ((IPEndPoint)_client.Client.LocalEndPoint!).Port;

        var result = await RegisterCurrentSocketAsync(cancellationToken);
        if (result.Connected)
        {
            _registered = true;
            StartListening();
            StartRegistrationRefresh();
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
        _audioSession?.Dispose();
        _audioSession = new RtpAudioSession(_config.AudioInput, _config.AudioOutput);
        var invite = BuildInvite(target, callId, localTag, cseq, null);
        DebugLog.Write($"SEND INVITE target={target} callId={callId}");
        _activeCall = new ActiveCall(callId, target, localTag, cseq, false, null);
        var firstResponse = await SendAndWaitFromListenerAsync(invite, cancellationToken);
        DebugLog.Write($"INVITE RESPONSE code={firstResponse.Code} reason={firstResponse.Reason}");

        if (firstResponse.Code is >= 100 and < 300)
        {
            _activeCall = _activeCall with { Established = firstResponse.Code >= 200, RemoteTag = ExtractTag(firstResponse.Headers.GetValueOrDefault("to", "")) };
            if (firstResponse.Code >= 200)
            {
                await AcknowledgeAndStartAudioAsync(firstResponse, cancellationToken);
            }
            return new SipCallResult(true, $"Outbound call signalled: {firstResponse.Code} {firstResponse.Reason}".Trim());
        }

        if (firstResponse.Code is 401 or 407)
        {
            var challengeHeader = firstResponse.Headers.TryGetValue("www-authenticate", out var wwwAuthenticate)
                ? wwwAuthenticate
                : firstResponse.Headers.GetValueOrDefault("proxy-authenticate", "");
            var authorization = BuildDigestAuthorization("INVITE", $"sip:{target}", challengeHeader);
            var authCseq = _inviteCseq++;
            var secondInvite = BuildInvite(target, callId, localTag, authCseq, authorization);
            DebugLog.Write($"SEND AUTH INVITE target={target} callId={callId}");
            _activeCall = new ActiveCall(callId, target, localTag, authCseq, false, null);
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
                return new SipCallResult(true, $"Outbound call signalled: {secondResponse.Code} {secondResponse.Reason}".Trim());
            }

            _activeCall = null;
            _audioSession?.Dispose();
            _audioSession = null;
            return new SipCallResult(false, $"Call failed: {secondResponse.Code} {secondResponse.Reason}".Trim());
        }

        _activeCall = null;
        _audioSession?.Dispose();
        _audioSession = null;
        return new SipCallResult(false, $"Call failed: {firstResponse.Code} {firstResponse.Reason}".Trim());
    }

    public async Task<SipCallResult> EndCallAsync(CancellationToken cancellationToken = default)
    {
        if (_client is null || _config is null || _activeCall is null)
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
        DebugLog.Write($"SEND {method} callId={_activeCall.CallId}");
        await _client.SendAsync(payload, _config.Server, _config.Port, cancellationToken);
        _activeCall = null;
        return new SipCallResult(true, $"{method} sent to SIP server.");
    }

    public void Dispose()
    {
        DisposeClient();
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
            return new SipRegistrationResult(_registered, "Registration refresh skipped during active call.");
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
            "Expires: 120",
            "User-Agent: Merlin SIP",
            "Allow: INVITE, ACK, CANCEL, BYE, OPTIONS, REGISTER, REFER, NOTIFY",
            "Content-Length: 0"
        };

        if (!string.IsNullOrWhiteSpace(authorization))
        {
            lines.Insert(8, $"Authorization: {authorization}");
        }

        return string.Join("\r\n", lines) + "\r\n\r\n";
    }

    private string BuildInvite(string target, string callId, string localTag, int cseq, string? authorization)
    {
        var branch = $"z9hG4bK-{Guid.NewGuid():N}";
        var sdp = string.Join("\r\n", [
            "v=0",
            $"o=MerlinSIP 0 0 IN IP4 {_localAddress}",
            "s=Merlin SIP call",
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
            "User-Agent: Merlin SIP",
            "Allow: INVITE, ACK, CANCEL, BYE, OPTIONS, REGISTER, REFER, NOTIFY",
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
        var branch = $"z9hG4bK-{Guid.NewGuid():N}";
        return string.Join("\r\n", [
            $"CANCEL sip:{call.Target} SIP/2.0",
            $"Via: SIP/2.0/UDP {_localAddress}:{_localPort};branch={branch};rport",
            "Max-Forwards: 70",
            $"From: <sip:{_config!.Extension}@{_domain}>;tag={call.LocalTag}",
            $"To: <sip:{call.Target}>",
            $"Call-ID: {call.CallId}",
            $"CSeq: {call.CSeq} CANCEL",
            "User-Agent: Merlin SIP",
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
            "User-Agent: Merlin SIP",
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
        await _client.SendAsync(payload, _config.Server, _config.Port, cancellationToken);
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
        await _client.SendAsync(payload, _config.Server, _config.Port, cancellationToken);
        DebugLog.Write($"SEND LISTENER bytes={payload.Length}");

        var timeoutAt = DateTimeOffset.Now.AddSeconds(30);
        SipResponse lastResponse = new(0, "Timed out waiting for SIP call response.", new Dictionary<string, string>(), "");
        try
        {
            while (DateTimeOffset.Now < timeoutAt)
            {
                var completed = await Task.WhenAny(waitSource.Task, Task.Delay(TimeSpan.FromSeconds(6), cancellationToken));
                if (completed != waitSource.Task)
                {
                    return lastResponse.Code > 0 ? lastResponse : new SipResponse(0, "Timed out waiting for SIP call response.", new Dictionary<string, string>(), "");
                }

                var response = await waitSource.Task;
                lastResponse = response;
                if (response.Code >= 200 || response.Code is 401 or 407)
                {
                    return response;
                }

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

        var ack = BuildAck(_activeCall);
        await _client.SendAsync(Encoding.UTF8.GetBytes(ack), _config.Server, _config.Port, cancellationToken);

        if (TryParseRemoteAudio(response.Raw, out var remoteAddress, out var remotePort, out var payloadType))
        {
            await _audioSession.StartAsync(remoteAddress, remotePort, payloadType);
        }
    }

    private string BuildAck(ActiveCall call)
    {
        var branch = $"z9hG4bK-{Guid.NewGuid():N}";
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
            "User-Agent: Merlin SIP",
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

    private async Task RegistrationRefreshLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(55), cancellationToken);
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
                    _pendingResponse?.TrySetResult(response);
                    if (response.Code >= 200 && _activeCall is not null && response.Headers.GetValueOrDefault("call-id", "") == _activeCall.CallId)
                    {
                        _activeCall = _activeCall with { Established = response.Code < 300, RemoteTag = ExtractTag(response.Headers.GetValueOrDefault("to", "")) };
                        if (response.Code < 300)
                        {
                            await AcknowledgeAndStartAudioAsync(response, cancellationToken);
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
        await SendSimpleResponseAsync(message, remoteEndPoint, 100, "Trying", cancellationToken);
        await SendSimpleResponseAsync(message, remoteEndPoint, 180, "Ringing", cancellationToken);

        var headers = ParseHeaders(message);
        var from = headers.GetValueOrDefault("from", "Unknown caller");
        IncomingCall?.Invoke(this, new IncomingCallEventArgs(ExtractCaller(from), from));
    }

    private async Task SendSimpleResponseAsync(string request, IPEndPoint remoteEndPoint, int code, string reason, CancellationToken cancellationToken)
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
            to += $";tag={Guid.NewGuid():N}"[..16];
        }

        var response = string.Join("\r\n", [
            $"SIP/2.0 {code} {reason}",
            $"Via: {via}",
            $"From: {from}",
            $"To: {to}",
            $"Call-ID: {callId}",
            $"CSeq: {cseq}",
            "User-Agent: Merlin SIP",
            "Content-Length: 0",
            "",
            ""
        ]);

        await _client.SendAsync(Encoding.UTF8.GetBytes(response), remoteEndPoint, cancellationToken);
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
        _pendingResponse?.TrySetCanceled();
        _pendingResponse = null;
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

internal sealed record SipResponse(int Code, string Reason, IReadOnlyDictionary<string, string> Headers, string Raw);

internal sealed record ActiveCall(string CallId, string Target, string LocalTag, int CSeq, bool Established, string? RemoteTag);
