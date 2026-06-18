/* ============================================================================
 * 대시보드(Overview) — Home.razor 의 위젯 구성을 정적 페이지로 이식.
 * GET /api/dashboard 스냅샷 1회 조회 → 전 위젯 렌더. 30초 폴링 + 탭 복귀 시 갱신.
 * ==========================================================================*/
(function () {
  'use strict';

  const HEALTH = {
    backedup:   { color: 'var(--health-backedup)',   label: '백업 갱신', chip: 'chip-success',    tile: 'heatmap-tile-success' },
    unchanged:  { color: 'var(--health-unchanged)',  label: '변경 없음', chip: 'chip-info',       tile: 'heatmap-tile-info' },
    failed:     { color: 'var(--health-failed)',     label: '작업 실패', chip: 'chip-error',      tile: 'heatmap-tile-error' },
    inprogress: { color: 'var(--health-inprogress)', label: '작업중',    chip: 'chip-warning',    tile: 'heatmap-tile-inprogress' },
    unknown:    { color: 'var(--health-unknown)',    label: '내역 없음', chip: 'chip-default',    tile: 'heatmap-tile-warning' },
  };
  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const $ = (id) => document.getElementById(id);

  async function load() {
    try {
      const res = await fetch('/api/dashboard', { headers: { 'Accept': 'application/json' } });
      if (!res.ok) return;
      render(await res.json());
    } catch (e) { /* 무시 */ }
  }

  function render(d) {
    renderKpi(d.kpi || {});
    renderHeatmap(d.heatmap || []);
    renderSegList('type-stats', d.typeStats || [], 'type');
    renderSegList('line-stats', d.lineStats || [], 'line');
    renderAttention(d.attention || []);
    renderActivities(d.activities || []);
    renderTimeline(d.schedule || [], d.today);
    renderDrive(d.drive);
    if (d.today) $('today-label').textContent = d.today;
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

  function renderHeatmap(tiles) {
    const host = $('heatmap');
    if (!tiles.length) { host.innerHTML = '<p class="text-on-surface-variant" style="padding:16px;">등록된 자산이 없습니다.</p>'; return; }
    host.innerHTML = tiles.map(t => {
      const h = HEALTH[t.health] || HEALTH.unknown;
      const net = t.offline ? '오프라인' : '온라인';
      const title = `[${t.name}]\n백업: ${h.label}\n네트워크: ${net}`;
      return `<a href="/assets/${t.assetId}" class="heatmap-tile ${h.tile}${t.offline ? ' heatmap-tile-offline' : ''}" title="${esc(title)}"></a>`;
    }).join('');
  }

  function renderSegList(hostId, stats, kind) {
    const host = $(hostId);
    if (!stats.length) { host.innerHTML = '<p class="text-on-surface-variant" style="font-size:13px;">데이터가 없습니다.</p>'; return; }
    const segDef = [
      ['backedUp', 'backedup'], ['unchanged', 'unchanged'], ['failed', 'failed'],
      ['inProgress', 'inprogress'], ['unknown', 'unknown'],
    ];
    host.innerHTML = stats.map(s => {
      const total = (s.backedUp || 0) + (s.unchanged || 0) + (s.failed || 0) + (s.inProgress || 0) + (s.unknown || 0);
      const segs = segDef.map(([key, hk]) => {
        const val = s[key] || 0; const pct = total > 0 ? 100 * val / total : 0;
        if (pct <= 0) return '';
        const link = `/history?tab=1&${kind === 'line' ? 'line' : 'type'}=${encodeURIComponent(s.name)}&health=${hk}`;
        return `<a href="${link}" class="seg-fill" style="width:${pct.toFixed(1)}%;background:${HEALTH[hk].color};" title="${esc(s.name)} ${HEALTH[hk].label} ${val}">${pct >= 15 ? Math.round(pct) + '%' : ''}</a>`;
      }).join('');
      const link = `/history?tab=1&${kind === 'line' ? 'line' : 'type'}=${encodeURIComponent(s.name)}`;
      return `<div class="seg-item"><a href="${link}" class="seg-item-header">
        <span class="seg-name">${esc(s.name)}</span><span class="seg-count">${total}</span></a>
        <div class="seg-track">${segs}</div></div>`;
    }).join('');
  }

  function renderAttention(list) {
    const host = $('attention');
    if (!list.length) {
      host.innerHTML = `<div class="chip chip-success" style="display:inline-flex;gap:6px;"><span class="material-symbols-outlined" style="font-size:16px;">check_circle</span>모든 자산이 정상입니다.</div>`;
      return;
    }
    host.innerHTML = `<table class="nm-table"><thead><tr>
      <th>자산명</th><th>타입</th><th>백업 상태</th><th>연속 실패</th></tr></thead><tbody>
      ${list.map(a => {
        const h = HEALTH[a.health] || HEALTH.unknown;
        return `<tr><td><a href="/assets/${a.assetId}">${esc(a.name)}</a></td>
          <td>${esc(a.typeName || '-')}</td>
          <td><span class="chip ${h.chip}">${esc(a.healthLabel || h.label)}</span></td>
          <td><span class="chip chip-error">${a.consecutiveFailureCount}회</span></td></tr>`;
      }).join('')}</tbody></table>`;
  }

  function renderActivities(list) {
    const host = $('activities');
    if (!list.length) { host.innerHTML = '<p class="text-on-surface-variant" style="font-size:13px;">활동 이력이 없습니다.</p>'; return; }
    host.innerHTML = list.slice(0, 6).map(a => {
      const cls = a.success === true ? 'activity-icon-success' : a.success === false ? 'activity-icon-error' : 'activity-icon-inprogress';
      const ico = a.success === true ? 'check' : a.success === false ? 'close' : 'hourglass_top';
      const href = a.assetId != null ? `/assets/${a.assetId}` : '#';
      return `<a href="${href}" class="activity-item">
        <div class="activity-icon ${cls}"><span class="material-symbols-outlined">${ico}</span></div>
        <div><div class="activity-title">${esc(a.assetName || '-')} ${esc(a.action || '')}</div>
        <div class="activity-time">${esc(fmtTime(a.timestamp))}</div></div></a>`;
    }).join('');
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

  document.addEventListener('DOMContentLoaded', async () => {
    if (window.Shell) await Shell.init({ active: 'overview' });
    await load();
    setInterval(load, 30000);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) load(); });
  });
})();
