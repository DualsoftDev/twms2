using Dapper;

namespace Twms2.Server.Data;

/// <summary>
/// TWM DB 마이그레이션 관리자.
/// 버전 기반으로 스키마 변경을 추적하고 순차 적용.
/// </summary>
public class TwmDbInitializer
{
    private readonly TwmDbConnection _db;
    private readonly ILogger<TwmDbInitializer> _logger;

    public TwmDbInitializer(TwmDbConnection db, ILogger<TwmDbInitializer> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// 마이그레이션 정의: (버전, 설명, SQL)
    /// 새 마이그레이션 추가 시 리스트 끝에 추가.
    /// </summary>
    private static readonly (int Version, string Description, string Sql)[] Migrations =
    [
        // V1: 자산 공통 정보 테이블 (전 타입)
        (1, "TwmsAsset 테이블 생성", """
            CREATE TABLE IF NOT EXISTS TwmsAsset (
                DexaId           INTEGER PRIMARY KEY,
                AugStationNumber INTEGER,
                AugVendor        TEXT,
                AugSpec          TEXT,
                AugLineId        INTEGER,
                UpdatedAt        DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            """),

        // V2: PLC/Servo 연결정보 테이블
        (2, "TwmsAssetConn 테이블 생성", """
            CREATE TABLE IF NOT EXISTS TwmsAssetConn (
                DexaId        INTEGER PRIMARY KEY,
                AugIp         TEXT NOT NULL,
                AugIpVia      TEXT,
                AugBaseNumber INTEGER DEFAULT 0,
                AugSlotNumber INTEGER,
                AugIsRobotPLC INTEGER,
                UpdatedAt     DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            """),

        // V16: 기존 V15 설치본 → 구 테이블 제거 및 신규 스키마 재적용
        // 새 설치에서는 IF NOT EXISTS로 안전하게 처리됨
        (16, "DB 리뉴얼: 구 테이블 제거 및 신규 스키마 재적용", """
            DROP TABLE IF EXISTS TwmAssetGroup;
            DROP TABLE IF EXISTS TwmAssetGroupMember;
            DROP TABLE IF EXISTS TwmAssetTag;
            DROP TABLE IF EXISTS TwmPingResult;
            DROP TABLE IF EXISTS TwmSetting;
            DROP TABLE IF EXISTS TwmAuditLog;
            DROP TABLE IF EXISTS TwmHealthLog;
            DROP TABLE IF EXISTS TwmUserAug;
            DROP TABLE IF EXISTS TwmLayoutLine;
            DROP TABLE IF EXISTS TwmLayoutGroup;

            DROP TABLE IF EXISTS TwmsAsset;
            CREATE TABLE IF NOT EXISTS TwmsAsset (
                DexaId           INTEGER PRIMARY KEY,
                AugStationNumber INTEGER,
                AugVendor        TEXT,
                AugSpec          TEXT,
                AugLineId        INTEGER,
                UpdatedAt        DATETIME DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS TwmsAssetConn (
                DexaId        INTEGER PRIMARY KEY,
                AugIp         TEXT NOT NULL,
                AugIpVia      TEXT,
                AugBaseNumber INTEGER DEFAULT 0,
                AugSlotNumber INTEGER,
                AugIsRobotPLC INTEGER,
                UpdatedAt     DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            """),

        // V17: 레이아웃 라인/그룹 로컬 복사 테이블
        (17, "TwmsLayoutLine/TwmsLayoutGroup 테이블 생성", """
            CREATE TABLE IF NOT EXISTS TwmsLayoutLine (
                Id        INTEGER PRIMARY KEY,
                Name      TEXT NOT NULL,
                UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE IF NOT EXISTS TwmsLayoutGroup (
                Id        INTEGER PRIMARY KEY,
                AssetId   INTEGER NOT NULL,
                Floor     INTEGER,
                Assets    TEXT,
                UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            """),

        // V18: 핑 결과 영속화 + 상태 변경 이력
        (18, "TwmsPingResult/TwmsPingLog 테이블 생성", """
            CREATE TABLE IF NOT EXISTS TwmsPingResult (
                DexaAssetId INTEGER PRIMARY KEY,
                IpAddress   TEXT,
                Reachable   INTEGER NOT NULL DEFAULT 0,
                RoundtripMs INTEGER,
                CheckedAt   DATETIME NOT NULL
            );
            CREATE TABLE IF NOT EXISTS TwmsPingLog (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                DexaAssetId INTEGER NOT NULL,
                Reachable   INTEGER NOT NULL,
                CheckedAt   DATETIME NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_TwmsPingLog_Asset
                ON TwmsPingLog(DexaAssetId, CheckedAt DESC);
            """),

        // V19: 도면 레이아웃 — 라인 사각형 위치 + 도면 이미지 설정
        (19, "TwmsBlueprintRect/TwmsBlueprintConfig 테이블 생성", """
            CREATE TABLE IF NOT EXISTS TwmsBlueprintRect (
                LineId    INTEGER PRIMARY KEY,
                X         REAL NOT NULL DEFAULT 10,
                Y         REAL NOT NULL DEFAULT 10,
                Width     REAL NOT NULL DEFAULT 15,
                Height    REAL NOT NULL DEFAULT 10,
                UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE IF NOT EXISTS TwmsBlueprintConfig (
                Id          INTEGER PRIMARY KEY CHECK (Id = 1),
                ImagePath   TEXT,
                ImageWidth  REAL,
                ImageHeight REAL,
                UpdatedAt   DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            """),

        // V20: 벡터 도면 + 자산 배치 좌표
        (20, "DrawingData 컬럼 + TwmsAssetPosition 테이블", """
            ALTER TABLE TwmsBlueprintConfig ADD COLUMN DrawingData TEXT;

            CREATE TABLE IF NOT EXISTS TwmsAssetPosition (
                AssetId   INTEGER PRIMARY KEY,
                X         REAL NOT NULL DEFAULT 0,
                Y         REAL NOT NULL DEFAULT 0,
                Visible   INTEGER NOT NULL DEFAULT 0,
                UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
            );

            -- 기존 라인 사각형 좌표를 0~100 → 0~1000/600 스케일링
            UPDATE TwmsBlueprintRect SET
                X      = X * 10,
                Y      = Y * 6,
                Width  = Width * 10,
                Height = Height * 6,
                UpdatedAt = CURRENT_TIMESTAMP;
            """),

        // V21: 청사진 배경색
        (21, "BgColor 컬럼 추가", """
            ALTER TABLE TwmsBlueprintConfig ADD COLUMN BgColor TEXT;
            """),

        // V22: 그리드 색상
        (22, "GridColor 컬럼 추가", """
            ALTER TABLE TwmsBlueprintConfig ADD COLUMN GridColor TEXT;
            """),

        // V23: 멀티 레이아웃 지원 (TwmsLayout 마스터 + 기존 테이블에 LayoutId 추가)
        (23, "멀티 레이아웃 지원", """
            -- 1) TwmsLayout 마스터 테이블
            CREATE TABLE IF NOT EXISTS TwmsLayout (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Name      TEXT NOT NULL,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            INSERT INTO TwmsLayout (Id, Name, SortOrder) VALUES (1, '기본 레이아웃', 0);

            -- 2) TwmsBlueprintConfig 재생성 (Id → LayoutId PK)
            ALTER TABLE TwmsBlueprintConfig RENAME TO TwmsBlueprintConfig_old;
            CREATE TABLE TwmsBlueprintConfig (
                LayoutId    INTEGER PRIMARY KEY,
                ImagePath   TEXT,
                ImageWidth  REAL,
                ImageHeight REAL,
                DrawingData TEXT,
                BgColor     TEXT,
                GridColor   TEXT,
                UpdatedAt   DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            INSERT INTO TwmsBlueprintConfig (LayoutId, ImagePath, ImageWidth, ImageHeight, DrawingData, BgColor, GridColor, UpdatedAt)
            SELECT 1, ImagePath, ImageWidth, ImageHeight, DrawingData, BgColor, GridColor, UpdatedAt
            FROM TwmsBlueprintConfig_old WHERE Id = 1;
            DROP TABLE TwmsBlueprintConfig_old;

            -- 3) TwmsBlueprintRect 재생성 (PK: LayoutId + LineId)
            ALTER TABLE TwmsBlueprintRect RENAME TO TwmsBlueprintRect_old;
            CREATE TABLE TwmsBlueprintRect (
                LayoutId  INTEGER NOT NULL,
                LineId    INTEGER NOT NULL,
                X         REAL NOT NULL DEFAULT 10,
                Y         REAL NOT NULL DEFAULT 10,
                Width     REAL NOT NULL DEFAULT 15,
                Height    REAL NOT NULL DEFAULT 10,
                UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (LayoutId, LineId)
            );
            INSERT INTO TwmsBlueprintRect (LayoutId, LineId, X, Y, Width, Height, UpdatedAt)
            SELECT 1, LineId, X, Y, Width, Height, UpdatedAt FROM TwmsBlueprintRect_old;
            DROP TABLE TwmsBlueprintRect_old;

            -- 4) TwmsAssetPosition 재생성 (PK: LayoutId + AssetId)
            ALTER TABLE TwmsAssetPosition RENAME TO TwmsAssetPosition_old;
            CREATE TABLE TwmsAssetPosition (
                LayoutId  INTEGER NOT NULL,
                AssetId   INTEGER NOT NULL,
                X         REAL NOT NULL DEFAULT 0,
                Y         REAL NOT NULL DEFAULT 0,
                Visible   INTEGER NOT NULL DEFAULT 0,
                UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (LayoutId, AssetId)
            );
            INSERT INTO TwmsAssetPosition (LayoutId, AssetId, X, Y, Visible, UpdatedAt)
            SELECT 1, AssetId, X, Y, Visible, UpdatedAt FROM TwmsAssetPosition_old;
            DROP TABLE TwmsAssetPosition_old;
            """),

        // V24: 사용자 정의 배치 그룹
        (24, "TwmsPlacementGroup/TwmsPlacementGroupMember 테이블 생성", """
            CREATE TABLE IF NOT EXISTS TwmsPlacementGroup (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                LayoutId  INTEGER NOT NULL,
                Name      TEXT NOT NULL DEFAULT '',
                X         REAL NOT NULL DEFAULT 0,
                Y         REAL NOT NULL DEFAULT 0,
                Width     REAL NOT NULL DEFAULT 150,
                Height    REAL NOT NULL DEFAULT 100,
                Color     TEXT,
                UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE IF NOT EXISTS TwmsPlacementGroupMember (
                GroupId INTEGER NOT NULL,
                AssetId INTEGER NOT NULL,
                PRIMARY KEY (GroupId, AssetId)
            );
            """),

        // V25: 자산 크기 조절
        (25, "TwmsAssetPosition.Scale 컬럼 추가", """
            ALTER TABLE TwmsAssetPosition ADD COLUMN Scale REAL NOT NULL DEFAULT 1.0;
            """),

        // V26: 매뉴얼 관리 테이블
        (26, "TwmsManual 테이블 생성", """
            CREATE TABLE IF NOT EXISTS TwmsManual (
                Id             INTEGER PRIMARY KEY AUTOINCREMENT,
                Keyword        TEXT NOT NULL,
                FileName       TEXT NOT NULL,
                StoredFileName TEXT NOT NULL,
                UploadedAt     DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            """),

        // V27: 그리드 사용여부/크기 설정
        (27, "GridEnabled/GridSize 컬럼 추가", """
            ALTER TABLE TwmsBlueprintConfig ADD COLUMN GridEnabled INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE TwmsBlueprintConfig ADD COLUMN GridSize INTEGER NOT NULL DEFAULT 20;
            """),

        // V28: 배치 그룹 층 정보
        (28, "TwmsPlacementGroup.Floor 컬럼 추가", """
            ALTER TABLE TwmsPlacementGroup ADD COLUMN Floor INTEGER NOT NULL DEFAULT 1;
            """),

        // V29: 기존 Dexa 임포트 그룹의 Name("N층") → Floor 백필
        (29, "TwmsPlacementGroup.Floor 백필 (Name 파싱)", """
            UPDATE TwmsPlacementGroup
            SET Floor = CAST(REPLACE(Name, '층', '') AS INTEGER)
            WHERE Floor = 1
              AND Name LIKE '%층'
              AND CAST(REPLACE(Name, '층', '') AS INTEGER) != 0;
            """),

        (30, "LineColor 컬럼 추가 (라인 영역 카드 배경색)", """
            ALTER TABLE TwmsBlueprintConfig ADD COLUMN LineColor TEXT;
            """),

        // V31: 핑 이력 기간조회 인덱스 — /api/history/pings 의 CheckedAt 범위 조건이
        // 기존 (DexaAssetId, CheckedAt) 복합 인덱스를 못 타 풀스캔되던 것 해소
        (31, "IX_TwmsPingLog_CheckedAt 인덱스 추가", """
            CREATE INDEX IF NOT EXISTS IX_TwmsPingLog_CheckedAt
                ON TwmsPingLog(CheckedAt);
            """),
    ];

    public async Task InitializeAsync()
    {
        using var conn = _db.Create();

        // 마이그레이션 추적 테이블 생성
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS TwmMigration (
                Version     INTEGER PRIMARY KEY,
                Description NVARCHAR(256),
                AppliedAt   DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            """);

        // 현재 버전 조회
        var currentVersion = await conn.ExecuteScalarAsync<int?>(
            "SELECT MAX(Version) FROM TwmMigration") ?? 0;

        // 미적용 마이그레이션 순차 실행
        var applied = 0;
        foreach (var (version, description, sql) in Migrations)
        {
            if (version <= currentVersion) continue;

            await conn.ExecuteAsync(sql);
            await conn.ExecuteAsync(
                "INSERT INTO TwmMigration (Version, Description) VALUES (@Version, @Description)",
                new { Version = version, Description = description });

            _logger.LogInformation("TWM DB 마이그레이션 V{Version}: {Description}", version, description);
            applied++;
        }

        if (applied > 0)
            _logger.LogInformation("TWM DB 마이그레이션 완료: {Applied}개 적용 (현재 V{Version})",
                applied, Migrations[^1].Version);
        else
            _logger.LogInformation("TWM DB 스키마 최신 상태 (V{Version})", currentVersion);
    }
}
