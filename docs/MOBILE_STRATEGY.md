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

## Current APK Wrapper

The repository now includes a Capacitor Android wrapper for the existing `node-app/public` dialer UI. This is the quickest APK path for previewing the mobile product shape:

- `package.json` defines Capacitor Android scripts.
- `capacitor.config.json` points Android at `node-app/public`.
- The web client opens on the dialer and has an offline/local fallback so the APK can run without the Node prototype server.

Build flow:

```powershell
npm install
npm run android:add
npm run android:build
```

The debug APK is produced at:

```text
android\app\build\outputs\apk\debug\app-debug.apk
```

The review copy for this workspace is:

```text
outputs\merlin-sip-debug.apk
```

For iOS, Capacitor can generate the `ios/` project on Windows, but final dependency install, build, signing, and simulator/device review require macOS with Xcode and CocoaPods:

```powershell
npm install
npm run android:build
```

On a Mac:

```zsh
npm install
npx cap sync ios
npx cap open ios
```

This wrapper is not the final production SIP/media implementation. Native Android microphone permissions, audio routing, background incoming calls, and SIP/WebRTC media should be implemented with the mobile stack chosen above.
