#!/usr/bin/env bash
# Builds the Linux installer: artifacts/paz-scan-agent_<version>_<amd64|arm64>.deb, plus the tarball
# from publish-linux.sh for distributions without dpkg.
#   ./scripts/build-linux-installer.sh            # this machine's architecture
#   ./scripts/build-linux-installer.sh x64
#   ./scripts/build-linux-installer.sh arm64
#
# The package installs the agent into /opt/paz-scan-agent (and /usr/bin/paz-scan-agent), starts it
# at every user's desktop login (/etc/xdg/autostart), pulls in SANE, and restarts running agents
# across upgrades. Needs the .NET 10 SDK and dpkg-deb: any Debian or Ubuntu has it; on macOS,
# `brew install dpkg`.
set -euo pipefail
cd "$(dirname "$0")/.."

arch="${1:-$(uname -m)}"
case "$arch" in
  x64|x86_64|amd64) rid=linux-x64; deb_arch=amd64 ;;
  arm64|aarch64) rid=linux-arm64; deb_arch=arm64 ;;
  *) echo "usage: $0 [x64|arm64]" >&2; exit 2 ;;
esac

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
# A Debian version sorts "~" before anything, so 1.3.0~beta.1 comes before 1.3.0; "-" would start a
# Debian revision and put the beta after the release.
deb_version=$(printf '%s' "$version" | sed 's/-/~/')
setup="artifacts/paz-scan-agent_${deb_version}_$deb_arch.deb"

./scripts/publish-linux.sh "$arch"

root="artifacts/deb-$deb_arch"
rm -rf "$root" "$setup"
mkdir -p "$root/DEBIAN" "$root/opt" "$root/usr/bin" "$root/etc/xdg/autostart" \
  "$root/usr/share/applications" "$root/usr/share/pixmaps"

cp -R "artifacts/$rid/paz-scan-agent" "$root/opt/paz-scan-agent"
ln -s /opt/paz-scan-agent/PazScanAgent "$root/usr/bin/paz-scan-agent"
install -m 644 installer/linux/paz-scan-agent-autostart.desktop "$root/etc/xdg/autostart/paz-scan-agent.desktop"
install -m 644 installer/linux/paz-scan-agent.desktop "$root/usr/share/applications/paz-scan-agent.desktop"
install -m 644 assets/icon.png "$root/usr/share/pixmaps/paz-scan-agent.png"
install -m 755 installer/linux/postinst installer/linux/prerm "$root/DEBIAN/"
# A conffile: someone who switches the login start off by editing it keeps that through upgrades.
echo /etc/xdg/autostart/paz-scan-agent.desktop > "$root/DEBIAN/conffiles"

installed_kb=$(du -sk "$root/opt" "$root/usr" "$root/etc" | awk '{ total += $1 } END { print total }')

# libsane1 brings the USB backends and their udev rules. The .NET runtime inside also loads the
# system's ICU and OpenSSL, which every desktop distribution has; see the README for minimal ones.
cat > "$root/DEBIAN/control" <<EOF
Package: paz-scan-agent
Version: $deb_version
Architecture: $deb_arch
Maintainer: PazNoiro
Installed-Size: $installed_kb
Depends: libsane1 | libsane
Section: graphics
Priority: optional
Homepage: https://github.com/paznoiro/paz-scan-agent
Description: Local scanner bridge for the Paz web app
 Lets the Paz web app use the scanner on this computer: USB scanners through
 SANE and network scanners through eSCL (AirScan). It answers only on
 http://127.0.0.1:47316 and starts at every desktop login.
EOF

dpkg-deb --root-owner-group -Zxz --build "$root" "$setup"
rm -rf "$root"

echo "Installer: $setup ($(du -h "$setup" | cut -f1))"
