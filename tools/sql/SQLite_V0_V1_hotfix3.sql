/*
    hotfix3 for databases where SQLite_V0_V1.sql was already applied
    fixes action.schedule.id mismatch with actionLog.actionId

    2026-03-28 hotfix2
    2026-06-19 hotfix3: action.schedule.id 에 AUTOINCREMENT 추가
                        (DEXA 정식 스키마와 일치: INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL)
    IMPORTANT: Stop DEXA service before running. Back up the DB first.
*/

PRAGMA foreign_keys = OFF;

-- 1. Drop view first
DROP VIEW IF EXISTS [vwAction];

-- 2. Create temp holding table (no dot in name)
CREATE TABLE [tmp_action_schedule] (
    [id]              INTEGER PRIMARY KEY NOT NULL
    , [actionId]      INTEGER NOT NULL
    , [assetId]       INTEGER
    , [agentId]       INTEGER
    , [scheduleId]    INTEGER
    , [type]          INTEGER
    , [version]       INTEGER
    , [contentsChanged]  TINYINT NOT NULL DEFAULT 0
    , [nthSucceeded]  INTEGER DEFAULT -1
);

INSERT INTO [tmp_action_schedule] (
    id, actionId, assetId, agentId, scheduleId,
    type, version, contentsChanged, nthSucceeded
)
SELECT
    actionId,       -- id = actionId (original action id)
    actionId, assetId, agentId, scheduleId,
    type, version, contentsChanged, nthSucceeded
FROM "action.schedule";

-- 3. Drop original using double-quoted identifier
DROP TABLE "action.schedule";

-- 4. Create new table with correct name using double-quoted identifier
--    ★ hotfix3: id 에 AUTOINCREMENT 추가 (DEXA 정식 스키마와 동일)
CREATE TABLE "action.schedule" (
    [id]              INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
    , [actionId]      INTEGER NOT NULL
    , [assetId]       INTEGER
    , [agentId]       INTEGER
    , [scheduleId]    INTEGER
    , [type]          INTEGER
    , [version]       INTEGER
    , [contentsChanged]  TINYINT NOT NULL DEFAULT 0
    , [nthSucceeded]  INTEGER DEFAULT -1
    , FOREIGN KEY(actionId) REFERENCES [action.base](id)
    , FOREIGN KEY(scheduleId) REFERENCES schedule(id)
    , FOREIGN KEY(assetId)    REFERENCES asset(id)
    , FOREIGN KEY(agentId)    REFERENCES agent(id)
);

INSERT INTO "action.schedule" (
    id, actionId, assetId, agentId, scheduleId,
    type, version, contentsChanged, nthSucceeded
)
SELECT
    id, actionId, assetId, agentId, scheduleId,
    type, version, contentsChanged, nthSucceeded
FROM [tmp_action_schedule];

DROP TABLE [tmp_action_schedule];

-- 5. Update sqlite_sequence (AUTOINCREMENT 테이블이므로 다음 id 가 MAX(id)+1 부터 시작)
DELETE FROM sqlite_sequence WHERE name = 'action.schedule';
INSERT INTO sqlite_sequence (name, seq) VALUES ('action.schedule', (SELECT MAX(id) FROM "action.schedule"));

-- 6. Recreate vwAction
CREATE VIEW [vwAction] AS
SELECT
    acs.[id] AS actionId,
    acs.version,
    acb.started,
    acb.finished,
    acs.contentsChanged,
    acs.nthSucceeded,
    acb.memo,
    acb.exception,
    a.[id] AS assetId,
    s.[triggerId] AS triggerId,
    stg.connectionString || '\Backup\' || acs.assetId || '\' || acs.version || '.zip' AS backup,
    stg.connectionString || '\Report\' || acs.assetId || '\' || acs.version || '.zip' AS report
FROM
    "action.schedule" acs
    JOIN "action.base" acb    ON acs.actionId = acb.id
    JOIN [asset] a            ON a.id = acs.assetId
    LEFT JOIN [schedule] s    ON s.id = acs.scheduleId
    JOIN [storage] stg        ON a.storageId = stg.id
;

PRAGMA foreign_keys = ON;

-- 7. Verify: should return 0
SELECT COUNT(*) AS orphan_actionLogs
FROM actionLog al
LEFT JOIN "action.schedule" acs ON al.actionId = acs.id
WHERE acs.id IS NULL;
