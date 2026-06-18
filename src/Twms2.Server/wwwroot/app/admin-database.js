/* ============================================================================
 * DB 관리(DatabaseManagement) — DatabaseManagement.razor 를 정적 페이지로 이식.
 * GET  /api/admin/database          : 통계 + 마이그레이션 이력 + 레이아웃 목록 조회 (30초 폴링).
 * POST /api/admin/database/import    : DEXA.sqlite3 업로드 → 가져오기. 토큰 + 라인 프리뷰 반환.
 * POST /api/admin/database/positions : 토큰 + 라인↔레이아웃 매핑으로 배치 가져오기.
 * POST /api/admin/database/cancel    : 배치 단계 취소(임시파일 정리).
 * 관리자 전용(컨트롤러에서 Roles="Admin" 보호). 모든 가져오기는 파괴적 작업.
 * ==========================================================================*/
(function () {
  'use strict';

  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const $ = (id) => document.getElementById(id);

  let _layouts = [];        // [{id, name}]
  let _pendingFile = null;  // 선택된 DEXA.sqlite3
  let _importing = false;

  // 배치 단계 상태
  let _token = null;            // 1단계 import 가 반환한 임시파일 토큰
  let _linePreviews = [];       // [{id, name, selfW, selfH}]
  let _mappings = {};           // lineId -> layoutId(number) | null
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

  // ──────────────── 1단계: 가져오기 ────────────────
  function onFilePicked(e) {
    const f = e.target.files && e.target.files[0] ? e.target.files[0] : null;
    _pendingFile = f;
    $('import-file-name').textContent = f ? f.name : '선택된 파일 없음';
    // 새 파일 선택 시 이전 배치 단계 정리
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
    $('import-run-label').textContent = '가져오는 중...';
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
        renderImportResult(data.result || {});
        toast(`완료 — 확장정보 ${data.result?.augImported ?? 0}건, 연결정보 ${data.result?.connImported ?? 0}건 저장`);
        if (data.stats) renderStats(data.stats);
        if (data.layouts) _layouts = data.layouts;

        // 배치 매핑 단계 진입
        _token = data.token || null;
        _linePreviews = data.linePreviews || [];
        _mappings = {};
        if (_token && _linePreviews.length > 0) openBatch();
        else closeBatchCard();

        // 파일 입력 초기화 (Blazor 와 동일하게 선택 해제)
        _pendingFile = null;
        $('import-file-input').value = '';
        $('import-file-name').textContent = '선택된 파일 없음';
      }
    } catch (e) {
      renderImportError('가져오기 중 오류가 발생했습니다.');
      toast('가져오기 중 오류가 발생했습니다.');
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

  function renderImportResult(r) {
    const skipErr = (r.connSkipped ?? 0) + (r.errorCount ?? 0);
    const cells = [
      { v: r.totalRead ?? 0, l: '전체 자산', color: 'var(--c-on-surface)' },
      { v: r.augImported ?? 0, l: '확장정보 저장', color: 'var(--health-backedup)' },
      { v: r.connImported ?? 0, l: '연결정보 저장', color: 'var(--health-unchanged)' },
      { v: r.lineImported ?? 0, l: '라인 저장', color: 'var(--c-primary)' },
      { v: r.groupImported ?? 0, l: '그룹 저장', color: 'var(--c-secondary)' },
      { v: skipErr, l: '건너뜀/오류', color: skipErr > 0 ? 'var(--health-failed)' : 'var(--c-on-surface-variant)' },
    ];
    $('import-result-host').innerHTML = `<div class="db-divider"></div>
      <div class="dsp-grid" style="grid-template-columns:repeat(6,1fr);">
        ${cells.map(c => `<div class="db-mini">
          <span class="db-mini-value" style="color:${c.color};">${(c.v).toLocaleString()}</span>
          <span class="db-mini-label">${c.l}</span></div>`).join('')}
      </div>`;
  }

  // ──────────────── 2단계: 배치 매핑 ────────────────
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
        // 서버가 임시파일을 정리했으므로 토큰 만료
        _token = null;
        syncBatchBtn();
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

  async function cancelBatch(silent) {
    const token = _token;
    _token = null;
    _linePreviews = [];
    _mappings = {};
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
    if (!silent) toast('배치 가져오기를 닫았습니다.');
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

  function bind() {
    $('import-pick-btn').addEventListener('click', () => $('import-file-input').click());
    $('import-file-input').addEventListener('change', onFilePicked);
    $('import-run-btn').addEventListener('click', runImport);
    $('bulk-apply-btn').addEventListener('click', applyBulk);
    $('batch-run-btn').addEventListener('click', runPositionImport);
    $('batch-close-btn').addEventListener('click', () => cancelBatch(false));
  }

  document.addEventListener('DOMContentLoaded', async () => {
    if (window.Shell) await Shell.init({ active: 'admin' });
    bind();
    await load();
    setInterval(load, 30000);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) load(); });
  });
})();
