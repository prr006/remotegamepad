#!/bin/sh
set -eu

if [ "$(id -u)" -ne 0 ]; then
    echo "Run this uninstaller with sudo: sudo $0" >&2
    exit 1
fi

systemctl disable --now remote-gamepad-sdp-permissions.path 2>/dev/null || true
systemctl stop remote-gamepad-sdp-permissions.service 2>/dev/null || true
rm -f /etc/systemd/system/remote-gamepad-sdp-permissions.path \
      /etc/systemd/system/remote-gamepad-sdp-permissions.service \
      /usr/local/libexec/remote-gamepad-fix-sdp-permissions
systemctl daemon-reload

# Restore the documented Ubuntu default for the current socket, if present.
# A later bluetooth.service restart will recreate it with the distro defaults.
if [ -S /run/sdp ]; then
    chown root:root /run/sdp
    chmod 0660 /run/sdp
fi

echo "Removed the RemoteGamepad SDP permission watcher and restored root:root 0660 on any current /run/sdp socket."
