@echo off
rem ============================================================================
rem  KidControl - update.bat
rem  Downloads the latest version from GitHub, builds it, and performs a
rem  BINARY-ONLY update of an already-installed instance.
rem
rem  Uses the installer's "/update" mode, which replaces the executables but
rem  DELIBERATELY preserves:
rem     - %ProgramData%\KidControl\appsettings.json   (bot token / admins)
rem     - %ProgramData%\KidControl\session_state.json (the child's current timer)
rem
rem  Same batch/PowerShell polyglot as deploy.bat, with a debug log at
rem  %TEMP%\kidcontrol-update.log.
rem ============================================================================

rem ---------------------------------------------------------------------------
rem  CONFIG
rem ---------------------------------------------------------------------------
set "KC_OWNER=chuck-uz"
set "KC_REPO=kid-control"

rem  PRIVATE repo? GitHub token (fine-grained Contents:Read, or classic 'repo')
rem  so the source can download. Empty for a public repo.
set "KC_GH_TOKEN="

rem  Source: release = latest published GitHub release; branch = head of KC_BRANCH.
set "KC_SOURCE_MODE=branch"
set "KC_BRANCH=v2"

rem  Optional self-update tweaks applied AFTER the update (leave empty to keep as-is):
rem    KC_CHECK_INTERVAL   = poll period HH:MM:SS. Do NOT use 1 min (GitHub 60/hr limit).
rem    KC_REQUIRE_SIGNATURE= true|false
rem    KC_THUMBPRINT       = SHA-256 thumbprint (for signed self-update)
set "KC_CHECK_INTERVAL=00:15:00"
set "KC_REQUIRE_SIGNATURE=false"
set "KC_THUMBPRINT="
rem ---------------------------------------------------------------------------

setlocal EnableExtensions EnableDelayedExpansion

rem --- Debug log ------------------------------------------------------------
set "KC_LOG=%TEMP%\kidcontrol-update.log"
>>"%KC_LOG%" echo(
call :log "===== update.bat started ====="
call :log "script = %~f0"
call :log "user   = %USERNAME%    log = %KC_LOG%"

rem --- Require administrator -------------------------------------------------
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
echo === KidControl update: %KC_OWNER%/%KC_REPO% (%KC_SOURCE_MODE%) ===
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

rem --- Simple logger: echoes to console and appends to the debug log ---------
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

$owner  = $env:KC_OWNER
$repo   = $env:KC_REPO
$mode   = $env:KC_SOURCE_MODE
$branch = $env:KC_BRANCH
$ua     = @{ 'User-Agent' = 'kidcontrol-update' }
if (-not [string]::IsNullOrWhiteSpace($env:KC_GH_TOKEN)) { $ua['Authorization'] = "Bearer $($env:KC_GH_TOKEN)" }

$work    = Join-Path $env:TEMP 'kidcontrol-update'
$srcRoot = Join-Path $work 'source'
$zipPath = Join-Path $work 'source.zip'

# ---- 1. Ensure the .NET 8 SDK (for building) -----------------------------
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
        Info 'Installing the .NET 8 SDK locally to %USERPROFILE%\.dotnet'
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

# ---- 2. Require an existing installation ----------------------------------
$svc = Get-Service -Name 'KidControlService' -ErrorAction SilentlyContinue
if (-not $svc) {
    throw "KidControlService is not installed. Run deploy.bat for a first-time install; update.bat only updates an existing one."
}

# ---- 3. Download the source ----------------------------------------------
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
        throw "No published release found for $owner/$repo. If there are no releases yet, set KC_SOURCE_MODE=branch."
    }
    Ok ("Latest release: " + $rel.tag_name)
    $zipUrl = $rel.zipball_url
}

Info 'Downloading source archive'
Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -Headers $ua
Info 'Extracting'
Expand-Archive -Path $zipPath -DestinationPath $srcRoot -Force

$sln = Get-ChildItem -Path $srcRoot -Recurse -Filter 'KidControl.sln' | Select-Object -First 1
if (-not $sln) { throw "KidControl.sln not found in the downloaded source." }
$root = $sln.Directory.FullName
Ok "Source root: $root"

# ---- 4. Build (restore + build + test + publish) -------------------------
$build = Join-Path $root 'build.ps1'
if (-not (Test-Path $build)) { throw "build.ps1 not found next to the solution." }
Info 'Building (restore, build, test, publish) - this can take a few minutes'
& $build

$installer = Join-Path $root 'publish\Installer\KidControl.Installer.exe'
if (-not (Test-Path $installer)) { throw "Installer not found at $installer after build." }
Ok "Built installer: $installer"

# ---- 5. Binary-only update (config + timer preserved) --------------------
Info 'Applying update (/update - appsettings.json and session_state.json are kept)'
$sourceDir = Join-Path $root 'publish\Installer'
$argList = @('/update', '--source', ('"' + $sourceDir + '"'))
$p = Start-Process -FilePath $installer -ArgumentList $argList -PassThru -Wait
if ($p.ExitCode -ne 0) { throw "Update exited with code $($p.ExitCode)." }

# ---- 6. Optional: apply self-update tweaks (/update does not touch config) -
$applyThumb    = -not [string]::IsNullOrWhiteSpace($env:KC_THUMBPRINT)
$applyReq      = -not [string]::IsNullOrWhiteSpace($env:KC_REQUIRE_SIGNATURE)
$applyInterval = -not [string]::IsNullOrWhiteSpace($env:KC_CHECK_INTERVAL)
if ($applyThumb -or $applyReq -or $applyInterval) {
    $cfgPath = Join-Path $env:ProgramData 'KidControl\appsettings.json'
    if (Test-Path $cfgPath) {
        $json = Get-Content -Raw -LiteralPath $cfgPath | ConvertFrom-Json
        if (-not ($json.PSObject.Properties.Name -contains 'Update')) {
            $json | Add-Member -NotePropertyName Update -NotePropertyValue ([pscustomobject]@{})
        }
        if ($applyThumb)    { Set-OrAdd $json.Update 'TrustedThumbprint' $env:KC_THUMBPRINT }
        if ($applyReq)      { Set-OrAdd $json.Update 'RequireSignature' ($env:KC_REQUIRE_SIGNATURE -match '^(true|1|yes)$') }
        if ($applyInterval) { Set-OrAdd $json.Update 'CheckInterval' $env:KC_CHECK_INTERVAL }
        ($json | ConvertTo-Json -Depth 16) | Set-Content -LiteralPath $cfgPath -Encoding utf8
        Info 'Applied self-update settings; restarting service'
        Restart-Service -Name 'KidControlService' -Force -ErrorAction SilentlyContinue
    }
}

Ok 'KidControl updated successfully.'
try { Stop-Transcript -ErrorAction SilentlyContinue | Out-Null } catch { }
exit 0
