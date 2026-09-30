# Linux / Ubuntu server (Wi-Fi + Bluetooth)

The Linux server is a .NET 10 console process and writes an actual gamepad through kernel uinput. It supports simultaneous Wi-Fi/UDP and Bluetooth Classic RFCOMM/SPP input; whichever transport sends most recently updates the shared virtual controller.

Wi-Fi discovery listens on **0.0.0.0:26761**, accepts `REMOTE_GAMEPAD_DISCOVER`, and responds `REMOTE_GAMEPAD_SERVER|RemoteGamepad Linux|26760`. Wi-Fi input listens on **0.0.0.0:26760** and accepts raw UTF-8 JSON. The parser also accepts `INPUT|{json}` for Bluetooth framing compatibility.

> Compatibility note: the Android Wi-Fi implementation files are not present in this repository snapshot. UDP framing, command text, response text, and ports above follow the Android protocol details provided for this change, but end-to-end Android compatibility has not been exercised here.

## Verification status for this checkout

The Android protocol text/fields below were checked against `rahulp06/RemoteGamepad` `main` (read-only); no Android source was changed. The implementation and setup commands have **not** been built or run in this Arena environment: it has no `dotnet`, `/dev/uinput`, Bluetooth adapter, or `bluetoothctl`. Therefore build, uinput, network, RFCOMM, and physical Android interoperability are explicitly unverified here.

## Install and configure

Install .NET SDK 10 for Ubuntu 26.04 using the Ubuntu package source or the Microsoft .NET Ubuntu instructions for that release, then verify `dotnet --version` reports `10.x`. The install commands below are guidance and have not been verified in this checkout (the development environment has no .NET SDK).

```sh
sudo apt update
sudo apt install -y dotnet-sdk-10.0 evtest bluez libbluetooth-dev
sudo systemctl enable --now bluetooth
sudo modprobe uinput
sudo groupadd -f uinput
sudo usermod -aG input,uinput "$USER"
```

Create `/etc/udev/rules.d/99-uinput.rules`:

```udev
KERNEL=="uinput", MODE="0660", GROUP="uinput", OPTIONS+="static_node=uinput"
```

Then apply it and log out/in so group membership refreshes:

```sh
sudo udevadm control --reload-rules
sudo udevadm trigger
sudo modprobe uinput
ls -l /dev/uinput
```

Ensure `uinput` is loaded at boot:

```sh
echo uinput | sudo tee /etc/modules-load.d/uinput.conf
```

## Build, run, verify

```sh
cd Server/RemoteGamepadServer
dotnet build -c Release
dotnet run -c Release
```

In a second terminal verify the device and events:

```sh
grep -A8 -B2 'Xbox 360 Controller' /proc/bus/input/devices
sudo evtest /dev/input/eventN   # replace eventN with the device node shown above
```

Run the backend-only mapping exercise (it first validates a raw 20-field JSON packet through `InputParser`, then emits each digital press/release and stick/trigger extreme with an explicit `[SELF-TEST]` label):

```sh
dotnet run -c Release -- --uinput-self-test
```

For rate-limited state-to-code diagnostics while using transports:

```sh
dotnet run -c Release -- --trace-input
```

Both diagnostics require a working `/dev/uinput`; neither runs in the default mode.

Send an example button/axis packet from another host (substitute the server's Wi-Fi IP):

```sh
printf '{"lx":0.6,"ly":-0.2,"a":true}' | nc -u -w1 SERVER_IP 26760
```

Open UDP 26760 and 26761 in the host firewall on the trusted local network. `Ctrl+C` resets controls and destroys the uinput device. If startup reports permission denied, check `ls -l /dev/uinput`, `id`, the loaded module (`lsmod | grep uinput`), and log out/in after changing groups. Do not run the server as root as a routine workaround.

## Controller mapping

The virtual input identity is BUS `0x03`, VID `0x045e`, PID `0x028e`, version `0x0114`, name `Xbox 360 Controller`. Face buttons follow the tested Xbox 360 `xpad` mapping: A/B/X/Y emit `BTN_SOUTH`/`BTN_EAST`/`BTN_NORTH`/`BTN_WEST`. LB/RB, Select/Start, Guide, and stick clicks use the Xbox button-code order `BTN_TL`, `BTN_TR`, `BTN_SELECT`, `BTN_START`, `BTN_MODE`, `BTN_THUMBL`, `BTN_THUMBR`; Guide is advertised but Android currently sends no Guide state. Sticks use `ABS_X/Y` and `ABS_RX/RY`; X is unchanged, and the Android positive-up Y values are negated when sent to Linux so negative Linux Y means up. LT/RT use `ABS_Z`/`ABS_RZ` with range 0–255. D-pad is represented only by `ABS_HAT0X/Y` (-1..1); up/left are negative; simultaneous opposites resolve to neutral on that axis.

## Bluetooth Classic / SPP

Adapter discovery/power/discoverability use InTheHand 32feet.NET 4.2.5 (`BluetoothRadio.Default`); RFCOMM uses Linux Bluetooth sockets and native BlueZ/libbluetooth SDP APIs (Ubuntu runtime library `libbluetooth.so.3`, provided by the `bluez` package). The server binds RFCOMM channel 1 and registers the standard SPP UUID `00001101-0000-1000-8000-00805F9B34FB`, with L2CAP and RFCOMM protocol descriptors, Public Browse Group membership, and the service name `RemoteGamepad`. Registration is removed and its SDP session closed when the listener stops. The process remains unprivileged; the one-time system setup below installs an event-driven systemd path unit so members of the `bluetooth` group can access BlueZ's `/run/sdp` socket after every daemon start/restart. No external SDP tool, per-launch privileged command, or modification/override of Ubuntu's `bluetooth.service` is used. The Bluetooth listener handles the existing 4-byte big-endian length-prefixed UTF-8 protocol (`HELLO`/`HELLO_ACK`, `PING`/`PONG`, `DISCONNECT`, and `INPUT|{json}`). UDP and RFCOMM share the same uinput device; each complete state update replaces the previous state (last update wins).

### Persistent local SDP socket permissions

BlueZ compatibility-mode SDP registration connects to `/run/sdp`. On Ubuntu this socket is commonly recreated as `root:root` mode `0660`, which prevents an unprivileged user service from registering a record even when its user belongs to `bluetooth`. Do not add `ExecStartPost` to the vendor Bluetooth unit: the socket may not exist when that command runs.

Install the small system-level watcher once from the repository checkout:

```sh
sudo ./scripts/install-linux-sdp-permissions.sh
```

The installer places a oneshot helper and a systemd `.path` unit in `/etc/systemd/system`, and the helper in `/usr/local/libexec`. The path unit is enabled as a `Wants=` dependency of `bluetooth.service` and ordered `Before=bluetooth.service`, so it begins watching before BlueZ starts. It observes `/run/sdp` existence changes and starts the helper only on those events; the helper verifies the path is a socket, then sets `root:bluetooth` and mode `0660`. The watcher is ordered before `bluetooth.service`, while the short helper itself is deliberately not ordered after the daemon: when BlueZ removes the socket during a restart, the helper immediately no-ops so the watcher can re-arm before BlueZ recreates it. The initial installer also runs the same helper once for an already-running daemon. Nothing polls continuously, no vendor unit is replaced/overridden, and a missing socket is a safe no-op. The application itself never changes socket permissions or invokes `sudo`.

The account running the user service must be a member of `bluetooth`; after changing group membership, log out and back in so the user service inherits the new supplementary group. Check the installed state and behavior:

```sh
id -nG
stat -c '%U:%G %a %n' /run/sdp
systemctl status remote-gamepad-sdp-permissions.path
systemctl status remote-gamepad-sdp-permissions.service
sudo systemctl restart bluetooth
stat -c '%U:%G %a %n' /run/sdp
journalctl -u remote-gamepad-sdp-permissions.service -b --no-pager
```

After the restart, the expected socket is `root:bluetooth 660`; Wi-Fi and RFCOMM remain managed by the normal unprivileged RemoteGamepad user service.

To remove the integration and restore `root:root 0660` on the current socket (if present):

```sh
sudo ./scripts/uninstall-linux-sdp-permissions.sh
```

The watcher is appropriate here because native RFCOMM and SDP registration already work; migrating to BlueZ `ProfileManager1` would replace the proven RFCOMM socket acceptor with a D-Bus-owned profile/FD lifecycle and is not needed to persist this narrowly scoped socket access policy.

Check the adapter state with:

```sh
bluetoothctl show
```

The server's startup log reports RFCOMM service registration and waiting status. `btmon` (from BlueZ tools) can inspect live Bluetooth traffic. If no adapter/service appears, check `systemctl status bluetooth`, `journalctl -u bluetooth`, `bluetoothctl show`, and that the server runs in a session allowed to access the system D-Bus; no raw RFCOMM channel configuration is required. Stop with Ctrl+C to stop the listener and close the client. Build/runtime setup commands above are documented for Ubuntu but have not been tested in this environment; physical-adapter/Android Bluetooth discovery and RFCOMM interoperability also remain unverified here.
