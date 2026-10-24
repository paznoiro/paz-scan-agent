#!/usr/bin/env bash
# Builds the Linux agent (self-contained: no .NET needed to run it) and a tarball of it.
#   ./scripts/publish-linux.sh            # this machine's architecture
#   ./scripts/publish-linux.sh x64
#   ./scripts/publish-linux.sh arm64
# → artifacts/linux-<arch>/paz-scan-agent/ and artifacts/paz-scan-agent-<version>-linux-<arch>.tar.gz,
# which unpacks into a paz-scan-agent folder. The .deb around it: scripts/build-linux-installer.sh.
# Works from macOS too.
set -euo pipefail
cd "$(dirname "$0")/.."

arch="${1:-$(uname -m)}"
case "$arch" in
  x64|x86_64|amd64) rid=linux-x64 ;;
  arm64|aarch64) rid=linux-arm64 ;;
  *) echo "usage: $0 [x64|arm64]" >&2; exit 2 ;;
esac

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
out="artifacts/$rid/paz-scan-agent"
tarball="artifacts/paz-scan-agent-$version-$rid.tar.gz"
rm -rf "artifacts/$rid" "$tarball"

dotnet publish src/PazScan.Agent.Desktop -c Release -r "$rid" --self-contained true \
  -o "$out" -p:DebugType=none -p:GenerateDocumentationFile=false \
  -p:Version="$version"

# NuGet unpacks some files as rwxr--r--; nothing here needs to be writable or run but the apphost.
find "$out" -type d -exec chmod 755 {} +
find "$out" -type f -exec chmod 644 {} +
chmod 755 "$out/PazScanAgent"
[ -f "$out/createdump" ] && chmod 755 "$out/createdump"

# COPYFILE_DISABLE: macOS tar would add ._ metadata files.
COPYFILE_DISABLE=1 tar -czf "$tarball" -C "artifacts/$rid" paz-scan-agent

echo "Published to $out"
echo "Tarball: $tarball"
echo "Run: $out/PazScanAgent"
