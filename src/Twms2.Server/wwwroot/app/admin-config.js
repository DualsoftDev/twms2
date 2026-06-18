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
      return `<tr>
        <td>${esc(a.name || '-')}</td>
        <td>${esc(a.ip || '-')}</td>
        <td>${esc(a.swVersion || '-')}</td>
        <td>${statusChip}</td>
        <td>${fmtDateTime(a.connected)}</td>
      </tr>`;
    }).join('');
    host.innerHTML = `<div class="ac-table-wrap"><table class="nm-table">
      <thead><tr><th>이름</th><th>IP</th><th>버전</th><th>상태</th><th>접속 시간</th></tr></thead>
      <tbody>${body}</tbody></table></div>`;
  }

  /* ════════════════ 트리거 목록 ════════════════ */
  // ServerConfig.razor 의 CronToReadable 이식 (Quartz 6필드: 초 분 시 일 월 요일)
  function cronToReadable(cron) {
    if (!cron || !cron.trim()) return cron || '';
    const parts = cron.trim().split(/\s+/);
    if (parts.length < 6) return cron;
    const min = parts[1], hour = parts[2], dom = parts[3], dow = parts[5];
    const m = parseInt(min, 10);
    if (!Number.isInteger(m)) return cron;
    const p2 = (n) => String(n).padStart(2, '0');
    if (hour === '*' && dom === '*' && dow === '?') return `매시간 :${p2(m)}`;
    const h = parseInt(hour, 10);
    if (!Number.isInteger(h)) return cron;
    if (dom === '*' && dow === '?') return `매일 ${p2(h)}:${p2(m)}`;
    if (dom === '?' && dow !== '*' && dow !== '?') {
      const day = { MON: '월', TUE: '화', WED: '수', THU: '목', FRI: '금', SAT: '토', SUN: '일' }[dow.toUpperCase()] || dow;
      return `매주 ${day} ${p2(h)}:${p2(m)}`;
    }
    return cron;
  }

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
    const cron = $('add-cron').value.trim();
    const desc = $('add-desc').value.trim();
    if (!name) { toast('트리거 이름을 입력해주세요.'); return; }
    if (!cron) { toast('Cron 표현식을 입력해주세요.'); return; }
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
    const cron = $('cron-value').value.trim();
    if (id == null) return;
    if (!cron) { toast('Cron 표현식을 입력해주세요.'); return; }
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

  /* ════════════════ 모달 제어 ════════════════ */
  function openModal(id) { $(id).classList.add('open'); }
  function closeModal(id) { $(id).classList.remove('open'); }

  function openAddModal() {
    $('add-name').value = '';
    $('add-cron').value = '';
    $('add-desc').value = '';
    openModal('add-modal');
    $('add-name').focus();
  }

  function openCronModal(id) {
    const t = S.triggers.find(x => x.id === id);
    if (!t) return;
    S.cronEditId = id;
    $('cron-trigger-label').textContent = `${t.name} — Cron 표현식`;
    $('cron-value').value = t.cronExpression || '';
    openModal('cron-modal');
    $('cron-value').focus();
  }

  /* ── 이벤트 바인딩 ── */
  function bind() {
    $('show-offline').addEventListener('change', (e) => { S.showOffline = e.target.checked; renderAgents(); });
    $('agents-refresh').addEventListener('click', load);
    $('triggers-refresh').addEventListener('click', load);
    $('trigger-add').addEventListener('click', openAddModal);
    $('add-save').addEventListener('click', addTrigger);
    $('cron-save').addEventListener('click', saveCron);

    document.querySelectorAll('[data-close]').forEach(b =>
      b.addEventListener('click', () => closeModal(b.getAttribute('data-close'))));
    document.querySelectorAll('.ac-modal-backdrop').forEach(bd =>
      bd.addEventListener('click', (e) => { if (e.target === bd) closeModal(bd.id); }));
    document.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') document.querySelectorAll('.ac-modal-backdrop.open').forEach(bd => closeModal(bd.id));
    });
  }

  document.addEventListener('DOMContentLoaded', async () => {
    if (window.Shell) await Shell.init({ active: 'admin' });
    bind();
    await load();
    setInterval(load, 30000);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) load(); });
  });
})();
