# RemoteGamepad — Milestone 1

**Goal:** Establish reliable Android → Windows Bluetooth Classic communication.

## Architecture

```
Android Phone (Kotlin)
    ↓ Bluetooth Classic (RFCOMM / SPP)
Windows PC (C# .NET 8)
    ↓
Console Server
```

## Project Structure

```
RemoteGamepad/
├── Android/                    # Kotlin Android app
│   └── app/
│       ├── build.gradle
│       └── src/main/
│           ├── AndroidManifest.xml
│           ├── java/com/remotegamepad/
│           │   ├── bluetooth/
│           │   │   ├── Protocol.kt          # Framing + message constants
│           │   │   └── BluetoothService.kt  # Isolated transport layer
│           │   └── ui/
│           │       └── MainActivity.kt      # UI, discovery, permissions
│           └── res/
│               ├── layout/
│               │   ├── activity_main.xml
│               │   └── item_device.xml
│               ├── values/
│               │   ├── strings.xml
│               │   ├── colors.xml
│               │   └── themes.xml
│               └── drawable/
├── Server/                     # C# Windows console server
│   └── RemoteGamepadServer/
│       ├── RemoteGamepadServer.csproj
│       ├── Protocol.cs         # Framing (identical to Android)
│       ├── BluetoothServer.cs  # Isolated transport layer
│       └── Program.cs          # Entry point + CLI
├── scripts/
│   ├── build-android.sh
│   ├── build-server.sh            # Windows build (publish win-x64)
│   ├── run-server.sh
│   ├── setup-linux.sh             # one-time Linux system setup
│   ├── build-server-linux.sh      # Linux: restore + Release build
│   ├── run-server-linux.sh        # Linux: run unprivileged
│   ├── check-linux.sh             # Linux: environment report
│   ├── test-linux.sh              # Linux: uinput self-test
│   ├── uninstall-linux.sh         # Linux: remove RemoteGamepad config
│   ├── lib/remote-gamepad-linux.sh
│   └── tests/linux-scripts-test.sh
├── systemd/                       # /run/sdp permission watcher units
├── docs/
│   └── Linux.md                   # Linux setup, build, test, run, troubleshooting
└── README.md
```

## Protocol / Message Framing

Both sides use **exactly** the same framing to prevent message merging on the Bluetooth stream:

```
[4 bytes: payload length, big-endian][N bytes: UTF-8 payload]
```

Messages (plain UTF-8 strings):

| Message      | Direction            | Meaning                          |
|--------------|----------------------|----------------------------------|
| `HELLO`      | Android → Windows    | Handshake initiate               |
| `HELLO_ACK`  | Windows → Android    | Handshake acknowledge            |
| `PING`       | Android → Windows    | Keepalive / latency test         |
| `PONG`       | Windows → Android    | Keepalive response               |
| `DISCONNECT` | Android → Windows    | Graceful disconnect request      |

**SPP UUID:** `00001101-0000-1000-8000-00805F9B34FB` (standard Bluetooth SPP)

## Android App

### Features
- Runtime Bluetooth permissions (Android 12+ BLUETOOTH_SCAN / BLUETOOTH_CONNECT)
- Display paired devices
- Discovery of nearby Bluetooth devices
- Tap-to-connect
- Connection status indicator
- Send HELLO / PING buttons
- Event log with timestamps
- Graceful disconnect + resource cleanup

### Build
```bash
cd Android
./gradlew assembleDebug
```

Install the resulting APK on your Android phone:
```bash
adb install app/build/outputs/apk/debug/app-debug.apk
```

## Linux Server

The Linux server is the same .NET project. It creates a real gamepad through
kernel **uinput** and accepts input over **Wi-Fi/UDP** (ports 26760/26761) and,
optionally, **Bluetooth Classic RFCOMM/SPP**. It always runs as your normal
user — never with `sudo`.

```bash
sudo ./scripts/setup-linux.sh     # one-time setup (groups, udev, uinput, BlueZ)
# log out and back in, then:
./scripts/check-linux.sh          # environment report
./scripts/build-server-linux.sh   # restore + Release build
./scripts/test-linux.sh           # uinput self-test (no Bluetooth required)
./scripts/run-server-linux.sh     # run the server
sudo ./scripts/uninstall-linux.sh # remove only RemoteGamepad's configuration
```

Supported distribution families: **Fedora/RHEL (`dnf`), Debian/Ubuntu (`apt`),
Arch (`pacman`), openSUSE (`zypper`)** and any other systemd distribution
(configuration is applied; packages are reported for manual installation).
Machines without Bluetooth hardware are fully supported in Wi-Fi-only mode.

📖 **Full guide: [docs/Linux.md](docs/Linux.md)** — setup, build, test, run,
Wi-Fi-only mode, Bluetooth requirements, troubleshooting, uninstall and
supported distributions.

## Windows Server

### Dependencies
- **.NET 8 SDK**
- **32feet.NET** (`InTheHand.Net.Bluetooth` v4.0.36) — NuGet package
  - This is the de-facto standard Bluetooth library for .NET on Windows.
  - It wraps the native Microsoft Bluetooth APIs and provides a clean sockets-like interface over RFCOMM.

### Build
```bash
cd Server/RemoteGamepadServer
dotnet build
dotnet publish -c Release -r win-x64 --self-contained false
```

### Run
```bash
cd Server/RemoteGamepadServer
dotnet run
```

The server will:
1. Print your Bluetooth radio info
2. Start listening on the SPP UUID
3. Auto-respond to `HELLO` with `HELLO_ACK`
4. Auto-respond to `PING` with `PONG`
5. Accept console commands (`q` = quit, `d` = disconnect)

## Testing Steps

1. **Enable Bluetooth** on both your Android phone and Windows PC.
2. **Pair the devices** in Windows Settings → Bluetooth → Add device.
   - The Android phone should appear as a paired device on Windows.
   - The Windows PC should appear as a paired device on Android.
3. **Run the Windows server:**
   ```bash
   cd Server/RemoteGamepadServer
   dotnet run
   ```
   You should see:
   ```
   [SERVER] Listening on <your-mac-address>
   [SERVER] Waiting for Android connection...
   ```
4. **Open the Android app** on your phone.
5. **Grant permissions** when prompted (Bluetooth + Location).
6. **Tap "Scan for Devices"** or select your Windows PC from the **Paired Devices** list.
7. **Tap your Windows PC** in the list to connect.
8. **Verify connection:**
   - Android status turns green → "Connected"
   - Windows console shows: `[SERVER] Client connected: <android-mac>`
9. **Tap "Send HELLO"** on Android:
   - Android log: `→ Sent: HELLO`
   - Windows log: `[RECV] HELLO` then `[SEND] HELLO_ACK`
   - Android log: `← Received: HELLO_ACK`
10. **Tap "Send PING"** on Android:
    - Android log: `→ Sent: PING`
    - Windows log: `[RECV] PING` then `[SEND] PONG`
    - Android log: `← Received: PONG`
11. **Tap "Disconnect"** on Android or type `d` + Enter on Windows.
12. **Verify clean disconnect** on both sides.

## Known Limitations

- **Single client:** The Windows server accepts one Android connection at a time. A new connection will disconnect the previous one.
- **No reconnection retry:** The Android app does not auto-retry on connection failure. Tap the device again to reconnect.
- **No Wi-Fi / UDP / TCP:** Intentionally excluded per Milestone 1 scope.
- **No controller UI / input:** Intentionally excluded per Milestone 1 scope.
- **Windows-only server:** The server uses 32feet.NET which targets Windows Bluetooth APIs.

## Next Milestones

- Milestone 2: Controller input protocol + virtual controller layer
- Milestone 3: Controller UI on Android
- Milestone 4: Integration testing + latency optimisation
