#!/bin/sh
# Removes Illux Image Tool from this Mac: run as an administrator with
#   sudo "/Library/Application Support/Illux Image Tool/uninstall.sh"
# Stops the watcher daemon and the menu-bar helpers, removes their launchd jobs, the app and the program files, and
# forgets the package so a later install starts clean. config.json, the logs and the other data stay in
# /Library/Application Support/Illux Image Tool (delete that folder by hand to remove them too).
set -u

ROOT='/Library/Application Support/Illux Image Tool'
APP='/Applications/Illux Image Tool.app'
DAEMON_LABEL='dk.illux.imagetool.watcher'
TRAY_LABEL='dk.illux.imagetool.tray'
TRAY_PATTERN='Illux Image Tool\.app/Contents/MacOS/IlluxImageToolSettings --tray'
PACKAGE_ID='dk.illux.imagetool'

if [ "$(id -u)" != 0 ]; then
    echo "Run this as an administrator: sudo \"$ROOT/uninstall.sh\"" >&2
    exit 1
fi
if [ -L "$ROOT" ]; then
    echo "ERROR: '$ROOT' is a symbolic link; nothing was removed." >&2
    exit 1
fi

status=0
step() { echo "$*"; }
problem() { echo "ERROR: $*" >&2; status=1; }

if launchctl bootout "system/$DAEMON_LABEL" 2>/dev/null; then step "Stopped the watcher daemon."; fi

# The menu-bar helper of every desktop user (loginwindow owners, the console user) and of whoever still runs one.
users=$( {
    ps -axo uid=,comm= | awk '$2 ~ /(^|\/)loginwindow$/ { print $1 }'
    stat -f %u /dev/console 2>/dev/null
    for pid in $(pgrep -f "$TRAY_PATTERN"); do ps -o uid= -p "$pid" 2>/dev/null; done
} | awk '$1 ~ /^[0-9]+$/ && $1 != 0' | sort -un )
for uid in $users; do
    launchctl bootout "gui/$uid/$TRAY_LABEL" 2>/dev/null && step "Stopped the menu-bar helper of user $uid."
done
pkill -f "$TRAY_PATTERN" 2>/dev/null

rm -f "/Library/LaunchDaemons/$DAEMON_LABEL.plist" "/Library/LaunchAgents/$TRAY_LABEL.plist" || problem "could not remove the launchd jobs"
rm -rf "$APP" || problem "could not remove $APP"
# Inside the root-owned data folder only: the program files, and the daemon's socket folder.
rm -rf "$ROOT/bin" "$ROOT/run" || problem "could not remove the program files in $ROOT"

pkgutil --forget "$PACKAGE_ID" >/dev/null 2>&1 && step "Forgot the package $PACKAGE_ID."

rm -f "$ROOT/uninstall.sh"
if [ "$status" = 0 ]; then
    step "Illux Image Tool was removed. Settings and logs are kept in $ROOT."
fi
exit "$status"
