# RemoteGamepad on Linux

The Linux server is the existing .NET console application in
`Server/RemoteGamepadServer`. It creates a real gamepad through the kernel's
**uinput** interface and accepts input from the Android app over **Wi-Fi/UDP**
and, optionally, **Bluetooth Classic RFCOMM/SPP**. Whichever transport sends
most recently updates the shared virtual controller.

This document covers the portable setup scripts that make that work on any
systemd-based distribution with as little manual work as possible.

| | |
|---|---|
| Virtual device | `Xbox 360 Controller` (BUS `0x03`, VID `0x045e`, PID `0x028e`) via `/dev/uinput` |
| Wi-Fi input | UDP `0.0.0.0:26760` (raw JSON, or `INPUT\|{json}`) |
| Wi-Fi discovery | UDP `0.0.0.0:26761` — `REMOTE_GAMEPAD_DISCOVER` → `REMOTE_GAMEPAD_SERVER\|RemoteGamepad Linux\|26760` |
| Bluetooth | RFCOMM channel 1, SPP UUID `00001101-0000-1000-8000-00805F9B34FB`, service name `RemoteGamepad` |
| Privileges | the server always runs as your **normal user** — never with `sudo` |

---

## Supported distribution families

The scripts detect the distribution at runtime; nothing is hard-coded to one
distro. Package installation is automated for these families:

| Family | Detected IDs (`/etc/os-release`) | Package manager | BlueZ packages | .NET package |
|---|---|---|---|---|
| Fedora / RHEL | `fedora`, `rhel`, `centos`, `almalinux`, `rocky` | `dnf` (or `yum`) | `bluez`, `bluez-libs`, `bluez-deprecated` (optional `sdptool`) | `dotnet-sdk-10.0` |
| Debian / Ubuntu | `debian`, `ubuntu`, and anything with `ID_LIKE=debian` (Mint, Pop!_OS, …) | `apt-get` | `bluez`, `libbluetooth3` | `dotnet-sdk-10.0` |
| Arch | `arch`, `manjaro`, `endeavouros` | `pacman` | `bluez`, `bluez-libs`, `bluez-utils`, `bluez-deprecated-tools` (optional `sdptool`) | `dotnet-sdk-10.0` |
| openSUSE / SLES | `opensuse*`, `sles`, `suse` | `zypper` | `bluez`, `libbluetooth3` | `dotnet-sdk-10.0` (best effort) |
| Anything else | – | – | detected and reported, installed manually | installed manually |

Other systemd distributions (Void, Gentoo, NixOS, Slackware, …) are still
usable: `setup-linux.sh` performs every configuration step it can and simply
reports the packages you have to install yourself.

Requirements on any distribution:

* Linux kernel with `CONFIG_INPUT_UINPUT` (module or built-in) — standard everywhere.
* systemd (for the Bluetooth drop-in and the `/run/sdp` watcher; the Wi-Fi path
  works without it).
* .NET **10** SDK (the project targets `net10.0`).
* For Bluetooth only: BlueZ ≥ 5 with `libbluetooth.so.3` and a Bluetooth adapter.
* x86_64 or aarch64 (other architectures work if .NET 10 is available for them).

---

## Quick start

```sh
git clone <this repository>
cd RemoteGamepad

sudo ./scripts/setup-linux.sh     # one-time system configuration
# log out and back in (new group membership), then:
./scripts/check-linux.sh          # verify the environment
./scripts/build-server-linux.sh   # restore + Release build
./scripts/test-linux.sh           # uinput self-test (no Bluetooth needed)
./scripts/run-server-linux.sh     # run the server as your normal user
```

---

## 1. Setup — `scripts/setup-linux.sh`

```sh
sudo ./scripts/setup-linux.sh [options]
```

The script re-executes itself with `sudo` if you forget it, and it is
**idempotent** — running it again after a distribution upgrade, a BlueZ update
or a failed first attempt is safe and only changes what is actually different.

What it does:

1. **Detects** Linux, the distribution and family, architecture, package
   manager, systemd, the .NET SDK, BlueZ, `bluetoothd` (by path discovery, not
   assumption), `bluetoothctl`, `sdptool`, Bluetooth adapters and `/dev/uinput`.
2. **Installs the missing packages** for your distribution (asks first unless
   `--yes`). Packages that do not exist in your repositories are reported, not
   forced.
3. **Creates the dedicated `remote-gamepad` system group** and adds your user
   to it. If — and only if — your distribution also ships a `bluetooth` group
   (Debian/Ubuntu do, Fedora/Arch do not), your user is added to that too,
   because BlueZ's D-Bus policy there is group based.
4. **Configures persistent `/dev/uinput` access**:
   * `/etc/udev/rules.d/99-remote-gamepad-uinput.rules` →
     `KERNEL=="uinput", SUBSYSTEM=="misc", GROUP="remote-gamepad", MODE="0660", OPTIONS+="static_node=uinput"`
     (survives reboots, and the static node lets the module autoload on first open),
   * `/etc/modules-load.d/remote-gamepad-uinput.conf` → loads `uinput` at boot,
   * loads the module and re-applies permissions immediately so you do not have
     to reboot.
5. **Enables BlueZ compatibility SDP mode when required.** The server registers
   its SPP record through the local SDP socket `/run/sdp`, which only exists
   when `bluetoothd` runs with `--compat`. The script checks the *effective*
   `ExecStart` of `bluetooth.service`; if compatibility mode is already enabled
   by your distribution, nothing is changed. Otherwise it writes a drop-in:

   ```ini
   # /etc/systemd/system/bluetooth.service.d/10-remote-gamepad-compat.conf
   [Service]
   ExecStart=
   ExecStart=<detected bluetoothd> --compat
   ```

   The vendor unit (e.g. `/usr/lib/systemd/system/bluetooth.service`) is never
   edited, and the daemon path is taken from the vendor unit itself, so Fedora
   (`/usr/libexec/bluetooth/bluetoothd`), Arch (`/usr/lib/bluetooth/bluetoothd`)
   and anything else all work.
6. **Persists `/run/sdp` permissions as `root:remote-gamepad 0660`** using one
   event-driven mechanism (no polling, no competing watchers):
   * `/usr/local/libexec/remote-gamepad-fix-sdp-permissions` — a tiny idempotent
     helper,
   * `remote-gamepad-sdp-permissions.path` — a systemd path unit that watches
     `/run/sdp` for creation, deletion and attribute changes, ordered
     `Before=bluetooth.service` and pulled in by it (via a `.wants` symlink,
     not a unit-file edit),
   * `remote-gamepad-sdp-permissions.service` — the oneshot that runs the helper.

   Because the watcher reacts to attribute changes too, the permissions survive
   `systemctl restart bluetooth`, a reboot, and BlueZ's own `chmod` right after
   it creates the socket.
7. **Records** what it configured in `/etc/remote-gamepad/setup.env` (used by
   `check-linux.sh` and `uninstall-linux.sh`).

### Options

| Option | Meaning |
|---|---|
| `-y`, `--yes` | Non-interactive: accept package installation and the Bluetooth restart. |
| `--user NAME` | Configure a different account (default: the user behind `sudo`). |
| `--group NAME` | Use a different dedicated group name. |
| `--no-install` | Never install packages; only report what is missing. |
| `--no-bluetooth` | Wi-Fi-only machine: skip all BlueZ configuration. |
| `--no-sdp-tools` | Do not install the optional deprecated BlueZ tools (`sdptool`). |
| `--discoverable` | Optional: power the adapter, make it visible/pairable now and keep it visible across reboots (installs `remote-gamepad-discoverable.service`). Never required for RFCOMM/SDP; a BlueZ refusal is only a warning. |
| `--no-restart-bluetooth` | Apply the configuration but never restart `bluetooth.service`. |
| `--restart-bluetooth` | Always restart `bluetooth.service` when something changed. |
| `--dry-run` | Print every change without touching the system (works without root). |

> **Log out and back in** after the first run. Group membership only applies to
> new sessions. To test immediately in the current shell:
> `sg remote-gamepad -c './scripts/test-linux.sh'`.

---

## 2. Build — `scripts/build-server-linux.sh`

```sh
./scripts/build-server-linux.sh            # dotnet restore + dotnet build -c Release
```

Restore plus a Release build, nothing else: no `publish`, no `-r win-x64`, no
self-contained output. Output lands in
`Server/RemoteGamepadServer/bin/Release/net10.0/`.

The script refuses to run as root (root-owned `obj/` and `bin/` directories
would break the unprivileged server). Use `--allow-root` only if you really
need it. Extra arguments are forwarded to `dotnet build`.

---

## 3. Test — `scripts/test-linux.sh`

```sh
./scripts/test-linux.sh
```

1. Verifies the uinput prerequisites: module loaded (or built in), `/dev/uinput`
   present, writable by *your* user, the `remote-gamepad` group, .NET 10 SDK.
2. Runs the managed regression tests:

   ```sh
   dotnet run --project Server/RemoteGamepadServer.Tests -c Release
   ```

   which pin the Bluetooth startup behaviour: a simulated
   `org.bluez.Error.Failed` from the discoverability step must be logged and
   ignored so RFCOMM bind/listen and SDP registration still run (plus the
   protocol/port/mapping invariants). They need no adapter, no root and no
   uinput device.
3. Runs the server's existing self-test:

   ```sh
   dotnet run -c Release -- --uinput-self-test
   ```

   which validates a raw 20-field Android JSON packet through `InputParser` and
   then emits every digital press/release and stick/trigger extreme with
   `[SELF-TEST]` labels.

**Bluetooth is not required** — the tests exercise only the kernel uinput path
and pure managed logic, so they pass on Wi-Fi-only machines with no adapter and
no BlueZ.

Options: `--prereqs-only` (checks only), `--no-unit-tests` (skip the managed
regression tests), `--timeout SECONDS`.

While the server runs you can confirm the device from another terminal:

```sh
grep -A8 -B2 'Xbox 360 Controller' /proc/bus/input/devices
evtest /dev/input/eventN        # N from the output above
```

---

## 4. Run — `scripts/run-server-linux.sh`

```sh
./scripts/run-server-linux.sh                # normal run
./scripts/run-server-linux.sh --trace-input  # rate-limited input diagnostics
```

Runs `dotnet run -c Release` from the server directory as your normal user and
**refuses to run as root or under sudo**. Before starting it checks
`/dev/uinput` access and warns (without blocking) if `/run/sdp` has unexpected
permissions. Arguments are passed straight through to the server.

Stop with `Ctrl+C`: the controller is reset and the uinput device destroyed.

Open UDP **26760** and **26761** in your firewall on the trusted local network,
for example:

```sh
sudo firewall-cmd --add-port=26760-26761/udp --permanent && sudo firewall-cmd --reload   # firewalld (Fedora/RHEL)
sudo ufw allow 26760:26761/udp                                                           # ufw (Ubuntu/Debian)
```

Quick manual packet test from another machine:

```sh
printf '{"lx":0.6,"ly":-0.2,"a":true}' | nc -u -w1 SERVER_IP 26760
```

---

## 5. Diagnostics — `scripts/check-linux.sh`

```sh
./scripts/check-linux.sh          # read-only, no root needed
./scripts/check-linux.sh --strict # also exit non-zero on warnings
```

Reports distribution, kernel, architecture, systemd, .NET SDKs and runtimes,
BlueZ version, the detected `bluetoothd` path, `bluetooth.service` state and
effective `ExecStart`, whether compatibility SDP mode is active (and whether it
comes from the distribution or from the RemoteGamepad drop-in), adapters,
`/dev/uinput` ownership and writability, the udev rule and module autoload,
`/run/sdp` ownership, the SDP watcher units, group membership (including
whether it is active in your current session) and whether the RemoteGamepad SDP
record is currently registered.

Exit codes: `0` ready, `1` blocking problem, `2` warnings with `--strict`.
Bluetooth findings are warnings only — a Wi-Fi-only machine still exits `0`.

---

## Wi-Fi-only mode (no Bluetooth hardware)

Everything works without a Bluetooth adapter:

```sh
sudo ./scripts/setup-linux.sh --no-bluetooth
./scripts/build-server-linux.sh
./scripts/test-linux.sh
./scripts/run-server-linux.sh
```

* No BlueZ package is installed, no drop-in is written, no SDP watcher is
  enabled.
* If BlueZ *is* installed but no adapter is present, setup still configures
  everything and simply does not restart `bluetooth.service`.
* The server logs that Bluetooth is unavailable and keeps serving Wi-Fi/UDP.
* `check-linux.sh` reports the missing Bluetooth pieces as warnings, not errors.

---

## Bluetooth requirements

1. **BlueZ runtime**: `bluetoothd` plus `libbluetooth.so.3` (the server P/Invokes
   it for native SDP registration) — `bluez` + `bluez-libs`/`libbluetooth3`.
2. **Compatibility SDP mode**: `bluetoothd --compat`, provided by the
   RemoteGamepad drop-in when your distribution does not enable it. Without it
   `/run/sdp` does not exist and SDP registration fails.
3. **Socket access**: `/run/sdp` must be `root:remote-gamepad 0660` so the
   unprivileged server can register. Maintained by the path unit.
4. **Group membership**: your user in `remote-gamepad` (and in `bluetooth` where
   that group exists, for the D-Bus policy on Debian/Ubuntu).
5. **A powered adapter** and the phone paired with the PC.

**Not** a requirement: *discoverable* mode. The server registers its SPP record
and listens on RFCOMM channel 1 whether or not the adapter is visible to other
devices — discoverability only matters while you pair the phone the first time.
BlueZ frequently refuses the request (`org.bluez.Error.Failed`, e.g. on
Fedora 44 / BlueZ 5.87), so the server logs the refusal and keeps going:

```text
[BT] Discoverable mode could not be enabled through InTheHand/BlueZ: org.bluez.Error.Failed: Failed
[BT] Continuing with RFCOMM/SDP registration.
[BT] Native BlueZ SPP SDP record registered on RFCOMM channel 1
```

If you do want the machine to stay visible, use the optional helper:

```sh
sudo ./scripts/setup-linux.sh --discoverable   # now and after every reboot
./scripts/remote-gamepad-set-discoverable on   # just for this session
./scripts/remote-gamepad-set-discoverable status
```

It powers the adapter, clears `DiscoverableTimeout` and sets `Discoverable`
through `busctl`, falling back to `dbus-send` and then `bluetoothctl`. Failure is
always a warning: it can never stop the RFCOMM listener from starting. Run the
server with `--no-discoverable` to skip the attempt (and its warning) entirely.

Verification:

```sh
bluetoothctl show                       # adapter present and powered
systemctl status bluetooth              # daemon running
stat -c '%U:%G %a %n' /run/sdp          # root:remote-gamepad 660
systemctl status remote-gamepad-sdp-permissions.path
sdptool browse local | grep -i RemoteGamepad   # while the server runs
```

---

## Troubleshooting

**`Cannot open /dev/uinput (errno 13)` / `Write access: no`**
Your session predates the group change. Log out and back in, or
`sg remote-gamepad -c './scripts/run-server-linux.sh'`. Confirm with
`ls -l /dev/uinput` (expect `root remote-gamepad` and `crw-rw----`) and `id -nG`.

**`/dev/uinput` missing after reboot**
Check `lsmod | grep uinput` and `cat /etc/modules-load.d/remote-gamepad-uinput.conf`.
Some kernels build uinput in — then `/sys/class/misc/uinput` exists and the node
is created on demand by the udev `static_node` option. Re-run
`sudo ./scripts/setup-linux.sh`.

**`/dev/uinput` has the wrong group**
Another udev rule may sort after ours. `check-linux.sh` lists competing rules
under "Other uinput rules"; remove or renumber them.

**`Bluetooth SDP error while connecting to the local BlueZ SDP server`**
`/run/sdp` is missing (compatibility mode off) or not accessible. Check:

```sh
systemctl show -p ExecStart --value bluetooth.service    # must contain --compat
ls -l /run/sdp
journalctl -u remote-gamepad-sdp-permissions.service -b --no-pager
```

**`/run/sdp` reverts to `root:root` after `systemctl restart bluetooth`**
The path unit is not armed. `systemctl status remote-gamepad-sdp-permissions.path`
should be *active (waiting)*; if not,
`sudo systemctl enable --now remote-gamepad-sdp-permissions.path`.

**`Bluetooth unavailable; Wi-Fi remains active`**
No adapter, adapter off, or the daemon is not running:
`bluetoothctl show`, `rfkill list`, `systemctl status bluetooth`.

**`org.bluez.Error.Failed` when the server enables discoverable mode**
Harmless by design since this is best-effort: the log continues with
`[BT] Continuing with RFCOMM/SDP registration.` and Bluetooth still works for a
paired phone. Common causes are an unpowered adapter, a session without polkit
authorisation (SSH, service) or a controller/kernel quirk. To fix it anyway:

```sh
./scripts/remote-gamepad-set-discoverable status   # powered? discoverable?
sudo ./scripts/setup-linux.sh --discoverable       # power + make visible, persistently
rfkill list bluetooth                              # soft/hard blocked?
```

Start the server with `./scripts/run-server-linux.sh --no-discoverable` if you
never need the machine to be visible.

**Adapter visible but the server cannot change its mode (D-Bus)**
On Debian/Ubuntu make sure you are in the `bluetooth` group (setup does this
when the group exists) and that you are logged into a normal desktop session.

**Android cannot find the PC over Wi-Fi**
Both devices must be on the same subnet with client isolation off, and UDP
26760/26761 must be open. Test discovery:
`printf 'REMOTE_GAMEPAD_DISCOVER' | nc -u -w1 SERVER_IP 26761`.

**`dotnet` is missing or too old**
The project targets `net10.0`. `dotnet --list-sdks` must show a `10.x` SDK. If
your distribution does not package it yet:
`curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0`
and add `$HOME/.dotnet` to `PATH`.

**SELinux (Fedora/RHEL)**
The scripts use standard file locations and systemd drop-ins, so no custom
policy is required. If `journalctl -t setroubleshoot` shows denials around
`/run/sdp`, report them with the output of `./scripts/check-linux.sh`.

---

## Uninstall — `scripts/uninstall-linux.sh`

```sh
sudo ./scripts/uninstall-linux.sh [--yes] [--remove-group] [--dry-run]
```

Removes **only** what the setup script created:

* `/etc/udev/rules.d/99-remote-gamepad-uinput.rules`
* `/etc/modules-load.d/remote-gamepad-uinput.conf`
* `/etc/systemd/system/bluetooth.service.d/10-remote-gamepad-compat.conf`
  (and the drop-in directory if it is left empty)
* `/etc/systemd/system/remote-gamepad-sdp-permissions.{path,service}` and their
  `.wants` symlinks
* `/usr/local/libexec/remote-gamepad-fix-sdp-permissions`
* `/etc/systemd/system/remote-gamepad-discoverable.service`, its `.wants`
  symlink and `/usr/local/libexec/remote-gamepad-set-discoverable`
  (only present after `--discoverable`)
* `/etc/remote-gamepad/setup.env`

then reloads systemd and udev, optionally restarts `bluetooth.service` and
restores `/run/sdp` to `root:root 0660` if it still exists.

It never touches .NET, BlueZ, distribution packages, repository files or any
configuration it did not write. The `remote-gamepad` group is kept unless you
pass `--remove-group`.

---

## Developer notes

* **Self-test for the scripts themselves:** `./scripts/tests/linux-scripts-test.sh`
  runs the setup/check/test/uninstall scripts against simulated Fedora, Ubuntu,
  Arch, Debian-with-`--compat` and unknown-distro environments (stubbed `PATH`
  plus `--dry-run`), performs a full install → re-install → uninstall cycle with
  real file writes redirected into a scratch directory, unit-tests the shared
  helpers in `scripts/lib/remote-gamepad-linux.sh`, and exercises the `/run/sdp`
  helper against a real Unix socket. It needs no root and changes nothing on the
  host.
* **Test hooks** (only for that harness, never needed in normal use):
  `REMOTE_GAMEPAD_PREFIX` relocates every managed path under a scratch root (and
  disables the `sudo` re-exec), `RG_DRY_RUN=1` forces dry-run mode, and
  `RG_OS_RELEASE`, `REMOTE_GAMEPAD_UINPUT_DEV`, `REMOTE_GAMEPAD_SDP_SOCKET`,
  `REMOTE_GAMEPAD_SYS_BLUETOOTH`, `REMOTE_GAMEPAD_SYSTEMD_RUN_DIR` point the
  detection helpers at fixtures.
* **Re-running `setup-linux.sh` is cheap:** files are compared before they are
  written, and `bluetooth.service` is only restarted when the configuration
  actually changed or the running daemon was started without `--compat`.
* **Shared library:** all Linux scripts source
  `scripts/lib/remote-gamepad-linux.sh`, which holds the paths, group name,
  detection helpers and the dry-run-aware execution helpers.
* **Server code is unchanged** by this setup work: the Android protocol,
  controller mapping, uinput implementation, UDP server and Bluetooth
  RFCOMM/SDP implementation are exactly as before.

## Known distro-specific limitations

* **.NET 10 packaging varies.** Ubuntu 24.04+/Fedora/Arch ship `dotnet-sdk-10.0`;
  Ubuntu 22.04 needs `ppa:dotnet/backports` and older Debian needs the Microsoft
  feed or `dotnet-install.sh`. Setup detects and reports this instead of adding
  third-party repositories behind your back.
* **`sdptool` is deprecated** and lives in `bluez-deprecated` (Fedora) or
  `bluez-deprecated-tools` (Arch); it is shipped inside `bluez` on
  Debian/Ubuntu. It is only used for verification — the server registers its SDP
  record through `libbluetooth` directly.
* **BlueZ compatibility mode is a deprecated interface.** It is required by the
  current native SDP registration code; a future port to BlueZ's D-Bus
  `ProfileManager1` would remove that need.
* **Non-systemd init systems** (OpenRC, runit, s6) are out of scope for the
  Bluetooth drop-in and the `/run/sdp` watcher. uinput configuration still
  applies, and Wi-Fi/UDP works.
* **Immutable/atomic distributions** (Silverblue, MicroOS, NixOS) do not accept
  `/usr/local` or package installs the usual way; the udev rule, modules-load
  file and systemd units must be adapted to the host's configuration mechanism.
* **Flatpak/Snap .NET** installs are not auto-detected; install the SDK through
  the distribution or `dotnet-install.sh`.
