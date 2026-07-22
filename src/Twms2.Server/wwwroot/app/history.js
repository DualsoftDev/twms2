/* ============================================================================
 * 자산 통합조회(History) — ActionHistory.razor 의 3개 탭(자산정보/백업이력/통신이력)을
 * 정적 페이지로 이식. GET /api/history(자산+백업) + GET /api/history/pings(통신).
 * 클라이언트 필터/정렬/탭전환 + CSV(Blob) + URL 쿼리(tab/health/type/line 등) 복원.
 * 30초 폴링 + 탭 복귀 시 갱신.
 * ==========================================================================*/
(function () {
  'use strict';

  // ── 백업 상태 매핑 (dashboard.js 와 동일 톤) ──
  const HEALTH = {
    backedup:   { label: '백업 갱신', chip: 'chip-success', icon: 'check_circle' },
    unchanged:  { label: '변경 없음', chip: 'chip-info',    icon: 'remove' },
    failed:     { label: '작업 실패', chip: 'chip-error',   icon: 'error' },
    inprogress: { label: '작업중',    chip: 'chip-warning', icon: 'hourglass_top' },
    unknown:    { label: '내역 없음', chip: 'chip-default', icon: 'help' },
  };
  // 백업 이력 결과(액션) 매핑 — incomplete(미완료)는 별도 칩
  const RESULT = {
    backedup:   { label: '백업 갱신', chip: 'chip-success', icon: 'check_circle' },
    unchanged:  { label: '변경 없음', chip: 'chip-info',    icon: 'remove' },
    failed:     { label: '작업 실패', chip: 'chip-error',   icon: 'error' },
    incomplete: { label: '미완료',    chip: 'chip-error',   icon: 'hourglass_disabled' },
    inprogress: { label: '작업중',    chip: 'chip-warning', icon: 'hourglass_top' },
  };
  // 백업 로그 레벨 → 칩 클래스 (BackupLog.razor.GetLevelColor 이식)
  const LOG_LEVEL = {
    INFO:  'chip-info',
    WARN:  'chip-warning',
    ERROR: 'chip-error',
    FATAL: 'chip-error',
    DEBUG: 'chip-default',
  };

  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const $ = (id) => document.getElementById(id);

  // ── 전역 상태 ──
  const S = {
    activeTab: 0,
    searchText: '', searchMode: 'contains',
    // 탭1 자산
    assets: [], typeNames: [], lineNames: [],
    filterType: '', filterLine: '', filterHealth: '',
    aSort: { key: 'name', dir: 1 }, aPage: 0, aPageSize: 25,
    // 탭2 백업
    actions: [], selectedResult: '',
    bSort: { key: 'started', dir: -1 }, bPage: 0, bPageSize: 25,
    // 탭3 통신
    pings: [], pingStatus: '',
    pSort: { key: 'checkedAt', dir: -1 }, pPage: 0, pPageSize: 25,
    // 기간 (백업/통신 공유)
    periodLabel: 'today',
    startDate: todayStr(), endDate: todayStr(),
    // assetId → {name,typeName,lineName,ip}
    meta: {},
  };

  function todayStr() {
    const d = new Date(); const p = (n) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
  }
  function addDaysStr(days) {
    const d = new Date(); d.setDate(d.getDate() + days); const p = (n) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
  }

  /* ── URL 쿼리 복원 (대시보드가 /history?tab=1&health=backedup 등으로 진입) ── */
  function applyQuery() {
    const q = new URLSearchParams(location.search);
    const tab = parseInt(q.get('tab'), 10);
    S.activeTab = Number.isInteger(tab) ? tab : 0;
    if (q.get('line')) { S.filterLine = q.get('line'); }
    if (q.get('type')) { S.filterType = q.get('type'); }
    if (q.get('health')) S.filterHealth = q.get('health');
    if (q.get('result')) S.selectedResult = q.get('result');
    if (q.get('pstatus')) S.pingStatus = q.get('pstatus');
    if (q.get('q')) S.searchText = q.get('q');
    if (q.get('mode')) S.searchMode = q.get('mode');
    if (q.get('period')) S.periodLabel = q.get('period');
    const days = S.periodLabel === '7days' ? 7 : S.periodLabel === '30days' ? 30 : 0;
    if (S.periodLabel && S.periodLabel !== '') { setPeriodDates(days); }
    if (q.get('start')) S.startDate = q.get('start');
    if (q.get('end')) S.endDate = q.get('end');
  }

  function setPeriodDates(days) {
    S.endDate = todayStr();
    S.startDate = days === 0 ? todayStr() : addDaysStr(-(days - 1));
  }

  /* ── URL 동기화 (필터 변경 시 — replaceState, Razor.UpdateUrl 이식) ── */
  function syncUrl() {
    const q = new URLSearchParams();
    if (S.activeTab !== 0) q.set('tab', String(S.activeTab));
    if (S.filterLine) q.set('line', S.filterLine);
    if (S.filterType) q.set('type', S.filterType);
    if (S.periodLabel && S.periodLabel !== 'today') q.set('period', S.periodLabel);
    if (S.startDate !== todayStr()) q.set('start', S.startDate);
    if (S.endDate !== todayStr()) q.set('end', S.endDate);
    if (S.searchText) q.set('q', S.searchText);
    if (S.searchMode !== 'contains') q.set('mode', S.searchMode);
    if (S.selectedResult) q.set('result', S.selectedResult);
    if (S.pingStatus) q.set('pstatus', S.pingStatus);
    if (S.filterHealth) q.set('health', S.filterHealth);
    const qs = q.toString();
    history.replaceState(null, '', '/history' + (qs ? '?' + qs : ''));
  }

  /* ── 데이터 로드 ── */
  async function load() {
    try {
      // 백업 이력은 현재 선택 기간만 서버에서 받는다 (30초 폴링 응답 크기 절감)
      const url = `/api/history?start=${encodeURIComponent(S.startDate)}&end=${encodeURIComponent(S.endDate)}`;
      const res = await fetch(url, { headers: { 'Accept': 'application/json' } });
      if (!res.ok) return;
      const d = await res.json();
      S.assets = d.assets || [];
      S.actions = d.actions || [];
      S.typeNames = d.typeNames || [];
      S.lineNames = d.lineNames || [];
      S.meta = d.assetMeta || {};
      fillSelectOptions();
      renderActiveTab();
    } catch (e) { /* 무시 */ }
  }

  async function loadPings() {
    try {
      const url = `/api/history/pings?start=${encodeURIComponent(S.startDate)}&end=${encodeURIComponent(S.endDate)}`;
      const res = await fetch(url, { headers: { 'Accept': 'application/json' } });
      if (!res.ok) return;
      const d = await res.json();
      S.pings = d.pings || [];
      if (S.activeTab === 2) renderPings();
    } catch (e) { /* 무시 */ }
  }

  function fillSelectOptions() {
    // 로봇 PLC 가 있으면(또는 현재 필터가 로봇이면) '자산별 현황' 차트 드릴다운과 맞물리도록 합성 옵션 노출
    const hasRobot = S.assets.some(a => a.isRobotPlc) || S.filterType === ROBOT_TYPE;
    const typeOpts = '<option value="">전체 타입</option>' +
      S.typeNames.map(t => `<option value="${esc(t)}"${t === S.filterType ? ' selected' : ''}>${esc(t)}</option>`).join('') +
      (hasRobot ? `<option value="${ROBOT_TYPE}"${S.filterType === ROBOT_TYPE ? ' selected' : ''}>Robot PLC</option>` : '');
    $('a-type').innerHTML = typeOpts;
    const lineOpts = (sel) => '<option value="">전체 라인</option>' +
      S.lineNames.map(l => `<option value="${esc(l)}"${l === sel ? ' selected' : ''}>${esc(l)}</option>`).join('');
    $('a-line').innerHTML = lineOpts(S.filterLine);
    $('b-line').innerHTML = lineOpts(S.filterLine);
    $('p-line').innerHTML = lineOpts(S.filterLine);
  }

  /* ── 검색 매칭 (Razor 의 contains/exact/exclude 이식) ── */
  function matchSearch(fields) {
    const term = (S.searchText || '').trim();
    if (!term) return true;
    const lc = term.toLowerCase();
    const vals = fields.filter(v => v != null).map(v => String(v));
    if (S.searchMode === 'exact') {
      // 단어 단위 정확 일치
      return vals.some(v => v.split(/\s+/).some(w => w.toLowerCase() === lc));
    }
    if (S.searchMode === 'exclude') {
      return !vals.some(v => v.toLowerCase().includes(lc));
    }
    return vals.some(v => v.toLowerCase().includes(lc));
  }

  /* ── 정렬 ── */
  function sortRows(rows, sort) {
    const { key, dir } = sort;
    return rows.slice().sort((a, b) => {
      let x = a[key], y = b[key];
      if (x == null && y == null) return 0;
      if (x == null) return 1; if (y == null) return -1;
      if (typeof x === 'number' && typeof y === 'number') return (x - y) * dir;
      if (typeof x === 'boolean') { x = x ? 1 : 0; y = y ? 1 : 0; return (x - y) * dir; }
      // 날짜 ISO 문자열 / 일반 문자열
      return String(x).localeCompare(String(y), 'ko') * dir;
    });
  }

  function thHtml(cols, sort) {
    return '<thead><tr>' + cols.map(c => {
      const arrow = c.key && c.key === sort.key ? `<span class="sort-arrow material-symbols-outlined">${sort.dir === 1 ? 'arrow_drop_up' : 'arrow_drop_down'}</span>` : '';
      return `<th data-key="${c.key || ''}">${esc(c.title)}${arrow}</th>`;
    }).join('') + '</tr></thead>';
  }

  function fmtDateTime(s) {
    if (!s) return '-';
    const d = new Date(s); if (isNaN(d)) return s;
    const p = (n) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
  }
  // 로그 다이얼로그용 시각 (BackupLog.razor 와 동일하게 HH:mm:ss.fff)
  function fmtLogTime(s) {
    if (!s) return '-';
    const d = new Date(s); if (isNaN(d)) return s;
    const p = (n) => String(n).padStart(2, '0');
    return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}.${String(d.getMilliseconds()).padStart(3, '0')}`;
  }
  function fmtShort(s) {
    if (!s) return '-';
    const d = new Date(s); if (isNaN(d)) return s;
    const p = (n) => String(n).padStart(2, '0');
    return `${p(d.getMonth() + 1)}/${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
  }

  /* ════════════════ 탭 0: 자산 정보 ════════════════ */
  // 타입 필터값 'Robot PLC' 는 대시보드 '자산별 현황' 차트의 합성 그룹(로봇 PLC) — 실제 타입명이 아니라
  // isRobotPlc 플래그로 거른다. 로봇 PLC 는 XGT PLC 이므로 연결 컬럼 가시성은 XGT PLC 와 동일.
  const ROBOT_TYPE = 'Robot PLC';
  // 타입별 컬럼 가시성 (Razor.ShowConnColumns / ShowRobotColumn / ShowModelVersion)
  function showConn() { const t = S.filterType; return !t || t === ROBOT_TYPE || t === 'XGT PLC' || t === 'LS Servo' || t === 'LS Drive'; }
  function showRobot() { const t = S.filterType; return !t || t === ROBOT_TYPE || t === 'XGT PLC'; }
  function showModelVer() { const t = S.filterType; return !t || t === 'LS Drive'; }

  function filteredAssets() {
    return S.assets.filter(a => {
      if (!matchSearch([a.name, a.ip, a.description])) return false;
      if (S.filterType === ROBOT_TYPE) { if (!a.isRobotPlc) return false; }
      else if (S.filterType && a.typeName !== S.filterType) return false;
      if (S.filterLine && a.lineName !== S.filterLine) return false;
      if (S.filterHealth && a.health !== S.filterHealth) return false;
      return true;
    });
  }

  function renderAssets() {
    const rows = filteredAssets();
    $('a-count').textContent = `${rows.length} 건 조회됨`;
    $('a-bulk').disabled = rows.length === 0;
    $('a-csv').disabled = rows.length === 0;

    const cols = [
      { title: '자산명', key: 'name' }, { title: '타입', key: 'typeName' }, { title: '라인', key: 'lineName' },
      { title: '벤더', key: 'vendor' }, { title: '스펙', key: 'spec' }, { title: '설명', key: 'description' },
    ];
    if (showConn()) cols.push({ title: 'ViaIP', key: 'ipVia' });
    cols.push({ title: 'IP', key: 'ip' });
    if (showConn()) { cols.push({ title: 'Base', key: 'baseNumber' }); cols.push({ title: 'Slot', key: 'slotNumber' }); }
    cols.push({ title: '스테이션', key: 'stationNumber' });
    if (showModelVer()) cols.push({ title: '모델버전', key: 'modelVersion' });
    if (showRobot()) cols.push({ title: '로봇PLC', key: 'isRobotPlc' });
    cols.push({ title: '상태', key: 'health' });
    cols.push({ title: '마지막 백업', key: 'lastBackupTime' });

    const sorted = sortRows(rows, S.aSort);
    const pages = Math.max(1, Math.ceil(sorted.length / S.aPageSize));
    if (S.aPage >= pages) S.aPage = 0;
    const page = sorted.slice(S.aPage * S.aPageSize, (S.aPage + 1) * S.aPageSize);

    const body = page.map(a => {
      const h = HEALTH[a.health] || HEALTH.unknown;
      const tds = [
        `<td><a href="/assets/${a.assetId}">${esc(a.name)}</a></td>`,
        `<td>${esc(a.typeName || '-')}</td>`,
        `<td>${esc(a.lineName || '-')}</td>`,
        `<td>${esc(a.vendor || '-')}</td>`,
        `<td>${esc(a.spec || '-')}</td>`,
        `<td class="wrap" title="${esc(a.description || '')}">${esc(a.description || '-')}</td>`,
      ];
      if (showConn()) tds.push(`<td>${esc(a.ipVia || '-')}</td>`);
      tds.push(`<td>${esc(a.ip || '-')}</td>`);
      if (showConn()) { tds.push(`<td>${a.baseNumber ?? '-'}</td>`); tds.push(`<td>${a.slotNumber ?? '-'}</td>`); }
      tds.push(`<td>${a.stationNumber ?? '-'}</td>`);
      if (showModelVer()) tds.push(`<td>${esc(a.modelVersion || '-')}</td>`);
      if (showRobot()) tds.push(`<td>${a.isRobotPlc ? '<span class="material-symbols-outlined" style="color:var(--health-backedup);font-size:18px;">check</span>' : '-'}</td>`);
      // agentName 없음 = 에이전트 미지정(자동 선택) → 오프라인 표기 제외
      const off = (a.agentOnline || !a.agentName) ? '' : ' (오프라인)';
      tds.push(`<td><span class="chip ${h.chip}">${esc(a.healthLabel || h.label)}</span>${off ? `<span style="font-size:10px;color:var(--c-on-surface-variant);">${off}</span>` : ''}</td>`);
      tds.push(`<td>${fmtShort(a.lastBackupTime)}</td>`);
      return `<tr>${tds.join('')}</tr>`;
    }).join('');

    $('a-table').innerHTML = thHtml(cols, S.aSort) +
      (page.length ? `<tbody>${body}</tbody>` : `<tbody><tr><td colspan="${cols.length}"><div class="hist-empty">자산 데이터가 없습니다.</div></td></tr></tbody>`);
    bindSort('a-table', S.aSort, renderAssets);
    renderPager('a-pager', S.aPage, pages, sorted.length, (p) => { S.aPage = p; renderAssets(); });
  }

  /* ════════════════ 탭 1: 백업 이력 ════════════════ */
  function filteredActions() {
    const start = new Date(S.startDate + 'T00:00:00');
    const end = new Date(S.endDate + 'T00:00:00'); end.setDate(end.getDate() + 1);
    return S.actions.filter(a => {
      const st = a.started ? new Date(a.started) : null;
      if (!st || st < start || st >= end) return false;
      if (S.selectedResult && a.resultLabel !== S.selectedResult) return false;
      if (S.filterLine && a.lineName !== S.filterLine) return false;
      if (!matchSearch([a.assetName, a.typeName, a.version, fmtDateTime(a.started), fmtDateTime(a.finished), a.resultLabel])) return false;
      return true;
    });
  }

  function renderActions() {
    const rows = filteredActions();
    $('b-count').textContent = `${rows.length} 건 조회됨`;
    $('b-csv').disabled = rows.length === 0;

    const cols = [
      { title: '자산', key: 'assetName' }, { title: '타입', key: 'typeName' }, { title: '라인', key: 'lineName' },
      { title: '버전', key: 'version' }, { title: '작업 시작', key: 'started' }, { title: '작업 종료', key: 'finished' },
      { title: '결과', key: 'resultLabel' }, { title: '다운로드', key: '' }, { title: '리포트', key: '' }, { title: '로그', key: '' },
    ];
    const sorted = sortRows(rows, S.bSort);
    const pages = Math.max(1, Math.ceil(sorted.length / S.bPageSize));
    if (S.bPage >= pages) S.bPage = 0;
    const page = sorted.slice(S.bPage * S.bPageSize, (S.bPage + 1) * S.bPageSize);

    const body = page.map(a => {
      const r = RESULT[a.result] || RESULT.unchanged;
      let verCell = esc(a.version ?? '-');
      if (a.result === 'backedup' && a.version > 1)
        verCell += ` <span class="chip chip-ghost" style="border-color:var(--health-backedup);color:var(--health-backedup);">${a.version - 1} → ${a.version}</span>`;
      const resultChip = `<span class="chip ${r.chip}"><span class="material-symbols-outlined">${r.icon}</span>${r.label}</span>`;
      // 다운로드 셀
      let dl = '';
      if (a.downloadableVersion != null && !a.isInProgress) {
        if (a.result === 'backedup') {
          dl = `<a class="hist-iconbtn" href="/api/download/backup/${a.assetId}/${a.downloadableVersion}" target="_blank" title="v${a.version} 새 백업 다운로드" style="color:var(--health-backedup);"><span class="material-symbols-outlined">download</span></a>`;
        } else if (!a.isSuccess) {
          dl = `<a class="hist-iconbtn" href="/api/download/backup/${a.assetId}/${a.downloadableVersion}" target="_blank" title="마지막 성공 백업 v${a.downloadableVersion} 다운로드" style="color:var(--health-failed);opacity:0.7;"><span class="material-symbols-outlined">download</span></a>`;
        } else {
          dl = `<a class="hist-iconbtn" href="/api/download/backup/${a.assetId}/${a.downloadableVersion}" target="_blank" title="현재 백업 v${a.version} 다운로드" style="color:var(--health-unchanged);"><span class="material-symbols-outlined">download</span></a>`;
        }
      }
      const report = a.hasReport
        ? `<a class="hist-iconbtn" href="/report/${a.assetId}/${a.version}/index.html" target="_blank" title="리포트 보기"><span class="material-symbols-outlined">open_in_new</span></a>` : '';
      const log = `<button class="hist-iconbtn" data-log="${a.id}" data-asset="${a.assetId}" title="로그 보기"><span class="material-symbols-outlined">article</span></button>`;
      return `<tr>
        <td><a href="/assets/${a.assetId}">${esc(a.assetName)}</a></td>
        <td>${esc(a.typeName || '-')}</td>
        <td>${esc(a.lineName || '-')}</td>
        <td>${verCell}</td>
        <td>${fmtDateTime(a.started)}</td>
        <td>${fmtDateTime(a.finished)}</td>
        <td>${resultChip}</td>
        <td>${dl}</td>
        <td>${report}</td>
        <td>${log}</td>
      </tr>`;
    }).join('');

    $('b-table').innerHTML = thHtml(cols, S.bSort) +
      (page.length ? `<tbody>${body}</tbody>` : `<tbody><tr><td colspan="${cols.length}"><div class="hist-empty">해당 기간에 이력 데이터가 없습니다.</div></td></tr></tbody>`);
    bindSort('b-table', S.bSort, renderActions);
    renderPager('b-pager', S.bPage, pages, sorted.length, (p) => { S.bPage = p; renderActions(); });
  }

  /* ── 백업 로그 다이얼로그 (BackupLog.razor.ShowLogs 이식) ── */
  async function openLogModal(actionId, assetId) {
    $('log-title').textContent = `액션 #${actionId} 로그`;
    $('log-sub').textContent = '불러오는 중…';
    $('log-body').innerHTML = '';
    $('log-modal').classList.add('show');
    await fetchLogs(actionId, false, assetId);
  }

  async function fetchLogs(actionId, all, assetId) {
    try {
      const res = await fetch(`/api/history/logs/${actionId}${all ? '?all=true' : ''}`, { headers: { 'Accept': 'application/json' } });
      if (!res.ok) throw new Error('http ' + res.status);
      const d = await res.json();
      renderLogs(d.logs || [], d.total || 0, all, actionId, assetId);
    } catch (e) {
      $('log-sub').textContent = '';
      $('log-body').innerHTML = `<div class="hist-empty">로그를 불러오지 못했습니다.</div>`;
    }
  }

  function renderLogs(logs, total, all, actionId, assetId) {
    if (logs.length === 0) {
      $('log-sub').textContent = '';
      $('log-body').innerHTML = `<div class="hist-empty">로그가 없습니다.</div>`;
      return;
    }
    const showAll = !all && total > logs.length;
    $('log-sub').innerHTML =
      `<span>Asset ID: ${esc(assetId)} · 로그 ${logs.length} / ${total} 건</span>` +
      (showAll ? `<button class="hist-btn" id="log-all"><span class="material-symbols-outlined">unfold_more</span>전체 보기 (${total}건)</button>` : '');
    const rows = logs.map(l => {
      const cls = LOG_LEVEL[l.level] || 'chip-default';
      return `<tr>
        <td style="font-family:var(--font-mono);font-variant-numeric:tabular-nums;font-size:12px;color:var(--c-on-surface-variant);">${esc(fmtLogTime(l.dateTime))}</td>
        <td><span class="chip ${cls}">${esc(l.level || '-')}</span></td>
        <td style="white-space:pre-wrap;word-break:break-word;">${esc(l.message)}</td>
      </tr>`;
    }).join('');
    $('log-body').innerHTML =
      `<table class="nm-table"><thead><tr><th style="width:110px;">시간</th><th style="width:90px;">레벨</th><th>메시지</th></tr></thead><tbody>${rows}</tbody></table>`;
    const allBtn = $('log-all');
    if (allBtn) allBtn.addEventListener('click', () => {
      allBtn.disabled = true;
      allBtn.innerHTML = '<span class="material-symbols-outlined">hourglass_top</span>불러오는 중…';
      fetchLogs(actionId, true, assetId);
    });
  }

  function closeLogModal() { $('log-modal').classList.remove('show'); }

  /* ════════════════ 탭 2: 통신 이력 ════════════════ */
  function metaOf(id) { return S.meta[String(id)] || { name: '#' + id, typeName: '', lineName: '', ip: '' }; }

  function filteredPings() {
    return S.pings.filter(p => {
      const m = metaOf(p.assetId);
      if (S.pingStatus) {
        const wantReachable = S.pingStatus === 'online';
        if (p.reachable !== wantReachable) return false;
      }
      if (S.filterLine && m.lineName !== S.filterLine) return false;
      if (!matchSearch([m.name, m.ip])) return false;
      return true;
    });
  }

  function renderPings() {
    const rows = filteredPings().map(p => {
      const m = metaOf(p.assetId);
      return { assetId: p.assetId, name: m.name, typeName: m.typeName, lineName: m.lineName, ip: m.ip, reachable: p.reachable, checkedAt: p.checkedAt };
    });
    $('p-count').textContent = `${rows.length} 건 조회됨`;
    $('p-csv').disabled = rows.length === 0;

    const cols = [
      { title: '자산', key: 'name' }, { title: '타입', key: 'typeName' }, { title: '라인', key: 'lineName' },
      { title: 'IP', key: 'ip' }, { title: '상태', key: 'reachable' }, { title: '변경 시각', key: 'checkedAt' },
    ];
    const sorted = sortRows(rows, S.pSort);
    const pages = Math.max(1, Math.ceil(sorted.length / S.pPageSize));
    if (S.pPage >= pages) S.pPage = 0;
    const page = sorted.slice(S.pPage * S.pPageSize, (S.pPage + 1) * S.pPageSize);

    const body = page.map(p => {
      const chip = p.reachable
        ? `<span class="chip chip-success"><span class="material-symbols-outlined">wifi</span>온라인</span>`
        : `<span class="chip chip-error"><span class="material-symbols-outlined">wifi_off</span>오프라인</span>`;
      return `<tr>
        <td><a href="/assets/${p.assetId}">${esc(p.name)}</a></td>
        <td>${esc(p.typeName || '-')}</td>
        <td>${esc(p.lineName || '-')}</td>
        <td>${esc(p.ip || '-')}</td>
        <td>${chip}</td>
        <td>${fmtDateTime(p.checkedAt)}</td>
      </tr>`;
    }).join('');

    $('p-table').innerHTML = thHtml(cols, S.pSort) +
      (page.length ? `<tbody>${body}</tbody>` : `<tbody><tr><td colspan="${cols.length}"><div class="hist-empty">해당 기간에 온라인 상태 변경 이력이 없습니다.</div></td></tr></tbody>`);
    bindSort('p-table', S.pSort, renderPings);
    renderPager('p-pager', S.pPage, pages, sorted.length, (p) => { S.pPage = p; renderPings(); });
  }

  /* ── 정렬 헤더 바인딩 ── */
  function bindSort(tableId, sort, rerender) {
    $(tableId).querySelectorAll('th[data-key]').forEach(th => {
      const key = th.getAttribute('data-key');
      if (!key) return;
      th.addEventListener('click', () => {
        if (sort.key === key) sort.dir = -sort.dir;
        else { sort.key = key; sort.dir = 1; }
        rerender();
      });
    });
  }

  /* ── 페이저 ── */
  function renderPager(hostId, page, pages, total, go) {
    const host = $(hostId);
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

  /* ── CSV (Blob 다운로드) ── */
  function csvEscape(v) {
    const s = String(v == null ? '' : v);
    return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
  }
  function downloadCsv(filename, lines) {
    const content = '﻿' + lines.join('\r\n');
    const blob = new Blob([content], { type: 'text/csv;charset=utf-8;' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url; a.download = filename; a.click();
    URL.revokeObjectURL(url);
  }

  function exportAssetCsv() {
    const rows = filteredAssets();
    const headers = ['자산명', '타입', '라인', '벤더', '스펙', '설명'];
    if (showConn()) headers.push('ViaIP');
    headers.push('IP');
    if (showConn()) { headers.push('Base'); headers.push('Slot'); }
    headers.push('스테이션');
    if (showModelVer()) headers.push('모델버전');
    if (showRobot()) headers.push('로봇PLC');
    headers.push('상태'); headers.push('마지막 백업');
    const lines = [headers.map(csvEscape).join(',')];
    for (const a of rows) {
      const cols = [a.name || '', a.typeName || '', a.lineName || '', a.vendor || '', a.spec || '', a.description || ''];
      if (showConn()) cols.push(a.ipVia || '');
      cols.push(a.ip || '');
      if (showConn()) { cols.push(a.baseNumber ?? ''); cols.push(a.slotNumber ?? ''); }
      cols.push(a.stationNumber ?? '');
      if (showModelVer()) cols.push(a.modelVersion || '');
      if (showRobot()) cols.push(a.isRobotPlc ? 'Y' : '');
      cols.push(a.healthLabel || (HEALTH[a.health] || HEALTH.unknown).label);
      cols.push(a.lastBackupTime ? fmtDateTime(a.lastBackupTime) : '');
      lines.push(cols.map(csvEscape).join(','));
    }
    downloadCsv(`자산정보_${todayStr().replace(/-/g, '')}.csv`, lines);
  }

  function exportActionCsv() {
    const rows = filteredActions();
    const lines = ['자산,타입,라인,버전,작업 시작,작업 종료,결과'];
    for (const a of rows) {
      const cols = [a.assetName || '', a.typeName || '', a.lineName || '', a.version ?? '',
        a.started ? fmtDateTime(a.started) : '', a.finished ? fmtDateTime(a.finished) : '', a.resultLabel];
      lines.push(cols.map(csvEscape).join(','));
    }
    downloadCsv(`백업이력_${S.startDate.replace(/-/g, '')}_${S.endDate.replace(/-/g, '')}.csv`, lines);
  }

  function exportPingCsv() {
    const rows = filteredPings();
    const lines = ['자산,타입,라인,IP,상태,변경 시각'];
    for (const p of rows) {
      const m = metaOf(p.assetId);
      const cols = [m.name, m.typeName, m.lineName, m.ip, p.reachable ? '온라인' : '오프라인', fmtDateTime(p.checkedAt)];
      lines.push(cols.map(csvEscape).join(','));
    }
    downloadCsv(`온라인이력_${S.startDate.replace(/-/g, '')}_${S.endDate.replace(/-/g, '')}.csv`, lines);
  }

  /* ── 일괄 다운로드 (POST /api/download/backup/bulk — Program.cs 기존 엔드포인트) ── */
  async function bulkDownload() {
    const ids = filteredAssets().map(a => a.assetId);
    if (ids.length === 0) { if (window.Shell) Shell.toast('다운로드할 자산이 없습니다.'); return; }
    const btn = $('a-bulk');
    btn.disabled = true;
    const orig = btn.innerHTML;
    btn.innerHTML = '<span class="material-symbols-outlined">hourglass_top</span>다운로드중...';
    try {
      const r = await fetch('/api/download/backup/bulk', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(ids),
      });
      if (r.status === 401) {
        // 미로그인: 개별 다운로드(<a> 내비게이션 → /login 리다이렉트)와 동일하게 로그인 페이지로
        location.href = '/login?returnUrl=' + encodeURIComponent(location.pathname + location.search);
        return;
      }
      if (!r.ok) { if (window.Shell) Shell.toast((await r.text()) || '다운로드 실패'); return; }
      const parts = ['DEXA_Backup'];
      if (S.filterLine) parts.push(S.filterLine);
      if (S.filterType) parts.push(S.filterType);
      if (S.filterHealth) {
        const hl = { backedup: '백업갱신', unchanged: '변경없음', failed: '실패', inprogress: '작업중', unknown: '내역없음' }[S.filterHealth] || S.filterHealth;
        parts.push(hl);
      }
      const d = new Date(); const p = (n) => String(n).padStart(2, '0');
      parts.push(`${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}_${p(d.getHours())}${p(d.getMinutes())}`);
      const name = parts.join('_') + '.zip';
      const blob = await r.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a'); a.href = url; a.download = name; a.click();
      URL.revokeObjectURL(url);
    } catch (e) {
      if (window.Shell) Shell.toast('다운로드 실패: ' + e.message);
    } finally {
      btn.innerHTML = orig; btn.disabled = false;
    }
  }

  // HTTP(비보안 컨텍스트)에서는 navigator.clipboard가 없으므로 execCommand로 폴백
  function copyText(text) {
    if (navigator.clipboard && window.isSecureContext) return navigator.clipboard.writeText(text);
    return new Promise((resolve, reject) => {
      const ta = document.createElement('textarea');
      ta.value = text;
      ta.style.position = 'fixed';
      ta.style.opacity = '0';
      document.body.appendChild(ta);
      ta.focus();
      ta.select();
      try {
        if (document.execCommand('copy')) resolve();
        else reject(new Error('execCommand copy failed'));
      } catch (e) {
        reject(e);
      } finally {
        ta.remove();
      }
    });
  }

  function copyLink() {
    syncUrl();
    copyText(location.href).then(
      () => { if (window.Shell) Shell.toast('링크가 복사되었습니다.'); },
      () => { if (window.Shell) Shell.toast('링크 복사 실패'); });
  }

  /* ── 탭 전환 ── */
  function switchTab(idx) {
    S.activeTab = idx;
    document.querySelectorAll('.hist-tab').forEach(t => t.classList.toggle('active', +t.getAttribute('data-tab') === idx));
    document.querySelectorAll('.hist-tabpanel').forEach(p => p.classList.toggle('active', +p.getAttribute('data-panel') === idx));
    syncUrl();
    renderActiveTab();
  }

  function renderActiveTab() {
    if (S.activeTab === 0) renderAssets();
    else if (S.activeTab === 1) renderActions();
    else renderPings();
  }

  /* ── 기간 버튼 처리 (백업/통신 공유) ── */
  function setPeriod(days, fromTab) {
    S.periodLabel = days === 0 ? 'today' : days === 7 ? '7days' : days === 30 ? '30days' : '';
    setPeriodDates(days);
    syncDateInputs(); syncPeriodButtons();
    load(); loadPings();
    if (S.activeTab === 1) renderActions(); else if (S.activeTab === 2) renderPings();
    syncUrl();
  }
  function onDateManual() {
    S.periodLabel = '';
    syncPeriodButtons();
    load(); loadPings();
    if (S.activeTab === 1) renderActions(); else if (S.activeTab === 2) renderPings();
    syncUrl();
  }
  function syncDateInputs() {
    ['b-start', 'p-start'].forEach(id => $(id).value = S.startDate);
    ['b-end', 'p-end'].forEach(id => $(id).value = S.endDate);
  }
  function syncPeriodButtons() {
    const map = { today: '0', '7days': '7', '30days': '30' };
    ['b-period', 'p-period'].forEach(gid => {
      $(gid).querySelectorAll('.hist-seg-btn').forEach(b =>
        b.classList.toggle('active', map[S.periodLabel] === b.getAttribute('data-days')));
    });
  }

  /* ── 검색/모드 동기화 (3탭 공유) ── */
  function syncSearchInputs() {
    ['a-search', 'b-search', 'p-search'].forEach(id => { if ($(id)) $(id).value = S.searchText; });
    ['a-mode', 'b-mode', 'p-mode'].forEach(gid => {
      $(gid).querySelectorAll('.hist-seg-btn').forEach(b =>
        b.classList.toggle('active', b.getAttribute('data-mode') === S.searchMode));
    });
  }

  /* ── 이벤트 바인딩 ── */
  function bind() {
    // 탭
    $('hist-tabs').querySelectorAll('.hist-tab').forEach(t =>
      t.addEventListener('click', () => switchTab(+t.getAttribute('data-tab'))));

    // 검색 (3탭 동기화)
    ['a-search', 'b-search', 'p-search'].forEach(id => {
      $(id).addEventListener('input', (e) => { S.searchText = e.target.value; syncSearchInputs(); renderActiveTab(); syncUrl(); });
    });
    ['a-mode', 'b-mode', 'p-mode'].forEach(gid => {
      $(gid).querySelectorAll('.hist-seg-btn').forEach(b =>
        b.addEventListener('click', () => { S.searchMode = b.getAttribute('data-mode'); syncSearchInputs(); renderActiveTab(); syncUrl(); }));
    });

    // 탭0 자산 필터
    $('a-type').addEventListener('change', (e) => { S.filterType = e.target.value; S.aPage = 0; renderAssets(); syncUrl(); });
    $('a-line').addEventListener('change', (e) => { S.filterLine = e.target.value; fillSelectOptions(); renderActiveTab(); syncUrl(); });
    $('a-health').addEventListener('change', (e) => { S.filterHealth = e.target.value; S.aPage = 0; renderAssets(); syncUrl(); });
    $('a-bulk').addEventListener('click', bulkDownload);
    $('a-csv').addEventListener('click', exportAssetCsv);
    $('a-link').addEventListener('click', copyLink);

    // 탭1 백업
    $('b-start').addEventListener('change', (e) => { S.startDate = e.target.value; onDateManual(); });
    $('b-end').addEventListener('change', (e) => { S.endDate = e.target.value; onDateManual(); });
    $('b-period').querySelectorAll('.hist-seg-btn').forEach(b =>
      b.addEventListener('click', () => setPeriod(+b.getAttribute('data-days'))));
    $('b-result').addEventListener('change', (e) => { S.selectedResult = e.target.value; S.bPage = 0; renderActions(); syncUrl(); });
    $('b-line').addEventListener('change', (e) => { S.filterLine = e.target.value; fillSelectOptions(); renderActiveTab(); syncUrl(); });
    $('b-csv').addEventListener('click', exportActionCsv);
    $('b-link').addEventListener('click', copyLink);
    // 로그 버튼(행 위임): /assets 이동 대신 로그 다이얼로그
    $('b-table').addEventListener('click', (e) => {
      const btn = e.target.closest('[data-log]');
      if (!btn) return;
      openLogModal(+btn.getAttribute('data-log'), +btn.getAttribute('data-asset'));
    });

    // 로그 다이얼로그 닫기: 배경 클릭 / 닫기 버튼 / ESC
    const logModal = $('log-modal');
    if (logModal) {
      logModal.addEventListener('click', (e) => {
        if (e.target === logModal || e.target.closest('[data-close]')) closeLogModal();
      });
    }
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeLogModal(); });

    // 탭2 통신
    $('p-start').addEventListener('change', (e) => { S.startDate = e.target.value; onDateManual(); });
    $('p-end').addEventListener('change', (e) => { S.endDate = e.target.value; onDateManual(); });
    $('p-period').querySelectorAll('.hist-seg-btn').forEach(b =>
      b.addEventListener('click', () => setPeriod(+b.getAttribute('data-days'))));
    $('p-status').addEventListener('change', (e) => { S.pingStatus = e.target.value; S.pPage = 0; renderPings(); syncUrl(); });
    $('p-line').addEventListener('change', (e) => { S.filterLine = e.target.value; fillSelectOptions(); renderActiveTab(); syncUrl(); });
    $('p-csv').addEventListener('click', exportPingCsv);
    $('p-link').addEventListener('click', copyLink);
  }

  /* ── 초기 컨트롤 상태 반영 ── */
  function applyControlsFromState() {
    syncSearchInputs();
    syncDateInputs();
    syncPeriodButtons();
    $('a-health').value = S.filterHealth;
    $('b-result').value = S.selectedResult;
    $('p-status').value = S.pingStatus;
    // 탭 표시
    document.querySelectorAll('.hist-tab').forEach(t => t.classList.toggle('active', +t.getAttribute('data-tab') === S.activeTab));
    document.querySelectorAll('.hist-tabpanel').forEach(p => p.classList.toggle('active', +p.getAttribute('data-panel') === S.activeTab));
  }

  document.addEventListener('DOMContentLoaded', async () => {
    if (window.Shell) await Shell.init({ active: 'history' });
    applyQuery();
    bind();
    applyControlsFromState();
    await Promise.all([load(), loadPings()]);
    setInterval(() => { if (!document.hidden) { load(); loadPings(); } }, 30000);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) { load(); loadPings(); } });
  });
})();
