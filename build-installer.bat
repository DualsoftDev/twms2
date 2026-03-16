@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

echo ============================================
echo   Twms2.0 Installer Build Script
echo ============================================
echo.

set "ROOT=%~dp0"
set "SRC=%ROOT%src"
set "SERVER_PROJ=%SRC%\DexaWeb.Server\DexaWeb.Server.csproj"
set "PUBLISH_DIR=%SRC%\DexaWeb.Server\bin\Release\net10.0\win-x64\publish"
set "DEEP_PINGER_DLL=%SRC%\DexaWeb.Server\bin\Release\net10.0\DeepPinger.dll"
set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
set "ISS_SCRIPT=%ROOT%installer\twms2.0-setup.iss"

rem ---- MSBuild 경로 자동 탐색 (vswhere) ----
set "VSWHERE=C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
set "MSBUILD="
"!VSWHERE!" -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" -products * > "%TEMP%\msbuild_path.txt" 2>nul
set /p MSBUILD=<"%TEMP%\msbuild_path.txt"
del "%TEMP%\msbuild_path.txt" 2>nul
if not defined MSBUILD (
    echo [오류] MSBuild를 찾을 수 없습니다. Visual Studio 또는 Build Tools를 설치해 주세요.
    pause
    exit /b 1
)
echo MSBuild: !MSBUILD!

rem ---- 사전 조건 확인 ----
if not exist "!ISCC!" (
    echo [오류] InnoSetup 6이 설치되어 있지 않습니다.
    echo        https://jrsoftware.org/isinfo.php 에서 설치해 주세요.
    echo        경로: !ISCC!
    pause
    exit /b 1
)

if not exist "%ROOT%installer\redist\vc_redist.x64.exe" (
    echo [경고] installer\redist\vc_redist.x64.exe 가 없습니다.
    echo        VC++ Redistributable 없이 빌드를 계속합니다.
    echo        대상 PC에 VC++ 런타임이 없으면 DeepPinger가 작동하지 않을 수 있습니다.
    echo.
)

rem ---- Step 1: 이전 publish 출력 정리 ----
echo [1/5] 이전 publish 출력 정리 중...
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
echo       완료.
echo.

rem ---- Step 2: DeepPinger (Release|x64) 빌드 ----
echo [2/5] DeepPinger 빌드 중 (Release x64, Static MFC)...
"!MSBUILD!" "%SRC%\DeepPinger\DeepPinger.vcxproj" /p:Configuration=Release /p:Platform=x64 /p:SolutionDir="%SRC%\\" /t:Build /v:minimal
if errorlevel 1 (
    echo [오류] DeepPinger 빌드 실패!
    pause
    exit /b 1
)
echo       완료.
echo.

rem ---- Step 3: DexaWeb.Server Publish (self-contained) ----
echo [3/5] DexaWeb.Server 퍼블리시 중 (self-contained, win-x64)...
dotnet publish "%SERVER_PROJ%" -c Release -r win-x64 --self-contained -o "%PUBLISH_DIR%" /p:SkipDeepPinger=true
if errorlevel 1 (
    echo [오류] dotnet publish 실패!
    pause
    exit /b 1
)
echo       완료.
echo.

rem ---- Step 4: DeepPinger.dll 복사 ----
echo [4/5] DeepPinger.dll을 publish 출력에 복사 중...
if exist "%DEEP_PINGER_DLL%" (
    copy /y "%DEEP_PINGER_DLL%" "%PUBLISH_DIR%\"
    echo       DeepPinger.dll 복사 완료.
) else (
    echo [경고] DeepPinger.dll을 찾을 수 없습니다: %DEEP_PINGER_DLL%
    echo        인스톨러에 DeepPinger.dll이 포함되지 않습니다.
)
echo.

rem ---- Step 5: InnoSetup 컴파일 ----
echo [5/5] InnoSetup 인스톨러 컴파일 중...
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
