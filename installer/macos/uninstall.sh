#!/bin/sh
# Removes Paz Scan Agent installed from the macOS package:
#
#   sudo "/Applications/Paz Scan Agent.app/Contents/Resources/uninstall.sh"
#
# Each user's logs, pending scans and settings in ~/Library/Application Support/Paz Scan Agent are
# left in place; delete that folder too for a clean slate.

label=com.paznoiro.pazscanagent

if [ "$(id -u)" -ne 0 ]; then
  echo "Run it with sudo: sudo \"$0\"" >&2
  exit 1
fi

# Stop it in every session: those running it now, and anyone logged in with the job loaded.
users_uids=$( (for pid in $(pgrep -x PazScanAgent); do ps -o uid= -p "$pid"; done
               for user in $(who | awk '$2 == "console" { print $1 }'); do id -u "$user" 2>/dev/null; done) | sort -u)
for uid in $users_uids; do
  launchctl bootout "gui/$uid/$label" 2>/dev/null || true
done
pkill -x PazScanAgent 2>/dev/null || true

rm -f "/Library/LaunchAgents/$label.plist"
rm -rf "/Applications/Paz Scan Agent.app"
pkgutil --forget "$label" >/dev/null 2>&1 || true

echo "Paz Scan Agent has been removed."
