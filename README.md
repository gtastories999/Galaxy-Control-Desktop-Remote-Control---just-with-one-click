# Galaxy Control

Minimal, attended Windows remote support: **enter the technician's email → connect directly**. Light UI with Galaxy yellow-to-gold accents.

## How it works

1. Choose **I'm a technician**. New users see **Sign Up** as the primary action; existing users switch using **Already have an account? Sign In**. Registration, email confirmation and sign-in stay inside the app. The selected sign-in preference is remembered for this installation.
2. The customer opens the app, enters only that email and clicks **Connect to technician**. IP addresses and ports are handled in the background; no customer registration screen is shown.
3. The technician accepts. The apps exchange session-specific WebRTC addresses through Supabase and establish **direct, encrypted P2P** screen and control channels. Supabase updates the technician's email/IP/port mapping as actual ICE endpoints are gathered.
4. **Stop sharing**, closing either app or a connection failure ends control and releases held keys. There is no unattended access or automatic reconnection.

| Component | What it does | Desktop/control payload |
| --- | --- | --- |
| Your Supabase project | Verified technician email directory, online leases, support requests and WebRTC SDP/ICE metadata | Never |
| STUN | Discovers public network endpoints for NAT traversal | Never |
| Direct WebRTC connection | DTLS-encrypted screen and mouse/keyboard channels between the PCs | Yes |
| TURN/media relay | Not included | Never |

A plain IP/port row does not remove NAT restrictions. Actual session candidates are exchanged automatically. Networks requiring a relay fail clearly; Galaxy Control does not fall back to server-based media transport.

## Roles, sign-in and updates

The role selector offers **Get Help**, **I'm a technician** and **Admin**.

- **Get Help:** enter the technician's email and connect.
- **I'm a technician:** one primary **Sign Up** or **Sign In** action, with a small link to switch. There is no Supabase setup action. A missing project connection asks the technician to obtain the operator's configured app; it never opens developer setup.
- **Admin:** **Connect Supabase** appears above **Sign In**. No technician registration action appears here. Admin sign-in verifies the current user through the project's Auth server and requires server-controlled `app_metadata.galaxy_role = "admin"`. Editable user metadata never grants administration. Admin sign-in does not start a remote-support session.

A developer-configured download imports and saves its public project connection automatically. Updates reuse it even from a different folder. Technicians never copy configuration files or use a separate HTML page.

## Administrator project setup

Choose **Admin → Connect Supabase**. The owner authorizes the distributor's Supabase integration in the external browser, selects a dedicated Galaxy Control project and supplies the installation admin email/password. The app installs and verifies the directory schema and public configuration, creates the account if necessary, and assigns the admin role using the owner's temporary Management API authorization. Email confirmation remains required. Existing installations can repeat this flow to assign their owner account; an ordinary technician signup cannot assign itself an admin role.

After confirmation, use **Admin → Sign In**. To create a technician account, switch to **I'm a technician → Sign Up**. Account roles are separate from selecting a UI mode. Project setup always requires Supabase owner authorization, even when accessed without a prior app login; a local UI is not a server authorization boundary.

**Every developer/fork registers their own OAuth application and deploys their own exchange backend.** Follow the [developer setup guide](supabase/ACCOUNT-CONNECTION.md), then run `python scripts/configure-provider.py YOUR_HTTPS_BROKER_URL` before building. OAuth secrets and owner tokens are never distributed or persisted. Generic GitHub downloads have no shared provider or personal project configured. Exported apps contain only public project settings.

Auth role handling checked against the official [Auth user verification](https://supabase.com/docs/reference/javascript/auth-getuser) and [authorization guidance](https://supabase.com/docs/guides/database/postgres/row-level-security) on 2026-10-23. The current changelog introduced no change to this HTTP flow. Contract tests use isolated handlers; they do not claim a live owner authorization or two-PC test.

## Own Supabase required

**There is no shared project in this repository or the GitHub download.** Both participants use the technician's project configuration. No secret/service-role key belongs in the app.

As an alternative to automatic setup, follow the [manual Supabase setup guide](supabase/README.md), install the schema and configure your own public URL/key. This manual route requires no OAuth exchange backend.

For a personal source build:

```bash
python scripts/configure.py https://YOUR-PROJECT.supabase.co sb_publishable_YOUR_OWN_KEY
```

This creates a **Git-ignored local configuration**. Generic public builds continue to ship placeholders. For a downloaded ZIP, edit its adjacent `connection.json` using your own project settings.

## Portable Windows build

Requires .NET 8 SDK:

```powershell
dotnet publish src/GalaxyControl -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/windows
```

Developers distribute **GalaxyControl.exe, connection.json and setup-provider.json together** in their configured package. Technicians open the EXE; the app imports and preserves the public project connection automatically. .NET is bundled; Microsoft Edge WebView2 Evergreen Runtime must be installed. If missing, the app reports the requirement; get it from [Microsoft](https://developer.microsoft.com/microsoft-edge/webview2/). Production distribution should be code-signed.

GitHub Actions produces **Galaxy-Control-P2P-Windows-x64** after the actual Windows WebView2 transport test. Public artifacts are deliberately unconfigured and do not use the author's Supabase. A configured Supabase project and a real two-PC Internet test are required before using a build with customers.

## Tests

- `npm ci && npm run test:database`: real isolated PostgreSQL through PGlite; directory/RPC permissions, confirmed email ownership, IP/port updates, private table denial, instance fencing, cross-user isolation, consent, metadata cursor, expiry and rate limits. Supabase Auth identities are mocked only in the test database.
- `dotnet run --project tests/DirectoryClient -c Release`: HTTP client contract, Auth/signup/refresh flows, publishable-only configuration, lookup, acceptance, metadata and stop against a fake HTTP handler. This does not claim a hosted Supabase deployment test.
- `npx playwright install chromium && npm test`: two separate Chromium processes with actual direct WebRTC, chunked frame bytes, keyboard/mouse, route checks, TURN rejection, stop, cancellation and heartbeat teardown.
- `dotnet run --project tests/WindowsTransport -c Release` on Windows: actual embedded WebView2 bridge, two direct peers, frame bytes, input transport and remote stop.

CI also publishes the native Windows executable. These tests do not change a hosted project and do not replace a real two-PC test through different routers, nor an audit of the operator's deployed Supabase project.

## Scope

Windows 10/11 x64; primary monitor; JPEG frames up to 1600 pixels on the longest side at about 6 FPS; mouse, left/right buttons, wheel and keyboard. One acknowledged frame is allowed in flight, chunked and bounded at 2 MB. No audio, clipboard, file transfer, multi-monitor picker, unattended access or TURN fallback.

The app uses standard user permissions. Elevated windows, UAC secure desktop, Ctrl+Alt+Delete and the lock screen are not supported. Matching keyboard layouts are recommended. The native app captures/applies input; a local non-navigable WebView2 runs maintained browser WebRTC code without camera/microphone or host-object permissions.

## Reference

Reviewed [miroslavpejic85/p2p](https://github.com/miroslavpejic85/p2p). Galaxy Control is a fresh .NET 8/browser-WebRTC implementation inspired by its screen/input concept; it does not copy the reference's UDT networking, cryptography or binaries.

## License

Galaxy Control's original code is [MIT-licensed](LICENSE). Microsoft WebView2 is redistributed under its own [license](licenses/WebView2-LICENSE.txt) and [notices](licenses/WebView2-NOTICE.txt), included in Windows artifacts. Test dependencies retain their respective package licenses.
