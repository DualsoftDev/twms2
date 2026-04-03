"""
DEXA SQLite3 → MSSQL Server 데이터 마이그레이션 스크립트

사용법:
    pip install pyodbc
    python migrate_sqlite_to_mssql.py

설정:
    아래 SQLITE_PATH, MSSQL_CONNECTION 을 환경에 맞게 수정

특징:
    - SQLite 스키마를 자동 감지하여 MSSQL 테이블/뷰를 자동 생성
    - DDL 파일을 별도로 실행할 필요 없음
    - DEXA 버전과 무관하게 동작
"""

import sqlite3
import pyodbc
import sys
import re
from datetime import datetime

# ============================================================
# 설정
# ============================================================
SQLITE_PATH = r"C:\ProgramData\LS\DEXA\Storage\DEXA.sqlite3"

MSSQL_CONNECTION = (
    "DRIVER={ODBC Driver 17 for SQL Server};"
    "SERVER=localhost;"
    "DATABASE=lpb;"
    "UID=sa;"
    "PWD=yourpassword;"
)

# FK 의존 순서. 실제 DB에 없는 테이블은 자동으로 건너뜀.
# DEXA 2.20 기준: action → action.base + action.schedule 분리,
#                 asset.status 추가
TABLE_ORDER = [
    # 의존 없음
    "dll",
    "user",
    "storage",
    "trigger",
    "layoutFactory",
    "assetType",
    # layoutFactory 의존
    "layoutLine",
    # 의존 없음
    "projectFile",
    "agent",
    "client",
    # assetType, projectFile, storage 의존
    "asset",
    # asset 의존
    "layoutGroup",
    "asset.status",
    # user, asset 의존
    "permission",
    # asset, trigger 의존
    "schedule",
    # DEXA 2.20: action.base → action.schedule 순서
    "action.base",
    "action.schedule",
    # 구버전 호환: 단일 action 테이블
    "action",
    # action 의존
    "actionLog",
    "backupLog",
    # 기타
    "healthLog",
    "vnc",
    "tableHistory",
    "pseudoPing",
]

# SQLite → MSSQL 타입 매핑
TYPE_MAP = {
    "INTEGER": "INT",
    "INT": "INT",
    "TINYINT": "TINYINT",
    "SMALLINT": "SMALLINT",
    "BIGINT": "BIGINT",
    "REAL": "REAL",
    "FLOAT": "FLOAT",
    "DOUBLE": "FLOAT",
    "NUMERIC": "NUMERIC",
    "DECIMAL": "DECIMAL",
    "TEXT": "NVARCHAR(4000)",
    "NVARCHAR": "NVARCHAR",
    "VARCHAR": "NVARCHAR",
    "CHAR": "NCHAR",
    "BLOB": "VARBINARY(MAX)",
    "DATETIME": "DATETIME2(7)",
    "DATE": "DATE",
    "BOOLEAN": "TINYINT",
}


def get_sqlite_tables(sqlite_conn: sqlite3.Connection) -> list[str]:
    """SQLite DB에 실제 존재하는 테이블 목록 반환 (뷰 제외)"""
    cursor = sqlite_conn.execute(
        "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name"
    )
    return [row[0] for row in cursor.fetchall()]


def get_sqlite_views(sqlite_conn: sqlite3.Connection) -> list[tuple[str, str]]:
    """SQLite DB에 존재하는 뷰 목록 반환 [(name, sql), ...]"""
    cursor = sqlite_conn.execute(
        "SELECT name, sql FROM sqlite_master WHERE type='view' ORDER BY name"
    )
    return [(row[0], row[1]) for row in cursor.fetchall()]


def get_columns_info(sqlite_conn: sqlite3.Connection, table: str) -> list[dict]:
    """SQLite 테이블의 컬럼 상세 정보 반환"""
    cursor = sqlite_conn.execute(f'PRAGMA table_info([{table}])')
    columns = []
    for row in cursor.fetchall():
        columns.append({
            "cid": row[0],
            "name": row[1],
            "type": row[2] if row[2] else "TEXT",
            "notnull": row[3],
            "default": row[4],
            "pk": row[5],
        })
    return columns


def get_column_names(sqlite_conn: sqlite3.Connection, table: str) -> list[str]:
    """테이블의 컬럼명 목록 반환"""
    return [col["name"] for col in get_columns_info(sqlite_conn, table)]


def get_row_count(sqlite_conn: sqlite3.Connection, table: str) -> int:
    cursor = sqlite_conn.execute(f'SELECT COUNT(*) FROM [{table}]')
    return cursor.fetchone()[0]


def sqlite_type_to_mssql(sqlite_type: str) -> str:
    """SQLite 타입을 MSSQL 타입으로 변환"""
    upper = sqlite_type.upper().strip()

    # 괄호 포함 타입 처리: NVARCHAR(64) 등
    match = re.match(r"(\w+)\((.+)\)", upper)
    if match:
        base_type = match.group(1)
        params = match.group(2)
        if base_type in ("NVARCHAR", "VARCHAR", "TEXT"):
            return f"NVARCHAR({params})"
        if base_type in ("CHAR", "NCHAR"):
            return f"NCHAR({params})"
        if base_type in ("DECIMAL", "NUMERIC"):
            return f"{base_type}({params})"
        if base_type == "VARBINARY":
            return f"VARBINARY({params})"

    # 단순 타입 매핑
    if upper in TYPE_MAP:
        return TYPE_MAP[upper]

    # 매칭 안 되면 NVARCHAR(4000) 기본값
    return "NVARCHAR(4000)"


def build_mssql_create_table(sqlite_conn: sqlite3.Connection, table: str) -> str:
    """SQLite 스키마에서 MSSQL CREATE TABLE 문 생성"""
    columns = get_columns_info(sqlite_conn, table)
    pk_columns = [col for col in columns if col["pk"] > 0]

    col_defs = []
    for col in columns:
        mssql_type = sqlite_type_to_mssql(col["type"])
        parts = [f"[{col['name']}]", mssql_type]

        # 단일 PK + INTEGER = IDENTITY
        is_single_int_pk = (
            col["pk"] == 1
            and len(pk_columns) == 1
            and col["type"].upper() in ("INTEGER", "INT")
        )

        if is_single_int_pk:
            parts = [f"[{col['name']}]", "INT", "PRIMARY KEY IDENTITY NOT NULL"]
        else:
            if col["notnull"]:
                parts.append("NOT NULL")

            if col["default"] is not None:
                default_val = col["default"]
                # SQLite 기본값 → MSSQL 호환
                if default_val.upper() == "CURRENT_TIMESTAMP":
                    default_val = "GETDATE()"
                parts.append(f"DEFAULT {default_val}")

        col_defs.append("    " + " ".join(parts))

    # 복합 PK
    if len(pk_columns) > 1:
        pk_list = ", ".join(f"[{col['name']}]" for col in pk_columns)
        col_defs.append(f"    PRIMARY KEY ({pk_list})")

    col_str = ",\n".join(col_defs)
    return f"CREATE TABLE [{table}] (\n{col_str}\n);"


def convert_view_sql_to_mssql(name: str, sql: str) -> str | None:
    """SQLite CREATE VIEW 문을 MSSQL 호환 문으로 변환 (best-effort)"""
    if not sql:
        return None

    # CREATE VIEW ... AS 부분 추출
    match = re.match(r"CREATE\s+VIEW\s+\[?(\w[\w.]*)\]?\s+AS\s+(.+)", sql, re.DOTALL | re.IGNORECASE)
    if not match:
        return None

    view_body = match.group(2).strip().rstrip(";")

    # SQLite → MSSQL 함수 변환
    view_body = re.sub(r"\bLIMIT\s+(\d+)", r"", view_body)  # LIMIT 제거 (TOP은 수동 조정 필요)
    view_body = re.sub(r"\bdatetime\s*\(\s*'now'\s*\)", "GETDATE()", view_body, flags=re.IGNORECASE)

    # EXEC('CREATE VIEW ...') 형태로 래핑
    escaped_body = view_body.replace("'", "''")
    return f"EXEC('CREATE VIEW [{name}] AS {escaped_body}');"


def check_mssql_table_exists(mssql_conn: pyodbc.Connection, table: str) -> bool:
    cursor = mssql_conn.cursor()
    cursor.execute(
        "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = ?",
        table,
    )
    return cursor.fetchone()[0] > 0


def check_mssql_view_exists(mssql_conn: pyodbc.Connection, name: str) -> bool:
    cursor = mssql_conn.cursor()
    cursor.execute(
        "SELECT COUNT(*) FROM INFORMATION_SCHEMA.VIEWS WHERE TABLE_NAME = ?",
        name,
    )
    return cursor.fetchone()[0] > 0


def ensure_database(mssql_conn: pyodbc.Connection):
    """lpb 데이터베이스가 없으면 생성"""
    cursor = mssql_conn.cursor()
    cursor.execute(
        "SELECT COUNT(*) FROM sys.databases WHERE name = 'lpb'"
    )
    if cursor.fetchone()[0] == 0:
        # autocommit 모드에서 CREATE DATABASE 실행
        mssql_conn.autocommit = True
        cursor.execute("CREATE DATABASE lpb")
        mssql_conn.autocommit = False
        print("  lpb 데이터베이스 생성 완료")


def migrate_table(
    sqlite_conn: sqlite3.Connection,
    mssql_conn: pyodbc.Connection,
    table: str,
) -> int:
    """단일 테이블 데이터 마이그레이션. 이관된 행 수 반환."""
    cols = get_column_names(sqlite_conn, table)
    if not cols:
        return 0

    row_count = get_row_count(sqlite_conn, table)
    if row_count == 0:
        return 0

    col_list = ", ".join(f"[{c}]" for c in cols)
    placeholders = ", ".join(["?"] * len(cols))
    insert_sql = f"INSERT INTO [{table}] ({col_list}) VALUES ({placeholders})"

    cursor = mssql_conn.cursor()

    # IDENTITY_INSERT ON
    has_identity = False
    try:
        cursor.execute(f"SET IDENTITY_INSERT [{table}] ON")
        has_identity = True
    except pyodbc.ProgrammingError:
        pass

    # 배치 단위 삽입
    BATCH_SIZE = 500
    rows = sqlite_conn.execute(f"SELECT * FROM [{table}]").fetchall()
    migrated = 0

    for i in range(0, len(rows), BATCH_SIZE):
        batch = rows[i : i + BATCH_SIZE]
        for row in batch:
            values = []
            for v in row:
                # SQLite TEXT 날짜 → Python datetime 변환 시도
                if isinstance(v, str):
                    for fmt in ("%Y-%m-%d %H:%M:%S", "%Y-%m-%dT%H:%M:%S", "%Y-%m-%d %H:%M:%S.%f"):
                        try:
                            v = datetime.strptime(v, fmt)
                            break
                        except ValueError:
                            continue
                values.append(v)
            try:
                cursor.execute(insert_sql, values)
            except pyodbc.IntegrityError as e:
                print(f"  [SKIP] {table} row: {e}")
                continue
        migrated += len(batch)

    if has_identity:
        try:
            cursor.execute(f"SET IDENTITY_INSERT [{table}] OFF")
        except pyodbc.ProgrammingError:
            pass

    mssql_conn.commit()
    return migrated


def reseed_identity(mssql_conn: pyodbc.Connection, table: str):
    """IDENTITY 시드를 현재 최대값으로 재설정"""
    try:
        cursor = mssql_conn.cursor()
        cursor.execute(f"DBCC CHECKIDENT ('{table}', RESEED)")
        mssql_conn.commit()
    except Exception:
        pass


def main():
    print("=" * 60)
    print("DEXA SQLite3 → MSSQL 마이그레이션")
    print("=" * 60)

    # SQLite 연결
    print(f"\n[1] SQLite 연결: {SQLITE_PATH}")
    try:
        sqlite_conn = sqlite3.connect(SQLITE_PATH)
    except Exception as e:
        print(f"  SQLite 연결 실패: {e}")
        sys.exit(1)

    sqlite_tables = get_sqlite_tables(sqlite_conn)
    sqlite_views = get_sqlite_views(sqlite_conn)
    print(f"  테이블 수: {len(sqlite_tables)}")
    print(f"  테이블: {', '.join(sorted(sqlite_tables))}")
    print(f"  뷰 수: {len(sqlite_views)}")

    # MSSQL 연결
    print(f"\n[2] MSSQL 연결...")
    try:
        mssql_conn = pyodbc.connect(MSSQL_CONNECTION)
    except Exception as e:
        print(f"  MSSQL 연결 실패: {e}")
        print("  연결 문자열을 확인하세요: MSSQL_CONNECTION")
        sys.exit(1)
    print("  연결 성공")

    # 마이그레이션 대상 결정 (FK 순서)
    ordered_tables = [t for t in TABLE_ORDER if t in sqlite_tables]
    extra_tables = sorted(set(sqlite_tables) - set(TABLE_ORDER))
    if extra_tables:
        print(f"\n  [참고] TABLE_ORDER에 없는 테이블 발견: {', '.join(extra_tables)}")
        ordered_tables.extend(extra_tables)

    # ── 스키마 생성 ──
    print(f"\n[3] MSSQL 스키마 자동 생성 ({len(ordered_tables)}개 테이블)")
    print("-" * 60)

    mssql_cursor = mssql_conn.cursor()
    mssql_cursor.execute("USE lpb; SET LANGUAGE us_english; SET DATEFORMAT mdy;")
    mssql_conn.commit()

    created_tables = []
    for table in ordered_tables:
        if check_mssql_table_exists(mssql_conn, table):
            print(f"  {table:30s}  (이미 존재 - 건너뜀)")
            created_tables.append(table)
            continue

        ddl = build_mssql_create_table(sqlite_conn, table)
        try:
            mssql_cursor.execute(ddl)
            mssql_conn.commit()
            created_tables.append(table)
            print(f"  {table:30s}  생성 완료")
        except Exception as e:
            print(f"  {table:30s}  [오류] {e}")

    # 뷰 생성
    if sqlite_views:
        print(f"\n    뷰 생성 ({len(sqlite_views)}개)")
        for name, sql in sqlite_views:
            if check_mssql_view_exists(mssql_conn, name):
                print(f"  {name:30s}  (이미 존재 - 건너뜀)")
                continue
            converted = convert_view_sql_to_mssql(name, sql)
            if converted:
                try:
                    mssql_cursor.execute(converted)
                    mssql_conn.commit()
                    print(f"  {name:30s}  생성 완료")
                except Exception as e:
                    print(f"  {name:30s}  [경고] 뷰 생성 실패 (수동 확인 필요): {e}")
            else:
                print(f"  {name:30s}  [경고] SQL 변환 실패 (수동 확인 필요)")

    # ── 데이터 이관 ──
    print(f"\n[4] 데이터 마이그레이션 ({len(created_tables)}개 테이블)")
    print("-" * 60)

    total_rows = 0
    for table in created_tables:
        src_count = get_row_count(sqlite_conn, table)
        if src_count == 0:
            print(f"  {table:30s}  (빈 테이블 - 건너뜀)")
            continue

        try:
            migrated = migrate_table(sqlite_conn, mssql_conn, table)
            total_rows += migrated
            print(f"  {table:30s}  {migrated:>6d} rows")
        except Exception as e:
            print(f"  {table:30s}  [오류] {e}")

    # IDENTITY 시드 재설정
    print(f"\n[5] IDENTITY 시드 재설정...")
    for table in created_tables:
        reseed_identity(mssql_conn, table)
    print("  완료")

    # 결과
    print(f"\n{'=' * 60}")
    print(f"마이그레이션 완료: {total_rows} rows ({len(created_tables)} tables)")
    print(f"{'=' * 60}")

    sqlite_conn.close()
    mssql_conn.close()


if __name__ == "__main__":
    main()
