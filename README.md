# Galaxy Control

Attended, direct peer-to-peer remote support for Windows, with a light interface and Galaxy yellow-to-gold branding.

## Customer and technician flow

1. The customer opens the portable app, enters the technician's email and clicks **Connect to technician**. This grants access for that session; there is no customer account, IP exchange or connection code.
2. The technician chooses **I'm a technician**, signs in with their email and private access key, and accepts the request.
3. The apps establish a direct, encrypted WebRTC connection. Only then does screen capture or input begin. The UI displays **Connected directly · encrypted P2P · no relay**.
4. **Stop sharing**, closing either app, transport failure or signaling loss ends control. Held keys and mouse buttons are released. No startup service, hidden access or automatic reconnection is installed.

## Direct P2P architecture

| Component | Purpose | Screen or input payload? |
| --- | --- | --- |
| HTTPS/WebSocket signaling service | Authenticates the technician, resolves the email, exchanges WebRTC SDP and ICE connection data | No; binary, frame and input messages are rejected |
| STUN discovery | Discovers public network endpoints for NAT traversal | No |
| WebRTC peer connection | DTLS-encrypted SCTP data channels directly between the two PCs | Yes: screen and control channels |
| TURN/media relay | Not configured or supported | Never |

The native app uses Microsoft Edge WebView2's WebRTC implementation. The embedded transport page cannot navigate or open windows, has no host objects and is denied camera/microphone permissions. Windows captures the primary screen and applies input; the transport only receives data that the native app explicitly supplies. No remote HTML or JavaScript is loaded.

Both the client and signaling service reject TURN configuration and relay ICE candidates. The app checks the selected local and remote candidate types before enabling capture or control. If the network requires a relay (for example, some symmetric NATs or restrictive corporate firewalls), connection fails with a clear message. There is **no fallback through the signaling server**.

The signaling service is still needed for the email-based discovery flow. It sees technician identity, addresses and connection metadata, but screen/input payload is encrypted between the peers. Operate a trusted HTTPS signaling service: it binds the technician's identity to the peer's exchanged DTLS fingerprint. The implementation does not provide an independent, out-of-band identity verification channel.

## Deploy signaling with automatic HTTPS

Requires a Linux server with Docker Compose, a hostname pointing to it, and inbound TCP ports 80 and 443. From the repository root:

```bash
python3 scripts/configure.py support.example.com tech@example.com
docker compose --env-file deployment/.env -f deployment/compose.yaml up -d --build
```

Save the generated technician access key privately. Only its SHA-256 hash is written to the ignored `deployment/.env`. The script writes the public `SignalingUrl` into the app's `connection.json`; publish the app **after** setting that address. Caddy obtains and renews HTTPS certificates, and the signaling listener stays inside the Docker network.

`StunUrls` defaults to `stun:stun.l.google.com:19302`. This external discovery service sees the client's public network endpoint, not desktop or keyboard data. You can replace it with your own STUN server, or use an empty array for local-network-only discovery. TURN URLs are refused. Outbound UDP must be allowed on the client networks for Internet P2P.

For development without Docker:

```bash
export GALAXY_TECHNICIANS='{"tech@example.com":"SHA256_HEX_HASH_OF_ACCESS_KEY"}'
dotnet run --project src/GalaxyControl.Signaling --urls http://127.0.0.1:8080
```

Use a trusted HTTPS reverse proxy before exposing signaling to clients. The app requires a `wss://` endpoint. Configure proxy WebSocket support, long-lived connection timeouts and appropriate connection limits. `/health` reports `mediaRelay: false`. One technician connection and one customer session per email are supported. Customer session lifetime is capped at one hour. The service keeps no recordings or session content on disk.

## Build and download

Requires .NET 8 SDK. On Windows:

```powershell
dotnet publish src/GalaxyControl -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/windows
```

Distribute **GalaxyControl.exe and connection.json together**. The .NET runtime is bundled. Microsoft Edge WebView2 Evergreen Runtime must already be installed; if it is missing, the app reports that requirement. Obtain it from [Microsoft's official download page](https://developer.microsoft.com/microsoft-edge/webview2/). The app does not silently install software or disable Windows protections. Production distribution should be code-signed.

GitHub Actions publishes the **Galaxy-Control-P2P-Windows-x64** artifact only after its actual Windows WebView2 transport test passes. The build contains a placeholder endpoint until you configure and republish it. An artifact does not deploy a signaling service or make an unconfigured app Internet-ready.

## Verification

The CI pipeline runs:

- Signaling authentication, offline/busy handling, acceptance gate, SDP/ICE exchange, field stripping and teardown; binary, screen, keyboard and relay-candidate rejection.
- Two separate Chromium processes with real WebRTC: verified direct routes, encrypted data-channel connection, byte-exact chunked frames, mouse/keyboard messages, stop, cancellation, TURN rejection and heartbeat teardown.
- The actual embedded WebView2 bridge on Windows: two peers, direct selected routes, frame chunk reconstruction, input transport and remote stop.
- A self-contained Windows x64 publish.

Local checks are also possible with `python3 tests/signaling.py` after building the service, `npm ci && npx playwright install chromium && npm test`, and `dotnet run --project tests/WindowsTransport -c Release` on Windows. Node tests do not grant desktop access or inject real keys.

A real two-PC acceptance test on different Internet connections is still required to assess NAT compatibility, Windows screen capture and input injection, monitor scaling and stop/network-loss behavior on users' hardware. CI loopback tests do not prove connectivity through every router.

## Current scope and limits

Windows 10/11 x64, primary screen only, JPEG frames up to 1600 pixels on the longest side at about 6 FPS, mouse, left/right buttons, wheel and keyboard. Frames are chunked and bounded at 2 MB; only one frame can be in flight, with receiver acknowledgment and backpressure. The control channel has an eight-second peer heartbeat timeout, in addition to immediate close/failure handling.

The app runs with standard user permissions. Elevated windows, UAC secure desktop, Ctrl+Alt+Delete and the lock screen are not supported. There is no audio, clipboard, file transfer, multi-monitor selection or unattended access. Matching keyboard layouts are recommended. This is an initial implementation, not a production security audit or a high-frame-rate video codec.

## Reference

Reviewed [miroslavpejic85/p2p](https://github.com/miroslavpejic85/p2p), an MIT-licensed Windows remote desktop example. The capture/input concept informed this design. Galaxy Control uses a fresh .NET 8 implementation and browser WebRTC rather than the reference's UDT networking, cryptography or bundled binaries.
