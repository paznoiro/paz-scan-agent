# Builds the agent and its installer on Windows.
#
#   powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 [-Version 1.0.0] [-NoInstall]
#
# -Version defaults to <Version> in Directory.Build.props.
#
# Needs the .NET 10 SDK and Inno Setup 6. Whichever is missing is installed first, for the current
# user only: no administrator rights, nothing machine-wide, nothing added to PATH.
#
#   .NET 10 SDK    Microsoft's dotnet-install.ps1      -> %LOCALAPPDATA%\Microsoft\dotnet
#   Inno Setup 6   the official 6.7.3 installer        -> %LOCALAPPDATA%\Programs\Inno Setup 6
#                  (from GitHub, checked against its SHA-256 before it runs)
#
# -NoInstall stops with a message instead of installing anything.
#
# Written for Windows PowerShell 5.1, which every Windows 10 and 11 has; PowerShell 7 works too.

param(
    [string]$Version,
    [switch]$NoInstall
)

$ErrorActionPreference = "Stop"
# Windows PowerShell draws Invoke-WebRequest's progress bar so slowly it can triple a download.
$ProgressPreference = "SilentlyContinue"
# Windows PowerShell 5.1 may still default to TLS 1.0; GitHub and Microsoft need 1.2.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

Set-Location (Join-Path $PSScriptRoot "..")

if (-not $Version) {
    $Version = ([xml](Get-Content Directory.Build.props)).SelectSingleNode("//Version").InnerText
}

$DotnetChannel = "10.0"
$DotnetInstallScript = "https://dot.net/v1/dotnet-install.ps1"
$UserDotnetDir = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet"

$InnoVersion = "6.7.3"
$InnoUrl = "https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe"
$InnoSha256 = "9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732"

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Stop-Missing([string]$What, [string]$Where) {
    throw "$What is not installed. Run without -NoInstall to install it for this user, or get it from $Where"
}

# --- .NET 10 SDK ----------------------------------------------------------------------------------

function Test-Dotnet10([string]$Exe) {
    if (-not $Exe -or -not (Test-Path $Exe)) { return $false }
    $sdks = & $Exe --list-sdks 2>$null
    return [bool]($sdks | Where-Object { $_ -match '^10\.' })
}

function Find-Dotnet {
    $candidates = @()
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    $candidates += (Join-Path $UserDotnetDir "dotnet.exe")
    $candidates += (Join-Path $env:ProgramFiles "dotnet\dotnet.exe")
    foreach ($candidate in $candidates) {
        if (Test-Dotnet10 $candidate) { return $candidate }
    }
    return $null
}

function Install-Dotnet {
    Write-Step "Installing the .NET $DotnetChannel SDK for this user into $UserDotnetDir"
    $script = Join-Path $env:TEMP "dotnet-install.ps1"
    Invoke-WebRequest -UseBasicParsing -Uri $DotnetInstallScript -OutFile $script
    # -NoPath: this build finds it by its full path; the user's PATH is left as it was.
    & $script -Channel $DotnetChannel -InstallDir $UserDotnetDir -NoPath
    Remove-Item $script -ErrorAction SilentlyContinue
}

# --- Inno Setup -----------------------------------------------------------------------------------

function Find-Iscc {
    $onPath = Get-Command iscc -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    # Wherever an earlier install went, for all users or just this one, its uninstall entry says.
    $roots = @()
    $uninstallKeys = @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    )
    foreach ($key in $uninstallKeys) {
        if (-not (Test-Path $key)) { continue }
        Get-ChildItem $key -ErrorAction SilentlyContinue |
            Where-Object { $_.PSChildName -like "Inno Setup 6*_is1" -or $_.PSChildName -like "Inno Setup 7*_is1" } |
            ForEach-Object {
                $location = (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).InstallLocation
                if ($location) { $roots += $location }
            }
    }
    foreach ($edition in @("Inno Setup 6", "Inno Setup 7")) {
        $roots += (Join-Path $env:LOCALAPPDATA "Programs\$edition")
        $roots += (Join-Path $env:ProgramFiles $edition)
        if (${env:ProgramFiles(x86)}) { $roots += (Join-Path ${env:ProgramFiles(x86)} $edition) }
    }

    foreach ($root in $roots) {
        $exe = Join-Path $root "ISCC.exe"
        if (Test-Path $exe) { return $exe }
    }
    return $null
}

function Install-InnoSetup {
    Write-Step "Installing Inno Setup $InnoVersion for this user"
    $installer = Join-Path $env:TEMP "innosetup-$InnoVersion.exe"
    Invoke-WebRequest -UseBasicParsing -Uri $InnoUrl -OutFile $installer

    $hash = (Get-FileHash $installer -Algorithm SHA256).Hash
    if ($hash -ne $InnoSha256) {
        Remove-Item $installer -ErrorAction SilentlyContinue
        throw "The Inno Setup download does not match its known SHA-256 (got $hash). Not running it."
    }

    $log = Join-Path $env:TEMP "innosetup-install.log"
    # /CURRENTUSER: Inno's own setup allows choosing the install mode, so no elevation prompt.
    $arguments = @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/CURRENTUSER", "/LOG=`"$log`"")
    $process = Start-Process -FilePath $installer -ArgumentList $arguments -Wait -PassThru
    Remove-Item $installer -ErrorAction SilentlyContinue
    if ($process.ExitCode -ne 0) {
        throw "Inno Setup's installer failed (exit code $($process.ExitCode)). Its log: $log"
    }
}

# --- Build ----------------------------------------------------------------------------------------

Write-Step "Checking the tools"

$dotnet = Find-Dotnet
if (-not $dotnet) {
    if ($NoInstall) { Stop-Missing ".NET $DotnetChannel SDK" "https://dotnet.microsoft.com/download/dotnet/10.0" }
    Install-Dotnet
    $dotnet = Find-Dotnet
    if (-not $dotnet) { throw "The .NET $DotnetChannel SDK still cannot be found after installing it into $UserDotnetDir." }
}
Write-Host ".NET SDK:   $dotnet ($((& $dotnet --version) | Select-Object -First 1))"

$iscc = Find-Iscc
if (-not $iscc) {
    if ($NoInstall) { Stop-Missing "Inno Setup 6" "https://jrsoftware.org/isdl.php" }
    Install-InnoSetup
    $iscc = Find-Iscc
    if (-not $iscc) { throw "Inno Setup still cannot be found after installing it." }
}
Write-Host "Inno Setup: $iscc"

# Tools the SDK starts by name during the build find this copy, not some other one on PATH.
$dotnetDir = Split-Path $dotnet
$env:PATH = "$dotnetDir;$env:PATH"
$env:DOTNET_ROOT = $dotnetDir
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

Write-Step "Publishing the agent ($Version)"
if (Test-Path artifacts\win-x64) { Remove-Item -Recurse -Force artifacts\win-x64 }
& $dotnet publish src\PazScan.Agent.Windows -c Release -o artifacts\win-x64 -p:DebugType=none -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Step "Building the installer"
& $iscc "/DAppVersion=$Version" installer\PazScanAgent.iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

$setup = Resolve-Path "artifacts\PazScanAgentSetup-$Version.exe"
Write-Step "Done"
Write-Host "Installer: $setup"
