<div align="center">

# TWMS 2.0

**Total Workshop Management System**

종합설비관리 시스템

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Blazor](https://img.shields.io/badge/Blazor-Server-512BD4)](https://dotnet.microsoft.com/apps/aspnet/web-apps/blazor)
[![Akka.NET](https://img.shields.io/badge/Akka.NET-1.4.24-0D4EA6)](https://getakka.net/)
[![MudBlazor](https://img.shields.io/badge/MudBlazor-8.15.0-7e6fff)](https://mudblazor.com/)
[![SQLite](https://img.shields.io/badge/SQLite-Dapper-003B57)](https://www.sqlite.org/)

---

*제조 라인의 설비 자산을 통합 관리·모니터링하는 웹 애플리케이션*
*DEXA 서버와 Akka.NET 원격 통신 및 DEXA SQLite DB 직접 조회로 연동하며,*
*Blazor Server 기반의 실시간 대시보드와 레이아웃 시각화 기능을 제공합니다.*

</div>

---

## 주요 기능

| 기능 | 설명 |
|------|------|
| **대시보드** | KPI 카드, 히트맵, Sankey 차트 등 GridStack 기반 위젯 레이아웃 |
| **자산 관리** | PLC·드라이브·로봇·서보 등 설비 자산 CRUD 및 트리 탐색 |
| **레이아웃 시각화** | SVG 블루프린트 위에 자산 배치·편집 (드래그 앤 드롭) |
| **상태 모니터링** | 백업 상태(BACKEDUP/UNCHANGED/FAILED) 및 실시간 Ping 모니터링 |
| **스케줄/트리거** | Cron 기반 스케줄 관리 및 트리거 빌더 |
| **매뉴얼 관리** | 기종별 PDF 매뉴얼 업로드·다운로드 |
| **사용자 관리** | 사용자·권한 관리 (Admin 역할 기반) |
| **mDNS 브로드캐스트** | `twms.local`로 네트워크 내 자동 검색 |

---

## 기술 스택

| 레이어 | 기술 | 용도 |
|--------|------|------|
| 런타임 | .NET 10.0 | 최신 .NET 런타임 |
| 액터 시스템 | Akka.NET 1.4.24 | DEXA 서버와 원격 메시징 |
| UI 프레임워크 | Blazor Server | 서버 사이드 인터랙티브 렌더링 |
| UI 컴포넌트 | MudBlazor 8.15.0 | Material Design 컴포넌트 |
| 데이터베이스 | SQLite + Dapper | 경량 ORM 기반 데이터 접근 |
| 시각화 | ECharts, D3, GridStack | 차트·그래프·반응형 대시보드 레이아웃 |
| 설정 | HOCON, appsettings.json | 계층적 설정 관리 |
| 네이티브 | C++ DLL (DeepPinger) | PLC 경유 네트워크 Ping |
| 언어 | F#, C#, C++ | 타입 안전 백엔드 + UI + 네이티브 성능 |

---

## 프로젝트 구조

```
Twms2.0/
├── src/
│   ├── DexaWeb.Dexa/              # F# — Akka.NET 클라이언트 라이브러리
│   │   ├── Infrastructure/        #   액터 시스템 설정, Guardian, Serializer
│   │   ├── Messages/              #   C2S, S2C, A2S 메시지 정의
│   │   ├── Models/                #   Asset, User, Schedule 등 도메인 모델
│   │   ├── Utilities/             #   mDNS, 에러 처리
│   │   ├── DexaClient.fs          #   메인 클라이언트 (초기화, 재연결)
│   │   └── IDexaClient.fs         #   C# 인터옵용 인터페이스
│   │
│   ├── DexaWeb.Server/            # C# — Blazor Server 웹 애플리케이션
│   │   ├── Components/
│   │   │   ├── Pages/             #   Razor 페이지 (Home, Layout, Assets, ...)
│   │   │   ├── Layout/            #   MainLayout, NavMenu
│   │   │   └── Shared/            #   재사용 컴포넌트
│   │   ├── Services/              #   비즈니스 로직 서비스
│   │   ├── Models/
│   │   │   ├── Dexa/              #   DEXA 도메인 모델 (ViewAsset, Action, ...)
│   │   │   ├── Twm/               #   TWM 로컬 모델 (Layout, Conn, Manual, ...)
│   │   │   └── Dashboard/         #   대시보드 모델
│   │   ├── Data/                  #   DB 연결 팩토리, 초기화
│   │   ├── HOCON/                 #   HOCON 파라미터 파싱
│   │   ├── wwwroot/               #   정적 자산 (CSS, JS, 이미지)
│   │   ├── Program.cs             #   앱 진입점 및 DI 구성
│   │   └── appsettings.json       #   애플리케이션 설정
│   │
│   ├── DeepPinger/                # C++ — 네이티브 Ping DLL (x64)
│   └── DeepPingerTest/            # C# — DeepPinger 테스트 앱 (WinForms)
│
├── installer/                     # 인스톨러 관련 파일
│   ├── twms2.0-setup.iss          #   InnoSetup 스크립트
│   ├── setup-https.ps1            #   HTTPS 인증서 설정 스크립트
│   └── output/                    #   빌드된 설치파일 출력 디렉토리
│
├── build-installer.bat            # 인스톨러 빌드 스크립트 (전체 빌드 + 패키징)
└── DexaWeb.slnx                   # 솔루션 파일
```

---

## 페이지 구성

```mermaid
graph LR
    Root["/ Home<br/>대시보드"]

    Root --- Layout["/layout<br/>레이아웃 시각화"]
    Layout --- Blueprint["/layout/blueprint<br/>블루프린트 편집기"]
    Layout --- Placement["/layout/placement<br/>자산 배치 편집기"]
    Layout --- Groups["/layout/groups<br/>자산 그룹 관리"]

    Root --- Assets["/assets<br/>자산 관리"]
    Assets --- Detail["/assets/{id}<br/>자산 상세"]
    Assets --- Explorer["/assets/explorer<br/>트리 탐색기"]
    Assets --- Grid["/assets/grid<br/>스프레드시트 편집"]

    Root --- History["/history<br/>액션·상태 이력"]
    Root --- Schedules["/schedules<br/>스케줄·트리거"]
    Root --- Status["/status<br/>실시간 상태"]

    Root --- Settings["/settings<br/>앱 설정"]
    Settings --- Manuals["/settings/manuals<br/>매뉴얼 관리"]

    Root --- Admin["/admin<br/>관리자"]
    Admin --- Users["/admin/users<br/>사용자·권한"]
    Admin --- DB["/admin/database<br/>DB 유지보수"]
```

---

## 아키텍처

```mermaid
graph TD
    subgraph Browser["🌐 브라우저"]
        UI["MudBlazor UI · ECharts · D3 · GridStack"]
    end

    subgraph Server["⚙️ DexaWeb.Server — C# Blazor Server"]
        direction LR
        subgraph Services["Services"]
            S1["AssetService"]
            S2["DexaReadSvc"]
            S3["DashboardSvc"]
            S4["PingService"]
            S5["ScheduleSvc"]
            S6["NotifySvc"]
        end
        subgraph Data["Data"]
            D1["DexaDb<br/>(SQLite RO)"]
            D2["TwmDb<br/>(SQLite RW)"]
            D3["Dapper ORM"]
        end
        subgraph Background["Background"]
            B1["PingBgSvc"]
            B2["MdnsHosted"]
            B3["CacheWarming"]
        end
    end

    subgraph Dexa["📡 DexaWeb.Dexa — F# 클라이언트 라이브러리"]
        DC["DexaClient<br/>(IDexaClient)"]
        GA["GuardianActor<br/>(Ask/Tell)"]
        DC --> GA
    end

    DS["🖥️ DEXA Server<br/>(원격 액터 시스템)"]

    Browser -- "SignalR" --> Server
    Server -- "Akka.Remote TCP" --> Dexa
    GA --> DS
```

---

## 데이터베이스

### DEXA DB (읽기 전용 — 외부)
DEXA 서버가 관리하는 SQLite 데이터베이스입니다. 경로: `C:\ProgramData\LS\DEXA\Storage\DEXA.sqlite3`

### TWM DB (읽기/쓰기 — 로컬)
TWMS 고유 데이터를 저장하는 로컬 SQLite입니다. 앱 시작 시 자동 생성·마이그레이션됩니다. 경로: `C:\ProgramData\DualSoft\TWMS2\twm.db.sqlite3`

```mermaid
erDiagram
    %% === DEXA DB (Read-Only) ===
    asset {
        int id PK
        int parentId FK
        int assetTypeId FK
        string parameter
        bool deleted
    }
    assetType {
        int id PK
        string userFriendlyName
        bool fake
    }
    user {
        int id PK
        string name
    }
    permission {
        int id PK
        int userId FK
    }
    schedule {
        int id PK
    }
    trigger {
        int id PK
        int scheduleId FK
    }
    actionLog {
        int id PK
        int assetId FK
    }

    assetType ||--o{ asset : "분류"
    asset ||--o{ asset : "parentId"
    user ||--o{ permission : "권한"
    schedule ||--o{ trigger : "트리거"
    asset ||--o{ actionLog : "이력"

    %% === TWM DB (Read/Write) ===
    TwmsAsset {
        int DexaId PK
        string StationNumber
        string Vendor
        string Spec
        int LineId
    }
    TwmsAssetConn {
        int DexaId PK
        string Ip
        string IpVia
        int Base
        int Slot
        bool IsRobotPLC
    }
    TwmsLayout {
        int Id PK
        string Name
    }
    TwmsLayoutLine {
        int Id PK
        int LayoutId FK
    }
    TwmsAssetPosition {
        int AssetId PK
        int LayoutId FK
        float X
        float Y
    }
    TwmsManual {
        int Id PK
        string ModelName
        string FileName
    }
    TwmPingLog {
        int Id PK
        int AssetId FK
    }

    asset ||--o| TwmsAsset : "DexaId 확장"
    asset ||--o| TwmsAssetConn : "DexaId 연결정보"
    TwmsLayout ||--o{ TwmsLayoutLine : "라인"
    TwmsLayout ||--o{ TwmsAssetPosition : "배치"
    asset ||--o{ TwmPingLog : "Ping 이력"
```

---

## 런타임 데이터 경로

설치 후 런타임 데이터는 `C:\ProgramData\DualSoft\TWMS2\` 하위에 저장됩니다.

```
C:\ProgramData\DualSoft\TWMS2\
├── twm.db.sqlite3        # TWM 로컬 데이터베이스
├── appsettings.json      # 런타임 설정 (UI에서 편집 가능)
├── manuals/              # 기종별 PDF 매뉴얼 파일
└── uploads/              # 업로드 파일 (블루프린트 이미지 등)
```

---

## 설정

`appsettings.json` 주요 항목:

```jsonc
{
  "App": {
    "Title": "종합설비관리",              // 앱 타이틀 (배포 환경에 맞게 변경)
    "ShowDate": true,
    "LogoPadding": 13
  },
  "DexaServer": {
    "ServerIp": "YOUR_SERVER_HOST",     // DEXA 서버 주소
    "ServerPort": 50001,                // Akka Remote 포트
    "AskTimeoutSeconds": 30,
    "PingTimeoutSeconds": 5,
    "ClientName": "twm-web"
  },
  "DexaDb": {
    "ConnectionString": "Data Source=C:\\ProgramData\\LS\\DEXA\\Storage\\DEXA.sqlite3;"
  },
  "TwmDb": {
    "ConnectionString": "Data Source=C:\\ProgramData\\DualSoft\\TWMS2\\twm.db.sqlite3;"
  },
  "PingSchedule": {
    "IntervalMinutes": 5,               // Ping 주기 (분)
    "Phase1MaxConcurrency": 10          // 동시 Ping 수
  },
  "Mdns": {
    "Hostname": "twms",                 // mDNS 호스트명 (twms.local)
    "Enabled": true
  },
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://0.0.0.0:80" }
    }
  }
}
```

---

## 빌드 및 실행

### 사전 요구 사항

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
- Visual Studio 2022+ (C++ 빌드 도구 포함, DeepPinger DLL 빌드 시 필요)

### 개발 빌드

```bash
cd src
dotnet build DexaWeb.slnx
```

> DeepPinger(C++ DLL)는 MSBuild 타겟에 의해 자동으로 빌드·복사됩니다.

### 실행

```bash
dotnet run --project src/DexaWeb.Server
```

브라우저에서 `http://localhost` 또는 `http://twms.local` (mDNS 활성화 시)로 접속합니다.

---

## 인스톨러 빌드

`build-installer.bat`를 실행하면 전체 빌드부터 설치파일 생성까지 자동 수행됩니다.

### 사전 요구 사항

| 도구 | 용도 | 비고 |
|------|------|------|
| .NET 10.0 SDK | C#/F# 프로젝트 빌드 | `dotnet` CLI 사용 |
| Visual Studio Build Tools | C++ DeepPinger DLL 빌드 | MSBuild + C++ 워크로드 필요 |
| [InnoSetup 6](https://jrsoftware.org/isinfo.php) | 설치파일(.exe) 생성 | 기본 경로 설치 권장 |
| vc_redist.x64.exe | VC++ 런타임 재배포 패키지 | 빌드 시 `installer/redist/`에 자동 다운로드(인터넷 필요). 오프라인 빌드 PC는 수동 배치 |

### 빌드 과정

```bash
build-installer.bat
```

스크립트가 수행하는 단계:

1. **Clean** — 이전 빌드 출력 정리
2. **MSBuild DeepPinger** — C++ DLL을 Release|x64로 빌드
3. **dotnet publish** — `win-x64` self-contained 배포 패키지 생성
4. **DLL 복사** — DeepPinger.dll을 publish 출력에 복사
5. **InnoSetup 컴파일** — `installer/twms2.0-setup.iss` → `installer/output/Twms2.0-Setup-{version}.exe`

> 사전 요구 사항 확인 단계에서 `installer/redist/vc_redist.x64.exe`가 없으면 자동으로 다운로드하여
> 인스톨러에 포함합니다. 다운로드 실패 시 빌드가 중단됩니다(오프라인 강제 빌드는 `SKIP_VCREDIST=1`).

빌드 완료 후 `installer/output/` 디렉토리에 설치파일이 생성됩니다.

### 인스톨러 포함 사항

- TWMS 2.0 애플리케이션 (self-contained, .NET 런타임 포함)
- DeepPinger.dll (네이티브 PLC Ping)
- Windows 서비스 등록 (`twms2.0`, 지연 자동 시작)
- 방화벽 규칙 자동 추가 (HTTP 80, HTTPS 443)
- ProgramData 디렉토리 자동 생성
- HTTPS 설정 (선택 구성요소, `setup-https.ps1`)
- VC++ 런타임 재배포 패키지 (`vc_redist.x64.exe`, 대상 PC에 미설치 시 자동 설치 → 오프라인 설치 보장)

---

## Windows 서비스 배포

인스톨러로 설치하면 **Windows 서비스**로 자동 등록됩니다.

| 항목 | 값 |
|------|-----|
| 서비스 이름 | `twms2.0` |
| 표시 이름 | `TWMS 2.0` |
| 시작 유형 | 지연 자동 시작 (`delayed-auto`) |
| 실패 복구 | 1차·2차: 서비스 재시작, 리셋 주기: 1일 |
| 설치 경로 | `C:\Program Files\DualSoft\TWMS2\` |

서비스 관리 명령:

```bash
# 서비스 상태 확인
sc query twms2.0

# 서비스 중지/시작
sc stop twms2.0
sc start twms2.0
```

---

## 백그라운드 서비스

| 서비스 | 설명 |
|--------|------|
| **PingBackgroundService** | 5분 주기로 전체 자산 Ping 상태 확인 (ICMP + DeepPinger) |
| **MdnsHostedService** | `twms.local` mDNS 브로드캐스트 |
| **캐시 워밍** | 시작 시 자산·액션·에이전트 데이터 사전 로드 |

---

## API 엔드포인트

| 메서드 | 경로 | 설명 |
|--------|------|------|
| GET | `/api/download/backup/{assetId}/{version}` | 백업 ZIP 다운로드 |
| Static | `/report/*` | DEXA 리포트 파일 서빙 |
| Static | `/uploads/*` | 업로드 파일 서빙 |
| Static | `/manuals/*` | PDF 매뉴얼 파일 서빙 |

---

## 라이선스

Private — 내부 사용 전용
