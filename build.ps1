$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishDir = Join-Path $root "publish"

if (Test-Path $publishDir) {
    Remove-Item -Path $publishDir -Recurse -Force
}

New-Item -Path $publishDir -ItemType Directory -Force | Out-Null

dotnet publish "$root/src/KidControl.ServiceHost/KidControl.ServiceHost.csproj" -c Release -r win-x64 --self-contained false -o "$publishDir"
dotnet publish "$root/src/KidControl.UiHost/KidControl.UiHost.csproj" -c Release -r win-x64 --self-contained false -o "$publishDir"
dotnet publish "$root/src/KidControl.Unlocker/KidControl.Unlocker.csproj" -c Release -r win-x64 --self-contained false -o "$publishDir"

Write-Host "Publish completed: $publishDir"
