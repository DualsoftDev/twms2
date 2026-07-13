-- ============================================================================
-- augName -> parameter HOCON name.value 변환 (SQLite / DEXA asset 테이블)
--
-- 목적: asset.augName 컬럼 값을 parameter HOCON 문자열의 name.value 자리
--       (name { ... value = "여기" ... })에 위치 기반(instr/substr)으로 치환.
--
-- 주의:
--   * SQLite 3.33+ 필요 (UPDATE ... FROM 구문).
--   * 실행 전 반드시 DB 파일 백업 + STEP 1 미리보기로 대상/건수 확인.
--   * 이스케이프는 백슬래시(\)와 큰따옴표(")만 처리한다. 개행/탭 등 제어문자,
--     그리고 name.value 가 중첩(③)형이거나 위치가 다른 자산은 이 위치기반
--     SQL의 대상이 아니다(WHERE로 대부분 걸러지나 100% 아님). README 참고.
-- ============================================================================

-- ============ STEP 1: 미리보기 (먼저 실행해 변경 대상/건수 확인) ============
WITH
base AS (
  SELECT id, parameter AS p,
         REPLACE(REPLACE(augName, '\', '\\'), '"', '\"') AS e
  FROM asset
  WHERE deleted = 0 AND augName IS NOT NULL AND augName <> ''
    AND instr(parameter, 'name {') > 0
),
a  AS (SELECT id, p, e, instr(p, 'name {')                       AS A FROM base),
b  AS (SELECT id, p, e, A,  (A - 1) + instr(substr(p, A), 'value') AS B FROM a),
q1 AS (SELECT id, p, e, B,  (B - 1) + instr(substr(p, B), '"')     AS Q1 FROM b),
q2 AS (SELECT id, p, e, Q1, Q1 + instr(substr(p, Q1 + 1), '"')     AS Q2 FROM q1)
SELECT id,
       substr(p, Q1 + 1, Q2 - Q1 - 1) AS current_name,
       e                              AS new_name
FROM q2
WHERE Q1 > 0 AND Q2 > Q1
  AND substr(p, Q1 + 1, Q2 - Q1 - 1) <> e
ORDER BY id;


-- ============ STEP 2: 실제 적용 (미리보기 확인 후 실행) ============
WITH
base AS (
  SELECT id, parameter AS p,
         REPLACE(REPLACE(augName, '\', '\\'), '"', '\"') AS e
  FROM asset
  WHERE deleted = 0 AND augName IS NOT NULL AND augName <> ''
    AND instr(parameter, 'name {') > 0
),
a  AS (SELECT id, p, e, instr(p, 'name {')                       AS A FROM base),
b  AS (SELECT id, p, e, A,  (A - 1) + instr(substr(p, A), 'value') AS B FROM a),
q1 AS (SELECT id, p, e, B,  (B - 1) + instr(substr(p, B), '"')     AS Q1 FROM b),
q2 AS (SELECT id, p, e, Q1, Q1 + instr(substr(p, Q1 + 1), '"')     AS Q2 FROM q1)
UPDATE asset
   SET parameter = substr(q2.p, 1, q2.Q1) || q2.e || substr(q2.p, q2.Q2)
  FROM q2
 WHERE asset.id = q2.id
   AND q2.Q1 > 0 AND q2.Q2 > q2.Q1
   AND substr(q2.p, q2.Q1 + 1, q2.Q2 - q2.Q1 - 1) <> q2.e;


-- ============================================================================
-- V2: 작은따옴표(') / 큰따옴표(") name.value 모두 인식
--
-- 위 STEP 1/2 는 value = "..."(큰따옴표)만 처리한다. value = 'HMI3' 처럼
-- 작은따옴표로 저장된 자산은 건너뛰거나(뒤에 " 없을 때), 잘못하면 뒤쪽의
-- 다른 큰따옴표 필드를 덮어쓸 수 있다. V2 는 value = 뒤에 처음 나오는
-- 따옴표 종류(' 또는 ")를 감지해 같은 종류의 닫는 따옴표까지를 값으로 본다.
--
-- 이스케이프: 열린 따옴표가 " 이면 \ 와 " 를, ' 이면 \ 와 ' 를 이스케이프.
-- (augName 이 순수 텍스트면 이스케이프 대상이 없어 그대로 들어감)
-- ============================================================================

-- ---- STEP 1 (V2): 미리보기 ----
WITH
base AS (
  SELECT id, parameter AS p, augName AS raw
  FROM asset
  WHERE deleted = 0 AND augName IS NOT NULL AND augName <> ''
    AND instr(parameter, 'name {') > 0
),
a  AS (SELECT id, p, raw, instr(p, 'name {')                    AS A FROM base),
b  AS (SELECT id, p, raw, (A - 1) + instr(substr(p, A), 'value') AS B FROM a),
e  AS (SELECT id, p, raw, (B - 1) + instr(substr(p, B), '=')     AS E FROM b),
-- value = 뒤에서 처음 나오는 " 와 ' 까지의 거리 (0 = 없음)
d  AS (SELECT id, p, raw, E,
              instr(substr(p, E + 1), '"')  AS dD,
              instr(substr(p, E + 1), '''') AS dS
       FROM e),
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
  SELECT id, p, qc, O, C,
    CASE qc WHEN '"' THEN REPLACE(REPLACE(raw, '\', '\\'), '"',  '\"')
            ELSE          REPLACE(REPLACE(raw, '\', '\\'), '''', '\''') END AS e
  FROM c WHERE C > O)
SELECT id, qc AS quote, substr(p, O + 1, C - O - 1) AS current_name, e AS new_name
FROM esc
WHERE substr(p, O + 1, C - O - 1) <> e
ORDER BY id;


-- ---- STEP 2 (V2): 실제 적용 (미리보기 확인 후) ----
WITH
base AS (
  SELECT id, parameter AS p, augName AS raw
  FROM asset
  WHERE deleted = 0 AND augName IS NOT NULL AND augName <> ''
    AND instr(parameter, 'name {') > 0
),
a  AS (SELECT id, p, raw, instr(p, 'name {')                    AS A FROM base),
b  AS (SELECT id, p, raw, (A - 1) + instr(substr(p, A), 'value') AS B FROM a),
e  AS (SELECT id, p, raw, (B - 1) + instr(substr(p, B), '=')     AS E FROM b),
d  AS (SELECT id, p, raw, E,
              instr(substr(p, E + 1), '"')  AS dD,
              instr(substr(p, E + 1), '''') AS dS
       FROM e),
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
  SELECT id, p, qc, O, C,
    CASE qc WHEN '"' THEN REPLACE(REPLACE(raw, '\', '\\'), '"',  '\"')
            ELSE          REPLACE(REPLACE(raw, '\', '\\'), '''', '\''') END AS e
  FROM c WHERE C > O)
UPDATE asset
   SET parameter = substr(esc.p, 1, esc.O) || esc.e || substr(esc.p, esc.C)
  FROM esc
 WHERE asset.id = esc.id
   AND substr(esc.p, esc.O + 1, esc.C - esc.O - 1) <> esc.e;


-- ============================================================================
-- V3 (권장): name.value 점표기 + name { } 블록형 + 작은/큰따옴표 전부 처리
--
-- 실제 DEXA parameter 는 두 가지 형식이 섞여 있다:
--   (점표기)  name.value = 'HMI3'
--   (블록형)  name { type = Text  value = "HMI3" }
-- V1/V2 는 블록형(name {)만 골라서 점표기 자산을 통째로 건너뛰었다.
-- V3 는 name.value 가 있으면 그 지점을, 없고 name { 가 있으면 그 안의 value 를
-- 앵커로 잡은 뒤, '=' 다음 첫 따옴표(' 또는 ")부터 같은 종류의 닫는 따옴표까지를
-- 값으로 보고 augName 으로 치환한다.
--
-- 주의: value 가 따옴표 없이 쓰인 경우(예: optionType.value = TCP)는 대상 아님.
--       name.value 는 예시상 항상 따옴표가 있으므로 문제 없음.
-- ============================================================================

-- ---- STEP 1 (V3): 미리보기 ----
WITH
base AS (
  SELECT id, parameter AS p, augName AS raw
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
  SELECT id, p, qc, O, C,
    CASE qc WHEN '"' THEN REPLACE(REPLACE(raw, '\', '\\'), '"',  '\"')
            ELSE          REPLACE(REPLACE(raw, '\', '\\'), '''', '\''') END AS e
  FROM c WHERE C > O)
SELECT id, qc AS quote, substr(p, O + 1, C - O - 1) AS current_name, e AS new_name
FROM esc
WHERE substr(p, O + 1, C - O - 1) <> e
ORDER BY id;


-- ---- STEP 2 (V3): 실제 적용 (미리보기 확인 후) ----
WITH
base AS (
  SELECT id, parameter AS p, augName AS raw
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
  SELECT id, p, qc, O, C,
    CASE qc WHEN '"' THEN REPLACE(REPLACE(raw, '\', '\\'), '"',  '\"')
            ELSE          REPLACE(REPLACE(raw, '\', '\\'), '''', '\''') END AS e
  FROM c WHERE C > O)
UPDATE asset
   SET parameter = substr(esc.p, 1, esc.O) || esc.e || substr(esc.p, esc.C)
  FROM esc
 WHERE asset.id = esc.id
   AND substr(esc.p, esc.O + 1, esc.C - esc.O - 1) <> esc.e;
