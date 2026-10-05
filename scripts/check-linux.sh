#!/usr/bin/env bash
#
# RemoteGamepad — Linux environment report
#
# Read-only diagnostics: distro, kernel, architecture, .NET, BlueZ,
# bluetoothd path/state, compatibility SDP mode, /dev/uinput, /run/sdp,
# group membership and the RemoteGamepad SDP registration.
#
# Never changes anything and never needs root (run it as the user that will
# run the server, so group membership is reported for the right session).
#
# Usage: ./scripts/check-linux.sh [--user NAME] [--strict]
set -uo pipefail

RG_SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source-path=SCRIPTDIR source=lib/remote-gamepad-linux.sh
. "${RG_SCRIPT_DIR}/lib/remote-gamepad-linux.sh"

STRICT=0
RG_TARGET_USER="${RG_TARGET_USER:-}"
while [ $# -gt 0 ]; do
    case "$1" in
        --user) RG_TARGET_USER="${2:-}"; shift ;;
        --user=*) RG_TARGET_USER="${1#*=}" ;;
        --strict) STRICT=1 ;;
        -h|--help)
            cat <<EOF
${RG_PROJECT_NAME} Linux environment report (read-only)

Usage: $0 [--user NAME] [--strict]

  --user NAME   Report group membership for NAME (default: the invoking user,
                or the user behind sudo).
  --strict      Exit non-zero on warnings too (Bluetooth problems included).

Exit codes: 0 = ready, 1 = blocking problem (2 with --strict and warnings).
EOF
            exit 0 ;;
        *) rg_err "Unknown option: $1"; exit 2 ;;
    esac
    shift
done

rg_require_linux

PROBLEMS=0
WARNINGS=0
problem() { PROBLEMS=$((PROBLEMS + 1)); }
warning() { WARNINGS=$((WARNINGS + 1)); }

USER_NAME="$(rg_target_user 2>/dev/null || id -un)"

printf '%s%s Linux environment report%s\n' "${RG_C_BOLD}" "$RG_PROJECT_NAME" "${RG_C_RESET}"

# --------------------------------------------------------------------------
rg_section "System"
rg_load_os_release
FAMILY="$(rg_distro_family)"
rg_field "Distribution" "${RG_OS_NAME} (ID=${RG_OS_ID}${RG_OS_VERSION:+, ${RG_OS_VERSION}})"
rg_field "Distro family" "$FAMILY"
rg_field "Kernel" "$(uname -r)"
if rg_arch_supported; then
    rg_field "Architecture" "$(rg_arch)" ok
else
    rg_field "Architecture" "$(rg_arch) (untested)" warn; warning
fi
rg_field "Package manager" "$(rg_package_manager)"
if rg_systemd_running; then
    rg_field "systemd" "running (version $(rg_systemd_version 2>/dev/null || echo '?'))" ok
elif rg_have systemctl; then
    rg_field "systemd" "installed, not running as init" warn; warning
else
    rg_field "systemd" "not found" warn; warning
fi
rg_field "Report for user" "$USER_NAME"

# --------------------------------------------------------------------------
rg_section ".NET"
if rg_have dotnet; then
    rg_field "dotnet" "$(rg_dotnet_path)" ok
    SDKS="$(dotnet --list-sdks 2>/dev/null | awk '{print $1}' | tr '\n' ' ' | sed 's/ $//')"
    rg_field "Installed SDKs" "${SDKS:-none}"
    if rg_dotnet_sdk_ok; then
        rg_field "SDK requirement" "net${RG_REQUIRED_DOTNET_MAJOR}.0 satisfied by $(rg_dotnet_sdk_version)" ok
    else
        rg_field "SDK requirement" "net${RG_REQUIRED_DOTNET_MAJOR}.0 NOT satisfied" fail; problem
    fi
    RUNTIMES="$(dotnet --list-runtimes 2>/dev/null | awk '{print $1"/"$2}' | tr '\n' ' ' | sed 's/ $//')"
    rg_field "Runtimes" "${RUNTIMES:-none}"
else
    rg_field "dotnet" "not installed" fail; problem
fi

# --------------------------------------------------------------------------
rg_section "uinput"
MODULE_STATE="$(rg_uinput_module_state)"
case "$MODULE_STATE" in
    loaded)            rg_field "Kernel module" "loaded" ok ;;
    builtin-or-loaded) rg_field "Kernel module" "available (built-in or loaded)" ok ;;
    *)                 rg_field "Kernel module" "not loaded" fail; problem ;;
esac
if rg_uinput_exists; then
    OWNER="$(rg_uinput_owner)"
    if [ "$OWNER" = "root:${RG_GROUP} 660" ]; then
        rg_field "/dev/uinput" "$OWNER" ok
    else
        rg_field "/dev/uinput" "$OWNER (expected root:${RG_GROUP} 660)" warn; warning
    fi
    if rg_uinput_writable; then
        rg_field "Writable by session" "yes" ok
    else
        rg_field "Writable by session" "no" fail; problem
    fi
else
    rg_field "/dev/uinput" "missing" fail; problem
fi
if [ -f "$RG_UDEV_RULE" ]; then
    rg_field "udev rule" "$RG_UDEV_RULE" ok
else
    rg_field "udev rule" "not installed (${RG_UDEV_RULE})" fail; problem
fi
if [ -f "$RG_MODULES_CONF" ]; then
    rg_field "Load at boot" "$RG_MODULES_CONF" ok
else
    rg_field "Load at boot" "not configured (${RG_MODULES_CONF})" warn; warning
fi
OTHER_RULES="$(grep -rl 'uinput' /etc/udev/rules.d 2>/dev/null | grep -v "^${RG_UDEV_RULE}$" | tr '\n' ' ' | sed 's/ $//')"
if [ -n "$OTHER_RULES" ]; then
    rg_field "Other uinput rules" "$OTHER_RULES (may override ours)" warn; warning
fi

# --------------------------------------------------------------------------
rg_section "Groups"
if rg_group_exists "$RG_GROUP"; then
    rg_field "Group '${RG_GROUP}'" "exists (gid $(getent group "$RG_GROUP" | cut -d: -f3))" ok
    if rg_user_in_group "$USER_NAME" "$RG_GROUP"; then
        rg_field "Membership" "${USER_NAME} is a member" ok
        if [ "$USER_NAME" = "$(id -un)" ] && ! rg_session_in_group "$RG_GROUP"; then
            rg_field "Active in session" "no — log out and back in" warn; warning
        elif [ "$USER_NAME" = "$(id -un)" ]; then
            rg_field "Active in session" "yes" ok
        fi
    else
        rg_field "Membership" "${USER_NAME} is NOT a member" fail; problem
    fi
else
    rg_field "Group '${RG_GROUP}'" "missing — run sudo ./scripts/setup-linux.sh" fail; problem
fi
if rg_group_exists bluetooth; then
    if rg_user_in_group "$USER_NAME" bluetooth; then
        rg_field "Distro 'bluetooth' grp" "${USER_NAME} is a member" ok
    else
        rg_field "Distro 'bluetooth' grp" "exists; ${USER_NAME} is not a member (BlueZ D-Bus policy may need it)" warn; warning
    fi
else
    rg_field "Distro 'bluetooth' grp" "not present on this distribution (fine)"
fi
rg_field "Session groups" "$(id -nG 2>/dev/null | tr ' ' ',')"

# --------------------------------------------------------------------------
rg_section "Bluetooth (optional — Wi-Fi works without it)"
BLUETOOTHD="$(rg_find_bluetoothd 2>/dev/null || true)"
if [ -n "$BLUETOOTHD" ]; then
    rg_field "bluetoothd" "$BLUETOOTHD" ok
    rg_field "BlueZ version" "$(rg_bluetoothd_version 2>/dev/null || echo unknown)"
else
    rg_field "bluetoothd" "not found (BlueZ not installed?)" warn; warning
fi
LIBBT="$(rg_libbluetooth_path 2>/dev/null || true)"
if [ -n "$LIBBT" ]; then
    rg_field "libbluetooth.so.3" "$LIBBT" ok
else
    rg_field "libbluetooth.so.3" "not found (needed for SDP registration)" warn; warning
fi
rg_field "bluetoothctl" "$(command -v bluetoothctl 2>/dev/null || echo 'not installed')"
rg_field "sdptool" "$(command -v sdptool 2>/dev/null || echo 'not installed (optional)')"

if rg_have systemctl; then
    if rg_unit_exists bluetooth.service; then
        rg_field "bluetooth.service" "$(rg_unit_state bluetooth.service)"
    else
        rg_field "bluetooth.service" "unit not present (BlueZ not installed?)"
    fi
    FRAGMENT="$(rg_bluetooth_fragment_path 2>/dev/null || true)"
    [ -n "$FRAGMENT" ] && rg_field "Vendor unit" "$FRAGMENT"
    EFFECTIVE="$(rg_bluetooth_effective_execstart 2>/dev/null || true)"
    [ -n "$EFFECTIVE" ] && rg_field "Effective ExecStart" "$EFFECTIVE"
fi
if rg_compat_mode_configured; then
    if [ -f "$RG_BT_DROPIN" ]; then
        rg_field "Compat SDP mode" "enabled via ${RG_BT_DROPIN}" ok
    else
        rg_field "Compat SDP mode" "enabled by the distribution" ok
    fi
elif [ -n "$BLUETOOTHD" ]; then
    rg_field "Compat SDP mode" "DISABLED (no --compat); Bluetooth SDP registration will fail" warn; warning
fi

ADAPTERS="$(rg_bluetooth_adapters 2>/dev/null || true)"
if [ -n "$ADAPTERS" ]; then
    rg_field "Adapter(s)" "$ADAPTERS" ok
    for hci in $ADAPTERS; do
        addr_file="/sys/class/bluetooth/${hci}/address"
        [ -r "$addr_file" ] && rg_field "  ${hci}" "$(cat "$addr_file" 2>/dev/null)"
    done
else
    rg_field "Adapter(s)" "none detected — Wi-Fi-only mode"
fi

if [ -e "$RG_SDP_SOCKET" ]; then
    if rg_sdp_socket_ok; then
        rg_field "${RG_SDP_SOCKET}" "$(rg_sdp_socket_state)" ok
    else
        rg_field "${RG_SDP_SOCKET}" "$(rg_sdp_socket_state) (expected root:${RG_GROUP} 660)" warn; warning
    fi
else
    rg_field "${RG_SDP_SOCKET}" "absent (bluetoothd not running in compat mode)"
fi

if rg_have systemctl; then
    if [ -f "${RG_SYSTEMD_UNIT_DIR}/${RG_SDP_PATH_UNIT}" ]; then
        rg_field "SDP watcher" "$(rg_unit_state "$RG_SDP_PATH_UNIT")" ok
        LAST="$(systemctl show -p ExecMainStatus --value "$RG_SDP_SERVICE_UNIT" 2>/dev/null || true)"
        [ -n "$LAST" ] && rg_field "  last helper exit" "$LAST"
    elif [ -n "$BLUETOOTHD" ]; then
        rg_field "SDP watcher" "not installed (${RG_SDP_PATH_UNIT})" warn; warning
    fi
fi
if [ -x "$RG_SDP_HELPER" ]; then
    rg_field "SDP helper" "$RG_SDP_HELPER" ok
fi

# Adapter visibility. Purely informational: the server needs RFCOMM + SDP, not
# discoverability, so nothing here is counted as a problem.
if [ -n "$ADAPTERS" ]; then
    DISC_STATUS="$(rg_discoverable_status || true)"
    if [ -n "$DISC_STATUS" ]; then
        DISCOVERABLE="$(rg_discoverable_field discoverable "$DISC_STATUS" || echo unknown)"
        POWERED="$(rg_discoverable_field powered "$DISC_STATUS" || echo unknown)"
        rg_field "Adapter powered" "$POWERED" "$([ "$POWERED" = "true" ] && echo ok || echo info)"
        case "$DISCOVERABLE" in
            true)  rg_field "Discoverable" "yes (phones can find this machine)" ok ;;
            false) rg_field "Discoverable" "no — fine for an already paired phone; 'sudo ./scripts/setup-linux.sh --discoverable' or 'bluetoothctl discoverable on' to change it" ;;
            *)     rg_field "Discoverable" "unknown (no busctl/dbus-send/bluetoothctl, or bluetoothd not running)" ;;
        esac
    fi
fi
if rg_have systemctl && [ -f "${RG_SYSTEMD_UNIT_DIR}/${RG_DISCOVERABLE_UNIT}" ]; then
    rg_field "Discoverable unit" "$(rg_unit_state "$RG_DISCOVERABLE_UNIT")"
fi

# RemoteGamepad SDP record — only observable while the server is running.
if rg_have sdptool; then
    if SDP_OUT="$(sdptool browse local 2>&1)"; then
        if printf '%s' "$SDP_OUT" | grep -qi 'RemoteGamepad'; then
            rg_field "SDP registration" "RemoteGamepad service record present" ok
        else
            rg_field "SDP registration" "not registered (server not running?)"
        fi
    else
        rg_field "SDP registration" "sdptool could not query the local SDP server: $(printf '%s' "$SDP_OUT" | head -1)" warn; warning
    fi
else
    rg_field "SDP registration" "sdptool not installed — cannot verify (optional)"
fi

# --------------------------------------------------------------------------
rg_section "Automatic startup"
SERVICE_UNIT_PATH="${RG_SYSTEMD_UNIT_DIR}/${RG_SERVICE_UNIT}"
if [ -f "$SERVICE_UNIT_PATH" ]; then
    rg_field "Service unit" "$SERVICE_UNIT_PATH" ok
    SVC_USER="$(sed -n 's/^User=//p' "$SERVICE_UNIT_PATH" | head -1)"
    SVC_EXEC="$(sed -n 's/^ExecStart=//p' "$SERVICE_UNIT_PATH" | head -1)"
    if [ "$SVC_USER" = "root" ]; then
        rg_field "Runs as" "root — the server is meant to run unprivileged" fail; problem
    else
        rg_field "Runs as" "${SVC_USER:-unknown}" "$([ -n "$SVC_USER" ] && echo ok || echo warn)"
    fi
    rg_field "ExecStart" "${SVC_EXEC:-unknown}"
    SVC_GROUPS="$(sed -n 's/^SupplementaryGroups=//p' "$SERVICE_UNIT_PATH" | head -1)"
    if [ -n "$SVC_GROUPS" ]; then
        rg_field "Supplementary groups" "$SVC_GROUPS"
    fi

    if rg_have systemctl; then
        if rg_unit_enabled "$RG_SERVICE_UNIT"; then
            rg_field "Starts at boot" "enabled" ok
        else
            rg_field "Starts at boot" "$(rg_unit_state "$RG_SERVICE_UNIT" 2>/dev/null || echo disabled) — sudo systemctl enable ${RG_SERVICE_UNIT}" warn; warning
        fi
        if rg_unit_active "$RG_SERVICE_UNIT"; then
            MAIN_PID="$(systemctl show -p MainPID --value "$RG_SERVICE_UNIT" 2>/dev/null || true)"
            SINCE="$(systemctl show -p ActiveEnterTimestamp --value "$RG_SERVICE_UNIT" 2>/dev/null || true)"
            rg_field "Status" "active (running)${MAIN_PID:+, PID ${MAIN_PID}}" ok
            if [ -n "$SINCE" ]; then
                rg_field "Active since" "$SINCE"
            fi
        else
            ACTIVE_STATE="$(systemctl show -p ActiveState --value "$RG_SERVICE_UNIT" 2>/dev/null || echo unknown)"
            SUB_STATE="$(systemctl show -p SubState --value "$RG_SERVICE_UNIT" 2>/dev/null || true)"
            RESULT="$(systemctl show -p Result --value "$RG_SERVICE_UNIT" 2>/dev/null || true)"
            EXIT_STATUS="$(systemctl show -p ExecMainStatus --value "$RG_SERVICE_UNIT" 2>/dev/null || true)"
            CONDITION="$(systemctl show -p ConditionResult --value "$RG_SERVICE_UNIT" 2>/dev/null || true)"
            NRESTARTS="$(systemctl show -p NRestarts --value "$RG_SERVICE_UNIT" 2>/dev/null || true)"
            rg_field "Status" "${ACTIVE_STATE}${SUB_STATE:+ (${SUB_STATE})}" warn; warning
            if [ -n "$RESULT" ] && [ "$RESULT" != "success" ]; then
                rg_field "  Last result" "$RESULT" warn
            fi
            if [ -n "$EXIT_STATUS" ] && [ "$EXIT_STATUS" != "0" ]; then
                rg_field "  Exit status" "$EXIT_STATUS" warn
            fi
            if [ "$CONDITION" = "no" ]; then
                rg_field "  Start condition" "not met — the Release build is missing (./scripts/build-server-linux.sh)" warn
            fi
            if [ -n "$NRESTARTS" ] && [ "$NRESTARTS" != "0" ]; then
                rg_field "  Restarts" "$NRESTARTS"
            fi
            rg_info "Recent log: journalctl -u remote-gamepad -n 20 --no-pager"
        fi
    else
        rg_field "Status" "systemctl not available" warn; warning
    fi
    rg_info "Manage with:"
    rg_info "  systemctl status ${RG_SERVICE_UNIT}"
    rg_info "  sudo systemctl restart ${RG_SERVICE_UNIT}"
    rg_info "  sudo systemctl stop ${RG_SERVICE_UNIT}"
    rg_info "  journalctl -u remote-gamepad -f"
else
    rg_field "Service unit" "not installed (sudo ./scripts/setup-linux.sh, or --no-autostart to opt out)"
    rg_info "Without it, start the server manually: ./scripts/run-server-linux.sh"
fi

# --------------------------------------------------------------------------
rg_section "RemoteGamepad configuration"
if [ -f "$RG_STATE_FILE" ]; then
    rg_field "Setup state" "$RG_STATE_FILE" ok
    while IFS= read -r line; do
        case "$line" in \#*|'') continue ;; esac
        rg_field "  ${line%%=*}" "${line#*=}"
    done < "$RG_STATE_FILE"
else
    rg_field "Setup state" "not found — run sudo ./scripts/setup-linux.sh" warn; warning
fi
rg_field "Server project" "${RG_SERVER_DIR}"
if [ -d "${RG_SERVER_DIR}/bin/Release/net${RG_REQUIRED_DOTNET_MAJOR}.0" ]; then
    rg_field "Release build" "present" ok
else
    rg_field "Release build" "not built yet (./scripts/build-server-linux.sh)"
fi
rg_field "UDP ports" "${RG_UDP_INPUT_PORT} (input), ${RG_UDP_DISCOVERY_PORT} (discovery)"

# --------------------------------------------------------------------------
rg_section "Summary"
if [ "$PROBLEMS" -eq 0 ] && [ "$WARNINGS" -eq 0 ]; then
    rg_ok "Ready: Wi-Fi and Bluetooth prerequisites look complete."
    exit 0
fi
if [ "$PROBLEMS" -eq 0 ]; then
    rg_warn "${WARNINGS} warning(s); no blocking problem. Wi-Fi/UDP input should work."
    rg_info "Bluetooth-related warnings are expected on Wi-Fi-only machines."
    [ "$STRICT" = "1" ] && exit 2
    exit 0
fi
rg_err "${PROBLEMS} blocking problem(s) and ${WARNINGS} warning(s)."
rg_info "Fix with: sudo ./scripts/setup-linux.sh   (then log out and back in)"
exit 1
