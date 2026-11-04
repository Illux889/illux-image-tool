# Shared by preinstall and postinstall (sourced, not run). POSIX sh, run as root by /usr/sbin/installer; anything they
# print goes to /var/log/install.log.
#
# Rule (as on Windows): no file operation as root inside a folder that users can write to. Root and its folders are
# checked with lstat (a link is never followed), and anything that is not what this package would have made is moved
# aside, not reused or deleted.

ROOT='/Library/Application Support/Illux Image Tool'
APP='/Applications/Illux Image Tool.app'
DAEMON_LABEL='dk.illux.imagetool.watcher'
DAEMON_PLIST="/Library/LaunchDaemons/${DAEMON_LABEL}.plist"
TRAY_LABEL='dk.illux.imagetool.tray'
TRAY_PLIST="/Library/LaunchAgents/${TRAY_LABEL}.plist"
# For pkill -f (a regular expression): the menu-bar helper, not a settings window of the same program.
TRAY_PATTERN='Illux Image Tool\.app/Contents/MacOS/IlluxImageToolSettings --tray'

say() { echo "Illux Image Tool ${SCRIPT_NAME:-script}: $*"; }

# Prints why "$1" cannot be used as a root-owned folder of ours, or nothing when it can. With "$2" = strict, group or
# others must not be able to write to it either (Root itself; the daemon refuses to use a Root they can write to).
untrusted_reason() {
    if [ -L "$1" ]; then echo "it is a symbolic link"; return; fi
    if [ ! -d "$1" ]; then echo "it is not a folder"; return; fi
    owner=$(stat -f %u "$1") || { echo "its owner cannot be read"; return; }
    if [ "$owner" != 0 ]; then echo "it is owned by user $owner, not root"; return; fi
    if [ "${2:-}" = strict ]; then
        mode=$(stat -f %Lp "$1") || { echo "its mode cannot be read"; return; }
        if [ $(( 0$mode & 022 )) -ne 0 ]; then echo "group or others may write to it (mode $mode)"; return; fi
    fi
}

# Renames "$1" (the entry itself, also a link) to "<name>.untrusted-<timestamp>" next to it; "$2" is the reason.
move_aside() {
    stamp=$(date +%Y%m%d-%H%M%S)
    aside="$1.untrusted-$stamp"
    n=1
    while [ -e "$aside" ] || [ -L "$aside" ]; do
        n=$((n + 1))
        aside="$1.untrusted-$stamp-$n"
    done
    say "moving '$1' aside to '$aside': $2"
    if ! mv "$1" "$aside"; then
        say "ERROR: '$1' cannot be used ($2) and could not be moved aside"
        return 1
    fi
}

# Root: kept only when it is a real folder owned by root that group and others cannot write to; otherwise moved aside
# and created again. Then root:admin 0755 (the daemon checks: owned by root, no group or other write).
ensure_root() {
    if [ -e "$ROOT" ] || [ -L "$ROOT" ]; then
        reason=$(untrusted_reason "$ROOT" strict)
        if [ -n "$reason" ]; then move_aside "$ROOT" "$reason" || return 1; fi
    fi
    if [ ! -e "$ROOT" ]; then mkdir -m 0755 "$ROOT" || return 1; fi
    reason=$(untrusted_reason "$ROOT")
    if [ -n "$reason" ]; then say "ERROR: '$ROOT' $reason"; return 1; fi
    chown root:admin "$ROOT" && chmod 0755 "$ROOT"
}

# Users of the logged-in desktop sessions: the owners of loginwindow processes, plus the console user.
gui_users() {
    {
        ps -axo uid=,comm= | awk '$2 ~ /(^|\/)loginwindow$/ { print $1 }'
        stat -f %u /dev/console 2>/dev/null
    } | awk '$1 ~ /^[0-9]+$/ && $1 != 0' | sort -un
}

# Users running the menu-bar helper right now.
tray_users() {
    for pid in $(pgrep -f "$TRAY_PATTERN"); do
        ps -o uid= -p "$pid" 2>/dev/null
    done | awk '$1 ~ /^[0-9]+$/' | sort -un
}
