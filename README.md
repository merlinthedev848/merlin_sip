# Merlin SIP

Merlin SIP is a native Windows softphone starter built for commercial licensing.

## Current Direction

The product is native Windows first. The app starts as a WPF desktop application, shows a license screen first, then asks for generic SIP account details before opening the main dialer workspace.

The license screen is intentionally separated from SIP setup so it can later call the accounts module activation endpoint before the user can configure an account.

## Self-Contained Windows Build

For a customer machine, publish a self-contained Windows build so the .NET runtime does not need to be installed separately:

```powershell
dotnet publish .\MerlinSIP\MerlinSIP.csproj /p:PublishProfile=SelfContainedWinX64
```

The output is written to:

```text
MerlinSIP\bin\Release\net10.0-windows\win-x64\publish\
```

That published output is the application package direction. The old Node prototype is not the product runtime.

## What This Version Includes

- Native WPF desktop shell for Windows.
- First-open license gate with a debug-only placeholder key.
- Second-step SIP server and login credential setup.
- Native SIP REGISTER connection attempt over UDP.
- Dialer workspace with call controls, recent calls, and user-managed contacts.
- Contacts stored locally in JSON for quick dial and caller lookup.
- License activation flow connected through the app services layer.
- Product, licensing, and PBX notes in `docs/`.

## Important SIP Note

The native app now attempts standard SIP registration over UDP. The next calling layer is SIP INVITE plus RTP audio, with device routing through the selected Windows audio devices.

## Licensing Model

The activation path expects a base64url JSON object:

```json
{
  "payload": {
    "name": "Customer Ltd",
    "seats": 10,
    "expiresAt": "2027-05-08",
    "features": ["voice", "transfer", "recording"]
  },
  "signature": "base64url-ed25519-signature"
}
```

License signing material must stay server-side and must never ship with the app.
