    public async Task PublishPresenceAsync(string status, CancellationToken cancellationToken = default)
    {
        if (!IsTransportReady || _config is null || !_registered)
        {
            return;
        }

        var basicState = string.Equals(status, "Available", StringComparison.OrdinalIgnoreCase) ? "open" : "closed";
        var pidfXml = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>
<presence xmlns=\"urn:ietf:params:xml:ns:pidf\" entity=\"sip:{_config.Extension}@{_domain}\">
  <tuple id=\"{_config.Extension}\">
    <status>
      <basic>{basicState}</basic>
    </status>
    <note>{status}</note>
  </tuple>
</presence>";

        _publishCallId ??= CreateCallId();
        var first = BuildPublish(_config.Extension, _publishCallId, _localTag, _publishCseq++, pidfXml, null, _publishEtag);
        DebugLog.Write($"SEND PUBLISH presence={status} state={basicState}\");
        
        var firstResponse = await SendAndWaitFromListenerAsync(first, cancellationToken);
        if (firstResponse.Code is 401 or 407)
        {
            var challengeHeader = firstResponse.Headers.TryGetValue("www-authenticate", out var wwwAuthenticate)
                ? wwwAuthenticate
                : firstResponse.Headers.GetValueOrDefault("proxy-authenticate", "");
            var authorization = BuildDigestAuthorization("PUBLISH", $"sip:{_config.Extension}@{_domain}\", challengeHeader);
            var second = BuildPublish(_config.Extension, _publishCallId, _localTag, _publishCseq++, pidfXml, authorization, _publishEtag);
            var secondResponse = await SendAndWaitFromListenerAsync(second, cancellationToken);
            if (secondResponse.Code is >= 200 and < 300)
            {
                _publishEtag = secondResponse.Headers.GetValueOrDefault("sip-etag");
            }
        }
        else if (firstResponse.Code is >= 200 and < 300)
        {
            _publishEtag = firstResponse.Headers.GetValueOrDefault("sip-etag");
        }
        else if (firstResponse.Code == 412) // Precondition Failed (Invalid SIP-ETag)
        {
            _publishEtag = null;
        }
    }

    private string BuildPublish(string target, string callId, string localTag, int cseq, string body, string? authorization, string? etag)
    {
        var lines = new List<string>
        {
            $"PUBLISH sip:{target}@{_domain} SIP/2.0\",
            $"Via: SIP/2.0/{SipTransportName} {_localAddress}:{_localPort};branch={CreateBranch()};rport\",
            "Max-Forwards: 70",
            $"From: <sip:{_config!.Extension}@{_domain}>;tag={localTag}\",
            $"To: <sip:{target}@{_domain}>\",
            $"Call-ID: {callId}\",
            $"CSeq: {cseq} PUBLISH\",
            $"Contact: <sip:{_config.Extension}@{_localAddress}:{_localPort};transport={ContactTransport}>\",
            "Event: presence",
            "Expires: 3600",
            "User-Agent: CK Media Services Merlin SIP"
        };

        if (!string.IsNullOrEmpty(etag))
        {
            lines.Add($"SIP-If-Match: {etag}\");
        }
        if (!string.IsNullOrEmpty(authorization))
        {
            lines.Add($"Authorization: {authorization}\");
        }

        lines.Add("Content-Type: application/pidf+xml");
        lines.Add($"Content-Length: {Encoding.UTF8.GetByteCount(body)}\");
        lines.Add("");
        lines.Add(body);

        return string.Join("\r\n", lines);
    }
