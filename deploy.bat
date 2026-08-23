@echo off
rem ============================================================================
rem  KidControl - deploy.bat
rem  Downloads the latest release source from GitHub, builds it, and installs it.
rem
rem  Flow:  self-elevate -> ensure .NET 8 SDK -> download latest release ->
rem         build.ps1 (restore/build/test/publish) -> run the installer.
rem
rem  This is a single self-contained file: the batch header below configures and
rem  elevates, then hands off to the PowerShell section after the marker line
rem  (batch never executes those lines - it exits first).
rem ============================================================================

rem ---------------------------------------------------------------------------
rem  CONFIG - edit these, then just double-click the file.
rem ---------------------------------------------------------------------------
rem  GitHub repository that holds the KidControl v2 source (with KidControl.sln
rem  and build.ps1). Change REPO to wherever you pushed v2.
set "KC_OWNER=chuck-uz"
set "KC_REPO=kid-control"

rem  PRIVATE repo? Put a GitHub token here (classic PAT with 'repo' scope, or a
rem  fine-grained token with read access to this repo) so the source can be
rem  downloaded. Without it a private repo returns HTTP 404. Leave empty if public.
set "KC_GH_TOKEN="

rem  Where to pull source from:
rem    release = latest published GitHub release (default)
rem    branch  = head of the branch named in KC_BRANCH (use if there are no releases yet)
set "KC_SOURCE_MODE=branch"
set "KC_BRANCH=v2"

rem  Install mode:
rem    - Leave KC_BOT_TOKEN empty  -> the graphical installer wizard opens
rem      (you type the token / admin id / night window there).
rem    - Fill KC_BOT_TOKEN + KC_ADMIN_IDS -> fully silent, unattended install.
set "KC_BOT_TOKEN="
set "KC_ADMIN_IDS="
set "KC_NIGHT_START=22:00:00"
set "KC_NIGHT_END=07:00:00"
rem  --- Code signing (Variant A: self-signed cert) -------------------------
rem  For automatic self-update to accept your signed releases, this target must
rem  (1) trust your signing certificate and (2) know its SHA-256 thumbprint.
rem
rem  KC_CERT_FILE : path to the PUBLIC certificate (kidcontrol-codesign.cer produced
rem                 by setup-signing.ps1). If set, deploy.bat imports it into the
rem                 machine's Trusted Root + Trusted Publishers so the update
rem                 signature chain builds. Leave empty to skip.
rem  KC_THUMBPRINT: SHA-256 thumbprint printed by setup-signing.ps1. Written into
rem                 appsettings (Update.TrustedThumbprint) so self-update pins it.
set "KC_CERT_FILE="
set "KC_THUMBPRINT="

rem  Self-update signature policy written into the installed config:
rem    empty or true = require a valid signature (secure; needs signed releases + KC_THUMBPRINT)
rem    false         = accept UNSIGNED releases (quick auto-update on a public repo; less strict)
set "KC_REQUIRE_SIGNATURE="
rem ---------------------------------------------------------------------------

setlocal EnableExtensions EnableDelayedExpansion

rem --- Debug log ------------------------------------------------------------
set "KC_LOG=%TEMP%\kidcontrol-deploy.log"
>>"%KC_LOG%" echo(
call :log "===== deploy.bat started ====="
call :log "script = %~f0"
call :log "cwd    = %CD%"
call :log "user   = %USERNAME%    log = %KC_LOG%"

rem --- Require administrator (needed to register the Windows service) --------
net session >nul 2>&1
if !errorlevel! NEQ 0 (
    call :log "not elevated -> requesting UAC elevation"
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    if !errorlevel! NEQ 0 call :log "ERROR: failed to start elevated process, errorlevel !errorlevel!"
    echo(
    echo Requested administrator rights. If no elevated window appeared, UAC was declined.
    echo Debug log: %KC_LOG%
    echo(
    pause
    exit /b
)
call :log "running elevated: OK"

echo(
echo === KidControl deploy: %KC_OWNER%/%KC_REPO% (%KC_SOURCE_MODE%) ===
echo Debug log: %KC_LOG%
echo(

rem --- Verify PowerShell is available ---------------------------------------
where powershell >nul 2>&1
if !errorlevel! NEQ 0 (
    call :log "ERROR: powershell.exe not found in PATH"
    echo powershell.exe not found in PATH. Cannot continue.
    echo(
    pause
    exit /b 9
)

rem --- Hand off to the embedded PowerShell payload --------------------------
call :log "launching PowerShell payload"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$raw = Get-Content -Raw -Encoding UTF8 -LiteralPath '%~f0'; $m = '::PS' + '_START::'; $i = $raw.LastIndexOf($m); Invoke-Expression $raw.Substring($i + $m.Length)"

set "RC=!errorlevel!"
call :log "PowerShell payload exit code = !RC!"
echo(
if "!RC!"=="0" (
    echo === Done. ===
) else (
    echo === FAILED ^(exit !RC!^). See messages above and %KC_LOG% ===
)
echo(
pause
exit /b !RC!

rem --- Simple logger: echoes to console and appends to the debug log ---------
:log
echo [%TIME%] %~1
>>"%KC_LOG%" echo [%DATE% %TIME%] %~1
goto :eof

::PS_START::
$ErrorActionPreference = 'Stop'
try { if ($env:KC_LOG) { Start-Transcript -Path $env:KC_LOG -Append -ErrorAction SilentlyContinue | Out-Null } } catch { }
# Any terminating error lands here: log it clearly and return a non-zero exit code.
trap {
    Write-Host "FATAL: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.InvocationInfo) { Write-Host ("  at " + $_.InvocationInfo.PositionMessage) -ForegroundColor DarkYellow }
    try { Stop-Transcript -ErrorAction SilentlyContinue | Out-Null } catch { }
    exit 1
}
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

function Info($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host $m -ForegroundColor Green }

# Sets a property on a PSCustomObject, adding it if absent.
function Set-OrAdd($obj, $name, $value) {
    if ($obj.PSObject.Properties.Name -contains $name) { $obj.$name = $value }
    else { $obj | Add-Member -NotePropertyName $name -NotePropertyValue $value }
}

$owner   = $env:KC_OWNER
$repo    = $env:KC_REPO
$mode    = $env:KC_SOURCE_MODE
$branch  = $env:KC_BRANCH
$ua      = @{ 'User-Agent' = 'kidcontrol-deploy' }
if (-not [string]::IsNullOrWhiteSpace($env:KC_GH_TOKEN)) { $ua['Authorization'] = "Bearer $($env:KC_GH_TOKEN)" }

$work    = Join-Path $env:TEMP 'kidcontrol-deploy'
$srcRoot = Join-Path $work 'source'
$zipPath = Join-Path $work 'source.zip'

# ---- 1. Ensure the .NET 8 SDK is available -------------------------------
Info 'Checking for the .NET SDK'
$haveSdk = $false
try {
    $v = (& dotnet --list-sdks) 2>$null
    if ($LASTEXITCODE -eq 0 -and ($v | Select-String '^8\.' -Quiet)) { $haveSdk = $true }
} catch { }

if (-not $haveSdk) {
    $localDotnet = Join-Path $env:USERPROFILE '.dotnet'
    $localExe    = Join-Path $localDotnet 'dotnet.exe'
    if (-not (Test-Path $localExe)) {
        Info 'No .NET 8 SDK found - installing it locally to %USERPROFILE%\.dotnet (no admin/global change)'
        New-Item -ItemType Directory -Force -Path $work | Out-Null
        $installPs1 = Join-Path $work 'dotnet-install.ps1'
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installPs1 -Headers $ua
        & $installPs1 -Channel '8.0' -InstallDir $localDotnet -Architecture x64
    }
    $env:DOTNET_ROOT = $localDotnet
    $env:PATH = "$localDotnet;$env:PATH"
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Ok ("SDK: " + ((& dotnet --version) 2>$null))

# ---- 1b. Ensure a machine-wide .NET 8 Desktop Runtime --------------------
# The published apps are framework-dependent, so the LocalSystem service and the
# WPF UI need the .NET 8 Desktop Runtime installed system-wide (a user-local SDK
# only covers building on this account, not the service that runs as SYSTEM).
Info 'Checking for a machine-wide .NET 8 Desktop Runtime'
$sysDotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$haveDesktop = $false
if (Test-Path $sysDotnet) {
    $rt = & $sysDotnet --list-runtimes 2>$null
    if ($rt | Select-String 'Microsoft\.WindowsDesktop\.App 8\.' -Quiet) { $haveDesktop = $true }
}
if ($haveDesktop) {
    Ok 'Desktop Runtime 8 present.'
} else {
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        Info 'Installing Microsoft.DotNet.DesktopRuntime.8 via winget'
        & winget install --id Microsoft.DotNet.DesktopRuntime.8 -e --silent --source winget `
            --accept-source-agreements --accept-package-agreements
    } else {
        Write-Host 'WARNING: no machine-wide .NET 8 Desktop Runtime and winget is unavailable.' -ForegroundColor Yellow
        Write-Host '         Install it (x64) from https://dotnet.microsoft.com/download/dotnet/8.0' -ForegroundColor Yellow
        Write-Host '         or the KidControl service will not start after install.' -ForegroundColor Yellow
    }
}

# ---- 1c. Trust the signing certificate (Variant A: self-signed) ----------
# Self-update verifies the downloaded installer's Authenticode chain, so the
# signer certificate must be trusted on THIS machine. Import the public .cer into
# LocalMachine\Root (chain root) and TrustedPublisher (no publisher prompt).
if (-not [string]::IsNullOrWhiteSpace($env:KC_CERT_FILE)) {
    if (-not (Test-Path $env:KC_CERT_FILE)) {
        throw "KC_CERT_FILE '$($env:KC_CERT_FILE)' not found."
    }
    Info "Trusting signing certificate: $($env:KC_CERT_FILE)"
    Import-Certificate -FilePath $env:KC_CERT_FILE -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    Import-Certificate -FilePath $env:KC_CERT_FILE -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
    Ok 'Signing certificate trusted (Root + TrustedPublisher).'
}

# ---- 2. Resolve and download the source ----------------------------------
if (Test-Path $srcRoot) { Remove-Item -Recurse -Force $srcRoot }
New-Item -ItemType Directory -Force -Path $srcRoot | Out-Null

if ($mode -eq 'branch') {
    $zipUrl = "https://api.github.com/repos/$owner/$repo/zipball/$branch"
    Info "Downloading branch '$branch' of $owner/$repo"
} else {
    Info "Querying latest release of $owner/$repo"
    try {
        $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$repo/releases/latest" -Headers $ua
    } catch {
        throw "No published release found for $owner/$repo. If v2 isn't released yet, set KC_SOURCE_MODE=branch."
    }
    Ok ("Latest release: " + $rel.tag_name)
    $zipUrl = $rel.zipball_url
}

Info 'Downloading source archive'
Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -Headers $ua

Info 'Extracting'
Expand-Archive -Path $zipPath -DestinationPath $srcRoot -Force

# GitHub zipballs extract into a single "<owner>-<repo>-<sha>" folder; find the sln.
$sln = Get-ChildItem -Path $srcRoot -Recurse -Filter 'KidControl.sln' | Select-Object -First 1
if (-not $sln) { throw "KidControl.sln not found in the downloaded source." }
$root = $sln.Directory.FullName
Ok "Source root: $root"

# ---- 3. Build (restore + build + test + publish) -------------------------
$build = Join-Path $root 'build.ps1'
if (-not (Test-Path $build)) { throw "build.ps1 not found next to the solution." }
Info 'Building (restore, build, test, publish) - this can take a few minutes'
& $build

$installer = Join-Path $root 'publish\Installer\KidControl.Installer.exe'
if (-not (Test-Path $installer)) { throw "Installer not found at $installer after build." }
Ok "Built installer: $installer"

# ---- 4. Install ----------------------------------------------------------
$token = $env:KC_BOT_TOKEN
if ([string]::IsNullOrWhiteSpace($token)) {
    Info 'Launching the graphical installer wizard'
    $p = Start-Process -FilePath $installer -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "Installer exited with code $($p.ExitCode)." }
} else {
    if ([string]::IsNullOrWhiteSpace($env:KC_ADMIN_IDS)) {
        throw "KC_BOT_TOKEN is set but KC_ADMIN_IDS is empty - silent install needs both."
    }
    Info 'Running silent install'
    $sourceDir = Join-Path $root 'publish\Installer'
    $argList = @(
        '/silent',
        '--token', $token,
        '--admin-ids', $env:KC_ADMIN_IDS,
        '--night-start', $env:KC_NIGHT_START,
        '--night-end', $env:KC_NIGHT_END,
        '--source', ('"' + $sourceDir + '"')
    )
    if (-not [string]::IsNullOrWhiteSpace($env:KC_THUMBPRINT)) {
        $argList += @('--thumbprint', $env:KC_THUMBPRINT)
    }
    $p = Start-Process -FilePath $installer -ArgumentList $argList -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "Silent install exited with code $($p.ExitCode)." }
}

# ---- 5. Apply self-update settings into appsettings (mode-independent) -----
# The GUI wizard cannot set these, so write them straight into the deployed config
# and restart the service. Writes only what you provided (thumbprint / signature
# policy / private-repo token).
$applyThumb = -not [string]::IsNullOrWhiteSpace($env:KC_THUMBPRINT)
$applyReq   = -not [string]::IsNullOrWhiteSpace($env:KC_REQUIRE_SIGNATURE)
$applyTok   = -not [string]::IsNullOrWhiteSpace($env:KC_GH_TOKEN)
if ($applyThumb -or $applyReq -or $applyTok) {
    $cfgPath = Join-Path $env:ProgramData 'KidControl\appsettings.json'
    if (Test-Path $cfgPath) {
        $json = Get-Content -Raw -LiteralPath $cfgPath | ConvertFrom-Json
        if (-not ($json.PSObject.Properties.Name -contains 'Update')) {
            $json | Add-Member -NotePropertyName Update -NotePropertyValue ([pscustomobject]@{})
        }
        if ($applyThumb) { Set-OrAdd $json.Update 'TrustedThumbprint' $env:KC_THUMBPRINT }
        if ($applyReq) {
            Set-OrAdd $json.Update 'RequireSignature' ($env:KC_REQUIRE_SIGNATURE -match '^(true|1|yes)$')
        } elseif ($applyThumb) {
            Set-OrAdd $json.Update 'RequireSignature' $true
        }
        # Store a token only for a PRIVATE repo; on a public repo leave KC_GH_TOKEN empty.
        if ($applyTok) { Set-OrAdd $json.Update 'GitHubToken' $env:KC_GH_TOKEN }

        ($json | ConvertTo-Json -Depth 16) | Set-Content -LiteralPath $cfgPath -Encoding utf8
        Info 'Applied self-update settings to appsettings.json; restarting service'
        Restart-Service -Name 'KidControlService' -Force -ErrorAction SilentlyContinue
        Ok 'Self-update settings configured.'
    } else {
        Write-Host "WARNING: $cfgPath not found; could not apply self-update settings." -ForegroundColor Yellow
    }
}

Ok 'KidControl installed successfully.'
try { Stop-Transcript -ErrorAction SilentlyContinue | Out-Null } catch { }
exit 0
