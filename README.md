# Paz Scan Agent

A small desktop program for Windows, macOS and Linux that lets the Paz web app use the scanner on
the user's desk. A browser cannot reach a scanner itself; this agent does it through
[NAPS2.Sdk](https://www.naps2.com/sdk/) and answers Paz on `http://127.0.0.1:47316`.

```
Paz (browser) ──fetch──► 127.0.0.1:47316 ──► NAPS2.Sdk ──► WIA / TWAIN / SANE (USB) · eSCL (network)
```

- **Windows:** WIA, plus TWAIN through NAPS2's 32-bit worker (`NAPS2.Worker.exe`), plus eSCL.
- **macOS:** SANE (bundled with the agent) plus eSCL. The Apple ImageCaptureCore driver is not in
  the NAPS2 NuGet build, so it is not used.
- **Linux:** SANE (the system's `libsane`) plus eSCL.
- **Network scanners:** eSCL / AirScan, found over mDNS, no driver needed.
- **Runs as:** a tray icon started at sign-in on Windows; a background process started at login on
  macOS (LaunchAgent) and Linux (XDG autostart). It is not a Windows service, because scanner
  drivers expect the signed-in user's desktop.
- **Licences:** NAPS2.Sdk is LGPL 2.1 (used unmodified, as a separate library), and its dependencies
  are MIT or Apache 2.0. The macOS/Linux build also uses ImageSharp (Six Labors Split License,
  Apache 2.0 for open-source/transitive use) and, on macOS, bundled SANE. Nothing here is paid.

## Install

Every [release](https://github.com/paznoiro/paz-scan-agent/releases) has an installer for each
platform. Each one starts the agent at login and replaces an older version in place.

| Platform | Download | Installs |
|---|---|---|
| Windows 10/11 (x64) | `PazScanAgentSetup-<version>.exe` | For the current user, no admin rights. Starts the agent now. |
| macOS 12+, Apple silicon | `PazScanAgent-<version>-macos-arm64.pkg` | `/Applications/Paz Scan Agent.app` and a LaunchAgent for every user. Asks for an admin password. Starts the agent now for whoever is logged in. |
| macOS 12+, Intel | `PazScanAgent-<version>-macos-x64.pkg` | The same, for Intel Macs. |
| Debian 12+, Ubuntu 22.04+ | `paz-scan-agent_<version>_amd64.deb` or `_arm64.deb` | `/opt/paz-scan-agent`, SANE (`libsane1`), and a login start for every desktop user. `sudo apt install ./paz-scan-agent_*.deb` from a terminal also starts it now. |
| Other Linux | `paz-scan-agent-<version>-linux-x64.tar.gz` or `-arm64` | Nothing: unpack, run `paz-scan-agent/PazScanAgent`, and `--register-startup` it. Install SANE yourself ([Linux notes](#linux)). |

None of them is signed, so the first run asks for a click:
- **Windows:** SmartScreen says "Windows protected your PC": **More info → Run anyway**.
- **macOS:** "Apple could not verify…": open **System Settings → Privacy & Security** and click
  **Open Anyway** next to the package's name, then open it again. (macOS 14 and earlier: Control-click
  the package → **Open**.) Installing from Terminal shows no prompt:
  `sudo installer -pkg PazScanAgent-<version>-macos-arm64.pkg -target /`.

To uninstall: **Settings → Apps** on Windows;
`sudo "/Applications/Paz Scan Agent.app/Contents/Resources/uninstall.sh"` on macOS;
`sudo apt remove paz-scan-agent` on Debian/Ubuntu. The macOS and Linux ones leave each user's logs and
settings in the data folder (see [Logs](#logs)); the Windows one removes them.

## Layout

| Path | What |
|---|---|
| `src/PazScan.Core` | The HTTP API, origin checks, scan sessions and the NAPS2 backend. Cross-platform. |
| `src/PazScan.Agent.Windows` | The Windows tray app (`PazScanAgent.exe`). Builds on macOS too. |
| `src/PazScan.Agent.Desktop` | The macOS/Linux agent: same API, same logs, no tray. |
| `src/PazScan.Agent.Console` | The same agent with **demo scanners**, for working on the web app without a scanner. |
| `tests/PazScan.Core.Tests` | API and unit tests (in-memory server, demo backend). |
| `installer/PazScanAgent.iss` | Inno Setup script: a per-user install that needs no admin rights. |
| `installer/macos` | The macOS package's LaunchAgent, install scripts and uninstaller. |
| `installer/linux` | The `.deb`'s desktop entries and maintainer scripts. |
| `scripts` | Publish, build and test the installers (`build-*`, `test-*`, `publish-*`). |

## Develop

```sh
dotnet test                                                   # all tests
dotnet run --project src/PazScan.Agent.Console                # demo agent on :47316
dotnet run --project src/PazScan.Agent.Console -- --pages ~/x # plus "Demo folder": scans the images in ~/x
dotnet run --project src/PazScan.Agent.Desktop                # real scanners on macOS/Linux
```

On macOS, `dotnet run` also copies the bundled SANE libraries next to the app. On Linux, install
`libsane` first (see [Platform notes](#platform-notes)).

There are three demo scanners:
- **Demo scanner** feeds six generated pages. The fourth is blank. Scanning both sides, most backs come
  out blank.
- **Demo scanner (jams)** jams on the third sheet.
- **Demo folder** feeds the PNG and JPEG files in the `--pages` folder. Put in a photo of a printed
  separator sheet to try splitting.

## Logs

The agent writes to `agent-YYYYMMDD.log` in its data folder:

| Platform | Logs |
|---|---|
| Windows | `%LOCALAPPDATA%\Paz Scan Agent\logs` |
| macOS | `~/Library/Application Support/Paz Scan Agent/logs` |
| Linux | `~/.local/share/Paz Scan Agent/logs` (or `$XDG_DATA_HOME`) |

A log rotates to `agent-YYYYMMDD-1.log`, `-2` and so on at 5 MB; files older than 7 days are
removed, and at most 20 are kept. The Windows tray menu has **View live logs**, which tails the
current file, and **Open logs folder**. On macOS/Linux, run `PazScanAgent --open-logs` or tail the
file yourself.

Every HTTP request, scanner listing, capability query, scan and scanned page (path, size, type,
dimensions, DPI and side) is logged. If the system shows a printer but the agent lists no scanner,
install the manufacturer's scanner driver or software: a print-only queue has no scanner behind it,
and eSCL scanners must advertise themselves over mDNS.

## Release

The application, tray, log window, installer, and shortcuts use the artwork in
`assets/paz-scan-agent.png`. After replacing it, run `scripts\update-icon.ps1` on Windows
to regenerate `assets/icon.png` and the multi-resolution `src/PazScan.Agent.Windows/app.ico`.

All builds are self-contained, so the person running them needs no .NET installed. CI builds and
tries every installer (see [CI](#ci)); the scripts below do the same on your machine.

### Windows

```sh
./scripts/publish-windows.sh          # → artifacts/win-x64 (self-contained, ~160 MB unpacked)
```

The installer is built on Windows with [Inno Setup 6](https://jrsoftware.org/isinfo.php):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
# → artifacts\PazScanAgentSetup-1.0.0.exe (~50 MB); -Version defaults to <Version> in Directory.Build.props
```

The script installs anything missing first, for the current user only. It needs no admin rights and
does not change PATH:
- **.NET 10 SDK:** installed with Microsoft's `dotnet-install.ps1` into `%LOCALAPPDATA%\Microsoft\dotnet`.
- **Inno Setup 6.7.3:** the official signed installer from GitHub, checked against its SHA-256 before
  it runs, installed into `%LOCALAPPDATA%\Programs\Inno Setup 6`.

`-NoInstall` stops with a message instead. A copy that is already installed, whether for all users,
this user or on PATH, is used as it is.

To try a setup end to end (install, upgrade over the running agent, uninstall), run
`scripts\test-installer.ps1 -Setup artifacts\PazScanAgentSetup-1.0.0.exe -Version 1.0.0`. It replaces,
then removes, any Paz Scan Agent installed for the current user.

### macOS

On a Mac with the .NET 10 SDK (the SDK signs the executable there, and Apple silicon runs nothing
unsigned, so the macOS build cannot be made on Windows or Linux):

```sh
./scripts/build-macos-installer.sh arm64   # → artifacts/PazScanAgent-<version>-macos-arm64.pkg (~50 MB)
./scripts/build-macos-installer.sh x64     # → …-macos-x64.pkg, for Intel Macs
./scripts/test-macos-installer.sh artifacts/PazScanAgent-<version>-macos-arm64.pkg <version>
```

The test installs the package (it asks for `sudo`), checks the agent it started, upgrades over it,
uninstalls and checks nothing is left; it replaces, then removes, any Paz Scan Agent on that Mac.

Without the package, `./scripts/publish-macos.sh arm64` (or `x64`) builds just
`artifacts/osx-arm64/Paz Scan Agent.app` (~130 MB). Register that copy to start at login with:

```sh
"artifacts/osx-arm64/Paz Scan Agent.app/Contents/MacOS/PazScanAgent" --register-startup
```

That writes `~/Library/LaunchAgents/com.paznoiro.pazscanagent.plist` and starts the agent now;
`--unregister-startup` stops it and removes that file.

### Linux

```sh
./scripts/build-linux-installer.sh x64     # → artifacts/paz-scan-agent_<version>_amd64.deb (~35 MB)
                                           #   + artifacts/paz-scan-agent-<version>-linux-x64.tar.gz
./scripts/build-linux-installer.sh arm64   # → …_arm64.deb + …-linux-arm64.tar.gz
sudo ./scripts/test-linux-installer.sh artifacts/paz-scan-agent_<version>_amd64.deb <version>
```

Building the `.deb` needs `dpkg-deb`: Debian and Ubuntu have it, and on macOS it is
`brew install dpkg`. `./scripts/publish-linux.sh x64` builds just the tarball, from any OS. The test
installs the package with apt, checks the agent it started for the account that ran `sudo`, upgrades
over it, switches the login start off and on, then removes and purges it.

From the tarball, run `paz-scan-agent/PazScanAgent`; `--register-startup` writes
`~/.config/autostart/paz-scan-agent.desktop`. For a headless machine or a server, use a systemd user
unit instead (see [Linux](#linux)).

## CI

Every push to `master` and every pull request builds every project, runs `dotnet test`, then builds
each installer on its own runner and tries it there, as the test scripts above do:

| Job | Runner | Builds | Tries |
|---|---|---|---|
| `windows` | Windows | `PazScanAgentSetup-<version>.exe` | install, upgrade, uninstall |
| `macos` | macOS (Apple silicon) | the arm64 and x64 `.pkg` | the arm64 one: install, upgrade, uninstall |
| `linux` | Ubuntu x64 and Ubuntu arm64 | each `.deb` and tarball | each `.deb`: install, upgrade, remove, purge |

The installers are kept as run artifacts. To publish a release, first bump `<Version>` in
`Directory.Build.props`, commit and push, and wait for that build to pass. Then tag that commit
using the project version:

```sh
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
git tag "v$version"
git push origin "v$version"   # → GitHub Release with every installer above
```

The tag must match the committed project version or the build fails before creating an installer.
If a release was already published with a mismatched tag, correct the version and publish a new
patch release. A tag with a suffix (`v1.3.0-rc.1`) becomes a pre-release; its `.deb` is versioned
`1.3.0~rc.1`, so apt orders it before `1.3.0`. Re-running a tag's workflow replaces the files on its
existing release. The workflow installs the same pinned Inno Setup 6.7.3 (SHA-256 checked) the local
script uses.

The installers are too large for Cloudflare Pages (25 MiB per file). Host them somewhere else, such
as an R2 bucket or a GitHub release. Then set `VITE_SCAN_AGENT_DOWNLOAD_URL` in the web app so the
Scan panel's **Download Scan Agent** button points there.

The installers are unsigned (see [Install](#install) for the first-run prompts). A Windows
code-signing certificate removes the SmartScreen warning; an Apple Developer ID plus notarization
removes the macOS one. Those are the only parts that would cost money, and they are optional.

## Platform notes

### macOS

- **USB scanners** use SANE from `NAPS2.Sane.Binaries`, which the publish script puts inside
  `Paz Scan Agent.app/Contents/MacOS`. **Network scanners** use NAPS2's eSCL driver. The Apple
  ImageCaptureCore driver is not in the NAPS2 NuGet build, so a scanner that only works through
  Apple's own driver may not appear; check the log for the SANE and eSCL listings.
- The first `GET /v1/devices` after a cold start can take 20–30 seconds while SANE initializes
  all of its backends; later calls take about 4 seconds (the eSCL mDNS search). The agent warms
  this up in the background at startup, but the web app should still show a spinner and allow for
  the first call to be slow.
- macOS may ask for **local network** access the first time the agent searches for network
  scanners. Allow it, or the eSCL listing stays empty.
- The package installs `/Library/LaunchAgents/com.paznoiro.pazscanagent.plist`, so launchd starts
  the agent at every user's login. macOS 13+ says "Background Items Added" and lists it under
  **System Settings → General → Login Items & Extensions**; switching it off there, or
  `PazScanAgent --unregister-startup`, turns it off for that user only. An upgrade stops the agent
  first and starts it again afterwards.
- Every native binary in the app is ad-hoc signed, which is enough for Apple silicon to run it, but
  the app as a whole is not signed with a Developer ID. A copy downloaded as a bare `.app` (not
  through the package) is quarantined: allow it in **System Settings → Privacy & Security**, or run
  `xattr -dr com.apple.quarantine "Paz Scan Agent.app"`.
- To diagnose a USB scanner, look at the log first. The bundled SANE binaries are not on `PATH`,
  so `sane-find-scanner` from Homebrew would use a different SANE installation.

### Linux

- The `.deb` installs the agent into `/opt/paz-scan-agent` (also `/usr/bin/paz-scan-agent`), a menu
  entry, and `/etc/xdg/autostart/paz-scan-agent.desktop`, which starts it at every user's desktop
  login. `paz-scan-agent --unregister-startup` turns that off for one user (a `Hidden=true` entry in
  `~/.config/autostart`); `--register-startup` turns it back on. An upgrade stops every running
  agent and starts it again for the same users; `apt remove` stops them.
- From the tarball, install SANE before starting the agent (the `.deb` depends on it). On
  Debian/Ubuntu:

  ```sh
  sudo apt install sane-utils libsane1
  ```

  Fedora: `sudo dnf install sane-backends`. Arch: `sudo pacman -S sane`.
- USB access usually needs the `scanner` group (the package's udev rules add it):

  ```sh
  sudo usermod -aG scanner "$USER"   # then sign out and back in
  ```
- The self-contained build still uses the system ICU. Most desktop distributions have it; minimal
  containers may need `libicu`.
- To start without a graphical login, use a systemd user unit instead of XDG autostart:

  ```ini
  # ~/.config/systemd/user/paz-scan-agent.service
  [Unit]
  Description=Paz Scan Agent

  [Service]
  # The .deb's agent; for the tarball, wherever you unpacked it, such as %h/paz-scan-agent/PazScanAgent.
  ExecStart=/opt/paz-scan-agent/PazScanAgent
  Restart=on-failure

  [Install]
  WantedBy=default.target
  ```

  ```sh
  systemctl --user daemon-reload
  systemctl --user enable --now paz-scan-agent
  loginctl enable-linger "$USER"   # keep it running when you are not logged in
  ```
- eSCL discovery uses mDNS (UDP 5353). A firewall that blocks multicast on the local network hides
  network scanners; the agent's log will show an empty eSCL list.
- `GET /v1/devices` takes about 4 seconds while the eSCL search runs. If SANE is not installed, the
  log shows a warning on every search and network scanners still work.

### Browsers

The web app calls `http://127.0.0.1:47316` from an HTTPS page. Chrome, Edge and Firefox treat
loopback as a trustworthy origin, so this works; Chrome's Private Network Access preflight is
answered by the agent. **Safari does not**: WebKit blocks HTTP loopback requests from an HTTPS page
as mixed content, so use Chrome/Edge/Firefox on macOS, or run the web app over HTTP on localhost
while testing. Removing that limitation would mean serving the agent over HTTPS with a locally
trusted certificate and changing the web app's agent URL.

## API (v1)

Every call except `GET /` must carry an allowed `Origin` and a loopback `Host`. Otherwise the agent
answers `403 ORIGIN_NOT_ALLOWED` or `421 HOST_NOT_ALLOWED`. Errors are `{ code, message }`.

| Call | |
|---|---|
| `GET /v1/status` | `{ name, version, apiVersion, platform, drivers }` |
| `GET /v1/devices` | `[{ id, name, driver, network }]`. Takes a few seconds while network scanners are searched for. |
| `GET /v1/devices/{id}/caps` | `{ flatbed, feeder, duplex, dpis, color, gray, blackAndWhite, maxWidthMm, maxHeightMm }`. A `null` means the scanner did not say. |
| `POST /v1/scans` | `{ deviceId, source: feeder\|duplex\|flatbed\|auto, color: color\|gray\|bw, dpi, pageSize: a4\|a5\|a3\|letter\|legal, deskew, quality }` → `202` with the status. `409 SCAN_IN_PROGRESS` while another scan runs. |
| `GET /v1/scans/{id}` | `{ id, state: scanning\|done\|failed\|cancelled, pages: [{ index, width, height, dpi, contentType, bytes, side }], error }`. Poll this. |
| `GET /v1/scans/{id}/pages/{n}` | Page *n* at full resolution: JPEG, or PNG for black and white |
| `GET /v1/scans/{id}/pages/{n}/preview` | Page *n*, about 640 px on its long side |
| `POST /v1/scans/{id}/cancel` | Stops the feeder and keeps the pages scanned so far |
| `DELETE /v1/scans/{id}` | Stops the scan and deletes its pages |

Scan error codes: `FEEDER_EMPTY`, `PAPER_JAM`, `COVER_OPEN`, `WARMING_UP`, `DEVICE_BUSY`,
`DEVICE_OFFLINE`, `DEVICE_NOT_FOUND`, `NO_FEEDER`, `NO_DUPLEX`, `DRIVER_NOT_SUPPORTED`,
`COMMUNICATION`, `DEVICE_ERROR`, `SCAN_FAILED`.

### Settings

Settings go in an optional `appsettings.json` beside the executable or in the agent's data folder
(`%LOCALAPPDATA%\Paz Scan Agent` on Windows, `~/Library/Application Support/Paz Scan Agent` on macOS,
`~/.local/share/Paz Scan Agent` on Linux), or in `PAZSCAN_` environment variables. The data folder's
file overrides the one beside the executable, and environment variables override both. On macOS, use
the data folder: an upgrade replaces the whole app bundle, along with anything added to it.

```json
{ "Agent": { "Port": 47316, "AllowedOrigins": ["https://*.zaporion.com", "https://*.zaphrms.com"], "SessionIdleMinutes": 120 } }
```

The same list through environment variables (one entry per index). On Windows, `setx` writes it for
the user, so run it in the same account that runs the agent:

```powershell
setx PAZSCAN_Agent__AllowedOrigins__0 "https://*.zaporion.com"
setx PAZSCAN_Agent__AllowedOrigins__1 "https://*.zaphrms.com"
setx PAZSCAN_Agent__AllowedOrigins__2 "https://*.vanix.com"
setx PAZSCAN_Agent__AllowedOrigins__3 "https://*.vanix.store"
setx PAZSCAN_Agent__AllowedOrigins__4 "http://127.0.0.1:*"
setx PAZSCAN_Agent__AllowedOrigins__5 "http://localhost:*"
```

On macOS/Linux, use the shell that starts the agent (or the LaunchAgent/systemd unit):

```sh
export PAZSCAN_Agent__AllowedOrigins__0="https://*.zaporion.com"
export PAZSCAN_Agent__AllowedOrigins__1="https://*.zaphrms.com"
export PAZSCAN_Agent__AllowedOrigins__2="https://*.vanix.com"
export PAZSCAN_Agent__AllowedOrigins__3="https://*.vanix.store"
export PAZSCAN_Agent__AllowedOrigins__4="http://127.0.0.1:*"
export PAZSCAN_Agent__AllowedOrigins__5="http://localhost:*"
```

`AllowedOrigins` replaces the built-in list rather than adding to it. The built-in list is
`*.zaporion.com`, `*.zaphrms.com`, `*.vanix.com`, `*.vanix.store`, plus `127.0.0.1` and `localhost` over http on any port — bare domains
(`https://zaporion.com` with no subdomain) are not covered. Logs are in the data folder's `logs`
directory (see [Logs](#logs)).

Changing `appsettings.json` or the `PAZSCAN_` variables needs an agent restart: options are
read once at start-up, so stop and start the agent (the tray menu on Windows). Upgrades keep the
settings file in the data folder on every platform, and on Windows and Linux the one beside the
executable too. The Windows uninstaller removes both, with the logs; the macOS and Linux ones leave
the data folder. A setting the agent cannot use (such as a malformed allowed origin) shows in the
tray icon's menu or in the log.
