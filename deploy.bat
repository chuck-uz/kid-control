@echo off
rem ============================================================================
rem  KidControl - deploy.bat  (installs a PREBUILT RELEASE)
rem
rem  Downloads the release setup zip built by CI (correct version baked in),
rem  extracts it, and runs the installer. No .NET SDK, no build, no git -> the
rem  version shown in the bot is the real release version (e.g. 2.0.4), not a
rem  from-source "0.0.1-source".
rem
rem  Flow: self-elevate -> ensure .NET 8 Desktop Runtime -> download release zip ->
rem        extract -> run installer (GUI wizard, or silent if a token is set).
rem  Debug log: %TEMP%\kidcontrol-deploy.log
rem ============================================================================

rem ---------------------------------------------------------------------------
rem  CONFIG
rem ---------------------------------------------------------------------------
set "KC_OWNER=chuck-uz"
set "KC_REPO=kid-control"

rem  Which release to install. A pinned tag installs exactly that version (recommended,
rem  and avoids the rate-limited GitHub API entirely). Empty = latest (needs the
rem  fixed-name KidControl-Setup.zip asset on the release).
set "KC_TAG=v2.0.7"

rem  PRIVATE repo? GitHub token (fine-grained Contents:Read, or classic 'repo').
rem  Empty for a public repo.
set "KC_GH_TOKEN="

rem  Install mode:
rem    - Leave KC_BOT_TOKEN empty  -> the graphical installer wizard opens.
rem    - Fill KC_BOT_TOKEN + KC_ADMIN_IDS -> fully silent, unattended install.
set "KC_BOT_TOKEN="
set "KC_ADMIN_IDS="
set "KC_NIGHT_START=22:00:00"
set "KC_NIGHT_END=07:00:00"

rem  Self-update settings written after install:
rem    KC_REQUIRE_SIGNATURE = false to accept unsigned releases (public-repo auto-update)
rem    KC_CHECK_INTERVAL    = poll period HH:MM:SS (not 1 min -> GitHub 60/hr limit)
rem    KC_THUMBPRINT        = SHA-256 thumbprint for signed self-update (optional)
rem    KC_CERT_FILE         = public .cer to trust (only for signed self-update)
set "KC_REQUIRE_SIGNATURE=false"
set "KC_CHECK_INTERVAL=00:15:00"
set "KC_THUMBPRINT="
set "KC_CERT_FILE="
rem ---------------------------------------------------------------------------

setlocal EnableExtensions EnableDelayedExpansion

set "KC_LOG=%TEMP%\kidcontrol-deploy.log"
>>"%KC_LOG%" echo(
call :log "===== deploy.bat (release install) started ====="
call :log "script = %~f0    user = %USERNAME%    log = %KC_LOG%"

net session >nul 2>&1
if !errorlevel! NEQ 0 (
    call :log "not elevated -> requesting UAC elevation"
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    echo(
    echo Requested administrator rights. If no elevated window appeared, UAC was declined.
    echo Debug log: %KC_LOG%
    echo(
    pause
    exit /b
)
call :log "running elevated: OK"

echo(
echo === KidControl deploy (release): %KC_OWNER%/%KC_REPO% ===
echo Debug log: %KC_LOG%
echo(

where powershell >nul 2>&1
if !errorlevel! NEQ 0 (
    call :log "ERROR: powershell.exe not found in PATH"
    echo powershell.exe not found in PATH. Cannot continue.
    echo(
    pause
    exit /b 9
)

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

:log
echo [%TIME%] %~1
>>"%KC_LOG%" echo [%DATE% %TIME%] %~1
goto :eof

::PS_START::
$ErrorActionPreference = 'Stop'
try { if ($env:KC_LOG) { Start-Transcript -Path $env:KC_LOG -Append -ErrorAction SilentlyContinue | Out-Null } } catch { }
trap {
    Write-Host "FATAL: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.InvocationInfo) { Write-Host ("  at " + $_.InvocationInfo.PositionMessage) -ForegroundColor DarkYellow }
    try { Stop-Transcript -ErrorAction SilentlyContinue | Out-Null } catch { }
    exit 1
}
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

function Info($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host $m -ForegroundColor Green }
function Set-OrAdd($obj, $name, $value) {
    if ($obj.PSObject.Properties.Name -contains $name) { $obj.$name = $value }
    else { $obj | Add-Member -NotePropertyName $name -NotePropertyValue $value }
}

$owner = $env:KC_OWNER
$repo  = $env:KC_REPO
$tag   = $env:KC_TAG
$token = $env:KC_GH_TOKEN
$ua    = @{ 'User-Agent' = 'kidcontrol-deploy' }
if (-not [string]::IsNullOrWhiteSpace($token)) { $ua['Authorization'] = "Bearer $token" }

$work    = Join-Path $env:TEMP 'kidcontrol-deploy'
$extract = Join-Path $work 'release'

# ---- 1. Ensure a machine-wide .NET 8 Desktop Runtime ---------------------
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
        Write-Host 'WARNING: no .NET 8 Desktop Runtime and winget is unavailable.' -ForegroundColor Yellow
        Write-Host '         Install it (x64) from https://dotnet.microsoft.com/download/dotnet/8.0' -ForegroundColor Yellow
        Write-Host '         or the service/UI will not start.' -ForegroundColor Yellow
    }
}

# ---- 1b. Trust the signing certificate (only for signed self-update) ------
if (-not [string]::IsNullOrWhiteSpace($env:KC_CERT_FILE)) {
    if (-not (Test-Path $env:KC_CERT_FILE)) { throw "KC_CERT_FILE '$($env:KC_CERT_FILE)' not found." }
    Info "Trusting signing certificate: $($env:KC_CERT_FILE)"
    Import-Certificate -FilePath $env:KC_CERT_FILE -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    Import-Certificate -FilePath $env:KC_CERT_FILE -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
}

# ---- 2. Download the setup zip -------------------------------------------
# Public repo: use the DIRECT release-download URL (github.com/.../releases/download/...).
# It goes through the CDN, NOT api.github.com, so it is never hit by the 60/hr API
# rate limit that was returning HTTP 403. Private repo: authenticated Releases API.
if (Test-Path $extract) { Remove-Item -Recurse -Force $extract }
New-Item -ItemType Directory -Force -Path $extract | Out-Null
$zip = Join-Path $work 'setup.zip'

if ([string]::IsNullOrWhiteSpace($token)) {
    if ([string]::IsNullOrWhiteSpace($tag)) {
        $zipUrl = "https://github.com/$owner/$repo/releases/latest/download/KidControl-Setup.zip"
        Info "Downloading latest setup: $zipUrl"
    } else {
        $zipUrl = "https://github.com/$owner/$repo/releases/download/$tag/KidControl-Setup-$tag.zip"
        Info "Downloading setup $tag : $zipUrl"
    }
    Invoke-WebRequest -Uri $zipUrl -Headers $ua -OutFile $zip
} else {
    $relApi = if ([string]::IsNullOrWhiteSpace($tag)) {
        "https://api.github.com/repos/$owner/$repo/releases/latest"
    } else {
        "https://api.github.com/repos/$owner/$repo/releases/tags/$tag"
    }
    Info "Querying private release: $relApi"
    $rel = Invoke-RestMethod -Uri $relApi -Headers $ua
    $asset = $rel.assets | Where-Object { $_.name -like '*.zip' } | Select-Object -First 1
    if (-not $asset) { throw "Release $($rel.tag_name) has no .zip setup asset." }
    $dh = @{ 'User-Agent' = 'kidcontrol-deploy'; 'Authorization' = "Bearer $token"; 'Accept' = 'application/octet-stream' }
    Info "Downloading setup: $($asset.name)"
    Invoke-WebRequest -Uri $asset.url -Headers $dh -OutFile $zip
}

Info 'Extracting'
Expand-Archive -Path $zip -DestinationPath $extract -Force

$installer = Get-ChildItem -Path $extract -Recurse -Filter 'KidControl.Installer.exe' | Select-Object -First 1
if (-not $installer) { throw "KidControl.Installer.exe not found in the setup zip." }
$installerPath = $installer.FullName
Ok "Installer: $installerPath"

# ---- 4. Install ----------------------------------------------------------
$botToken = $env:KC_BOT_TOKEN
if ([string]::IsNullOrWhiteSpace($botToken)) {
    Info 'Launching the graphical installer wizard'
    $p = Start-Process -FilePath $installerPath -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "Installer exited with code $($p.ExitCode)." }
} else {
    if ([string]::IsNullOrWhiteSpace($env:KC_ADMIN_IDS)) {
        throw "KC_BOT_TOKEN is set but KC_ADMIN_IDS is empty - silent install needs both."
    }
    Info 'Running silent install'
    $argList = @(
        '/silent',
        '--token', $botToken,
        '--admin-ids', $env:KC_ADMIN_IDS,
        '--night-start', $env:KC_NIGHT_START,
        '--night-end', $env:KC_NIGHT_END,
        '--source', ('"' + (Split-Path $installerPath -Parent) + '"')
    )
    if (-not [string]::IsNullOrWhiteSpace($env:KC_THUMBPRINT)) { $argList += @('--thumbprint', $env:KC_THUMBPRINT) }
    $p = Start-Process -FilePath $installerPath -ArgumentList $argList -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "Silent install exited with code $($p.ExitCode)." }
}

# ---- 5. Apply self-update settings into appsettings (mode-independent) ----
$applyThumb    = -not [string]::IsNullOrWhiteSpace($env:KC_THUMBPRINT)
$applyReq      = -not [string]::IsNullOrWhiteSpace($env:KC_REQUIRE_SIGNATURE)
$applyTok      = -not [string]::IsNullOrWhiteSpace($token)
$applyInterval = -not [string]::IsNullOrWhiteSpace($env:KC_CHECK_INTERVAL)
if ($applyThumb -or $applyReq -or $applyTok -or $applyInterval) {
    $cfgPath = Join-Path $env:ProgramData 'KidControl\appsettings.json'
    if (Test-Path $cfgPath) {
        $json = Get-Content -Raw -LiteralPath $cfgPath | ConvertFrom-Json
        if (-not ($json.PSObject.Properties.Name -contains 'Update')) {
            $json | Add-Member -NotePropertyName Update -NotePropertyValue ([pscustomobject]@{})
        }
        if ($applyThumb)    { Set-OrAdd $json.Update 'TrustedThumbprint' $env:KC_THUMBPRINT }
        if ($applyReq)      { Set-OrAdd $json.Update 'RequireSignature' ($env:KC_REQUIRE_SIGNATURE -match '^(true|1|yes)$') }
        if ($applyTok)      { Set-OrAdd $json.Update 'GitHubToken' $token }
        if ($applyInterval) { Set-OrAdd $json.Update 'CheckInterval' $env:KC_CHECK_INTERVAL }
        ($json | ConvertTo-Json -Depth 16) | Set-Content -LiteralPath $cfgPath -Encoding utf8
        Info 'Applied self-update settings; restarting service'
        Restart-Service -Name 'KidControlService' -Force -ErrorAction SilentlyContinue
    }
}

Ok ("KidControl " + $rel.tag_name + " installed successfully.")
try { Stop-Transcript -ErrorAction SilentlyContinue | Out-Null } catch { }
exit 0
