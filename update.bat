@echo off
rem ============================================================================
rem  KidControl - update.bat  (updates to a PREBUILT RELEASE, keeps config)
rem
rem  Downloads the release setup zip built by CI (correct version baked in) and
rem  applies a BINARY-ONLY update via the installer's /update mode, which preserves:
rem     - %ProgramData%\KidControl\appsettings.json   (bot token / admins)
rem     - %ProgramData%\KidControl\session_state.json (the child's current timer)
rem
rem  No .NET SDK, no build, no git, no api.github.com (uses the direct CDN URL, so
rem  it is never blocked by GitHub's 60/hr API rate limit). Mirrors deploy.bat.
rem  Debug log: %TEMP%\kidcontrol-update.log
rem ============================================================================

rem ---------------------------------------------------------------------------
rem  CONFIG
rem ---------------------------------------------------------------------------
set "KC_OWNER=chuck-uz"
set "KC_REPO=kid-control"

rem  Which release to install. Pinned tag = exact version (recommended). Empty = latest.
set "KC_TAG=v2.1.0"

rem  PRIVATE repo? GitHub token (fine-grained Contents:Read, or classic 'repo'). Empty if public.
set "KC_GH_TOKEN="

rem  Self-update settings applied after the update (empty = leave as-is):
rem    KC_REQUIRE_SIGNATURE = false to accept unsigned releases (public-repo auto-update)
rem    KC_CHECK_INTERVAL    = poll period HH:MM:SS (not 1 min -> GitHub 60/hr limit)
rem    KC_THUMBPRINT        = SHA-256 thumbprint for signed self-update (optional)
set "KC_REQUIRE_SIGNATURE=false"
set "KC_CHECK_INTERVAL=00:15:00"
set "KC_THUMBPRINT="
rem ---------------------------------------------------------------------------

setlocal EnableExtensions EnableDelayedExpansion

set "KC_LOG=%TEMP%\kidcontrol-update.log"
>>"%KC_LOG%" echo(
call :log "===== update.bat (release install) started ====="
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
echo === KidControl update (release): %KC_OWNER%/%KC_REPO% ===
echo Config and the current timer are preserved.  Debug log: %KC_LOG%
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
    echo === Update complete. ===
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
$ua    = @{ 'User-Agent' = 'kidcontrol-update' }
if (-not [string]::IsNullOrWhiteSpace($token)) { $ua['Authorization'] = "Bearer $token" }

$work    = Join-Path $env:TEMP 'kidcontrol-update'
$extract = Join-Path $work 'release'

# ---- 1. Require an existing installation ----------------------------------
$svc = Get-Service -Name 'KidControlService' -ErrorAction SilentlyContinue
if (-not $svc) {
    throw "KidControlService is not installed. Run deploy.bat for a first-time install; update.bat only updates an existing one."
}

# ---- 2. Download the setup zip -------------------------------------------
# Public repo: direct CDN URL (no api.github.com -> no rate limit). Private: authenticated API.
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
    $dh = @{ 'User-Agent' = 'kidcontrol-update'; 'Authorization' = "Bearer $token"; 'Accept' = 'application/octet-stream' }
    Info "Downloading setup: $($asset.name)"
    Invoke-WebRequest -Uri $asset.url -Headers $dh -OutFile $zip
}

Info 'Extracting'
Expand-Archive -Path $zip -DestinationPath $extract -Force

$installer = Get-ChildItem -Path $extract -Recurse -Filter 'KidControl.Installer.exe' | Select-Object -First 1
if (-not $installer) { throw "KidControl.Installer.exe not found in the setup zip." }
$sourceDir = Split-Path $installer.FullName -Parent
Ok "Installer: $($installer.FullName)"

# ---- 3. Binary-only update (config + timer preserved) --------------------
Info 'Applying update (/update - appsettings.json and session_state.json are kept)'
$argList = @('/update', '--source', ('"' + $sourceDir + '"'))
$p = Start-Process -FilePath $installer.FullName -ArgumentList $argList -PassThru -Wait
if ($p.ExitCode -ne 0) { throw "Update exited with code $($p.ExitCode)." }

# ---- 4. Apply self-update settings (/update does not touch config) --------
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

Ok 'KidControl updated successfully.'
try { Stop-Transcript -ErrorAction SilentlyContinue | Out-Null } catch { }
exit 0
