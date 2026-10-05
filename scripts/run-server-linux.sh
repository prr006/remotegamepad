#!/usr/bin/env bash
#
# RemoteGamepad — run the Linux server as your normal user
#
# The server is designed to run unprivileged: /dev/uinput access comes from
# the dedicated "remote-gamepad" group (udev rule) and BlueZ SDP access comes
# from the group-owned /run/sdp socket. This script therefore refuses to run
# under sudo/root.
#
# Usage: ./scripts/run-server-linux.sh [server arguments]
#   e.g. ./scripts/run-server-linux.sh --trace-input
set -euo pipefail

RG_SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source-path=SCRIPTDIR source=lib/remote-gamepad-linux.sh
. "${RG_SCRIPT_DIR}/lib/remote-gamepad-linux.sh"

if [ "${1:-}" = "-h" ] || [ "${1:-}" = "--help" ]; then
    cat <<EOF
${RG_PROJECT_NAME} Linux server

Usage: $0 [server arguments]

Server arguments are passed straight through, for example:
  --trace-input        rate-limited state-to-event-code diagnostics
  --no-discoverable    do not ask BlueZ to make the adapter visible
                       (RFCOMM/SDP are unaffected; use it for a paired phone)
  --uinput-self-test   backend-only mapping exercise (see scripts/test-linux.sh)

Listens on UDP ${RG_UDP_INPUT_PORT} (input) and UDP ${RG_UDP_DISCOVERY_PORT} (discovery),
plus Bluetooth RFCOMM channel 1 when a working adapter is present.
Stop with Ctrl+C.
EOF
    exit 0
fi

rg_require_linux

if rg_is_root; then
    rg_err "Do not run the server as root or with sudo."
    rg_info "Run it as your normal desktop user. If you get a permission error on /dev/uinput,"
    rg_info "run 'sudo ./scripts/setup-linux.sh' once and log out and back in."
    exit 1
fi

if ! rg_have dotnet; then
    rg_err ".NET SDK not found. Run: sudo ./scripts/setup-linux.sh"
    exit 1
fi

# ---- preflight (advisory, never fatal except for a missing uinput device) ---
if ! rg_uinput_exists; then
    rg_err "/dev/uinput does not exist (uinput module state: $(rg_uinput_module_state))."
    rg_info "Run: sudo ./scripts/setup-linux.sh    then reboot if the module could not be loaded."
    exit 1
fi

if ! rg_uinput_writable; then
    rg_err "/dev/uinput is not writable by $(id -un) (currently $(rg_uinput_owner || echo 'unknown'))."
    if rg_user_in_group "$(id -un)" "$RG_GROUP" && ! rg_session_in_group "$RG_GROUP"; then
        rg_info "You are a member of '${RG_GROUP}' but this session predates that change."
        rg_info "Log out and back in, or start the server via:  sg ${RG_GROUP} -c '$0 $*'"
    else
        rg_info "Run: sudo ./scripts/setup-linux.sh    (then log out and back in)"
    fi
    exit 1
fi

if ! rg_bluetooth_adapters >/dev/null 2>&1; then
    rg_dim "No Bluetooth adapter detected — starting in Wi-Fi/UDP-only mode."
elif [ -S "$RG_SDP_SOCKET" ] && ! rg_sdp_socket_ok; then
    rg_warn "${RG_SDP_SOCKET} is $(rg_sdp_socket_state) instead of root:${RG_GROUP} 660; Bluetooth SDP registration may fail."
    rg_info "Check: systemctl status ${RG_SDP_PATH_UNIT}  and  ./scripts/check-linux.sh"
fi

cd -- "$RG_SERVER_DIR"
rg_info "Starting ${RG_PROJECT_NAME} server as $(id -un) (Ctrl+C to stop)..."
exec dotnet run -c Release -- "$@"
