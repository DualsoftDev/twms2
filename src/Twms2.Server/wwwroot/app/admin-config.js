/* ============================================================================
 * DEXA 설정(관리) — ServerConfig.razor 의 에이전트 목록 + 트리거 관리를 정적 페이지로 이식.
 * GET /api/admin/config 스냅샷 1회 조회 → 두 표 렌더. 30초 폴링 + 탭 복귀 시 갱신.
 * 쓰기: 트리거 추가/스케줄(cron) 수정/실행/삭제 (같은 출처 → 쿠키 자동 동봉, JS 토큰 불필요).
 * 에이전트 재시작 + 트리거↔자산 매핑 편집 포함(Inc12).
 * 수동 백업 테스트: 필터 테이블에서 선택한 자산을 한 대씩 순차 백업 —
 * 큐는 이 페이지 JS 에만 존재(이탈 시 잔여 자동 중단), 진행 감지는 액션 id 기반 폴링.
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
        ? `<button class="ac-chip-btn ac-chip-btn-on" data-act="toggle" data-id="${t.id}" title="클릭하여 비활성화">활성</button>`
        : `<button class="ac-chip-btn ac-chip-btn-off" data-act="toggle" data-id="${t.id}" title="클릭하여 활성화">비활성</button>`;
      const mapChip = `<span class="chip ${t.mappedAssetCount > 0 ? 'chip-info' : 'chip-default'}">${t.mappedAssetCount}개</span>`;
      const running = S.runningTriggerIds.has(t.id);
      const runBtn = running
        ? `<span class="ac-spinner" title="실행 중"></span>`
        : `<button class="ac-iconbtn ac-iconbtn-success" data-act="run" data-id="${t.id}" title="즉시 실행"><span class="material-symbols-outlined">play_arrow</span></button>`;
      return `<tr>
        <td>${t.id}</td>
        <td><a class="ac-cron-link" data-act="rename" data-id="${t.id}" title="이름 변경">${esc(t.name || '-')}</a></td>
        <td><a class="ac-cron-link" data-act="cron" data-id="${t.id}" title="${esc(t.cronExpression || '')}">${esc(cronToReadable(t.cronExpression))}</a></td>
        <td class="wrap" title="${esc(t.description || '')}">${esc(t.description || '-')}</td>
        <td>${mapChip}</td>
        <td>${stateChip}</td>
        <td><div class="ac-actions">
          <button class="ac-iconbtn ac-iconbtn-info" data-act="map" data-id="${t.id}" title="자산 매핑"><span class="material-symbols-outlined">account_tree</span></button>
          ${runBtn}
          <button class="ac-iconbtn ac-iconbtn-danger" data-act="del" data-id="${t.id}" title="삭제"><span class="material-symbols-outlined">delete</span></button>
        </div></td>
      </tr>`;
    }).join('');
    host.innerHTML = `<div class="ac-table-wrap"><table class="nm-table">
      <thead><tr><th>ID</th><th>이름</th><th>스케줄</th><th>설명</th><th>자산 매핑</th><th>상태</th><th>작업</th></tr></thead>
      <tbody>${body}</tbody></table></div>`;

    host.querySelectorAll('[data-act]').forEach(el => {
      const id = parseInt(el.getAttribute('data-id'), 10);
      const act = el.getAttribute('data-act');
      el.addEventListener('click', () => {
        if (act === 'run') executeTrigger(id);
        else if (act === 'del') deleteTrigger(id);
        else if (act === 'cron') openCronModal(id);
        else if (act === 'map') openMapModal(id);
        else if (act === 'rename') renameTrigger(id);
        else if (act === 'toggle') toggleTriggerEnabled(id);
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

  async function renameTrigger(id) {
    const t = S.triggers.find(x => x.id === id);
    if (!t) return;
    const input = window.prompt(`트리거 '${t.name}'의 새 이름을 입력하세요.`, t.name || '');
    if (input == null) return;
    const name = input.trim();
    if (!name) { toast('트리거 이름을 입력해주세요.'); return; }
    if (name === t.name) return;
    try {
      const r = await fetch(`/api/admin/config/triggers/${id}/name`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name }),
      });
      if (r.ok) { toast('트리거 이름이 변경되었습니다.'); await load(); }
      else toast((await safeErr(r)) || '트리거 이름 변경 실패');
    } catch (e) { toast('트리거 이름 변경 실패: ' + e.message); }
  }

  async function toggleTriggerEnabled(id) {
    const t = S.triggers.find(x => x.id === id);
    if (!t) return;
    const next = !t.enabled;
    if (!confirm(`트리거 '${t.name}'을(를) ${next ? '활성화' : '비활성화'}하시겠습니까?${next ? '' : '\n비활성화하면 스케줄 백업이 실행되지 않습니다.'}`)) return;
    try {
      const r = await fetch(`/api/admin/config/triggers/${id}/enabled`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ enabled: next }),
      });
      if (r.ok) { toast(`트리거가 ${next ? '활성화' : '비활성화'}되었습니다.`); await load(); }
      else toast((await safeErr(r)) || '트리거 활성 상태 변경 실패');
    } catch (e) { toast('트리거 활성 상태 변경 실패: ' + e.message); }
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

  /* ════════════════ 수동 백업 테스트 (선택 자산 순차 실행) ════════════════
   * DEXA 백업은 fire & forget 이라 완료 응답이 없다. 대신 백업 시작 시 DEXA DB 에
   * 액션 행이 생기고 종료 시 그 행에 결과가 영구 기록되므로,
   * 요청 직전 최신 액션 id(baseline)를 잡고 "baseline 보다 큰 새 행 등장 → 그 행의 종료"를
   * 폴링으로 관측한다 (health 비교보다 견고 — 서버 15s 캐시 지연/동일 결과 재백업에도 오판 없음).
   * 순차 큐는 이 함수 스코프에만 존재 → 페이지 이탈 시 다음 명령이 나가지 않아 자동 중단. */
  const BT = {
    assets: [],            // GET /api/assets 스냅샷
    selected: new Set(),   // 체크된 assetId
    q: '', line: '', type: '', health: '',
    running: false,
    stopReq: false,
    currentId: null,       // 지금 백업 중인 자산 (행 하이라이트)
    res: new Map(),        // assetId → { state, label, spin }
    page: 1,
    manualPage: false,     // 실행 중 사용자가 직접 페이지를 넘겼으면 자동 추적(현재 자산 페이지로 이동) 중단
  };
  const BT_PAGE_SIZE = 15;         // 통계 자산별 상세 테이블과 동일
  const BT_POLL = 5000;            // 상태 폴링 주기 (서버 캐시 15s 보다 촘촘하면 충분)
  const BT_START_TIMEOUT = 90e3;   // 시작(새 액션 행 등장) 감지 상한 — 캐시 15s×2단 지연 + 큐잉 여유
  const BT_FINISH_TIMEOUT = 600e3; // 완료 대기 상한 — 3분대 백업 실측 + 여유 (원본 Blazor 5분 → 10분)

  const btSleep = (ms) => new Promise(r => setTimeout(r, ms));
  function btBeforeUnload(e) { e.preventDefault(); e.returnValue = ''; return ''; }

  async function btFetchStatus(id) {
    const r = await fetch(`/api/assets/${id}/backup-status`, { headers: { 'Accept': 'application/json' } });
    if (!r.ok) throw new Error('HTTP ' + r.status);
    return r.json();
  }

  async function btLoadAssets() {
    if (BT.running || !$('bt-host')) return;
    try {
      const r = await fetch('/api/assets', { headers: { 'Accept': 'application/json' } });
      if (!r.ok) return;
      const d = await r.json();
      BT.assets = d.assets || [];
      btFillSelect($('bt-line'), d.lineNames || [], BT.line, '전체 라인');
      btFillSelect($('bt-type'), d.typeNames || [], BT.type, '전체 타입');
      // 목록에서 사라진 자산은 선택 해제
      const ids = new Set(BT.assets.map(a => a.assetId));
      Array.from(BT.selected).forEach(id => { if (!ids.has(id)) BT.selected.delete(id); });
      renderBT();
    } catch (e) { /* 무시 — 다음 폴링에서 재시도 */ }
  }

  function btFillSelect(sel, names, cur, allLabel) {
    if (!sel) return;
    sel.innerHTML = `<option value="">${esc(allLabel)}</option>`
      + names.map(n => `<option value="${esc(n)}"${n === cur ? ' selected' : ''}>${esc(n)}</option>`).join('');
  }

  function btFiltered() {
    const q = BT.q.trim().toLowerCase();
    return BT.assets.filter(a =>
      (!q || (a.name || '').toLowerCase().includes(q) || (a.ip || '').toLowerCase().includes(q)) &&
      (!BT.line || (a.lineName || '') === BT.line) &&
      (!BT.type || (a.typeName || '') === BT.type) &&
      (!BT.health || (a.health || 'unknown') === BT.health));
  }

  function btSetRes(id, state, label, spin) {
    BT.res.set(id, { state, label, spin: !!spin });
    renderBT();
  }

  const BT_HEALTH_CHIP = { backedup: 'chip-success', unchanged: 'chip-info', failed: 'chip-error', inprogress: 'chip-warning' };
  const BT_RES_CHIP = { backedup: 'chip-success', unchanged: 'chip-info', failed: 'chip-error', incomplete: 'chip-error', error: 'chip-error', nostart: 'chip-error' };

  function btResultCell(id) {
    const r = BT.res.get(id);
    if (!r) return `<span style="color:var(--c-outline);font-size:12px;">-</span>`;
    if (r.spin)
      return `<span style="display:inline-flex;align-items:center;gap:6px;font-size:12px;color:var(--c-on-surface-variant);"><span class="ac-spinner" style="width:14px;height:14px;"></span>${esc(r.label)}</span>`;
    const title = r.state === 'timeout' ? '완료 신호를 제한시간 내 받지 못했습니다. 백업은 서버에서 계속 진행 중일 수 있으니 백업 이력에서 확인하세요.'
      : r.state === 'nostart' ? 'DEXA 가 백업을 시작하지 않았습니다. 에이전트 오프라인/미할당이거나 요청이 거부되었을 수 있습니다.'
      : '';
    return `<span class="chip ${BT_RES_CHIP[r.state] || 'chip-default'}"${title ? ` title="${esc(title)}"` : ''}>${esc(r.label)}</span>`;
  }

  function renderBT() {
    const host = $('bt-host');
    if (!host) return;
    const list = btFiltered();
    const visSel = list.filter(a => BT.selected.has(a.assetId));

    const cnt = $('bt-selcount'); if (cnt) cnt.textContent = `${visSel.length}개 선택`;
    const run = $('bt-run'); if (run) { run.style.display = BT.running ? 'none' : ''; run.disabled = !visSel.length; }
    const stop = $('bt-stop'); if (stop) { stop.style.display = BT.running ? '' : 'none'; stop.disabled = BT.stopReq; }
    const refresh = $('bt-refresh'); if (refresh) refresh.disabled = BT.running;

    if (!list.length) {
      host.innerHTML = `<div class="ac-empty">${BT.assets.length ? '조건에 맞는 자산이 없습니다.' : '등록된 자산이 없습니다.'}</div>`;
      return;
    }

    // 페이징 — 실행 중에는 현재 백업 중인 자산의 페이지를 자동 추적(직접 넘기면 중단)
    const pages = Math.max(1, Math.ceil(list.length / BT_PAGE_SIZE));
    if (BT.running && BT.currentId != null && !BT.manualPage) {
      const idx = list.findIndex(a => a.assetId === BT.currentId);
      if (idx >= 0) BT.page = Math.floor(idx / BT_PAGE_SIZE) + 1;
    }
    if (BT.page > pages) BT.page = pages;
    if (BT.page < 1) BT.page = 1;
    const pageList = list.slice((BT.page - 1) * BT_PAGE_SIZE, BT.page * BT_PAGE_SIZE);

    const allOn = visSel.length === list.length && list.length > 0;
    const dis = BT.running ? ' disabled' : '';
    const cb = 'style="width:16px;height:16px;accent-color:var(--c-primary);cursor:pointer;"';
    const rows = pageList.map(a => {
      const agent = a.agentName
        ? `${esc(a.agentName)} <span class="chip ${a.agentOnline ? 'chip-success' : 'chip-default'}">${a.agentOnline ? '온라인' : '오프라인'}</span>`
        : `<span style="color:var(--c-outline);">자동</span>`;
      return `<tr${BT.currentId === a.assetId ? ' style="background:var(--c-surface-container-low);"' : ''}>
        <td><input type="checkbox" data-bt="${a.assetId}" ${BT.selected.has(a.assetId) ? 'checked' : ''}${dis} ${cb} /></td>
        <td>${esc(a.name || '-')}</td>
        <td>${esc(a.lineName || '-')}</td>
        <td>${esc(a.typeName || '-')}</td>
        <td>${esc(a.ip || '-')}</td>
        <td>${agent}</td>
        <td><span class="chip ${BT_HEALTH_CHIP[a.health] || 'chip-default'}">${esc(a.healthLabel || '-')}</span></td>
        <td>${fmtDateTime(a.lastBackupTime)}</td>
        <td>${btResultCell(a.assetId)}</td>
      </tr>`;
    }).join('');
    const pager = pages > 1 ? `<div style="display:flex;align-items:center;justify-content:flex-end;gap:10px;margin-top:10px;">
      <button class="ac-btn" id="bt-prev" ${BT.page <= 1 ? 'disabled' : ''} title="이전 페이지"><span class="material-symbols-outlined">chevron_left</span></button>
      <span style="font-size:12px;color:var(--c-on-surface-variant);font-variant-numeric:tabular-nums;">${BT.page} / ${pages} 페이지 · 총 ${list.length}개</span>
      <button class="ac-btn" id="bt-next" ${BT.page >= pages ? 'disabled' : ''} title="다음 페이지"><span class="material-symbols-outlined">chevron_right</span></button>
    </div>` : '';

    host.innerHTML = `<div class="ac-table-wrap"><table class="nm-table">
      <thead><tr>
        <th><input type="checkbox" id="bt-all" ${allOn ? 'checked' : ''}${dis} ${cb} title="필터 결과 전체(모든 페이지) 선택/해제" /></th>
        <th>이름</th><th>라인</th><th>타입</th><th>IP</th><th>에이전트</th><th>현재 상태</th><th>마지막 백업</th><th>테스트 결과</th>
      </tr></thead><tbody>${rows}</tbody></table></div>${pager}`;

    host.querySelectorAll('input[data-bt]').forEach(el => el.addEventListener('change', () => {
      const id = parseInt(el.getAttribute('data-bt'), 10);
      if (el.checked) BT.selected.add(id); else BT.selected.delete(id);
      renderBT();
    }));
    const all = host.querySelector('#bt-all');
    if (all) all.addEventListener('change', () => {
      list.forEach(a => { if (all.checked) BT.selected.add(a.assetId); else BT.selected.delete(a.assetId); });
      renderBT();
    });
    const movePage = (d) => { BT.page += d; if (BT.running) BT.manualPage = true; renderBT(); };
    const prev = host.querySelector('#bt-prev'); if (prev) prev.addEventListener('click', () => movePage(-1));
    const next = host.querySelector('#bt-next'); if (next) next.addEventListener('click', () => movePage(1));
  }

  async function btRun() {
    if (BT.running) return;
    const order = btFiltered().map(a => a.assetId).filter(id => BT.selected.has(id));
    if (!order.length) { toast('백업할 자산을 선택해주세요.'); return; }
    if (!confirm(`선택한 ${order.length}개 자산을 위에서부터 한 대씩 순서대로 백업합니다.\n진행 중 페이지를 벗어나면 남은 자산은 실행되지 않습니다.\n시작하시겠습니까?`)) return;

    BT.running = true;
    BT.stopReq = false;
    BT.manualPage = false;
    BT.res.clear();
    order.forEach(id => BT.res.set(id, { state: 'pending', label: '대기' }));
    window.addEventListener('beforeunload', btBeforeUnload);
    renderBT();
    try {
      for (const id of order) {
        if (BT.stopReq) { BT.res.set(id, { state: 'skipped', label: '건너뜀' }); continue; }
        BT.currentId = id;
        await btBackupOne(id);
        BT.currentId = null;
      }
    } finally {
      BT.running = false;
      BT.currentId = null;
      window.removeEventListener('beforeunload', btBeforeUnload);
      renderBT();
      load();          // 상단 표(에이전트/트리거) + 다음 btLoadAssets 로 health 갱신
      btLoadAssets();
    }
    toast(BT.stopReq ? '수동 백업 테스트가 중지되었습니다.' : '수동 백업 테스트가 완료되었습니다.');
  }

  async function btBackupOne(id) {
    // 1) baseline — 요청 직전 최신 액션 id. 이보다 큰 id 의 새 행 = 이번 백업.
    let baseline = 0;
    try { baseline = (await btFetchStatus(id)).latestActionId || 0; }
    catch (e) { btSetRes(id, 'error', '상태 조회 실패'); return; }

    // 2) 백업 명령 (fire & forget)
    btSetRes(id, 'request', '요청 중', true);
    try {
      const r = await fetch(`/api/assets/${id}/backup`, { method: 'POST' });
      if (!r.ok) { btSetRes(id, 'error', (await safeErr(r)) || `요청 실패 (${r.status})`); return; }
    } catch (e) { btSetRes(id, 'error', '요청 실패: ' + e.message); return; }

    // 3) 시작 감지 — baseline 보다 큰 새 액션 행 등장 대기.
    //    경과시간(Date.now) 기준 — 백그라운드 탭 타이머 스로틀링에도 창 길이가 유지된다.
    btSetRes(id, 'waitstart', '시작 대기', true);
    let action = null;
    const t0 = Date.now();
    while (Date.now() - t0 < BT_START_TIMEOUT) {
      await btSleep(BT_POLL);
      try {
        const st = await btFetchStatus(id);
        action = (st.actions || []).filter(a => a.id > baseline).sort((x, y) => x.id - y.id)[0] || null;
      } catch (e) { /* 일시 오류 — 다음 폴에서 재시도 */ }
      if (action) break;
    }
    if (!action) { btSetRes(id, 'nostart', '시작 감지 실패'); return; }

    // 4) 완료 대기 — 해당 액션 행의 종료 (result != inprogress 는 영구 기록이라 유실 없음)
    if (action.result === 'inprogress') {
      btSetRes(id, 'inprogress', '작업중', true);
      const t1 = Date.now();
      while (Date.now() - t1 < BT_FINISH_TIMEOUT) {
        await btSleep(BT_POLL);
        try {
          const st = await btFetchStatus(id);
          const cur = (st.actions || []).find(a => a.id === action.id);
          if (cur) action = cur;
        } catch (e) { /* 재시도 */ }
        if (action.result !== 'inprogress') break;
      }
    }
    if (action.result === 'inprogress') { btSetRes(id, 'timeout', '시간 초과'); return; }
    // 서버 result 키 계약: backedup(백업 갱신)/unchanged(변경 없음)/failed(작업 실패)/incomplete(미완료)
    btSetRes(id, action.result, action.resultLabel || action.result);
  }

  function bindBackupTest() {
    if (!$('bt-host')) return;
    $('bt-run').addEventListener('click', btRun);
    $('bt-stop').addEventListener('click', () => {
      if (!BT.running || BT.stopReq) return;
      BT.stopReq = true;
      toast('진행 중인 백업이 끝나면 중지됩니다. (이미 시작된 백업은 서버에서 계속 진행)');
      renderBT();
    });
    $('bt-refresh').addEventListener('click', btLoadAssets);
    $('bt-search').addEventListener('input', (e) => { BT.q = e.target.value; BT.page = 1; renderBT(); });
    $('bt-line').addEventListener('change', (e) => { BT.line = e.target.value; BT.page = 1; renderBT(); });
    $('bt-type').addEventListener('change', (e) => { BT.type = e.target.value; BT.page = 1; renderBT(); });
    $('bt-health').addEventListener('change', (e) => { BT.health = e.target.value; BT.page = 1; renderBT(); });
    btLoadAssets();
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
    bindBackupTest();

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
    setInterval(() => { if (!document.hidden && panelActive()) { load(); btLoadAssets(); } }, 30000);
    document.addEventListener('visibilitychange', () => { if (!document.hidden && panelActive()) { load(); btLoadAssets(); } });
    document.addEventListener('twms:panel-shown', (e) => { if (e.detail === 'dexa') { load(); btLoadAssets(); } });
  });
})();
