/* ============================================================================
 * 대시보드(Overview) — Home.razor 의 위젯 구성을 정적 페이지로 이식.
 * GET /api/dashboard 스냅샷 1회 조회 → 전 위젯 렌더. 30초 폴링 + 탭 복귀 시 갱신.
 * ==========================================================================*/
(function () {
  'use strict';

  const HEALTH = {
    backedup:   { color: 'var(--health-backedup)',   label: '백업 갱신', chip: 'chip-success' },
    unchanged:  { color: 'var(--health-unchanged)',  label: '변경 없음', chip: 'chip-info' },
    failed:     { color: 'var(--health-failed)',     label: '작업 실패', chip: 'chip-error' },
    inprogress: { color: 'var(--health-inprogress)', label: '작업중',    chip: 'chip-warning' },
    unknown:    { color: 'var(--health-unknown)',    label: '내역 없음', chip: 'chip-default' },
  };
  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const $ = (id) => document.getElementById(id);
  let bpRenderer = null; // 전체 자산 분포도(도면) 렌더러 — 최초 1회 마운트
  let lastServerDay = null; // 서버가 마지막으로 내려준 오늘 날짜(yyyy-MM-dd) — 자정 변경 감지용
  const localDay = () => { const d = new Date(); const p = (n) => String(n).padStart(2, '0'); return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`; };
  // 서버가 내려준 yyyy-MM-dd 를 'xxxx년 xx월 xx일' 표기로 변환 (형식이 다르면 원본 그대로)
  const fmtKDate = (s) => { const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(String(s || '')); return m ? `${m[1]}년 ${m[2]}월 ${m[3]}일` : (s || ''); };
  const lastStats = { type: [], line: [] }; // 토글 시 재조회 없이 다시 그리기 위한 캐시
  const chartMode = (kind) => (localStorage.getItem('twms-dash-chart-' + kind) === 'donut') ? 'donut' : 'bar';
  const segDef = [
    ['backedUp', 'backedup'], ['unchanged', 'unchanged'], ['failed', 'failed'],
    ['inProgress', 'inprogress'], ['unknown', 'unknown'],
  ];

  // 주의 필요 자산 / 최근 작업: 다음·이전 버튼으로 페이지 단위 조회. 폴링 중에도 현재 페이지를 유지한다.
  const ATTN_KEY = 'twms-dash-attn-threshold'; // 연속 실패 기준(사용자 설정)
  const ATTN_PAGE_SIZE = 8;   // 주의 필요 자산: 페이지당 행 수
  const ACT_PAGE_SIZE = 7;    // 최근 작업: 페이지당 항목 수
  let attnList = [], attnPage = 0; // 주의 필요 자산 캐시 + 현재 페이지
  let actList = [], actPage = 0;   // 최근 작업 캐시 + 현재 페이지
  const attnThreshold = () => { const v = parseInt(localStorage.getItem(ATTN_KEY), 10); return (Number.isFinite(v) && v >= 1) ? Math.min(v, 100) : 3; };

  async function load() {
    try {
      const res = await fetch('/api/dashboard?failThreshold=' + attnThreshold(), { headers: { 'Accept': 'application/json' } });
      if (!res.ok) return;
      render(await res.json());
    } catch (e) { /* 무시 */ }
  }

  function render(d) {
    renderKpi(d.kpi || {});
    lastStats.type = d.typeStats || [];
    lastStats.line = d.lineStats || [];
    renderStats('type');
    renderStats('line');
    renderAttention(d.attention || [], d.attentionThreshold);
    renderActivities(d.activities || []);
    renderTimeline(d.schedule || [], d.today);
    renderDrive(d.drive);
    if (d.today) {
      lastServerDay = d.today;
      $('today-label').textContent = fmtKDate(d.today);
      const td = $('kpi-total-date');
      if (td) td.textContent = fmtKDate(d.today);
    }

    // 전체 자산 분포도(도면) — 최초 1회 마운트. 라인별/개별은 헤더 토글로 사용자가 직접 전환(선택 기억).
    if (!bpRenderer && window.LayoutRenderer) {
      bpRenderer = LayoutRenderer.mount({
        viewport: 'dash-bp-viewport',
        count: 'dash-bp-count',
        viewmode: 'dash-bp-viewmode',
        tabs: 'dash-bp-tabs',
        splitBtn: 'dash-bp-split',
        fullscreenBtn: 'dash-bp-fs',
        storeKey: 'twms-dash-bp-viewmode',
        splitStoreKey: 'twms-dash-bp-split',
        defaultMode: 0,
        poll: 30000,
      });
    }
  }

  function renderKpi(k) {
    $('kpi-total').textContent = (k.total ?? 0).toLocaleString();
    $('kpi-backed').textContent = (k.backedUp ?? 0).toLocaleString();
    $('kpi-unchanged').textContent = (k.unchanged ?? 0).toLocaleString();
    $('kpi-failed').textContent = (k.failed ?? 0).toLocaleString();
    $('kpi-backed-pct').textContent = (k.backedUpPct ?? 0) + '%';
    $('kpi-unchanged-pct').textContent = (k.unchangedPct ?? 0) + '%';
    $('kpi-failed-pct').textContent = (k.failedPct ?? 0) + '%';
  }

  // 타입/라인별 현황을 막대 또는 도넛으로 그린다. 모드는 localStorage 에 사용자별 저장.
  function renderStats(kind) {
    const host = $(kind + '-stats');
    const stats = lastStats[kind] || [];
    const mode = chartMode(kind);
    // 토글 버튼 active 상태 동기화
    document.querySelectorAll(`.chart-toggle[data-chart="${kind}"] .chart-toggle-btn`).forEach(b => {
      b.classList.toggle('is-active', b.dataset.mode === mode);
    });
    host.className = mode === 'donut' ? 'donut-grid' : 'seg-list';
    if (!stats.length) { host.innerHTML = '<p class="text-on-surface-variant" style="font-size:13px;">데이터가 없습니다.</p>'; return; }
    host.innerHTML = stats.map(s => mode === 'donut' ? donutItem(s, kind) : barItem(s, kind)).join('');
  }

  const statTotal = (s) => (s.backedUp || 0) + (s.unchanged || 0) + (s.failed || 0) + (s.inProgress || 0) + (s.unknown || 0);
  const statLink = (s, kind) => `/history?tab=0&${kind === 'line' ? 'line' : 'type'}=${encodeURIComponent(s.name)}`;

  function barItem(s, kind) {
    const total = statTotal(s);
    const segs = segDef.map(([key, hk]) => {
      const val = s[key] || 0; const pct = total > 0 ? 100 * val / total : 0;
      if (pct <= 0) return '';
      return `<a href="${statLink(s, kind)}&health=${hk}" class="seg-fill" style="width:${pct.toFixed(1)}%;background:${HEALTH[hk].color};" title="${esc(s.name)} ${HEALTH[hk].label} ${val}">${pct >= 15 ? Math.round(pct) + '%' : ''}</a>`;
    }).join('');
    return `<div class="seg-item"><a href="${statLink(s, kind)}" class="seg-item-header">
      <span class="seg-name">${esc(s.name)}</span><span class="seg-count">${total}</span></a>
      <div class="seg-track">${segs}</div></div>`;
  }

  function donutItem(s, kind) {
    const total = statTotal(s);
    const r = 26, c = 32, sw = 11, circ = 2 * Math.PI * r;
    let offset = 0;
    const arcs = segDef.map(([key, hk]) => {
      const val = s[key] || 0; if (val <= 0 || total <= 0) return '';
      const len = (val / total) * circ;
      const el = `<circle cx="${c}" cy="${c}" r="${r}" fill="none" stroke="${HEALTH[hk].color}" stroke-width="${sw}"
        stroke-dasharray="${len.toFixed(2)} ${(circ - len).toFixed(2)}" stroke-dashoffset="${(-offset).toFixed(2)}"
        transform="rotate(-90 ${c} ${c})"><title>${esc(s.name)} ${HEALTH[hk].label} ${val}</title></circle>`;
      offset += len;
      return el;
    }).join('');
    const ring = arcs || `<circle cx="${c}" cy="${c}" r="${r}" fill="none" stroke="var(--c-surface-container-high)" stroke-width="${sw}"/>`;
    return `<a href="${statLink(s, kind)}" class="donut-item" title="${esc(s.name)} ${total}">
      <div class="donut-wrap"><svg viewBox="0 0 64 64" width="64" height="64">${ring}</svg>
        <span class="donut-center">${total}</span></div>
      <span class="donut-label">${esc(s.name)}</span></a>`;
  }

  // 주의 필요 자산: 서버가 임계값 이상 전체를 내려주고, 여기서 다음/이전으로 페이지 단위 표시.
  function renderAttention(list, threshold) {
    attnList = list || [];
    if (threshold) {
      const sub = $('attn-subtitle');
      if (sub) sub.textContent = `연속 ${threshold}회 이상 실패`;
    }
    paintAttention();
  }

  function paintAttention() {
    const host = $('attention');
    const list = attnList;
    if (!list.length) {
      host.innerHTML = `<div class="chip chip-success" style="display:inline-flex;gap:6px;"><span class="material-symbols-outlined" style="font-size:16px;">check_circle</span>해당 기준의 주의 필요 자산이 없습니다.</div>`;
      renderPager('attn-pager', 0, 1, 0, null);
      return;
    }
    const pages = Math.max(1, Math.ceil(list.length / ATTN_PAGE_SIZE));
    if (attnPage >= pages) attnPage = pages - 1;
    if (attnPage < 0) attnPage = 0;
    const items = list.slice(attnPage * ATTN_PAGE_SIZE, (attnPage + 1) * ATTN_PAGE_SIZE);
    host.innerHTML = `<table class="nm-table"><thead><tr>
      <th>자산명</th><th>타입</th><th>백업 상태</th><th>연속 실패</th></tr></thead><tbody>
      ${items.map(a => {
        const h = HEALTH[a.health] || HEALTH.unknown;
        return `<tr><td><a href="/assets/${a.assetId}">${esc(a.name)}</a></td>
          <td>${esc(a.typeName || '-')}</td>
          <td><span class="chip ${h.chip}">${esc(a.healthLabel || h.label)}</span></td>
          <td><span class="chip chip-error">${a.consecutiveFailureCount}회</span></td></tr>`;
      }).join('')}</tbody></table>`;
    renderPager('attn-pager', attnPage, pages, list.length, (p) => { attnPage = p; paintAttention(); });
  }

  // 최근 작업: 오늘 하루 전체를 다음/이전으로 페이지 단위 표시.
  function renderActivities(list) {
    actList = list || [];
    paintActivities();
  }

  function paintActivities() {
    const host = $('activities');
    const list = actList;
    if (!list.length) {
      host.innerHTML = '<p class="text-on-surface-variant" style="font-size:13px;">오늘 작업 이력이 없습니다.</p>';
      renderPager('act-pager', 0, 1, 0, null);
      return;
    }
    const pages = Math.max(1, Math.ceil(list.length / ACT_PAGE_SIZE));
    if (actPage >= pages) actPage = pages - 1;
    if (actPage < 0) actPage = 0;
    const items = list.slice(actPage * ACT_PAGE_SIZE, (actPage + 1) * ACT_PAGE_SIZE);
    host.innerHTML = items.map(a => {
      const cls = a.success === true ? 'activity-icon-success' : a.success === false ? 'activity-icon-error' : 'activity-icon-inprogress';
      const ico = a.success === true ? 'check' : a.success === false ? 'close' : 'hourglass_top';
      const href = a.assetId != null ? `/assets/${a.assetId}` : '#';
      return `<a href="${href}" class="activity-item">
        <div class="activity-icon ${cls}"><span class="material-symbols-outlined">${ico}</span></div>
        <div><div class="activity-title">${esc(a.assetName || '-')} ${esc(a.action || '')}</div>
        <div class="activity-time">${esc(fmtTime(a.timestamp))}</div></div></a>`;
    }).join('');
    renderPager('act-pager', actPage, pages, list.length, (p) => { actPage = p; paintActivities(); });
  }

  // 다음/이전 페이저 (history 페이지와 동일 동작). total===0 이면 비운다.
  function renderPager(hostId, page, pages, total, go) {
    const host = $(hostId);
    if (!host) return;
    if (total === 0) { host.innerHTML = ''; return; }
    host.innerHTML = `
      <button class="hist-iconbtn" ${page <= 0 ? 'disabled' : ''} data-act="prev"><span class="material-symbols-outlined">chevron_left</span></button>
      <span>${page + 1} / ${pages} <span style="opacity:0.6;">(${total}건)</span></span>
      <button class="hist-iconbtn" ${page >= pages - 1 ? 'disabled' : ''} data-act="next"><span class="material-symbols-outlined">chevron_right</span></button>`;
    const prev = host.querySelector('[data-act="prev"]');
    const next = host.querySelector('[data-act="next"]');
    if (prev) prev.addEventListener('click', () => { if (page > 0) go(page - 1); });
    if (next) next.addEventListener('click', () => { if (page < pages - 1) go(page + 1); });
  }

  function renderTimeline(schedule, today) {
    const host = $('timeline');
    if (!schedule.length) { host.innerHTML = '<p class="text-on-surface-variant" style="padding:8px;">오늘 백업 이력이 없습니다.</p>'; return; }

    const mins = (s) => { const d = new Date(s); return d.getHours() * 60 + d.getMinutes(); };
    const now = new Date(); const nowMin = now.getHours() * 60 + now.getMinutes();
    const starts = schedule.map(e => mins(e.started));
    let axisStart = Math.max(0, (Math.floor(Math.min(...starts) / 60) - 1) * 60);
    const ends = schedule.map(e => {
      const end = e.finished ? new Date(e.finished) : (e.inProgress ? now : new Date(new Date(e.started).getTime() + 5 * 60000));
      return end.getHours() * 60 + end.getMinutes();
    });
    let axisEnd = Math.min(Math.max(Math.max(...ends), nowMin) + 60, 1440);
    if (axisEnd <= axisStart) axisEnd = Math.min(axisStart + 120, 1440);
    const range = axisEnd - axisStart;
    const pct = (m) => Math.max(0, Math.min(100, (m - axisStart) / range * 100));
    const tick = range <= 180 ? 30 : range <= 480 ? 60 : 120;

    // 레인 패킹 (겹침 방지)
    const bars = schedule.map(e => {
      const sM = mins(e.started);
      let cls, end;
      if (e.inProgress) { cls = 'timeline-bar-inprogress'; end = nowMin; }
      else if (e.success === true) { cls = 'timeline-bar-success'; end = e.finished ? mins(e.finished) : sM + 1; }
      else { cls = 'timeline-bar-failed'; end = e.finished ? mins(e.finished) : sM + 5; }
      const left = pct(sM), right = pct(end), width = Math.max(right - left, 0.3);
      const status = e.inProgress ? '진행중' : e.success === true ? '성공' : '실패';
      return { assetId: e.assetId, left, width, cls, lane: 0, tip: `[${status}] ${e.assetName}` };
    }).sort((a, b) => a.left - b.left);
    const laneEnds = [];
    bars.forEach(b => {
      let placed = false;
      for (let i = 0; i < laneEnds.length; i++) { if (b.left >= laneEnds[i]) { b.lane = i; laneEnds[i] = b.left + b.width; placed = true; break; } }
      if (!placed) { b.lane = laneEnds.length; laneEnds.push(b.left + b.width); }
    });
    const laneCount = laneEnds.length || 1;
    const stride = laneCount <= 1 ? 20 : laneCount === 2 ? 12 : laneCount === 3 ? 8 : 7;
    const barH = laneCount <= 1 ? 16 : laneCount === 2 ? 10 : laneCount === 3 ? 6 : 5;
    const trackH = Math.max(24, laneCount * stride + 4);

    let ruler = '';
    for (let m = axisStart; m <= axisEnd; m += tick) {
      const label = m % 60 === 0 ? String(m / 60).padStart(2, '0') : String(Math.floor(m / 60)).padStart(2, '0') + ':30';
      ruler += `<span class="timeline-tick" style="left:${pct(m).toFixed(2)}%;">${label}</span>`;
    }
    const barEls = bars.map(b => `<a href="/assets/${b.assetId}" class="timeline-bar ${b.cls}" title="${esc(b.tip)}"
      style="left:${b.left.toFixed(2)}%;width:${b.width.toFixed(2)}%;top:${2 + b.lane * stride}px;height:${barH}px;"></a>`).join('');

    host.innerHTML = `<div class="timeline"><div class="timeline-ruler">${ruler}</div>
      <div class="timeline-track" style="min-height:${trackH}px;">
        <div class="timeline-now" style="left:${pct(nowMin).toFixed(2)}%;"></div>${barEls}</div></div>`;
  }

  function renderDrive(drive) {
    const host = $('drive');
    if (!drive) { host.innerHTML = '<p class="text-on-surface-variant">드라이브 정보를 가져올 수 없습니다.</p>'; return; }
    const cls = drive.usedPct >= 90 ? 'drive-bar-danger' : drive.usedPct >= 75 ? 'drive-bar-warning' : 'drive-bar-ok';
    host.innerHTML = `<div class="drive-track">
        <div class="drive-fill ${cls}" style="width:${Math.max(0, Math.min(100, drive.usedPct)).toFixed(1)}%;">${drive.usedPct >= 8 ? drive.usedPct.toFixed(1) + '%' : ''}</div></div>
      <div class="drive-numbers"><span>사용 <strong>${fmtGB(drive.usedBytes)}</strong></span>
        <span>여유 <strong>${fmtGB(drive.freeBytes)}</strong></span>
        <span>전체 <strong>${fmtGB(drive.totalBytes)}</strong></span></div>`;
  }

  function fmtGB(b) { if (b == null) return '-'; const gb = b / 1e9; return gb >= 1 ? gb.toFixed(1) + ' GB' : (b / 1e6).toFixed(0) + ' MB'; }
  function fmtTime(s) { if (!s) return ''; const d = new Date(s); if (isNaN(d)) return s; const p = (n) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`; }

  // 차트 토글: 사용자가 막대/도넛 선택 → localStorage 저장 후 캐시로 즉시 재렌더(재조회 X)
  function bindChartToggles() {
    document.querySelectorAll('.chart-toggle').forEach(group => {
      const kind = group.dataset.chart;
      const cur = chartMode(kind);
      group.querySelectorAll('.chart-toggle-btn').forEach(b => b.classList.toggle('is-active', b.dataset.mode === cur));
      group.addEventListener('click', (e) => {
        const btn = e.target.closest('.chart-toggle-btn');
        if (!btn) return;
        localStorage.setItem('twms-dash-chart-' + kind, btn.dataset.mode);
        renderStats(kind);
      });
    });
  }

  // 전체 자산 분포도 높이 조절: 우측 하단 앵커를 드래그해 높이를 조절하고 localStorage 에 기억한다.
  // (높이는 CSS 변수 --dash-bp-h 로 적용 → 전체화면 100% 규칙이 그대로 우선한다)
  function bindBpResize() {
    const vp = $('dash-bp-viewport');
    const handle = $('dash-bp-resize');
    if (!vp || !handle) return;
    const KEY = 'twms-dash-bp-height';
    const MIN = 220, MAX = 1600;
    const clamp = (v) => Math.max(MIN, Math.min(MAX, v));

    // 저장값 복원
    const saved = parseInt(localStorage.getItem(KEY), 10);
    if (Number.isFinite(saved)) vp.style.setProperty('--dash-bp-h', clamp(saved) + 'px');

    let startY = 0, startH = 0, dragging = false;
    const onMove = (e) => {
      if (!dragging) return;
      vp.style.setProperty('--dash-bp-h', clamp(startH + (e.clientY - startY)) + 'px');
    };
    const onUp = (e) => {
      if (!dragging) return;
      dragging = false;
      handle.classList.remove('is-dragging');
      document.body.style.userSelect = '';
      document.body.style.cursor = '';
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onUp);
      const h = clamp(startH + (e.clientY - startY));
      try { localStorage.setItem(KEY, String(h)); } catch (_) { /* 무시 */ }
    };
    handle.addEventListener('pointerdown', (e) => {
      e.preventDefault();
      dragging = true;
      startY = e.clientY;
      startH = vp.getBoundingClientRect().height; // 현재 렌더 높이(기본 clamp 또는 저장값)에서 시작
      handle.classList.add('is-dragging');
      document.body.style.userSelect = 'none';
      document.body.style.cursor = 'ns-resize';
      window.addEventListener('pointermove', onMove);
      window.addEventListener('pointerup', onUp);
    });
    // 더블클릭 → 기본 높이로 복원
    handle.addEventListener('dblclick', () => {
      vp.style.removeProperty('--dash-bp-h');
      try { localStorage.removeItem(KEY); } catch (_) { /* 무시 */ }
    });
  }

  // 우측 상단 메뉴: 연속 실패 기준을 사용자가 설정 → localStorage 저장 후 새 기준으로 재조회.
  function bindAttnMenu() {
    const btn = $('attn-menu-btn');
    const menu = $('attn-menu');
    const input = $('attn-threshold-input');
    if (!btn || !menu || !input) return;
    input.value = attnThreshold();
    const apply = (raw) => {
      const n = Math.max(1, Math.min(100, parseInt(raw, 10) || 3));
      input.value = n;
      if (n === attnThreshold()) return; // 변화 없음 → 재조회 생략
      try { localStorage.setItem(ATTN_KEY, String(n)); } catch (_) { /* 무시 */ }
      attnPage = 0; // 기준이 바뀌면 첫 페이지부터
      load();       // 새 기준으로 재조회
    };
    btn.addEventListener('click', (e) => {
      e.stopPropagation();
      menu.hidden = !menu.hidden;
      if (!menu.hidden) { input.value = attnThreshold(); input.focus(); input.select(); }
    });
    menu.addEventListener('click', (e) => e.stopPropagation());
    menu.querySelectorAll('[data-attn-step]').forEach(b =>
      b.addEventListener('click', () => apply((parseInt(input.value, 10) || attnThreshold()) + parseInt(b.dataset.attnStep, 10))));
    input.addEventListener('change', () => apply(input.value));
    input.addEventListener('keydown', (e) => { if (e.key === 'Enter') { apply(input.value); menu.hidden = true; } });
    document.addEventListener('click', () => { menu.hidden = true; });
  }

  // 전체 자산 분포도 편집 버튼: 관리자에게만 노출 → 현재 표시 중인 레이아웃 편집기로 이동.
  function bindBpEdit() {
    const btn = $('dash-bp-edit');
    if (!btn) return;
    const reveal = () => { btn.hidden = !(window.Shell && Shell.isAdmin); };
    reveal();
    document.addEventListener('shell:auth', reveal);
    btn.addEventListener('click', async () => {
      let id = bpRenderer && bpRenderer.getLayoutId && bpRenderer.getLayoutId();
      if (!id) {
        try { const r = await fetch('/api/layout', { headers: { 'Accept': 'application/json' } }); if (r.ok) id = (await r.json()).selectedLayoutId; } catch (_) { /* 무시 */ }
      }
      location.href = id ? `/admin/layout/${id}/edit` : '/admin/layout';
    });
  }

  document.addEventListener('DOMContentLoaded', async () => {
    if (window.Shell) await Shell.init({ active: 'overview' });
    bindChartToggles();
    bindBpResize();
    bindBpEdit();
    bindAttnMenu();
    await load();
    setInterval(load, 30000);
    // 자정이 지나 날짜가 바뀌면 카드 날짜를 즉시 갱신하고 데이터를 새로 받는다.
    setInterval(() => {
      if (lastServerDay && localDay() !== lastServerDay) load();
    }, 60000);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) load(); });
  });
})();
