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

  // 자산명 Windows 파일/폴더명 규칙 검사 — DEXA 가 자산명으로 백업 경로를 만들므로 위반 시 저장 차단.
  // (서버 AssetTableController.WinNameError 와 동일 규칙; 위반 사유 문자열, 통과 시 null)
  function winNameError(name) {
    const raw = String(name == null ? '' : name);
    const s = raw.trim();
    if (!s) return '자산명이 비어 있습니다.';
    if (/[\\/:*?"<>|]/.test(s)) return '\\ / : * ? " < > | 문자는 사용할 수 없습니다.';
    if (/[\x00-\x1f]/.test(s)) return '제어 문자는 사용할 수 없습니다.';
    if (/[. ]$/.test(raw)) return '이름은 마침표(.)나 공백으로 끝날 수 없습니다.';
    if (/^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$/i.test(s)) return `'${s}' 은(는) Windows 예약어라 사용할 수 없습니다.`;
    return null;
  }

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
    bindSelection();
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
        } else if (field === 'name') {
          // 자산명: Windows 파일명 규칙 실시간 검사 (DEXA 백업 경로에 사용)
          el.addEventListener('input', () => {
            const err = winNameError(el.value);
            el.classList.toggle('invalid', !!err);
            el.title = err || '';
            setEdit(aid, field, el.value);
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
    // 자산명 규칙 위반 행이 있으면 저장 차단 (DEXA 백업 경로 보호)
    const badRow = modifiedRows().find(r => winNameError(cur(r, 'name')));
    if (badRow) {
      showAlert('err', 'error', `자산 #${badRow.assetId} 이름 오류: ${winNameError(cur(badRow, 'name'))}`);
      return;
    }
    const rows = buildSavePayload();
    if (rows.length === 0) return;
    S.saving = true; updateSummary();
    setProgress(40); hideAlert();
    try {
      const res = await fetch('/api/assets/table', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ rows }),
      });
      setProgress(80);
      if (!res.ok) {
        let msg = '저장 실패 (' + res.status + ')';
        try { const ej = await res.json(); if (ej.error) msg = ej.error; } catch (e) { /* */ }
        showAlert('err', 'error', msg); return;
      }
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
    // 자산명 일괄 변경: Windows 파일명 규칙 검사 (DEXA 백업 경로에 사용)
    if (body.applyName) {
      const nameErr = winNameError(body.name);
      if (nameErr) { if (window.Shell) Shell.toast('자산명 오류: ' + nameErr); $('b-name').focus(); return; }
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
  /* ── 선택 ─────────────────────────────────────────────────────────
     행 체크박스는 일괄 편집이 쓰던 것을 그대로 쓴다. 선택 수를 삭제 버튼에 반영. */
  function bindSelection() {
    const table = $('t-table');
    if (!table) return;
    table.querySelectorAll('.sel-check').forEach(c => c.addEventListener('change', updateSelCount));
    updateSelCount();
  }

  function updateSelCount() {
    const btn = $('t-del');
    if (!btn) return;
    const n = selectedIds().length;
    btn.disabled = n === 0;
    btn.style.opacity = n === 0 ? '0.45' : '';
    btn.innerHTML = '<span class="material-symbols-outlined">delete</span>선택 삭제' + (n ? ' (' + n + ')' : '');
  }

  /* ── 삭제 ───────────────────────────────────────────────────────── */
  function openConfirmDelete() {
    const ids = selectedIds();
    if (!ids.length) return;
    const rows = ids.map(id => S.rows.find(r => r.assetId === id)).filter(Boolean);
    $('cfm-msg').textContent = '선택한 자산 ' + ids.length + '건을 삭제합니다.';
    $('cfm-list').innerHTML =
      rows.slice(0, 20).map(r =>
        '<div>' + esc(cur(r, 'name')) + ' <span class="reg-hint">· ' + esc(r.typeName) + ' · #' + r.assetId + '</span></div>'
      ).join('') +
      (rows.length > 20 ? '<div class="reg-hint">… 외 ' + (rows.length - 20) + '건</div>' : '');
    $('cfm-overlay').classList.add('open');
    $('cfm-box').classList.add('open');
  }

  function closeConfirm() {
    $('cfm-overlay').classList.remove('open');
    $('cfm-box').classList.remove('open');
  }

  async function doDelete() {
    const ids = selectedIds();
    closeConfirm();
    if (!ids.length) return;

    hideAlert();
    setProgress(30);
    try {
      const res = await fetch('/api/assets/delete', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ assetIds: ids }),
      });
      const d = await res.json().catch(() => ({}));
      setProgress(90);
      if (!res.ok) { showAlert('err', 'error', d.error || '삭제에 실패했습니다.'); return; }

      if (d.fail > 0) {
        const first = (d.results || []).find(r => !r.success);
        showAlert('warn', 'warning',
          d.success + '건 삭제, ' + d.fail + '건 실패' + (first && first.error ? ' — ' + first.error : ''));
      } else {
        showAlert('ok', 'check_circle', d.success + '건을 삭제했습니다.', true);
      }
      await load();
    } catch (e) {
      showAlert('err', 'error', '삭제 중 오류: ' + e.message);
    } finally {
      setProgress(0);
    }
  }

  /* ── 등록 (단건) ─────────────────────────────────────────────────── */
  const REG = { token: null, fileName: null, identified: null };

  // PLC(6)·서보(7)는 프로젝트 파일이 필수다 — 등록 후에는 붙일 수 없다.
  function isFileType(typeId) { return typeId === 6 || typeId === 7; }
  // 드라이브(4)만 모델·버전과 경유 연결이 DEXA 파라미터로 들어간다.
  function isDriveType(typeId) { return typeId === 4; }

  // DEXA "LS Drive" 타입 템플릿의 modelName candidates 와 같은 목록.
  const DRIVE_MODELS = ['iS7', 'S100', 'H100', 'G100', 'S300'];
  // 입력 보조용 제안 목록일 뿐이다 — 직접 입력도 된다.
  const DRIVE_VERSIONS = ['1.00', '1.02', '1.04', '1.05', '1.06', '1.10'];

  function regAlert(msg) {
    $('reg-alert-msg').textContent = msg;
    $('reg-alert').style.display = '';
  }

  function syncRegType() {
    const raw = $('reg-type').value;
    const t = raw === '' ? 0 : +raw;
    const file = isFileType(t);
    const drive = isDriveType(t);

    $('reg-file-box').style.display = file ? '' : 'none';
    $('reg-drive-box').style.display = drive ? '' : 'none';
    // 경유는 드라이브와 PLC/서보가 함께 쓴다 — 저장되는 곳만 다르다.
    $('reg-via-box').style.display = (file || drive) ? '' : 'none';
    $('reg-robot-box').style.display = t === 6 ? '' : 'none';
    $('reg-file').setAttribute('accept', t === 7 ? '.xpj' : '.xgwx');

    $('reg-via-hint').textContent = drive
      ? '드라이브는 대개 PLC 를 거쳐 접속합니다. 실제 백업 경로이므로 실물과 맞춰주세요.'
      : '상태 표시(핑)에만 쓰입니다. 백업 접속 경로는 프로젝트 파일을 따릅니다.';
    $('reg-ip-hint').textContent = file
      ? '상태 표시(핑)에 쓰는 주소입니다. 백업 접속 대상은 프로젝트 파일에서 읽습니다.'
      : '백업 대상 주소입니다.';
    $('reg-check-hint').textContent = drive
      ? '드라이브에 접속해 기종·버전을 읽어 모델명·버전을 채웁니다.'
      : (file ? '핑으로 확인합니다 (경유가 있으면 경유 PLC 를 통해).' : '핑으로 확인합니다.');
    clearCheck();
  }

  function syncViaUse() {
    const on = $('reg-via-use').checked;
    ['reg-via', 'reg-base', 'reg-slot'].forEach(id => { $(id).disabled = !on; });
  }

  function openReg() {
    REG.token = null; REG.fileName = null;
    ['reg-name', 'reg-desc', 'reg-ip', 'reg-agent', 'reg-via', 'reg-slot', 'reg-modelver']
      .forEach(id => { $(id).value = ''; });
    $('reg-base').value = '0';
    $('reg-via-use').checked = false;
    $('reg-robot').checked = false;
    $('reg-file').value = '';
    $('reg-file-name').textContent = '선택된 파일 없음';
    $('reg-file-warn').style.display = 'none';
    $('reg-alert').style.display = 'none';
    $('reg-name-err').textContent = '';

    // 등록 가능한 타입만 — FTP/SFTP 는 현재 범위에서 제외
    const regTypes = TYPES.filter(t => [4, 5, 6, 7].indexOf(t.id) >= 0);
    // 타입 탭에서 열었으면 그 타입을 미리 고른다. '전체' 탭에서는 고르지 않는다 —
    // 기본값을 몰래 정해두면 타입을 선택한 줄 모르고 엉뚱한 자산을 등록하게 된다.
    const preset = regTypes.some(t => t.id === S.activeType) ? S.activeType : null;
    $('reg-type').innerHTML =
      (preset === null ? '<option value="" selected>-- 자산 타입 선택 --</option>' : '') +
      regTypes.map(t =>
        '<option value="' + t.id + '"' + (t.id === preset ? ' selected' : '') + '>' + esc(t.name) + '</option>').join('');

    $('reg-model').innerHTML = DRIVE_MODELS.map(m => '<option value="' + m + '">' + m + '</option>').join('');
    $('reg-ver-list').innerHTML = DRIVE_VERSIONS.map(v => '<option value="' + v + '">').join('');
    $('reg-line').innerHTML = '<option value="">-- 선택 --</option>' +
      S.lineOptions.map(o => '<option value="' + o.id + '">' + esc(o.name) + '</option>').join('');

    syncRegType();
    syncViaUse();
    $('reg-overlay').classList.add('open');
    $('reg-panel').classList.add('open');
    $('reg-name').focus();
  }

  function closeReg() {
    $('reg-overlay').classList.remove('open');
    $('reg-panel').classList.remove('open');
  }

  async function uploadProjectFile(f) {
    if (!f) return;
    const fd = new FormData();
    fd.append('file', f);
    $('reg-file-name').textContent = '업로드 중…';
    try {
      const res = await fetch('/api/assets/project-file', { method: 'POST', body: fd });
      const d = await res.json().catch(() => ({}));
      if (!res.ok) {
        $('reg-file-name').textContent = '선택된 파일 없음';
        regAlert(d.error || '업로드에 실패했습니다.');
        return;
      }
      REG.token = d.token; REG.fileName = d.fileName;
      $('reg-file-name').textContent =
        d.fileName + ' · ' + (d.size / 1024).toFixed(1) + 'KB · md5 ' + String(d.md5).slice(0, 8) + '…';
      if (d.warning) {
        $('reg-file-warn-msg').textContent = d.warning;
        $('reg-file-warn').style.display = '';
      } else {
        $('reg-file-warn').style.display = 'none';
      }
    } catch (e) {
      $('reg-file-name').textContent = '선택된 파일 없음';
      regAlert('업로드 중 오류: ' + e.message);
    }
  }

  async function submitReg() {
    const typeRaw = $('reg-type').value;
    if (typeRaw === '') { regAlert('자산 타입을 선택해주세요.'); return; }
    const typeId = +typeRaw;
    const name = $('reg-name').value.trim();
    const lineRaw = $('reg-line').value;
    const ip = $('reg-ip').value.trim();

    const nameErr = winNameError(name);
    $('reg-name-err').textContent = nameErr || '';
    if (nameErr) return;
    if (!lineRaw) { regAlert('라인을 선택해주세요.'); return; }
    if (isFileType(typeId) && !REG.token) { regAlert('PLC·서보는 프로젝트 파일이 필요합니다.'); return; }
    if (!ip) { regAlert('IP 를 입력해주세요.'); return; }

    // 드라이브는 모델·버전이 백업 파라미터 맵을 고른다 — 비워두면 템플릿 기본값(iS7 1.00)이 남는다.
    const modelVer = $('reg-modelver').value.trim();
    if (isDriveType(typeId)) {
      if (!modelVer) { regAlert('드라이브는 모델 버전이 필요합니다. 비워두면 백업 내용이 어긋납니다.'); return; }
      if (!/^\d+\.\d+$/.test(modelVer)) { regAlert('모델 버전은 1.04 처럼 입력해주세요.'); return; }
    }

    // 전역 동명 확인 — 서버도 막지만 왕복 전에 알려준다(대소문자·공백 무시)
    const dup = S.rows.find(r => String(cur(r, 'name') || '').trim().toLowerCase() === name.toLowerCase());
    if (dup) { regAlert('같은 이름의 자산이 이미 있습니다 (#' + dup.assetId + ').'); return; }

    const viaUse = $('reg-via-use').checked;
    const body = {
      assetTypeId: typeId,
      name: name,
      lineId: parseInt(lineRaw, 10),
      ip: ip,
      description: $('reg-desc').value.trim() || null,
      agent: $('reg-agent').value.trim() || null,
      projectFileToken: REG.token,
      connIpVia: viaUse ? ($('reg-via').value.trim() || null) : null,
      connBase: viaUse ? (parseInt($('reg-base').value, 10) || 0) : 0,
      connSlot: viaUse && $('reg-slot').value !== '' ? parseInt($('reg-slot').value, 10) : null,
      isRobotPlc: $('reg-robot').checked,
      modelName: isDriveType(typeId) ? $('reg-model').value : null,
      modelVersion: isDriveType(typeId) ? modelVer : null,
    };

    $('reg-submit').disabled = true;
    hideAlert();
    setProgress(30);
    try {
      const res = await fetch('/api/assets', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
      const d = await res.json().catch(() => ({}));
      setProgress(90);
      if (!res.ok || !d.ok) { regAlert(d.error || '등록에 실패했습니다.'); return; }

      closeReg();
      showAlert('ok', 'check_circle', name + ' 을(를) 등록했습니다 (#' + d.assetId + ').', true);
      await load();
    } catch (e) {
      regAlert('등록 중 오류: ' + e.message);
    } finally {
      $('reg-submit').disabled = false;
      setProgress(0);
    }
  }

  /* ── 연결 확인 ─────────────────────────────────────────────────────
     입력한 연결 정보로 실제 접속해 본다. HMI/PLC/서보는 핑(경유면 DeepPing),
     드라이브는 Modbus 로 기종코드·모델버전을 읽어 폼에 채운다 —
     DEXA 템플릿 기본값(iS7 1.00)이 조용히 남아 백업이 어긋나는 일을 막는 게 목적이다.
     채우되 숨기지 않는다: 읽은 값을 보여주고 등록은 여전히 사용자가 누른다. */
  function showCheck(kind, icon, html) {
    const box = $('reg-check-result');
    box.className = 'hist-alert ' + kind;
    $('reg-check-icon').textContent = icon;
    $('reg-check-msg').innerHTML = html;
    box.style.display = '';
  }

  function clearCheck() {
    REG.identified = null;
    const box = $('reg-check-result');
    if (box) box.style.display = 'none';
  }

  async function checkConnection() {
    const typeRaw = $('reg-type').value;
    if (typeRaw === '') { regAlert('자산 타입을 먼저 선택해주세요.'); return; }
    const typeId = +typeRaw;
    const ip = $('reg-ip').value.trim();
    if (!ip) { regAlert('IP 를 입력해주세요.'); return; }

    const viaUse = $('reg-via-use').checked && (isDriveType(typeId) || isFileType(typeId));
    const viaIp = viaUse ? $('reg-via').value.trim() : '';
    if (viaUse && !viaIp) { regAlert('경유 IP 를 입력해주세요.'); return; }

    const btn = $('reg-check');
    btn.disabled = true;
    $('reg-alert').style.display = 'none';
    showCheck('info', 'hourglass_top', isDriveType(typeId) ? '드라이브에 접속해 기종·버전을 읽는 중…' : '연결 확인 중…');
    try {
      const res = await fetch('/api/assets/connection-check', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          assetTypeId: typeId,
          ip: ip,
          viaIp: viaUse ? viaIp : null,
          viaBase: viaUse ? (parseInt($('reg-base').value, 10) || 0) : 0,
          viaSlot: viaUse && $('reg-slot').value !== '' ? parseInt($('reg-slot').value, 10) : null,
        }),
      });
      const d = await res.json().catch(() => ({}));
      if (!res.ok) { showCheck('err', 'error', esc(d.error || '확인에 실패했습니다.')); return; }
      renderCheck(typeId, d);
    } catch (e) {
      showCheck('err', 'error', '확인 중 오류: ' + esc(e.message));
    } finally {
      btn.disabled = false;
    }
  }

  function renderCheck(typeId, d) {
    const rtt = d.rttMs != null ? ' · ' + d.rttMs + 'ms' : '';
    if (!d.reachable) {
      const viaPath = d.method === 'xgt-tunnel' || d.method === 'deep-ping';
      showCheck('err', 'link_off',
        '연결 실패' + (d.error ? ' — ' + esc(d.error) : '') +
        '<br><small>' + (viaPath ? '경유 PLC 주소·Base·Slot 과 장비 IP 를 확인하세요.' : 'IP 와 네트워크 경로를 확인하세요.') + '</small>');
      return;
    }
    if (!isDriveType(typeId) || !d.drive) {
      showCheck('ok', 'check_circle', '연결 확인됨' + rtt);
      return;
    }

    const dr = d.drive;
    const ver = dr.modelVersion || '';
    if (!dr.dexaSupported) {
      showCheck('warn', 'warning',
        '접속은 되지만 <b>' + esc(dr.series) + '</b>(코드 ' + dr.modelCode + ')은 DEXA 가 지원하지 않는 기종입니다.' +
        (ver ? ' 버전 ' + esc(ver) : '') + rtt);
      return;
    }
    if (!ver) {
      showCheck('warn', 'warning', '<b>' + esc(dr.modelName) + '</b> 로 읽혔지만 모델버전을 읽지 못했습니다. 버전은 직접 입력해주세요.' + rtt);
      $('reg-model').value = dr.modelName;
      return;
    }

    // DEXA 가 후보를 늘렸는데 폼 목록에 없으면 추가해서 고른다
    const sel = $('reg-model');
    if (!Array.from(sel.options).some(o => o.value === dr.modelName)) {
      const o = document.createElement('option'); o.value = o.textContent = dr.modelName; sel.appendChild(o);
    }
    sel.value = dr.modelName;
    $('reg-modelver').value = ver;
    REG.identified = { modelName: dr.modelName, modelVersion: ver };

    let msg = '<b>' + esc(dr.modelName) + ' ' + esc(ver) + '</b> 로 읽혔습니다 — 모델명·버전을 채웠습니다' + rtt;
    if (dr.invSwVersion) msg += '<br><small>인버터 SW 버전 ' + esc(dr.invSwVersion) + ' (백업에는 모델버전을 씁니다)</small>';
    if (dr.inCatalog === false) {
      showCheck('warn', 'warning', msg +
        '<br><small><b>DriveView 9 에 ' + esc(dr.modelName) + ' ' + esc(ver) + ' 정의(.INV)가 없습니다.</b> 이대로 등록하면 백업이 실패할 수 있습니다.</small>');
      return;
    }
    showCheck('ok', 'check_circle', msg);
  }

  /* ── 일괄 등록 (HMI·드라이브) ─────────────────────────────────────
     CSV 로 받아 검증 표에 올리고, 행마다 연결 확인으로 드라이브 모델·버전을 실물에서 채운 뒤
     건별로 등록한다. PLC/서보는 프로젝트 파일이 있어야 해서 여기선 받지 않는다.
     되돌리기는 없다 — DEXA 등록은 건마다 커밋이라 롤백이 불가능하고, 대신 실패 행만 남겨 다시 올린다. */
  const BULK = { rows: [], seq: 0, checking: false, submitting: false };
  const BULK_TYPES = [5, 4];   // HMI, 드라이브 — 표에 보이는 순서

  // 헤더 별칭 — DriveScanner 서식(영문)과 TWMS 서식(한글)을 모두 받는다. 공백·밑줄·괄호는 무시.
  const BULK_COLS = {
    type:    ['타입', 'type', 'assettype', '자산타입'],
    name:    ['이름', 'name', '자산명'],
    line:    ['라인', 'line', 'linename', '라인명'],
    ip:      ['ip', 'ip주소', '주소', 'driveip'],
    viaUse:  ['viause', '경유사용', '경유'],
    viaIp:   ['경유ip', 'viaip', 'via', '경유plc', '경유plcip'],
    base:    ['base', 'viabase', '베이스'],
    slot:    ['slot', 'viaslot', '슬롯'],
    model:   ['모델명', 'modelname', 'model', 'series', '기종'],
    ver:     ['모델버전', 'modelversion', 'version', '버전'],
    desc:    ['설명', 'description', 'desc', 'memo'],
    agent:   ['에이전트', 'agent'],
    enabled: ['enabled', '사용', '사용여부'],
  };
  const normHeader = (h) => String(h == null ? '' : h).trim().toLowerCase().replace(/[\s_\-()\[\]]/g, '');

  function bulkAlert(msg) {
    $('bulk-alert-msg').textContent = msg;
    $('bulk-alert').style.display = msg ? '' : 'none';
  }

  function inferType(raw, viaUse, ver, model) {
    const s = normHeader(raw);
    if (s) {
      if (/^(4|드라이브|drive|inverter|인버터|lsdrive)$/.test(s)) return 4;
      if (/^(5|hmi|xp|lsxpseries|xp시리즈)$/.test(s)) return 5;
      if (/^(6|plc|xgt|lsxgtplc)$/.test(s)) return 6;
      if (/^(7|servo|서보|lsservo)$/.test(s)) return 7;
    }
    // 타입 열이 없으면 경유·모델·버전이 있는 행을 드라이브로 본다 — DriveScanner 파일에는 타입이 없다
    return (viaUse || ver || model) ? 4 : 5;
  }

  function resolveLine(raw) {
    const s = String(raw == null ? '' : raw).trim();
    if (!s) return '';
    const byName = S.lineOptions.find(o => String(o.name).trim().toLowerCase() === s.toLowerCase());
    if (byName) return String(byName.id);
    if (/^\d+$/.test(s) && S.lineOptions.some(o => String(o.id) === s)) return s;
    return '';
  }

  function newBulkRow(d) {
    return {
      id: ++BULK.seq, typeId: d.typeId || 5,
      name: d.name || '', lineRaw: d.lineRaw || '', lineId: resolveLine(d.lineRaw),
      ip: d.ip || '', viaIp: d.viaIp || '', base: d.base || '0', slot: d.slot || '',
      model: d.model || '', ver: d.ver || '', desc: d.desc || '', agent: d.agent || '',
      status: null,   // 연결 확인 결과 {kind, icon, msg}
      result: null,   // 등록 결과 {success, assetId, error}
    };
  }

  /* CSV 파싱 — 큰따옴표 안의 구분자 보호, 첫 줄이 탭을 품으면 탭 구분(Excel 붙여넣기). */
  function parseCsvText(text) {
    const lines = text.replace(/^﻿/, '').split(/\r?\n/).filter(l => l.trim() && !l.trim().startsWith('#'));
    if (!lines.length) return [];
    const delim = lines[0].includes('\t') ? '\t' : ',';
    const split = (line) => {
      const out = []; let cur = '', q = false;
      for (let i = 0; i < line.length; i++) {
        const c = line[i];
        if (c === '"') { if (q && line[i + 1] === '"') { cur += '"'; i++; } else q = !q; }
        else if (c === delim && !q) { out.push(cur); cur = ''; }
        else cur += c;
      }
      out.push(cur);
      return out.map(v => v.trim());
    };
    const header = split(lines[0]).map(normHeader);
    const idx = {};
    Object.keys(BULK_COLS).forEach(k => {
      const i = header.findIndex(h => BULK_COLS[k].includes(h));
      if (i >= 0) idx[k] = i;
    });
    if (idx.name == null || idx.ip == null)
      throw new Error('헤더에 "이름"과 "IP" 열이 있어야 합니다. 서식을 내려받아 확인하세요.');

    const rows = [];
    for (let li = 1; li < lines.length; li++) {
      const c = split(lines[li]);
      const g = (k) => (idx[k] != null ? (c[idx[k]] == null ? '' : c[idx[k]]) : '');
      if (/^(0|false|no|n|x|아니오)$/i.test(g('enabled'))) continue;   // DriveScanner 의 Enabled=0
      const viaIp = g('viaIp');
      const viaUse = viaIp !== '' && !/^(0|false|no|n)$/i.test(g('viaUse') || '1');
      rows.push(newBulkRow({
        typeId: inferType(g('type'), viaUse, g('ver'), g('model')),
        name: g('name'), lineRaw: g('line'), ip: g('ip'),
        viaIp: viaUse ? viaIp : '', base: g('base') || '0', slot: g('slot'),
        model: g('model'), ver: g('ver'), desc: g('desc'), agent: g('agent'),
      }));
    }
    return rows;
  }

  function parseBulk() {
    bulkAlert('');
    let rows;
    try { rows = parseCsvText($('bulk-text').value); }
    catch (e) { bulkAlert(e.message); return; }
    if (!rows.length) { bulkAlert('불러올 행이 없습니다.'); return; }
    BULK.rows = rows;
    $('bulk-progress').textContent = '';
    renderBulk();
  }

  function readBulkFile(f) {
    if (!f) return;
    const read = (enc) => new Promise((res, rej) => {
      const fr = new FileReader(); fr.onload = () => res(fr.result); fr.onerror = rej; fr.readAsText(f, enc);
    });
    // Excel 이 저장한 한글 CSV 는 CP949 인 경우가 많다 — UTF-8 로 깨지면 다시 읽는다
    read('utf-8').then(t => (t.includes('�') ? read('euc-kr') : t))
      .then(t => { $('bulk-text').value = t; parseBulk(); })
      .catch(e => bulkAlert('파일 읽기 실패: ' + e.message));
  }

  function downloadBulkTemplate() {
    const line = S.lineOptions[0] ? S.lineOptions[0].name : 'BB';
    const csv = [
      '타입,이름,라인,IP,경유IP,Base,Slot,모델명,모델버전,설명,에이전트',
      '드라이브,UB1 #121 INV,' + line + ',200.19.8.142,120.200.200.190,0,8,iS7,1.04,선택,',
      'HMI,UB1 HMI,' + line + ',192.168.0.10,,,,,,,',
      '# 로 시작하는 줄은 무시됩니다. 드라이브 모델명·버전은 비워두고 "전체 연결 확인" 으로 실물에서 채워도 됩니다.',
    ].join('\r\n');
    const blob = new Blob(['﻿' + csv], { type: 'text/csv;charset=utf-8' });   // BOM: Excel 한글 깨짐 방지
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob); a.download = 'twms-자산-일괄등록-서식.csv'; a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 1000);
  }

  /* ── 표 ── */
  function bulkRowClass(r) {
    if (r.result) return r.result.success ? 'bulk-ok' : 'bulk-err';
    return r.status ? 'bulk-' + r.status.kind : '';
  }
  function bulkStatusHtml(r) {
    if (r.result) {
      return r.result.success
        ? '<span class="bulk-status"><span class="material-symbols-outlined">check_circle</span>등록됨 #' + r.result.assetId + '</span>'
        : '<span class="bulk-status"><span class="material-symbols-outlined">error</span>' + esc(r.result.error || '실패') + '</span>';
    }
    if (r.status)
      return '<span class="bulk-status"><span class="material-symbols-outlined">' + r.status.icon + '</span>' + esc(r.status.msg) + '</span>';
    return '';
  }
  function bulkRowEl(r) { return $('bulk-rows').querySelector('tr[data-id="' + r.id + '"]'); }
  function setBulkStatus(r, kind, icon, msg) {
    r.status = { kind, icon, msg };
    const tr = bulkRowEl(r);
    if (!tr) return;
    tr.className = bulkRowClass(r);
    tr.querySelector('.bulk-st').innerHTML = bulkStatusHtml(r);
  }
  function syncBulkRowInputs(r) {
    const tr = bulkRowEl(r);
    if (!tr) return;
    const sel = tr.querySelector('[data-k="model"]');
    if (sel) {
      if (r.model && !Array.from(sel.options).some(o => o.value === r.model)) {
        const o = document.createElement('option'); o.value = o.textContent = r.model; sel.appendChild(o);
      }
      sel.value = r.model;
    }
    const ver = tr.querySelector('[data-k="ver"]');
    if (ver) ver.value = r.ver;
  }

  function renderBulk() {
    const typeOpts = (sel) => BULK_TYPES.map(id => {
      const t = TYPES.find(x => x.id === id);
      return '<option value="' + id + '"' + (sel === id ? ' selected' : '') + '>' + esc(t ? t.name : id) + '</option>';
    }).join('');
    const lineOpts = (sel) => '<option value="">-- 라인 --</option>' +
      S.lineOptions.map(o => '<option value="' + o.id + '"' + (String(o.id) === String(sel) ? ' selected' : '') + '>' + esc(o.name) + '</option>').join('');
    const modelOpts = (sel) => {
      const list = DRIVE_MODELS.concat(sel && DRIVE_MODELS.indexOf(sel) < 0 ? [sel] : []);
      return '<option value="">--</option>' + list.map(m => '<option value="' + esc(m) + '"' + (m === sel ? ' selected' : '') + '>' + esc(m) + '</option>').join('');
    };

    $('bulk-rows').innerHTML = BULK.rows.map((r, i) => {
      const drive = r.typeId === 4;
      const done = !!(r.result && r.result.success);
      const dis = done ? ' disabled' : '';
      return '<tr data-id="' + r.id + '" class="' + bulkRowClass(r) + '">' +
        '<td>' + (i + 1) + '</td>' +
        '<td><select class="hist-input" data-k="typeId"' + dis + '>' + typeOpts(r.typeId) + '</select></td>' +
        '<td><input class="hist-input bulk-w-l" data-k="name" value="' + esc(r.name) + '"' + dis + ' /></td>' +
        '<td><select class="hist-input" data-k="lineId"' + dis + '>' + lineOpts(r.lineId) + '</select>' +
          (r.lineRaw && !r.lineId ? '<div class="reg-err">"' + esc(r.lineRaw) + '" 라인 없음</div>' : '') + '</td>' +
        '<td><input class="hist-input" data-k="ip" value="' + esc(r.ip) + '"' + dis + ' /></td>' +
        '<td><input class="hist-input" data-k="viaIp" value="' + esc(r.viaIp) + '" placeholder="없으면 직접"' + dis + ' /></td>' +
        '<td><input class="hist-input bulk-w-s" data-k="base" type="number" min="0" value="' + esc(r.base) + '"' + dis + ' /></td>' +
        '<td><input class="hist-input bulk-w-s" data-k="slot" type="number" min="0" value="' + esc(r.slot) + '"' + dis + ' /></td>' +
        '<td>' + (drive ? '<select class="hist-input" data-k="model"' + dis + '>' + modelOpts(r.model) + '</select>' : '<span class="reg-hint">—</span>') + '</td>' +
        '<td>' + (drive ? '<input class="hist-input bulk-w-s" data-k="ver" value="' + esc(r.ver) + '" placeholder="1.04"' + dis + ' />' : '<span class="reg-hint">—</span>') + '</td>' +
        '<td><input class="hist-input" data-k="desc" value="' + esc(r.desc) + '"' + dis + ' /></td>' +
        '<td class="bulk-st">' + bulkStatusHtml(r) + '</td>' +
        '<td>' + (done ? '' :
          '<span class="material-symbols-outlined bulk-rowbtn" data-act="check" title="이 행 연결 확인">network_check</span> ' +
          '<span class="material-symbols-outlined bulk-rowbtn" data-act="del" title="행 제거">close</span>') + '</td>' +
        '</tr>';
    }).join('');

    const pending = BULK.rows.filter(r => !(r.result && r.result.success)).length;
    $('bulk-empty').style.display = BULK.rows.length ? 'none' : '';
    $('bulk-count').textContent = BULK.rows.length ? BULK.rows.length + '행' : '';
    $('bulk-submit').disabled = pending === 0 || BULK.checking || BULK.submitting;
    $('bulk-check-all').disabled = pending === 0 || BULK.checking || BULK.submitting;
    $('bulk-keep-failed').style.display = BULK.rows.some(r => r.result) ? '' : 'none';
    $('bulk-foot-hint').textContent = pending ? pending + '건 등록 대기' : '';
  }

  function onBulkEdit(e) {
    const el = e.target.closest('[data-k]');
    const tr = e.target.closest('tr[data-id]');
    if (!el || !tr) return;
    const r = BULK.rows.find(x => String(x.id) === tr.dataset.id);
    if (!r) return;
    const k = el.dataset.k;
    if (k === 'typeId') { r.typeId = +el.value; r.status = null; renderBulk(); return; }   // 모델·버전 칸이 바뀌므로 다시 그린다
    r[k] = el.value;
    if (k === 'ip' || k === 'viaIp' || k === 'base' || k === 'slot') {
      // 연결 정보가 바뀌면 이전 확인 결과는 더 이상 그 입력에 대한 것이 아니다
      r.status = null; tr.className = bulkRowClass(r); tr.querySelector('.bulk-st').innerHTML = '';
    }
  }

  function onBulkClick(e) {
    const btn = e.target.closest('.bulk-rowbtn');
    const tr = e.target.closest('tr[data-id]');
    if (!btn || !tr) return;
    const r = BULK.rows.find(x => String(x.id) === tr.dataset.id);
    if (!r) return;
    if (btn.dataset.act === 'del') { BULK.rows = BULK.rows.filter(x => x !== r); renderBulk(); }
    else if (btn.dataset.act === 'check') checkBulkRow(r);
  }

  /* ── 연결 확인 — 단건 폼과 같은 API, 결과를 행에 쓴다 ── */
  async function checkBulkRow(r) {
    if (!r.ip) { setBulkStatus(r, 'err', 'error', 'IP 없음'); return; }
    setBulkStatus(r, 'info', 'hourglass_top', r.typeId === 4 ? '기종·버전 읽는 중…' : '확인 중…');
    try {
      const res = await fetch('/api/assets/connection-check', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          assetTypeId: r.typeId, ip: r.ip.trim(),
          viaIp: r.viaIp.trim() || null,
          viaBase: parseInt(r.base, 10) || 0,
          viaSlot: String(r.slot).trim() !== '' ? parseInt(r.slot, 10) : null,
        }),
      });
      const d = await res.json().catch(() => ({}));
      if (!res.ok) { setBulkStatus(r, 'err', 'error', d.error || '확인 실패'); return; }
      const rtt = d.rttMs != null ? ' · ' + d.rttMs + 'ms' : '';
      if (!d.reachable) { setBulkStatus(r, 'err', 'link_off', '연결 실패' + (d.error ? ' — ' + d.error : '')); return; }
      if (r.typeId !== 4 || !d.drive) { setBulkStatus(r, 'ok', 'check_circle', '연결됨' + rtt); return; }

      const dr = d.drive;
      if (!dr.dexaSupported) { setBulkStatus(r, 'warn', 'warning', dr.series + ' — DEXA 미지원 기종'); return; }
      if (dr.modelName) r.model = dr.modelName;
      if (dr.modelVersion) r.ver = dr.modelVersion;
      syncBulkRowInputs(r);
      if (!dr.modelVersion) { setBulkStatus(r, 'warn', 'warning', dr.modelName + ' — 버전을 못 읽음, 직접 입력'); return; }
      if (dr.inCatalog === false) { setBulkStatus(r, 'warn', 'warning', dr.modelName + ' ' + dr.modelVersion + ' — DriveView9 에 .INV 없음, 백업 실패 가능'); return; }
      setBulkStatus(r, 'ok', 'check_circle', dr.modelName + ' ' + dr.modelVersion + ' 읽음' + rtt);
    } catch (e) {
      setBulkStatus(r, 'err', 'error', '오류: ' + e.message);
    }
  }

  async function checkAllBulk() {
    if (BULK.checking) return;
    const targets = BULK.rows.filter(r => !(r.result && r.result.success));
    if (!targets.length) return;
    BULK.checking = true; renderBulk();
    let done = 0;
    const prog = () => { $('bulk-progress').textContent = '연결 확인 ' + done + ' / ' + targets.length; };
    prog();
    // 같은 경유 PLC 의 터널 슬롯을 다투므로 동시에 3개까지만
    const queue = targets.slice();
    await Promise.all(Array.from({ length: Math.min(3, queue.length) }, async () => {
      while (queue.length) { const r = queue.shift(); await checkBulkRow(r); done++; prog(); }
    }));
    BULK.checking = false;
    const bad = targets.filter(r => r.status && r.status.kind === 'err').length;
    $('bulk-progress').textContent = '연결 확인 완료 — ' + (targets.length - bad) + '건 성공' + (bad ? ', ' + bad + '건 실패' : '');
    renderBulk();
  }

  /* ── 등록 ── */
  async function submitBulk() {
    if (BULK.submitting || BULK.checking) return;
    bulkAlert('');
    const targets = BULK.rows.filter(r => !(r.result && r.result.success));
    if (!targets.length) return;

    // 서버에 보내기 전에 걸러낸다 — 건마다 커밋이라 중간 거부는 앞선 건들이 이미 만들어진 뒤다
    const seen = Object.create(null);
    const existing = Object.create(null);
    S.rows.forEach(x => { existing[String(cur(x, 'name') || '').trim().toLowerCase()] = x.assetId; });
    let bad = 0;
    targets.forEach(r => {
      const name = r.name.trim();
      let err = winNameError(name);
      if (!err && seen[name.toLowerCase()]) err = '같은 배치 안에 같은 이름';
      if (!err && existing[name.toLowerCase()]) err = '이미 있는 이름 (#' + existing[name.toLowerCase()] + ')';
      if (!err && !r.lineId) err = '라인을 선택하세요';
      if (!err && !r.ip.trim()) err = 'IP 없음';
      if (!err && r.typeId === 4) {
        if (!r.model) err = '모델명 없음 — 연결 확인으로 채우거나 직접 고르세요';
        else if (!/^\d+\.\d+$/.test(r.ver.trim())) err = '모델버전은 1.04 처럼';
      }
      seen[name.toLowerCase()] = true;
      if (err) { bad++; setBulkStatus(r, 'err', 'error', err); }
    });
    if (bad) { bulkAlert(bad + '건에 문제가 있습니다. 상태 열을 확인하세요.'); return; }

    BULK.submitting = true; renderBulk();
    $('bulk-progress').textContent = targets.length + '건 등록 중…';
    try {
      const items = targets.map(r => ({
        assetTypeId: r.typeId, name: r.name.trim(), lineId: parseInt(r.lineId, 10),
        ip: r.ip.trim(), description: r.desc.trim() || null, agent: r.agent.trim() || null,
        connIpVia: r.viaIp.trim() || null,
        connBase: parseInt(r.base, 10) || 0,
        connSlot: String(r.slot).trim() !== '' ? parseInt(r.slot, 10) : null,
        modelName: r.typeId === 4 ? r.model : null,
        modelVersion: r.typeId === 4 ? r.ver.trim() : null,
      }));
      const res = await fetch('/api/assets/register-bulk', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ items }),
      });
      const d = await res.json().catch(() => ({}));
      if (!res.ok) { bulkAlert(d.error || '등록에 실패했습니다.'); return; }
      (d.results || []).forEach(x => { const r = targets[x.index]; if (r) r.result = { success: x.success, assetId: x.assetId, error: x.error }; });
      $('bulk-progress').textContent = '등록 완료 — ' + d.success + '건 성공' + (d.fail ? ', ' + d.fail + '건 실패' : '');
      if (d.fail) showAlert('warn', 'warning', '일괄 등록: ' + d.success + '건 성공, ' + d.fail + '건 실패 — 패널의 상태 열을 확인하세요.');
      else showAlert('ok', 'check_circle', d.success + '건을 등록했습니다.', true);
      await load();
    } catch (e) {
      bulkAlert('등록 중 오류: ' + e.message);
    } finally {
      BULK.submitting = false; renderBulk();
    }
  }

  function keepFailedBulk() {
    BULK.rows = BULK.rows.filter(r => !(r.result && r.result.success));
    BULK.rows.forEach(r => { r.result = null; });
    $('bulk-progress').textContent = '';
    renderBulk();
  }

  function openBulk() {
    BULK.rows = []; BULK.checking = false; BULK.submitting = false;
    $('bulk-text').value = ''; $('bulk-file').value = ''; $('bulk-progress').textContent = '';
    bulkAlert('');
    renderBulk();
    $('bulk-overlay').classList.add('open');
    $('bulk-panel').classList.add('open');
  }
  function closeBulk() {
    $('bulk-overlay').classList.remove('open');
    $('bulk-panel').classList.remove('open');
  }

  function bindBulk() {
    if (!$('t-bulk')) return;
    $('t-bulk').addEventListener('click', openBulk);
    $('bulk-close').addEventListener('click', closeBulk);
    $('bulk-cancel').addEventListener('click', closeBulk);
    $('bulk-overlay').addEventListener('click', closeBulk);
    $('bulk-file-btn').addEventListener('click', () => $('bulk-file').click());
    $('bulk-file').addEventListener('change', (e) => readBulkFile(e.target.files[0]));
    $('bulk-template').addEventListener('click', downloadBulkTemplate);
    $('bulk-parse').addEventListener('click', parseBulk);
    $('bulk-addrow').addEventListener('click', () => { BULK.rows.push(newBulkRow({ typeId: 5 })); renderBulk(); });
    $('bulk-check-all').addEventListener('click', checkAllBulk);
    $('bulk-keep-failed').addEventListener('click', keepFailedBulk);
    $('bulk-submit').addEventListener('click', submitBulk);
    $('bulk-rows').addEventListener('input', onBulkEdit);
    $('bulk-rows').addEventListener('change', onBulkEdit);
    $('bulk-rows').addEventListener('click', onBulkClick);
  }

  function bindRegistrationAndDelete() {
    bindBulk();   // 일괄 등록은 마크업 유무를 스스로 확인한다
    if (!$('t-add')) return;   // 마크업이 없는 페이지면 건너뛴다

    $('t-add').addEventListener('click', openReg);
    $('t-del').addEventListener('click', openConfirmDelete);

    $('reg-close').addEventListener('click', closeReg);
    $('reg-cancel').addEventListener('click', closeReg);
    $('reg-overlay').addEventListener('click', closeReg);
    $('reg-type').addEventListener('change', syncRegType);
    $('reg-via-use').addEventListener('change', syncViaUse);
    $('reg-check').addEventListener('click', checkConnection);
    // 연결 정보가 바뀌면 이전 확인 결과는 더 이상 그 입력에 대한 것이 아니다
    ['reg-ip', 'reg-via', 'reg-base', 'reg-slot'].forEach(id => $(id).addEventListener('input', clearCheck));
    $('reg-via-use').addEventListener('change', clearCheck);
    $('reg-file-btn').addEventListener('click', () => $('reg-file').click());
    $('reg-file').addEventListener('change', (e) => uploadProjectFile(e.target.files[0]));
    $('reg-submit').addEventListener('click', submitReg);
    $('reg-name').addEventListener('input', () => {
      $('reg-name-err').textContent = winNameError($('reg-name').value.trim()) || '';
    });

    $('cfm-cancel').addEventListener('click', closeConfirm);
    $('cfm-overlay').addEventListener('click', closeConfirm);
    $('cfm-ok').addEventListener('click', doDelete);
  }

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
    bindRegistrationAndDelete();

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
