#!/usr/bin/env bash
# Tries a built macOS package the way a Mac meets it: installs it, checks the agent it started
# answers, upgrades over the running agent, then uninstalls and checks nothing is left. CI runs it on
# every build. Run it as the logged-in user (not with sudo); it asks sudo for the install steps. It
# replaces, and then removes, any Paz Scan Agent already installed on this Mac.
#
#   ./scripts/test-macos-installer.sh artifacts/PazScanAgent-1.2.2-macos-arm64.pkg 1.2.2
set -euo pipefail

setup="${1:?usage: $0 <package.pkg> <version>}"
version="${2:?usage: $0 <package.pkg> <version>}"

label=com.paznoiro.pazscanagent
app="/Applications/Paz Scan Agent.app"
agent_plist="/Library/LaunchAgents/$label.plist"
data_dir="$HOME/Library/Application Support/Paz Scan Agent"
settings="$data_dir/appsettings.json"
origin="http://127.0.0.1:3000"
default_port=47316
other_port=47399

step() { printf '\n==> %s\n' "$1"; }
ok() { echo "ok: $1"; }
fail() { echo "FAILED: $1" >&2; exit 1; }
check() { # message, then the command that must succeed
  local message=$1
  shift
  if "$@"; then ok "$message"; else fail "$message"; fi
}

# Body of a GET, or nothing (and a non-zero exit) when nobody answers with a 200.
get() { curl -fsS -m 90 -H "Origin: $origin" "http://127.0.0.1:$1$2" 2>/dev/null; }
status_code() { curl -s -o /dev/null -m 10 -w '%{http_code}' -H "Origin: $3" "http://127.0.0.1:$1$2" 2>/dev/null || true; }
silent() { ! curl -s -m 2 "http://127.0.0.1:$1/" > /dev/null 2>&1; }
contains() { grep -qF -- "$2" <<< "$1"; }

wait_agent() { # port
  local deadline=$((SECONDS + 60))
  until status=$(get "$1" /v1/status); do
    [ "$SECONDS" -lt "$deadline" ] || fail "(after 60 s) the agent answers on port $1"
    sleep 0.5
  done
  ok "the agent answers on port $1"
}

wait_gone() { # message, then the command that must eventually succeed
  local message=$1 deadline=$((SECONDS + 30))
  shift
  until "$@"; do
    [ "$SECONDS" -lt "$deadline" ] || fail "(after 30 s) $message"
    sleep 0.5
  done
  ok "$message"
}

install_setup() { sudo installer -pkg "$setup" -target / > /dev/null; }

passed=0
on_exit() {
  [ "$passed" = 1 ] && return
  for log in "$data_dir"/logs/*.log; do
    [ -f "$log" ] || continue
    echo "--- $log"
    cat "$log"
  done
  launchctl print "gui/$(id -u)/$label" 2>&1 | head -40 || true
}
trap on_exit EXIT

rm -f "$settings"

step "Installing $setup"
install_setup
check "the app is in /Applications" test -x "$app/Contents/MacOS/PazScanAgent"
check "it starts at login (LaunchAgent)" test -f "$agent_plist"
check "the package is registered" pkgutil --pkg-info "$label"

step "Talking to the agent the package started"
wait_agent "$default_port"
check "it says it is Paz Scan Agent" contains "$status" '"name":"Paz Scan Agent"'
check "it is version $version" contains "$status" "\"version\":\"$version\""
check "it offers SANE and network scanners" contains "$status" '"drivers":["sane","escl"]'
check "it lists scanners (SANE and eSCL load)" contains "$(get "$default_port" /v1/devices)" '['

check "launchd runs it in this session" launchctl print "gui/$(id -u)/$label"
check "it refuses other web pages" test "$(status_code "$default_port" /v1/devices https://evil.example)" = 403

step "Upgrading over the running agent"
mkdir -p "$data_dir"
printf '{ "Agent": { "Port": %s } }\n' "$other_port" > "$settings"
sudo touch "$app/Contents/MacOS/left-over.dll"
install_setup
check "nothing from the old version lingers" test ! -e "$app/Contents/MacOS/left-over.dll"
wait_agent "$other_port"
check "the upgraded agent restarted with the kept settings" contains "$status" "\"version\":\"$version\""
check "the old agent was stopped" silent "$default_port"

step "Uninstalling"
check "there is an uninstaller" test -x "$app/Contents/Resources/uninstall.sh"
sudo "$app/Contents/Resources/uninstall.sh"
check "the app is gone" test ! -e "$app"
check "it no longer starts at login" test ! -e "$agent_plist"
wait_gone "the agent has stopped" silent "$other_port"
check "launchd no longer has it" bash -c "! launchctl print gui/$(id -u)/$label > /dev/null 2>&1"
check "the package is forgotten" bash -c "! pkgutil --pkg-info $label > /dev/null 2>&1"

rm -f "$settings"
passed=1
step "The installer works"
