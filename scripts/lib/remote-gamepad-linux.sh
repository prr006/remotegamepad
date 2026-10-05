#!/usr/bin/env bash
# shellcheck shell=bash
#
# Shared helpers for the RemoteGamepad Linux scripts
# (setup-linux.sh, check-linux.sh, test-linux.sh, build-server-linux.sh,
#  run-server-linux.sh, uninstall-linux.sh).
#
# Design rules enforced here:
#   * nothing is distro specific unless it is detected at runtime,
#   * no hard-coded bluetoothd path, no assumed "bluetooth" Unix group,
#   * vendor systemd unit files are never edited (drop-ins only),
#   * every mutating action goes through rg_run / rg_install_file so that
#     --dry-run is honoured everywhere.
#
# Test hooks (used by the repository self-tests only, never required at runtime):
#   RG_OS_RELEASE                 alternative os-release file
#   RG_DRY_RUN=1                  print mutating actions instead of running them
#   RG_FAKE_UNAME_M               override the reported architecture
#   REMOTE_GAMEPAD_UINPUT_DEV     alternative uinput device node
#   REMOTE_GAMEPAD_SDP_SOCKET     alternative BlueZ compat SDP socket
#   REMOTE_GAMEPAD_SYS_BLUETOOTH  alternative /sys/class/bluetooth directory
#   REMOTE_GAMEPAD_SYSTEMD_RUN_DIR  alternative /run/systemd/system marker directory
#   REMOTE_GAMEPAD_PREFIX         relocate every managed system path under a prefix

# The constants below are consumed by the scripts that source this library.
# shellcheck disable=SC2034

[ -n "${RG_LIB_SOURCED:-}" ] && return 0
RG_LIB_SOURCED=1

# --------------------------------------------------------------------------
# Constants (single source of truth for every script)
# --------------------------------------------------------------------------
RG_PROJECT_NAME="RemoteGamepad"
RG_GROUP="${REMOTE_GAMEPAD_GROUP:-remote-gamepad}"

# Every managed path goes through this prefix. It is empty in normal use; the
# repository self-tests set it to a scratch directory so the scripts can be
# executed for real without touching the host.
RG_PREFIX="${REMOTE_GAMEPAD_PREFIX:-}"

RG_UDEV_RULE="${RG_PREFIX}/etc/udev/rules.d/99-remote-gamepad-uinput.rules"
RG_MODULES_CONF="${RG_PREFIX}/etc/modules-load.d/remote-gamepad-uinput.conf"

RG_SYSTEMD_UNIT_DIR="${RG_PREFIX}/etc/systemd/system"
RG_BT_DROPIN_DIR="${RG_SYSTEMD_UNIT_DIR}/bluetooth.service.d"
RG_BT_DROPIN="${RG_BT_DROPIN_DIR}/10-remote-gamepad-compat.conf"

RG_SDP_HELPER="${RG_PREFIX}/usr/local/libexec/remote-gamepad-fix-sdp-permissions"
RG_SDP_PATH_UNIT="remote-gamepad-sdp-permissions.path"
RG_SDP_SERVICE_UNIT="remote-gamepad-sdp-permissions.service"

# Autostart service: the server itself, running unprivileged at boot.
RG_SERVICE_UNIT="remote-gamepad.service"

# Optional adapter-visibility helper (setup-linux.sh --discoverable). Never
# required for RFCOMM/SDP: it only makes pairing from the phone easier.
RG_DISCOVERABLE_HELPER="${RG_PREFIX}/usr/local/libexec/remote-gamepad-set-discoverable"
RG_DISCOVERABLE_UNIT="remote-gamepad-discoverable.service"
RG_SDP_SOCKET="${REMOTE_GAMEPAD_SDP_SOCKET:-/run/sdp}"
RG_UINPUT_DEV="${REMOTE_GAMEPAD_UINPUT_DEV:-/dev/uinput}"
RG_SYS_BLUETOOTH_DIR="${REMOTE_GAMEPAD_SYS_BLUETOOTH:-/sys/class/bluetooth}"
RG_SYSTEMD_RUN_DIR="${REMOTE_GAMEPAD_SYSTEMD_RUN_DIR:-/run/systemd/system}"

RG_STATE_DIR="${RG_PREFIX}/etc/remote-gamepad"
RG_STATE_FILE="${RG_STATE_DIR}/setup.env"

RG_UDP_INPUT_PORT=26760
RG_UDP_DISCOVERY_PORT=26761
RG_REQUIRED_DOTNET_MAJOR=10

# Files written by older revisions of this repository. They are superseded by
# the files above and are removed on setup so that only one SDP watcher exists.
RG_LEGACY_UDEV_RULE="${RG_PREFIX}/etc/udev/rules.d/99-uinput.rules"
RG_LEGACY_UDEV_CONTENT='KERNEL=="uinput", MODE="0660", GROUP="uinput", OPTIONS+="static_node=uinput"'
RG_LEGACY_PATHS=(
    "${RG_PREFIX}/usr/local/libexec/remote-gamepad-fix-sdp-permissions.sh"
)

RG_DRY_RUN="${RG_DRY_RUN:-0}"
RG_CHANGES=0

# --------------------------------------------------------------------------
# PATH: administrative tools (groupadd, usermod, modprobe, udevadm,
# bluetoothd, ...) live in sbin directories that are frequently absent from a
# normal user's PATH. Add the standard locations so detection works the same
# whether a script runs as root, under sudo or as the desktop user.
# --------------------------------------------------------------------------
for rg_sbin in /usr/local/sbin /usr/sbin /sbin; do
    case ":${PATH}:" in
        *":${rg_sbin}:"*) ;;
        *) [ -d "$rg_sbin" ] && PATH="${PATH}:${rg_sbin}" ;;
    esac
done
unset rg_sbin
export PATH

# --------------------------------------------------------------------------
# Output helpers
# --------------------------------------------------------------------------
if [ -t 1 ] && [ -z "${NO_COLOR:-}" ] && [ "${TERM:-dumb}" != "dumb" ]; then
    RG_C_RESET=$'\033[0m'; RG_C_BOLD=$'\033[1m'; RG_C_DIM=$'\033[2m'
    RG_C_RED=$'\033[31m'; RG_C_GREEN=$'\033[32m'; RG_C_YELLOW=$'\033[33m'; RG_C_BLUE=$'\033[34m'
else
    RG_C_RESET=''; RG_C_BOLD=''; RG_C_DIM=''
    RG_C_RED=''; RG_C_GREEN=''; RG_C_YELLOW=''; RG_C_BLUE=''
fi

rg_section() { printf '\n%s== %s ==%s\n' "${RG_C_BOLD}${RG_C_BLUE}" "$*" "${RG_C_RESET}"; }
rg_info()    { printf '%s\n' "$*"; }
rg_step()    { printf '%s->%s %s\n' "${RG_C_BLUE}" "${RG_C_RESET}" "$*"; }
rg_ok()      { printf '%s[ ok ]%s %s\n' "${RG_C_GREEN}" "${RG_C_RESET}" "$*"; }
rg_warn()    { printf '%s[warn]%s %s\n' "${RG_C_YELLOW}" "${RG_C_RESET}" "$*" >&2; }
rg_err()     { printf '%s[fail]%s %s\n' "${RG_C_RED}" "${RG_C_RESET}" "$*" >&2; }
rg_dim()     { printf '%s%s%s\n' "${RG_C_DIM}" "$*" "${RG_C_RESET}"; }
# Success message for an action that was actually performed (quiet in dry-run,
# where the planned command has already been printed).
rg_done()    { rg_dry_run || rg_ok "$*"; }
rg_die()     { rg_err "$*"; exit 1; }

# rg_field <label> <value> [status]  -> aligned "label : value" report line
rg_field() {
    local label="$1" value="$2" status="${3:-}" marker=''
    case "$status" in
        ok)   marker="${RG_C_GREEN}[ ok ]${RG_C_RESET} " ;;
        warn) marker="${RG_C_YELLOW}[warn]${RG_C_RESET} " ;;
        fail) marker="${RG_C_RED}[fail]${RG_C_RESET} " ;;
        *)    marker="       " ;;
    esac
    printf '  %s%-22s : %s\n' "$marker" "$label" "$value"
}

# --------------------------------------------------------------------------
# Execution helpers (dry-run aware)
# --------------------------------------------------------------------------
rg_have() { command -v "$1" >/dev/null 2>&1; }

rg_dry_run() { [ "$RG_DRY_RUN" = "1" ]; }

# rg_run <command...>  — run a mutating command (skipped in dry-run mode)
rg_run() {
    if rg_dry_run; then
        printf '   %s[dry-run]%s %s\n' "${RG_C_DIM}" "${RG_C_RESET}" "$*"
        return 0
    fi
    "$@"
}

# rg_run_quiet <command...> — like rg_run but tolerates failure
rg_run_quiet() {
    if rg_dry_run; then
        printf '   %s[dry-run]%s %s\n' "${RG_C_DIM}" "${RG_C_RESET}" "$*"
        return 0
    fi
    "$@" >/dev/null 2>&1 || return $?
}

# rg_install_file <path> <mode> <<<content
# Idempotent: only writes when the content or mode differs.
rg_install_file() {
    local path="$1" mode="$2" content
    content="$(cat)"
    if [ -f "$path" ] && [ "$(cat -- "$path" 2>/dev/null)" = "$content" ]; then
        local current_mode=''
        current_mode="$(stat -c '%a' -- "$path" 2>/dev/null || echo '')"
        if [ "$current_mode" = "${mode#0}" ] || [ "$current_mode" = "$mode" ]; then
            rg_dim "   unchanged: $path"
            return 0
        fi
    fi
    RG_CHANGES=$((RG_CHANGES + 1))
    if rg_dry_run; then
        printf '   %s[dry-run]%s write %s (mode %s)\n' "${RG_C_DIM}" "${RG_C_RESET}" "$path" "$mode"
        printf '%s\n' "$content" | sed 's/^/        | /'
        return 0
    fi
    install -d -m 0755 -- "$(dirname -- "$path")"
    printf '%s\n' "$content" > "$path.rg-tmp.$$"
    chmod "$mode" -- "$path.rg-tmp.$$"
    mv -f -- "$path.rg-tmp.$$" "$path"
    rg_ok "wrote $path"
}

rg_remove_file() {
    local path="$1"
    [ -e "$path" ] || [ -L "$path" ] || return 0
    RG_CHANGES=$((RG_CHANGES + 1))
    rg_run rm -f -- "$path" && rg_ok "removed $path"
}

# --------------------------------------------------------------------------
# Platform / distribution detection
# --------------------------------------------------------------------------
rg_require_linux() {
    local kernel; kernel="$(uname -s 2>/dev/null || echo unknown)"
    [ "$kernel" = "Linux" ] || rg_die "This script supports Linux only (detected: $kernel)."
}

RG_OS_ID=''; RG_OS_ID_LIKE=''; RG_OS_NAME=''; RG_OS_VERSION=''
rg_load_os_release() {
    local file="${RG_OS_RELEASE:-/etc/os-release}" parsed
    local -a fields=()
    RG_OS_ID='unknown'; RG_OS_ID_LIKE=''; RG_OS_NAME='Unknown Linux'; RG_OS_VERSION=''
    [ -r "$file" ] || return 0
    # os-release is a shell-compatible key=value file; sourcing it in a
    # subshell is the documented way to read it.
    # shellcheck disable=SC1090
    parsed="$(. "$file" >/dev/null 2>&1; printf '%s\n%s\n%s\n%s\n' \
        "${ID:-unknown}" "${ID_LIKE:-}" "${PRETTY_NAME:-${NAME:-Unknown Linux}}" "${VERSION_ID:-}")"
    mapfile -t fields <<< "$parsed"
    RG_OS_ID="${fields[0]:-unknown}"
    RG_OS_ID_LIKE="${fields[1]:-}"
    RG_OS_NAME="${fields[2]:-Unknown Linux}"
    RG_OS_VERSION="${fields[3]:-}"
}

# rg_distro_family -> fedora | debian | arch | suse | unknown
rg_distro_family() {
    [ -n "$RG_OS_ID" ] || rg_load_os_release
    local id="${RG_OS_ID,,}" like="${RG_OS_ID_LIKE,,}"
    case " $id $like " in
        *" fedora "*|*" rhel "*|*" centos "*|*" almalinux "*|*" rocky "*) echo fedora; return ;;
        *" debian "*|*" ubuntu "*)                                        echo debian; return ;;
        *" arch "*|*" archlinux "*|*" manjaro "*|*" endeavouros "*)       echo arch;   return ;;
        *" suse "*|*" opensuse "*|*" sles "*)                             echo suse;   return ;;
    esac
    # ID itself may be a derivative not covered above; fall back to tooling.
    if rg_have dnf || rg_have yum; then echo fedora
    elif rg_have apt-get;             then echo debian
    elif rg_have pacman;              then echo arch
    elif rg_have zypper;              then echo suse
    else echo unknown
    fi
}

# rg_package_manager -> dnf | apt | pacman | zypper | none
rg_package_manager() {
    if   rg_have dnf5;    then echo dnf
    elif rg_have dnf;     then echo dnf
    elif rg_have apt-get; then echo apt
    elif rg_have pacman;  then echo pacman
    elif rg_have zypper;  then echo zypper
    elif rg_have yum;     then echo yum
    else echo none
    fi
}

rg_arch() { printf '%s' "${RG_FAKE_UNAME_M:-$(uname -m)}"; }

rg_arch_supported() {
    case "$(rg_arch)" in
        x86_64|amd64|aarch64|arm64) return 0 ;;
        *) return 1 ;;
    esac
}

# --------------------------------------------------------------------------
# systemd helpers
# --------------------------------------------------------------------------
rg_have_systemctl() { rg_have systemctl; }

# True when systemd is the running init (not just installed, e.g. containers).
rg_systemd_running() { [ -d "$RG_SYSTEMD_RUN_DIR" ] && rg_have systemctl; }

rg_systemd_version() {
    rg_have systemctl || return 1
    systemctl --version 2>/dev/null | awk 'NR==1 {print $2; exit}'
}

rg_unit_exists() {
    rg_have systemctl || return 1
    systemctl list-unit-files "$1" >/dev/null 2>&1 &&
        [ -n "$(systemctl list-unit-files --no-legend "$1" 2>/dev/null)" ]
}

rg_unit_active()  { rg_have systemctl && systemctl is-active  --quiet "$1" 2>/dev/null; }
rg_unit_enabled() { rg_have systemctl && systemctl is-enabled --quiet "$1" 2>/dev/null; }

rg_unit_state() {
    local unit="$1" active enabled
    rg_have systemctl || { echo "unknown (systemctl unavailable)"; return; }
    active="$(systemctl is-active "$unit" 2>/dev/null || true)"
    enabled="$(systemctl is-enabled "$unit" 2>/dev/null || true)"
    printf '%s (%s)' "${active:-unknown}" "${enabled:-not-enabled}"
}

rg_daemon_reload() {
    rg_systemd_running || { rg_dim "   systemd not running; skipping daemon-reload"; return 0; }
    rg_run systemctl daemon-reload || { rg_warn "systemctl daemon-reload failed."; return 0; }
}

# --------------------------------------------------------------------------
# .NET detection
# --------------------------------------------------------------------------
rg_dotnet_path() { command -v dotnet 2>/dev/null; }

# Absolute dotnet host for a given user: the system install first, then the
# per-user installation that dotnet-install.sh creates (root's PATH misses it).
rg_dotnet_for_user() {
    local user="${1:-}" candidate home
    candidate="$(rg_dotnet_path 2>/dev/null || true)"
    if [ -n "$candidate" ]; then
        printf '%s' "$candidate"
        return 0
    fi
    for candidate in /usr/lib/dotnet/dotnet /usr/share/dotnet/dotnet /usr/local/bin/dotnet /opt/dotnet/dotnet; do
        [ -x "$candidate" ] && { printf '%s' "$candidate"; return 0; }
    done
    if [ -n "$user" ]; then
        home="$(rg_user_home "$user" 2>/dev/null || true)"
        [ -n "$home" ] && [ -x "${home}/.dotnet/dotnet" ] && { printf '%s' "${home}/.dotnet/dotnet"; return 0; }
    fi
    return 1
}

# Target framework of the server project (falls back to the required major).
rg_server_tfm() {
    local csproj="${RG_SERVER_DIR:-}/RemoteGamepadServer.csproj" tfm=''
    if [ -r "$csproj" ]; then
        tfm="$(sed -n 's|.*<TargetFramework>\([^<]*\)</TargetFramework>.*|\1|p' "$csproj" | head -1)"
    fi
    printf '%s' "${tfm:-net${RG_REQUIRED_DOTNET_MAJOR}.0}"
}

# Absolute path of the Release build output: an existing build wins, otherwise
# the path the build scripts will produce.
rg_server_dll() {
    local dll
    for dll in "${RG_SERVER_DIR:-}"/bin/Release/*/RemoteGamepadServer.dll; do
        [ -f "$dll" ] && { printf '%s' "$dll"; return 0; }
    done
    printf '%s' "${RG_SERVER_DIR:-}/bin/Release/$(rg_server_tfm)/RemoteGamepadServer.dll"
}

# Highest installed SDK version string, empty when no SDK is present.
rg_dotnet_sdk_version() {
    rg_have dotnet || return 1
    dotnet --list-sdks 2>/dev/null | awk '{print $1}' | sort -V | tail -1
}

rg_dotnet_sdk_major() {
    local version; version="$(rg_dotnet_sdk_version 2>/dev/null || true)"
    [ -n "$version" ] || return 1
    printf '%s' "${version%%.*}"
}

rg_dotnet_sdk_ok() {
    local major; major="$(rg_dotnet_sdk_major 2>/dev/null || true)"
    [ -n "$major" ] || return 1
    [ "$major" -ge "$RG_REQUIRED_DOTNET_MAJOR" ] 2>/dev/null
}

# --------------------------------------------------------------------------
# uinput helpers
# --------------------------------------------------------------------------
rg_uinput_module_state() {
    if [ -r /proc/modules ] && grep -q '^uinput ' /proc/modules 2>/dev/null; then
        echo loaded
    elif [ -e "$RG_UINPUT_DEV" ] || [ -d /sys/class/misc/uinput ]; then
        echo builtin-or-loaded
    else
        echo not-loaded
    fi
}

rg_uinput_exists() { [ -e "$RG_UINPUT_DEV" ]; }

rg_uinput_owner() {
    [ -e "$RG_UINPUT_DEV" ] || return 1
    stat -c '%U:%G %a' "$RG_UINPUT_DEV" 2>/dev/null
}

# True when the *invoking* user can open the uinput device read-write.
rg_uinput_writable() { [ -w "$RG_UINPUT_DEV" ] && [ -r "$RG_UINPUT_DEV" ]; }

# --------------------------------------------------------------------------
# BlueZ helpers (nothing below assumes a path, a group, or a distro)
# --------------------------------------------------------------------------

# Vendor ExecStart line of bluetooth.service, read from the unit *fragment* so
# that a RemoteGamepad drop-in is never mistaken for vendor configuration.
rg_bluetooth_fragment_path() {
    rg_have systemctl || return 1
    local fragment
    fragment="$(systemctl show -p FragmentPath --value bluetooth.service 2>/dev/null || true)"
    [ -n "$fragment" ] && [ -r "$fragment" ] || return 1
    printf '%s' "$fragment"
}

rg_bluetooth_vendor_execstart() {
    local fragment line
    fragment="$(rg_bluetooth_fragment_path 2>/dev/null || true)"
    [ -n "$fragment" ] || return 1
    line="$(grep -E '^[[:space:]]*ExecStart[[:space:]]*=' -- "$fragment" | tail -1 || true)"
    [ -n "$line" ] || return 1
    line="${line#*=}"
    # strip surrounding whitespace
    line="$(printf '%s' "$line" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')"
    [ -n "$line" ] || return 1
    printf '%s' "$line"
}

# Effective ExecStart (vendor + drop-ins) as reported by systemd.
rg_bluetooth_effective_execstart() {
    rg_have systemctl || return 1
    systemctl show -p ExecStart --value bluetooth.service 2>/dev/null |
        sed -n 's/.*argv\[\]=\([^;]*\).*/\1/p' | tail -1 |
        sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//'
}

# Remove systemd's special ExecStart prefixes (@ - : + !) from a command word.
rg_strip_exec_prefix() {
    local word="$1"
    while [ -n "$word" ]; do
        case "$word" in
            [-@+!:]*) word="${word#?}" ;;
            *) break ;;
        esac
    done
    printf '%s' "$word"
}

# Locate bluetoothd without assuming any distribution layout.
rg_find_bluetoothd() {
    local candidate exec_line
    for exec_line in "$(rg_bluetooth_vendor_execstart 2>/dev/null || true)" \
                     "$(rg_bluetooth_effective_execstart 2>/dev/null || true)"; do
        [ -n "$exec_line" ] || continue
        candidate="$(rg_strip_exec_prefix "${exec_line%% *}")"
        [ -x "$candidate" ] && { printf '%s' "$candidate"; return 0; }
    done
    if candidate="$(command -v bluetoothd 2>/dev/null)"; then
        printf '%s' "$candidate"; return 0
    fi
    for candidate in /usr/libexec/bluetooth/bluetoothd \
                     /usr/lib/bluetooth/bluetoothd \
                     /usr/lib64/bluetooth/bluetoothd \
                     /usr/lib/bluez/bluetoothd \
                     /usr/sbin/bluetoothd \
                     /sbin/bluetoothd \
                     /usr/local/libexec/bluetooth/bluetoothd; do
        [ -x "$candidate" ] && { printf '%s' "$candidate"; return 0; }
    done
    return 1
}

rg_bluetoothd_version() {
    local daemon; daemon="$(rg_find_bluetoothd 2>/dev/null || true)"
    [ -n "$daemon" ] || return 1
    "$daemon" --version 2>/dev/null | head -1
}

# Does this bluetoothd build understand the compatibility (--compat) switch?
rg_bluetoothd_supports_compat() {
    local daemon; daemon="$(rg_find_bluetoothd 2>/dev/null || true)"
    [ -n "$daemon" ] || return 1
    "$daemon" --help 2>&1 | grep -q -- '--compat' || return 1
}

# True when bluetoothd is currently configured to run in compatibility mode.
rg_compat_mode_configured() {
    local exec_line
    exec_line="$(rg_bluetooth_effective_execstart 2>/dev/null || true)"
    [ -n "$exec_line" ] || exec_line="$(rg_bluetooth_vendor_execstart 2>/dev/null || true)"
    [ -n "$exec_line" ] || return 1
    case " $exec_line " in
        *" --compat "*|*" -C "*) return 0 ;;
    esac
    return 1
}

# Is the *running* bluetoothd process in compatibility mode?
#   0 = yes, 1 = no, 2 = cannot tell (no systemd, not running, /proc unreadable).
# Used to decide whether a restart is genuinely needed, so re-running setup on
# an already-configured host does not bounce the Bluetooth stack.
rg_bluetoothd_running_compat() {
    local pid cmdline
    rg_have systemctl || return 2
    pid="$(systemctl show -p MainPID --value bluetooth.service 2>/dev/null || true)"
    case "$pid" in
        ''|0|*[!0-9]*) return 2 ;;
    esac
    [ -r "/proc/${pid}/cmdline" ] || return 2
    cmdline="$(tr '\0' ' ' < "/proc/${pid}/cmdline" 2>/dev/null || true)"
    [ -n "$cmdline" ] || return 2
    case " $cmdline " in
        *" --compat "*|*" -C "*) return 0 ;;
    esac
    return 1
}

# Runs the repository copy of the discoverability helper (on|off|status).
# There is exactly one implementation; the installed copy and this call site
# both use scripts/remote-gamepad-set-discoverable.
rg_discoverable_helper() {
    local helper="${RG_SCRIPT_DIR:-}/remote-gamepad-set-discoverable"
    [ -x "$helper" ] || return 127
    "$helper" "$@"
}

# Prints "key=value" lines (adapter/powered/discoverable/pairable/...) or nothing.
rg_discoverable_status() {
    rg_discoverable_helper status 2>/dev/null || return 1
}

# Extracts one field from rg_discoverable_status output.
rg_discoverable_field() {
    local field="$1" status="${2:-}"
    [ -n "$status" ] || status="$(rg_discoverable_status || true)"
    printf '%s\n' "$status" | awk -F= -v key="$field" '$1 == key { print $2; found = 1 } END { exit !found }'
}

rg_bluetooth_adapters() {
    local found=''
    if [ -d "$RG_SYS_BLUETOOTH_DIR" ]; then
        local dev
        for dev in "$RG_SYS_BLUETOOTH_DIR"/hci*; do
            [ -e "$dev" ] || continue
            found="${found}${found:+ }$(basename -- "$dev")"
        done
    fi
    [ -n "$found" ] || return 1
    printf '%s' "$found"
}

rg_libbluetooth_path() {
    local candidate
    if rg_have ldconfig; then
        candidate="$(ldconfig -p 2>/dev/null | awk '/libbluetooth\.so\.3/ {print $NF; exit}')"
        [ -n "$candidate" ] && [ -e "$candidate" ] && { printf '%s' "$candidate"; return 0; }
    fi
    for candidate in /usr/lib64/libbluetooth.so.3 \
                     /usr/lib/libbluetooth.so.3 \
                     /usr/lib/x86_64-linux-gnu/libbluetooth.so.3 \
                     /usr/lib/aarch64-linux-gnu/libbluetooth.so.3 \
                     /lib64/libbluetooth.so.3 \
                     /usr/local/lib/libbluetooth.so.3; do
        [ -e "$candidate" ] && { printf '%s' "$candidate"; return 0; }
    done
    return 1
}

rg_sdp_socket_state() {
    [ -e "$RG_SDP_SOCKET" ] || return 1
    stat -c '%U:%G %a' "$RG_SDP_SOCKET" 2>/dev/null
}

rg_sdp_socket_ok() {
    local state; state="$(rg_sdp_socket_state 2>/dev/null || true)"
    [ -n "$state" ] || return 1
    [ "$state" = "root:${RG_GROUP} 660" ]
}

# --------------------------------------------------------------------------
# Users and groups
# --------------------------------------------------------------------------
rg_group_exists() { getent group "$1" >/dev/null 2>&1; }

rg_user_exists() { getent passwd "$1" >/dev/null 2>&1; }

rg_user_in_group() {
    local user="$1" group="$2"
    id -nG "$user" 2>/dev/null | tr ' ' '\n' | grep -qx -- "$group"
}

# Group membership active in the *current* process (needs re-login otherwise).
rg_session_in_group() {
    id -nG 2>/dev/null | tr ' ' '\n' | grep -qx -- "$1"
}

# Home directory of a user, empty when unknown.
rg_user_home() {
    [ -n "${1:-}" ] || return 1
    local home
    home="$(getent passwd "$1" 2>/dev/null | cut -d: -f6 || true)"
    [ -n "$home" ] || return 1
    printf '%s' "$home"
}

# The human user the configuration is for, even when running under sudo.
rg_target_user() {
    local user="${RG_TARGET_USER:-}"
    if [ -z "$user" ] && [ -n "${SUDO_USER:-}" ] && [ "${SUDO_USER}" != "root" ]; then
        user="$SUDO_USER"
    fi
    if [ -z "$user" ] && [ -n "${PKEXEC_UID:-}" ]; then
        user="$(getent passwd "$PKEXEC_UID" 2>/dev/null | cut -d: -f1 || true)"
    fi
    if [ -z "$user" ] && [ "$(id -u)" -ne 0 ]; then
        user="$(id -un)"
    fi
    if [ -z "$user" ] && rg_have logname; then
        user="$(logname 2>/dev/null || true)"
    fi
    [ -n "$user" ] || return 1
    printf '%s' "$user"
}

rg_is_root() { [ "$(id -u)" -eq 0 ]; }

# --------------------------------------------------------------------------
# Repository layout
# Callers set RG_SCRIPT_DIR before sourcing this library; RG_REPO_ROOT and
# RG_SERVER_DIR are derived from it so the scripts work from any directory.
# --------------------------------------------------------------------------
if [ -n "${RG_SCRIPT_DIR:-}" ]; then
    RG_REPO_ROOT="$(cd -- "${RG_SCRIPT_DIR}/.." && pwd)"
    RG_SERVER_DIR="${RG_REPO_ROOT}/Server/RemoteGamepadServer"
    RG_SERVER_TESTS_DIR="${RG_REPO_ROOT}/Server/RemoteGamepadServer.Tests"
fi

rg_repo_root()  { printf '%s' "${RG_REPO_ROOT:-}"; }
rg_server_dir() { printf '%s' "${RG_SERVER_DIR:-}"; }
