/* ============================================================================
 * 상태 모니터(StatusMonitor) — StatusMonitor.razor 의 MudDataGrid + 필터 + 상세 Drawer 를
 * 정적 페이지로 이식. GET /api/status 스냅샷 1회 조회 → 테이블 렌더.
 * 클라이언트 검색/타입/상태/온라인 필터 + 컬럼 정렬 + 행 클릭 상세 패널.
 * 30초 폴링 + 탭 복귀 시 갱신. (Ping 트리거는 정적 페이지에서 읽기 전용)
 * ==========================================================================*/
(function () {
  'use strict';

  // 백업 상태 매핑 (dashboard.js / history.js 와 동일 톤)
  const HEALTH = {
    backedup:   { label: '백업 갱신', chip: 'chip-success', icon: 'check_circle' },
    unchanged:  { label: '변경 없음', chip: 'chip-info',    icon: 'remove' },
    failed:     { label: '작업 실패', chip: 'chip-error',   icon: 'error' },
    inprogress: { label: '작업중',    chip: 'chip-warning', icon: 'hourglass_top' },
    unknown:    { label: '내역 없음', chip: 'chip-default', icon: 'help' },
  };

  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const $ = (id) => document.getElementById(id);

  const S = {
    rows: [], types: [],
    search: '', filterType: '', filterHealth: '', filterOnline: '',
    sort: { key: 'name', dir: 1 },
    selectedId: null,
  };

  /* ── 데이터 로드 ── */
  async function load() {
    try {
      const res = await fetch('/api/status', { headers: { 'Accept': 'application/json' } });
      if (!res.ok) return;
      const d = await res.json();
      S.rows = d.rows || [];
      S.types = d.types || [];
      fillTypeOptions();
      render();
      // 상세 패널 열려 있으면 최신 데이터로 갱신
      if (S.selectedId != null) {
        const cur = S.rows.find(r => r.assetId === S.selectedId);
        if (cur) renderDetail(cur); else closeDetail();
      }
    } catch (e) { /* 무시 */ }
  }

  function fillTypeOptions() {
    const sel = $('st-type');
    sel.innerHTML = '<option value="">전체 타입</option>' +
      S.types.map(t => `<option value="${esc(t)}"${t === S.filterType ? ' selected' : ''}>${esc(t)}</option>`).join('');
  }

  /* ── 필터 (Razor FilteredAssets 이식) ── */
  function filtered() {
    const term = (S.search || '').trim().toLowerCase();
    return S.rows.filter(r => {
      if (term) {
        const hit = (r.name && r.name.toLowerCase().includes(term)) ||
                    (r.ip && r.ip.toLowerCase().includes(term));
        if (!hit) return false;
      }
      if (S.filterType && r.assetTypeName !== S.filterType) return false;
      if (S.filterHealth && r.health !== S.filterHealth) return false;
      if (S.filterOnline) {
        const want = S.filterOnline === 'online';
        if (!!r.agentOnline !== want) return false;
      }
      return true;
    });
  }

  /* ── 정렬 ── */
  function sortRows(rows) {
    const { key, dir } = S.sort;
    return rows.slice().sort((a, b) => {
      let x = sortVal(a, key), y = sortVal(b, key);
      if (x == null && y == null) return 0;
      if (x == null) return 1; if (y == null) return -1;
      if (typeof x === 'number' && typeof y === 'number') return (x - y) * dir;
      if (typeof x === 'boolean') { x = x ? 1 : 0; y = y ? 1 : 0; return (x - y) * dir; }
      return String(x).localeCompare(String(y), 'ko') * dir;
    });
  }
  // 상태(health)는 dashboard 와 동일한 심각도 순으로 정렬
  const HEALTH_ORDER = { backedup: 0, inprogress: 1, failed: 2, unknown: 3, unchanged: 4 };
  function sortVal(r, key) {
    switch (key) {
      case 'health': return HEALTH_ORDER[r.health] ?? 9;
      case 'name': return r.name;
      case 'assetTypeName': return r.assetTypeName;
      case 'ip': return r.ip;
      case 'agentName': return r.agentName;
      case 'lastBackupTime': return r.lastBackupTime;
      default: return r[key];
    }
  }

  /* ── 테이블 렌더 (MudDataGrid 컬럼 구성 이식) ── */
  const COLS = [
    { title: '상태', key: 'health' },
    { title: '자산명', key: 'name' },
    { title: '타입', key: 'assetTypeName' },
    { title: 'IP', key: 'ip' },
    { title: '에이전트', key: 'agentName' },
    { title: '마지막 백업', key: 'lastBackupTime' },
    { title: 'Ping', key: '' },
  ];

  function render() {
    const rows = sortRows(filtered());
    $('st-count').textContent = `${rows.length} 건 조회됨`;

    const thead = '<thead><tr>' + COLS.map(c => {
      if (!c.key) return `<th class="nosort">${esc(c.title)}</th>`;
      const arrow = c.key === S.sort.key
        ? `<span class="sort-arrow material-symbols-outlined">${S.sort.dir === 1 ? 'arrow_drop_up' : 'arrow_drop_down'}</span>` : '';
      return `<th data-key="${c.key}">${esc(c.title)}${arrow}</th>`;
    }).join('') + '</tr></thead>';

    const body = rows.map(r => {
      const h = HEALTH[r.health] || HEALTH.unknown;
      const off = r.agentOnline ? '' : ` <span style="font-size:10px;color:var(--c-on-surface-variant);">(오프라인)</span>`;
      const statusChip = `<span class="chip ${h.chip}"><span class="material-symbols-outlined" style="font-size:14px;margin-right:3px;">${h.icon}</span>${esc(r.healthLabel || h.label)}</span>${off}`;
      const agent = `<span class="st-inline"><span class="st-dot ${r.agentOnline ? 'online' : 'offline'}"></span>${esc(r.agentName || '-')}</span>`;
      return `<tr data-id="${r.assetId}"${r.assetId === S.selectedId ? ' class="selected"' : ''}>
        <td>${statusChip}</td>
        <td>${esc(r.name)}</td>
        <td>${esc(r.assetTypeName || '-')}</td>
        <td>${esc(r.ip || '-')}</td>
        <td>${agent}</td>
        <td>${backupCell(r)}</td>
        <td>${pingCell(r.ping)}</td>
      </tr>`;
    }).join('');

    $('st-table').innerHTML = thead +
      (rows.length ? `<tbody>${body}</tbody>` : `<tbody><tr><td colspan="${COLS.length}"><div class="hist-empty">조회된 자산이 없습니다.</div></td></tr></tbody>`);

    bindSort();
    $('st-table').querySelectorAll('tbody tr[data-id]').forEach(tr => {
      tr.addEventListener('click', () => {
        const id = +tr.getAttribute('data-id');
        const row = S.rows.find(r => r.assetId === id);
        if (row) openDetail(row);
      });
    });
  }

  // 마지막 백업 셀 (Razor: 실패=Error / 변경=CheckCircle / 동일=RemoveCircle)
  function backupCell(r) {
    if (!r.lastBackupTime) return `<span class="st-ping-unk">-</span>`;
    let ico, color;
    if (!r.lastBackupSucceeded) { ico = 'error'; color = 'var(--health-failed)'; }
    else if (r.lastBackupChanged === true) { ico = 'check_circle'; color = 'var(--health-backedup)'; }
    else { ico = 'remove_circle'; color = 'var(--health-unchanged)'; }
    return `<span class="st-inline"><span class="material-symbols-outlined" style="font-size:16px;color:${color};">${ico}</span>${esc(fmtShort(r.lastBackupTime))}</span>`;
  }

  // Ping 셀 (Razor PingIndicator: 도달=ms / 미도달 / 미확인)
  function pingCell(p) {
    if (!p) return `<span class="st-ping-unk">미확인</span>`;
    if (p.reachable) return `<span class="st-inline st-ping-ok"><span class="material-symbols-outlined" style="font-size:16px;">wifi</span>${p.roundtripMs != null ? esc(p.roundtripMs) + 'ms' : '도달'}</span>`;
    return `<span class="st-inline st-ping-bad"><span class="material-symbols-outlined" style="font-size:16px;">wifi_off</span>미도달</span>`;
  }

  function bindSort() {
    $('st-table').querySelectorAll('th[data-key]').forEach(th => {
      const key = th.getAttribute('data-key');
      th.addEventListener('click', () => {
        if (S.sort.key === key) S.sort.dir = -S.sort.dir;
        else { S.sort.key = key; S.sort.dir = 1; }
        render();
      });
    });
  }

  /* ── 상세 패널 (Razor MudDrawer 이식) ── */
  function openDetail(r) {
    S.selectedId = r.assetId;
    $('st-layout').classList.add('with-detail');
    $('st-detail').style.display = '';
    renderDetail(r);
    // 선택 행 강조 갱신
    $('st-table').querySelectorAll('tbody tr[data-id]').forEach(tr =>
      tr.classList.toggle('selected', +tr.getAttribute('data-id') === r.assetId));
  }

  function closeDetail() {
    S.selectedId = null;
    $('st-layout').classList.remove('with-detail');
    $('st-detail').style.display = 'none';
    $('st-table').querySelectorAll('tbody tr.selected').forEach(tr => tr.classList.remove('selected'));
  }

  function renderDetail(r) {
    const h = HEALTH[r.health] || HEALTH.unknown;
    $('st-detail-name').textContent = r.name || '자산 상세';
    const fields = [];
    const field = (label, val) => `<div class="st-field"><div class="st-field-label">${esc(label)}</div><div class="st-field-value">${val}</div></div>`;

    fields.push(field('타입', esc(r.assetTypeName || '-')));
    fields.push(field('IP', esc(r.ip || '-')));
    fields.push(field('에이전트', `<span class="st-inline"><span class="st-dot ${r.agentOnline ? 'online' : 'offline'}"></span>${esc(r.agentName || '-')} (${r.agentOnline ? '온라인' : '오프라인'})</span>`));
    fields.push(field('마지막 백업', r.lastBackupTime ? esc(fmtDateTime(r.lastBackupTime)) : '없음'));
    fields.push(field('상태', `<span class="chip ${h.chip}"><span class="material-symbols-outlined" style="font-size:14px;margin-right:3px;">${h.icon}</span>${esc(r.healthLabel || h.label)}</span>`));
    if (r.ping) {
      const txt = r.ping.reachable
        ? `도달 (${r.ping.roundtripMs != null ? esc(r.ping.roundtripMs) + 'ms' : '-'})`
        : '미도달';
      fields.push(field('Ping', `${txt} — ${esc(fmtTimeOnly(r.ping.checkedAt))}`));
    }
    if (r.groupName) fields.push(field('그룹', esc(r.groupName)));

    $('st-detail-body').innerHTML = fields.join('');
  }

  /* ── 시간 포맷 ── */
  function fmtDateTime(s) {
    if (!s) return '-'; const d = new Date(s); if (isNaN(d)) return s;
    const p = (n) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
  }
  function fmtShort(s) {
    if (!s) return '-'; const d = new Date(s); if (isNaN(d)) return s;
    const p = (n) => String(n).padStart(2, '0');
    return `${p(d.getMonth() + 1)}/${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
  }
  function fmtTimeOnly(s) {
    if (!s) return '-'; const d = new Date(s); if (isNaN(d)) return s;
    const p = (n) => String(n).padStart(2, '0');
    return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
  }

  /* ── 이벤트 바인딩 ── */
  function bind() {
    $('st-search').addEventListener('input', (e) => { S.search = e.target.value; render(); });
    $('st-type').addEventListener('change', (e) => { S.filterType = e.target.value; render(); });
    $('st-health').addEventListener('change', (e) => { S.filterHealth = e.target.value; render(); });
    $('st-online').addEventListener('change', (e) => { S.filterOnline = e.target.value; render(); });
    $('st-refresh').addEventListener('click', load);
    $('st-detail-close').addEventListener('click', closeDetail);
  }

  document.addEventListener('DOMContentLoaded', async () => {
    if (window.Shell) await Shell.init({ active: '' });
    bind();
    await load();
    setInterval(load, 30000);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) load(); });
  });
})();
