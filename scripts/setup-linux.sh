#!/usr/bin/env bash
#
# RemoteGamepad — portable Linux setup
#
# Prepares any systemd-based Linux machine (Fedora/RHEL, Debian/Ubuntu,
# Arch, openSUSE and derivatives) to run the unprivileged RemoteGamepad
# server with:
#
#   * persistent /dev/uinput access for a dedicated "remote-gamepad" group,
#   * the uinput module loaded at boot,
#   * BlueZ compatibility (SDP) mode enabled through a systemd drop-in,
#   * persistent root:remote-gamepad 0660 permissions on /run/sdp across
#     reboots and "systemctl restart bluetooth".
#
# The script is idempotent, safe to re-run, never edits vendor unit files and
# works on Wi-Fi-only machines that have no Bluetooth hardware at all.
#
# Usage: sudo ./scripts/setup-linux.sh [options]   (see --help)
set -euo pipefail

RG_SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source-path=SCRIPTDIR source=lib/remote-gamepad-linux.sh
. "${RG_SCRIPT_DIR}/lib/remote-gamepad-linux.sh"

RG_ORIGINAL_ARGS=("$@")
ASSUME_YES=0
DO_INSTALL=1
DO_BLUETOOTH=1
DO_SDP_TOOLS=1
RESTART_BLUETOOTH=auto
RG_TARGET_USER="${RG_TARGET_USER:-}"

usage() {
    cat <<EOF
${RG_PROJECT_NAME} Linux setup

Usage: sudo $0 [options]

Options:
  -y, --yes               Non-interactive: accept package installs and the
                          Bluetooth service restart without prompting.
      --user NAME         Account to add to the '${RG_GROUP}' group
                          (default: the user behind sudo).
      --group NAME        Use a different dedicated group name
                          (default: ${RG_GROUP}).
      --no-install        Never install distribution packages; only report
                          what is missing.
      --no-bluetooth      Wi-Fi/UDP only: skip all BlueZ configuration.
      --no-sdp-tools      Do not install the optional deprecated BlueZ tools
                          (sdptool) used for verification.
      --no-restart-bluetooth
                          Apply the Bluetooth configuration but never restart
                          bluetooth.service (takes effect on next restart).
      --restart-bluetooth Always restart bluetooth.service when configuration
                          changed, even without an adapter present.
      --dry-run           Print every change without touching the system.
                          Can be run without root.
  -h, --help              Show this help.

Examples:
  sudo ./scripts/setup-linux.sh                 # interactive, full setup
  sudo ./scripts/setup-linux.sh --yes           # unattended
  sudo ./scripts/setup-linux.sh --no-bluetooth  # Wi-Fi-only machine
  ./scripts/setup-linux.sh --dry-run            # show planned changes only
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        -y|--yes)                ASSUME_YES=1 ;;
        --user)                  RG_TARGET_USER="${2:-}"; shift ;;
        --user=*)                RG_TARGET_USER="${1#*=}" ;;
        --group)                 RG_GROUP="${2:-}"; shift ;;
        --group=*)               RG_GROUP="${1#*=}" ;;
        --no-install)            DO_INSTALL=0 ;;
        --no-bluetooth)          DO_BLUETOOTH=0 ;;
        --no-sdp-tools)          DO_SDP_TOOLS=0 ;;
        --no-restart-bluetooth)  RESTART_BLUETOOTH=never ;;
        --restart-bluetooth)     RESTART_BLUETOOTH=always ;;
        --dry-run)               RG_DRY_RUN=1 ;;  # consumed by the library
        -h|--help)               usage; exit 0 ;;
        *) rg_err "Unknown option: $1"; usage >&2; exit 2 ;;
    esac
    shift
done

rg_require_linux

# --------------------------------------------------------------------------
# Privilege handling
# --------------------------------------------------------------------------
if ! rg_is_root; then
    if rg_dry_run; then
        rg_warn "Running --dry-run without root: some detection results may be incomplete."
    elif [ -n "$RG_PREFIX" ]; then
        # Scratch-prefix mode (repository self-tests): nothing outside the
        # prefix is written, so no privilege escalation is attempted.
        rg_warn "REMOTE_GAMEPAD_PREFIX is set; configuring ${RG_PREFIX} without root."
    elif rg_have sudo; then
        rg_step "Re-running with sudo..."
        exec sudo -- "$0" ${RG_ORIGINAL_ARGS[@]+"${RG_ORIGINAL_ARGS[@]}"}
    else
        rg_die "Root privileges are required. Run: su -c '$0 $*'"
    fi
fi

WARNINGS=0
note_warn() { WARNINGS=$((WARNINGS + 1)); rg_warn "$*"; }

confirm() {
    local prompt="$1"
    [ "$ASSUME_YES" = "1" ] && return 0
    if [ ! -t 0 ]; then
        rg_dim "   (non-interactive shell, assuming 'no' — re-run with --yes to accept)"
        return 1
    fi
    local answer=''
    read -r -p "$prompt [y/N] " answer || return 1
    case "${answer,,}" in y|yes) return 0 ;; *) return 1 ;; esac
}

# --------------------------------------------------------------------------
# 1. System detection
# --------------------------------------------------------------------------
rg_section "System detection"
rg_load_os_release
FAMILY="$(rg_distro_family)"
PKG="$(rg_package_manager)"
ARCH="$(rg_arch)"

rg_field "Distribution" "${RG_OS_NAME} (ID=${RG_OS_ID}${RG_OS_VERSION:+, ${RG_OS_VERSION}})"
rg_field "Distro family" "$FAMILY"
rg_field "Kernel" "$(uname -r)"
if rg_arch_supported; then
    rg_field "Architecture" "$ARCH" ok
else
    rg_field "Architecture" "$ARCH (untested; .NET packages may be unavailable)" warn
    WARNINGS=$((WARNINGS + 1))
fi
rg_field "Package manager" "$PKG"

if rg_systemd_running; then
    rg_field "systemd" "running (version $(rg_systemd_version 2>/dev/null || echo '?'))" ok
elif rg_have systemctl; then
    rg_field "systemd" "installed but not the running init (container?)" warn
    note_warn "systemd is not running: units are installed but cannot be started/enabled now."
else
    rg_field "systemd" "not found" fail
    note_warn "No systemd detected. udev rules and modules-load.d still apply, but the SDP watcher and the Bluetooth drop-in need systemd."
fi

TARGET_USER="$(rg_target_user 2>/dev/null || true)"
if [ -z "$TARGET_USER" ] || ! rg_user_exists "$TARGET_USER"; then
    rg_field "Target user" "${TARGET_USER:-<unknown>}" fail
    rg_die "Could not determine the user to configure. Re-run with --user <name>."
fi
rg_field "Target user" "$TARGET_USER" ok
rg_field "Dedicated group" "$RG_GROUP"

# Component detection (used both for reporting and for package selection).
DOTNET_VERSION="$(rg_dotnet_sdk_version 2>/dev/null || true)"
BLUETOOTHD="$(rg_find_bluetoothd 2>/dev/null || true)"
LIBBLUETOOTH="$(rg_libbluetooth_path 2>/dev/null || true)"
ADAPTERS="$(rg_bluetooth_adapters 2>/dev/null || true)"

rg_section "Component detection"
if rg_have dotnet; then
    if rg_dotnet_sdk_ok; then
        rg_field ".NET SDK" "${DOTNET_VERSION:-unknown} ($(rg_dotnet_path))" ok
    else
        rg_field ".NET SDK" "${DOTNET_VERSION:-none installed} (need ${RG_REQUIRED_DOTNET_MAJOR}.x)" warn
    fi
else
    rg_field ".NET SDK" "not installed (need ${RG_REQUIRED_DOTNET_MAJOR}.x)" warn
fi
rg_field "bluetoothd" "${BLUETOOTHD:-not found}" "$([ -n "$BLUETOOTHD" ] && echo ok || echo warn)"
rg_field "libbluetooth.so.3" "${LIBBLUETOOTH:-not found}" "$([ -n "$LIBBLUETOOTH" ] && echo ok || echo warn)"
rg_field "bluetoothctl" "$(command -v bluetoothctl 2>/dev/null || echo 'not found')"
rg_field "sdptool" "$(command -v sdptool 2>/dev/null || echo 'not found (optional)')"
rg_field "Bluetooth adapter" "${ADAPTERS:-none detected (Wi-Fi-only mode)}"
rg_field "/dev/uinput" "$(rg_uinput_exists && rg_uinput_owner || echo 'missing (module not loaded yet)')"

BLUETOOTH_SKIP_REASON=''
if [ "$DO_BLUETOOTH" = "0" ]; then
    BLUETOOTH_SKIP_REASON='--no-bluetooth was given'
elif [ -z "$BLUETOOTHD" ] && [ "$DO_INSTALL" = "0" ]; then
    note_warn "BlueZ is not installed and --no-install was given: continuing in Wi-Fi-only mode."
    DO_BLUETOOTH=0
    BLUETOOTH_SKIP_REASON='BlueZ is not installed and --no-install was given'
fi

# --------------------------------------------------------------------------
# 2. Packages
# --------------------------------------------------------------------------
pkg_installed() {
    case "$PKG" in
        dnf|yum|zypper) rpm -q "$1" >/dev/null 2>&1 ;;
        apt)            dpkg-query -W -f='${Status}' "$1" 2>/dev/null | grep -q 'ok installed' ;;
        pacman)         pacman -Qi "$1" >/dev/null 2>&1 ;;
        *)              return 1 ;;
    esac
}

pkg_available() {
    case "$PKG" in
        dnf)    dnf -q list --available "$1" >/dev/null 2>&1 || dnf -q list "$1" >/dev/null 2>&1 ;;
        yum)    yum -q list "$1" >/dev/null 2>&1 ;;
        apt)    [ -n "$(apt-cache policy "$1" 2>/dev/null | awk '/Candidate:/ && $2 != "(none)" {print $2}')" ] ;;
        pacman) pacman -Si "$1" >/dev/null 2>&1 ;;
        zypper) zypper --non-interactive search --match-exact "$1" >/dev/null 2>&1 ;;
        *)      return 1 ;;
    esac
}

pkg_install() {
    [ $# -gt 0 ] || return 0
    case "$PKG" in
        dnf)    rg_run dnf install -y "$@" ;;
        yum)    rg_run yum install -y "$@" ;;
        apt)    rg_run env DEBIAN_FRONTEND=noninteractive apt-get install -y "$@" ;;
        pacman) rg_run pacman -S --needed --noconfirm "$@" ;;
        zypper) rg_run zypper --non-interactive install "$@" ;;
        *)      return 1 ;;
    esac
}

APT_REFRESHED=0
pkg_refresh() {
    case "$PKG" in
        apt)
            [ "$APT_REFRESHED" = "1" ] && return 0
            APT_REFRESHED=1
            rg_run env DEBIAN_FRONTEND=noninteractive apt-get update ;;
        # Never "pacman -Sy" on its own: a partial upgrade breaks Arch systems.
        # The existing sync database is used as-is instead.
        pacman) rg_dim "   (Arch: using the current sync database; run 'sudo pacman -Syu' first if it is stale)"; return 0 ;;
        *) return 0 ;;
    esac
}

# Package names per distro family. Everything here is looked up before use, so
# an unknown or renamed package is reported instead of failing the run.
case "$FAMILY" in
    fedora) PKG_BLUEZ="bluez";  PKG_BLUEZ_LIB="bluez-libs";    PKG_BLUEZ_TOOLS="bluez";       PKG_SDP_TOOLS="bluez-deprecated";      DOTNET_CANDIDATES=("dotnet-sdk-10.0") ;;
    debian) PKG_BLUEZ="bluez";  PKG_BLUEZ_LIB="libbluetooth3"; PKG_BLUEZ_TOOLS="bluez";       PKG_SDP_TOOLS="bluez";                 DOTNET_CANDIDATES=("dotnet-sdk-10.0") ;;
    arch)   PKG_BLUEZ="bluez";  PKG_BLUEZ_LIB="bluez-libs";    PKG_BLUEZ_TOOLS="bluez-utils"; PKG_SDP_TOOLS="bluez-deprecated-tools"; DOTNET_CANDIDATES=("dotnet-sdk-10.0" "dotnet-sdk") ;;
    suse)   PKG_BLUEZ="bluez";  PKG_BLUEZ_LIB="libbluetooth3"; PKG_BLUEZ_TOOLS="bluez";       PKG_SDP_TOOLS="bluez";                 DOTNET_CANDIDATES=("dotnet-sdk-10.0") ;;
    *)      PKG_BLUEZ="bluez";  PKG_BLUEZ_LIB="";              PKG_BLUEZ_TOOLS="";            PKG_SDP_TOOLS="";                      DOTNET_CANDIDATES=("dotnet-sdk-10.0") ;;
esac

rg_section "Packages"
WANTED=()
UNAVAILABLE=0
REPORTED_UNAVAILABLE=''
add_package() {
    local pkg="$1" reason="$2"
    [ -n "$pkg" ] || return 0
    if pkg_installed "$pkg"; then return 0; fi
    if ! pkg_available "$pkg"; then
        case " $REPORTED_UNAVAILABLE " in
            *" $pkg "*) return 0 ;;
        esac
        REPORTED_UNAVAILABLE="${REPORTED_UNAVAILABLE} ${pkg}"
        UNAVAILABLE=$((UNAVAILABLE + 1))
        note_warn "Package '$pkg' (${reason}) is not available from $PKG on this system; install it manually."
        return 0
    fi
    # Avoid listing the same package twice (e.g. bluez provides several tools).
    local existing
    for existing in ${WANTED[@]+"${WANTED[@]}"}; do
        [ "$existing" = "$pkg" ] && return 0
    done
    WANTED+=("$pkg")
}

if [ "$PKG" = "none" ]; then
    note_warn "No supported package manager found (dnf/apt/pacman/zypper). Install .NET ${RG_REQUIRED_DOTNET_MAJOR} SDK and BlueZ manually."
elif [ "$DO_INSTALL" = "0" ]; then
    rg_info "Package installation disabled (--no-install)."
    rg_dotnet_sdk_ok || note_warn ".NET ${RG_REQUIRED_DOTNET_MAJOR} SDK is missing; the server cannot be built until it is installed."
    if [ "$DO_BLUETOOTH" = "1" ] && { [ -z "$BLUETOOTHD" ] || [ -z "$LIBBLUETOOTH" ]; }; then
        note_warn "BlueZ runtime (bluetoothd / libbluetooth.so.3) incomplete; Bluetooth transport will be unavailable."
    fi
else
    # apt answers availability questions from its cached index, so refresh it
    # first when something is actually missing.
    if [ "$PKG" = "apt" ] && { ! rg_dotnet_sdk_ok || [ -z "$BLUETOOTHD" ] || [ -z "$LIBBLUETOOTH" ] || ! rg_have bluetoothctl; }; then
        if confirm "Refresh the apt package index (apt-get update)?"; then
            pkg_refresh || note_warn "apt-get update failed; continuing with the cached index."
        fi
    fi
    if ! rg_dotnet_sdk_ok; then
        dotnet_pkg=''
        for candidate in "${DOTNET_CANDIDATES[@]}"; do
            if pkg_installed "$candidate" || pkg_available "$candidate"; then dotnet_pkg="$candidate"; break; fi
        done
        if [ -n "$dotnet_pkg" ]; then
            add_package "$dotnet_pkg" ".NET SDK"
        else
            note_warn ".NET ${RG_REQUIRED_DOTNET_MAJOR} SDK package not found in your repositories. Install it from your distribution's .NET feed or with: curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel ${RG_REQUIRED_DOTNET_MAJOR}.0"
        fi
    fi
    if [ "$DO_BLUETOOTH" = "1" ]; then
        [ -n "$BLUETOOTHD" ]    || add_package "$PKG_BLUEZ" "bluetoothd"
        [ -n "$LIBBLUETOOTH" ]  || add_package "$PKG_BLUEZ_LIB" "libbluetooth.so.3"
        rg_have bluetoothctl    || add_package "$PKG_BLUEZ_TOOLS" "bluetoothctl"
        if [ "$DO_SDP_TOOLS" = "1" ] && ! rg_have sdptool; then
            add_package "$PKG_SDP_TOOLS" "sdptool (optional verification tool)"
        fi
    fi

    if [ ${#WANTED[@]} -eq 0 ] && [ "$UNAVAILABLE" -gt 0 ]; then
        rg_warn "${UNAVAILABLE} required package(s) could not be found in the configured repositories (see above)."
    elif [ ${#WANTED[@]} -eq 0 ]; then
        rg_ok "All required distribution packages are present."
    else
        rg_info "Packages to install via $PKG: ${WANTED[*]}"
        if confirm "Install these packages now?"; then
            pkg_refresh || note_warn "Package index refresh failed; continuing with the cached index."
            if pkg_install "${WANTED[@]}"; then
                rg_done "Package installation finished."
            else
                note_warn "Package installation failed; install ${WANTED[*]} manually and re-run."
            fi
            # Refresh detection after installing.
            DOTNET_VERSION="$(rg_dotnet_sdk_version 2>/dev/null || true)"
            BLUETOOTHD="$(rg_find_bluetoothd 2>/dev/null || true)"
            LIBBLUETOOTH="$(rg_libbluetooth_path 2>/dev/null || true)"
        else
            note_warn "Skipped package installation: ${WANTED[*]}"
        fi
    fi
fi

# --------------------------------------------------------------------------
# 3. Dedicated group
# --------------------------------------------------------------------------
rg_section "Dedicated '${RG_GROUP}' group"
if rg_group_exists "$RG_GROUP"; then
    rg_ok "Group '${RG_GROUP}' already exists (gid $(getent group "$RG_GROUP" | cut -d: -f3))."
else
    if rg_have groupadd; then
        rg_run groupadd --system "$RG_GROUP" || rg_die "groupadd --system ${RG_GROUP} failed."
    elif rg_have addgroup; then
        rg_run addgroup --system "$RG_GROUP" || rg_die "addgroup --system ${RG_GROUP} failed."
    else
        rg_die "Neither groupadd nor addgroup is available; cannot create the '${RG_GROUP}' group."
    fi
    rg_done "Created system group '${RG_GROUP}'."
fi

RELOGIN_REQUIRED=0
if rg_user_in_group "$TARGET_USER" "$RG_GROUP"; then
    rg_ok "User '${TARGET_USER}' is already a member of '${RG_GROUP}'."
else
    if rg_have usermod; then
        rg_run usermod -aG "$RG_GROUP" "$TARGET_USER" || rg_die "Could not add '${TARGET_USER}' to '${RG_GROUP}'."
    elif rg_have gpasswd; then
        rg_run gpasswd -a "$TARGET_USER" "$RG_GROUP" || rg_die "Could not add '${TARGET_USER}' to '${RG_GROUP}'."
    else
        rg_die "Neither usermod nor gpasswd is available; add '${TARGET_USER}' to '${RG_GROUP}' manually."
    fi
    rg_done "Added '${TARGET_USER}' to '${RG_GROUP}'."
    RELOGIN_REQUIRED=1
fi

# Debian/Ubuntu ship a 'bluetooth' group used by the BlueZ D-Bus policy. It is
# NOT assumed to exist: join it only when the distribution actually created it.
if rg_group_exists bluetooth; then
    if rg_user_in_group "$TARGET_USER" bluetooth; then
        rg_ok "User '${TARGET_USER}' is already in this distribution's 'bluetooth' group."
    else
        if rg_run usermod -aG bluetooth "$TARGET_USER"; then
            rg_done "Added '${TARGET_USER}' to the distribution's 'bluetooth' group (BlueZ D-Bus policy)."
            RELOGIN_REQUIRED=1
        fi
    fi
else
    rg_dim "   No 'bluetooth' group on this distribution — nothing to join (expected on Fedora/Arch)."
fi

# --------------------------------------------------------------------------
# 4. uinput: module at boot + persistent device permissions
# --------------------------------------------------------------------------
rg_section "uinput (/dev/uinput)"

rg_install_file "$RG_MODULES_CONF" 0644 <<EOF
# Installed by ${RG_PROJECT_NAME} scripts/setup-linux.sh — load uinput at boot.
uinput
EOF

rg_install_file "$RG_UDEV_RULE" 0644 <<EOF
# Installed by ${RG_PROJECT_NAME} scripts/setup-linux.sh.
# Persistent, reboot-safe /dev/uinput access for the dedicated group.
KERNEL=="uinput", SUBSYSTEM=="misc", GROUP="${RG_GROUP}", MODE="0660", OPTIONS+="static_node=uinput"
EOF

# A rule created by the old README instructions would sort after ours and win.
if [ -f "$RG_LEGACY_UDEV_RULE" ]; then
    if [ "$(tr -d '[:space:]' < "$RG_LEGACY_UDEV_RULE")" = "$(printf '%s' "$RG_LEGACY_UDEV_CONTENT" | tr -d '[:space:]')" ]; then
        rg_remove_file "$RG_LEGACY_UDEV_RULE"
        rg_dim "   (removed the superseded rule from the previous RemoteGamepad instructions)"
    else
        note_warn "$RG_LEGACY_UDEV_RULE exists and may override ${RG_UDEV_RULE}; review it manually."
    fi
fi
for legacy in ${RG_LEGACY_PATHS[@]+"${RG_LEGACY_PATHS[@]}"}; do
    if [ -e "$legacy" ]; then rg_remove_file "$legacy"; fi
done

if rg_have modprobe; then
    if [ "$(rg_uinput_module_state)" = "not-loaded" ]; then
        if rg_run modprobe uinput; then
            rg_done "Loaded the uinput kernel module."
        else
            note_warn "modprobe uinput failed. The module may be built into the kernel, or your kernel lacks CONFIG_INPUT_UINPUT."
        fi
    else
        rg_ok "uinput is available ($(rg_uinput_module_state))."
    fi
else
    note_warn "modprobe not found; cannot load uinput now (it will load at boot via ${RG_MODULES_CONF})."
fi

if rg_have udevadm; then
    rg_run udevadm control --reload-rules || note_warn "udevadm control --reload-rules failed."
    rg_run udevadm trigger --subsystem-match=misc --action=add || note_warn "udevadm trigger failed."
    rg_run udevadm settle --timeout=5 || true
else
    note_warn "udevadm not found; reboot for the udev rule to take effect."
fi

if rg_uinput_exists; then
    # Apply the target ownership immediately for the current boot as well; the
    # udev rule is what makes it survive a reboot.
    current_owner="$(rg_uinput_owner || true)"
    if [ "$current_owner" != "root:${RG_GROUP} 660" ]; then
        rg_run chgrp "$RG_GROUP" "$RG_UINPUT_DEV" || note_warn "Could not set the group on ${RG_UINPUT_DEV}."
        rg_run chmod 0660 "$RG_UINPUT_DEV" || note_warn "Could not set the mode on ${RG_UINPUT_DEV}."
    fi
    rg_ok "${RG_UINPUT_DEV} is $(rg_uinput_owner || echo 'unavailable')."
elif ! rg_dry_run; then
    note_warn "/dev/uinput does not exist yet. It appears after the uinput module loads (reboot if modprobe failed)."
fi

# --------------------------------------------------------------------------
# 5. BlueZ compatibility SDP mode + persistent /run/sdp permissions
# --------------------------------------------------------------------------
BLUETOOTH_CHANGED=0
SDP_CONFIGURED=0

if [ "$DO_BLUETOOTH" = "0" ]; then
    rg_section "Bluetooth"
    rg_info "Skipped (${BLUETOOTH_SKIP_REASON:---no-bluetooth was given}). Wi-Fi/UDP transport is fully functional without it."
elif [ -z "$BLUETOOTHD" ]; then
    rg_section "Bluetooth"
    note_warn "bluetoothd was not found. Skipping Bluetooth configuration; the server still works over Wi-Fi/UDP."
    rg_info "Install BlueZ and re-run this script to enable the Bluetooth transport."
else
    rg_section "Bluetooth (BlueZ compatibility SDP mode)"
    rg_field "bluetoothd" "$BLUETOOTHD" ok
    rg_field "BlueZ version" "$(rg_bluetoothd_version 2>/dev/null || echo unknown)"
    if rg_unit_exists bluetooth.service; then
        rg_field "bluetooth.service" "$(rg_unit_state bluetooth.service)"
    else
        rg_field "bluetooth.service" "unit not present" warn
    fi
    rg_field "Adapter(s)" "${ADAPTERS:-none detected}"

    if ! rg_bluetoothd_supports_compat; then
        note_warn "This bluetoothd build does not advertise --compat; SDP registration may be unavailable."
    fi

    # 5a. Compatibility mode drop-in (never touches the vendor unit file).
    vendor_exec="$(rg_bluetooth_vendor_execstart 2>/dev/null || true)"
    if [ -z "$vendor_exec" ]; then
        vendor_exec="$BLUETOOTHD"
        [ -n "$(rg_bluetooth_fragment_path 2>/dev/null || true)" ] &&
            note_warn "Could not read bluetooth.service's vendor ExecStart; using the detected daemon path."
    fi
    vendor_has_compat=0
    case " $vendor_exec " in
        *" --compat "*|*" -C "*) vendor_has_compat=1 ;;
    esac
    rg_field "Vendor ExecStart" "$vendor_exec"

    dropin_changes_before="$RG_CHANGES"
    if ! rg_have systemctl; then
        note_warn "systemctl is unavailable; start bluetoothd with --compat yourself."
    elif [ "$vendor_has_compat" = "1" ]; then
        rg_ok "Compatibility SDP mode is already enabled by this distribution; no drop-in needed."
        if [ -f "$RG_BT_DROPIN" ]; then
            rg_remove_file "$RG_BT_DROPIN"
            rg_dim "   (removed our now-redundant drop-in)"
        fi
    else
        rg_install_file "$RG_BT_DROPIN" 0644 <<EOF
# Installed by ${RG_PROJECT_NAME} scripts/setup-linux.sh.
# Enables BlueZ compatibility mode (/run/sdp) required for local SDP record
# registration. This is a drop-in: the vendor unit file is left untouched.
# Remove with scripts/uninstall-linux.sh.
[Service]
ExecStart=
ExecStart=${vendor_exec} --compat
EOF
    fi
    # Only a real configuration change justifies restarting the daemon.
    [ "$RG_CHANGES" -ne "$dropin_changes_before" ] && BLUETOOTH_CHANGED=1

    # Warn about other drop-ins that also try to manage the SDP socket, so two
    # mechanisms never fight over /run/sdp.
    if [ -d "$RG_BT_DROPIN_DIR" ]; then
        for other in "$RG_BT_DROPIN_DIR"/*.conf; do
            [ -f "$other" ] || continue
            [ "$other" = "$RG_BT_DROPIN" ] && continue
            if grep -qE 'sdp|chmod|chgrp|chown' -- "$other" 2>/dev/null; then
                note_warn "Another drop-in also touches the SDP socket: ${other}. Remove it to avoid competing mechanisms."
            fi
        done
    fi

    # 5b. Single, event-driven /run/sdp permission watcher.
    rg_install_file "$RG_SDP_HELPER" 0755 < "${RG_SCRIPT_DIR}/remote-gamepad-fix-sdp-permissions"
    rg_install_file "${RG_SYSTEMD_UNIT_DIR}/${RG_SDP_SERVICE_UNIT}" 0644 < <(
        sed "s|^Environment=REMOTE_GAMEPAD_SDP_GROUP=.*|Environment=REMOTE_GAMEPAD_SDP_GROUP=${RG_GROUP}|" \
            "${RG_REPO_ROOT}/systemd/${RG_SDP_SERVICE_UNIT}"
    )
    rg_install_file "${RG_SYSTEMD_UNIT_DIR}/${RG_SDP_PATH_UNIT}" 0644 < "${RG_REPO_ROOT}/systemd/${RG_SDP_PATH_UNIT}"
    SDP_CONFIGURED=1

    rg_daemon_reload
    if rg_systemd_running; then
        rg_run systemctl enable "$RG_SDP_PATH_UNIT" || note_warn "Could not enable ${RG_SDP_PATH_UNIT}."
        rg_run systemctl restart "$RG_SDP_PATH_UNIT" || note_warn "Could not start ${RG_SDP_PATH_UNIT}."
        rg_done "SDP permission watcher armed ($(rg_unit_state "$RG_SDP_PATH_UNIT"))."
    else
        rg_dim "   systemd is not running; the watcher will arm on the next boot."
    fi

    # 5c. Apply now: restart bluetoothd so compat mode and the socket exist.
    do_restart=0
    case "$RESTART_BLUETOOTH" in
        never)  do_restart=0 ;;
        always) do_restart=1 ;;
        auto)
            if [ -z "$ADAPTERS" ]; then
                [ "$BLUETOOTH_CHANGED" = "1" ] &&
                    rg_dim "   No Bluetooth adapter detected; not restarting bluetooth.service."
            elif [ "$BLUETOOTH_CHANGED" = "1" ]; then
                do_restart=1
            elif rg_unit_active bluetooth.service; then
                # Configuration unchanged: only restart when the daemon that is
                # actually running predates compatibility mode.
                running_compat=0
                rg_bluetoothd_running_compat || running_compat=$?
                case "$running_compat" in
                    1) do_restart=1
                       rg_dim "   The running bluetoothd was started without --compat." ;;
                    2) [ -S "$RG_SDP_SOCKET" ] ||
                           rg_dim "   Could not inspect the running bluetoothd; restart bluetooth.service manually if ${RG_SDP_SOCKET} stays missing." ;;
                esac
            fi
            ;;
    esac

    if [ "$do_restart" = "1" ] && rg_systemd_running; then
        if confirm "Restart bluetooth.service now to apply compatibility mode?"; then
            rg_run systemctl restart bluetooth.service || note_warn "Restarting bluetooth.service failed; check: systemctl status bluetooth"
            sleep 1
        else
            note_warn "bluetooth.service was not restarted; compatibility mode applies after the next restart or reboot."
        fi
    elif [ "$do_restart" = "1" ]; then
        note_warn "systemd is not running; restart Bluetooth manually later."
    fi

    # 5d. Fix the socket for the currently running daemon (the watcher handles
    #     every later start/restart).
    if rg_systemd_running && [ -x "$RG_SDP_HELPER" ] && ! rg_dry_run; then
        REMOTE_GAMEPAD_SDP_GROUP="$RG_GROUP" "$RG_SDP_HELPER" || true
    fi

    if [ -S "$RG_SDP_SOCKET" ]; then
        if rg_sdp_socket_ok; then
            rg_ok "${RG_SDP_SOCKET} is $(rg_sdp_socket_state) (persisted by ${RG_SDP_PATH_UNIT})."
        else
            note_warn "${RG_SDP_SOCKET} is $(rg_sdp_socket_state); expected root:${RG_GROUP} 660. Check: journalctl -u ${RG_SDP_SERVICE_UNIT} -b"
        fi
    elif [ -n "$ADAPTERS" ]; then
        rg_dim "   ${RG_SDP_SOCKET} does not exist yet; it appears when bluetoothd runs in compatibility mode."
    else
        rg_dim "   No adapter and no ${RG_SDP_SOCKET} socket — expected on a Wi-Fi-only machine."
    fi
fi

# --------------------------------------------------------------------------
# 6. Record what was installed (used by check/uninstall)
# --------------------------------------------------------------------------
rg_install_file "$RG_STATE_FILE" 0644 <<EOF
# ${RG_PROJECT_NAME} Linux setup state — written by scripts/setup-linux.sh.
# Remove with scripts/uninstall-linux.sh.
REMOTE_GAMEPAD_GROUP=${RG_GROUP}
REMOTE_GAMEPAD_USER=${TARGET_USER}
REMOTE_GAMEPAD_DISTRO=${RG_OS_ID}
REMOTE_GAMEPAD_FAMILY=${FAMILY}
REMOTE_GAMEPAD_BLUETOOTHD=${BLUETOOTHD:-}
REMOTE_GAMEPAD_BT_DROPIN=$([ -f "$RG_BT_DROPIN" ] && echo yes || echo no)
REMOTE_GAMEPAD_SDP_WATCHER=$([ "$SDP_CONFIGURED" = "1" ] && echo yes || echo no)
REMOTE_GAMEPAD_SETUP_VERSION=1
EOF

# --------------------------------------------------------------------------
# 7. Summary
# --------------------------------------------------------------------------
rg_section "Summary"
rg_info "Configured for user '${TARGET_USER}' on ${RG_OS_NAME} (${FAMILY} family, ${ARCH})."
rg_info "Files managed by ${RG_PROJECT_NAME}:"
rg_info "  ${RG_MODULES_CONF}"
rg_info "  ${RG_UDEV_RULE}"
if [ "$SDP_CONFIGURED" = "1" ]; then
    rg_info "  ${RG_SDP_HELPER}"
    rg_info "  ${RG_SYSTEMD_UNIT_DIR}/${RG_SDP_PATH_UNIT}"
    rg_info "  ${RG_SYSTEMD_UNIT_DIR}/${RG_SDP_SERVICE_UNIT}"
fi
if [ -f "$RG_BT_DROPIN" ]; then rg_info "  ${RG_BT_DROPIN}"; fi
rg_info "  ${RG_STATE_FILE}"

if [ "$RELOGIN_REQUIRED" = "1" ]; then
    echo
    rg_warn "Group membership changed: log out and back in (or reboot) so '${TARGET_USER}' picks up the '${RG_GROUP}' group."
    rg_info "A quick check without logging out:  sg ${RG_GROUP} -c 'id -nG'"
fi

echo
rg_info "Next steps:"
rg_info "  ./scripts/check-linux.sh          # verify the environment"
rg_info "  ./scripts/build-server-linux.sh   # restore + Release build"
rg_info "  ./scripts/test-linux.sh           # uinput self-test (no Bluetooth needed)"
rg_info "  ./scripts/run-server-linux.sh     # run as your normal user, never sudo"

if [ "$WARNINGS" -gt 0 ]; then
    echo
    rg_warn "Finished with ${WARNINGS} warning(s) — see above."
else
    echo
    rg_ok "Setup finished."
fi
if rg_dry_run; then rg_dim "(dry run: nothing was changed)"; fi
exit 0
