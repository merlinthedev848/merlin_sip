# Mobile Strategy

The Windows app should be built so it can become Android and iOS without starting again.

## Recommended Shape

- Keep the product language, screens, PBX adapters, licensing rules, and settings model in shared TypeScript packages.
- Keep Electron-specific code limited to the desktop shell, secure storage, updater, and installer.
- Build mobile screens in React Native using the same design tokens and PBX adapter contracts.
- Use native mobile call handling for iOS CallKit and Android Telecom/ConnectionService.
- Use push notifications for inbound call wakeups. Mobile apps cannot rely on a forever-running SIP registration in the background.
- Use a PBX/SBC/push gateway for reliable mobile inbound calls.

## What Can Be Shared

- PBX feature matrix.
- Yeastar and FreePBX adapter contracts.
- License activation flow.
- Contact, call history, queue, voicemail, and presence models.
- Visual design tokens and component behavior.

## What Must Be Native

- Microphone permissions.
- Audio routing.
- Bluetooth/headset behavior.
- Push notification registration.
- Background incoming call handling.
- OS-level incoming call UI.
- App Store and Play Store billing/licensing constraints.

## Practical Mobile Stack

Use React Native for the app shell and shared UI logic. For media, choose between:

- SIP over WSS/WebRTC with a maintained React Native WebRTC stack.
- A commercial native SIP SDK if you need classic UDP/TCP/TLS SIP and stronger background behavior.

The second path costs more but is usually more reliable for business telephony.
