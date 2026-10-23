# Paz Scan Agent

A small Windows program that lets the Paz web app use the scanner on the user's desk. A
browser cannot reach a scanner itself; this agent does it through
[NAPS2.Sdk](https://www.naps2.com/sdk/) and answers Paz on `http://127.0.0.1:47316`.

```
Paz (browser) ──fetch──► 127.0.0.1:47316 ──► NAPS2.Sdk ──► WIA / TWAIN (USB) · eSCL (network)
```

- **USB scanners:** WIA, plus TWAIN through NAPS2's 32-bit worker (`NAPS2.Worker.exe`)
- **Network scanners:** eSCL / AirScan, found over mDNS, no driver needed
- **Runs as:** a tray icon started at sign-in. It is not a Windows service, because scanner drivers
  expect the signed-in user's desktop.
- **Licences:** NAPS2.Sdk is LGPL 2.1 (used unmodified, as a separate library), and its dependencies are MIT
  or Apache 2.0. Nothing here is paid.

## Layout

| Path | What |
|---|---|
| `src/PazScan.Core` | The HTTP API, origin checks, scan sessions and the NAPS2 backend. Cross-platform. |
| `src/PazScan.Agent.Windows` | The tray app (`PazScanAgent.exe`). Builds on macOS too. |
| `src/PazScan.Agent.Console` | The same agent with **demo scanners**, for working on the web app without a scanner or without Windows. |
| `tests/PazScan.Core.Tests` | API and unit tests (in-memory server, demo backend). |
| `installer/PazScanAgent.iss` | Inno Setup script: a per-user install that needs no admin rights. |

## Develop

```sh
dotnet test                                                   # all tests
dotnet run --project src/PazScan.Agent.Console                # demo agent on :47316
dotnet run --project src/PazScan.Agent.Console -- --pages ~/x # plus "Demo folder": scans the images in ~/x
```

There are three demo scanners:
- **Demo scanner** feeds six generated pages. The fourth is blank. Scanning both sides, most backs come
  out blank.
- **Demo scanner (jams)** jams on the third sheet.
- **Demo folder** feeds the PNG and JPEG files in the `--pages` folder. Put in a photo of a printed
  separator sheet to try splitting.

## Logs

The agent writes to `%LOCALAPPDATA%\Paz Scan Agent\logs\agent-YYYYMMDD.log`. A log rotates to
`agent-YYYYMMDD-1.log`, `-2` and so on at 5 MB; files older than 7 days are removed, and at most
20 are kept. The tray menu has **View live logs**, which tails the current file, and **Open logs
folder**.

Every HTTP request, scanner listing, capability query, scan and scanned page (path, size, type,
dimensions, DPI and side) is logged. If Windows shows a printer but the agent lists no scanner,
install the manufacturer's scanner driver or software: a print-only queue has no WIA/TWAIN scanner
behind it, and eSCL scanners must advertise themselves over mDNS.

## Release

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

## CI

Merges to `master` run `dotnet test` plus the installer build above, then `test-installer.ps1` on
the Windows runner; the setup is kept as a run artifact. First bump `<Version>` in
`Directory.Build.props`, commit and push, and wait for that build to pass. Then tag that commit
using the project version to publish it:

```sh
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
git tag "v$version"
git push origin "v$version"   # → GitHub Release + PazScanAgentSetup-<Version>.exe
```

The tag must match the committed project version or the build fails before creating an installer.
If a release was already published with a mismatched tag, correct the version and publish a new
patch release. A tag with a suffix (`v1.3.0-rc.1`) becomes a pre-release. Re-running a tag's
workflow replaces the setup on its existing release. The workflow installs the same pinned Inno
Setup 6.7.3 (SHA-256 checked) the local script uses.

The setup is too large for Cloudflare Pages (25 MiB per file). Host it somewhere else, such as an R2
bucket or a GitHub release. Then set `VITE_SCAN_AGENT_DOWNLOAD_URL` in the web app so the Scan
panel's **Download Scan Agent** button points there.

The installer is unsigned. The first time someone runs it, Windows SmartScreen says "Windows
protected your PC"; they click **More info → Run anyway**. Signing it with a code-signing
certificate removes that warning. That is the only part that would cost money, and it is optional.

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

Settings go in an optional `appsettings.json` beside the exe, or in `PAZSCAN_` environment
variables:

```json
{ "Agent": { "Port": 47316, "AllowedOrigins": ["https://*.zaporion.com", "https://*.zaphrms.com"], "SessionIdleMinutes": 120 } }
```

The same list through environment variables (one entry per index; `setx` writes it for the
user, so run it in the same account that runs the agent):

```powershell
setx PAZSCAN_Agent__AllowedOrigins__0 "https://*.zaporion.com"
setx PAZSCAN_Agent__AllowedOrigins__1 "https://*.zaphrms.com"
setx PAZSCAN_Agent__AllowedOrigins__2 "https://*.vanix.com"
setx PAZSCAN_Agent__AllowedOrigins__3 "https://*.vanix.store"
setx PAZSCAN_Agent__AllowedOrigins__4 "http://127.0.0.1:*"
```

`AllowedOrigins` replaces the built-in list rather than adding to it. The built-in list is
`*.zaporion.com`, `*.zaphrms.com`, `*.vanix.com`, `*.vanix.store`, and `127.0.0.1` on any port — bare domains
(`https://zaporion.com` with no subdomain) are not covered. Logs are in
`%LOCALAPPDATA%\Paz Scan Agent\logs` (tray menu → **Open
logs**).

Changing `appsettings.json` or the `PAZSCAN_` variables needs an agent restart: options are
read once at start-up, so exit the tray icon and start the agent again. An upgrade keeps
`appsettings.json`; uninstalling removes it. A setting the agent cannot use (such as a malformed
allowed origin) shows in the tray icon's menu and in the log.
