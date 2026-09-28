# -*- coding: utf-8 -*-
"""
DEXA 자산 트리의 중복 경로 해소 — 1회성 점검/패치 스크립트.

[배경]
구버전 TWM 에서 올라온 환경에는 레이아웃 그룹용으로 만들어진 동명 폴더가
DEXA asset 테이블에 남아 있다(예: /Groups 아래 'Group asset on 레이아웃' 33개).
DEXA 2.20 서버의 ImportAssets 핸들러가 전체 자산의 경로로 Dictionary 를 만들기 때문에,
중복 경로가 하나라도 있으면 거기서 죽는다:
    "An item with the same key has already been added."
그 결과 DEXA GUI 의 '자산 가져오기'와 Akka ImportAssets 가 통째로 막힌다.
신규 설치 환경에는 없는 문제다.

[조치]
삭제가 아니라 rename. 이름 뒤에 asset id 를 붙여 경로를 유일하게 만든다.
- 참조(예: TWMS 의 TwmsLayoutGroup)는 AssetId 기반이라 깨지지 않는다.
- 자식/스케줄/권한/백업이력과도 무관하다(이름만 바뀐다).
- DEXA 서버는 자산을 캐시하지 않고 매번 재조회하므로 재시작이 필요 없다.

[사용법]
    python script/fix_dup_folders.py                    # 미리보기(변경 없음)
    python script/fix_dup_folders.py --apply            # 실제 적용
    python script/fix_dup_folders.py --db <경로>        # DB 위치 지정
    python script/fix_dup_folders.py --apply --all      # 폴더뿐 아니라 실자산까지 대상

--apply 시 자동으로:
  1) DB 백업            DEXA.sqlite3.bak_dupfix_<타임스탬프>
  2) rename 적용
  3) 적용 후 중복 재검증 (0건이어야 정상)
  4) 롤백 SQL 생성      rollback_dupfix_<타임스탬프>.sql

기본 대상은 폴더(assetTypeId=2)뿐이다. 실자산 이름은 화면 표시·이력 검색에
쓰이므로 함부로 바꾸지 않는다(--all 로만 포함).
"""
import sqlite3, io, os, re, sys, shutil, datetime
from collections import Counter, defaultdict

DEFAULT_DB = r'C:\ProgramData\LS\DEXA\Storage\DEXA.sqlite3'

out = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')


def arg(name, default=None):
    if name in sys.argv:
        i = sys.argv.index(name)
        if i + 1 < len(sys.argv):
            return sys.argv[i + 1]
    return default


def asset_name(param):
    """asset.parameter 에서 이름을 뽑는다.

    현장에 형식이 4가지 섞여 있다:
        name { ... value = "X" }      원본(중첩)
        name : { ... value : "X" }    TWMS 가 저장한 것(HOCON 재작성)
        X.name.value = X              DEXA GUI 가 만든 폴더(한 줄)
        Folder1.name.type = String    두 줄짜리 폴더
        Folder1.name.value = 'X'
    구분자를 필수로 두면 원본을 놓쳐 이름이 빈 값이 되고 가짜 중복이 잡힌다.
    """
    p = param or ''
    m = re.search(r'name\s*[:=]?\s*\{[^}]*value\s*[:=]\s*["\']?([^"\'\n},]*)', p, re.S)
    if m:
        return m.group(1).strip()
    m = re.search(r'\.name\.value\s*[:=]\s*["\']?([^"\'\n]*)', p)
    return m.group(1).strip().rstrip('"\'') if m else ''


def load(con):
    return {r[0]: (r[1], r[2], r[3]) for r in con.execute(
        'select id, parentId, parameter, assetTypeId from asset where deleted=0')}


def path_of(rows, aid):
    """DEXA 의 Asset.GetPath 와 같은 규칙 — 루트는 빼고 '/' 로 시작."""
    parts, guard = [], 0
    while aid in rows and rows[aid][0] and guard < 64:
        parts.append(asset_name(rows[aid][1]))
        aid = rows[aid][0]
        guard += 1
    return '/' + '/'.join(reversed(parts))


def duplicates(rows):
    by_path = defaultdict(list)
    for aid in rows:
        by_path[path_of(rows, aid)].append(aid)
    return {p: ids for p, ids in by_path.items() if len(ids) > 1}


def main():
    db = arg('--db', DEFAULT_DB)
    apply_ = '--apply' in sys.argv
    include_assets = '--all' in sys.argv

    if not os.path.exists(db):
        print('DB 를 찾을 수 없습니다: %s' % db, file=out); out.flush(); sys.exit(1)

    con = sqlite3.connect(db, timeout=30)
    con.execute('PRAGMA busy_timeout=30000')
    rows = load(con)

    dups = duplicates(rows)
    print('DB: %s' % db, file=out)
    print('자산(삭제 제외): %d건 / 중복 경로: %d건' % (len(rows), len(dups)), file=out)
    for p, ids in list(dups.items())[:10]:
        print('   "%s" x%d  ids=%s%s' % (
            p, len(ids), ids[:5], ' …' if len(ids) > 5 else ''), file=out)
    if not dups:
        print('\n중복 없음 — 조치할 것이 없습니다.', file=out); out.flush(); return

    plan, rollback = [], ['-- 원복: DEXA 중복 경로 rename 되돌리기']
    skipped_assets = 0
    for p, ids in dups.items():
        for aid in ids:
            parent, param, type_id = rows[aid]
            if type_id != 2 and not include_assets:
                skipped_assets += 1
                continue
            name = asset_name(param)
            if not name:
                print('   건너뜀(이름 파싱 실패): id=%d' % aid, file=out); continue
            new_name = '%s (%d)' % (name, aid)
            # 이름이 나타나는 첫 자리만 바꾼다(값 위치). 따옴표 유무를 모두 처리.
            for old, new in (('"%s"' % name, '"%s"' % new_name),
                             ("'%s'" % name, "'%s'" % new_name),
                             ('= %s' % name, '= %s' % new_name)):
                if old in param:
                    plan.append((aid, param.replace(old, new, 1), name, new_name))
                    rollback.append("UPDATE asset SET parameter = '%s' WHERE id = %d;"
                                    % (param.replace("'", "''"), aid))
                    break
            else:
                print('   건너뜀(치환 위치 불명): id=%d' % aid, file=out)

    print('\nrename 예정: %d건%s' % (
        len(plan), (' (실자산 %d건은 제외 — --all 로 포함)' % skipped_assets) if skipped_assets else ''),
        file=out)
    for aid, _, old, new in plan[:5]:
        print('   id=%-5d "%s"  ->  "%s"' % (aid, old, new), file=out)
    if len(plan) > 5:
        print('   … 외 %d건' % (len(plan) - 5), file=out)

    if not apply_:
        print('\n[미리보기] 변경하지 않았습니다. 적용하려면 --apply 를 붙이세요.', file=out)
        out.flush(); return

    stamp = datetime.datetime.now().strftime('%Y%m%d_%H%M%S')
    backup = '%s.bak_dupfix_%s' % (db, stamp)
    shutil.copy2(db, backup)
    print('\n백업: %s' % backup, file=out)

    for aid, new_param, _, _ in plan:
        con.execute('update asset set parameter=? where id=?', (new_param, aid))
    con.commit()
    print('적용: %d건' % len(plan), file=out)

    after = duplicates(load(con))
    print('적용 후 중복 경로: %d건' % len(after), file=out)
    for p, ids in list(after.items())[:5]:
        print('   "%s" x%d' % (p, len(ids)), file=out)
    con.close()

    rb = os.path.join(os.path.dirname(db) or '.', 'rollback_dupfix_%s.sql' % stamp)
    io.open(rb, 'w', encoding='utf-8').write('\n'.join(rollback))
    print('롤백 SQL: %s' % rb, file=out)
    print('\n원복이 필요하면 롤백 SQL 을 실행하거나 백업 파일을 되돌리세요.', file=out)
    out.flush()


if __name__ == '__main__':
    main()
