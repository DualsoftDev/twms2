@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

echo ============================================
echo   Twms2.0 Installer Build Script
echo ============================================
echo.

set "ROOT=%~dp0"
set "SRC=%ROOT%src"
set "SERVER_PROJ=%SRC%\Twms2.Server\Twms2.Server.csproj"
rem  TFM 은 net10.0-windows (Twms2.Server.csproj 참조). publish 출력 경로도 이에 맞춘다.
set "PUBLISH_DIR=%SRC%\Twms2.Server\bin\Release\net10.0-windows\win-x64\publish"
rem  DeepPinger 는 미리 빌드된 네이티브 DLL(src\libs\DeepPinger.dll)을 사용한다.
rem  Twms2.Server.csproj 의 CopyDeepPinger 타깃이 빌드 출력에 복사하지만,
rem  self-contained publish 폴더에는 자동 포함되지 않으므로 여기서 별도로 복사한다.
set "DEEP_PINGER_DLL=%SRC%\libs\DeepPinger.dll"
set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
set "ISS_SCRIPT=%ROOT%installer\twms2.0-setup.iss"

rem ---- 사전 조건 확인 ----
if not exist "!ISCC!" (
    echo [오류] InnoSetup 6이 설치되어 있지 않습니다.
    echo        https://jrsoftware.org/isinfo.php 에서 설치해 주세요.
    echo        경로: !ISCC!
    pause
    exit /b 1
)

if not exist "%DEEP_PINGER_DLL%" (
    echo [오류] DeepPinger.dll 을 찾을 수 없습니다: %DEEP_PINGER_DLL%
    echo        src\libs\DeepPinger.dll 이 저장소에 포함되어 있어야 합니다.
    pause
    exit /b 1
)

rem ---- VC++ 재배포 패키지 확보 (없으면 자동 다운로드) ----
rem  installer\redist\ 는 .gitignore 대상이므로 fresh checkout 시 비어 있을 수 있음.
rem  오프라인 설치 보장을 위해 인스톨러에 반드시 포함되어야 한다.
set "VCREDIST=%ROOT%installer\redist\vc_redist.x64.exe"
set "VCREDIST_URL=https://aka.ms/vs/17/release/vc_redist.x64.exe"
if exist "%VCREDIST%" (
    echo [정보] VC++ 재배포 패키지 확인됨.
    goto :vcredist_done
)
echo [정보] VC++ 재배포 패키지가 없습니다. 다운로드를 시도합니다...
if not exist "%ROOT%installer\redist" mkdir "%ROOT%installer\redist"
powershell -NoProfile -ExecutionPolicy Bypass -Command "try { [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -Uri $env:VCREDIST_URL -OutFile $env:VCREDIST -UseBasicParsing; if ((Get-Item $env:VCREDIST).Length -lt 1000000) { throw 'downloaded file too small' }; exit 0 } catch { exit 1 }"
if not errorlevel 1 (
    echo       다운로드 완료: %VCREDIST%
    goto :vcredist_done
)
echo [오류] VC++ 재배포 패키지 다운로드에 실패했습니다.
echo        오프라인 빌드 환경이라면 다른 PC에서 아래 파일을 받아 수동으로 배치한 뒤 다시 실행하세요:
echo          URL : %VCREDIST_URL%
echo          위치: %VCREDIST%
if defined SKIP_VCREDIST (
    echo [경고] SKIP_VCREDIST 설정됨 - VC++ 없이 빌드를 계속합니다.
    echo        대상 PC에 VC++ 런타임이 없으면 DeepPinger(핑)가 작동하지 않습니다.
    goto :vcredist_done
)
pause
exit /b 1
:vcredist_done
echo.

rem ---- Step 1: 이전 publish 출력 정리 ----
echo [1/4] 이전 publish 출력 정리 중...
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
echo       완료.
echo.

rem ---- Step 2: Twms2.Server Publish (self-contained) ----
echo [2/4] Twms2.Server 퍼블리시 중 (self-contained, win-x64)...
dotnet publish "%SERVER_PROJ%" -c Release -r win-x64 --self-contained -o "%PUBLISH_DIR%"
if errorlevel 1 (
    echo [오류] dotnet publish 실패!
    pause
    exit /b 1
)
echo       완료.
echo.

rem ---- Step 3: DeepPinger.dll 복사 ----
echo [3/4] DeepPinger.dll을 publish 출력에 복사 중...
copy /y "%DEEP_PINGER_DLL%" "%PUBLISH_DIR%\"
if errorlevel 1 (
    echo [오류] DeepPinger.dll 복사 실패!
    pause
    exit /b 1
)
echo       DeepPinger.dll 복사 완료.
echo.

rem ---- Step 4: InnoSetup 컴파일 ----
echo [4/4] InnoSetup 인스톨러 컴파일 중...
"%ISCC%" "%ISS_SCRIPT%" /DPublishDir="%PUBLISH_DIR%"
if errorlevel 1 (
    echo [오류] InnoSetup 컴파일 실패!
    pause
    exit /b 1
)

echo.
echo ============================================
echo   빌드 완료!
echo   인스톨러: %ROOT%installer\output\
echo ============================================
pause
