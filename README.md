# Merlin SIP

Merlin SIP is a native Windows softphone starter built for commercial licensing.

## Current Direction

The product is native Windows first. The app starts as a WPF desktop application, shows a license screen first, then asks for generic SIP account details before opening the main dialer workspace.

The placeholder test license key is:

```text
TEST-MERLIN-SIP
```

The license screen is intentionally separated from SIP setup so it can later call the accounts module activation endpoint before the user can configure an account.

## Self-Contained Windows Build

For a customer machine, publish a self-contained Windows build so the .NET runtime does not need to be installed separately:

```powershell
dotnet publish .\MerlinSIP\MerlinSIP.csproj /p:PublishProfile=SelfContainedWinX64
```

The output is written to:

```text
MerlinSIP\bin\Release\net8.0-windows\win-x64\publish\
```

That published output is the application package direction. The old Node prototype is not the product runtime.

## What This Version Includes

- Native WPF desktop shell for Windows.
- First-open license gate with a placeholder test key.
- Second-step SIP server and login credential setup.
- Native SIP REGISTER connection attempt over UDP.
- Dialer workspace with call controls, recent calls, and user-managed contacts.
- Contacts stored locally in JSON for quick dial and caller lookup.
- License activation placeholder ready to connect to the accounts module.
- Product, licensing, PBX, and mobile notes in `docs/`.

## Important SIP Note

The native app now attempts standard SIP registration over UDP. The next calling layer is SIP INVITE plus RTP audio, with device routing through the selected Windows audio devices.

## Node/Web Prototype Deprecated

The `node-app/` folder is retained as a prototype/reference only. It is not the target runtime because the finished product must be fully self-contained and must not require Node, npm, a browser, or a local web server on customer machines.

## Licensing Model

The included activation path expects a base64url JSON object:

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

Before selling this, replace the placeholder public key in `src/main/verifyLicense.ts` and generate licenses from a private signing service that never ships with the app.
