/* ============================================================================
 * DEXA 설정(관리) — ServerConfig.razor 의 에이전트 목록 + 트리거 관리를 정적 페이지로 이식.
 * GET /api/admin/config 스냅샷 1회 조회 → 두 표 렌더. 30초 폴링 + 탭 복귀 시 갱신.
 * 쓰기: 트리거 추가/스케줄(cron) 수정/실행/삭제 (같은 출처 → 쿠키 자동 동봉, JS 토큰 불필요).
 * 제외(deviations): 에이전트 재시작·연결 피어(Akka), 트리거↔자산 매핑 편집.
 * ==========================================================================*/
(function () {
  'use strict';

  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const $ = (id) => document.getElementById(id);

  const S = {
    agents: [],
    triggers: [],
    showOffline: false,
    runningTriggerIds: new Set(),
    cronEditId: null,
    // 자산 매핑 모달 상태
    mapTriggerId: null,
    mapGroups: [],
    mapSelected: new Set(),
  };

  /* ── 데이터 로드 ── */
  async function load() {
    try {
      const res = await fetch('/api/admin/config', { headers: { 'Accept': 'application/json' } });
      if (!res.ok) return;
      const d = await res.json();
      S.agents = d.agents || [];
      S.triggers = d.triggers || [];
      renderAgents();
      renderTriggers();
    } catch (e) { /* 무시 */ }
  }

  /* ── 시간 포맷 ── */
  function fmtDateTime(s) {
    if (!s) return '-';
    const d = new Date(s); if (isNaN(d)) return s;
    const p = (n) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
  }

  /* ════════════════ 에이전트 목록 ════════════════ */
  function renderAgents() {
    const host = $('agents-host');
    const list = S.agents.filter(a => S.showOffline || a.online);
    if (!list.length) {
      const msg = S.agents.length === 0
        ? '등록된 에이전트가 없습니다.'
        : '온라인 에이전트가 없습니다. 오프라인 에이전트를 보려면 체크박스를 선택하세요.';
      host.innerHTML = `<div class="ac-empty">${esc(msg)}</div>`;
      return;
    }
    const body = list.map(a => {
      const statusChip = a.online
        ? `<span class="chip chip-success">온라인</span>`
        : `<span class="chip chip-default">오프라인</span>`;
      // 재시작: 온라인 에이전트만 (피어 매칭은 서버에서 수행, 없으면 에러 토스트)
      const restartCell = a.online
        ? `<button class="ac-iconbtn ac-iconbtn-info" data-act="restart" data-id="${a.id}" title="에이전트 재시작"><span class="material-symbols-outlined">restart_alt</span></button>`
        : `<span style="color:var(--c-outline);font-size:12px;">오프라인</span>`;
      return `<tr>
        <td>${esc(a.name || '-')}</td>
        <td>${esc(a.ip || '-')}</td>
        <td>${esc(a.swVersion || '-')}</td>
        <td>${statusChip}</td>
        <td>${fmtDateTime(a.connected)}</td>
        <td>${restartCell}</td>
      </tr>`;
    }).join('');
    host.innerHTML = `<div class="ac-table-wrap"><table class="nm-table">
      <thead><tr><th>이름</th><th>IP</th><th>버전</th><th>상태</th><th>접속 시간</th><th>작업</th></tr></thead>
      <tbody>${body}</tbody></table></div>`;

    host.querySelectorAll('[data-act="restart"]').forEach(b =>
      b.addEventListener('click', () => restartAgent(parseInt(b.getAttribute('data-id'), 10))));
  }

  /* ════════════════ 스케줄(cron) 해석 ════════════════ */
  // Quartz 6필드(초 분 시 일 월 요일, +선택 연도)를 빌더 상태로 역파싱. 못 읽는 형식이면 null.
  const DOW = [['SUN', '일'], ['MON', '월'], ['TUE', '화'], ['WED', '수'], ['THU', '목'], ['FRI', '금'], ['SAT', '토']];
  const DOW_KO = {}; const DOW_NUM = {};
  DOW.forEach(([q, ko], i) => { DOW_KO[q] = ko; DOW_NUM[String(i + 1)] = q; }); // Quartz 요일 숫자: 1=일 … 7=토

  function parseDowList(s) {
    const out = [];
    for (const tok of String(s).toUpperCase().split(',')) {
      const t = tok.trim();
      const q = DOW_KO[t] ? t : DOW_NUM[t];
      if (!q) return null;
      if (out.indexOf(q) < 0) out.push(q);
    }
    if (!out.length) return null;
    const order = DOW.map(d => d[0]);
    return out.sort((a, b) => order.indexOf(a) - order.indexOf(b));
  }

  function cronParse(cron) {
    if (!cron || !cron.trim()) return null;
    const p = cron.trim().split(/\s+/);
    if (p.length !== 6 && !(p.length === 7 && p[6] === '*')) return null;
    const [sec, min, hour, dom, mon, dow] = p;
    if (sec !== '0' || !/^\d+$/.test(min) || mon !== '*') return null;
    const m = parseInt(min, 10);
    if (m > 59) return null;
    const p2 = (n) => String(n).padStart(2, '0');
    const anyDom = dom === '*' || dom === '?';
    const anyDow = dow === '*' || dow === '?';
    if (hour === '*') return (anyDom && anyDow) ? { mode: 'hourly', minute: m } : null;
    if (!/^\d+$/.test(hour) || parseInt(hour, 10) > 23) return null;
    const time = `${p2(parseInt(hour, 10))}:${p2(m)}`;
    if (anyDom && anyDow) return { mode: 'daily', time };
    if (anyDom) {
      const days = parseDowList(dow);
      return days ? { mode: 'weekly', days, time } : null;
    }
    if (anyDow && /^\d+$/.test(dom) && +dom >= 1 && +dom <= 31) return { mode: 'monthly', dom: +dom, time };
    return null;
  }

  function schedSummary(p) {
    if (!p) return null;
    switch (p.mode) {
      case 'hourly': return `매시 ${String(p.minute).padStart(2, '0')}분`;
      case 'daily': return `매일 ${p.time}`;
      case 'weekly': return `매주 ${p.days.map(q => DOW_KO[q]).join('·')} ${p.time}`;
      case 'monthly': return `매월 ${p.dom}일 ${p.time}`;
      default: return null;
    }
  }

  function cronToReadable(cron) {
    return schedSummary(cronParse(cron)) || cron || '';
  }

  /* ════════════════ 스케줄 빌더 (cron 대신 쉬운 주기 선택 UI) ════════════════ */
  const SCHED_MODES = [['hourly', '매시간'], ['daily', '매일'], ['weekly', '매주'], ['monthly', '매월'], ['custom', '직접 입력']];

  function createSchedBuilder(hostId) {
    const host = $(hostId);
    const st = { mode: 'daily', minute: 0, time: '02:00', days: new Set(['MON']), dom: 1, raw: '' };

    function stateCron() {
      const t = /^(\d{1,2}):(\d{2})/.exec(st.time || '');
      const hh = t ? parseInt(t[1], 10) : null;
      const mm = t ? parseInt(t[2], 10) : null;
      switch (st.mode) {
        case 'hourly': return { cron: `0 ${st.minute} * * * ?` };
        case 'daily':
          if (!t) return { err: '실행 시각을 선택해주세요.' };
          return { cron: `0 ${mm} ${hh} * * ?` };
        case 'weekly': {
          if (!st.days.size) return { err: '요일을 하나 이상 선택해주세요.' };
          if (!t) return { err: '실행 시각을 선택해주세요.' };
          const days = DOW.map(d => d[0]).filter(q => st.days.has(q)).join(',');
          return { cron: `0 ${mm} ${hh} ? * ${days}` };
        }
        case 'monthly':
          if (!t) return { err: '실행 시각을 선택해주세요.' };
          return { cron: `0 ${mm} ${hh} ${st.dom} * ?` };
        default: {
          const raw = st.raw.trim();
          if (!raw) return { err: 'Cron 표현식을 입력해주세요.' };
          const n = raw.split(/\s+/).length;
          if (n < 6 || n > 7) return { err: 'Quartz cron은 6~7개 필드여야 합니다. (초 분 시 일 월 요일)' };
          return { cron: raw };
        }
      }
    }

    function preview() {
      const box = host.querySelector('.ac-sched-preview');
      const r = stateCron();
      box.classList.toggle('err', !!r.err);
      box.querySelector('b').textContent = r.err || schedSummary(cronParse(r.cron)) || '사용자 정의 스케줄';
      box.querySelector('code').textContent = r.cron || '';
    }

    function render() {
      const seg = SCHED_MODES.map(([k, label]) =>
        `<button type="button" class="ac-seg-btn${st.mode === k ? ' on' : ''}" data-mode="${k}">${label}</button>`).join('');
      let body = '';
      if (st.mode === 'hourly') {
        const opts = Array.from({ length: 60 }, (_, i) =>
          `<option value="${i}"${st.minute === i ? ' selected' : ''}>${String(i).padStart(2, '0')}</option>`).join('');
        body = `<div class="ac-sched-row">매시 <select class="ac-input" data-f="minute">${opts}</select> 분에 실행</div>`;
      } else if (st.mode === 'daily') {
        body = `<div class="ac-sched-row">매일 <input type="time" class="ac-input" data-f="time" value="${st.time}" /> 에 실행</div>`;
      } else if (st.mode === 'weekly') {
        const chips = DOW.map(([q, ko]) =>
          `<button type="button" class="ac-dow-btn${st.days.has(q) ? ' on' : ''}" data-day="${q}">${ko}</button>`).join('');
        body = `<div class="ac-dow">${chips}</div>
          <div class="ac-sched-row">선택한 요일 <input type="time" class="ac-input" data-f="time" value="${st.time}" /> 에 실행</div>`;
      } else if (st.mode === 'monthly') {
        const opts = Array.from({ length: 31 }, (_, i) =>
          `<option value="${i + 1}"${st.dom === i + 1 ? ' selected' : ''}>${i + 1}</option>`).join('');
        body = `<div class="ac-sched-row">매월 <select class="ac-input" data-f="dom">${opts}</select> 일 <input type="time" class="ac-input" data-f="time" value="${st.time}" /> 에 실행</div>
          <div class="ac-hint">29~31일은 해당 일자가 없는 달에는 실행되지 않습니다.</div>`;
      } else {
        body = `<div class="ac-sched-row"><input type="text" class="ac-input" style="width:100%;font-family:var(--font-mono);font-variant-numeric:tabular-nums;" data-f="raw" value="${esc(st.raw)}" placeholder="0 0 2 * * ?" /></div>
          <div class="ac-hint">초 분 시 일 월 요일 (Quartz 6필드). 예) 매일 02:00 → 0 0 2 * * ?</div>`;
      }
      host.innerHTML = `<div class="ac-seg">${seg}</div>${body}
        <div class="ac-sched-preview"><span class="material-symbols-outlined">schedule</span><div><b></b><code></code></div></div>`;

      host.querySelectorAll('[data-mode]').forEach(b =>
        b.addEventListener('click', () => { st.mode = b.getAttribute('data-mode'); render(); }));
      host.querySelectorAll('[data-day]').forEach(b =>
        b.addEventListener('click', () => {
          const q = b.getAttribute('data-day');
          if (st.days.has(q)) st.days.delete(q); else st.days.add(q);
          b.classList.toggle('on', st.days.has(q));
          preview();
        }));
      host.querySelectorAll('[data-f]').forEach(el =>
        el.addEventListener('input', () => {
          const f = el.getAttribute('data-f');
          if (f === 'minute') st.minute = parseInt(el.value, 10) || 0;
          else if (f === 'dom') st.dom = parseInt(el.value, 10) || 1;
          else if (f === 'time') st.time = el.value;
          else st.raw = el.value;
          preview();
        }));
      preview();
    }

    // 기존 cron 을 UI 상태로 복원. 해석 불가한 표현식은 '직접 입력' 모드에 원문 유지.
    function setCron(cron) {
      const p = cronParse(cron);
      if (p) {
        st.mode = p.mode;
        if (p.minute != null) st.minute = p.minute;
        if (p.time) st.time = p.time;
        if (p.days) st.days = new Set(p.days);
        if (p.dom) st.dom = p.dom;
      } else {
        st.mode = cron && cron.trim() ? 'custom' : 'daily';
      }
      st.raw = (cron || '').trim();
      render();
    }

    return { setCron, get: stateCron };
  }

  let addSched = null;   // 트리거 추가 모달의 빌더
  let editSched = null;  // 스케줄 수정 모달의 빌더

  /* ════════════════ 트리거 목록 ════════════════ */

  function renderTriggers() {
    const host = $('triggers-host');
    if (!S.triggers.length) {
      host.innerHTML = `<div class="ac-empty">등록된 트리거가 없습니다.</div>`;
      return;
    }
    const body = S.triggers.map(t => {
      const stateChip = t.enabled
        ? `<span class="chip chip-success">활성</span>`
        : `<span class="chip chip-default">비활성</span>`;
      const mapChip = `<span class="chip ${t.mappedAssetCount > 0 ? 'chip-info' : 'chip-default'}">${t.mappedAssetCount}개</span>`;
      const running = S.runningTriggerIds.has(t.id);
      const runBtn = running
        ? `<span class="ac-spinner" title="실행 중"></span>`
        : `<button class="ac-iconbtn ac-iconbtn-success" data-act="run" data-id="${t.id}" title="즉시 실행"><span class="material-symbols-outlined">play_arrow</span></button>`;
      return `<tr>
        <td>${t.id}</td>
        <td>${esc(t.name || '-')}</td>
        <td><a class="ac-cron-link" data-act="cron" data-id="${t.id}" title="${esc(t.cronExpression || '')}">${esc(cronToReadable(t.cronExpression))}</a></td>
        <td>${stateChip}</td>
        <td class="wrap" title="${esc(t.description || '')}">${esc(t.description || '-')}</td>
        <td>${mapChip}</td>
        <td><div class="ac-actions">
          <button class="ac-iconbtn ac-iconbtn-info" data-act="map" data-id="${t.id}" title="자산 매핑"><span class="material-symbols-outlined">account_tree</span></button>
          ${runBtn}
          <button class="ac-iconbtn ac-iconbtn-danger" data-act="del" data-id="${t.id}" title="삭제"><span class="material-symbols-outlined">delete</span></button>
        </div></td>
      </tr>`;
    }).join('');
    host.innerHTML = `<div class="ac-table-wrap"><table class="nm-table">
      <thead><tr><th>ID</th><th>이름</th><th>스케줄</th><th>상태</th><th>설명</th><th>자산 매핑</th><th>작업</th></tr></thead>
      <tbody>${body}</tbody></table></div>`;

    host.querySelectorAll('[data-act]').forEach(el => {
      const id = parseInt(el.getAttribute('data-id'), 10);
      const act = el.getAttribute('data-act');
      el.addEventListener('click', () => {
        if (act === 'run') executeTrigger(id);
        else if (act === 'del') deleteTrigger(id);
        else if (act === 'cron') openCronModal(id);
        else if (act === 'map') openMapModal(id);
      });
    });
  }

  function toast(msg) { if (window.Shell && Shell.toast) Shell.toast(msg); }

  /* ════════════════ 쓰기 작업 ════════════════ */
  async function executeTrigger(id) {
    const t = S.triggers.find(x => x.id === id);
    if (!t) return;
    S.runningTriggerIds.add(id);
    renderTriggers();
    try {
      const r = await fetch(`/api/admin/config/triggers/${id}/execute`, { method: 'POST' });
      if (r.ok) {
        toast(`트리거 '${t.name}' 백업 실행 요청됨`);
        // 자산 매핑 수에 비례한 예상 시간 동안 스피너 표시 (Razor 이식: 자산당 3초, 최소 10초)
        const waitMs = Math.max(10, (t.mappedAssetCount || 0) * 3) * 1000;
        setTimeout(() => { S.runningTriggerIds.delete(id); renderTriggers(); }, waitMs);
      } else {
        toast((await safeErr(r)) || '트리거 실행 실패');
        S.runningTriggerIds.delete(id);
        renderTriggers();
      }
    } catch (e) {
      toast('트리거 실행 실패: ' + e.message);
      S.runningTriggerIds.delete(id);
      renderTriggers();
    }
  }

  async function deleteTrigger(id) {
    const t = S.triggers.find(x => x.id === id);
    if (!t) return;
    if (!confirm(`트리거 '${t.name}'을(를) 삭제하시겠습니까?`)) return;
    try {
      const r = await fetch(`/api/admin/config/triggers/${id}`, { method: 'DELETE' });
      if (r.ok) { toast('트리거가 삭제되었습니다.'); await load(); }
      else toast((await safeErr(r)) || '트리거 삭제 실패');
    } catch (e) { toast('트리거 삭제 실패: ' + e.message); }
  }

  async function addTrigger() {
    const name = $('add-name').value.trim();
    const desc = $('add-desc').value.trim();
    if (!name) { toast('트리거 이름을 입력해주세요.'); return; }
    const sched = addSched.get();
    if (sched.err) { toast(sched.err); return; }
    const cron = sched.cron;
    const btn = $('add-save'); btn.disabled = true;
    try {
      const r = await fetch('/api/admin/config/triggers', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name, cronExpression: cron, description: desc || null }),
      });
      if (r.ok) { closeModal('add-modal'); toast('트리거가 추가되었습니다.'); await load(); }
      else toast((await safeErr(r)) || '트리거 추가 실패');
    } catch (e) { toast('트리거 추가 실패: ' + e.message); }
    finally { btn.disabled = false; }
  }

  async function saveCron() {
    const id = S.cronEditId;
    if (id == null) return;
    const sched = editSched.get();
    if (sched.err) { toast(sched.err); return; }
    const cron = sched.cron;
    const btn = $('cron-save'); btn.disabled = true;
    try {
      const r = await fetch(`/api/admin/config/triggers/${id}/cron`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ cronExpression: cron }),
      });
      if (r.ok) { closeModal('cron-modal'); toast('스케줄이 수정되었습니다.'); await load(); }
      else toast((await safeErr(r)) || '스케줄 수정 실패');
    } catch (e) { toast('스케줄 수정 실패: ' + e.message); }
    finally { btn.disabled = false; }
  }

  async function safeErr(r) {
    try { const d = await r.json(); return d && d.error; } catch (e) { return null; }
  }

  /* ════════════════ 에이전트 재시작 (ServerConfig.RestartAgent 이식) ════════════════ */
  async function restartAgent(id) {
    const a = S.agents.find(x => x.id === id);
    if (!a) return;
    if (!confirm(`에이전트 '${a.name}' (${a.ip || '-'})을(를) 재시작하시겠습니까?`)) return;
    try {
      const r = await fetch(`/api/admin/config/agents/${id}/restart`, { method: 'POST' });
      if (r.ok) { toast(`에이전트 '${a.name}' 재시작 요청됨`); setTimeout(load, 2000); }
      else toast((await safeErr(r)) || '재시작 요청 실패');
    } catch (e) { toast('재시작 요청 실패: ' + e.message); }
  }

  /* ════════════════ 트리거 ↔ 자산 매핑 (ScheduleAssetEditor 이식) ════════════════ */
  async function openMapModal(id) {
    const t = S.triggers.find(x => x.id === id);
    if (!t) return;
    S.mapTriggerId = id;
    S.mapGroups = [];
    S.mapSelected = new Set();
    $('map-title').textContent = `자산 매핑 — ${t.name}`;
    $('map-list').innerHTML = `<div class="ac-loading"><span class="ac-spinner"></span>불러오는 중…</div>`;
    $('map-count').textContent = '0개 선택';
    openModal('map-modal');
    try {
      const res = await fetch(`/api/admin/config/triggers/${id}/assets`, { headers: { 'Accept': 'application/json' } });
      if (!res.ok) { toast((await safeErr(res)) || '자산 목록 조회 실패'); closeModal('map-modal'); return; }
      const d = await res.json();
      S.mapGroups = d.groups || [];
      S.mapSelected = new Set(d.selectedIds || []);
      renderMap();
    } catch (e) { toast('자산 목록 조회 실패: ' + e.message); closeModal('map-modal'); }
  }

  function renderMap() {
    const host = $('map-list');
    const sc = host.scrollTop; // 토글 시 스크롤 위치 보존
    $('map-count').textContent = `${S.mapSelected.size}개 선택`;
    if (!S.mapGroups.length) {
      host.innerHTML = `<div class="ac-empty">선택 가능한 자산이 없습니다.</div>`;
      return;
    }
    host.innerHTML = S.mapGroups.map(g => {
      const sel = g.assets.filter(a => S.mapSelected.has(a.assetId)).length;
      const allOn = g.assets.length > 0 && sel === g.assets.length;
      const rows = g.assets.map(a => `
        <label class="ac-map-asset">
          <input type="checkbox" data-asset="${a.assetId}" ${S.mapSelected.has(a.assetId) ? 'checked' : ''} />
          ${a.icon ? `<img src="${esc(a.icon)}" class="ac-map-ico" onerror="this.style.visibility='hidden'" />` : ''}
          <span>${esc(a.name || '')}</span>
          ${a.ip ? `<span class="ac-map-ip">${esc(a.ip)}</span>` : ''}
        </label>`).join('');
      return `<div class="ac-map-group">
        <label class="ac-map-grouphead">
          <input type="checkbox" data-group="${esc(g.lineName)}" ${allOn ? 'checked' : ''} />
          <strong>${esc(g.lineName)}</strong>
          <span class="ac-map-gcount">(${sel} / ${g.assets.length})</span>
        </label>
        ${rows}
      </div>`;
    }).join('');
    host.scrollTop = sc;

    host.querySelectorAll('input[data-asset]').forEach(cb => cb.addEventListener('change', () => {
      const aid = parseInt(cb.getAttribute('data-asset'), 10);
      if (cb.checked) S.mapSelected.add(aid); else S.mapSelected.delete(aid);
      renderMap();
    }));
    host.querySelectorAll('input[data-group]').forEach(cb => cb.addEventListener('change', () => {
      const g = S.mapGroups.find(x => x.lineName === cb.getAttribute('data-group'));
      if (!g) return;
      g.assets.forEach(a => { if (cb.checked) S.mapSelected.add(a.assetId); else S.mapSelected.delete(a.assetId); });
      renderMap();
    }));
  }

  function mapSelectAll(on) {
    if (on) S.mapGroups.forEach(g => g.assets.forEach(a => S.mapSelected.add(a.assetId)));
    else S.mapSelected.clear();
    renderMap();
  }

  async function saveMap() {
    const id = S.mapTriggerId;
    if (id == null) return;
    const btn = $('map-save'); btn.disabled = true;
    try {
      const r = await fetch(`/api/admin/config/triggers/${id}/assets`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ assetIds: Array.from(S.mapSelected) }),
      });
      if (r.ok) { closeModal('map-modal'); toast('자산 매핑이 저장되었습니다.'); await load(); }
      else toast((await safeErr(r)) || '자산 매핑 저장 실패');
    } catch (e) { toast('자산 매핑 저장 실패: ' + e.message); }
    finally { btn.disabled = false; }
  }

  /* ════════════════ 모달 제어 ════════════════ */
  function openModal(id) { $(id).classList.add('open'); }
  function closeModal(id) { $(id).classList.remove('open'); }

  function openAddModal() {
    $('add-name').value = '';
    $('add-desc').value = '';
    addSched.setCron('0 0 2 * * ?'); // 기본값: 매일 02:00
    openModal('add-modal');
    $('add-name').focus();
  }

  function openCronModal(id) {
    const t = S.triggers.find(x => x.id === id);
    if (!t) return;
    S.cronEditId = id;
    $('cron-trigger-label').textContent = `${t.name} — 실행 주기`;
    editSched.setCron(t.cronExpression || '0 0 2 * * ?');
    openModal('cron-modal');
  }

  /* ── 이벤트 바인딩 ── */
  function bind() {
    addSched = createSchedBuilder('add-sched');
    editSched = createSchedBuilder('cron-sched');
    $('show-offline').addEventListener('change', (e) => { S.showOffline = e.target.checked; renderAgents(); });
    $('agents-refresh').addEventListener('click', load);
    $('triggers-refresh').addEventListener('click', load);
    $('trigger-add').addEventListener('click', openAddModal);
    $('add-save').addEventListener('click', addTrigger);
    $('cron-save').addEventListener('click', saveCron);
    $('map-save').addEventListener('click', saveMap);
    $('map-all').addEventListener('click', () => mapSelectAll(true));
    $('map-none').addEventListener('click', () => mapSelectAll(false));

    document.querySelectorAll('[data-close]').forEach(b =>
      b.addEventListener('click', () => closeModal(b.getAttribute('data-close'))));
    document.querySelectorAll('.ac-modal-backdrop').forEach(bd =>
      bd.addEventListener('click', (e) => { if (e.target === bd) closeModal(bd.id); }));
    document.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') document.querySelectorAll('.ac-modal-backdrop.open').forEach(bd => closeModal(bd.id));
    });
  }

  // settings.html 동거 시 자기 패널(dexa)이 활성일 때만 폴링 — 독립 페이지에는 패널이 없어 항상 true
  function panelActive() {
    const p = document.querySelector('.set-panel[data-panel="dexa"]');
    return !p || p.classList.contains('active');
  }

  document.addEventListener('DOMContentLoaded', async () => {
    if (window.Shell) await Shell.init({ active: 'admin' });
    bind();
    await load();
    setInterval(() => { if (!document.hidden && panelActive()) load(); }, 30000);
    document.addEventListener('visibilitychange', () => { if (!document.hidden && panelActive()) load(); });
    document.addEventListener('twms:panel-shown', (e) => { if (e.detail === 'dexa') load(); });
  });
})();
