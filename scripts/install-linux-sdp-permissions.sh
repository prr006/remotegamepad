#!/bin/sh
set -eu

if [ "$(id -u)" -ne 0 ]; then
    echo "Run this one-time installer with sudo: sudo $0" >&2
    exit 1
fi

repo_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
unit_dir=/etc/systemd/system
helper=/usr/local/libexec/remote-gamepad-fix-sdp-permissions

getent group bluetooth >/dev/null || {
    echo "Ubuntu's 'bluetooth' system group was not found; install/enable BlueZ first." >&2
    exit 1
}

install -D -m 0755 "$repo_dir/scripts/remote-gamepad-fix-sdp-permissions" "$helper"
install -D -m 0644 "$repo_dir/systemd/remote-gamepad-sdp-permissions.path" "$unit_dir/remote-gamepad-sdp-permissions.path"
install -D -m 0644 "$repo_dir/systemd/remote-gamepad-sdp-permissions.service" "$unit_dir/remote-gamepad-sdp-permissions.service"

systemctl daemon-reload
# The enabled Wants= link is under bluetooth.service.wants; the path unit is
# ordered before bluetooth.service and does not alter/override its vendor file.
systemctl enable --now remote-gamepad-sdp-permissions.path
# Handle the already-running Bluetooth daemon/socket once during installation.
# The same unit is subsequently started by the event-driven path watcher.
systemctl start remote-gamepad-sdp-permissions.service

echo "Installed. The system path unit will reapply permissions whenever BlueZ creates /run/sdp."
echo "Verify with: stat -c '%U:%G %a %n' /run/sdp; systemctl status remote-gamepad-sdp-permissions.path"
