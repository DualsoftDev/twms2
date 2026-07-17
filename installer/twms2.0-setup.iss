; ============================================================
; Twms2.0 InnoSetup 설치 스크립트
; ============================================================

#define MyAppName "Twms2.0"
#define MyAppPublisher "LS"
#define MyAppExeName "Twms2.Server.exe"
#define MyServiceName "twms2.0"
#define MyServiceDisplayName "TWMS 2.0 Web Server"

; BAT에서 전달받는 파라미터 (기본값 포함)
#ifndef PublishDir
  #define PublishDir "..\src\Twms2.Server\bin\Release\net10.0-windows\win-x64\publish"
#endif

; 버전은 게시된 exe(ProductVersion = csproj <Version>)에서 읽는다 — 단일 출처: Twms2.Server.csproj
#define MyAppVersion GetStringFileInfo(PublishDir + "\" + MyAppExeName, "ProductVersion")

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=Twms2.0-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UsePreviousAppDir=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Components]
Name: "main"; Description: "Twms2.0 서버 프로그램"; Types: full compact custom; Flags: fixed
Name: "https"; Description: "HTTPS 자체서명 인증서 설정 (포트 443)"; Types: full

[Files]
; appsettings.json: 업그레이드 시 사용자 설정 보존
Source: "{#PublishDir}\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall; Components: main

; 메인 애플리케이션 파일 (appsettings.json 제외)
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "appsettings.json,appsettings.Development.json"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: main

; PowerShell 스크립트 (임시 디렉토리에 복사 후 삭제)
Source: "setup-https.ps1"; DestDir: "{tmp}"; Flags: deleteafterinstall; Components: https

; VC++ Redistributable
Source: "redist\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall skipifsourcedoesntexist; Check: NeedsVCRedist

[Run]
; VC++ Redistributable 설치 (필요시)
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Visual C++ 런타임 설치 중..."; Flags: waituntilterminated; Check: NeedsVCRedist

; HTTPS 인증서 생성 (선택 시)
Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{tmp}\setup-https.ps1"" -InstallDir ""{app}"""; StatusMsg: "HTTPS 인증서 생성 중..."; Flags: waituntilterminated runhidden; Components: https

; 방화벽 규칙 추가
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""TWMS 2.0 HTTP"" dir=in action=allow protocol=TCP localport=80"; Flags: runhidden waituntilterminated
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""TWMS 2.0 HTTPS"" dir=in action=allow protocol=TCP localport=443"; Flags: runhidden waituntilterminated; Components: https

[UninstallRun]
; 서비스 중지 및 삭제
Filename: "sc.exe"; Parameters: "stop {#MyServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "sc.exe"; Parameters: "delete {#MyServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"

; 방화벽 규칙 제거
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""TWMS 2.0 HTTP"""; Flags: runhidden waituntilterminated; RunOnceId: "DelFwHttp"
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""TWMS 2.0 HTTPS"""; Flags: runhidden waituntilterminated; RunOnceId: "DelFwHttps"

[UninstallDelete]
; 런타임 생성 파일 정리 (ProgramData\DualSoft\TWMS2)
Type: files; Name: "{commonappdata}\DualSoft\TWMS2\twm.db.sqlite3"
Type: files; Name: "{commonappdata}\DualSoft\TWMS2\twm.db.sqlite3-shm"
Type: files; Name: "{commonappdata}\DualSoft\TWMS2\twm.db.sqlite3-wal"
Type: files; Name: "{commonappdata}\DualSoft\TWMS2\certificate.pfx"
Type: files; Name: "{commonappdata}\DualSoft\TWMS2\appsettings.json"
Type: filesandordirs; Name: "{commonappdata}\DualSoft\TWMS2\manuals"
Type: filesandordirs; Name: "{commonappdata}\DualSoft\TWMS2\uploads"
Type: dirifempty; Name: "{commonappdata}\DualSoft\TWMS2"
Type: dirifempty; Name: "{commonappdata}\DualSoft"
; 앱 디렉토리 로그
Type: filesandordirs; Name: "{app}\logs"

[Code]

const
  SERVICE_NAME = 'twms2.0';

// 서비스 존재 여부 확인
function ServiceExists(): Boolean;
var
  ResultCode: Integer;
begin
  Exec('sc.exe', 'query ' + SERVICE_NAME, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := (ResultCode = 0);
end;

// 서비스가 STOPPED 상태인지 확인
function ServiceStopped(): Boolean;
var
  ResultCode: Integer;
begin
  Exec('cmd.exe', '/c sc.exe query ' + SERVICE_NAME + ' | findstr /C:"STOPPED"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := (ResultCode = 0);
end;

// 기존 서비스 중지 (업그레이드 시) — STOPPED 확인까지 대기 + 잔여 프로세스 강제 종료
procedure StopExistingService();
var
  ResultCode: Integer;
  i: Integer;
begin
  if ServiceExists() then
  begin
    Log('기존 서비스 중지 중...');
    Exec('sc.exe', 'stop ' + SERVICE_NAME, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    // 최대 30초 동안 STOPPED 상태 대기 (Akka/Kestrel 정리에 3초 이상 걸릴 수 있음)
    for i := 1 to 30 do
    begin
      if ServiceStopped() then Break;
      Sleep(1000);
    end;
    if not ServiceStopped() then
      Log('경고: 서비스가 30초 내에 중지되지 않음 — 프로세스 강제 종료 시도');
  end;

  // 수동 실행 인스턴스 포함, DLL을 물고 있는 잔여 프로세스 강제 종료
  Exec('taskkill.exe', '/F /T /IM ' + '{#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
end;

// 서비스 생성 및 설정
procedure CreateAndConfigureService();
var
  ResultCode: Integer;
  ExePath: String;
begin
  ExePath := ExpandConstant('{app}\Twms2.Server.exe');

  // 기존 서비스 삭제 (업그레이드 시 재등록)
  if ServiceExists() then
  begin
    Exec('sc.exe', 'delete ' + SERVICE_NAME, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1000);
  end;

  // 서비스 생성: 지연된 자동 시작
  Exec('sc.exe',
    'create ' + SERVICE_NAME +
    ' binPath= "' + ExePath + '"' +
    ' start= delayed-auto' +
    ' DisplayName= "TWMS 2.0 Web Server"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if ResultCode <> 0 then
  begin
    MsgBox('서비스 등록에 실패했습니다. (오류 코드: ' + IntToStr(ResultCode) + ')', mbError, MB_OK);
    Exit;
  end;

  // 서비스 설명 설정
  Exec('sc.exe',
    'description ' + SERVICE_NAME + ' "TWMS 2.0 종합설비관리 웹 서버"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // 실패 시 재시작 설정: 10초, 30초, 60초 후 재시작 / 24시간 후 카운터 리셋
  Exec('sc.exe',
    'failure ' + SERVICE_NAME +
    ' reset= 86400' +
    ' actions= restart/10000/restart/30000/restart/60000',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // 서비스 시작
  Exec('sc.exe', 'start ' + SERVICE_NAME, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if ResultCode <> 0 then
    MsgBox('서비스를 시작하지 못했습니다. 설치 완료 후 services.msc에서 수동으로 시작해 주세요.', mbInformation, MB_OK);
end;

// VC++ Redistributable 필요 여부 확인
function NeedsVCRedist(): Boolean;
begin
  Result := not RegKeyExists(HKLM, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64');
end;

// 설치 전: 기존 서비스 중지
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopExistingService();
  Result := '';
end;

// ProgramData\DualSoft\TWMS2 디렉토리 생성
procedure EnsureDataDirectories();
begin
  ForceDirectories(ExpandConstant('{commonappdata}\DualSoft\TWMS2\manuals'));
  ForceDirectories(ExpandConstant('{commonappdata}\DualSoft\TWMS2\uploads'));
end;

// 설치 후: ProgramData 디렉토리 생성 + 서비스 등록 및 시작
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    EnsureDataDirectories();
    CreateAndConfigureService();
  end;
end;

// 제거 시: 서비스 중지 및 삭제
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopExistingService();
    Exec('sc.exe', 'delete ' + SERVICE_NAME, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
