#Requires -RunAsAdministrator
# ============================================================
# HTTPS 자체 서명 인증서 생성 스크립트
# 관리자 권한으로 실행: Right-click > "Run as Administrator"
# ============================================================

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$pfxPath = Join-Path $scriptDir "certificate.pfx"
$password = "changeit"

# --- 1) 이 PC의 IP 주소 + 호스트명 자동 수집 ---
$hostName = [System.Net.Dns]::GetHostName()
$ipAddresses = Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.IPAddress -ne "127.0.0.1" -and $_.PrefixOrigin -ne "WellKnown" } |
    Select-Object -ExpandProperty IPAddress

$dnsNames = @("localhost", $hostName) + $ipAddresses

Write-Host "=== HTTPS Certificate Generator ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Subject Alternative Names:" -ForegroundColor Yellow
$dnsNames | ForEach-Object { Write-Host "  - $_" }
Write-Host ""

# --- 2) 자체 서명 인증서 생성 (10년 유효) ---
Write-Host "Generating self-signed certificate..." -ForegroundColor Yellow

$cert = New-SelfSignedCertificate `
    -DnsName $dnsNames `
    -CertStoreLocation "Cert:\LocalMachine\My" `
    -NotAfter (Get-Date).AddYears(10) `
    -FriendlyName "TWM Web Server HTTPS" `
    -KeyAlgorithm RSA `
    -KeyLength 2048

Write-Host "Certificate created: $($cert.Thumbprint)" -ForegroundColor Green

# --- 3) .pfx 파일로 내보내기 ---
Write-Host "Exporting to: $pfxPath" -ForegroundColor Yellow

$securePassword = ConvertTo-SecureString -String $password -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePassword | Out-Null

Write-Host "Export complete!" -ForegroundColor Green
Write-Host ""

# --- 4) 안내 출력 ---
Write-Host "=== Next Steps ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "appsettings.json의 Kestrel 섹션을 아래 내용으로 교체하세요:" -ForegroundColor Yellow
Write-Host ""
Write-Host @"
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://0.0.0.0:80"
      },
      "Https": {
        "Url": "https://0.0.0.0:443",
        "Certificate": {
          "Path": "$($pfxPath.Replace('\','\\'))",
          "Password": "$password"
        }
      }
    }
  }
"@ -ForegroundColor White
Write-Host ""
Write-Host "Certificate file: $pfxPath" -ForegroundColor Green
Write-Host "Password: $password" -ForegroundColor Green
Write-Host ""
Write-Host "Note: Browsers will show a security warning for self-signed certificates." -ForegroundColor DarkYellow
Write-Host "      Click 'Advanced' > 'Proceed' to continue." -ForegroundColor DarkYellow
