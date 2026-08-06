<#
.SYNOPSIS
    Clean, restore, build, test and publish KidControl.

.DESCRIPTION
    Linear and fail-hard: any non-zero exit code stops the script immediately.
    Publishes the three shipping executables (ServiceHost, UiHost, Installer) as
    framework-dependent, single-file win-x64 binaries into ./publish.

    Unlike the v1 build script this does NOT hunt for and kill "locking" processes
    while building — a build machine should never have the product running on it,
    and that subsystem masked real errors. If a publish fails because a file is
    locked, that is a signal to fix the environment, not to paper over it.
#>

$ErrorActionPreference = "Stop"

$root       = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution   = Join-Path $root "KidControl.sln"
$publishDir = Join-Path $root "publish"

function Invoke-Checked {
    param([Parameter(Mandatory = $true)][scriptblock]$Command)
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed (exit $LASTEXITCODE): $Command"
    }
}

function Publish-Project {
    param(
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$OutDir
    )
    Invoke-Checked { dotnet publish $Project -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true --no-build -o $OutDir }
}

Write-Host "==> Cleaning" -ForegroundColor Cyan
if (Test-Path $publishDir) {
    Remove-Item -Path $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir | Out-Null

Write-Host "==> Restoring" -ForegroundColor Cyan
Invoke-Checked { dotnet restore $solution }

Write-Host "==> Building (Release)" -ForegroundColor Cyan
Invoke-Checked { dotnet build $solution -c Release --no-restore }

Write-Host "==> Testing (Release)" -ForegroundColor Cyan
Invoke-Checked { dotnet test $solution -c Release --no-build }

Write-Host "==> Publishing" -ForegroundColor Cyan
Publish-Project "$root/src/KidControl.ServiceHost/KidControl.ServiceHost.csproj" "$publishDir/ServiceHost"
Publish-Project "$root/src/KidControl.UiHost/KidControl.UiHost.csproj"           "$publishDir/UiHost"
Publish-Project "$root/src/KidControl.Installer/KidControl.Installer.csproj"      "$publishDir/Installer"

# Stage the payload binaries next to the installer so an offline install (installer
# + payloads in one folder) works without any download step.
Copy-Item "$publishDir/ServiceHost/KidControl.ServiceHost.exe" "$publishDir/Installer/" -Force
Copy-Item "$publishDir/UiHost/KidControl.UiHost.exe"           "$publishDir/Installer/" -Force

Write-Host ""
Write-Host "Build complete. Artifacts in: $publishDir" -ForegroundColor Green
Write-Host "  Installer (with payloads): $publishDir/Installer/KidControl.Installer.exe"
Write-Host "  Service:                   $publishDir/ServiceHost/KidControl.ServiceHost.exe"
Write-Host "  UI:                        $publishDir/UiHost/KidControl.UiHost.exe"
