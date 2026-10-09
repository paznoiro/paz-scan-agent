# Tries a built setup the way someone's PC meets it: installs it for the current user, checks the
# agent it started answers, upgrades over the running agent, then uninstalls and checks nothing is
# left. CI runs it on every build. It works the same on a Windows desk, but it replaces (and then
# removes) any Paz Scan Agent already installed there.
#
#   powershell -ExecutionPolicy Bypass -File scripts\test-installer.ps1 -Setup artifacts\PazScanAgentSetup-1.0.0.exe -Version 1.0.0
#
# Written for Windows PowerShell 5.1; PowerShell 7 works too.

param(
    [Parameter(Mandatory = $true)][string]$Setup,
    [Parameter(Mandatory = $true)][string]$Version
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$Setup = (Resolve-Path $Setup).Path
$AppDir = Join-Path $env:LOCALAPPDATA "Programs\Paz Scan Agent"
$DataDir = Join-Path $env:LOCALAPPDATA "Paz Scan Agent"
$Exe = Join-Path $AppDir "PazScanAgent.exe"
$Settings = Join-Path $AppDir "appsettings.json"
$Shortcut = Join-Path ([Environment]::GetFolderPath("Programs")) "Paz Scan Agent.lnk"
$RunKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$Origin = "http://127.0.0.1:3000"
$DefaultPort = 47316
$OtherPort = 47399

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAILED: $Message" }
    Write-Host "ok: $Message"
}

function Wait-Until([scriptblock]$Condition, [string]$Message) {
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (& $Condition)) {
        if ((Get-Date) -gt $deadline) { throw "FAILED (after 60 s): $Message" }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "ok: $Message"
}

# Not Start-Process -Wait: that also waits for every process the setup starts, and it leaves the
# agent running.
function Invoke-Silently([string]$Path, [string[]]$Arguments) {
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -PassThru
    $null = $process.Handle # keeps ExitCode readable once the process has gone
    $process.WaitForExit()
    return $process.ExitCode
}

function Install-Setup {
    $log = Join-Path $env:TEMP "pazscan-setup.log"
    $exitCode = Invoke-Silently $Setup @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/LOG=`"$log`"")
    if ($exitCode -ne 0) {
        Get-Content $log -ErrorAction SilentlyContinue | Write-Host
        throw "FAILED: Setup exited with code $exitCode."
    }
}

# Status and body, or $null when nothing is listening. PowerShell throws on a 4xx; both editions
# carry the response on the exception.
function Invoke-Agent([int]$Port, [string]$Path, [string]$FromOrigin) {
    $headers = @{}
    if ($FromOrigin) { $headers.Origin = $FromOrigin }
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$Port$Path" -Headers $headers -TimeoutSec 10
        return [pscustomobject]@{ Status = [int]$response.StatusCode; Content = [string]$response.Content }
    } catch {
        if ($_.Exception.Response) { return [pscustomobject]@{ Status = [int]$_.Exception.Response.StatusCode; Content = "" } }
        return $null
    }
}

function Wait-Agent([int]$Port) {
    $script:status = $null
    Wait-Until {
        $response = Invoke-Agent $Port "/v1/status" $Origin
        if ($response -and $response.Status -eq 200) { $script:status = $response.Content | ConvertFrom-Json }
        $null -ne $script:status
    } "the agent answers on port $Port"
    return $script:status
}

try {
    Write-Step "Installing $Setup"
    Install-Setup
    Assert (Test-Path $Exe) "the agent is in $AppDir"
    Assert (Test-Path $Shortcut) "the Start menu has a shortcut"
    Assert ((Get-ItemProperty $RunKey).PazScanAgent -eq "`"$Exe`"") "it starts at sign-in"

    Write-Step "Talking to the agent Setup started"
    $status = Wait-Agent $DefaultPort
    Assert ($status.name -eq "Paz Scan Agent") "it says it is Paz Scan Agent"
    Assert ($status.version -eq $Version) "it is version $Version (it says $($status.version))"
    Assert ((@($status.drivers) -join ",") -eq "wia,twain,escl") "it offers WIA, TWAIN and network scanners"
    Assert ((Invoke-Agent $DefaultPort "/v1/devices" "https://evil.example").Status -eq 403) "it refuses other web pages"

    Write-Step "Upgrading over the running agent"
    Set-Content -Path $Settings -Value "{ `"Agent`": { `"Port`": $OtherPort } }"
    Set-Content -Path (Join-Path $AppDir "left-over.dll") -Value ""
    Install-Setup
    Assert (Test-Path $Settings) "appsettings.json survives the upgrade"
    Assert (-not (Test-Path (Join-Path $AppDir "left-over.dll"))) "nothing from the old version lingers"
    $status = Wait-Agent $OtherPort
    Assert ($status.version -eq $Version) "the upgraded agent restarted with the kept settings"
    Assert ($null -eq (Invoke-Agent $DefaultPort "/" $null)) "the old agent was stopped"

    Write-Step "Uninstalling"
    $uninstaller = Get-ChildItem $AppDir -Filter "unins*.exe" | Select-Object -First 1
    Assert ($null -ne $uninstaller) "there is an uninstaller"
    $exitCode = Invoke-Silently $uninstaller.FullName @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART")
    Assert ($exitCode -eq 0) "the uninstaller succeeds"
    # It hands over to a copy of itself in %TEMP% and may return before that copy has finished.
    Wait-Until { -not (Test-Path $AppDir) } "the program folder is gone"
    Wait-Until { -not (Test-Path $DataDir) } "its logs and scans are gone"
    Assert ($null -eq (Invoke-Agent $OtherPort "/" $null)) "the agent has stopped"
    Assert ($null -eq (Get-ItemProperty $RunKey).PazScanAgent) "it no longer starts at sign-in"
    Assert (-not (Test-Path $Shortcut)) "the Start menu shortcut is gone"
} catch {
    foreach ($log in Get-ChildItem (Join-Path $DataDir "logs") -Filter "*.log" -ErrorAction SilentlyContinue) {
        Write-Host "--- $($log.FullName)"
        Get-Content $log.FullName | Write-Host
    }
    throw
}

Write-Step "The installer works"
