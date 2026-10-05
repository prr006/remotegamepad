# Linux server — implementation notes

> **Setup, build, test, run, troubleshooting and uninstall instructions now live
> in [`docs/Linux.md`](../../docs/Linux.md).** They are portable across
> Fedora/RHEL, Debian/Ubuntu, Arch and other systemd distributions and are
> driven by `scripts/setup-linux.sh`, `scripts/build-server-linux.sh`,
> `scripts/test-linux.sh`, `scripts/run-server-linux.sh`,
> `scripts/check-linux.sh` and `scripts/uninstall-linux.sh`.
>
> This file documents how the server itself works on Linux.

The Linux server is a .NET 10 console process that writes an actual gamepad
through kernel uinput. It supports simultaneous Wi-Fi/UDP and Bluetooth Classic
RFCOMM/SPP input; whichever transport sends most recently updates the shared
virtual controller.

Wi-Fi discovery listens on **0.0.0.0:26761**, accepts `REMOTE_GAMEPAD_DISCOVER`,
and responds `REMOTE_GAMEPAD_SERVER|RemoteGamepad Linux|26760`. Wi-Fi input
listens on **0.0.0.0:26760** and accepts raw UTF-8 JSON. The parser also accepts
`INPUT|{json}` for Bluetooth framing compatibility.

> Compatibility note: the Android Wi-Fi implementation files are not present in
> this repository snapshot. UDP framing, command text, response text, and ports
> above follow the Android protocol details provided for this change, but
> end-to-end Android compatibility has not been exercised here.

## Verification status for this checkout

The Android protocol text/fields were checked against the upstream `main`
(read-only); no Android source was changed. The implementation has **not** been
built or run in the Arena environment used for the Linux portability work: it
has no `dotnet`, `/dev/uinput`, Bluetooth adapter, `bluetoothctl` or running
BlueZ. Build, uinput, network, RFCOMM and physical Android interoperability
therefore remain unverified there. The Linux shell scripts themselves are
exercised by `scripts/tests/linux-scripts-test.sh`.

## Diagnostics built into the server

```sh
dotnet run -c Release -- --uinput-self-test   # parser + full mapping exercise
dotnet run -c Release -- --trace-input        # rate-limited state-to-code trace
```

Both require a working `/dev/uinput`; neither runs in the default mode. Use
`./scripts/test-linux.sh` to run the self-test with its prerequisites checked
first.

## Controller mapping

The virtual input identity is BUS `0x03`, VID `0x045e`, PID `0x028e`, version
`0x0114`, name `Xbox 360 Controller`. Face buttons follow the tested Xbox 360
`xpad` mapping: A/B/X/Y emit `BTN_SOUTH`/`BTN_EAST`/`BTN_NORTH`/`BTN_WEST`.
LB/RB, Select/Start, Guide, and stick clicks use the Xbox button-code order
`BTN_TL`, `BTN_TR`, `BTN_SELECT`, `BTN_START`, `BTN_MODE`, `BTN_THUMBL`,
`BTN_THUMBR`; Guide is advertised but Android currently sends no Guide state.
Sticks use `ABS_X/Y` and `ABS_RX/RY`; X is unchanged, and the Android
positive-up Y values are negated when sent to Linux so negative Linux Y means
up. LT/RT use `ABS_Z`/`ABS_RZ` with range 0–255. D-pad is represented only by
`ABS_HAT0X/Y` (-1..1); up/left are negative; simultaneous opposites resolve to
neutral on that axis.

## Bluetooth Classic / SPP

Adapter discovery/power/discoverability use InTheHand 32feet.NET 4.2.5
(`BluetoothRadio.Default`); RFCOMM uses Linux Bluetooth sockets and native
BlueZ/libbluetooth SDP APIs (runtime library `libbluetooth.so.3`, provided by
the distribution's BlueZ package — `bluez-libs` on Fedora/Arch, `libbluetooth3`
on Debian/Ubuntu). The server binds RFCOMM channel 1 and registers the standard
SPP UUID `00001101-0000-1000-8000-00805F9B34FB`, with L2CAP and RFCOMM protocol
descriptors, Public Browse Group membership, and the service name
`RemoteGamepad`. Registration is removed and its SDP session closed when the
listener stops. The Bluetooth listener handles the existing 4-byte big-endian
length-prefixed UTF-8 protocol (`HELLO`/`HELLO_ACK`, `PING`/`PONG`,
`DISCONNECT`, and `INPUT|{json}`). UDP and RFCOMM share the same uinput device;
each complete state update replaces the previous state (last update wins).

### Why the local SDP socket needs system configuration

`sdp_connect(BDADDR_ANY, BDADDR_LOCAL)` talks to BlueZ's compatibility socket
`/run/sdp`, which only exists when `bluetoothd` runs with `--compat`, and which
BlueZ recreates as `root:root 0660` on every start. The server process stays
unprivileged and never changes socket permissions or invokes `sudo`; instead
`scripts/setup-linux.sh` installs, once:

* a systemd **drop-in** for `bluetooth.service` that appends `--compat` to the
  vendor `ExecStart` (the vendor unit file is never modified, and the
  `bluetoothd` path is detected, not assumed), and
* a systemd **path unit** (`remote-gamepad-sdp-permissions.path`) plus a small
  oneshot helper that sets `/run/sdp` to `root:remote-gamepad 0660` whenever the
  socket is created, recreated or re-chmod'ed.

A dedicated `remote-gamepad` group is used — no distribution-specific
`bluetooth` group is assumed to exist (Fedora and Arch do not create one). The
watcher is event driven, nothing polls, a missing socket is a safe no-op, and
the configuration survives `systemctl restart bluetooth` and reboots. See
[`docs/Linux.md`](../../docs/Linux.md) for the commands and verification steps.

Migrating to BlueZ `ProfileManager1` would remove the need for compatibility
mode, but it would also replace the proven RFCOMM socket acceptor with a
D-Bus-owned profile/FD lifecycle, which is out of scope here.
