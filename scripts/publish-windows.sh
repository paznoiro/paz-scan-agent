#!/usr/bin/env bash
# Builds the Windows agent into artifacts/win-x64 — from macOS, Linux or Windows (Git Bash).
# Self-contained: the person installing it needs no .NET of their own.
set -euo pipefail
cd "$(dirname "$0")/.."
rm -rf artifacts/win-x64
dotnet publish src/PazScan.Agent.Windows -c Release -o artifacts/win-x64 -p:DebugType=none -p:GenerateDocumentationFile=false
echo "Published to artifacts/win-x64 ($(du -sh artifacts/win-x64 | cut -f1))"
echo "Installer: iscc installer/PazScanAgent.iss   (on Windows, Inno Setup 6)"
