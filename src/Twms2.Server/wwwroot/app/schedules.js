/* ============================================================================
 * 스케줄(트리거) 관리 — ServerConfig.razor 의 "트리거 목록" 위젯을 정적 페이지로 이식.
 * GET /api/schedules → 트리거 목록 + 트리거별 매핑 자산 수 + 라인별 자산 그룹.
 * 쓰기: 추가(POST) / cron 수정(PUT /cron) / 자산 매핑(PUT /assets) / 실행(POST /execute) / 삭제(DELETE).
 * 실시간 아님 → 30초 폴링 + 탭 복귀 시 갱신 (Blazor 사이드바 30s 주기와 동일).
 * ==========================================================================*/
(function () {
  'use strict';

  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const $ = (id) => document.getElementById(id);

  // ── 전역 상태 ──
  const S = {
    triggers: [],     // { id,name,cronExpression,enabled,description,assetCount,assetIds }
    assetGroups: [],  // { lineName, assets:[{assetId,displayName,ip,typeName}] }
    runningIds: new Set(),
    // 자산 매핑 모달 상태
    mapTriggerId: 0, mapSelected: new Set(),
  };

  /* ════════════════ 데이터 로드 ════════════════ */
  async function load() {
    try {
      const res = await fetch('/api/schedules', { headers: { 'Accept': 'application/json' } });
      if (!res.ok) return;
      const d = await res.json();
      S.triggers = d.triggers || [];
      S.assetGroups = d.assetGroups || [];
      renderTable();
    } catch (e) { /* 무시 */ }
  }

  /* ── 쓰기 공통 (같은 출처 → 쿠키 자동 동봉, 토큰 불필요) ── */
  async function send(url, method, body) {
    const opts = { method, headers: { 'Accept': 'application/json' } };
    if (body !== undefined) { opts.headers['Content-Type'] = 'application/json'; opts.body = JSON.stringify(body); }
    const res = await fetch(url, opts);
    let data = null;
    try { data = await res.json(); } catch (e) { /* 빈 본문 */ }
    if (!res.ok) {
      const msg = (data && data.error) || `요청 실패 (${res.status})`;
      throw new Error(msg);
    }
    return data;
  }

  /* ════════════════ cron 표현식 변환 (Razor.CronToReadable 이식) ════════════════ */
  // Quartz 6필드: sec min hour dom month dow
  function cronToReadable(cron) {
    if (!cron || !cron.trim()) return cron || '';
    const parts = cron.trim().split(/\s+/);
    if (parts.length < 6) return cron;
    const min = parts[1], hour = parts[2], dom = parts[3], dow = parts[5];
    const m = parseInt(min, 10);
    if (isNaN(m)) return cron;
    const p2 = (n) => String(n).padStart(2, '0');
    if (hour === '*' && dom === '*' && dow === '?') return `매시간 :${p2(m)}`;
    const h = parseInt(hour, 10);
    if (isNaN(h)) return cron;
    if (dom === '*' && dow === '?') return `매일 ${p2(h)}:${p2(m)}`;
    if (dom === '?' && dow !== '*' && dow !== '?') {
      const dayName = { MON: '월', TUE: '화', WED: '수', THU: '목', FRI: '금', SAT: '토', SUN: '일' }[dow.toUpperCase()] || dow;
      return `매주 ${dayName} ${p2(h)}:${p2(m)}`;
    }
    return cron;
  }

  /* ════════════════ 트리거 테이블 ════════════════ */
  function renderTable() {
    const cols = ['ID', '이름', '스케줄', '상태', '설명', '자산 매핑', '작업'];
    const head = '<thead><tr>' + cols.map(c => `<th>${esc(c)}</th>`).join('') + '</tr></thead>';

    if (!S.triggers.length) {
      $('trigger-table').innerHTML = head +
        `<tbody><tr><td colspan="${cols.length}"><div class="sch-empty">등록된 트리거가 없습니다.</div></td></tr></tbody>`;
      return;
    }

    const body = S.triggers.map(t => {
      const statusChip = t.enabled
        ? '<span class="chip chip-success">활성</span>'
        : '<span class="chip chip-default">비활성</span>';
      const countChip = `<span class="chip ${t.assetCount > 0 ? 'chip-info' : 'chip-default'} sch-asset-link" data-map="${t.id}">${t.assetCount}개</span>`;
      const running = S.runningIds.has(t.id);
      const runBtn = running
        ? `<span class="sch-iconbtn" title="실행중" style="color:var(--health-inprogress);"><span class="material-symbols-outlined">hourglass_top</span></span>`
        : `<button class="sch-iconbtn sch-iconbtn-success" title="즉시 실행" data-exec="${t.id}"><span class="material-symbols-outlined">play_arrow</span></button>`;
      return `<tr>
        <td>${t.id}</td>
        <td><strong>${esc(t.name || '-')}</strong></td>
        <td><a class="sch-cron-link" data-cron="${t.id}" title="${esc(t.cronExpression || '')}">${esc(cronToReadable(t.cronExpression))}</a></td>
        <td>${statusChip}</td>
        <td class="wrap">${esc(t.description || '-')}</td>
        <td>${countChip}</td>
        <td>
          <div style="display:flex;gap:6px;">
            <button class="sch-iconbtn sch-iconbtn-info" title="자산 매핑" data-map="${t.id}"><span class="material-symbols-outlined">account_tree</span></button>
            ${runBtn}
            <button class="sch-iconbtn sch-iconbtn-error" title="삭제" data-del="${t.id}"><span class="material-symbols-outlined">delete</span></button>
          </div>
        </td>
      </tr>`;
    }).join('');

    const table = $('trigger-table');
    table.innerHTML = head + `<tbody>${body}</tbody>`;

    table.querySelectorAll('[data-cron]').forEach(el =>
      el.addEventListener('click', () => openCronModal(+el.getAttribute('data-cron'))));
    table.querySelectorAll('[data-map]').forEach(el =>
      el.addEventListener('click', () => openAssetsModal(+el.getAttribute('data-map'))));
    table.querySelectorAll('[data-exec]').forEach(el =>
      el.addEventListener('click', () => executeTrigger(+el.getAttribute('data-exec'))));
    table.querySelectorAll('[data-del]').forEach(el =>
      el.addEventListener('click', () => deleteTrigger(+el.getAttribute('data-del'))));
  }

  /* ════════════════ Cron 빌더 (MudBlazor CronBuilder 대체) ════════════════ */
  // 프리셋(매시간/매일/매주) + 시/분/요일 선택 + 직접 입력. value 는 항상 Quartz 6필드.
  function createCronBuilder(host, initial) {
    host.innerHTML = `
      <label class="sch-field-label">스케줄 (Cron)</label>
      <div class="sch-seg" data-role="mode" style="margin-bottom:10px;">
        <button class="sch-seg-btn" data-mode="hourly">매시간</button>
        <button class="sch-seg-btn" data-mode="daily">매일</button>
        <button class="sch-seg-btn" data-mode="weekly">매주</button>
        <button class="sch-seg-btn" data-mode="custom">직접 입력</button>
      </div>
      <div data-role="fields" style="display:flex;gap:10px;flex-wrap:wrap;align-items:flex-end;">
        <div data-role="dow-wrap" style="display:none;">
          <label class="sch-field-label">요일</label>
          <select class="sch-input" data-role="dow" style="width:110px;">
            <option value="MON">월</option><option value="TUE">화</option><option value="WED">수</option>
            <option value="THU">목</option><option value="FRI">금</option><option value="SAT">토</option><option value="SUN">일</option>
          </select>
        </div>
        <div data-role="hour-wrap">
          <label class="sch-field-label">시</label>
          <select class="sch-input" data-role="hour" style="width:90px;"></select>
        </div>
        <div data-role="min-wrap">
          <label class="sch-field-label">분</label>
          <select class="sch-input" data-role="min" style="width:90px;"></select>
        </div>
        <div data-role="custom-wrap" style="flex:1 1 200px;display:none;">
          <label class="sch-field-label">Cron 표현식 (sec min hour dom month dow)</label>
          <input class="sch-input" data-role="custom" type="text" style="font-family:var(--font-mono);font-variant-numeric:tabular-nums;" />
        </div>
      </div>
      <div class="sch-cron-preview" data-role="preview"></div>
      <div class="sch-cron-readable" data-role="readable"></div>`;

    const q = (sel) => host.querySelector(sel);
    const hourSel = q('[data-role="hour"]');
    const minSel = q('[data-role="min"]');
    const dowSel = q('[data-role="dow"]');
    const customInput = q('[data-role="custom"]');
    const p2 = (n) => String(n).padStart(2, '0');
    for (let i = 0; i < 24; i++) hourSel.add(new Option(p2(i), String(i)));
    for (let i = 0; i < 60; i++) minSel.add(new Option(p2(i), String(i)));

    let mode = 'daily';

    function compose() {
      if (mode === 'custom') return (customInput.value || '').trim();
      const m = minSel.value, h = hourSel.value;
      if (mode === 'hourly') return `0 ${m} * * * ?`;
      if (mode === 'daily') return `0 ${m} ${h} * * ?`;
      return `0 ${m} ${h} ? * ${dowSel.value}`; // weekly
    }

    function syncFieldVisibility() {
      q('[data-role="dow-wrap"]').style.display = mode === 'weekly' ? '' : 'none';
      q('[data-role="hour-wrap"]').style.display = (mode === 'daily' || mode === 'weekly') ? '' : 'none';
      q('[data-role="min-wrap"]').style.display = mode !== 'custom' ? '' : 'none';
      q('[data-role="custom-wrap"]').style.display = mode === 'custom' ? '' : 'none';
      host.querySelectorAll('[data-mode]').forEach(b => b.classList.toggle('active', b.getAttribute('data-mode') === mode));
    }

    function refresh() {
      const cron = compose();
      if (mode !== 'custom') customInput.value = cron;
      q('[data-role="preview"]').textContent = cron || '(비어 있음)';
      q('[data-role="readable"]').textContent = cron ? cronToReadable(cron) : '';
    }

    host.querySelectorAll('[data-mode]').forEach(b =>
      b.addEventListener('click', () => { mode = b.getAttribute('data-mode'); syncFieldVisibility(); refresh(); }));
    [hourSel, minSel, dowSel].forEach(el => el.addEventListener('change', refresh));
    customInput.addEventListener('input', refresh);

    // ── 초기값 파싱 (가능하면 프리셋에 매핑, 아니면 직접 입력) ──
    function setFromCron(cron) {
      const parts = (cron || '').trim().split(/\s+/);
      if (parts.length >= 6) {
        const min = parts[1], hour = parts[2], dom = parts[3], dow = parts[5];
        const m = parseInt(min, 10), h = parseInt(hour, 10);
        if (!isNaN(m) && hour === '*' && dom === '*' && dow === '?') {
          mode = 'hourly'; minSel.value = String(m);
        } else if (!isNaN(m) && !isNaN(h) && dom === '*' && dow === '?') {
          mode = 'daily'; minSel.value = String(m); hourSel.value = String(h);
        } else if (!isNaN(m) && !isNaN(h) && dom === '?' && dow !== '*' && dow !== '?') {
          mode = 'weekly'; minSel.value = String(m); hourSel.value = String(h); dowSel.value = dow.toUpperCase();
        } else {
          mode = 'custom'; customInput.value = cron;
        }
      } else if (cron) {
        mode = 'custom'; customInput.value = cron;
      }
      syncFieldVisibility(); refresh();
    }

    setFromCron(initial || '0 0 * * * ?');
    return { value: () => compose() };
  }

  /* ════════════════ 모달 공통 ════════════════ */
  function openModal(id) { $(id).classList.add('show'); }
  function closeModal(id) { $(id).classList.remove('show'); }

  /* ── 트리거 추가 ── */
  let addCron = null;
  function openAddModal() {
    $('add-name').value = '';
    $('add-desc').value = '';
    addCron = createCronBuilder($('add-cron-host'), '0 0 * * * ?');
    openModal('modal-add');
    $('add-name').focus();
  }
  async function submitAdd() {
    const name = $('add-name').value.trim();
    if (!name) { if (window.Shell) Shell.toast('트리거 이름을 입력하세요.'); $('add-name').focus(); return; }
    const btn = $('add-submit'); btn.disabled = true;
    try {
      await send('/api/schedules', 'POST', {
        name, description: $('add-desc').value.trim() || null, cronExpression: addCron.value(),
      });
      closeModal('modal-add');
      if (window.Shell) Shell.toast('트리거가 추가되었습니다.');
      await load();
    } catch (e) {
      if (window.Shell) Shell.toast(e.message);
    } finally { btn.disabled = false; }
  }

  /* ── 스케줄(cron) 수정 ── */
  let editCron = null, editCronTriggerId = 0;
  function openCronModal(id) {
    const t = S.triggers.find(x => x.id === id);
    if (!t) return;
    editCronTriggerId = id;
    $('cron-trigger-name').textContent = t.name || '';
    editCron = createCronBuilder($('edit-cron-host'), t.cronExpression || '0 0 * * * ?');
    openModal('modal-cron');
  }
  async function submitCron() {
    const cron = editCron.value();
    if (!cron) { if (window.Shell) Shell.toast('Cron 표현식을 입력하세요.'); return; }
    const btn = $('cron-submit'); btn.disabled = true;
    try {
      await send(`/api/schedules/${editCronTriggerId}/cron`, 'PUT', { cronExpression: cron });
      closeModal('modal-cron');
      if (window.Shell) Shell.toast('스케줄이 수정되었습니다.');
      await load();
    } catch (e) {
      if (window.Shell) Shell.toast(e.message);
    } finally { btn.disabled = false; }
  }

  /* ── 자산 매핑 ── */
  function openAssetsModal(id) {
    const t = S.triggers.find(x => x.id === id);
    if (!t) return;
    S.mapTriggerId = id;
    S.mapSelected = new Set(t.assetIds || []);
    $('assets-trigger-name').textContent = t.name || '';
    renderAssetGroups();
    openModal('modal-assets');
  }

  function renderAssetGroups() {
    const host = $('assets-groups');
    if (!S.assetGroups.length) {
      host.innerHTML = '<div class="sch-empty">등록된 자산이 없습니다.</div>';
      updateSelCount();
      return;
    }
    host.innerHTML = S.assetGroups.map((g, gi) => {
      const total = g.assets.length;
      const sel = g.assets.filter(a => S.mapSelected.has(a.assetId)).length;
      const rows = g.assets.map(a => {
        const checked = S.mapSelected.has(a.assetId) ? 'checked' : '';
        const ip = a.ip ? `<span class="sch-ip">${esc(a.ip)}</span>` : '';
        return `<label class="sch-asset-row">
          <input type="checkbox" class="sch-check" data-asset="${a.assetId}" ${checked} />
          <span>${esc(a.displayName)}</span>${ip}</label>`;
      }).join('');
      const allChecked = total > 0 && sel === total ? 'checked' : '';
      return `<div class="sch-group">
        <label class="sch-group-header">
          <input type="checkbox" class="sch-check" data-group="${gi}" ${allChecked} />
          <span>${esc(g.lineName)}</span>
          <span class="sch-group-count" data-gcount="${gi}">(${sel} / ${total})</span>
        </label>
        ${rows}</div>`;
    }).join('');

    host.querySelectorAll('[data-asset]').forEach(cb =>
      cb.addEventListener('change', () => {
        const aid = +cb.getAttribute('data-asset');
        if (cb.checked) S.mapSelected.add(aid); else S.mapSelected.delete(aid);
        refreshGroupHeaders();
      }));
    host.querySelectorAll('[data-group]').forEach(cb =>
      cb.addEventListener('change', () => {
        const gi = +cb.getAttribute('data-group');
        const grp = S.assetGroups[gi];
        grp.assets.forEach(a => { if (cb.checked) S.mapSelected.add(a.assetId); else S.mapSelected.delete(a.assetId); });
        renderAssetGroups();
      }));
    updateSelCount();
  }

  function refreshGroupHeaders() {
    S.assetGroups.forEach((g, gi) => {
      const total = g.assets.length;
      const sel = g.assets.filter(a => S.mapSelected.has(a.assetId)).length;
      const c = document.querySelector(`[data-gcount="${gi}"]`);
      if (c) c.textContent = `(${sel} / ${total})`;
      const gcb = document.querySelector(`[data-group="${gi}"]`);
      if (gcb) gcb.checked = total > 0 && sel === total;
    });
    updateSelCount();
  }

  function updateSelCount() { $('assets-selcount').textContent = `${S.mapSelected.size}개 선택`; }

  function selectAllAssets(on) {
    S.assetGroups.forEach(g => g.assets.forEach(a => { if (on) S.mapSelected.add(a.assetId); else S.mapSelected.delete(a.assetId); }));
    renderAssetGroups();
  }

  async function submitMapping() {
    const btn = $('assets-submit'); btn.disabled = true;
    try {
      await send(`/api/schedules/${S.mapTriggerId}/assets`, 'PUT', { assetIds: [...S.mapSelected] });
      closeModal('modal-assets');
      if (window.Shell) Shell.toast('자산 매핑이 저장되었습니다.');
      await load();
    } catch (e) {
      if (window.Shell) Shell.toast(e.message);
    } finally { btn.disabled = false; }
  }

  /* ── 즉시 실행 ── */
  async function executeTrigger(id) {
    const t = S.triggers.find(x => x.id === id);
    if (t && t.assetCount === 0) {
      if (window.Shell) Shell.toast('연결된 자산이 없습니다. 먼저 자산을 매핑하세요.');
      return;
    }
    S.runningIds.add(id);
    renderTable();
    try {
      await send(`/api/schedules/${id}/execute`, 'POST');
      if (window.Shell) Shell.toast('트리거 실행을 요청했습니다.');
    } catch (e) {
      if (window.Shell) Shell.toast(e.message);
    } finally {
      // 백업은 오래 걸리므로(fire-and-forget) 잠시 후 진행 표시 해제
      setTimeout(() => { S.runningIds.delete(id); renderTable(); }, 3000);
    }
  }

  /* ── 삭제 ── */
  async function deleteTrigger(id) {
    const t = S.triggers.find(x => x.id === id);
    if (!confirm(`트리거 '${t ? t.name : id}'을(를) 삭제하시겠습니까?`)) return;
    try {
      await send(`/api/schedules/${id}`, 'DELETE');
      if (window.Shell) Shell.toast('트리거가 삭제되었습니다.');
      await load();
    } catch (e) {
      if (window.Shell) Shell.toast(e.message);
    }
  }

  /* ════════════════ 바인딩 ════════════════ */
  function bind() {
    $('btn-add').addEventListener('click', openAddModal);
    $('btn-refresh').addEventListener('click', load);
    $('add-submit').addEventListener('click', submitAdd);
    $('cron-submit').addEventListener('click', submitCron);
    $('assets-submit').addEventListener('click', submitMapping);
    $('assets-all').addEventListener('click', () => selectAllAssets(true));
    $('assets-none').addEventListener('click', () => selectAllAssets(false));

    // 모달 닫기 (취소 버튼 + 배경 클릭)
    document.querySelectorAll('.sch-modal-backdrop').forEach(bd => {
      bd.addEventListener('click', (e) => { if (e.target === bd) bd.classList.remove('show'); });
      bd.querySelectorAll('[data-close]').forEach(b => b.addEventListener('click', () => bd.classList.remove('show')));
    });
    document.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') document.querySelectorAll('.sch-modal-backdrop.show').forEach(bd => bd.classList.remove('show'));
    });
  }

  document.addEventListener('DOMContentLoaded', async () => {
    if (window.Shell) await Shell.init({ active: '' });
    bind();
    await load();
    setInterval(load, 30000);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) load(); });
  });
})();
