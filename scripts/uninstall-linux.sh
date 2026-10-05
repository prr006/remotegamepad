#!/usr/bin/env bash
#
# RemoteGamepad — remove the Linux system configuration
#
# Removes ONLY what scripts/setup-linux.sh created:
#   * /etc/udev/rules.d/99-remote-gamepad-uinput.rules
#   * /etc/modules-load.d/remote-gamepad-uinput.conf
#   * /etc/systemd/system/bluetooth.service.d/10-remote-gamepad-compat.conf
#   * /etc/systemd/system/remote-gamepad-sdp-permissions.{path,service}
#   * /usr/local/libexec/remote-gamepad-fix-sdp-permissions
#   * /etc/remote-gamepad/setup.env
# and then reloads systemd and udev.
#
# It never removes .NET, BlueZ, distribution packages, repository files or any
# configuration it did not write. The dedicated group is kept unless you pass
# --remove-group.
#
# Usage: sudo ./scripts/uninstall-linux.sh [--yes] [--remove-group] [--dry-run]
set -euo pipefail

RG_SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source-path=SCRIPTDIR source=lib/remote-gamepad-linux.sh
. "${RG_SCRIPT_DIR}/lib/remote-gamepad-linux.sh"

RG_ORIGINAL_ARGS=("$@")
ASSUME_YES=0
REMOVE_GROUP=0
RESTART_BLUETOOTH=auto

usage() {
    cat <<EOF
${RG_PROJECT_NAME} Linux uninstall

Usage: sudo $0 [options]

  -y, --yes                 Do not prompt (also restarts bluetooth.service
                            when the compatibility drop-in is removed).
      --remove-group        Also delete the dedicated '${RG_GROUP}' group.
      --no-restart-bluetooth
                            Leave bluetooth.service running with the old
                            configuration (changes apply on next restart).
      --dry-run             Show what would be removed; changes nothing.
  -h, --help                Show this help.

Not removed: .NET, BlueZ, distribution packages, repository files, the user's
group membership in distribution groups, and anything this project did not
create.
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        -y|--yes) ASSUME_YES=1 ;;
        --remove-group) REMOVE_GROUP=1 ;;
        --no-restart-bluetooth) RESTART_BLUETOOTH=never ;;
        --restart-bluetooth) RESTART_BLUETOOTH=always ;;
        --dry-run) RG_DRY_RUN=1 ;;
        -h|--help) usage; exit 0 ;;
        *) rg_err "Unknown option: $1"; usage >&2; exit 2 ;;
    esac
    shift
done

rg_require_linux

if ! rg_is_root; then
    if rg_dry_run; then
        rg_warn "Running --dry-run without root."
    elif [ -n "$RG_PREFIX" ]; then
        rg_warn "REMOTE_GAMEPAD_PREFIX is set; cleaning ${RG_PREFIX} without root."
    elif rg_have sudo; then
        rg_step "Re-running with sudo..."
        exec sudo -- "$0" ${RG_ORIGINAL_ARGS[@]+"${RG_ORIGINAL_ARGS[@]}"}
    else
        rg_die "Root privileges are required. Run: su -c '$0'"
    fi
fi

confirm() {
    [ "$ASSUME_YES" = "1" ] && return 0
    [ -t 0 ] || return 1
    local answer=''
    read -r -p "$1 [y/N] " answer || return 1
    case "${answer,,}" in y|yes) return 0 ;; *) return 1 ;; esac
}

# Read the recorded group name so a custom --group setup is cleaned up too.
if [ -f "$RG_STATE_FILE" ]; then
    RECORDED_GROUP="$(awk -F= '/^REMOTE_GAMEPAD_GROUP=/{print $2}' "$RG_STATE_FILE" | tail -1)"
    [ -n "${RECORDED_GROUP:-}" ] && RG_GROUP="$RECORDED_GROUP"
fi

rg_section "Removing ${RG_PROJECT_NAME} system configuration"
rg_info "Group in use: ${RG_GROUP}"

BT_DROPIN_REMOVED=0

# --------------------------------------------------------------------------
# 1. systemd units (SDP watcher)
# --------------------------------------------------------------------------
if rg_have systemctl; then
    if rg_systemd_running; then
        rg_run_quiet systemctl disable --now "$RG_SDP_PATH_UNIT" || true
        rg_run_quiet systemctl stop "$RG_SDP_SERVICE_UNIT" || true
    fi
fi
rg_remove_file "${RG_SYSTEMD_UNIT_DIR}/${RG_SDP_PATH_UNIT}"
rg_remove_file "${RG_SYSTEMD_UNIT_DIR}/${RG_SDP_SERVICE_UNIT}"
# Enablement symlink, in case systemd was not running when we disabled it.
rg_remove_file "${RG_SYSTEMD_UNIT_DIR}/bluetooth.service.wants/${RG_SDP_PATH_UNIT}"
rg_remove_file "${RG_SYSTEMD_UNIT_DIR}/multi-user.target.wants/${RG_SDP_PATH_UNIT}"

# --------------------------------------------------------------------------
# 2. Bluetooth compatibility drop-in (vendor unit was never modified)
# --------------------------------------------------------------------------
if [ -f "$RG_BT_DROPIN" ]; then
    rg_remove_file "$RG_BT_DROPIN"
    BT_DROPIN_REMOVED=1
    # Remove the drop-in directory only when we created it and it is now empty.
    if [ -d "$RG_BT_DROPIN_DIR" ] && [ -z "$(ls -A "$RG_BT_DROPIN_DIR" 2>/dev/null)" ]; then
        rg_run rmdir -- "$RG_BT_DROPIN_DIR" && rg_ok "removed empty ${RG_BT_DROPIN_DIR}"
    fi
fi

# --------------------------------------------------------------------------
# 3. Helper
# --------------------------------------------------------------------------
rg_remove_file "$RG_SDP_HELPER"

# --------------------------------------------------------------------------
# 4. udev rule + module autoload
# --------------------------------------------------------------------------
rg_remove_file "$RG_UDEV_RULE"
rg_remove_file "$RG_MODULES_CONF"

# --------------------------------------------------------------------------
# 5. Recorded state
# --------------------------------------------------------------------------
rg_remove_file "$RG_STATE_FILE"
if [ -d "$RG_STATE_DIR" ] && [ -z "$(ls -A "$RG_STATE_DIR" 2>/dev/null)" ]; then
    rg_run rmdir -- "$RG_STATE_DIR" && rg_ok "removed empty ${RG_STATE_DIR}"
fi

# --------------------------------------------------------------------------
# 6. Reload systemd + udev
# --------------------------------------------------------------------------
rg_section "Reloading systemd and udev"
rg_daemon_reload
if rg_have udevadm; then
    rg_run udevadm control --reload-rules || rg_warn "udevadm control --reload-rules failed."
    rg_run udevadm trigger --subsystem-match=misc --action=add || rg_warn "udevadm trigger failed."
else
    rg_warn "udevadm not found; /dev/uinput keeps its current permissions until reboot."
fi

# --------------------------------------------------------------------------
# 7. Optional: restart Bluetooth so compatibility mode really goes away
# --------------------------------------------------------------------------
if [ "$BT_DROPIN_REMOVED" = "1" ] && [ "$RESTART_BLUETOOTH" != "never" ] && rg_systemd_running; then
    if rg_unit_active bluetooth.service || [ "$RESTART_BLUETOOTH" = "always" ]; then
        if confirm "Restart bluetooth.service to drop compatibility mode?"; then
            rg_run systemctl restart bluetooth.service || rg_warn "Restarting bluetooth.service failed."
        else
            rg_info "bluetooth.service keeps compatibility mode until its next restart."
        fi
    fi
fi

# Restore the distribution default on a still-existing socket.
if [ -S "$RG_SDP_SOCKET" ] && ! rg_dry_run; then
    current="$(rg_sdp_socket_state || true)"
    if [ "$current" != "root:root 660" ]; then
        chown root:root "$RG_SDP_SOCKET" 2>/dev/null || true
        chmod 0660 "$RG_SDP_SOCKET" 2>/dev/null || true
        rg_ok "restored ${RG_SDP_SOCKET} to $(rg_sdp_socket_state || echo 'unknown')"
    fi
fi

# --------------------------------------------------------------------------
# 8. Optional: dedicated group
# --------------------------------------------------------------------------
if [ "$REMOVE_GROUP" = "1" ]; then
    if rg_group_exists "$RG_GROUP"; then
        if rg_have groupdel; then
            rg_run groupdel "$RG_GROUP" && rg_ok "removed group '${RG_GROUP}'"
        elif rg_have delgroup; then
            rg_run delgroup "$RG_GROUP" && rg_ok "removed group '${RG_GROUP}'"
        else
            rg_warn "No groupdel/delgroup command; remove the '${RG_GROUP}' group manually."
        fi
    else
        rg_info "Group '${RG_GROUP}' does not exist."
    fi
elif rg_group_exists "$RG_GROUP"; then
    rg_info "Kept the '${RG_GROUP}' group (use --remove-group to delete it)."
else
    rg_info "Group '${RG_GROUP}' does not exist; nothing to remove."
fi

rg_section "Done"
rg_info "Left untouched: .NET, BlueZ, distribution packages and every repository file."
if rg_dry_run; then
    rg_dim "(dry run: nothing was changed)"
fi
