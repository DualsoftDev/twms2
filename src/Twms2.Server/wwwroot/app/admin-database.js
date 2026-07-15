/* ============================================================================
 * DB 관리(DatabaseManagement) — DatabaseManagement.razor 를 정적 페이지로 이식.
 * GET  /api/admin/database          : 통계 + 마이그레이션 이력 + 레이아웃 목록 조회 (30초 폴링).
 * POST /api/admin/database/import    : DEXA.sqlite3 업로드 → 분석(미리보기)만. 커밋 없음. 토큰+미리보기 반환.
 * POST /api/admin/database/apply-aug : 토큰으로 aug/연결/라인/그룹 실제 이전(전체 교체 커밋).
 * POST /api/admin/database/positions : 토큰 + 라인↔레이아웃 매핑으로 배치 가져오기.
 * POST /api/admin/database/cancel    : 닫기/취소(임시파일 정리).
 * 관리자 전용(컨트롤러에서 Roles="Admin" 보호). 모든 가져오기는 파괴적 작업.
 *
 * 흐름: [가져오기] 분석 → "무엇이 업데이트되는지" 미리보기 → [aug 데이터 이전하기 · 적용] 커밋 → [배치 가져오기].
 * ==========================================================================*/
(function () {
  'use strict';

  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const $ = (id) => document.getElementById(id);

  let _layouts = [];        // [{id, name}]
  let _pendingFile = null;  // 선택된 DEXA.sqlite3
  let _importing = false;

  // aug 이전(적용) 단계 상태
  let _token = null;        // 1단계 import 가 반환한 임시파일 토큰
  let _preview = null;      // {totalRead, augInsert, augUpdate, connInsert, connUpdate, connSkipped, line*, group*}
  let _augApplying = false;
  let _augApplied = false;

  // 배치 단계 상태
  let _linePreviews = [];   // [{id, name, selfW, selfH}]
  let _mappings = {};       // lineId -> layoutId(number) | null
  let _posImporting = false;

  // ──────────────── 조회 ────────────────
  async function load() {
    try {
      const res = await fetch('/api/admin/database', { headers: { 'Accept': 'application/json' } });
      if (!res.ok) return;
      const d = await res.json();
      _layouts = d.layouts || [];
      renderStats(d.stats || {});
      renderMigrations(d.migrations || []);
      if ($('batch-card').style.display !== 'none') renderBulkOptions();
    } catch (e) { /* 무시 */ }
  }

  function renderStats(s) {
    $('stat-version').textContent = 'V' + (s.schemaVersion ?? 0);
    $('stat-aug').textContent = (s.assetAugCount ?? 0).toLocaleString();
    $('stat-conn').textContent = (s.assetConnCount ?? 0).toLocaleString();
  }

  function renderMigrations(list) {
    const host = $('migrations-host');
    if (!list.length) {
      host.innerHTML = `<div class="db-alert"><span class="material-symbols-outlined">info</span>적용된 마이그레이션이 없습니다.</div>`;
      return;
    }
    host.innerHTML = `<table class="nm-table"><thead><tr>
      <th style="width:90px;">버전</th><th>설명</th><th style="width:200px;">적용일</th></tr></thead><tbody>
      ${list.map(m => `<tr>
        <td><span class="chip chip-info">V${m.version}</span></td>
        <td>${esc(m.description || '-')}</td>
        <td>${esc(fmtTime(m.appliedAt))}</td></tr>`).join('')}</tbody></table>`;
  }

  // ──────────────── 1단계: 가져오기(분석, 커밋 없음) ────────────────
  function onFilePicked(e) {
    const f = e.target.files && e.target.files[0] ? e.target.files[0] : null;
    _pendingFile = f;
    $('import-file-name').textContent = f ? f.name : '선택된 파일 없음';
    // 새 파일 선택 시 이전 분석/배치 단계 정리 (aug 카드 포함)
    if (_token) cancelBatch(true);
    $('import-result-host').innerHTML = '';
    syncImportBtn();
  }

  function syncImportBtn() {
    $('import-run-btn').disabled = _importing || !_pendingFile;
  }

  async function runImport() {
    if (!_pendingFile || _importing) return;
    if (_pendingFile.size > 500 * 1024 * 1024) { toast('파일 크기가 500MB를 초과합니다.'); return; }

    _importing = true;
    $('import-run-label').textContent = '분석 중...';
    syncImportBtn();
    $('import-result-host').innerHTML = '';

    const fd = new FormData();
    fd.append('file', _pendingFile);
    try {
      const res = await fetch('/api/admin/database/import', { method: 'POST', body: fd });
      const data = await res.json().catch(() => ({}));

      if (!res.ok) {
        renderImportError(data.error || '가져오기 요청이 거부되었습니다.');
        toast(data.error || '가져오기 실패');
      } else if (data.ok === false) {
        renderImportError('파일 읽기 오류: ' + (data.fatalError || '알 수 없는 오류'));
        toast('가져오기 실패: ' + (data.fatalError || ''));
      } else {
        // 분석 결과 → aug 이전 단계 진입 (아직 커밋 안 됨)
        _token = data.token || null;
        _preview = data.preview || {};
        _linePreviews = data.linePreviews || [];
        _layouts = data.layouts || _layouts;
        _mappings = {};
        _augApplied = false;

        openAfterImport();
        toast(`분석 완료 — 자산 ${(_preview.totalRead ?? 0).toLocaleString()}건. '적용'을 눌러 반영하세요.`);

        // 파일 입력 초기화 (다음 선택을 위해)
        _pendingFile = null;
        $('import-file-input').value = '';
        $('import-file-name').textContent = '선택된 파일 없음';
      }
    } catch (e) {
      console.error('[db-import]', e); // 네트워크 오류 외에 렌더링 단계 JS 예외도 여기로 떨어진다
      // fetch 자체가 실패한 경우: 네트워크 문제 또는 브라우저가 파일을 읽지 못한 경우.
      // 운영 중인 DEXA.sqlite3(다른 프로세스가 계속 쓰는 파일)를 직접 선택하면
      // 선택~업로드 사이에 파일이 변경되어 브라우저가 전송을 거부한다(ERR_UPLOAD_FILE_CHANGED).
      let msg = '가져오기 중 오류가 발생했습니다. (네트워크/서버 연결 확인)';
      try {
        if (_pendingFile) await _pendingFile.slice(0, 1).arrayBuffer();
      } catch (readErr) {
        msg = '선택한 파일이 변경되었거나 다른 프로그램이 사용 중이라 읽을 수 없습니다. '
            + '운영 중인 DEXA.sqlite3는 파일을 복사한 뒤 복사본을 선택해 업로드하세요.';
      }
      renderImportError(msg);
      toast(msg);
    } finally {
      _importing = false;
      $('import-run-label').textContent = '가져오기';
      syncImportBtn();
    }
  }

  function renderImportError(msg) {
    $('import-result-host').innerHTML = `<div class="db-divider"></div>
      <div class="db-alert db-alert-error"><span class="material-symbols-outlined">error</span>${esc(msg)}</div>`;
  }

  // ──────────────── 2단계: aug 데이터 이전하기(적용) ────────────────
  function openAfterImport() {
    // aug 이전 카드
    $('aug-card').style.display = '';
    $('aug-result-host').innerHTML = '';
    $('aug-apply-label').textContent = '적용';
    renderAugPreview(_preview);
    syncAugBtn();

    // 배치 카드 (라인 정보가 있을 때만)
    if (_token && _linePreviews.length > 0) openBatch();
    else closeBatchCard();
  }

  function renderAugPreview(p) {
    p = p || {};
    const cells = [
      { v: p.totalRead ?? 0,   l: '읽은 자산',       color: 'var(--c-on-surface)' },
      { v: p.augInsert ?? 0,   l: '확장정보 신규',    color: 'var(--health-backedup)' },
      { v: p.augUpdate ?? 0,   l: '확장정보 덮어씀',  color: 'var(--health-failed)' },
      { v: p.connInsert ?? 0,  l: '연결정보 신규',    color: 'var(--health-unchanged)' },
      { v: p.connUpdate ?? 0,  l: '연결정보 덮어씀',  color: 'var(--health-failed)' },
      { v: p.connSkipped ?? 0, l: '연결 건너뜀',      color: 'var(--c-on-surface-variant)' },
    ];
    // 라인/그룹은 파일에 행이 있을 때만 전체 교체된다(파일이 0개면 기존 유지).
    const lineReplaced  = (p.lineFile ?? 0) > 0;
    const groupReplaced = (p.groupFile ?? 0) > 0;
    const lineAfter  = lineReplaced  ? (p.lineFile ?? 0)  : (p.lineCurrent ?? 0);
    const groupAfter = groupReplaced ? (p.groupFile ?? 0) : (p.groupCurrent ?? 0);
    const keep = '<span style="color:var(--c-on-surface-variant);font-size:12px;">(유지)</span>';
    const swap = '<span class="material-symbols-outlined" style="font-size:14px;vertical-align:-2px;color:var(--health-failed);">swap_horiz</span>';
    const warn = (p.augUpdate ?? 0) > 0 || (p.connUpdate ?? 0) > 0
      || (lineReplaced && (p.lineCurrent ?? 0) > 0)
      || (groupReplaced && (p.groupCurrent ?? 0) > 0);

    $('aug-preview-host').innerHTML = `
      <div class="dsp-grid" style="grid-template-columns:repeat(6,1fr);">
        ${cells.map(c => `<div class="db-mini">
          <span class="db-mini-value" style="color:${c.color};">${(c.v).toLocaleString()}</span>
          <span class="db-mini-label">${c.l}</span></div>`).join('')}
      </div>
      <div class="db-divider"></div>
      <table class="nm-table"><thead><tr>
        <th>전체 교체 대상</th><th style="width:120px;">현재</th><th style="width:140px;">교체 후</th></tr></thead><tbody>
        <tr><td>라인 (TwmsLayoutLine)</td><td>${(p.lineCurrent ?? 0).toLocaleString()}</td>
            <td>${lineAfter.toLocaleString()} ${lineReplaced ? swap : keep}</td></tr>
        <tr><td>그룹 (TwmsLayoutGroup)</td><td>${(p.groupCurrent ?? 0).toLocaleString()}</td>
            <td>${groupAfter.toLocaleString()} ${groupReplaced ? swap : keep}</td></tr>
      </tbody></table>
      ${warn ? `<div class="db-alert db-alert-error" style="margin-top:14px;">
        <span class="material-symbols-outlined">warning</span>
        기존 데이터가 덮어쓰기/전체 교체됩니다. 적용 후에는 그 사이 수동 편집분이 보존되지 않습니다.</div>` : ''}`;
  }

  function syncAugBtn() {
    $('aug-apply-btn').disabled = _augApplying || _augApplied || !_token;
  }

  async function runApplyAug() {
    if (_augApplying || _augApplied || !_token) return;
    _augApplying = true;
    $('aug-apply-label').textContent = '적용 중...';
    syncAugBtn();
    $('aug-result-host').innerHTML = '';

    try {
      const res = await fetch('/api/admin/database/apply-aug', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
        body: JSON.stringify({ token: _token }),
      });
      const data = await res.json().catch(() => ({}));

      if (!res.ok) {
        renderAugResult(false, data.error || '적용 요청이 거부되었습니다.');
        toast(data.error || '적용 실패');
      } else if (data.ok === false) {
        renderAugResult(false, '오류: ' + (data.fatalError || '알 수 없는 오류'));
        toast('적용 오류: ' + (data.fatalError || ''));
      } else {
        const r = data.result || {};
        renderAugResult(true,
          `확장정보 ${r.augImported ?? 0}건, 연결정보 ${r.connImported ?? 0}건, 라인 ${r.lineImported ?? 0}건, 그룹 ${r.groupImported ?? 0}건 적용 완료`);
        toast(`적용 완료 — 확장정보 ${r.augImported ?? 0}건, 연결정보 ${r.connImported ?? 0}건`);
        if (data.stats) renderStats(data.stats);
        _augApplied = true;
      }
    } catch (e) {
      renderAugResult(false, '적용 중 오류가 발생했습니다.');
      toast('적용 중 오류가 발생했습니다.');
    } finally {
      _augApplying = false;
      $('aug-apply-label').textContent = _augApplied ? '적용됨' : '적용';
      syncAugBtn();
    }
  }

  function renderAugResult(success, msg) {
    $('aug-result-host').innerHTML = `<div class="db-divider"></div>
      <div class="db-alert ${success ? 'db-alert-success' : 'db-alert-error'}">
        <span class="material-symbols-outlined">${success ? 'check_circle' : 'error'}</span>${esc(msg)}</div>`;
  }

  // ──────────────── 3단계: 배치 매핑 ────────────────
  function openBatch() {
    $('batch-card').style.display = '';
    $('batch-result-host').innerHTML = '';
    renderBulkOptions();
    renderBatchTable();
    syncBatchBtn();
  }

  function closeBatchCard() {
    $('batch-card').style.display = 'none';
  }

  function renderBulkOptions() {
    const sel = $('bulk-layout');
    const cur = sel.value;
    sel.innerHTML = `<option value="">-- 일괄 선택 / 건너뛰기 --</option>` +
      _layouts.map(l => `<option value="${l.id}">${esc(l.name)}</option>`).join('');
    if (cur && _layouts.some(l => String(l.id) === cur)) sel.value = cur;
  }

  function renderBatchTable() {
    const host = $('batch-table-host');
    if (!_linePreviews.length) {
      host.innerHTML = `<div class="db-alert"><span class="material-symbols-outlined">info</span>가져온 파일에 라인 정보가 없습니다.</div>`;
      return;
    }
    const opts = (selectedId) =>
      `<option value="">선택 안 함</option>` +
      _layouts.map(l => `<option value="${l.id}"${selectedId === l.id ? ' selected' : ''}>${esc(l.name)}</option>`).join('');

    host.innerHTML = `<table class="nm-table"><thead><tr>
      <th>TWM1 라인</th><th style="width:160px;">이미지 크기</th><th style="min-width:240px;">TWM2 레이아웃</th>
      </tr></thead><tbody>
      ${_linePreviews.map(lp => `<tr>
        <td>${esc(lp.name)}</td>
        <td>${fmtNum(lp.selfW)} × ${fmtNum(lp.selfH)}</td>
        <td><select class="db-select" data-line="${lp.id}">${opts(_mappings[lp.id] ?? null)}</select></td>
      </tr>`).join('')}</tbody></table>`;

    host.querySelectorAll('select[data-line]').forEach(s => s.addEventListener('change', () => {
      const lineId = +s.getAttribute('data-line');
      _mappings[lineId] = s.value ? +s.value : null;
      syncBatchBtn();
    }));
  }

  function applyBulk() {
    const v = $('bulk-layout').value;
    const layoutId = v ? +v : null;
    _linePreviews.forEach(lp => { _mappings[lp.id] = layoutId; });
    renderBatchTable();
    syncBatchBtn();
  }

  function hasAnyMapping() {
    return Object.values(_mappings).some(v => v != null);
  }

  function syncBatchBtn() {
    $('batch-run-btn').disabled = _posImporting || !_token || !hasAnyMapping();
  }

  async function runPositionImport() {
    if (_posImporting || !_token || !hasAnyMapping()) return;
    _posImporting = true;
    $('batch-run-label').textContent = '배치 중...';
    syncBatchBtn();
    $('batch-result-host').innerHTML = '';

    const mappings = Object.entries(_mappings)
      .filter(([, layoutId]) => layoutId != null)
      .map(([lineId, layoutId]) => ({ lineId: +lineId, layoutId }));

    try {
      const res = await fetch('/api/admin/database/positions', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
        body: JSON.stringify({ token: _token, mappings }),
      });
      const data = await res.json().catch(() => ({}));

      if (!res.ok) {
        renderBatchResult(false, data.error || '배치 요청이 거부되었습니다.');
        toast(data.error || '배치 실패');
      } else if (data.ok === false) {
        renderBatchResult(false, '오류: ' + (data.fatalError || '알 수 없는 오류'));
        toast('배치 오류: ' + (data.fatalError || ''));
      } else {
        const r = data.result || {};
        let msg = `자산 ${r.assetPositionsImported ?? 0}건, 그룹 ${r.groupsImported ?? 0}건 배치 완료`;
        if ((r.skipped ?? 0) > 0) msg += ` (건너뜀 ${r.skipped}건)`;
        renderBatchResult(true, msg);
        toast(`배치 완료 — 자산 ${r.assetPositionsImported ?? 0}건, 그룹 ${r.groupsImported ?? 0}건`);
        // 임시파일은 서버가 유지(닫기 시 정리) — 토큰 유지하여 재배치/추가 적용 가능
        await load();
      }
    } catch (e) {
      renderBatchResult(false, '배치 중 오류가 발생했습니다.');
      toast('배치 중 오류가 발생했습니다.');
    } finally {
      _posImporting = false;
      $('batch-run-label').textContent = '배치 가져오기';
      syncBatchBtn();
    }
  }

  function renderBatchResult(success, msg) {
    $('batch-result-host').innerHTML = `<div class="db-divider"></div>
      <div class="db-alert ${success ? 'db-alert-success' : 'db-alert-error'}">
        <span class="material-symbols-outlined">${success ? 'check_circle' : 'error'}</span>${esc(msg)}</div>`;
  }

  // 분석/배치 단계 전체 닫기 + 임시파일 정리
  async function cancelBatch(silent) {
    const token = _token;
    _token = null;
    _preview = null;
    _augApplied = false;
    _linePreviews = [];
    _mappings = {};
    $('aug-card').style.display = 'none';
    $('aug-result-host').innerHTML = '';
    closeBatchCard();
    if (token) {
      try {
        await fetch('/api/admin/database/cancel', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
          body: JSON.stringify({ token }),
        });
      } catch (e) { /* 무시 */ }
    }
    if (!silent) toast('가져오기 단계를 닫았습니다.');
  }

  // ──────────────── 헬퍼 ────────────────
  function toast(msg) { if (window.Shell && Shell.toast) Shell.toast(msg); }

  function fmtNum(n) {
    if (n == null || isNaN(n)) return '0';
    return Math.round(Number(n)).toLocaleString();
  }

  function fmtTime(s) {
    if (!s) return '';
    const d = new Date(s); if (isNaN(d)) return s;
    const p = (n) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
  }

  // 요소가 없어도(캐시된 구버전 HTML 등) 전체 스크립트가 중단되지 않도록 방어적 바인딩
  function on(id, evt, fn) {
    const el = $(id);
    if (el) el.addEventListener(evt, fn);
  }

  function bind() {
    on('import-pick-btn', 'click', () => { const i = $('import-file-input'); if (i) i.click(); });
    on('import-file-input', 'change', onFilePicked);
    on('import-run-btn', 'click', runImport);
    on('aug-apply-btn', 'click', runApplyAug);
    on('aug-close-btn', 'click', () => cancelBatch(false));
    on('bulk-apply-btn', 'click', applyBulk);
    on('batch-run-btn', 'click', runPositionImport);
  }

  // settings.html 동거 시 자기 패널(database)이 활성일 때만 폴링 — 독립 페이지에는 패널이 없어 항상 true
  function panelActive() {
    const p = document.querySelector('.set-panel[data-panel="database"]');
    return !p || p.classList.contains('active');
  }

  document.addEventListener('DOMContentLoaded', async () => {
    if (window.Shell) await Shell.init({ active: 'admin' });
    bind();
    await load();
    setInterval(() => { if (!document.hidden && panelActive()) load(); }, 30000);
    document.addEventListener('visibilitychange', () => { if (!document.hidden && panelActive()) load(); });
    document.addEventListener('twms:panel-shown', (e) => { if (e.detail === 'database') load(); });
  });
})();
