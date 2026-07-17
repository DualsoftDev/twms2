-- ============================================================================
-- V4: V3 + Windows 금지문자 치환(sanitize)
--
-- augName -> parameter HOCON name.value 치환 (dexa.sqlite3 asset 테이블).
-- V3 와 동일하게 점표기(name.value = ...) / 블록형(name { value = ... }),
-- 작은/큰따옴표를 모두 처리하고, 추가로 augName 안의 Windows 파일명 금지문자
--   \ / : * ? " < > |  및 제어문자(탭/LF/CR)
-- 를 '_' 로 치환한 뒤 넣는다. (DEXA 가 백업 zip 을
-- Storage/Backup/{id}/{ver}/{자산이름}.zip 경로로 저장하므로 이름에 / 등이
-- 있으면 백업이 영구 실패하는 문제 방지 — 2026-07-13 발견)
--
-- 작은따옴표(')도 치환 대상에 포함한다. 파일명 금지문자는 아니지만,
-- HOCON 값에 \' 로 이스케이프해 넣으면 이 위치기반 파서가 재실행 시
-- \' 를 닫는 따옴표로 오인해 값을 깨뜨린다(멱등성 붕괴, 테스트로 확인).
--
-- 요구사항: SQLite 3.33+ (UPDATE ... FROM). DB Browser for SQLite 3.13.x
--           (엔진 3.46.x) 등 최신 툴에서 실행할 것. DEXA 번들 구버전
--           sqlite3.exe 에서는 동작하지 않는다.
--
-- 실행 순서:
--   0) DEXA 서비스 중지 + DB 파일 백업
--   1) STEP 0 — 치환 후 이름 중복 검사 (결과가 있으면 해당 자산 이름을 먼저 정리)
--   2) STEP 1 — 미리보기로 대상/건수와 current_name -> new_name 확인
--   3) STEP 2 — 실제 적용 후 Write Changes(변경사항 저장)
--   4) STEP 3 — 검증 (남은 불일치 0건 확인)
-- ============================================================================


-- ============ STEP 0 (V4): 치환 후 이름 중복 검사 ============
-- sanitize 로 서로 다른 augName 이 같은 이름이 될 수 있다.
-- (예: 'A/B' 와 'A?B' -> 둘 다 'A_B')  결과 0행이어야 안전.
WITH san AS (
  SELECT id,
    REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
    REPLACE(REPLACE(REPLACE(augName,
      char(9),  '_'), char(10), '_'), char(13), '_'),
      '\', '_'), '/', '_'), ':', '_'), '*', '_'), '?', '_'),
      '"', '_'), '<', '_'), '>', '_'), '|', '_'), '''', '_') AS s
  FROM asset
  WHERE deleted = 0 AND augName IS NOT NULL AND augName <> ''
)
SELECT s AS sanitized_name, COUNT(*) AS cnt, GROUP_CONCAT(id) AS ids
FROM san
GROUP BY s
HAVING COUNT(*) > 1
ORDER BY cnt DESC, s;


-- ============ STEP 1 (V4): 미리보기 ============
WITH
base AS (
  SELECT id, parameter AS p,
    -- sanitize: 제어문자(탭/LF/CR) + \ / : * ? " < > |  ->  '_'
    REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
    REPLACE(REPLACE(REPLACE(augName,
      char(9),  '_'), char(10), '_'), char(13), '_'),
      '\', '_'), '/', '_'), ':', '_'), '*', '_'), '?', '_'),
      '"', '_'), '<', '_'), '>', '_'), '|', '_'), '''', '_') AS raw
  FROM asset
  WHERE deleted = 0 AND augName IS NOT NULL AND augName <> ''
    AND (instr(parameter, 'name.value') > 0 OR instr(parameter, 'name {') > 0)
),
-- 'value' 토큰 위치 V: 점표기면 name.value 위치, 아니면 name { 뒤의 value
anc AS (
  SELECT id, p, raw,
    CASE WHEN instr(p, 'name.value') > 0
         THEN instr(p, 'name.value')
         ELSE (instr(p, 'name {') - 1) + instr(substr(p, instr(p, 'name {')), 'value')
    END AS V
  FROM base),
eq AS (SELECT id, p, raw, (V - 1) + instr(substr(p, V), '=') AS E FROM anc),
d  AS (SELECT id, p, raw, E,
              instr(substr(p, E + 1), '"')  AS dD,
              instr(substr(p, E + 1), '''') AS dS
       FROM eq),
o  AS (
  SELECT id, p, raw, E,
    CASE WHEN dD > 0 AND (dS = 0 OR dD < dS) THEN '"'
         WHEN dS > 0 AND (dD = 0 OR dS < dD) THEN ''''
         ELSE NULL END AS qc,
    CASE WHEN dD > 0 AND (dS = 0 OR dD < dS) THEN E + dD
         WHEN dS > 0 AND (dD = 0 OR dS < dD) THEN E + dS
         ELSE 0 END AS O
  FROM d),
c  AS (SELECT id, p, raw, qc, O, O + instr(substr(p, O + 1), qc) AS C
       FROM o WHERE qc IS NOT NULL AND O > 0),
esc AS (
  -- sanitize 가 \ " ' 를 전부 '_' 로 바꾸므로 이스케이프가 필요 없다.
  SELECT id, p, qc, O, C, raw AS e
  FROM c WHERE C > O)
SELECT id, qc AS quote, substr(p, O + 1, C - O - 1) AS current_name, e AS new_name
FROM esc
WHERE substr(p, O + 1, C - O - 1) <> e
ORDER BY id;


-- ============ STEP 2 (V4): 실제 적용 (미리보기 확인 후) ============
WITH
base AS (
  SELECT id, parameter AS p,
    REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
    REPLACE(REPLACE(REPLACE(augName,
      char(9),  '_'), char(10), '_'), char(13), '_'),
      '\', '_'), '/', '_'), ':', '_'), '*', '_'), '?', '_'),
      '"', '_'), '<', '_'), '>', '_'), '|', '_'), '''', '_') AS raw
  FROM asset
  WHERE deleted = 0 AND augName IS NOT NULL AND augName <> ''
    AND (instr(parameter, 'name.value') > 0 OR instr(parameter, 'name {') > 0)
),
anc AS (
  SELECT id, p, raw,
    CASE WHEN instr(p, 'name.value') > 0
         THEN instr(p, 'name.value')
         ELSE (instr(p, 'name {') - 1) + instr(substr(p, instr(p, 'name {')), 'value')
    END AS V
  FROM base),
eq AS (SELECT id, p, raw, (V - 1) + instr(substr(p, V), '=') AS E FROM anc),
d  AS (SELECT id, p, raw, E,
              instr(substr(p, E + 1), '"')  AS dD,
              instr(substr(p, E + 1), '''') AS dS
       FROM eq),
o  AS (
  SELECT id, p, raw, E,
    CASE WHEN dD > 0 AND (dS = 0 OR dD < dS) THEN '"'
         WHEN dS > 0 AND (dD = 0 OR dS < dD) THEN ''''
         ELSE NULL END AS qc,
    CASE WHEN dD > 0 AND (dS = 0 OR dD < dS) THEN E + dD
         WHEN dS > 0 AND (dD = 0 OR dS < dD) THEN E + dS
         ELSE 0 END AS O
  FROM d),
c  AS (SELECT id, p, raw, qc, O, O + instr(substr(p, O + 1), qc) AS C
       FROM o WHERE qc IS NOT NULL AND O > 0),
esc AS (
  SELECT id, p, qc, O, C, raw AS e
  FROM c WHERE C > O)
UPDATE asset
   SET parameter = substr(esc.p, 1, esc.O) || esc.e || substr(esc.p, esc.C)
  FROM esc
 WHERE asset.id = esc.id
   AND substr(esc.p, esc.O + 1, esc.C - esc.O - 1) <> esc.e;


-- ============ STEP 3 (V4): 검증 — 0행이어야 성공 ============
-- STEP 1 을 다시 실행해도 된다(적용 후 재실행 시 결과 0행이어야 함).
-- 아래는 name.value 자리에 금지문자가 남아 있는 행 검사.
WITH
base AS (
  SELECT id, parameter AS p
  FROM asset
  WHERE deleted = 0
    AND (instr(parameter, 'name.value') > 0 OR instr(parameter, 'name {') > 0)
),
anc AS (
  SELECT id, p,
    CASE WHEN instr(p, 'name.value') > 0
         THEN instr(p, 'name.value')
         ELSE (instr(p, 'name {') - 1) + instr(substr(p, instr(p, 'name {')), 'value')
    END AS V
  FROM base),
eq AS (SELECT id, p, (V - 1) + instr(substr(p, V), '=') AS E FROM anc),
d  AS (SELECT id, p, E,
              instr(substr(p, E + 1), '"')  AS dD,
              instr(substr(p, E + 1), '''') AS dS
       FROM eq),
o  AS (
  SELECT id, p, E,
    CASE WHEN dD > 0 AND (dS = 0 OR dD < dS) THEN '"'
         WHEN dS > 0 AND (dD = 0 OR dS < dD) THEN ''''
         ELSE NULL END AS qc,
    CASE WHEN dD > 0 AND (dS = 0 OR dD < dS) THEN E + dD
         WHEN dS > 0 AND (dD = 0 OR dS < dD) THEN E + dS
         ELSE 0 END AS O
  FROM d),
c  AS (SELECT id, p, qc, O, O + instr(substr(p, O + 1), qc) AS C
       FROM o WHERE qc IS NOT NULL AND O > 0),
cur AS (SELECT id, substr(p, O + 1, C - O - 1) AS name_value FROM c WHERE C > O)
SELECT id, name_value
FROM cur
WHERE instr(name_value, '/') > 0 OR instr(name_value, '\') > 0
   OR instr(name_value, ':') > 0 OR instr(name_value, '*') > 0
   OR instr(name_value, '?') > 0 OR instr(name_value, '"') > 0
   OR instr(name_value, '<') > 0 OR instr(name_value, '>') > 0
   OR instr(name_value, '|') > 0
ORDER BY id;
