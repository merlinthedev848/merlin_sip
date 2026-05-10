# PBX Integration Matrix

Merlin SIP needs two layers:

- SIP/media layer for calls, DTMF, hold, transfer, and audio.
- PBX companion layer for presence, queues, voicemail, recordings, call history, DND, forwarding, screen-pop, and admin workflows.

## Yeastar S100

Yeastar S100 is part of the S-Series line and is end-of-sale. Yeastar lists it as supporting up to 100 users, expandable to 200, and 30 concurrent calls, expandable to 60. The product direction should support S-Series deployments while keeping an upgrade path for Yeastar P-Series.

| Feature | Integration Path | Notes |
| --- | --- | --- |
| Register extension | SIP | Prefer WSS/WebRTC where enabled; otherwise use a PBX/SBC bridge. |
| Inbound/outbound calls | SIP or CTI originate | SIP handles direct calls; CTI gives click-to-call workflows. |
| Hold/resume | SIP | Standard SIP/WebRTC session control. |
| Blind/attended transfer | SIP plus PBX feature codes | Feature codes vary by PBX configuration. |
| DTMF | SIP INFO/RTP events | Needed for IVR and voicemail. |
| Presence/BLF | Yeastar API/Linkus/CTI source | SIP alone is not enough for a polished operator panel. |
| Queues/agents | Yeastar call center/API integration | QueueMetrics/Asternic integrations may also be present. |
| Voicemail | Feature codes plus API/media access | API playback depends on permissions. |
| Recordings | Yeastar recording/CDR access | Requires admin/user permissions. |
| CDR/call history | Yeastar CDR/API/export | Needed for reliable call history. |
| Corporate directory | Yeastar directory/Linkus source | Sync into local cache. |
| CRM screen-pop | Caller ID events plus CRM connector | Yeastar documents CRM integration patterns. |
| Provisioning | Yeastar admin/API | Useful for licensed business deployments. |

## FreePBX

FreePBX is an Asterisk distribution, so the deep integration path is usually:

- PJSIP/SIP for endpoint registration.
- Asterisk AMI for live events, originate, queues, device state, and call control.
- Asterisk ARI for advanced app-controlled channels/bridges where enabled.
- FreePBX modules, databases, and feature codes for voicemail, forwarding, DND, CDR, recordings, and endpoint management.

| Feature | Integration Path | Notes |
| --- | --- | --- |
| Register extension | SIP/PJSIP | Native desktop SIP or WSS bridge. |
| Calls/hold/transfer/DTMF | SIP | App-level media controls. |
| Click-to-call | AMI Originate or ARI | Requires secured PBX credentials. |
| Presence/BLF | AMI device state/events | Subscribe to live events. |
| Queues/agents | AMI QueueStatus/QueuePause/events | Enables supervisor view. |
| Voicemail | Feature codes or voicemail storage/API | Exact path depends on deployment. |
| Recordings | CDR/monitor storage/module access | Permissions and storage layout vary. |
| DND/forwarding | FreePBX feature codes | Codes can be customized by admins. |
| Contacts | Userman/Contact Manager | Sync where available. |
| Endpoint provisioning | Endpoint Manager | Commercial module may be involved. |

## Product Rule

Do not hard-code PBX behavior in the UI. Every PBX feature should be called through a vendor adapter so Yeastar S100, Yeastar P-Series, and FreePBX can each map the same app button to the correct SIP command, feature code, AMI action, ARI call, or vendor API endpoint.
