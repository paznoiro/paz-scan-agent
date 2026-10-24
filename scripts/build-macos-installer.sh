#!/usr/bin/env bash
# Builds the macOS installer: artifacts/PazScanAgent-<version>-macos-<arch>.pkg.
#   ./scripts/build-macos-installer.sh            # this machine's architecture
#   ./scripts/build-macos-installer.sh arm64      # Apple Silicon
#   ./scripts/build-macos-installer.sh x64        # Intel
#
# The package installs "Paz Scan Agent.app" into /Applications and a LaunchAgent that starts it at
# every user's login, stops the old agent first and starts the new one for whoever is logged in.
# Uninstall: sudo "/Applications/Paz Scan Agent.app/Contents/Resources/uninstall.sh".
# Needs a Mac (pkgbuild, productbuild) with the .NET 10 SDK. The package is unsigned.
set -euo pipefail
cd "$(dirname "$0")/.."

arch="${1:-$(uname -m)}"
case "$arch" in
  arm64|aarch64) rid=osx-arm64; arch=arm64; host_arch=arm64 ;;
  x64|x86_64|amd64) rid=osx-x64; arch=x64; host_arch=x86_64 ;;
  *) echo "usage: $0 [arm64|x64]" >&2; exit 2 ;;
esac

identifier=com.paznoiro.pazscanagent
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
setup="artifacts/PazScanAgent-$version-macos-$arch.pkg"

./scripts/publish-macos.sh "$arch"

work="artifacts/pkg-$rid"
root="$work/root"
rm -rf "$work"
mkdir -p "$root/Applications" "$root/Library/LaunchAgents" "$work/scripts"

ditto "artifacts/$rid/Paz Scan Agent.app" "$root/Applications/Paz Scan Agent.app"
install -m 755 installer/macos/uninstall.sh "$root/Applications/Paz Scan Agent.app/Contents/Resources/uninstall.sh"
install -m 644 "installer/macos/$identifier.plist" "$root/Library/LaunchAgents/$identifier.plist"
install -m 755 installer/macos/scripts/preinstall installer/macos/scripts/postinstall "$work/scripts/"

# Installer "relocates" a relocatable bundle to wherever it finds another copy with the same id, such
# as this build's own artifacts folder: pin the app to /Applications. Version checking would skip
# the app when going back to an older release: off, as the Windows installer allows that too.
# BundleOverwriteAction stays "upgrade": the new bundle replaces the old one whole, so no file of an
# older version lingers.
pkgbuild --analyze --root "$root" "$work/component.plist"
set_bundle_flag() { # index key value
  /usr/libexec/PlistBuddy -c "Delete :$1:$2" "$work/component.plist" > /dev/null 2>&1 || true
  /usr/libexec/PlistBuddy -c "Add :$1:$2 bool $3" "$work/component.plist"
}
i=0
while /usr/libexec/PlistBuddy -c "Print :$i" "$work/component.plist" > /dev/null 2>&1; do
  set_bundle_flag "$i" BundleIsRelocatable false
  set_bundle_flag "$i" BundleIsVersionChecked false
  i=$((i + 1))
done

pkgbuild --root "$root" --component-plist "$work/component.plist" --scripts "$work/scripts" \
  --identifier "$identifier" --version "$version" --install-location / \
  "$work/PazScanAgent.pkg"

cat > "$work/distribution.xml" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<installer-gui-script minSpecVersion="2">
  <title>Paz Scan Agent $version</title>
  <options customize="never" require-scripts="false" hostArchitectures="$host_arch"/>
  <domains enable_localSystem="true" enable_currentUserHome="false" enable_anywhere="false"/>
  <allowed-os-versions>
    <os-version min="12.0"/>
  </allowed-os-versions>
  <choices-outline>
    <line choice="default">
      <line choice="$identifier"/>
    </line>
  </choices-outline>
  <choice id="default"/>
  <choice id="$identifier" visible="false">
    <pkg-ref id="$identifier"/>
  </choice>
  <pkg-ref id="$identifier" version="$version" onConclusion="none">PazScanAgent.pkg</pkg-ref>
</installer-gui-script>
EOF

productbuild --distribution "$work/distribution.xml" --package-path "$work" "$setup"
rm -rf "$work"

echo "Installer: $setup ($(du -h "$setup" | cut -f1))"
