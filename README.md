# Galaxy Control

A minimal Windows remote support app with a light interface and Galaxy yellow-to-gold accents.

## First implementation

- Customer: open the portable app, enter the technician's email, click **Connect to technician**. This explicitly grants screen, mouse and keyboard access for that session.
- Technician: choose **I'm a technician**, enter their email and access key, go online and accept the request.
- Customer can stop sharing at any time. Closing either app or losing the connection ends the session. No background service, startup entry or automatic reconnection.
- Primary monitor JPEG streaming (about 6 FPS), mouse, left/right buttons, wheel, keyboard and release of held keys when disconnected.

## Run the relay

Requires .NET 8 SDK. Generate a unique random access key per technician (at least 32 random bytes), deliver it privately, and configure only its SHA-256 hex hash on the relay:

```bash
export GALAXY_TECHNICIANS='{"tech@example.com":"SHA256_HEX_HASH_OF_ACCESS_KEY"}'
dotnet run --project src/GalaxyControl.Relay --urls http://127.0.0.1:8080
```

Place the relay behind an HTTPS reverse proxy with WebSocket support. Set its public `wss://.../connect` endpoint in `src/GalaxyControl/connection.json` **before publishing**. Do not expose the HTTP listener publicly. Configure proxy timeouts for long-lived WebSockets and connection limits. `/health` returns readiness. Unknown technician addresses are rejected. Only one technician connection and one customer session per email are supported. Session lifetime is capped at one hour; technician connection lifetime at two hours. The relay keeps no screen recordings or session content on disk.

## Build a portable Windows app

On Windows with .NET 8 SDK:

```powershell
dotnet publish src/GalaxyControl -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/windows
```

Distribute **GalaxyControl.exe and connection.json together**. No customer account or installation is required. The unsigned build may trigger Windows reputation checks; production distribution should use a trusted code-signing certificate. Do not disable Windows protections.

GitHub Actions builds a Windows ZIP artifact and validates the relay on pushes and pull requests. It does not deploy the relay or configure a production endpoint.

## Architecture and current limits

This implementation uses a central authenticated WebSocket relay rather than the reference project's IP exchange and UDT traversal. It works across NAT using outbound HTTPS, with bandwidth passing through the relay. TLS protects each connection; this is **not end-to-end encryption** and the relay can access frames and input. Use only a relay you operate and trust.

Windows 10/11 x64 only. Standard user permissions; elevated windows, UAC secure desktop, Ctrl+Alt+Delete and the lock screen are not supported. Primary screen only. No audio, clipboard, file transfer or unattended access. High-resolution screens may be slow; JPEG frames over 2 MB are skipped. Matching keyboard layouts are recommended. This is an initial implementation, not a production-audited remote desktop service.

A deployed relay, configured technician and real two-PC acceptance test are required before claiming it is ready for customers. Test stop/close/network-loss behavior, keyboard modifiers, letterboxing coordinates and elevated-window restrictions on real Windows machines.

## Reference

Reviewed [miroslavpejic85/p2p](https://github.com/miroslavpejic85/p2p), an MIT-licensed Windows remote desktop example. The capture/input concept informed this design; this repository uses a fresh .NET 8 implementation and does not copy its networking, cryptography or bundled binaries.
