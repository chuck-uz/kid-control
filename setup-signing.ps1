<#
.SYNOPSIS
    One-time code-signing setup for KidControl (Variant A: self-signed certificate).

.DESCRIPTION
    Run this ONCE on your Windows dev machine. It:
      1. creates a self-signed code-signing certificate,
      2. exports the private PFX (for CI signing) and the public .CER (to trust on targets),
      3. computes the SHA-256 thumbprint EXACTLY as KidControl's verifier expects it
         (SHA-256 over the certificate's raw DER bytes — NOT the SHA-1 .Thumbprint),
      4. sets the two GitHub Actions secrets used by release.yml.

    After this, cut a signed release by pushing tag v2.0.0, and install on targets with
    deploy.bat setting KC_CERT_FILE=<the .cer> and KC_THUMBPRINT=<printed value>.

.NOTES
    Requires: Windows PowerShell 5.1+ and the GitHub CLI (gh) logged in (gh auth login).
#>
[CmdletBinding()]
param(
    [string]$Repo    = 'chuck-uz/kid-control',
    [string]$Subject = 'CN=KidControl Code Signing',
    [string]$OutDir  = (Join-Path $PSScriptRoot 'signing'),
    [int]   $Years   = 5,
    [switch]$SkipSecrets  # create cert/files but don't touch GitHub secrets
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$pfxPath = Join-Path $OutDir 'kidcontrol-codesign.pfx'
$cerPath = Join-Path $OutDir 'kidcontrol-codesign.cer'
$b64Path = Join-Path $OutDir 'kidcontrol-codesign.pfx.b64'

Write-Host '==> Creating self-signed code-signing certificate' -ForegroundColor Cyan
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyExportPolicy Exportable `
    -KeyUsage DigitalSignature `
    -KeyAlgorithm RSA -KeyLength 3072 `
    -NotAfter (Get-Date).AddYears($Years)

# Password for the PFX (used both to export and as the CI secret, so they always match).
$pw = Read-Host -AsSecureString 'Enter a password to protect the PFX'

Write-Host '==> Exporting PFX (private key) and CER (public)' -ForegroundColor Cyan
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $pw | Out-Null
Export-Certificate    -Cert $cert -FilePath $cerPath | Out-Null

# SHA-256 of the raw DER bytes — this is what AuthenticodeVerifier pins on.
$thumb = [BitConverter]::ToString(
    [System.Security.Cryptography.SHA256]::Create().ComputeHash($cert.RawData)
).Replace('-', '')

# Base64 of the PFX for the CI secret.
[Convert]::ToBase64String([IO.File]::ReadAllBytes($pfxPath)) | Set-Content -LiteralPath $b64Path -NoNewline

if (-not $SkipSecrets) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Write-Warning "gh (GitHub CLI) not found — skipping secret upload. Set them manually (see below)."
    } else {
        Write-Host "==> Setting GitHub secrets on $Repo" -ForegroundColor Cyan
        Get-Content -Raw -LiteralPath $b64Path | gh secret set CODE_SIGNING_PFX_BASE64 -R $Repo
        # Reuse the same password so the secret matches the PFX exactly.
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR($pw))
        $plain | gh secret set CODE_SIGNING_PFX_PASSWORD -R $Repo
        $plain = $null
    }
}

Write-Host ''
Write-Host '================ DONE ================' -ForegroundColor Green
Write-Host "PFX  : $pfxPath   (keep private; never commit)"
Write-Host "CER  : $cerPath   (distribute to target machines)"
Write-Host ''
Write-Host 'TrustedThumbprint (use as KC_THUMBPRINT / installer --thumbprint):' -ForegroundColor Yellow
Write-Host "    $thumb" -ForegroundColor Yellow
Write-Host ''
Write-Host 'Next:'
Write-Host '  1) Push tag v2.0.0 to trigger a SIGNED release.'
Write-Host '  2) On each target, run deploy.bat with:'
Write-Host "       set `"KC_CERT_FILE=<path to kidcontrol-codesign.cer>`""
Write-Host "       set `"KC_THUMBPRINT=$thumb`""
Write-Host ''
Write-Host 'The signing/ folder is gitignored — do NOT commit the PFX.'
