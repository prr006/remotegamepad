# Linux / Ubuntu server (Wi-Fi)

The Linux server is a .NET 10 console process and writes an actual gamepad through kernel uinput. It listens for UTF-8 `INPUT|{json}` datagrams on UDP **26761**; discovery listens on UDP **26760**, accepts `DISCOVER`, and returns `REMOTEGAMEPAD|26761`.

> Compatibility note: the repository snapshot used for this port did not contain `UdpInputServer`, `UdpDiscoveryListener`, or any Android UDP transport/discovery implementation. The Android app in this checkout uses Bluetooth RFCOMM. Consequently these UDP endpoints are a new, simple protocol and cannot be asserted compatible with that Android app without Android-side support. Existing UDP ports/protocol were not available to preserve.

## Install and configure

```sh
sudo apt update
sudo apt install -y dotnet-sdk-10.0 linux-tools-common evtest
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
