param(
    [Parameter(Mandatory=$true)]
    [string]$InstallDir
)

$ErrorActionPreference = "Stop"

$dataDir = Join-Path $env:ProgramData "DualSoft\TWMS2"
$pfxPath = Join-Path $dataDir "certificate.pfx"
$localConfigPath = Join-Path $dataDir "appsettings.json"

# ProgramData 디렉토리 보장
if (-not (Test-Path $dataDir)) {
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
}

# ---- 1. 기존 인증서 존재 확인 (업그레이드 시 스킵) ----
if (Test-Path $pfxPath) {
    Write-Host "기존 인증서가 존재합니다: $pfxPath"
    Write-Host "기존 인증서를 유지합니다. 새 인증서가 필요하면 certificate.pfx를 삭제 후 재설치하세요."
    exit 0
}

# ---- 2. 443 포트 사용 중 확인 ----
$portInUse = Get-NetTCPConnection -LocalPort 443 -ErrorAction SilentlyContinue
if ($portInUse) {
    $process = Get-Process -Id $portInUse[0].OwningProcess -ErrorAction SilentlyContinue
    $processName = if ($process) { $process.ProcessName } else { "알 수 없음" }
    Write-Warning "포트 443이 이미 사용 중입니다 (프로세스: $processName)."
    Write-Warning "HTTPS 인증서는 생성되지만, 서비스 시작 시 포트 충돌이 발생할 수 있습니다."
    Write-Warning "필요 시 appsettings.json에서 Https 포트를 변경하세요."
}

# ---- 3. 자체서명 인증서 생성 ----
$certPassword = -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 24 | ForEach-Object { [char]$_ })

Write-Host "HTTPS 자체서명 인증서 생성 중..."

$cert = New-SelfSignedCertificate `
    -DnsName "localhost", $env:COMPUTERNAME, "twms.local" `
    -CertStoreLocation "Cert:\LocalMachine\My" `
    -NotAfter (Get-Date).AddYears(10) `
    -FriendlyName "TWMS 2.0 Self-Signed"

# ---- 4. PFX로 내보내기 ----
$securePassword = ConvertTo-SecureString -String $certPassword -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePassword | Out-Null

# ---- 5. 신뢰할 수 있는 루트 인증서에 추가 (브라우저 경고 방지) ----
$rootStore = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root", "LocalMachine")
$rootStore.Open("ReadWrite")
$rootStore.Add($cert)
$rootStore.Close()

# ---- 6. appsettings.json (ProgramData)에 HTTPS 엔드포인트 추가 ----
if (Test-Path $localConfigPath) {
    $json = Get-Content $localConfigPath -Raw | ConvertFrom-Json
} else {
    $json = [PSCustomObject]@{}
}

if (-not $json.Kestrel) {
    $json | Add-Member -NotePropertyName "Kestrel" -NotePropertyValue ([PSCustomObject]@{})
}
if (-not $json.Kestrel.Endpoints) {
    $json.Kestrel | Add-Member -NotePropertyName "Endpoints" -NotePropertyValue ([PSCustomObject]@{})
}

$json.Kestrel.Endpoints | Add-Member -NotePropertyName "Http" -NotePropertyValue ([PSCustomObject]@{
    Url = "http://0.0.0.0:80"
}) -Force

$json.Kestrel.Endpoints | Add-Member -NotePropertyName "Https" -NotePropertyValue ([PSCustomObject]@{
    Url = "https://0.0.0.0:443"
    Certificate = [PSCustomObject]@{
        Path = $pfxPath
        Password = $certPassword
    }
}) -Force

$json | ConvertTo-Json -Depth 10 | Set-Content $localConfigPath -Encoding UTF8

Write-Host "HTTPS 인증서 생성 완료: $pfxPath"
Write-Host "appsettings.json에 HTTPS 엔드포인트 설정 추가 완료"
