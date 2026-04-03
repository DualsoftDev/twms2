"""
DEXA MSSQL Server → SQLite3 데이터 마이그레이션 스크립트

사용법:
    pip install pyodbc
    python migrate_mssql_to_sqlite.py

설정:
    아래 MSSQL_CONNECTION, SQLITE_OUTPUT_PATH 를 환경에 맞게 수정

주의:
    - 기존 SQLite 파일이 있으면 백업 후 새로 생성합니다.
    - MSSQL의 테이블 스키마를 자동 감지하여 SQLite DDL을 생성합니다.
"""

import sqlite3
import pyodbc
import sys
import os
import shutil
from datetime import datetime

# ============================================================
# 설정
# ============================================================
MSSQL_CONNECTION = (
    "DRIVER={ODBC Driver 17 for SQL Server};"
    "SERVER=localhost;"
    "DATABASE=lpb;"
    "UID=sa;"
    "PWD=yourpassword;"
)

SQLITE_OUTPUT_PATH = r"C:\ProgramData\LS\DEXA\Storage\DEXA.sqlite3"

# FK 의존 순서 (삽입 순서)
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
    # DEXA 2.20
    "action.base",
    "action.schedule",
    # 구버전 호환
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

# MSSQL → SQLite 타입 매핑
TYPE_MAP = {
    "int": "INTEGER",
    "bigint": "INTEGER",
    "smallint": "INTEGER",
    "tinyint": "INTEGER",
    "bit": "INTEGER",
    "float": "REAL",
    "real": "REAL",
    "decimal": "REAL",
    "numeric": "REAL",
    "nvarchar": "TEXT",
    "varchar": "TEXT",
    "nchar": "TEXT",
    "char": "TEXT",
    "text": "TEXT",
    "ntext": "TEXT",
    "datetime": "TEXT",
    "datetime2": "TEXT",
    "date": "TEXT",
    "time": "TEXT",
    "varbinary": "BLOB",
    "binary": "BLOB",
    "image": "BLOB",
}


def get_mssql_tables(mssql_conn: pyodbc.Connection) -> set[str]:
    """MSSQL DB에 실제 존재하는 사용자 테이블 목록 반환"""
    cursor = mssql_conn.cursor()
    cursor.execute(
        "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES "
        "WHERE TABLE_TYPE = 'BASE TABLE'"
    )
    return {row[0] for row in cursor.fetchall()}


def get_mssql_columns(
    mssql_conn: pyodbc.Connection, table: str
) -> list[dict]:
    """MSSQL 테이블의 컬럼 정보 반환"""
    cursor = mssql_conn.cursor()
    cursor.execute(
        """
        SELECT
            c.COLUMN_NAME,
            c.DATA_TYPE,
            c.IS_NULLABLE,
            c.COLUMN_DEFAULT,
            c.CHARACTER_MAXIMUM_LENGTH,
            COLUMNPROPERTY(OBJECT_ID(c.TABLE_SCHEMA + '.' + c.TABLE_NAME),
                           c.COLUMN_NAME, 'IsIdentity') AS is_identity
        FROM INFORMATION_SCHEMA.COLUMNS c
        WHERE c.TABLE_NAME = ?
        ORDER BY c.ORDINAL_POSITION
        """,
        table,
    )
    columns = []
    for row in cursor.fetchall():
        columns.append(
            {
                "name": row[0],
                "type": row[1].lower(),
                "nullable": row[2] == "YES",
                "default": row[3],
                "max_length": row[4],
                "is_identity": row[5] == 1,
            }
        )
    return columns


def get_mssql_primary_keys(
    mssql_conn: pyodbc.Connection, table: str
) -> list[str]:
    """MSSQL 테이블의 PK 컬럼 목록"""
    cursor = mssql_conn.cursor()
    cursor.execute(
        """
        SELECT COLUMN_NAME
        FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
        JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
          ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
        WHERE tc.TABLE_NAME = ? AND tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
        ORDER BY kcu.ORDINAL_POSITION
        """,
        table,
    )
    return [row[0] for row in cursor.fetchall()]


def build_sqlite_create_table(
    mssql_conn: pyodbc.Connection, table: str
) -> str:
    """MSSQL 스키마에서 SQLite CREATE TABLE 문 생성"""
    columns = get_mssql_columns(mssql_conn, table)
    pks = get_mssql_primary_keys(mssql_conn, table)

    col_defs = []
    for col in columns:
        sqlite_type = TYPE_MAP.get(col["type"], "TEXT")
        parts = [f"[{col['name']}]", sqlite_type]

        # PRIMARY KEY + AUTOINCREMENT (단일 PK이고 INTEGER IDENTITY인 경우)
        if col["name"] in pks and len(pks) == 1 and col["is_identity"]:
            parts.append("PRIMARY KEY AUTOINCREMENT")
        elif col["name"] in pks and len(pks) == 1:
            parts.append("PRIMARY KEY")

        if not col["nullable"] and col["name"] not in pks:
            parts.append("NOT NULL")

        # DEFAULT 처리 (MSSQL 기본값 → SQLite 호환)
        if col["default"] is not None:
            default_val = col["default"].strip("()")
            # MSSQL 함수 변환
            if "getdate" in default_val.lower():
                default_val = "CURRENT_TIMESTAMP"
            parts.append(f"DEFAULT {default_val}")

        col_defs.append("    " + " ".join(parts))

    # 복합 PK
    if len(pks) > 1:
        pk_list = ", ".join(f"[{pk}]" for pk in pks)
        col_defs.append(f"    PRIMARY KEY ({pk_list})")

    col_str = ",\n".join(col_defs)
    return f"CREATE TABLE IF NOT EXISTS [{table}] (\n{col_str}\n);"


def get_mssql_row_count(mssql_conn: pyodbc.Connection, table: str) -> int:
    cursor = mssql_conn.cursor()
    cursor.execute(f"SELECT COUNT(*) FROM [{table}]")
    return cursor.fetchone()[0]


def migrate_table(
    mssql_conn: pyodbc.Connection,
    sqlite_conn: sqlite3.Connection,
    table: str,
    columns: list[dict],
) -> int:
    """단일 테이블 데이터 마이그레이션. 이관된 행 수 반환."""
    row_count = get_mssql_row_count(mssql_conn, table)
    if row_count == 0:
        return 0

    col_names = [col["name"] for col in columns]
    mssql_col_list = ", ".join(f"[{c}]" for c in col_names)
    sqlite_col_list = ", ".join(f"[{c}]" for c in col_names)
    placeholders = ", ".join(["?"] * len(col_names))
    insert_sql = (
        f"INSERT INTO [{table}] ({sqlite_col_list}) VALUES ({placeholders})"
    )

    mssql_cursor = mssql_conn.cursor()
    mssql_cursor.execute(f"SELECT {mssql_col_list} FROM [{table}]")

    BATCH_SIZE = 500
    migrated = 0

    while True:
        rows = mssql_cursor.fetchmany(BATCH_SIZE)
        if not rows:
            break

        for row in rows:
            values = []
            for v in row:
                # datetime → ISO 문자열 (SQLite TEXT 호환)
                if isinstance(v, datetime):
                    v = v.strftime("%Y-%m-%d %H:%M:%S.%f")[:-3]
                values.append(v)

            try:
                sqlite_conn.execute(insert_sql, values)
            except sqlite3.IntegrityError as e:
                print(f"  [SKIP] {table} row: {e}")
                continue

        migrated += len(rows)

    sqlite_conn.commit()
    return migrated


def main():
    print("=" * 60)
    print("DEXA MSSQL → SQLite3 마이그레이션")
    print("=" * 60)

    # MSSQL 연결
    print(f"\n[1] MSSQL 연결...")
    try:
        mssql_conn = pyodbc.connect(MSSQL_CONNECTION)
    except Exception as e:
        print(f"  MSSQL 연결 실패: {e}")
        print("  연결 문자열을 확인하세요: MSSQL_CONNECTION")
        sys.exit(1)
    print("  연결 성공")

    mssql_tables = get_mssql_tables(mssql_conn)
    print(f"  테이블 수: {len(mssql_tables)}")
    print(f"  테이블: {', '.join(sorted(mssql_tables))}")

    # 기존 SQLite 파일 백업
    if os.path.exists(SQLITE_OUTPUT_PATH):
        backup_path = SQLITE_OUTPUT_PATH + f".bak.{datetime.now():%Y%m%d_%H%M%S}"
        print(f"\n[2] 기존 SQLite 파일 백업: {backup_path}")
        shutil.copy2(SQLITE_OUTPUT_PATH, backup_path)
        os.remove(SQLITE_OUTPUT_PATH)
    else:
        print(f"\n[2] 새 SQLite 파일 생성: {SQLITE_OUTPUT_PATH}")

    # SQLite 연결
    sqlite_conn = sqlite3.connect(SQLITE_OUTPUT_PATH)
    sqlite_conn.execute("PRAGMA journal_mode=WAL;")
    sqlite_conn.execute("PRAGMA foreign_keys=OFF;")  # 삽입 중 FK 검사 비활성화

    # 마이그레이션 대상 결정
    ordered_tables = [t for t in TABLE_ORDER if t in mssql_tables]
    extra_tables = sorted(mssql_tables - set(TABLE_ORDER))
    if extra_tables:
        print(f"\n  [참고] TABLE_ORDER에 없는 테이블 발견: {', '.join(extra_tables)}")
        ordered_tables.extend(extra_tables)

    # 스키마 생성
    print(f"\n[3] SQLite 스키마 생성 ({len(ordered_tables)}개 테이블)")
    print("-" * 60)
    table_columns: dict[str, list[dict]] = {}

    for table in ordered_tables:
        ddl = build_sqlite_create_table(mssql_conn, table)
        try:
            sqlite_conn.execute(ddl)
            table_columns[table] = get_mssql_columns(mssql_conn, table)
            print(f"  {table:30s}  OK")
        except Exception as e:
            print(f"  {table:30s}  [오류] {e}")
            ordered_tables = [t for t in ordered_tables if t != table]

    sqlite_conn.commit()

    # 데이터 이관
    print(f"\n[4] 데이터 마이그레이션")
    print("-" * 60)

    total_rows = 0
    for table in ordered_tables:
        src_count = get_mssql_row_count(mssql_conn, table)
        if src_count == 0:
            print(f"  {table:30s}  (빈 테이블 - 건너뜀)")
            continue

        try:
            migrated = migrate_table(
                mssql_conn, sqlite_conn, table, table_columns[table]
            )
            total_rows += migrated
            print(f"  {table:30s}  {migrated:>6d} rows")
        except Exception as e:
            print(f"  {table:30s}  [오류] {e}")

    # FK 활성화 & PRAGMA 설정
    sqlite_conn.execute("PRAGMA foreign_keys=ON;")
    sqlite_conn.commit()

    # 결과
    print(f"\n{'=' * 60}")
    print(f"마이그레이션 완료: {total_rows} rows ({len(ordered_tables)} tables)")
    print(f"출력 파일: {SQLITE_OUTPUT_PATH}")
    print(f"{'=' * 60}")

    sqlite_conn.close()
    mssql_conn.close()


if __name__ == "__main__":
    main()
