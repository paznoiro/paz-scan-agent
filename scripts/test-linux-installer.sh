#!/usr/bin/env bash
# Tries a built .deb the way a Debian or Ubuntu desk meets it: installs it with apt as from a
# terminal, checks the agent it started answers, upgrades over the running agent, switches the login
# start off and on again, then removes and purges it. CI runs it on every build. Run it with sudo
# from the account the agent should run as; it replaces, and then removes, any Paz Scan Agent
# installed here.
#
#   sudo ./scripts/test-linux-installer.sh artifacts/paz-scan-agent_1.2.2_amd64.deb 1.2.2
set -euo pipefail

deb=$(realpath "${1:?usage: $0 <package.deb> <version>}")
version="${2:?usage: $0 <package.deb> <version>}"
user="${SUDO_USER:-}"

step() { printf '\n==> %s\n' "$1"; }
ok() { echo "ok: $1"; }
fail() { echo "FAILED: $1" >&2; exit 1; }
check() { # message, then the command that must succeed
  local message=$1
  shift
  if "$@"; then ok "$message"; else fail "$message"; fi
}

[ "$(id -u)" = 0 ] || fail "run it with sudo"
case "$user" in "" | root) fail "run it with sudo from the account the agent should run as" ;; esac

home=$(getent passwd "$user" | cut -d: -f6)
exe=/opt/paz-scan-agent/PazScanAgent
autostart=/etc/xdg/autostart/paz-scan-agent.desktop
user_autostart="$home/.config/autostart/paz-scan-agent.desktop"
data_dir="$home/.local/share/Paz Scan Agent"
settings="$data_dir/appsettings.json"
origin="http://127.0.0.1:3000"
default_port=47316
other_port=47399

as_user() { runuser -u "$user" -- env -i HOME="$home" PATH=/usr/bin:/bin "$@"; }
get() { curl -fsS -m 90 -H "Origin: $origin" "http://127.0.0.1:$1$2" 2>/dev/null; }
status_code() { curl -s -o /dev/null -m 10 -w '%{http_code}' -H "Origin: $3" "http://127.0.0.1:$1$2" 2>/dev/null || true; }
silent() { ! curl -s -m 2 "http://127.0.0.1:$1/" > /dev/null 2>&1; }
contains() { grep -qF -- "$2" <<< "$1"; }
# Who runs an agent from the package's executable, one line each. The same test as prerm's.
agent_users() {
  local proc path
  for proc in /proc/[0-9]*; do
    case "$(cat "$proc/comm" 2> /dev/null)" in PazScanAgent | paz-scan-agent) ;; *) continue ;; esac
    path=$(readlink "$proc/exe" 2> /dev/null) || path=$exe
    [ "$path" = "$exe" ] && stat -c %U "$proc"
  done
  return 0
}

wait_agent() { # port
  local deadline=$((SECONDS + 60))
  until status=$(get "$1" /v1/status); do
    [ "$SECONDS" -lt "$deadline" ] || fail "(after 60 s) the agent answers on port $1"
    sleep 0.5
  done
  ok "the agent answers on port $1"
}

wait_until() { # message, then the command that must eventually succeed
  local message=$1 deadline=$((SECONDS + 30))
  shift
  until "$@"; do
    [ "$SECONDS" -lt "$deadline" ] || fail "(after 30 s) $message"
    sleep 0.5
  done
  ok "$message"
}

passed=0
on_exit() {
  [ "$passed" = 1 ] && return
  for log in "$data_dir"/logs/*.log; do
    [ -f "$log" ] || continue
    echo "--- $log"
    cat "$log"
  done
}
trap on_exit EXIT

export DEBIAN_FRONTEND=noninteractive
rm -f "$settings" "$user_autostart"

step "Installing $deb"
apt-get update -q > /dev/null
apt-get install -y -q "$deb" > /dev/null
check "the package is installed" test "$(dpkg-query -W -f '${Status}' paz-scan-agent)" = "install ok installed"
check "the agent is in /opt/paz-scan-agent" test -x "$exe"
check "it is on the PATH" test "$(readlink -f /usr/bin/paz-scan-agent)" = "$exe"
check "it starts at every desktop login" test -f "$autostart"
check "it has a menu entry" test -f /usr/share/applications/paz-scan-agent.desktop
check "SANE came with it" dpkg-query -W libsane1

step "Talking to the agent the package started for $user"
wait_agent "$default_port"
check "it says it is Paz Scan Agent" contains "$status" '"name":"Paz Scan Agent"'
check "it is version $version" contains "$status" "\"version\":\"$version\""
check "it offers SANE and network scanners" contains "$status" '"drivers":["sane","escl"]'
check "it runs as $user" test "$(agent_users | sort -u)" = "$user"
check "it lists scanners (SANE and eSCL load)" contains "$(get "$default_port" /v1/devices)" '['
check "it refuses other web pages" test "$(status_code "$default_port" /v1/devices https://evil.example)" = 403

step "Upgrading over the running agent"
as_user mkdir -p "$data_dir"
as_user sh -c "printf '{ \"Agent\": { \"Port\": $other_port } }\n' > \"\$1\"" sh "$settings"
dpkg -i "$deb" > /dev/null
wait_agent "$other_port"
check "the upgraded agent restarted with $user's settings" contains "$status" "\"version\":\"$version\""
check "the old agent was stopped" silent "$default_port"
check "only one agent runs" test "$(agent_users | wc -l)" -eq 1

step "Switching the login start off and on"
as_user paz-scan-agent --unregister-startup > /dev/null
check "--unregister-startup hides the package's entry for $user" grep -qx 'Hidden=true' "$user_autostart"
as_user paz-scan-agent --register-startup > /dev/null
check "--register-startup brings it back" bash -c "! grep -q Hidden= '$user_autostart'"
check "and points it at the installed agent" grep -qxF "Exec=\"$exe\"" "$user_autostart"
rm -f "$user_autostart"

step "Removing"
apt-get remove -y -q paz-scan-agent > /dev/null
wait_until "the agent has stopped" silent "$other_port"
check "no agent is left running" test -z "$(agent_users)"
check "the program folder is gone" test ! -e /opt/paz-scan-agent
check "it is off the PATH" test ! -e /usr/bin/paz-scan-agent
apt-get purge -y -q paz-scan-agent > /dev/null
check "purging removes the login start" test ! -e "$autostart"

rm -f "$settings"
passed=1
step "The installer works"
