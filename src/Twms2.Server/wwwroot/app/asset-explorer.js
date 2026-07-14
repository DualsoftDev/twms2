/* ============================================================================
 * 자산 관리 랜딩(AssetExplorer) — 자산을 선택하지 않은 상태의 탐색 페이지.
 * 사이드바 트리는 shell.js(/api/nav)가 렌더하므로, 여기서는
 * KPI 요약 + 검색 가능한 자산 카드 목록(클릭 → /assets/{id})만 제공.
 * GET /api/assets. 30초 폴링 + 탭 복귀 시 갱신.
 * ==========================================================================*/
(function () {
  'use strict';

  const HEALTH = {
    backedup:   { label: '백업 갱신', chip: 'chip-success', border: 'border-backedup' },
    unchanged:  { label: '변경 없음', chip: 'chip-info',    border: 'border-unchanged' },
    failed:     { label: '작업 실패', chip: 'chip-error',   border: 'border-failed' },
    inprogress: { label: '작업중',    chip: 'chip-warning', border: 'border-inprogress' },
    unknown:    { label: '내역 없음', chip: 'chip-default', border: '' },
  };

  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const $ = (id) => document.getElementById(id);

  const S = {
    assets: [], typeNames: [], lineNames: [],
    search: '', filterType: '', filterLine: '', filterHealth: '',
    filterIds: null,   // 도면 묶음 클릭 진입 시(?ids=) 그 멤버만 표시. null = 미적용
  };

  /* ── URL 쿼리 복원 (도면 묶음 클릭이 /assets?ids=1,2,3 등으로 진입) ── */
  function applyQuery() {
    const q = new URLSearchParams(location.search);
    const ids = q.get('ids');
    if (ids) {
      const set = new Set(ids.split(',').map(s => parseInt(s, 10)).filter(n => !Number.isNaN(n)));
      if (set.size) S.filterIds = set;
    }
    if (q.get('type')) S.filterType = q.get('type');
    if (q.get('line')) S.filterLine = q.get('line');
    if (q.get('health')) S.filterHealth = q.get('health');
    if (q.get('q')) S.search = q.get('q');
  }

  function fmtShort(s) {
    if (!s) return '';
    const d = new Date(s); if (isNaN(d)) return '';
    const p = (n) => String(n).padStart(2, '0');
    return `${p(d.getMonth() + 1)}/${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
  }

  // 타입명 → 아이콘 파일 (AssetStatusCard.GetTypeIcon 이식)
  function typeIcon(a) {
    const t = (a.typeName || '').toLowerCase();
    if (t.includes('robot')) return 'robot.png';
    if (t.includes('plc') && a.isRobotPlc) return 'robot.png';
    if (t.includes('plc')) return 'plc.png';
    if (t.includes('servo')) return 'servo.png';
    if (t.includes('drive')) return 'drive.png';
    if (t.includes('hmi') || t.includes('xp')) return 'hmi.png';
    if (t.includes('ftp')) return 'ftp.png';
    return '';
  }

  async function load() {
    try {
      const res = await fetch('/api/assets', { headers: { 'Accept': 'application/json' } });
      if (!res.ok) return;
      const d = await res.json();
      S.assets = d.assets || [];
      S.typeNames = d.typeNames || [];
      S.lineNames = d.lineNames || [];
      fillKpi(d.kpi || {});
      fillSelects();
      render();
    } catch (e) { /* 무시 */ }
  }

  function fillKpi(k) {
    $('kpi-total').textContent = k.total ?? '–';
    $('kpi-backed').textContent = k.backedUp ?? '–';
    $('kpi-unchanged').textContent = k.unchanged ?? '–';
    $('kpi-failed').textContent = k.failed ?? '–';
    $('kpi-offline').textContent = k.offline ?? '–';
  }

  function fillSelects() {
    $('ax-type').innerHTML = '<option value="">전체 타입</option>' +
      S.typeNames.map(t => `<option value="${esc(t)}"${t === S.filterType ? ' selected' : ''}>${esc(t)}</option>`).join('');
    $('ax-line').innerHTML = '<option value="">전체 라인</option>' +
      S.lineNames.map(l => `<option value="${esc(l)}"${l === S.filterLine ? ' selected' : ''}>${esc(l)}</option>`).join('');
  }

  function filtered() {
    const term = (S.search || '').trim().toLowerCase();
    return S.assets.filter(a => {
      if (S.filterIds && !S.filterIds.has(a.assetId)) return false;
      if (term) {
        const hay = [a.name, a.ip].filter(v => v != null).map(v => String(v).toLowerCase());
        if (!hay.some(v => v.includes(term))) return false;
      }
      if (S.filterType && a.typeName !== S.filterType) return false;
      if (S.filterLine && a.lineName !== S.filterLine) return false;
      if (S.filterHealth && a.health !== S.filterHealth) return false;
      return true;
    });
  }

  // 묶음(?ids) 진입 시 "선택한 자산만 표시" 안내 + 전체 보기 해제 버튼.
  function renderNotice() {
    const el = $('ax-notice');
    if (!el) return;
    if (!S.filterIds) { el.style.display = 'none'; el.innerHTML = ''; return; }
    el.style.display = 'flex';
    el.innerHTML = `<span class="material-symbols-outlined">filter_alt</span>`
      + `<span>도면 묶음에서 선택한 자산 ${S.filterIds.size}개만 표시 중</span>`
      + `<button type="button" class="ax-notice-clear" id="ax-notice-clear">전체 보기</button>`;
    $('ax-notice-clear').addEventListener('click', () => {
      S.filterIds = null;
      try { history.replaceState(null, '', '/assets'); } catch (e) { /* 무시 */ }
      renderNotice();
      render();
    });
  }

  function render() {
    renderNotice();
    const rows = filtered();
    $('ax-count').textContent = `${rows.length} 건`;
    if (rows.length === 0) {
      $('ax-list').innerHTML = `<div class="ax-empty">조건에 맞는 자산이 없습니다.</div>`;
      return;
    }
    const cards = rows.map(a => {
      const h = HEALTH[a.health] || HEALTH.unknown;
      const icon = typeIcon(a);
      const iconHtml = icon
        ? `<img src="/images/icons/${icon}" alt="" />`
        : `<span class="material-symbols-outlined">devices</span>`;
      const dotCls = a.pingReachable == null ? 'unknown' : (a.pingReachable ? 'on' : 'off');
      const dotTitle = a.pingReachable == null ? 'Ping 미확인' : (a.pingReachable ? '온라인' : '오프라인');
      // agentName 없음 = 에이전트 미지정 → DEXA 가 백업 시 자동 선택
      const agentIcon = a.agentOnline
        ? `<span class="material-symbols-outlined" title="에이전트 온라인" style="font-size:18px;color:var(--health-backedup);">cloud</span>`
        : a.agentName
          ? `<span class="material-symbols-outlined" title="에이전트 오프라인" style="font-size:18px;color:var(--c-on-surface-variant);">cloud_off</span>`
          : `<span class="material-symbols-outlined" title="에이전트 자동 (미지정)" style="font-size:18px;color:var(--c-on-surface-variant);">cloud_sync</span>`;
      const time = fmtShort(a.lastBackupTime);
      const sub = [esc(a.typeName || ''), a.ip ? esc(a.ip) : ''].filter(Boolean).join(' · ');
      return `<a class="ax-asset-card ${h.border}" href="/assets/${a.assetId}">
        <div class="ax-card-top">
          <div class="ax-card-icon">${iconHtml}</div>
          <div style="flex:1;min-width:0;">
            <div class="ax-card-name">${esc(a.name)}</div>
            <div class="ax-card-sub">${sub}</div>
          </div>
          <span class="ax-dot ${dotCls}" title="${dotTitle}"></span>
        </div>
        <div class="ax-card-bottom">
          <span class="chip ${h.chip}">${esc(a.healthLabel || h.label)}</span>
          ${time ? `<span class="ax-card-time">${time}</span>` : ''}
          <span style="flex:1;"></span>
          ${agentIcon}
        </div>
      </a>`;
    }).join('');
    $('ax-list').innerHTML = `<div class="ax-grid">${cards}</div>`;
  }

  function bind() {
    $('ax-search').addEventListener('input', (e) => { S.search = e.target.value; render(); });
    $('ax-type').addEventListener('change', (e) => { S.filterType = e.target.value; render(); });
    $('ax-line').addEventListener('change', (e) => { S.filterLine = e.target.value; render(); });
    $('ax-health').addEventListener('change', (e) => { S.filterHealth = e.target.value; render(); });
  }

  document.addEventListener('DOMContentLoaded', async () => {
    if (window.Shell) await Shell.init({ active: '' });
    applyQuery();
    bind();
    if (S.search) $('ax-search').value = S.search;
    if (S.filterHealth) $('ax-health').value = S.filterHealth;
    await load();
    setInterval(load, 30000);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) load(); });
  });
})();
