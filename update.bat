@echo off
rem ============================================================================
rem  KidControl - update.bat
rem  Downloads the latest version from GitHub, builds it, and performs a
rem  BINARY-ONLY update of an already-installed instance.
rem
rem  Unlike deploy.bat this uses the installer's "/update" mode, which replaces
rem  the executables but DELIBERATELY preserves:
rem     - %ProgramData%\KidControl\appsettings.json  (your bot token / admins)
rem     - %ProgramData%\KidControl\session_state.json (the child's current timer)
rem
rem  Flow:  self-elevate -> ensure .NET 8 SDK -> download latest -> build.ps1 ->
rem         installer /update. Same batch/PowerShell polyglot as deploy.bat.
rem ============================================================================

rem ---------------------------------------------------------------------------
rem  CONFIG - must match the source you deployed from.
rem ---------------------------------------------------------------------------
set "KC_OWNER=chuck-uz"
set "KC_REPO=kid-control"
rem  PRIVATE repo? GitHub token (PAT with 'repo' scope) so the source can download.
rem  Without it a private repo returns HTTP 404. Leave empty if public.
set "KC_GH_TOKEN="
rem  Source: release = latest GitHub release; branch = head of KC_BRANCH.
set "KC_SOURCE_MODE=branch"
set "KC_BRANCH=v2"
rem ---------------------------------------------------------------------------

setlocal EnableExtensions

rem --- Require administrator --------------------------------------------------
net session >nul 2>&1
if %errorlevel% NEQ 0 (
    echo Requesting administrator privileges...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo(
echo === KidControl update: %KC_OWNER%/%KC_REPO% (%KC_SOURCE_MODE%) ===
echo Config and the current timer will be preserved.
echo(

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$raw = Get-Content -Raw -Encoding UTF8 -LiteralPath '%~f0'; $m = '::PS' + '_START::'; $i = $raw.LastIndexOf($m); Invoke-Expression $raw.Substring($i + $m.Length)"

set "RC=%errorlevel%"
echo(
if "%RC%"=="0" (
    echo === Update complete. ===
) else (
    echo === FAILED (exit %RC%). See messages above. ===
)
echo(
pause
exit /b %RC%

::PS_START::
$ErrorActionPreference = 'Stop'
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

function Info($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host $m -ForegroundColor Green }

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

# ---- 2. Download the source ----------------------------------------------
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

# ---- 3. Build (restore + build + test + publish) -------------------------
$build = Join-Path $root 'build.ps1'
if (-not (Test-Path $build)) { throw "build.ps1 not found next to the solution." }

# Fail fast with a clear message if KidControl isn't actually installed yet.
$svc = Get-Service -Name 'KidControlService' -ErrorAction SilentlyContinue
if (-not $svc) {
    throw "KidControlService is not installed. Run deploy.bat for a first-time install; update.bat only updates an existing one."
}

Info 'Building (restore, build, test, publish) - this can take a few minutes'
& $build

$installer = Join-Path $root 'publish\Installer\KidControl.Installer.exe'
if (-not (Test-Path $installer)) { throw "Installer not found at $installer after build." }
Ok "Built installer: $installer"

# ---- 4. Binary-only update (config + timer preserved) --------------------
Info 'Applying update (/update - appsettings.json and session_state.json are kept)'
$sourceDir = Join-Path $root 'publish\Installer'
$argList = @('/update', '--source', ('"' + $sourceDir + '"'))
$p = Start-Process -FilePath $installer -ArgumentList $argList -PassThru -Wait
if ($p.ExitCode -ne 0) { throw "Update exited with code $($p.ExitCode)." }

Ok 'KidControl updated successfully.'
exit 0
