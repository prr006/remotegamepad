#!/usr/bin/env bash
#
# RemoteGamepad — Linux uinput test
#
# Verifies the uinput prerequisites and then runs the server's existing
# self-test:   dotnet run -c Release -- --uinput-self-test
#
# Bluetooth is deliberately NOT required: this test only exercises the
# kernel uinput path (parser -> virtual controller), so it passes on
# Wi-Fi-only machines with no Bluetooth hardware.
#
# Usage: ./scripts/test-linux.sh [--skip-build] [--timeout SECONDS]
set -euo pipefail

RG_SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source-path=SCRIPTDIR source=lib/remote-gamepad-linux.sh
. "${RG_SCRIPT_DIR}/lib/remote-gamepad-linux.sh"

TIMEOUT_SECONDS=180
PREREQS_ONLY=0
while [ $# -gt 0 ]; do
    case "$1" in
        --timeout) TIMEOUT_SECONDS="${2:-180}"; shift ;;
        --timeout=*) TIMEOUT_SECONDS="${1#*=}" ;;
        --prereqs-only) PREREQS_ONLY=1 ;;
        -h|--help)
            cat <<EOF
${RG_PROJECT_NAME} Linux uinput test

Usage: $0 [options]

  --prereqs-only     Only check the uinput prerequisites, do not run the server.
  --timeout SECONDS  Abort the self-test after SECONDS (default: 180).

Runs the existing server self-test:
  dotnet run -c Release -- --uinput-self-test
Bluetooth is not required.
EOF
            exit 0 ;;
        *) rg_err "Unknown option: $1"; exit 2 ;;
    esac
    shift
done

rg_require_linux

FAILURES=0
fail() { FAILURES=$((FAILURES + 1)); rg_err "$*"; }

rg_section "uinput prerequisites"

if rg_is_root; then
    rg_warn "Running as root. The real server must run unprivileged; this test only proves the kernel path."
fi

# 1. kernel module / device node
MODULE_STATE="$(rg_uinput_module_state)"
case "$MODULE_STATE" in
    loaded)            rg_field "uinput module" "loaded" ok ;;
    builtin-or-loaded) rg_field "uinput module" "available (built-in or already loaded)" ok ;;
    *)                 rg_field "uinput module" "not loaded" fail
                       fail "The uinput module is not loaded. Run: sudo ./scripts/setup-linux.sh" ;;
esac

if rg_uinput_exists; then
    rg_field "/dev/uinput" "$(rg_uinput_owner)" ok
else
    rg_field "/dev/uinput" "missing" fail
    fail "/dev/uinput does not exist. Run: sudo ./scripts/setup-linux.sh (reboot if modprobe failed)."
fi

# 2. access for the invoking user
if rg_uinput_exists; then
    if rg_uinput_writable; then
        rg_field "Write access" "yes (user $(id -un))" ok
    else
        rg_field "Write access" "no (user $(id -un))" fail
        if rg_user_in_group "$(id -un)" "$RG_GROUP" && ! rg_session_in_group "$RG_GROUP"; then
            fail "Your session predates the '${RG_GROUP}' group membership. Log out/in, or run: sg ${RG_GROUP} -c '$0'"
        else
            fail "No write access to /dev/uinput. Run: sudo ./scripts/setup-linux.sh, then log out and back in."
        fi
    fi
fi

# 3. group state
if rg_group_exists "$RG_GROUP"; then
    rg_field "Group '${RG_GROUP}'" "exists (gid $(getent group "$RG_GROUP" | cut -d: -f3))" ok
else
    rg_field "Group '${RG_GROUP}'" "missing" warn
fi

# 4. .NET SDK
if rg_dotnet_sdk_ok; then
    rg_field ".NET SDK" "$(rg_dotnet_sdk_version)" ok
else
    rg_field ".NET SDK" "$(rg_dotnet_sdk_version 2>/dev/null || echo 'not installed') (need ${RG_REQUIRED_DOTNET_MAJOR}.x)" fail
    fail ".NET ${RG_REQUIRED_DOTNET_MAJOR} SDK is required to run the self-test."
fi

rg_dim "Bluetooth is not required for this test."

if [ "$FAILURES" -gt 0 ]; then
    echo
    rg_err "${FAILURES} prerequisite problem(s); not running the self-test."
    rg_info "Diagnose with: ./scripts/check-linux.sh"
    exit 1
fi
rg_ok "All uinput prerequisites satisfied."

if [ "$PREREQS_ONLY" = "1" ]; then
    exit 0
fi

# --------------------------------------------------------------------------
# Run the existing server self-test
# --------------------------------------------------------------------------
rg_section "Server self-test"
rg_info "Command: dotnet run -c Release -- --uinput-self-test"
cd -- "$RG_SERVER_DIR"

status=0
if rg_have timeout; then
    timeout --foreground "${TIMEOUT_SECONDS}" dotnet run -c Release -- --uinput-self-test || status=$?
else
    dotnet run -c Release -- --uinput-self-test || status=$?
fi

echo
if [ "$status" -eq 0 ]; then
    rg_ok "uinput self-test passed."
    rg_info "Optional extra check while the server runs:"
    rg_info "  grep -A8 -B2 'Xbox 360 Controller' /proc/bus/input/devices"
    exit 0
fi
if [ "$status" -eq 124 ]; then
    rg_err "Self-test timed out after ${TIMEOUT_SECONDS}s."
else
    rg_err "Self-test failed (exit code ${status})."
fi
rg_info "Diagnose with: ./scripts/check-linux.sh"
exit "$status"
