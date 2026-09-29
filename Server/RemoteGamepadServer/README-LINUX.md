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

The virtual input identity is BUS `0x03`, VID `0x045e`, PID `0x028e`, version `0x0114`, name `Xbox 360 Controller`. The implementation follows the [Linux kernel gamepad specification](https://docs.kernel.org/input/gamepad.html) geometry, not the historical symbolic aliases: Android `X` is the physical left face button and emits `BTN_WEST` (kernel alias `BTN_Y`); Android `Y` is the top face button and emits `BTN_NORTH` (kernel alias `BTN_X`). A/B map to `BTN_SOUTH`/`BTN_EAST`. LB/RB, Start/Select, and stick clicks map to `BTN_TL`, `BTN_TR`, `BTN_START`, `BTN_SELECT`, `BTN_THUMBL`, and `BTN_THUMBR`. Left/right sticks use `ABS_X/Y` and `ABS_RX/RY` with negative X left and negative Y up. LT/RT use common Xbox evdev analog axes `ABS_Z`/`ABS_RZ` with range 0–255. D-pad is represented only by `ABS_HAT0X/Y` (-1..1); up/left are negative; simultaneous opposites resolve to neutral on that axis.

## Bluetooth Classic / SPP

Adapter discovery/power/discoverability use InTheHand 32feet.NET 4.2.5 (`BluetoothRadio.Default`); RFCOMM uses Linux Bluetooth sockets and `sdptool`. It binds channel 1 and registers the standard SPP UUID `00001101-0000-1000-8000-00805F9B34FB` automatically, so no per-run channel setup is needed. Important limitation: BlueZ's built-in `sdptool add SP` record is named **Serial Port**, not `RemoteGamepad`; the Android client in this checkout selects/connects by SPP UUID, so this record name does not prevent its connection, but the requested custom SDP service-name attribute is not currently provided. Do not interpret the startup log as proof that Android discovery/connect has been hardware-tested. The Bluetooth listener handles the existing 4-byte big-endian length-prefixed UTF-8 protocol (`HELLO`/`HELLO_ACK`, `PING`/`PONG`, `DISCONNECT`, and `INPUT|{json}`). UDP and RFCOMM share the same uinput device; each complete state update replaces the previous state (last update wins).

Check the adapter and local SDP records with:

```sh
bluetoothctl show
sdptool browse local
```

The server's startup log reports RFCOMM service registration and waiting status. `btmon` (from BlueZ tools) can inspect live Bluetooth traffic. If no adapter/service appears, check `systemctl status bluetooth`, `journalctl -u bluetooth`, `bluetoothctl show`, and that the server runs in a session allowed to access the system D-Bus; no raw RFCOMM channel configuration is required. Stop with Ctrl+C to stop the listener and close the client. Build/runtime setup commands above are documented for Ubuntu but have not been tested in this environment; physical-adapter/Android Bluetooth discovery and RFCOMM interoperability also remain unverified here.
