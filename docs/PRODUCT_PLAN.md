# Merlin SIP Product Plan

## Positioning

Merlin SIP should be a polished Windows softphone for small teams, managed IT providers, and sales/support desks that already have SIP accounts.

## Recommended Commercial Architecture

- Desktop app: native Windows WPF/.NET.
- SIP layer: native SIP/media stack embedded into the application.
- Packaging: self-contained win-x64 publish, then installer wrapping the published output.
- License enforcement: offline signed license files plus optional online seat activation.
- Update channel: signed auto-updates after code signing is configured.
- PBX compatibility: support classic PBX deployments without requiring the customer to install Node, browser runtimes, or a separate web server.
- PBX adapters: keep Yeastar S100 and FreePBX behavior behind a common contract.
- Mobile path: share product concepts and API contracts, but use native call handling and push for inbound mobile calls.

## Why Not Bundle PJSIP By Default

PJSIP is technically excellent, but it is GPL unless you arrange a proprietary license. That can be the right move for a mature product, but it is not ideal for a fast commercial prototype unless the budget includes that license.

SIP.js is open source and works naturally in Electron, but it requires SIP over WebSocket. That tradeoff keeps the early product legally cleaner and easier to ship.

## MVP Feature Set

- Account registration.
- Outbound and inbound audio calls.
- Hold, mute, DTMF, attended and blind transfer.
- Call history.
- Contacts CSV import.
- Audio device selection.
- Local encrypted account storage.
- Signed offline license activation.
- Crash/error logging with user consent.
- Windows installer and code signing.

## Paid Features

- Multi-account support.
- Call recording.
- Team call notes.
- Click-to-call URL handler.
- CRM screen-pop integrations.
- Admin-managed config profiles.
- White-label branding.

## License Tiers

- Solo: one user, one account.
- Team: multiple seats, call history, contacts, transfer.
- Pro: recording, CRM integrations, provisioning, priority support.
- White Label: custom branding and installer metadata.

## Next Build Steps

1. Add native SIP registration, inbound calls, outbound calls, and media attachment.
2. Add microphone/speaker device selection through Windows audio APIs.
3. Store SIP/PBX credentials with Windows-protected encrypted storage.
4. Wire Yeastar S100 adapter against the exact enabled API/CTI endpoints on your PBX.
5. Wire FreePBX adapter against secured AMI/ARI/CDR/recording access.
6. Replace the license placeholder with accounts-module activation and signed offline validation.
7. Add seat counts, revocation, and renewal state to the activation flow.
8. Publish self-contained win-x64 builds and wrap them in a signed installer.
9. Add code signing, auto-update, and installer branding.
