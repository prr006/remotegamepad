# Linux / Ubuntu server (Wi-Fi)

The Linux server is a .NET 10 console process and writes an actual gamepad through kernel uinput. It supports simultaneous Wi-Fi/UDP and Bluetooth Classic RFCOMM/SPP input; whichever transport sends most recently updates the shared virtual controller.

Wi-Fi discovery listens on **0.0.0.0:26761**, accepts `REMOTE_GAMEPAD_DISCOVER`, and responds `REMOTE_GAMEPAD_SERVER|RemoteGamepad Linux|26760`. Wi-Fi input listens on **0.0.0.0:26760** and accepts raw UTF-8 JSON. The parser also accepts `INPUT|{json}` for Bluetooth framing compatibility.

> Compatibility note: the Android Wi-Fi implementation files are not present in this repository snapshot. UDP framing, command text, response text, and ports above follow the Android protocol details provided for this change, but end-to-end Android compatibility has not been exercised here.

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

Ensure `uinput` is loaded at boot by adding it to `/etc/modules-load.d/uinput.conf`:

```text
uinput
```

## Build, run, verify

```sh
cd Server/RemoteGamepadServer
dotnet build -c Release
dotnet run -c Release
```

In a second terminal verify the device and events:

```sh
grep -A8 -B2 RemoteGamepad /proc/bus/input/devices
sudo evtest
```

Send an example button/axis packet from another host (substitute the server's Wi-Fi IP):

```sh
printf 'INPUT|{"lx":0.6,"ly":-0.2,"a":true}' | nc -u -w1 SERVER_IP 26761
```

Open UDP 26760 and 26761 in the host firewall on the trusted local network. `Ctrl+C` resets controls and destroys the uinput device. If startup reports permission denied, check `ls -l /dev/uinput`, `id`, the loaded module (`lsmod | grep uinput`), and log out/in after changing groups. Do not run the server as root as a routine workaround.

## Bluetooth Classic / SPP

The server uses the Linux provider in InTheHand 32feet.NET (`InTheHand.Net.Bluetooth` 4.2.5), backed by BlueZ; it advertises SPP UUID `00001101-0000-1000-8000-00805F9B34FB` and service name `RemoteGamepad`. Install/enable BlueZ using the commands above, make the adapter powered and discoverable (`bluetoothctl` → `power on`, `discoverable on`), then run the server. It reports adapter/listener errors but keeps UDP operating if Bluetooth cannot start. The Bluetooth listener handles the existing 4-byte big-endian length-prefixed UTF-8 protocol (`HELLO`/`HELLO_ACK`, `PING`/`PONG`, `DISCONNECT`, and `INPUT|{json}`). UDP and RFCOMM share the same uinput device; updates are last-input-wins.

Check the adapter and local SDP records with:

```sh
bluetoothctl show
sdptool browse local
```

The server's startup log reports RFCOMM service registration and waiting status. `btmon` (from BlueZ tools) can inspect live Bluetooth traffic. Stop with Ctrl+C to stop the listener and close the client. Build/runtime setup commands above are documented for Ubuntu but have not been tested in this environment; physical-adapter/Android Bluetooth discovery and RFCOMM interoperability also remain unverified here.
