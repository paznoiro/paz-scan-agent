#!/usr/bin/env bash
# Builds the macOS agent as a background .app bundle (self-contained: no .NET needed to run it).
#   ./scripts/publish-macos.sh            # this machine's architecture
#   ./scripts/publish-macos.sh arm64      # Apple Silicon
#   ./scripts/publish-macos.sh x64        # Intel
# Run it on a Mac: the SDK ad-hoc signs the executable there, and Apple Silicon runs nothing unsigned.
# The installer package around it: scripts/build-macos-installer.sh.
set -euo pipefail
cd "$(dirname "$0")/.."

arch="${1:-$(uname -m)}"
case "$arch" in
  arm64|aarch64) rid=osx-arm64; arch=arm64 ;;
  x64|x86_64|amd64) rid=osx-x64; arch=x64 ;;
  *) echo "usage: $0 [arm64|x64]" >&2; exit 2 ;;
esac

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
# CFBundleVersion must be numbers and dots: 1.3.0-beta.1 is 1.3.0 to macOS.
bundle_version="${version%%[-+]*}"
out="artifacts/$rid"
app="$out/Paz Scan Agent.app"
rm -rf "$out"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

dotnet publish src/PazScan.Agent.Desktop -c Release -r "$rid" --self-contained true \
  -o "$app/Contents/MacOS" -p:DebugType=none -p:GenerateDocumentationFile=false \
  -p:Version="$version"

# The icon Finder and System Settings → Login Items show.
iconset="$out/AppIcon.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" assets/icon.png --out "$iconset/icon_${size}x${size}.png" > /dev/null
  sips -z $((size * 2)) $((size * 2)) assets/icon.png --out "$iconset/icon_${size}x${size}@2x.png" > /dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/AppIcon.icns"
rm -rf "$iconset"

# LSMinimumSystemVersion: the .NET runtime's own minimum (otool -l libcoreclr.dylib | grep minos).
cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>Paz Scan Agent</string>
  <key>CFBundleDisplayName</key>
  <string>Paz Scan Agent</string>
  <key>CFBundleIdentifier</key>
  <string>com.paznoiro.pazscanagent</string>
  <key>CFBundleVersion</key>
  <string>$bundle_version</string>
  <key>CFBundleShortVersionString</key>
  <string>$bundle_version</string>
  <key>CFBundleExecutable</key>
  <string>PazScanAgent</string>
  <key>CFBundleIconFile</key>
  <string>AppIcon</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>LSMinimumSystemVersion</key>
  <string>12.0</string>
  <key>LSBackgroundOnly</key>
  <true/>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>NSLocalNetworkUsageDescription</key>
  <string>Paz Scan Agent finds scanners on your local network.</string>
</dict>
</plist>
EOF

# Signing: every native binary inside is ad-hoc signed (the SDK signs the executable, Microsoft and
# NAPS2 ship theirs signed), which is all Apple Silicon needs to run it. The bundle as a whole is not
# sealed: the .NET assemblies and SANE's config sit beside the executable in Contents/MacOS, where
# codesign expects only code. A downloaded copy is therefore blocked by Gatekeeper until allowed in
# System Settings → Privacy & Security. Distribution without that step needs a Developer ID
# certificate and notarization (see README).

echo "Published to $app"
echo "Register it to start at login:"
echo "  \"$app/Contents/MacOS/PazScanAgent\" --register-startup"
