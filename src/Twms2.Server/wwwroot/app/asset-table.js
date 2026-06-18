/* ============================================================================
 * 자산 테이블 편집 — AssetTableEditor.razor + AssetEditGrid.razor +
 *   AssetBatchEditDialog.razor 를 정적 페이지로 이식.
 * GET  /api/assets/table        편집 가능 행 + 유형별 컬럼 메타
 * POST /api/assets/table        인라인 편집 일괄 저장
 * POST /api/assets/table/batch  배치 편집(체크 필드만 일괄 적용)
 * 클라이언트 인라인 편집/변경추적/정렬/검색/페이징 + CSV(Blob).
 * 실시간 아님 — 저장/배치 후 재로드. (원본은 폴링 없음 → 폴링 없음)
 * ==========================================================================*/
(function () {
  'use strict';

  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const $ = (id) => document.getElementById(id);
  const ICON_BASE = '/images/icons/';

  // 유형 메타 (서버 /api/assets/table 의 types 로 대체되지만 기본값 보유)
  let TYPES = [];
  // 전체 탭 + 각 유형 탭 구성. id=0 → 전체
  function tabList() {
    return [{ id: 0, name: '전체', icon: null, hasVia: true, hasVersion: true, hasRobot: true }].concat(TYPES);
  }

  const S = {
    rows: [],            // 서버 원본(스냅샷) — 변경 비교 기준
    edits: {},           // assetId → { field: value }  (변경된 필드만)
    lineOptions: [],
    activeType: 0,       // 0 = 전체
    search: '',
    sort: { key: 'assetId', dir: 1 },
    page: 0, pageSize: 25,
    saving: false,
  };

  /* ── 유형별 컬럼 가시성 (AssetEditGrid 규칙) ── */
  function typeMeta(typeId) { return TYPES.find(t => t.id === typeId) || null; }
  function rowHasVia(typeId) { const m = typeMeta(typeId); return m ? m.hasVia : false; }
  function rowHasVersion(typeId) { const m = typeMeta(typeId); return m ? m.hasVersion : false; }
  function rowHasRobot(typeId) { const m = typeMeta(typeId); return m ? m.hasRobot : false; }
  function typeIcon(typeId) { const m = typeMeta(typeId); return m ? m.icon : null; }
  function typeName(typeId) { const m = typeMeta(typeId); return m ? m.name : ''; }

  /* ── 컬럼 정의 (현재 탭에 따라) ─────────────────────────────
   * 전체(0): 모든 컬럼 표시, 행별 비해당 셀은 — 로 표시.
   * 특정 유형: 해당 유형 규칙대로 컬럼 노출/숨김. */
  function columnsFor(activeType) {
    const all = activeType === 0;
    const cols = [
      { key: '_sel', title: '', cls: 'col-sel', sortable: false },
      { key: 'assetId', title: 'ID', cls: 'col-id' },
    ];
    if (all) cols.push({ key: 'typeName', title: '유형', cls: 'col-type' });
    cols.push({ key: 'name', title: '이름', edit: 'text' });
    cols.push({ key: 'lineName', title: '라인', edit: 'line' });
    cols.push({ key: 'vendor', title: '벤더', edit: 'text' });
    cols.push({ key: 'spec', title: '사양', edit: 'text' });

    const showVia = all || rowHasVia(activeType);
    if (showVia) cols.push({ key: 'connIpVia', title: '경유 IP', edit: 'via' });
    cols.push({ key: 'displayIp', title: 'IP', edit: 'text' });
    if (showVia) {
      cols.push({ key: 'connBase', title: 'Base', edit: 'numVia', cls: 'num' });
      cols.push({ key: 'connSlot', title: 'Slot', edit: 'numVia', cls: 'num' });
    }
    cols.push({ key: 'stationNumber', title: 'Station', edit: 'numInt', cls: 'num' });

    const showVer = all || rowHasVersion(activeType);
    if (showVer) {
      cols.push({ key: 'modelName', title: '모델명', edit: 'ver' });
      cols.push({ key: 'modelVersion', title: '버전', edit: 'ver' });
    }
    const showRobot = all || rowHasRobot(activeType);
    if (showRobot) cols.push({ key: 'connIsRobot', title: '로봇', edit: 'robot', sortable: false, cls: 'num' });
    return cols;
  }

  /* ── 현재 값 (edits 우선, 없으면 원본) ── */
  function cur(row, key) {
    const e = S.edits[row.assetId];
    if (e && Object.prototype.hasOwnProperty.call(e, key)) return e[key];
    return row[key];
  }
  function isRowModified(row) {
    const e = S.edits[row.assetId];
    if (!e) return false;
    return Object.keys(e).some(k => !valEq(e[k], row[k]));
  }
  function valEq(a, b) {
    if (a == null && b == null) return true;
    if (a === '' && b == null) return false; // 명시적 빈문자열은 변경으로 취급
    return a === b;
  }
  function modifiedRows() { return S.rows.filter(isRowModified); }

  /* ── 변경 기록 ── */
  function setEdit(assetId, key, value) {
    const row = S.rows.find(r => r.assetId === assetId);
    if (!row) return;
    let e = S.edits[assetId];
    if (!e) e = S.edits[assetId] = {};
    e[key] = value;
    // 원본과 같아지면 해당 키 제거
    if (valEq(value, row[key])) {
      delete e[key];
      if (Object.keys(e).length === 0) delete S.edits[assetId];
    }
    updateSummary();
    renderTabs();
    // 행 강조만 토글 (재렌더 없이 입력 포커스 유지)
    const tr = $('t-table').querySelector(`tr[data-aid="${assetId}"]`);
    if (tr) tr.classList.toggle('row-mod', isRowModified(row));
  }

  /* ── 데이터 로드 ── */
  async function load() {
    try {
      const res = await fetch('/api/assets/table', { headers: { 'Accept': 'application/json' } });
      if (!res.ok) { showAlert('err', 'error', '자산 목록을 가져올 수 없습니다. DEXA 서버 연결을 확인하세요.'); return; }
      const d = await res.json();
      S.rows = d.rows || [];
      S.lineOptions = d.lineOptions || [];
      TYPES = d.types || [];
      S.edits = {};
      renderTabs();
      render();
      updateSummary();
    } catch (e) {
      showAlert('err', 'error', '로드 실패: ' + e.message);
    }
  }

  /* ── 탭 ── */
  function rowsOfTab(typeId) {
    return typeId === 0 ? S.rows : S.rows.filter(r => r.assetTypeId === typeId);
  }
  function tabModCount(typeId) {
    return rowsOfTab(typeId).filter(isRowModified).length;
  }
  function renderTabs() {
    const host = $('t-tabs');
    host.innerHTML = tabList().map(t => {
      const rows = rowsOfTab(t.id);
      const mod = tabModCount(t.id);
      const iconHtml = t.icon
        ? `<img src="${ICON_BASE}${esc(t.icon)}.png" alt="" />`
        : `<span class="material-symbols-outlined">dataset</span>`;
      const modDot = mod > 0 ? `<span class="tab-mod" title="${mod}건 수정"></span>` : '';
      return `<button class="hist-tab${t.id === S.activeType ? ' active' : ''}" data-type="${t.id}">
        ${iconHtml}${esc(t.name)} (${rows.length})${modDot}</button>`;
    }).join('');
    host.querySelectorAll('.hist-tab').forEach(b =>
      b.addEventListener('click', () => { S.activeType = +b.getAttribute('data-type'); S.page = 0; renderTabs(); render(); }));
  }

  /* ── 검색/정렬 ── */
  function filtered() {
    let rows = rowsOfTab(S.activeType);
    const term = (S.search || '').trim().toLowerCase();
    if (term) {
      rows = rows.filter(r => [cur(r, 'name'), cur(r, 'displayIp'), cur(r, 'vendor'), cur(r, 'spec'), r.lineName]
        .some(v => v != null && String(v).toLowerCase().includes(term)));
    }
    return rows;
  }
  function sortRows(rows) {
    const { key, dir } = S.sort;
    return rows.slice().sort((a, b) => {
      let x = cur(a, key), y = cur(b, key);
      if (x == null && y == null) return 0;
      if (x == null) return 1; if (y == null) return -1;
      if (typeof x === 'number' && typeof y === 'number') return (x - y) * dir;
      return String(x).localeCompare(String(y), 'ko') * dir;
    });
  }

  /* ── 셀 렌더 ── */
  function lineSelectHtml(row) {
    const sel = cur(row, 'lineId');
    const opts = ['<option value="">-</option>'].concat(
      S.lineOptions.map(o => `<option value="${o.id}"${o.id === sel ? ' selected' : ''}>${esc(o.name)}</option>`));
    return `<select class="edit-in" data-edit="line">${opts.join('')}</select>`;
  }
  function naCell() { return `<td class="cell-na-bg"><span class="cell-na">—</span></td>`; }

  function cellHtml(row, col) {
    const t = row.assetTypeId;
    const v = cur(row, col.key);
    switch (col.edit) {
      case 'text':
        return `<td><input class="edit-in" type="text" data-edit="${col.key}" value="${esc(v ?? '')}" placeholder="-" /></td>`;
      case 'line':
        return `<td>${lineSelectHtml(row)}</td>`;
      case 'via':
        if (S.activeType === 0 && !rowHasVia(t)) return naCell();
        return `<td><input class="edit-in" type="text" data-edit="connIpVia" value="${esc(v ?? '')}" placeholder="-" /></td>`;
      case 'numVia':
        if (S.activeType === 0 && !rowHasVia(t)) return naCell();
        return `<td><input class="edit-in num" type="number" data-edit="${col.key}" value="${v == null ? '' : v}" placeholder="-" /></td>`;
      case 'numInt':
        return `<td><input class="edit-in num" type="number" data-edit="${col.key}" value="${v == null ? '' : v}" placeholder="-" /></td>`;
      case 'ver':
        if (S.activeType === 0 && !rowHasVersion(t)) return naCell();
        return `<td><input class="edit-in" type="text" data-edit="${col.key}" value="${esc(v ?? '')}" placeholder="-" /></td>`;
      case 'robot':
        if (S.activeType === 0 && !rowHasRobot(t)) return naCell();
        return `<td><input class="robot-check" type="checkbox" data-edit="connIsRobot"${v === 1 ? ' checked' : ''} /></td>`;
      default:
        // 비편집 컬럼
        if (col.key === '_sel') return `<td class="col-sel"><input class="robot-check sel-check" type="checkbox" /></td>`;
        if (col.key === 'assetId') return `<td class="col-id">${v}</td>`;
        if (col.key === 'typeName') {
          const ic = typeIcon(t);
          const img = ic ? `<img src="${ICON_BASE}${esc(ic)}.png" alt="" />` : '';
          return `<td class="col-type"><span class="type-cell">${img}${esc(v ?? '')}</span></td>`;
        }
        return `<td>${esc(v ?? '')}</td>`;
    }
  }

  /* ── 테이블 렌더 ── */
  function render() {
    const cols = columnsFor(S.activeType);
    const rows = sortRows(filtered());
    const pages = Math.max(1, Math.ceil(rows.length / S.pageSize));
    if (S.page >= pages) S.page = 0;
    const page = rows.slice(S.page * S.pageSize, (S.page + 1) * S.pageSize);

    $('t-count').textContent = `${rows.length} 건 (수정 ${modifiedRows().length}건)`;

    const thead = '<thead><tr>' + cols.map(c => {
      const sortable = c.sortable !== false && c.key !== '_sel';
      const arrow = sortable && c.key === S.sort.key
        ? `<span class="sort-arrow material-symbols-outlined">${S.sort.dir === 1 ? 'arrow_drop_up' : 'arrow_drop_down'}</span>` : '';
      return `<th class="${c.cls || ''}" ${sortable ? `data-key="${c.key}"` : ''}>${esc(c.title)}${arrow}</th>`;
    }).join('') + '</tr></thead>';

    const body = page.map(row => {
      const mod = isRowModified(row) ? ' row-mod' : '';
      const tds = cols.map(c => cellHtml(row, c)).join('');
      return `<tr class="${mod.trim()}" data-aid="${row.assetId}">${tds}</tr>`;
    }).join('');

    $('t-table').innerHTML = thead +
      (page.length ? `<tbody>${body}</tbody>`
        : `<tbody><tr><td colspan="${cols.length}"><div class="hist-empty">표시할 자산이 없습니다.</div></td></tr></tbody>`);

    bindCellEvents();
    bindSort();
    renderPager(pages, rows.length);
  }

  function bindCellEvents() {
    const table = $('t-table');
    table.querySelectorAll('tr[data-aid]').forEach(tr => {
      const aid = +tr.getAttribute('data-aid');
      tr.querySelectorAll('[data-edit]').forEach(el => {
        const field = el.getAttribute('data-edit');
        if (el.type === 'checkbox') {
          el.addEventListener('change', () => setEdit(aid, 'connIsRobot', el.checked ? 1 : null));
        } else if (el.tagName === 'SELECT') {
          el.addEventListener('change', () => {
            const raw = el.value;
            const lineId = raw === '' ? null : parseInt(raw, 10);
            const opt = S.lineOptions.find(o => o.id === lineId);
            // lineName 도 함께 갱신(검색/표시 일관)
            let e = S.edits[aid] || (S.edits[aid] = {});
            e.lineName = opt ? opt.name : '';
            setEdit(aid, 'lineId', lineId);
          });
        } else if (el.classList.contains('num')) {
          el.addEventListener('input', () => {
            const raw = el.value.trim();
            const num = raw === '' ? null : parseInt(raw, 10);
            setEdit(aid, field, Number.isNaN(num) ? null : num);
          });
        } else {
          el.addEventListener('input', () => setEdit(aid, field, el.value));
        }
      });
    });
  }

  function bindSort() {
    $('t-table').querySelectorAll('th[data-key]').forEach(th => {
      th.addEventListener('click', () => {
        const key = th.getAttribute('data-key');
        if (S.sort.key === key) S.sort.dir = -S.sort.dir;
        else { S.sort.key = key; S.sort.dir = 1; }
        render();
      });
    });
  }

  function renderPager(pages, total) {
    const host = $('t-pager');
    if (total === 0) { host.innerHTML = ''; return; }
    host.innerHTML = `
      <button class="hist-iconbtn" ${S.page <= 0 ? 'disabled' : ''} data-act="prev"><span class="material-symbols-outlined">chevron_left</span></button>
      <span>${S.page + 1} / ${pages} <span style="opacity:0.6;">(${total}건)</span></span>
      <button class="hist-iconbtn" ${S.page >= pages - 1 ? 'disabled' : ''} data-act="next"><span class="material-symbols-outlined">chevron_right</span></button>`;
    const prev = host.querySelector('[data-act="prev"]'); const next = host.querySelector('[data-act="next"]');
    if (prev) prev.addEventListener('click', () => { if (S.page > 0) { S.page--; render(); } });
    if (next) next.addEventListener('click', () => { if (S.page < pages - 1) { S.page++; render(); } });
  }

  function updateSummary() {
    const total = S.rows.length;
    const mod = modifiedRows().length;
    $('t-summary').textContent = `총 ${total}건 / 수정 ${mod}건`;
    $('t-save').disabled = mod === 0 || S.saving;
  }

  /* ── 알림 ── */
  let alertTimer = null;
  function showAlert(kind, icon, msg, autohide) {
    const el = $('t-alert');
    el.className = 'hist-alert ' + kind;
    el.style.display = 'flex';
    $('t-alert-icon').textContent = icon;
    $('t-alert-msg').textContent = msg;
    if (alertTimer) clearTimeout(alertTimer);
    if (autohide) alertTimer = setTimeout(() => { el.style.display = 'none'; }, 5000);
  }
  function hideAlert() { $('t-alert').style.display = 'none'; }
  function setProgress(pct) {
    const bar = $('t-progress');
    if (pct == null) { bar.style.display = 'none'; return; }
    bar.style.display = 'block';
    bar.firstElementChild.style.width = pct + '%';
  }

  /* ── 저장 페이로드 빌드 (변경된 필드만, nullable 은 *Set 플래그) ── */
  function buildSavePayload() {
    const rows = [];
    for (const row of modifiedRows()) {
      const e = S.edits[row.assetId];
      const payload = { assetId: row.assetId };
      const has = (k) => e && Object.prototype.hasOwnProperty.call(e, k) && !valEq(e[k], row[k]);
      // 문자열 필드: null = 미전송 (그대로 둠)
      if (has('name')) payload.name = e.name ?? '';
      if (has('description')) payload.description = e.description ?? '';
      if (has('agent')) payload.agent = e.agent ?? '';
      if (has('vendor')) payload.vendor = e.vendor ?? '';
      if (has('spec')) payload.spec = e.spec ?? '';
      if (has('modelName')) payload.modelName = e.modelName ?? '';
      if (has('modelVersion')) payload.modelVersion = e.modelVersion ?? '';
      if (has('displayIp')) payload.displayIp = e.displayIp ?? '';
      if (has('connIpVia')) payload.connIpVia = e.connIpVia ?? '';
      // 정수 필드
      if (has('connBase')) payload.connBase = e.connBase == null ? 0 : e.connBase;
      if (has('connSlot')) { payload.connSlotSet = true; payload.connSlot = e.connSlot; }
      if (has('connIsRobot')) { payload.connIsRobotSet = true; payload.connIsRobot = e.connIsRobot; }
      if (has('stationNumber')) { payload.stationNumberSet = true; payload.stationNumber = e.stationNumber; }
      if (has('lineId')) { payload.lineIdSet = true; payload.lineId = e.lineId; }
      rows.push(payload);
    }
    return rows;
  }

  async function save() {
    const rows = buildSavePayload();
    if (rows.length === 0) return;
    S.saving = true; updateSummary();
    setProgress(40); hideAlert();
    try {
      const res = await fetch('/api/assets/table', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ rows }),
      });
      setProgress(80);
      if (!res.ok) { showAlert('err', 'error', '저장 실패 (' + res.status + ')'); return; }
      const d = await res.json();
      if (d.fail === 0) showAlert('ok', 'check_circle', `${d.success}건 저장 완료`, true);
      else showAlert('warn', 'warning', `성공 ${d.success}건, 실패 ${d.fail}건`);
      setProgress(100);
      await load();
    } catch (e) {
      showAlert('err', 'error', '저장 오류: ' + e.message);
    } finally {
      S.saving = false; setProgress(null); updateSummary();
    }
  }

  /* ── CSV 내보내기 (현재 탭, 필터 반영, Blob) ── */
  function csvEscape(v) { const s = String(v == null ? '' : v); return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s; }
  function exportCsv() {
    const rows = sortRows(filtered());
    if (rows.length === 0) { if (window.Shell) Shell.toast('내보낼 데이터가 없습니다.'); return; }
    const headers = ['AssetId', 'AssetTypeId', 'TypeName', 'Name', 'LineName', 'StationNumber', 'Vendor', 'Spec',
      'DisplayIp', 'ConnIpVia', 'ConnBase', 'ConnSlot', 'ConnIsRobot', 'Description', 'Agent', 'ModelName', 'ModelVersion'];
    const lines = [headers.join(',')];
    for (const r of rows) {
      const cols = [r.assetId, r.assetTypeId, typeName(r.assetTypeId),
        cur(r, 'name'), cur(r, 'lineName'), cur(r, 'stationNumber') ?? '', cur(r, 'vendor') ?? '', cur(r, 'spec') ?? '',
        cur(r, 'displayIp'), cur(r, 'connIpVia') ?? '', cur(r, 'connBase'), cur(r, 'connSlot') ?? '', cur(r, 'connIsRobot') ?? '',
        cur(r, 'description'), cur(r, 'agent') ?? '', cur(r, 'modelName'), cur(r, 'modelVersion')];
      lines.push(cols.map(csvEscape).join(','));
    }
    const content = '﻿' + lines.join('\r\n');
    const blob = new Blob([content], { type: 'text/csv;charset=utf-8;' });
    const url = URL.createObjectURL(blob);
    const d = new Date(); const p = (n) => String(n).padStart(2, '0');
    const name = `assets-${typeName(S.activeType) || 'All'}-${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}.csv`;
    const a = document.createElement('a'); a.href = url; a.download = name; a.click();
    URL.revokeObjectURL(url);
  }

  /* ════════════════ 배치 편집 패널 ════════════════ */
  function selectedIds() {
    return Array.from($('t-table').querySelectorAll('.sel-check'))
      .filter(c => c.checked)
      .map(c => +c.closest('tr[data-aid]').getAttribute('data-aid'));
  }
  function openBatch() {
    let ids = selectedIds();
    // 선택된 행이 없으면 현재 필터된 전체 대상
    const scope = ids.length > 0 ? ids.length : sortRows(filtered()).length;
    $('batch-note').textContent = ids.length > 0
      ? `선택된 자산 ${ids.length}건에 적용합니다. 체크된 항목만 변경됩니다.`
      : `현재 표시된 자산 ${scope}건 전체에 적용합니다. 체크된 항목만 변경됩니다. (행 선택 시 선택분만 적용)`;
    $('batch-overlay').classList.add('open');
    $('batch-panel').classList.add('open');
  }
  function closeBatch() {
    $('batch-overlay').classList.remove('open');
    $('batch-panel').classList.remove('open');
  }
  function bindBatchToggles() {
    const pairs = [['b-apply-agent', 'b-agent'], ['b-apply-name', 'b-name'], ['b-apply-ip', 'b-ip'],
      ['b-apply-desc', 'b-desc'], ['b-apply-via', 'b-via'], ['b-apply-base', 'b-base'], ['b-apply-slot', 'b-slot']];
    pairs.forEach(([chk, inp]) => {
      $(chk).addEventListener('change', () => { $(inp).disabled = !$(chk).checked; if ($(chk).checked) $(inp).focus(); });
    });
  }
  async function applyBatch() {
    let ids = selectedIds();
    if (ids.length === 0) ids = sortRows(filtered()).map(r => r.assetId);
    if (ids.length === 0) { if (window.Shell) Shell.toast('대상 자산이 없습니다.'); return; }

    const num = (id) => { const v = $(id).value.trim(); return v === '' ? null : parseInt(v, 10); };
    const body = {
      assetIds: ids,
      applyAgent: $('b-apply-agent').checked, agentPreferences: $('b-agent').value,
      applyName: $('b-apply-name').checked, name: $('b-name').value,
      applyIp: $('b-apply-ip').checked, ip: $('b-ip').value,
      applyDescription: $('b-apply-desc').checked, description: $('b-desc').value,
      applyViaIp: $('b-apply-via').checked, viaIp: $('b-via').value,
      applyBase: $('b-apply-base').checked, baseNumber: num('b-base'),
      applySlot: $('b-apply-slot').checked, slotNumber: num('b-slot'),
    };
    if (!(body.applyAgent || body.applyName || body.applyIp || body.applyDescription || body.applyViaIp || body.applyBase || body.applySlot)) {
      if (window.Shell) Shell.toast('변경할 항목을 선택하세요.'); return;
    }

    const btn = $('batch-apply'); btn.disabled = true;
    setProgress(40); hideAlert();
    try {
      const res = await fetch('/api/assets/table/batch', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
      });
      setProgress(80);
      if (!res.ok) {
        let msg = '일괄 적용 실패';
        try { const ej = await res.json(); if (ej.error) msg = ej.error; } catch (e) { /* */ }
        showAlert('err', 'error', msg); return;
      }
      const d = await res.json();
      closeBatch();
      if (d.fail === 0) showAlert('ok', 'check_circle', `${d.success}건 일괄 적용 완료`, true);
      else showAlert('warn', 'warning', `성공 ${d.success}건, 실패 ${d.fail}건`);
      setProgress(100);
      await load();
    } catch (e) {
      showAlert('err', 'error', '일괄 적용 오류: ' + e.message);
    } finally {
      btn.disabled = false; setProgress(null);
    }
  }

  /* ── 이벤트 바인딩 ── */
  function bind() {
    $('t-search').addEventListener('input', (e) => { S.search = e.target.value; S.page = 0; render(); });
    $('t-save').addEventListener('click', save);
    $('t-refresh').addEventListener('click', () => {
      if (modifiedRows().length > 0 && !confirm('저장하지 않은 변경사항이 있습니다. 새로고침하시겠습니까?')) return;
      load();
    });
    $('t-csv').addEventListener('click', exportCsv);
    $('t-batch').addEventListener('click', openBatch);
    $('t-alert-close').addEventListener('click', hideAlert);

    $('batch-close').addEventListener('click', closeBatch);
    $('batch-cancel').addEventListener('click', closeBatch);
    $('batch-overlay').addEventListener('click', closeBatch);
    $('batch-apply').addEventListener('click', applyBatch);
    bindBatchToggles();

    window.addEventListener('beforeunload', (e) => {
      if (modifiedRows().length > 0) { e.preventDefault(); e.returnValue = ''; }
    });
  }

  document.addEventListener('DOMContentLoaded', async () => {
    if (window.Shell) await Shell.init({ active: '' });
    bind();
    await load();
  });
})();
