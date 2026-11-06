#!/usr/bin/env bash
# Builds the macOS package: artifacts/installer/IlluxImageTool-<version>-macOS-arm64.pkg and its .sha256 file.
#
#   installer/macos/build-pkg.sh <version>        (on a Mac with Apple silicon or a GitHub macos-15 runner)
#
# 1. Builds jpegtran (build/fetch-jpegtran.sh).
# 2. Publishes the command-line tool and the watcher daemon self-contained for osx-arm64 into one folder (bin/), and the
#    settings app as "Illux Image Tool.app" (Info.plist; Mach-O files in Contents/MacOS, the rest in
#    Contents/Resources/app), ad-hoc signed.
# 3. Lays out the payload:
#      /Applications/Illux Image Tool.app
#      /Library/Application Support/Illux Image Tool/bin/            (IlluxImageTool, IlluxImageToolWatcher, tools/,
#                                                                     THIRD-PARTY-NOTICES.md)
#      /Library/Application Support/Illux Image Tool/uninstall.sh
#      /Library/LaunchDaemons/dk.illux.imagetool.watcher.plist
#      /Library/LaunchAgents/dk.illux.imagetool.tray.plist
#    with the scripts in installer/macos/scripts (preinstall, postinstall and the common.sh they share) and the
#    template of the folders job (dk.illux.imagetool.folders.plist.in), which postinstall fills in (ruling V15).
# 4. pkgbuild (package id dk.illux.imagetool, the app not relocatable) and productbuild (Apple silicon and macOS 15 or
#    later only), unsigned.
# 5. Writes the checksum file as "<sha256 hex>  <package file name>".
# Nothing is installed on the build machine.
set -euo pipefail

VERSION="${1:-}"
if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Usage: $0 <version>   (for example 0.2.0)" >&2
    exit 1
fi
if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "build-pkg.sh builds the macOS package and needs macOS (pkgbuild, productbuild, codesign)." >&2
    exit 1
fi

PACKAGE_ID='dk.illux.imagetool'
BUNDLE_ID='dk.illux.imagetool.settings'
APP_NAME='Illux Image Tool.app'
DATA_ROOT='Library/Application Support/Illux Image Tool'
RID='osx-arm64'

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
ARTIFACTS="$REPO_ROOT/artifacts"
WORK="$ARTIFACTS/macos-pkg-$VERSION"
PUBLISH="$WORK/publish"
PAYLOAD="$WORK/payload"
NAME="IlluxImageTool-$VERSION-macOS-arm64.pkg"
OUT_DIR="$ARTIFACTS/installer"
OUT="$OUT_DIR/$NAME"

rm -rf "$WORK"
mkdir -p "$PUBLISH" "$PAYLOAD" "$OUT_DIR"

"$REPO_ROOT/build/fetch-jpegtran.sh"

publish() { # name project
    echo "Publishing $1 $VERSION for $RID"
    dotnet publish "$REPO_ROOT/$2" -c Release -r "$RID" --self-contained true \
        -p:PublishSingleFile=false "-p:Version=$VERSION" -o "$PUBLISH/$1" -nologo
}
publish Cli 'src/Illux.ImageTool.Cli/Illux.ImageTool.Cli.csproj'
publish Watcher 'src/Illux.ImageTool.Watcher/Illux.ImageTool.Watcher.csproj'
publish Settings 'src/Illux.ImageTool.Settings/Illux.ImageTool.Settings.csproj'

# --- bin/: the command-line tool and the daemon share one folder; a file both publish must be identical ---
BIN="$PAYLOAD/$DATA_ROOT/bin"
mkdir -p "$BIN"
for app in Cli Watcher; do
    (cd "$PUBLISH/$app" && find . -type f) | while IFS= read -r rel; do
        target="$BIN/$rel"
        if [[ -e "$target" ]]; then
            cmp -s "$PUBLISH/$app/$rel" "$target" || { echo "Publish conflict: '$rel' differs between the apps." >&2; exit 1; }
            continue
        fi
        mkdir -p "$(dirname "$target")"
        cp -p "$PUBLISH/$app/$rel" "$target"
    done
done
for rel in IlluxImageTool IlluxImageToolWatcher tools/jpegtran/jpegtran; do
    [[ -x "$BIN/$rel" ]] || { echo "bin/ lacks the executable $rel." >&2; exit 1; }
done
# The notices of the open-source components; jpegtran's own licence is already in bin/tools/jpegtran/.
install -m 0644 "$REPO_ROOT/THIRD-PARTY-NOTICES.md" "$BIN/THIRD-PARTY-NOTICES.md"
[[ -f "$BIN/tools/jpegtran/LICENSE.md" ]] || { echo "bin/ lacks tools/jpegtran/LICENSE.md." >&2; exit 1; }
install -m 0700 "$HERE/uninstall.sh" "$PAYLOAD/$DATA_ROOT/uninstall.sh"

# --- the settings app bundle ---
# codesign takes every file in Contents/MacOS for code, so only Mach-O files (the main executable, the runtime's and
# Avalonia's libraries) are real files there. The rest of the published app (managed assemblies, deps.json, folders)
# lives in Contents/Resources/app. The two folders link to each other's files, so each looks like the complete
# published folder: the main executable finds the app's .dll next to itself, and the .NET host (which resolves that
# .dll's real path, Contents/Resources/app) finds the runtime libraries next to the .dll.
APP="$PAYLOAD/Applications/$APP_NAME"
MACOS="$APP/Contents/MacOS"
APP_FILES="$APP/Contents/Resources/app"
mkdir -p "$MACOS" "$APP_FILES"
cp -Rp "$PUBLISH/Settings/." "$APP_FILES/"
# The settings app never converts images; jpegtran (a program of its own) is only in bin/.
rm -rf "$APP_FILES/tools"
for entry in "$APP_FILES"/*; do
    name="$(basename "$entry")"
    if [[ -f "$entry" ]] && file -b "$entry" | grep -q '^Mach-O'; then
        mv "$entry" "$MACOS/$name"
        ln -s "../../MacOS/$name" "$entry"
    else
        ln -s "../Resources/app/$name" "$MACOS/$name"
    fi
done
if find "$APP_FILES" -type f -exec file -b {} + | grep -q '^Mach-O'; then
    echo "Contents/Resources/app holds Mach-O files; they belong in Contents/MacOS:" >&2
    find "$APP_FILES" -type f -exec file {} + | grep 'Mach-O' >&2
    exit 1
fi
[[ -x "$MACOS/IlluxImageToolSettings" ]] || { echo "The settings app lacks IlluxImageToolSettings." >&2; exit 1; }
cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
    <key>CFBundleDisplayName</key>
    <string>Illux Image Tool</string>
    <key>CFBundleExecutable</key>
    <string>IlluxImageToolSettings</string>
    <key>CFBundleIdentifier</key>
    <string>$BUNDLE_ID</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>Illux Image Tool</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <key>LSMinimumSystemVersion</key>
    <string>15.0</string>
    <key>LSUIElement</key>
    <false/>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
EOF
plutil -lint "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"

# Ad-hoc signatures only (unsigned distribution): the libraries first, then the bundle with its main executable.
# find exits non-zero (and set -e stops the build) when codesign fails on any of them.
find "$MACOS" -type f ! -name IlluxImageToolSettings -exec codesign --force -s - {} +
codesign --force --deep -s - "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"

# --- launchd jobs ---
mkdir -p "$PAYLOAD/Library/LaunchDaemons" "$PAYLOAD/Library/LaunchAgents"
install -m 0644 "$HERE/dk.illux.imagetool.watcher.plist" "$PAYLOAD/Library/LaunchDaemons/"
install -m 0644 "$HERE/dk.illux.imagetool.tray.plist" "$PAYLOAD/Library/LaunchAgents/"
plutil -lint "$PAYLOAD/Library/LaunchDaemons/dk.illux.imagetool.watcher.plist" "$PAYLOAD/Library/LaunchAgents/dk.illux.imagetool.tray.plist"

# Modes as on a Mac (pkgbuild --ownership recommended makes everything root-owned): nobody but the owner writes;
# folders that exist on every Mac keep their usual modes.
chmod -R go-w "$PAYLOAD"
find "$PAYLOAD" -type d -exec chmod 0755 {} +
chmod 0775 "$PAYLOAD/Applications"
chmod 0700 "$PAYLOAD/$DATA_ROOT/uninstall.sh"

# --- scripts ---
SCRIPTS="$WORK/scripts"
mkdir -p "$SCRIPTS"
install -m 0755 "$HERE/scripts/preinstall" "$HERE/scripts/postinstall" "$SCRIPTS/"
install -m 0644 "$HERE/scripts/common.sh" "$HERE/scripts/dk.illux.imagetool.folders.plist.in" "$SCRIPTS/"
for script in preinstall postinstall common.sh; do sh -n "$SCRIPTS/$script"; done
sed 's/@FOLDER_ACCOUNT@/someone/' "$SCRIPTS/dk.illux.imagetool.folders.plist.in" > "$WORK/folders-check.plist"
plutil -lint "$WORK/folders-check.plist"

# --- component package: the app is installed in /Applications only (not relocated to a copy found elsewhere) ---
COMPONENTS="$WORK/components.plist"
pkgbuild --analyze --root "$PAYLOAD" "$COMPONENTS"
count=0
while plutil -extract "$count" xml1 -o /dev/null "$COMPONENTS" >/dev/null 2>&1; do count=$((count + 1)); done
if [[ "$count" != 1 || "$(plutil -extract 0.RootRelativeBundlePath raw "$COMPONENTS")" != "Applications/$APP_NAME" ]]; then
    echo "Expected exactly one bundle (Applications/$APP_NAME) in the payload:" >&2
    plutil -p "$COMPONENTS" >&2
    exit 1
fi
plutil -replace 0.BundleIsRelocatable -bool NO "$COMPONENTS"
plutil -replace 0.BundleIsVersionChecked -bool NO "$COMPONENTS"
plutil -replace 0.BundleOverwriteAction -string upgrade "$COMPONENTS"

mkdir -p "$WORK/packages"
pkgbuild --root "$PAYLOAD" --install-location / --component-plist "$COMPONENTS" --ownership recommended \
    --scripts "$SCRIPTS" --identifier "$PACKAGE_ID" --version "$VERSION" "$WORK/packages/IlluxImageTool.pkg"

# --- product archive: Apple silicon, macOS 15 or later, the startup disk ---
cat > "$WORK/distribution.xml" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<installer-gui-script minSpecVersion="2">
    <title>Illux Image Tool $VERSION</title>
    <options customize="never" require-scripts="false" hostArchitectures="arm64"/>
    <domains enable_anywhere="false" enable_currentUserHome="false" enable_localSystem="true"/>
    <allowed-os-versions>
        <os-version min="15.0"/>
    </allowed-os-versions>
    <choices-outline>
        <line choice="default">
            <line choice="$PACKAGE_ID"/>
        </line>
    </choices-outline>
    <choice id="default"/>
    <choice id="$PACKAGE_ID" visible="false">
        <pkg-ref id="$PACKAGE_ID"/>
    </choice>
    <pkg-ref id="$PACKAGE_ID" version="$VERSION" onConclusion="none">IlluxImageTool.pkg</pkg-ref>
</installer-gui-script>
EOF
rm -f "$OUT" "$OUT.sha256"
productbuild --distribution "$WORK/distribution.xml" --package-path "$WORK/packages" "$OUT"

hash="$(shasum -a 256 "$OUT" | awk '{print $1}')"
printf '%s  %s\n' "$hash" "$NAME" > "$OUT.sha256"
echo "Built $OUT"
echo "SHA-256 $hash"
echo "Package size $(awk -v b="$(stat -f %z "$OUT")" 'BEGIN { printf "%.1f MB", b / 1048576 }')"
