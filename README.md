# Syncly

Serverless note sync for **Android**, **Linux**, and **Windows**.

Discovery finds nearby Syncly apps. Cryptographic pairing authenticates devices. The sync engine is transport-independent (LAN today, Wi-Fi Direct next).

```
Discovery → Connect → Exchange keys → Authenticate → Encrypted sync
```

## Stack

| Layer | Tech |
|-------|------|
| UI | Shared Blazor (`Syncly.UI`) |
| Linux host | Photino.Blazor |
| Android / Windows host | .NET MAUI Blazor Hybrid (`Syncly.App.Maui`) |
| Core | C# / .NET 10 — identity, SQLite, crypto, sync protocol |
| Transport v1 | LAN UDP discovery + TCP framed messages |
| Transport later | Wi-Fi Direct stubs per platform |

## Solution layout

```
src/
  Syncly.Contracts/           # Peer models, IPeerDiscovery, ISyncTransport, ISyncEngine
  Syncly.Core/                # Identity, AES-GCM sessions, SQLite, sync engine
  Syncly.UI/                  # Devices, Notes, Sync, Pairing, Settings
  Syncly.Platform.Lan/        # Working LAN discovery + TCP
  Syncly.Platform.Android/    # WifiP2pManager stub
  Syncly.Platform.Windows/    # Wi-Fi Direct stub
  Syncly.Platform.Linux/      # NetworkManager P2P stub
  Syncly.App.Linux/           # Photino desktop app (primary on Linux)
  Syncly.App.Maui/            # MAUI Blazor Hybrid (Android + Windows)
tests/
  Syncly.Core.Tests/
```

## Prerequisites

- .NET 10 SDK
- Linux GUI deps for Photino: WebKitGTK (e.g. `webkit2gtk-4.1` / distro equivalent)
- For MAUI: `dotnet workload install maui` (Android SDK / Windows tooling as needed)

## Run (Linux Photino)

```bash
dotnet build Syncly.slnx
dotnet run --project src/Syncly.App.Linux
```

Two instances on one machine (separate data dirs + ports):

```bash
SYNCLY_DATA_DIR=/tmp/syncly-a SYNCLY_PORT=45678 dotnet run --project src/Syncly.App.Linux
SYNCLY_DATA_DIR=/tmp/syncly-b SYNCLY_PORT=45688 dotnet run --project src/Syncly.App.Linux -- --port 45688
```

1. Make both **Discoverable**
2. Open **Pairing** on both — confirm fingerprints on first sync
3. Create a note on one device, tap **Sync** on the other

## MAUI (Android / Windows)

The MAUI project lives at [`src/Syncly.App.Maui`](src/Syncly.App.Maui) but is **not** in the default solution (so Linux CI/builds work without the MAUI workload).

```bash
dotnet workload install maui
dotnet sln Syncly.slnx add src/Syncly.App.Maui/Syncly.App.Maui.csproj
dotnet build src/Syncly.App.Maui/Syncly.App.Maui.csproj -f net10.0-android
# or: -f net10.0-windows10.0.19041.0
```

`MauiProgram` registers the same Core + LAN services as the Photino host. Android permissions for nearby Wi-Fi are declared for a future WifiP2p implementation.

## Tests

```bash
dotnet test tests/Syncly.Core.Tests
```

Covers identity persistence, encrypted handshake, and note sync over an in-memory transport.

## Architecture

```
Blazor UI
    │
Syncly.Core (identity · trust · SQLite · sync protocol)
    │
ISyncTransport / IPeerDiscovery
    ├── LanTcpTransport + LanPeerDiscovery   ← v1
    ├── AndroidWifiDirect*                   ← stub
    ├── WindowsWifiDirect*                   ← stub
    └── LinuxWifiDirect*                     ← stub
```

### Protocol (v1)

1. `Hello` — device id, name, ECDSA P-256 public key  
2. Trust check / interactive pairing (fingerprint confirm)  
3. ECDH ephemeral key offers (signed) → HKDF → AES-GCM session  
4. `Manifest` → `Need` → `Object` → `Done` (last-write-wins)

Discovery alone never grants trust.

### Service advertisement

UDP broadcast JSON on port `45679`:

`Service=Syncly`, device id, display name, TCP port, protocol version.

## Wi-Fi Direct next steps

| Platform | Implementation hook |
|----------|---------------------|
| Android | [`AndroidWifiDirectDiscovery`](src/Syncly.Platform.Android/AndroidWifiDirectDiscovery.cs) → `WifiP2pManager` DNS-SD |
| Windows | [`WindowsWifiDirectDiscovery`](src/Syncly.Platform.Windows/WindowsWifiDirectDiscovery.cs) → current WinRT Wi-Fi Direct APIs |
| Linux | [`LinuxWifiDirectDiscovery`](src/Syncly.Platform.Linux/LinuxWifiDirectDiscovery.cs) → NetworkManager D-Bus |

After a P2P link is up, reuse the same framed TCP + Core sync path.

## Out of scope (this base)

- Real Wi-Fi Direct connect
- CRDTs
- Internet P2P / QUIC
- File attachments / chunked blobs beyond note text
