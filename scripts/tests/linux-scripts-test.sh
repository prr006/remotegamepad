#!/usr/bin/env bash
#
# Developer self-test for the RemoteGamepad Linux scripts.
#
# Runs the setup/check/test/uninstall scripts against simulated Fedora,
# Ubuntu, Arch, Debian-with-compat and unknown-distro environments using
# --dry-run plus stub commands, and unit-tests the shared library helpers and
# the /run/sdp permission helper.
#
# Nothing is installed and no system file is touched: every scenario runs in a
# throw-away directory with a stub PATH. Safe to run as a normal user.
#
# Usage: ./scripts/tests/linux-scripts-test.sh [-v]
#
# Scenario environments are intentionally confined to subshells.
# shellcheck disable=SC2030,SC2031
set -uo pipefail

VERBOSE=0
[ "${1:-}" = "-v" ] && VERBOSE=1

TEST_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${TEST_DIR}/../.." && pwd)"
SCRIPTS="${REPO_ROOT}/scripts"
WORK="$(mktemp -d -t remotegamepad-tests-XXXXXX)"
trap 'rm -rf -- "$WORK"' EXIT

PASS=0
FAIL=0

# A sanitized "system" bin so a scenario only sees the tools it declares
# (this container is Debian, so the real PATH would leak apt/dpkg into every
# simulated distribution).
SYSBIN="${WORK}/sysbin"
mkdir -p "$SYSBIN"
for tool in bash sh env awk basename cat chgrp chmod cut date dirname getent grep \
            head id install ln ls mkdir mktemp mv printf rm rmdir sed sleep sort \
            stat tail tr uname wc; do
    real="$(command -v "$tool" 2>/dev/null)" || continue
    ln -sf "$real" "${SYSBIN}/${tool}"
done

say()  { printf '%s\n' "$*"; }
head1() { printf '\n\033[1m== %s ==\033[0m\n' "$*"; }
ok()   { PASS=$((PASS + 1)); printf '  \033[32mPASS\033[0m %s\n' "$*"; }
bad()  { FAIL=$((FAIL + 1)); printf '  \033[31mFAIL\033[0m %s\n' "$*"; }

assert_contains() {
    local name="$1" file="$2" pattern="$3"
    if grep -qF -- "$pattern" "$file"; then ok "$name"; else
        bad "$name (missing: $pattern)"
        [ "$VERBOSE" = "1" ] && sed 's/^/        /' "$file"
    fi
}
assert_not_contains() {
    local name="$1" file="$2" pattern="$3"
    if grep -qF -- "$pattern" "$file"; then
        bad "$name (unexpected: $pattern)"
        [ "$VERBOSE" = "1" ] && sed 's/^/        /' "$file"
    else ok "$name"; fi
}
assert_matches() {
    local name="$1" file="$2" pattern="$3"
    if grep -qE -- "$pattern" "$file"; then ok "$name"; else
        bad "$name (no match: $pattern)"
        [ "$VERBOSE" = "1" ] && sed 's/^/        /' "$file"
    fi
}
assert_eq() {
    local name="$1" expected="$2" actual="$3"
    if [ "$expected" = "$actual" ]; then ok "$name"; else bad "$name (expected '$expected', got '$actual')"; fi
}

# --------------------------------------------------------------------------
# Scenario builder: fake os-release, stub binaries, fake sysfs/devices
# --------------------------------------------------------------------------
make_scenario() {
    # $1 name, then KEY=VALUE settings consumed below
    local name="$1"; shift
    local dir="${WORK}/${name}"
    mkdir -p "$dir/bin" "$dir/etc" "$dir/sys/class/bluetooth" "$dir/dev" "$dir/run/systemd/system" "$dir/log"

    local id=fedora id_like='' pretty='Test Linux' version='1' pkgmgr=dnf
    local bluetoothd='' compat=0 adapter=0 sdks='10.0.100' active=active tools=''
    local setting
    for setting in "$@"; do
        case "$setting" in
            id=*) id="${setting#*=}" ;;
            id_like=*) id_like="${setting#*=}" ;;
            pretty=*) pretty="${setting#*=}" ;;
            version=*) version="${setting#*=}" ;;
            pkgmgr=*) pkgmgr="${setting#*=}" ;;
            bluetoothd=*) bluetoothd="${setting#*=}" ;;
            compat=*) compat="${setting#*=}" ;;
            adapter=*) adapter="${setting#*=}" ;;
            sdks=*) sdks="${setting#*=}" ;;
            active=*) active="${setting#*=}" ;;
            tools=*) tools="${setting#*=}" ;;
        esac
    done

    {
        echo "ID=${id}"
        [ -n "$id_like" ] && echo "ID_LIKE=${id_like}"
        echo "PRETTY_NAME=\"${pretty}\""
        echo "VERSION_ID=\"${version}\""
    } > "$dir/etc/os-release"

    # uinput device stand-in (regular file, owned by the test user)
    : > "$dir/dev/uinput"
    chmod 0660 "$dir/dev/uinput"

    [ "$adapter" = "1" ] && { mkdir -p "$dir/sys/class/bluetooth/hci0"; echo "AA:BB:CC:DD:EE:FF" > "$dir/sys/class/bluetooth/hci0/address"; }

    # fake bluetoothd binary at the distro-specific location
    if [ -n "$bluetoothd" ]; then
        mkdir -p "$dir/${bluetoothd%/*}"
        cat > "$dir/${bluetoothd}" <<'EOS'
#!/bin/sh
case "${1:-}" in
  --version) echo "5.79" ;;
  --help) printf -- '  -C, --compat  Provide deprecated command line interfaces\n' ;;
esac
exit 0
EOS
        chmod +x "$dir/${bluetoothd}"
        # vendor unit fragment
        mkdir -p "$dir/etc/systemd/system"
        {
            echo '[Service]'
            if [ "$compat" = "1" ]; then
                echo "ExecStart=${dir}${bluetoothd} --compat"
            else
                echo "ExecStart=${dir}${bluetoothd}"
            fi
        } > "$dir/etc/bluetooth.service"
    fi

    # ---- stubs ----
    cat > "$dir/bin/systemctl" <<EOS
#!/bin/sh
log="${dir}/log/systemctl.log"
echo "systemctl \$*" >> "\$log"
case "\$1" in
  --version) echo "systemd 256 (256.4)"; exit 0 ;;
  show)
    property=""; unit=""
    for a in "\$@"; do
      case "\$a" in -p) next=prop ;; --value) ;; show) ;; *)
        if [ "\${next:-}" = prop ]; then property="\$a"; next=""; else unit="\$a"; fi ;;
      esac
    done
    case "\$property" in
      FragmentPath) [ -f "${dir}/etc/bluetooth.service" ] && echo "${dir}/etc/bluetooth.service"; exit 0 ;;
      ExecStart)
        if [ -f "${dir}/etc/bluetooth.service" ]; then
          line=\$(grep '^ExecStart=' "${dir}/etc/bluetooth.service" | tail -1); line="\${line#ExecStart=}"
          echo "{ path=\${line%% *} ; argv[]=\${line} ; ignore_errors=no ; start_time=[n/a] }"
        fi
        exit 0 ;;
      ExecMainStatus) echo 0; exit 0 ;;
      *) exit 0 ;;
    esac ;;
  is-active) [ "${active}" = active ] && exit 0 || exit 3 ;;
  is-enabled) exit 0 ;;
  list-unit-files) echo "bluetooth.service enabled"; exit 0 ;;
  *) exit 0 ;;
esac
EOS

    cat > "$dir/bin/dotnet" <<EOS
#!/bin/sh
case "\$1" in
  --list-sdks) for v in ${sdks}; do echo "\$v [/usr/share/dotnet/sdk]"; done ;;
  --list-runtimes) echo "Microsoft.NETCore.App 10.0.0 [/usr/share/dotnet/shared]" ;;
  --version) echo "${sdks%% *}" ;;
esac
exit 0
EOS
    [ "$sdks" = "none" ] && rm -f "$dir/bin/dotnet"

    # package managers (query subcommands answer "not installed, but available")
    case "$pkgmgr" in
        pacman)
            cat > "$dir/bin/pacman" <<EOS
#!/bin/sh
echo "pacman \$*" >> "${dir}/log/pkg.log"
case "\$1" in
  -Qi) exit 1 ;;   # not installed
  -Si) exit 0 ;;   # available in the sync database
esac
exit 0
EOS
            ;;
        *)
            cat > "$dir/bin/${pkgmgr}" <<EOS
#!/bin/sh
echo "${pkgmgr} \$*" >> "${dir}/log/pkg.log"
exit 0
EOS
            ;;
    esac
    case "$pkgmgr" in
        apt-get)
            printf '#!/bin/sh\necho "  Candidate: 1.2.3"\nexit 0\n' > "$dir/bin/apt-cache"
            printf '#!/bin/sh\nexit 1\n' > "$dir/bin/dpkg-query" ;;
        dnf|yum|zypper)
            printf '#!/bin/sh\nexit 1\n' > "$dir/bin/rpm" ;;
    esac
    for stub in modprobe udevadm groupadd usermod ldconfig; do
        printf '#!/bin/sh\nexit 0\n' > "$dir/bin/$stub"
    done
    # Optional tools: only present when the scenario asks for them, so package
    # planning can be observed when they are missing.
    for stub in $tools; do
        case "$stub" in
            sdptool) printf '#!/bin/sh\necho "Browsing FF:FF:FF:00:00:00 ..."\nexit 0\n' > "$dir/bin/sdptool" ;;
            *) printf '#!/bin/sh\nexit 0\n' > "$dir/bin/$stub" ;;
        esac
    done
    chmod +x "$dir/bin/"*

    printf '%s' "$dir"
}

run_scenario() {
    # run_scenario <dir> <logfile> <script> [args...]
    local dir="$1" log="$2"; shift 2
    (
        export PATH="${dir}/bin:${SYSBIN}"
        export RG_OS_RELEASE="${dir}/etc/os-release"
        export REMOTE_GAMEPAD_UINPUT_DEV="${dir}/dev/uinput"
        export REMOTE_GAMEPAD_SDP_SOCKET="${dir}/run/sdp"
        export REMOTE_GAMEPAD_SYS_BLUETOOTH="${dir}/sys/class/bluetooth"
        export REMOTE_GAMEPAD_SYSTEMD_RUN_DIR="${dir}/run/systemd/system"
        export NO_COLOR=1
        "$@"
    ) > "$log" 2>&1
    return $?
}

# ==========================================================================
head1 "Scenario: Fedora 43 with adapter, compatibility mode not yet enabled"
DIR="$(make_scenario fedora id=fedora pretty='Fedora Linux 43 (Workstation Edition)' version=43 \
        pkgmgr=dnf bluetoothd=/usr/libexec/bluetooth/bluetoothd compat=0 adapter=1)"
LOG="${WORK}/fedora.log"
run_scenario "$DIR" "$LOG" "${SCRIPTS}/setup-linux.sh" --dry-run --yes
STATUS=$?
assert_eq "setup exits 0" 0 "$STATUS"
assert_matches "detects Fedora family"        "$LOG" "Distro family +: fedora"
assert_matches "detects dnf"                  "$LOG" "Package manager +: dnf"
assert_contains "finds bluetoothd"             "$LOG" "/usr/libexec/bluetooth/bluetoothd"
assert_contains "plans Fedora packages"        "$LOG" "bluez-libs"
assert_contains "plans Fedora sdptool package" "$LOG" "bluez-deprecated"
assert_contains "creates dedicated group"      "$LOG" "groupadd --system remote-gamepad"
assert_contains "writes udev rule"             "$LOG" "/etc/udev/rules.d/99-remote-gamepad-uinput.rules"
assert_contains "udev rule uses group"         "$LOG" 'GROUP="remote-gamepad"'
assert_contains "writes modules-load.d"        "$LOG" "/etc/modules-load.d/remote-gamepad-uinput.conf"
assert_contains "writes bluetooth drop-in"     "$LOG" "/etc/systemd/system/bluetooth.service.d/10-remote-gamepad-compat.conf"
assert_contains "drop-in appends --compat"     "$LOG" "bluetoothd --compat"
assert_contains "resets vendor ExecStart"      "$LOG" "ExecStart="
assert_contains "installs SDP helper"          "$LOG" "/usr/local/libexec/remote-gamepad-fix-sdp-permissions"
assert_contains "installs path unit"           "$LOG" "remote-gamepad-sdp-permissions.path"
assert_contains "service unit group rewritten" "$LOG" "REMOTE_GAMEPAD_SDP_GROUP=remote-gamepad"
assert_contains "restarts bluetooth"           "$LOG" "systemctl restart bluetooth.service"
assert_not_contains "never edits vendor unit"  "$LOG" "/usr/lib/systemd/system"
assert_not_contains "no bluetooth-group assumption" "$LOG" "chgrp bluetooth"

# ==========================================================================
head1 "Scenario: Ubuntu 24.04, Wi-Fi only (no BlueZ, no adapter)"
DIR="$(make_scenario ubuntu id=ubuntu id_like=debian pretty='Ubuntu 24.04.1 LTS' version=24.04 \
        pkgmgr=apt-get bluetoothd= compat=0 adapter=0)"
LOG="${WORK}/ubuntu.log"
run_scenario "$DIR" "$LOG" "${SCRIPTS}/setup-linux.sh" --dry-run --yes
STATUS=$?
assert_eq "setup exits 0 without Bluetooth" 0 "$STATUS"
assert_matches "detects debian family"      "$LOG" "Distro family +: debian"
assert_matches "detects apt"                "$LOG" "Package manager +: apt"
assert_contains "plans Debian bluez runtime" "$LOG" "libbluetooth3"
assert_contains "still configures uinput"    "$LOG" "99-remote-gamepad-uinput.rules"
assert_contains "no adapter is tolerated"    "$LOG" "none detected"
assert_not_contains "no drop-in without BlueZ" "$LOG" "10-remote-gamepad-compat.conf"

LOG="${WORK}/ubuntu-nobt.log"
run_scenario "$DIR" "$LOG" "${SCRIPTS}/setup-linux.sh" --dry-run --yes --no-bluetooth --no-install
assert_eq "wifi-only setup exits 0" 0 $?
assert_contains "honours --no-bluetooth"  "$LOG" "Skipped (--no-bluetooth was given)"
assert_contains "honours --no-install"    "$LOG" "Package installation disabled"
assert_not_contains "no package install"  "$LOG" "apt-get install"

# ==========================================================================
head1 "Scenario: Arch Linux with adapter"
DIR="$(make_scenario arch id=arch pretty='Arch Linux' version='' pkgmgr=pacman \
        bluetoothd=/usr/lib/bluetooth/bluetoothd compat=0 adapter=1)"
LOG="${WORK}/arch.log"
run_scenario "$DIR" "$LOG" "${SCRIPTS}/setup-linux.sh" --dry-run --yes
assert_eq "setup exits 0" 0 $?
assert_matches "detects arch family"        "$LOG" "Distro family +: arch"
assert_matches "detects pacman"             "$LOG" "Package manager +: pacman"
assert_contains "plans bluez-utils"          "$LOG" "bluez-utils"
assert_contains "plans Arch sdptool package" "$LOG" "bluez-deprecated-tools"
assert_not_contains "never executes a bare pacman -Sy" "$LOG" "[dry-run] pacman -Sy"
assert_contains "uses pacman -S --needed"   "$LOG" "pacman -S --needed --noconfirm"
assert_contains "warns about a stale sync db" "$LOG" "sync database"
assert_contains "finds Arch bluetoothd path" "$LOG" "/usr/lib/bluetooth/bluetoothd"

# ==========================================================================
head1 "Scenario: Debian where the distribution already enables --compat"
DIR="$(make_scenario debian id=debian pretty='Debian GNU/Linux 13 (trixie)' version=13 \
        pkgmgr=apt-get bluetoothd=/usr/libexec/bluetooth/bluetoothd compat=1 adapter=1)"
LOG="${WORK}/debian.log"
run_scenario "$DIR" "$LOG" "${SCRIPTS}/setup-linux.sh" --dry-run --yes
assert_eq "setup exits 0" 0 $?
assert_contains "detects existing compat mode" "$LOG" "already enabled by this distribution"
assert_not_contains "no redundant drop-in"     "$LOG" "write /etc/systemd/system/bluetooth.service.d"
assert_contains "still installs SDP watcher"   "$LOG" "remote-gamepad-sdp-permissions.path"

# ==========================================================================
head1 "Scenario: unknown distribution without a package manager"
DIR="$(make_scenario unknown id=void pretty='Void Linux' version='' pkgmgr=xbps-install \
        bluetoothd= compat=0 adapter=0 sdks=none)"
LOG="${WORK}/unknown.log"
run_scenario "$DIR" "$LOG" "${SCRIPTS}/setup-linux.sh" --dry-run --yes
assert_eq "setup still exits 0" 0 $?
assert_matches "reports unknown family"   "$LOG" "Distro family +: unknown"
assert_contains "warns about package mgr"  "$LOG" "No supported package manager"
assert_contains "still writes udev rule"   "$LOG" "99-remote-gamepad-uinput.rules"

# ==========================================================================
head1 "check-linux.sh report"
DIR="$(make_scenario checkenv id=fedora pretty='Fedora Linux 43' version=43 pkgmgr=dnf \
        bluetoothd=/usr/libexec/bluetooth/bluetoothd compat=1 adapter=1 tools="bluetoothctl sdptool")"
LOG="${WORK}/check.log"
run_scenario "$DIR" "$LOG" "${SCRIPTS}/check-linux.sh"
CHECK_STATUS=$?
assert_contains "reports distribution"   "$LOG" "Distribution"
assert_contains "reports kernel"         "$LOG" "Kernel"
assert_contains "reports architecture"   "$LOG" "Architecture"
assert_contains "reports .NET"           "$LOG" "Installed SDKs"
assert_contains "reports bluetoothd"     "$LOG" "bluetoothd"
assert_contains "reports compat mode"    "$LOG" "Compat SDP mode"
assert_contains "reports uinput"         "$LOG" "uinput"
assert_contains "reports /run/sdp"       "$LOG" "/run/sdp"
assert_contains "reports groups"         "$LOG" "Session groups"
assert_contains "reports SDP registration" "$LOG" "SDP registration"
assert_contains "non-zero exit is explained" "$LOG" "blocking problem"
assert_eq "exit 1 when group is missing" 1 "$CHECK_STATUS"

# ==========================================================================
head1 "test-linux.sh prerequisites (no Bluetooth required)"
LOG="${WORK}/test.log"
run_scenario "$DIR" "$LOG" "${SCRIPTS}/test-linux.sh" --prereqs-only
TEST_STATUS=$?
assert_contains "checks uinput module"     "$LOG" "uinput module"
assert_contains "checks device node"       "$LOG" "/dev/uinput"
assert_contains "checks write access"      "$LOG" "Write access"
assert_contains "checks .NET SDK"          "$LOG" ".NET SDK"
assert_contains "states Bluetooth is optional" "$LOG" "Bluetooth is not required"
assert_not_contains "does not require adapter" "$LOG" "adapter required"
assert_contains "prerequisites summarised" "$LOG" "All uinput prerequisites satisfied"
assert_eq "prereq-only run succeeds" 0 "$TEST_STATUS"

# ==========================================================================
head1 "uninstall-linux.sh --dry-run"
LOG="${WORK}/uninstall.log"
run_scenario "$DIR" "$LOG" "${SCRIPTS}/uninstall-linux.sh" --dry-run --yes
assert_eq "uninstall exits 0" 0 $?
assert_contains "reloads systemd"    "$LOG" "systemctl daemon-reload"
assert_contains "reloads udev"       "$LOG" "udevadm control --reload-rules"
assert_matches "keeps the group by default" "$LOG" "Kept the '|does not exist; nothing to remove"
assert_contains "states what is preserved"   "$LOG" "Left untouched: .NET, BlueZ"
assert_not_contains "never removes dotnet"   "$LOG" "remove dotnet"
assert_not_contains "never touches /usr/lib/systemd" "$LOG" "/usr/lib/systemd/system"

# ==========================================================================
head1 "End-to-end: real file writes, idempotency, uninstall (sandboxed prefix)"
# Same scripts, executed for real, but with every managed path relocated under
# a scratch prefix so the host is never touched.
DIR="$(make_scenario e2e id=fedora pretty='Fedora Linux 43' version=43 pkgmgr=dnf \
        bluetoothd=/usr/libexec/bluetooth/bluetoothd compat=0 adapter=1)"
PREFIX="${DIR}/root"
mkdir -p "${PREFIX}/etc/udev/rules.d" "${PREFIX}/etc/systemd/system"
# Sentinel files that must survive both setup and uninstall.
echo 'KERNEL=="null", MODE="0666"' > "${PREFIX}/etc/udev/rules.d/10-unrelated.rules"
echo '[Unit]' > "${PREFIX}/etc/systemd/system/unrelated.service"

run_e2e() {
    local log="$1"; shift
    (
        export PATH="${DIR}/bin:${SYSBIN}"
        export RG_OS_RELEASE="${DIR}/etc/os-release"
        export REMOTE_GAMEPAD_UINPUT_DEV="${DIR}/dev/uinput"
        export REMOTE_GAMEPAD_SDP_SOCKET="${DIR}/run/sdp"
        export REMOTE_GAMEPAD_SYS_BLUETOOTH="${DIR}/sys/class/bluetooth"
        export REMOTE_GAMEPAD_SYSTEMD_RUN_DIR="${DIR}/run/systemd/system"
        export REMOTE_GAMEPAD_PREFIX="${PREFIX}"
        export NO_COLOR=1
        "$@"
    ) > "$log" 2>&1
}

LOG="${WORK}/e2e-setup.log"
run_e2e "$LOG" "${SCRIPTS}/setup-linux.sh" --yes --no-install
assert_eq "real setup run exits 0" 0 $?

UDEV="${PREFIX}/etc/udev/rules.d/99-remote-gamepad-uinput.rules"
MODCONF="${PREFIX}/etc/modules-load.d/remote-gamepad-uinput.conf"
DROPIN="${PREFIX}/etc/systemd/system/bluetooth.service.d/10-remote-gamepad-compat.conf"
PATHUNIT="${PREFIX}/etc/systemd/system/remote-gamepad-sdp-permissions.path"
SVCUNIT="${PREFIX}/etc/systemd/system/remote-gamepad-sdp-permissions.service"
HELPER_INSTALLED="${PREFIX}/usr/local/libexec/remote-gamepad-fix-sdp-permissions"
STATE="${PREFIX}/etc/remote-gamepad/setup.env"

for f in "$UDEV" "$MODCONF" "$DROPIN" "$PATHUNIT" "$SVCUNIT" "$HELPER_INSTALLED" "$STATE"; do
    if [ -f "$f" ]; then ok "created ${f#"$PREFIX"}"; else bad "created ${f#"$PREFIX"}"; fi
done
assert_contains "udev rule grants the dedicated group" "$UDEV" 'GROUP="remote-gamepad"'
assert_contains "udev rule keeps the static node"      "$UDEV" 'static_node=uinput'
assert_contains "modules-load loads uinput"            "$MODCONF" "uinput"
assert_contains "drop-in resets ExecStart"             "$DROPIN" "ExecStart="
assert_contains "drop-in uses the detected daemon"     "$DROPIN" "${DIR}/usr/libexec/bluetooth/bluetoothd --compat"
assert_contains "path unit watches the socket"         "$PATHUNIT" "PathChanged=/run/sdp"
assert_contains "service unit carries the group"       "$SVCUNIT" "REMOTE_GAMEPAD_SDP_GROUP=remote-gamepad"
assert_contains "state file records the group"         "$STATE" "REMOTE_GAMEPAD_GROUP=remote-gamepad"
assert_contains "state file records the daemon"        "$STATE" "REMOTE_GAMEPAD_BLUETOOTHD="
assert_eq "helper is installed executable" "755" "$(stat -c '%a' "$HELPER_INSTALLED")"
assert_eq "helper matches the repository copy" "" "$(diff "${SCRIPTS}/remote-gamepad-fix-sdp-permissions" "$HELPER_INSTALLED" 2>&1)"
assert_eq "udev rule mode" "644" "$(stat -c '%a' "$UDEV")"

BT_LOG="${DIR}/log/systemctl.log"
RESTARTS1="$(grep -c 'restart bluetooth.service' "$BT_LOG" 2>/dev/null || true)"
if [ "${RESTARTS1:-0}" -ge 1 ]; then ok "first run applies compat mode (bluetooth restarted)"; else
    bad "first run applies compat mode (bluetooth restarted)"
fi

LOG2="${WORK}/e2e-setup-rerun.log"
run_e2e "$LOG2" "${SCRIPTS}/setup-linux.sh" --yes --no-install
assert_eq "re-run exits 0" 0 $?
assert_contains "re-run reports unchanged files" "$LOG2" "unchanged:"
assert_not_contains "re-run rewrites nothing"    "$LOG2" "wrote ${PREFIX}"
UNCHANGED_COUNT="$(grep -c 'unchanged:' "$LOG2")"
if [ "$UNCHANGED_COUNT" -ge 7 ]; then ok "re-run leaves all 7 managed files untouched"; else
    bad "re-run leaves all 7 managed files untouched (only ${UNCHANGED_COUNT})"
fi

RESTARTS2="$(grep -c 'restart bluetooth.service' "$BT_LOG" 2>/dev/null || true)"
assert_eq "re-run does not restart bluetooth again" "$RESTARTS1" "$RESTARTS2"

LOG3="${WORK}/e2e-uninstall.log"
run_e2e "$LOG3" "${SCRIPTS}/uninstall-linux.sh" --yes
assert_eq "uninstall exits 0" 0 $?
for f in "$UDEV" "$MODCONF" "$DROPIN" "$PATHUNIT" "$SVCUNIT" "$HELPER_INSTALLED" "$STATE"; do
    if [ -e "$f" ]; then bad "removed ${f#"$PREFIX"}"; else ok "removed ${f#"$PREFIX"}"; fi
done
if [ -f "${PREFIX}/etc/udev/rules.d/10-unrelated.rules" ]; then ok "unrelated udev rule preserved"; else bad "unrelated udev rule preserved"; fi
if [ -f "${PREFIX}/etc/systemd/system/unrelated.service" ]; then ok "unrelated unit preserved"; else bad "unrelated unit preserved"; fi
if [ -d "${PREFIX}/etc/systemd/system/bluetooth.service.d" ]; then bad "empty drop-in dir removed"; else ok "empty drop-in dir removed"; fi

# Legacy udev rule from the previous RemoteGamepad instructions is superseded.
printf 'KERNEL=="uinput", MODE="0660", GROUP="uinput", OPTIONS+="static_node=uinput"\n' \
    > "${PREFIX}/etc/udev/rules.d/99-uinput.rules"
LOG4="${WORK}/e2e-legacy.log"
run_e2e "$LOG4" "${SCRIPTS}/setup-linux.sh" --yes --no-install
assert_contains "legacy uinput rule removed" "$LOG4" "removed ${PREFIX}/etc/udev/rules.d/99-uinput.rules"
if [ -e "${PREFIX}/etc/udev/rules.d/99-uinput.rules" ]; then bad "legacy rule gone"; else ok "legacy rule gone"; fi
# A hand-written rule with different content must be kept, only warned about.
printf 'KERNEL=="uinput", GROUP="wheel"\n' > "${PREFIX}/etc/udev/rules.d/99-uinput.rules"
LOG5="${WORK}/e2e-legacy-custom.log"
run_e2e "$LOG5" "${SCRIPTS}/setup-linux.sh" --yes --no-install
assert_contains "foreign uinput rule only warned about" "$LOG5" "may override"
if [ -f "${PREFIX}/etc/udev/rules.d/99-uinput.rules" ]; then ok "foreign rule preserved"; else bad "foreign rule preserved"; fi
run_e2e "${WORK}/e2e-cleanup.log" "${SCRIPTS}/uninstall-linux.sh" --yes --remove-group

# A distribution that already ships "bluetoothd --compat" needs no drop-in, and
# a drop-in left over from an earlier run must be cleaned up instead of fighting it.
DIR="$(make_scenario e2e-compat id=debian id_like=debian pretty='Debian GNU/Linux 13' version=13 \
        pkgmgr=apt bluetoothd=/usr/libexec/bluetooth/bluetoothd compat=1 adapter=1)"
PREFIX="${DIR}/root"
mkdir -p "${PREFIX}/etc/systemd/system/bluetooth.service.d"
printf '[Service]\nExecStart=\nExecStart=/stale/bluetoothd --compat\n' \
    > "${PREFIX}/etc/systemd/system/bluetooth.service.d/10-remote-gamepad-compat.conf"
LOG6="${WORK}/e2e-vendor-compat.log"
run_e2e "$LOG6" "${SCRIPTS}/setup-linux.sh" --yes --no-install
assert_eq "vendor-compat setup exits 0" 0 $?
assert_contains "vendor compat detected" "$LOG6" "already enabled by this distribution"
if [ -e "${PREFIX}/etc/systemd/system/bluetooth.service.d/10-remote-gamepad-compat.conf" ]; then
    bad "redundant drop-in removed"
else ok "redundant drop-in removed"; fi
if [ -f "${PREFIX}/etc/systemd/system/remote-gamepad-sdp-permissions.path" ]; then
    ok "watcher still installed on vendor-compat distros"
else bad "watcher still installed on vendor-compat distros"; fi
run_e2e "${WORK}/e2e-compat-cleanup.log" "${SCRIPTS}/uninstall-linux.sh" --yes --remove-group

# ==========================================================================
head1 "Library unit tests"
# shellcheck source-path=SCRIPTDIR source=../lib/remote-gamepad-linux.sh
RG_SCRIPT_DIR="${SCRIPTS}" . "${SCRIPTS}/lib/remote-gamepad-linux.sh"

assert_eq "strips systemd ExecStart prefixes" "/usr/bin/x" "$(rg_strip_exec_prefix '-@/usr/bin/x')"
assert_eq "leaves plain paths alone"          "/usr/bin/x" "$(rg_strip_exec_prefix '/usr/bin/x')"

for probe in "fedora:fedora" "rhel:fedora" "ubuntu:debian" "linuxmint:debian" "manjaro:arch" "opensuse-tumbleweed:suse"; do
    id="${probe%%:*}"; want="${probe##*:}"
    printf 'ID=%s\nID_LIKE=%s\n' "$id" "${want/fedora/rhel fedora}" > "${WORK}/os-release-${id}"
    got="$(RG_OS_RELEASE="${WORK}/os-release-${id}" bash -c "RG_SCRIPT_DIR='${SCRIPTS}'; . '${SCRIPTS}/lib/remote-gamepad-linux.sh'; rg_load_os_release; rg_distro_family")"
    assert_eq "distro family for ${id}" "$want" "$got"
done

# rg_install_file idempotency (real writes, in the temp dir)
TARGET="${WORK}/install-target.conf"
OUT1="$(printf 'hello\n' | rg_install_file "$TARGET" 0644)"
OUT2="$(printf 'hello\n' | rg_install_file "$TARGET" 0644)"
OUT3="$(printf 'changed\n' | rg_install_file "$TARGET" 0644)"
assert_contains "rg_install_file writes" <(printf '%s' "$OUT1") "wrote"
assert_contains "rg_install_file is idempotent" <(printf '%s' "$OUT2") "unchanged"
assert_contains "rg_install_file updates on change" <(printf '%s' "$OUT3") "wrote"
assert_eq "file content updated" "changed" "$(cat "$TARGET")"
assert_eq "file mode applied" "644" "$(stat -c '%a' "$TARGET")"

# dry-run must not write
DRY_TARGET="${WORK}/dry-target.conf"
RG_DRY_RUN=1 rg_install_file "$DRY_TARGET" 0644 <<< "nope" > /dev/null
if [ -e "$DRY_TARGET" ]; then bad "dry-run writes nothing"; else ok "dry-run writes nothing"; fi

# ==========================================================================
head1 "/run/sdp permission helper"
HELPER="${SCRIPTS}/remote-gamepad-fix-sdp-permissions"
OWN_GROUP="$(id -gn)"

# 1. missing socket is a successful no-op
OUT="$(REMOTE_GAMEPAD_SDP_SOCKET="${WORK}/no-such-socket" REMOTE_GAMEPAD_SDP_GROUP="$OWN_GROUP" "$HELPER" 2>&1)"
assert_eq "no-op when socket is absent" 0 $?

# 2. unknown group fails loudly
OUT="$(REMOTE_GAMEPAD_SDP_SOCKET="${WORK}/no-such-socket" REMOTE_GAMEPAD_SDP_GROUP="definitely-not-a-group-$$" "$HELPER" 2>&1)"
HELPER_STATUS=$?
assert_eq "missing group is an error" 1 "$HELPER_STATUS"
assert_contains "missing group is explained" <(printf '%s' "$OUT") "does not exist"

# 3. real unix socket: mode is corrected and the run is idempotent
if command -v python3 >/dev/null 2>&1; then
    SOCK="${WORK}/sdp.sock"
    python3 - "$SOCK" <<'PY'
import socket, sys, os
path = sys.argv[1]
if os.path.exists(path):
    os.unlink(path)
s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
s.bind(path)
os.chmod(path, 0o600)
PY
    OUT="$(REMOTE_GAMEPAD_SDP_SOCKET="$SOCK" REMOTE_GAMEPAD_SDP_GROUP="$OWN_GROUP" "$HELPER" 2>&1)"
    assert_eq "helper succeeds on a real socket" 0 $?
    assert_eq "socket mode corrected to 0660" "660" "$(stat -c '%a' "$SOCK")"
    assert_eq "socket group set" "$OWN_GROUP" "$(stat -c '%G' "$SOCK")"
    OUT2="$(REMOTE_GAMEPAD_SDP_SOCKET="$SOCK" REMOTE_GAMEPAD_SDP_GROUP="$OWN_GROUP" "$HELPER" 2>&1)"
    assert_eq "second run is a silent no-op" "" "$OUT2"
else
    say "  (skipped socket test: python3 unavailable)"
fi

# ==========================================================================
head1 "Static checks"
for script in "${SCRIPTS}"/*.sh "${SCRIPTS}"/lib/*.sh "${SCRIPTS}"/tests/*.sh; do
    if bash -n "$script" 2>/dev/null; then ok "bash -n $(basename "$script")"; else bad "bash -n $(basename "$script")"; fi
done
if sh -n "${SCRIPTS}/remote-gamepad-fix-sdp-permissions"; then ok "sh -n remote-gamepad-fix-sdp-permissions"; else bad "sh -n remote-gamepad-fix-sdp-permissions"; fi

# systemd unit sanity
for unit in "${REPO_ROOT}"/systemd/*.path "${REPO_ROOT}"/systemd/*.service; do
    if grep -q '^\[Unit\]' "$unit"; then ok "unit header $(basename "$unit")"; else bad "unit header $(basename "$unit")"; fi
done
assert_contains "path unit watches /run/sdp" "${REPO_ROOT}/systemd/remote-gamepad-sdp-permissions.path" "PathChanged=/run/sdp"
assert_contains "path unit ordered before bluetooth" "${REPO_ROOT}/systemd/remote-gamepad-sdp-permissions.path" "Before=bluetooth.service"
assert_contains "service unit carries the group" "${REPO_ROOT}/systemd/remote-gamepad-sdp-permissions.service" "REMOTE_GAMEPAD_SDP_GROUP=remote-gamepad"

# ==========================================================================
printf '\n\033[1mResult:\033[0m %d passed, %d failed\n' "$PASS" "$FAIL"
[ "$FAIL" -eq 0 ] || exit 1
