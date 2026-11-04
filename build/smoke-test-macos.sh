#!/usr/bin/env bash
# End-to-end test of the macOS package on a throw-away Mac (GitHub Actions macos-15 runner). NEVER run this on a real
# Mac: it installs, upgrades and uninstalls Illux Image Tool with sudo, plants folders in /Library/Application Support
# and /Users/Shared, and loads launchd jobs.
#
#   build/smoke-test-macos.sh <package> <upgrade package> <version> <upgrade version> <work folder>
#
# Run as an administrator who may use sudo without a password (the runner user), not as root.
# 1. Plants an untrusted (user-owned) data folder, then installs <package> with /usr/sbin/installer.
# 2. Checks the package receipt, owners and modes of every installed item, the launchd jobs (AbandonProcessGroup set
#    on the daemon), the signed app bundle, that the untrusted folder was moved aside, that folders every Mac has kept
#    their modes, and that the daemon runs. As the (non-root) user: the daemon's status, a "check" request through the
#    control socket (seen as root in the root-only service logs), and that the user cannot write into the data folder.
# 3. Writes config.json in place as the user (as the settings app does) with a watched folder below /Users/Shared and
#    drops a 20 x 15 cm, 150 ppi JPEG: the output is 300 ppi, 2362 x 1772 px (default rule "Small").
# 4. Upgrades the way the daemon does: plants updating.flag and an "Installing" update state, then runs the updater's
#    exact command line (sh -c 'exec /usr/sbin/installer ...') as a child of a throw-away launchd job that carries the
#    daemon's label and AbandonProcessGroup, so the package's preinstall boots out the installer's parent job. The
#    upgrade must complete: new version, daemon restarted, config.json unchanged, flag removed, state Idle, the
#    watched folder still works.
# 5. Uninstalls with uninstall.sh: daemon, jobs, app, program files and receipt gone; config.json and logs kept.
# Exits 1 if any check failed.
set -uo pipefail

if [[ $# -ne 5 ]]; then
    echo "Usage: $0 <package> <upgrade package> <version> <upgrade version> <work folder>" >&2
    exit 2
fi
PKG="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
UPGRADE_PKG="$(cd "$(dirname "$2")" && pwd)/$(basename "$2")"
VERSION=$3
UPGRADE_VERSION=$4
WORK=$5
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

ROOT='/Library/Application Support/Illux Image Tool'
APP='/Applications/Illux Image Tool.app'
LABEL='dk.illux.imagetool.watcher'
DAEMON_PLIST="/Library/LaunchDaemons/$LABEL.plist"
TRAY_PLIST='/Library/LaunchAgents/dk.illux.imagetool.tray.plist'
PACKAGE_ID='dk.illux.imagetool'
CLI="$ROOT/bin/IlluxImageTool"
SOCKET="$ROOT/run/control.sock"
WATCH='/Users/Shared/illux-smoke'
# The updater's command line on macOS (Updater.MacInstallScript), run as: /bin/sh -c <script> sh <package> <log>.
# Checked against the source before the upgrade test uses it.
MAC_INSTALL_SCRIPT='exec /usr/sbin/installer -pkg "$1" -target / >"$2" 2>&1'
FAKE_PLIST='/Library/LaunchDaemons/dk.illux.imagetool.smoke-updater.plist'

FAILURES=0
pass() { echo "PASS  $*"; }
fail() { echo "::error::FAIL  $*"; FAILURES=$((FAILURES + 1)); }
check() { local message=$1; shift; if "$@"; then pass "$message"; else fail "$message"; fi; }
check_eq() { if [[ "$3" == "$2" ]]; then pass "$1 ($3)"; else fail "$1 (expected '$2', got '$3')"; fi; }
section() { echo; echo "=== $* ==="; }
wait_until() { # seconds command...
    local end=$((SECONDS + $1)); shift
    until "$@"; do
        (( SECONDS >= end )) && return 1
        sleep 1
    done
}
not() { ! "$@"; }
# A process that exists and has not ended (a child that ended stays a zombie until it is waited for).
alive() { local stat; stat=$(ps -o stat= -p "$1" 2>/dev/null); [[ -n "$stat" && "$stat" != Z* ]]; }
no_acl() { [[ "$(sudo ls -lde "$1" | wc -l | tr -d ' ')" == 1 ]]; }

owner_mode() { stat -f '%Su:%Sg %Mp%Lp' "$1" 2>/dev/null || echo missing; }
daemon_print() { launchctl print "system/$LABEL" 2>/dev/null; }
daemon_loaded() { launchctl print "system/$LABEL" >/dev/null 2>&1; }
daemon_state() { daemon_print | awk '$1 == "state" && $2 == "=" { print $3; exit }'; }
daemon_running() { [[ "$(daemon_state)" == running ]]; }
daemon_pid() { daemon_print | awk '$1 == "pid" && $2 == "=" { print $3; exit }'; }
service_log() { sudo find "$ROOT/service-logs" -name 'watcher-*.log' -exec cat {} + 2>/dev/null; }
log_count() { service_log | grep -c -- "$1"; }
log_has() { service_log | grep -q -- "$1"; }
log_count_at_least() { (( $(log_count "$1") >= $2 )); }
plist_value() { plutil -extract "$2" raw -o - "$1" 2>/dev/null || echo missing; }
receipt_version() { pkgutil --pkg-info "$PACKAGE_ID" 2>/dev/null | awk '$1 == "version:" { print $2 }'; }
sha() { shasum -a 256 "$1" | awk '{ print $1 }'; }
image_value() { sips -g "$2" "$1" 2>/dev/null | awk -v k="$2:" '$1 == k { printf "%.0f", $2 }'; }
system_folder_modes() {
    for folder in / /Applications /Library '/Library/Application Support' /Library/LaunchDaemons /Library/LaunchAgents; do
        echo "$folder $(owner_mode "$folder")"
    done
}

dump_diagnostics() {
    section 'Diagnostics'
    echo '--- install.log (Illux Image Tool lines) ---'
    grep -E 'Illux Image Tool|dk\.illux\.imagetool' /var/log/install.log | tail -n 80
    echo '--- service logs ---'
    service_log | tail -n 80
    echo '--- launchd-stderr.log ---'
    sudo cat "$ROOT/service-logs/launchd-stderr.log" 2>/dev/null | tail -n 40
    echo '--- launchctl print ---'
    daemon_print | head -n 40
    mkdir -p "$WORK/logs"
    grep -E 'Illux Image Tool|dk\.illux\.imagetool|installer\[' /var/log/install.log > "$WORK/logs/install.log" 2>/dev/null
    service_log > "$WORK/logs/service.log"
    sudo cat "$ROOT/service-logs/launchd-stderr.log" > "$WORK/logs/launchd-stderr.log" 2>/dev/null
    sudo cat "$ROOT"/update/*.log > "$WORK/logs/upgrade-installer.log" 2>/dev/null
}

[[ "$(id -u)" != 0 ]] || { echo "Run as an administrator with sudo, not as root." >&2; exit 2; }
id -Gn | tr ' ' '\n' | grep -qx admin || { echo "The user must be in the admin group." >&2; exit 2; }
sudo -n true || { echo "sudo must work without a password." >&2; exit 2; }
[[ -f "$PKG" && -f "$UPGRADE_PKG" ]] || { echo "Package not found." >&2; exit 2; }
mkdir -p "$WORK"

# =====================================================================================================================
section 'Install over an untrusted data folder'
SYSTEM_MODES_BEFORE="$(system_folder_modes)"
# A data folder that someone other than root controls (as a user who created it before the install could).
sudo mkdir -p "$ROOT"
sudo chown "$(id -u)" "$ROOT"
printf '{ "planted": true }\n' > "$ROOT/config.json"
INSTALL_LOG_START=$(wc -l < /var/log/install.log)

sudo installer -pkg "$PKG" -target /
check "installer exit code 0 for $(basename "$PKG")" test $? -eq 0
check_eq 'package receipt version' "$VERSION" "$(receipt_version)"

aside=( "$ROOT".untrusted-* )
check "the user-owned data folder was moved aside (${aside[*]})" test "${#aside[@]}" -eq 1 -a -d "${aside[0]}"
check 'the planted config.json is in the moved-aside folder' grep -q planted "${aside[0]}/config.json"
check_eq 'a fresh install writes the default config.json' '{}' "$(cat "$ROOT/config.json")"

section 'Owners and modes'
check_eq 'data folder' 'root:admin 0755' "$(owner_mode "$ROOT")"
check_eq 'config.json' 'root:admin 0664' "$(owner_mode "$ROOT/config.json")"
check_eq 'config.json link count' 1 "$(stat -f %l "$ROOT/config.json")"
check_eq 'bin/' 'root:wheel 0755' "$(owner_mode "$ROOT/bin")"
check_eq 'bin/IlluxImageTool' 'root:wheel 0755' "$(owner_mode "$CLI")"
check_eq 'bin/IlluxImageToolWatcher' 'root:wheel 0755' "$(owner_mode "$ROOT/bin/IlluxImageToolWatcher")"
check_eq 'bin/tools/jpegtran/jpegtran' 'root:wheel 0755' "$(owner_mode "$ROOT/bin/tools/jpegtran/jpegtran")"
check_eq 'files in bin/ not owned by root or writable by group/others' '' "$(find "$ROOT/bin" \( ! -user root -o -perm +022 \) -print)"
check_eq 'uninstall.sh' 'root:wheel 0700' "$(owner_mode "$ROOT/uninstall.sh")"
check_eq 'update/' 'root:wheel 0700' "$(owner_mode "$ROOT/update")"
check_eq 'service-logs/' 'root:wheel 0700' "$(owner_mode "$ROOT/service-logs")"
check_eq 'run/' 'root:wheel 0755' "$(owner_mode "$ROOT/run")"
check_eq 'logs/' 'root:wheel 1777' "$(owner_mode "$ROOT/logs")"
check_eq 'runs/' 'root:wheel 1777' "$(owner_mode "$ROOT/runs")"
check_eq 'daemon plist' 'root:wheel 0644' "$(owner_mode "$DAEMON_PLIST")"
check_eq 'tray agent plist' 'root:wheel 0644' "$(owner_mode "$TRAY_PLIST")"
for folder in "$ROOT" "$ROOT/bin" "$ROOT/update" "$ROOT/service-logs" "$ROOT/run"; do
    check "no ACL on ${folder#"$ROOT"}/" no_acl "$folder"
done
check_eq 'nothing in the app is writable by group or others' '' "$(find "$APP" -perm +022 -print)"
check_eq 'folders every Mac has kept their owners and modes' "$SYSTEM_MODES_BEFORE" "$(system_folder_modes)"
echo "$SYSTEM_MODES_BEFORE"

section 'launchd jobs'
check_eq 'daemon AbandonProcessGroup' true "$(plist_value "$DAEMON_PLIST" AbandonProcessGroup)"
check_eq 'daemon RunAtLoad' true "$(plist_value "$DAEMON_PLIST" RunAtLoad)"
check_eq 'daemon KeepAlive' true "$(plist_value "$DAEMON_PLIST" KeepAlive)"
check_eq 'daemon UserName' root "$(plist_value "$DAEMON_PLIST" UserName)"
check_eq 'daemon Label' "$LABEL" "$(plist_value "$DAEMON_PLIST" Label)"
check_eq 'daemon program' "$ROOT/bin/IlluxImageToolWatcher" "$(plist_value "$DAEMON_PLIST" ProgramArguments.0)"
check_eq 'tray agent Label' dk.illux.imagetool.tray "$(plist_value "$TRAY_PLIST" Label)"
check_eq 'tray agent program' "$APP/Contents/MacOS/IlluxImageToolSettings" "$(plist_value "$TRAY_PLIST" ProgramArguments.0)"
check_eq 'tray agent argument' --tray "$(plist_value "$TRAY_PLIST" ProgramArguments.1)"
postinstall_lines="$(tail -n +"$((INSTALL_LOG_START + 1))" /var/log/install.log | grep 'Illux Image Tool postinstall')"
echo "$postinstall_lines"
check 'postinstall started the daemon' grep -q 'started the watcher daemon' <<< "$postinstall_lines"
check 'postinstall reported no error' not grep -q 'ERROR' <<< "$postinstall_lines"
# The menu-bar helper needs a desktop session; a headless runner may have none, so it is reported, not required.
echo "Menu-bar helper: $(grep 'menu-bar helper' <<< "$postinstall_lines" || echo 'no desktop session found')"

section 'App bundle'
INFO="$APP/Contents/Info.plist"
check_eq 'CFBundleIdentifier' dk.illux.imagetool.settings "$(plist_value "$INFO" CFBundleIdentifier)"
check_eq 'CFBundleShortVersionString' "$VERSION" "$(plist_value "$INFO" CFBundleShortVersionString)"
check_eq 'LSMinimumSystemVersion' 15.0 "$(plist_value "$INFO" LSMinimumSystemVersion)"
check_eq 'LSUIElement' false "$(plist_value "$INFO" LSUIElement)"
check 'main executable in Contents/MacOS' test -x "$APP/Contents/MacOS/IlluxImageToolSettings"
check 'app bundle signature verifies (codesign --verify --deep --strict)' codesign --verify --deep --strict "$APP"
check 'app bundle is ad-hoc signed' grep -q 'Signature=adhoc' <<< "$(codesign -dv "$APP" 2>&1)"
check_eq 'command-line tool version' "$VERSION" "$("$CLI" --version 2>&1)"
# The app's managed files are linked from Contents/MacOS; starting the window shows that the .NET host finds them.
console_user=$(stat -f %Su /dev/console)
if [[ "$console_user" == "$(id -un)" ]]; then
    # This user has a desktop session, so the menu-bar helper must have been started for them.
    check "postinstall started the menu-bar helper for $(id -un)" grep -q "started the menu-bar helper for user $(id -u)\$" <<< "$postinstall_lines"
    check 'the menu-bar helper is running' wait_until 20 pgrep -f 'IlluxImageToolSettings --tray'
    "$APP/Contents/MacOS/IlluxImageToolSettings" > "$WORK/settings-window.log" 2>&1 &
    window_pid=$!
    sleep 15
    check 'the settings app starts and keeps running' kill -0 "$window_pid"
    kill "$window_pid" 2>/dev/null
    wait "$window_pid" 2>/dev/null
    cat "$WORK/settings-window.log"
else
    echo "No desktop session of $(id -un) (console user: $console_user); the settings app is not started."
fi
echo "Menu-bar helpers running: $(pgrep -fl 'IlluxImageToolSettings --tray' || echo none)"

section 'Daemon'
check 'daemon is running (launchctl print, as the user)' wait_until 60 daemon_running
check 'daemon loaded from the installed plist' grep -q "path = $DAEMON_PLIST" <<< "$(daemon_print)"
FIRST_PID=$(daemon_pid)
echo "Daemon pid $FIRST_PID"
check 'control socket created' wait_until 30 test -S "$SOCKET"
check_eq 'control socket owner and mode' 'root:wheel 0666' "$(owner_mode "$SOCKET")"
check 'the daemon logs into service-logs/' wait_until 30 log_has 'Watcher started'
check 'the user cannot read service-logs/' not ls "$ROOT/service-logs" 2>/dev/null
check 'the user cannot create files in the data folder' not touch "$ROOT/smoke-user-file" 2>/dev/null
check 'the user cannot create files in update/' not touch "$ROOT/update/smoke-user-file" 2>/dev/null

section 'Control socket'
printf 'check\n' | nc -U -w 5 "$SOCKET"
check 'the user sent "check" to the control socket' test $? -eq 0
check 'the daemon accepted the request (service log: Update check requested)' wait_until 30 log_has 'Update check requested'

# =====================================================================================================================
section 'Watched folder'
rm -rf "$WATCH"
mkdir -p "$WATCH/In" "$WATCH/Out" "$WATCH/Error" "$WATCH/Originals"
# The 20 x 15 cm case of the brief: at 150 ppi that is 1181 x 886 px.
python3 -I - "$WORK/print.png" <<'EOF'
import struct, sys, zlib
w, h = 1181, 886
rows = b''.join(b'\0' + bytes(v for x in range(w) for v in ((x * 255) // w, (y * 255) // h, 128)) for y in range(h))
def chunk(kind, data):
    return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xffffffff)
png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0))
png += chunk(b'pHYs', struct.pack('>IIB', 5906, 5906, 1)) + chunk(b'IDAT', zlib.compress(rows, 6)) + chunk(b'IEND', b'')
open(sys.argv[1], 'wb').write(png)
EOF
sips -s format jpeg -s dpiWidth 150 -s dpiHeight 150 "$WORK/print.png" --out "$WORK/print-20x15cm-150ppi.jpg" >/dev/null
TEST_JPEG="$WORK/print-20x15cm-150ppi.jpg"
check_eq 'test JPEG size and resolution' '1181 886 150 150' "$(image_value "$TEST_JPEG" pixelWidth) $(image_value "$TEST_JPEG" pixelHeight) $(image_value "$TEST_JPEG" dpiWidth) $(image_value "$TEST_JPEG" dpiHeight)"

# V14: any local user may give the root-owned config.json a second name in the sticky logs/ folder (same volume).
CONFIG="$ROOT/config.json"
ln "$CONFIG" "$ROOT/logs/smoke-config-link"
check_eq 'a user gave config.json a second name in logs/' 2 "$(stat -f %l "$CONFIG")"

# The settings app saves config.json in place as an administrator (ConfigStore.SaveInPlace, covered by the unit
# tests); this test does the same from the shell (no sudo), with the second name in place.
inode_before=$(stat -f %i "$CONFIG")
cat > "$CONFIG" <<EOF
{
  "folders": [
    { "name": "smoke", "in": "$WATCH/In", "out": "$WATCH/Out", "error": "$WATCH/Error", "parameters": [] }
  ],
  "pollSeconds": 1
}
EOF
check 'an administrator can save config.json in place (without sudo)' test $? -eq 0
check_eq 'config.json is the same file after the save (in place)' "$inode_before" "$(stat -f %i "$CONFIG")"
check_eq 'the second name shows the saved content' "$(sha "$CONFIG")" "$(sha "$ROOT/logs/smoke-config-link")"
check_eq 'config.json keeps its owner and mode after the save' 'root:admin 0664' "$(owner_mode "$CONFIG")"
check 'the daemon loaded the new configuration' wait_until 30 log_has 'Configuration loaded: 1 watched folder(s) enabled'

drop_and_check() { # file name, label
    mkdir -p "$WORK/drop"
    cp "$TEST_JPEG" "$WORK/drop/$1"
    mv "$WORK/drop/$1" "$WATCH/In/$1"
    check "$2: the daemon converted a file dropped into the watched In folder" wait_until 120 test -f "$WATCH/Out/$1"
    if [[ -f "$WATCH/Out/$1" ]]; then
        sleep 1
        local out="$WATCH/Out/$1"
        check_eq "$2: output is 300 ppi and 2362 x 1772 px (20 x 15 cm kept, rule Small)" '2362 1772 300 300' \
            "$(image_value "$out" pixelWidth) $(image_value "$out" pixelHeight) $(image_value "$out" dpiWidth) $(image_value "$out" dpiHeight)"
    fi
    check "$2: the original was moved to Originals" wait_until 30 test -f "$WATCH/Originals/$1"
}
drop_and_check print-20x15cm-150ppi.jpg 'after install'

# =====================================================================================================================
section 'Upgrade the way the daemon runs it'
shipped_script=$(sed -n 's/.*const string MacInstallScript = "\(.*\)";.*/\1/p' "$REPO_ROOT/src/Illux.ImageTool.Core/Update/Updater.cs" | sed 's/\\"/"/g')
check_eq 'the simulated command line is Updater.MacInstallScript' "$MAC_INSTALL_SCRIPT" "$shipped_script"
CONFIG_HASH=$(sha "$CONFIG")
CONFIG_INODE=$(stat -f %i "$CONFIG")
CHECKS_BEFORE=$(log_count 'Update check requested')
PREINSTALL_BOOTOUTS=$(grep -c 'preinstall: stopped the watcher daemon' /var/log/install.log)
UPGRADE_START_LINE=$(wc -l < /var/log/install.log)
UPGRADE_WINDOW_PID=''
if [[ "$console_user" == "$(id -un)" ]]; then
    # An open settings window: preinstall must end it (its program files are replaced).
    "$APP/Contents/MacOS/IlluxImageToolSettings" > "$WORK/window-during-upgrade.log" 2>&1 &
    UPGRADE_WINDOW_PID=$!
    sleep 5
    check 'a settings window is open before the upgrade' alive "$UPGRADE_WINDOW_PID"
fi

# As the updater does before it starts the installer (and as an interrupted update could leave it).
sudo sh -c 'printf "smoke\n" > "$1/updating.flag"; printf "{ \"availableVersion\": \"9.9.9\", \"phase\": \"Installing\" }\n" > "$1/update-state.json"' sh "$ROOT"
# The downloaded package lives in the root-only update/ folder, the installer's log next to it.
UPDATE_PKG="$ROOT/update/$(basename "$UPGRADE_PKG")"
UPDATE_LOG="$ROOT/update/smoke-install-$UPGRADE_VERSION.log"
sudo install -m 0600 -o root -g wheel "$UPGRADE_PKG" "$UPDATE_PKG"

# A throw-away job in place of the daemon: same label, AbandonProcessGroup, and the updater's command line as its child
# while the job's own process stays (as the daemon does), so the package's preinstall boots out the installer's parent.
sudo launchctl bootout "system/$LABEL"
check 'the real daemon job was booted out for the test' wait_until 30 not daemon_loaded
xml() { sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g' <<< "$1"; }
cat > "$WORK/smoke-updater.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key><string>$LABEL</string>
    <key>ProgramArguments</key>
    <array>
        <string>/bin/sh</string>
        <string>-c</string>
        <string>$(xml "/bin/sh -c '$MAC_INSTALL_SCRIPT' sh \"\$1\" \"\$2\" & wait")</string>
        <string>sh</string>
        <string>$(xml "$UPDATE_PKG")</string>
        <string>$(xml "$UPDATE_LOG")</string>
    </array>
    <key>UserName</key><string>root</string>
    <key>RunAtLoad</key><true/>
    <key>AbandonProcessGroup</key><true/>
    <key>StandardOutPath</key><string>/dev/null</string>
    <key>StandardErrorPath</key><string>/dev/null</string>
</dict>
</plist>
EOF
plutil -lint "$WORK/smoke-updater.plist"
echo "Throw-away job command: $(plutil -extract ProgramArguments.2 raw -o - "$WORK/smoke-updater.plist")"
sudo install -m 0644 -o root -g wheel "$WORK/smoke-updater.plist" "$FAKE_PLIST"
sudo launchctl bootstrap system "$FAKE_PLIST"
check 'the throw-away updater job started' test $? -eq 0

upgrade_finished() { sudo grep -Eq 'installer: The (install|upgrade) (was successful|failed)' "$UPDATE_LOG" 2>/dev/null; }
check 'the installer started by the throw-away job finished' wait_until 400 upgrade_finished
echo '--- installer output ---'
sudo cat "$UPDATE_LOG" 2>/dev/null
check 'the installer reports success' sudo grep -Eq 'installer: The (install|upgrade) was successful' "$UPDATE_LOG"
check 'preinstall booted out the job that started the installer' \
    test "$(grep -c 'preinstall: stopped the watcher daemon' /var/log/install.log)" -gt "$PREINSTALL_BOOTOUTS"
sudo rm -f "$FAKE_PLIST"
check_eq 'package receipt version after the upgrade' "$UPGRADE_VERSION" "$(receipt_version)"
check 'daemon is running after the upgrade' wait_until 60 daemon_running
check 'the daemon job comes from the installed plist again' grep -q "path = $DAEMON_PLIST" <<< "$(daemon_print)"
check_eq 'the daemon job has AbandonProcessGroup after the upgrade' true "$(plist_value "$DAEMON_PLIST" AbandonProcessGroup)"
NEW_PID=$(daemon_pid)
check "the daemon was restarted (pid $FIRST_PID -> $NEW_PID)" test -n "$NEW_PID" -a "$NEW_PID" != "$FIRST_PID"
check_eq 'command-line tool version after the upgrade' "$UPGRADE_VERSION" "$("$CLI" --version 2>&1)"
check_eq 'app version after the upgrade' "$UPGRADE_VERSION" "$(plist_value "$APP/Contents/Info.plist" CFBundleShortVersionString)"
check 'app bundle signature verifies after the upgrade' codesign --verify --deep --strict "$APP"
check_eq 'config.json unchanged by the upgrade' "$CONFIG_HASH" "$(sha "$CONFIG")"
# config.json had a second name (logs/), so postinstall gave it a file of its own with the same content.
check 'config.json got a file of its own (new inode)' test "$(stat -f %i "$CONFIG")" != "$CONFIG_INODE"
check_eq 'config.json link count after the upgrade' 1 "$(stat -f %l "$CONFIG")"
check 'postinstall reported the copy' grep -q 'config.json has more than one link' <<< "$(tail -n +"$((UPGRADE_START_LINE + 1))" /var/log/install.log)"
check_eq 'nothing in the app is writable by group or others after the upgrade' '' "$(find "$APP" -perm +022 -print)"
if [[ -n "$UPGRADE_WINDOW_PID" ]]; then
    check 'preinstall ended the open settings window' not alive "$UPGRADE_WINDOW_PID"
    kill "$UPGRADE_WINDOW_PID" 2>/dev/null
    wait "$UPGRADE_WINDOW_PID" 2>/dev/null
fi
check_eq 'config.json owner and mode after the upgrade' 'root:admin 0664' "$(owner_mode "$CONFIG")"
check 'updating.flag removed' not test -e "$ROOT/updating.flag"
check 'update state reset to Idle' grep -q '"phase": "Idle"' "$ROOT/update-state.json"
check 'update state no longer says Installing' not grep -q Installing "$ROOT/update-state.json"
check_eq 'data folder after the upgrade' 'root:admin 0755' "$(owner_mode "$ROOT")"
check_eq 'update/ after the upgrade' 'root:wheel 0700' "$(owner_mode "$ROOT/update")"
check_eq 'folders every Mac has kept their owners and modes after the upgrade' "$SYSTEM_MODES_BEFORE" "$(system_folder_modes)"
tail -n +"$((UPGRADE_START_LINE + 1))" /var/log/install.log | grep -E 'Illux Image Tool (pre|post)install'

check 'control socket back after the upgrade' wait_until 30 test -S "$SOCKET"
printf 'check\n' | nc -U -w 5 "$SOCKET"
check 'the user sent "check" to the upgraded daemon' test $? -eq 0
check 'the upgraded daemon accepted the request' wait_until 30 log_count_at_least 'Update check requested' $((CHECKS_BEFORE + 1))
# The check itself asks GitHub for releases; when that answer arrives, the log names the running version.
sleep 5
if log_has "running $UPGRADE_VERSION)"; then pass "the daemon reports running $UPGRADE_VERSION"
else echo "Note: no update check result naming the running version (GitHub not reached?)"; fi
drop_and_check upgraded.jpg 'after the upgrade'

# =====================================================================================================================
section 'Uninstall'
sudo "$ROOT/uninstall.sh"
check 'uninstall.sh exit code 0' test $? -eq 0
check 'daemon job removed' wait_until 30 not daemon_loaded
check 'no daemon process left' not pgrep -f "$ROOT/bin/IlluxImageToolWatcher"
check 'daemon plist removed' not test -e "$DAEMON_PLIST"
check 'tray agent plist removed' not test -e "$TRAY_PLIST"
check 'app removed' not test -e "$APP"
check 'program files removed' not test -e "$ROOT/bin"
check 'uninstall.sh removed' not test -e "$ROOT/uninstall.sh"
check 'package receipt forgotten' not pkgutil --pkg-info "$PACKAGE_ID"
check_eq 'config.json kept' "$CONFIG_HASH" "$(sha "$CONFIG" 2>/dev/null)"
check 'logs/ kept' test -d "$ROOT/logs"
check 'service-logs/ kept' sudo test -d "$ROOT/service-logs"
check_eq 'downloaded packages removed from update/' '' "$(sudo find "$ROOT/update" -name '*.pkg*' -print)"
check_eq 'folders every Mac has kept their owners and modes after the uninstall' "$SYSTEM_MODES_BEFORE" "$(system_folder_modes)"

dump_diagnostics
echo
if (( FAILURES > 0 )); then
    echo "::error::$FAILURES check(s) failed"
    exit 1
fi
echo "All checks passed."
