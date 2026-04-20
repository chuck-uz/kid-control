$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishDir = Join-Path $root "publish"
$installDir = "C:\Program Files\KidControl"
$serviceName = "KidControlv0.4"
$serviceExe = Join-Path $installDir "KidControl.ServiceHost.exe"

if (-not (Test-Path $publishDir)) {
    throw "Publish folder not found: $publishDir. Run build.ps1 first."
}

Get-Process -Name "KidControl.UiHost" -ErrorAction SilentlyContinue | Stop-Process -Force

$existing = sc.exe query $serviceName 2>$null
if ($LASTEXITCODE -eq 0) {
    sc.exe stop $serviceName | Out-Null
    Start-Sleep -Seconds 2
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 1
}

if (-not (Test-Path $installDir)) {
    New-Item -Path $installDir -ItemType Directory -Force | Out-Null
}

Copy-Item -Path (Join-Path $publishDir "*") -Destination $installDir -Recurse -Force

sc.exe create $serviceName binPath= "`"$serviceExe`"" start= auto obj= LocalSystem | Out-Null
sc.exe failure $serviceName reset= 0 actions= restart/5000 | Out-Null
sc.exe start $serviceName | Out-Null

Write-Host "Service installed and started: $serviceName"
