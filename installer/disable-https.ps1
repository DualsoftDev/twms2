# HTTPS 구성요소를 선택하지 않고 설치(업그레이드)한 경우,
# 이전 설치가 남긴 ProgramData 설정의 HTTPS 엔드포인트를 제거해 HTTP 전용으로 전환한다.
# (Https 섹션이 존재하면 서버가 HTTP→HTTPS 강제 리다이렉트를 켜므로, 섹션 제거가 곧 리다이렉트 해제)
param(
    [string]$ConfigPath = (Join-Path $env:ProgramData "DualSoft\TWMS2\appsettings.json")
)
$ErrorActionPreference = "Stop"

$configPath = $ConfigPath
if (-not (Test-Path $configPath)) { exit 0 }

try {
    $json = Get-Content $configPath -Raw | ConvertFrom-Json
} catch {
    Write-Host "appsettings.json 파싱 실패 — 변경하지 않음: $_"
    exit 0
}

$endpoints = $null
if ($json.PSObject.Properties["Kestrel"] -and $json.Kestrel.PSObject.Properties["Endpoints"]) {
    $endpoints = $json.Kestrel.Endpoints
}
if ($null -eq $endpoints -or -not $endpoints.PSObject.Properties["Https"]) { exit 0 }

$endpoints.PSObject.Properties.Remove("Https")
$json | ConvertTo-Json -Depth 10 | Set-Content $configPath -Encoding UTF8
Write-Host "HTTPS 엔드포인트 제거 완료 — HTTP 전용으로 전환됨: $configPath"
