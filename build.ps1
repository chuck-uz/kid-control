$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishDir = Join-Path $root "publish"
# Publish installer to a unique run folder so GenerateBundle never collides with a running old exe.
$installerPublishRoot = Join-Path $root "Build/InstallerPublish"
$runStamp = Get-Date -Format "yyyyMMdd-HHmmss"
$installerPublishDir = Join-Path $installerPublishRoot ("run-" + $runStamp)
$installerMirrorDir = Join-Path $root "Build/Installer"
$installerStableDir = Join-Path $root "Build/InstallerPublish-Latest"

function Invoke-DotnetPublish {
    param(
        [string]$ProjectPath,
        [string]$OutputPath
    )

    dotnet publish $ProjectPath -c Release -r win-x64 --self-contained false -o $OutputPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $ProjectPath"
    }
}

function Stop-LockingProcessForPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TargetPath
    )

    if (-not (Test-Path $TargetPath)) {
        return
    }

    $full = [System.IO.Path]::GetFullPath($TargetPath)
    Write-Host "Checking lock owners for: $full"

    try {
        Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ExecutablePath -and ($_.ExecutablePath -ieq $full) } |
            ForEach-Object {
                Write-Host "Stopping PID $($_.ProcessId): $($_.Name)"
                Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
            }
    }
    catch {
        Write-Host "Warning: CIM process lookup failed: $($_.Exception.Message)"
    }

    try {
        $name = [System.IO.Path]::GetFileNameWithoutExtension($full)
        if (-not [string]::IsNullOrWhiteSpace($name)) {
            Get-Process -Name $name -ErrorAction SilentlyContinue |
                ForEach-Object {
                    try {
                        if ($_.Path -and ([System.IO.Path]::GetFullPath($_.Path) -ieq $full)) {
                            Write-Host "Stopping process by name/path PID $($_.Id): $($_.ProcessName)"
                            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
                        }
                    }
                    catch {
                        # Some system processes deny path lookup; ignore.
                    }
                }
        }
    }
    catch {
        Write-Host "Warning: process-name fallback failed: $($_.Exception.Message)"
    }
}

function Remove-FileWithRetry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [int]$Attempts = 12,
        [int]$DelayMs = 700
    )

    for ($i = 1; $i -le $Attempts; $i++) {
        if (-not (Test-Path $Path)) {
            return $true
        }

        Stop-LockingProcessForPath -TargetPath $Path
        Start-Sleep -Milliseconds 250

        try {
            Remove-Item -Path $Path -Force -ErrorAction Stop
            return $true
        }
        catch {
            if ($i -eq $Attempts) {
                break
            }

            Write-Host "Target still locked ($i/$Attempts): $Path"
            Start-Sleep -Milliseconds $DelayMs
        }
    }

    return -not (Test-Path $Path)
}

if (Test-Path $publishDir) {
    Remove-Item -Path $publishDir -Recurse -Force
}

New-Item -Path $installerPublishRoot -ItemType Directory -Force | Out-Null
New-Item -Path $publishDir -ItemType Directory -Force | Out-Null
New-Item -Path $installerPublishDir -ItemType Directory -Force | Out-Null

$serviceOut = Join-Path $publishDir "ServiceHost"
$uiOut = Join-Path $publishDir "UiHost"

Invoke-DotnetPublish "$root/src/KidControl.ServiceHost/KidControl.ServiceHost.csproj" "$serviceOut"
Invoke-DotnetPublish "$root/src/KidControl.UiHost/KidControl.UiHost.csproj" "$uiOut"

# Build installer (no embedded payloads - payloads are published as separate release assets)
Invoke-DotnetPublish "$root/src/KidControl.Installer/KidControl.Installer.csproj" "$installerPublishDir"

# Keep payload executables available for GitHub Release upload while the
# user-facing latest folder contains only the installer.
#   KidControl.Installer.exe
#   KidControl.ServiceHost.exe
#   KidControl.UiHost.exe
Copy-Item "$serviceOut/KidControl.ServiceHost.exe" "$installerPublishDir/KidControl.ServiceHost.exe" -Force
Copy-Item "$uiOut/KidControl.UiHost.exe" "$installerPublishDir/KidControl.UiHost.exe" -Force

try {
    if (Test-Path $installerStableDir) {
        try {
            Remove-Item -Path $installerStableDir -Recurse -Force -ErrorAction Stop
        }
        catch {
            Write-Host "Warning: could not clean $installerStableDir (possibly locked)."
        }
    }
    New-Item -Path $installerStableDir -ItemType Directory -Force | Out-Null
    Copy-Item (Join-Path $installerPublishDir "KidControl.Installer.exe")    (Join-Path $installerStableDir "KidControl.Installer.exe")    -Force
    Write-Host "Installer copied to stable folder: $installerStableDir"

    New-Item -Path $installerMirrorDir -ItemType Directory -Force | Out-Null
    Copy-Item (Join-Path $installerPublishDir "KidControl.Installer.exe")    (Join-Path $installerMirrorDir "KidControl.Installer.exe")    -Force
    Write-Host "Installer mirrored to: $installerMirrorDir"
}
catch {
    Write-Host "Note: could not copy to one of stable/mirror folders (likely locked). Use run output directly: $installerPublishDir\KidControl.Installer.exe"
}

Write-Host "Payload publish completed: $publishDir"
Write-Host "Installer publish completed: $installerPublishDir"
Write-Host ""
Write-Host "User-facing installer:"
Write-Host "  $installerStableDir\KidControl.Installer.exe"
Write-Host ""
Write-Host "GitHub Release payload assets:"
Write-Host "  $serviceOut\KidControl.ServiceHost.exe"
Write-Host "  $uiOut\KidControl.UiHost.exe"
