/* ============================================================================
 * 레이아웃 관리(Admin/LayoutManagement) — Admin/LayoutManagement.razor 의
 * 레이아웃 목록(테이블) + 생성/복제/이름변경/순서변경/삭제를 정적 페이지로 이식.
 * GET /api/admin/layout (목록 + 도면 썸네일).
 * 쓰기: POST(생성/복제) · PUT(이름/순서) · DELETE(삭제).
 * 편집기(/admin/layout/{id})는 Blazor 페이지 유지 — 단순 링크로 진입.
 * 30초 폴링 + 탭 복귀 시 갱신.
 * ==========================================================================*/
(function () {
  'use strict';

  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const $ = (id) => document.getElementById(id);

  let LAYOUTS = [];

  function fmtDateTime(s) {
    if (!s) return '-';
    const d = new Date(s); if (isNaN(d)) return s;
    const p = (n) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
  }

  /* ── 데이터 로드 ── */
  async function load() {
    try {
      const res = await fetch('/api/admin/layout', { headers: { 'Accept': 'application/json' } });
      if (!res.ok) return;
      const d = await res.json();
      LAYOUTS = d.layouts || [];
      render();
    } catch (e) { /* 무시 */ }
  }

  /* ── 테이블 렌더 (MudTable RowTemplate 이식) ── */
  function render() {
    const head = `<thead><tr>
      <th style="width:50px;">#</th>
      <th>이름</th>
      <th style="width:90px;">도면</th>
      <th style="width:150px;">수정일</th>
      <th style="width:360px;">액션</th>
    </tr></thead>`;

    if (LAYOUTS.length === 0) {
      $('lm-table').innerHTML = head +
        `<tbody><tr><td colspan="5"><div class="lm-empty">레이아웃이 없습니다. 새 레이아웃을 추가하세요.</div></td></tr></tbody>`;
      return;
    }

    const canDelete = LAYOUTS.length > 1;
    const body = LAYOUTS.map((l, i) => {
      const thumb = l.imagePath
        ? `<a href="${esc(l.imagePath)}" target="_blank"><img class="lm-thumb" src="${esc(l.imagePath)}" alt="도면" /></a>`
        : `<span class="material-symbols-outlined lm-thumb-none" title="도면 없음">hide_image</span>`;
      return `<tr>
        <td>${i + 1}</td>
        <td><span class="lm-name">${esc(l.name)}</span></td>
        <td>${thumb}</td>
        <td>${fmtDateTime(l.updatedAt)}</td>
        <td>
          <div class="lm-actions">
            <a class="lm-iconbtn lm-iconbtn-edit" href="/admin/layout/${l.id}"><span class="material-symbols-outlined">edit</span>편집</a>
            <button class="lm-iconbtn" data-act="duplicate" data-id="${l.id}"><span class="material-symbols-outlined">content_copy</span>복제</button>
            <button class="lm-iconbtn" data-act="rename" data-id="${l.id}"><span class="material-symbols-outlined">drive_file_rename_outline</span>이름변경</button>
            <a class="lm-iconbtn" href="/admin/layout/${l.id}?tab=dexa"><span class="material-symbols-outlined">file_upload</span>이전 TWMS 가져오기</a>
            <button class="lm-iconbtn lm-iconbtn-danger" data-act="delete" data-id="${l.id}" ${canDelete ? '' : 'disabled'}><span class="material-symbols-outlined">delete</span>삭제</button>
          </div>
        </td>
      </tr>`;
    }).join('');

    $('lm-table').innerHTML = head + `<tbody>${body}</tbody>`;

    $('lm-table').querySelectorAll('button[data-act]').forEach(btn => {
      const id = parseInt(btn.getAttribute('data-id'), 10);
      const act = btn.getAttribute('data-act');
      btn.addEventListener('click', () => {
        if (act === 'duplicate') duplicateLayout(id);
        else if (act === 'rename') renameLayout(id);
        else if (act === 'delete') deleteLayout(id);
      });
    });
  }

  function findLayout(id) { return LAYOUTS.find(l => l.id === id); }

  /* ════════════════ 입력 모달 (생성 / 복제 / 이름변경) ════════════════ */
  let _inputResolve = null;
  function promptName(title, message, defaultValue) {
    $('lm-input-title').textContent = title;
    $('lm-input-msg').textContent = message || '';
    const field = $('lm-input-field');
    field.value = defaultValue || '';
    $('lm-input-overlay').classList.add('show');
    field.focus(); field.select();
    return new Promise((resolve) => { _inputResolve = resolve; });
  }
  function closeInput(value) {
    $('lm-input-overlay').classList.remove('show');
    const r = _inputResolve; _inputResolve = null;
    if (r) r(value);
  }

  /* ════════════════ 확인 모달 (삭제) ════════════════ */
  let _confirmResolve = null;
  function confirmBox(title, message) {
    $('lm-confirm-title').textContent = title;
    $('lm-confirm-msg').textContent = message || '';
    $('lm-confirm-overlay').classList.add('show');
    return new Promise((resolve) => { _confirmResolve = resolve; });
  }
  function closeConfirm(value) {
    $('lm-confirm-overlay').classList.remove('show');
    const r = _confirmResolve; _confirmResolve = null;
    if (r) r(value);
  }

  /* ════════════════ 쓰기 호출 ════════════════ */
  async function api(url, method, payload) {
    try {
      const opts = { method, headers: { 'Accept': 'application/json' } };
      if (payload !== undefined) {
        opts.headers['Content-Type'] = 'application/json';
        opts.body = JSON.stringify(payload);
      }
      const res = await fetch(url, opts);
      if (!res.ok) {
        let msg = '요청에 실패했습니다.';
        try { const e = await res.json(); if (e && e.error) msg = e.error; } catch (_) {}
        if (window.Shell) Shell.toast(msg);
        return null;
      }
      return await res.json().catch(() => ({}));
    } catch (e) {
      if (window.Shell) Shell.toast('요청에 실패했습니다: ' + e.message);
      return null;
    }
  }

  async function createLayout() {
    const name = await promptName('새 레이아웃', '새 레이아웃 이름을 입력하세요.');
    if (!name || !name.trim()) return;
    const r = await api('/api/admin/layout', 'POST', { name: name.trim() });
    if (r) { if (window.Shell) Shell.toast(`레이아웃 '${name.trim()}'이(가) 생성되었습니다.`); await load(); }
  }

  async function duplicateLayout(id) {
    const l = findLayout(id); if (!l) return;
    const name = await promptName('레이아웃 복제', `'${l.name}' 복제본 이름을 입력하세요.`, `${l.name} (복사)`);
    if (!name || !name.trim()) return;
    const r = await api(`/api/admin/layout/${id}/duplicate`, 'POST', { name: name.trim() });
    if (r) { if (window.Shell) Shell.toast(`레이아웃이 '${name.trim()}'(으)로 복제되었습니다.`); await load(); }
  }

  async function renameLayout(id) {
    const l = findLayout(id); if (!l) return;
    const name = await promptName('이름 변경', '새 이름을 입력하세요.', l.name);
    if (!name || !name.trim()) return;
    const r = await api(`/api/admin/layout/${id}/name`, 'PUT', { name: name.trim() });
    if (r) { if (window.Shell) Shell.toast(`레이아웃 이름이 '${name.trim()}'(으)로 변경되었습니다.`); await load(); }
  }

  async function deleteLayout(id) {
    if (LAYOUTS.length <= 1) return;
    const l = findLayout(id); if (!l) return;
    const ok = await confirmBox('레이아웃 삭제',
      `'${l.name}' 레이아웃과 모든 설정(도면, 라인 영역, 자산 배치)을 삭제하시겠습니까?`);
    if (!ok) return;
    const r = await api(`/api/admin/layout/${id}`, 'DELETE');
    if (r) { if (window.Shell) Shell.toast('레이아웃이 삭제되었습니다.'); await load(); }
  }

  /* ════════════════ 순서 변경 모달 (LayoutReorderDialog 이식) ════════════════ */
  let _reorderItems = [];
  function openReorder() {
    if (LAYOUTS.length <= 1) return;
    _reorderItems = LAYOUTS.map(l => ({ id: l.id, name: l.name }));
    renderReorder();
    $('lm-reorder-overlay').classList.add('show');
  }
  function renderReorder() {
    $('lm-reorder-list').innerHTML = _reorderItems.map((it, i) => `
      <li class="lm-reorder-item">
        <span class="lm-reorder-idx">${i + 1}</span>
        <span class="lm-reorder-name">${esc(it.name)}</span>
        <span class="lm-reorder-move">
          <button data-move="up" data-i="${i}" ${i === 0 ? 'disabled' : ''}><span class="material-symbols-outlined">keyboard_arrow_up</span></button>
          <button data-move="down" data-i="${i}" ${i === _reorderItems.length - 1 ? 'disabled' : ''}><span class="material-symbols-outlined">keyboard_arrow_down</span></button>
        </span>
      </li>`).join('');
    $('lm-reorder-list').querySelectorAll('button[data-move]').forEach(btn => {
      btn.addEventListener('click', () => {
        const i = parseInt(btn.getAttribute('data-i'), 10);
        const dir = btn.getAttribute('data-move') === 'up' ? -1 : 1;
        const j = i + dir;
        if (j < 0 || j >= _reorderItems.length) return;
        const tmp = _reorderItems[i]; _reorderItems[i] = _reorderItems[j]; _reorderItems[j] = tmp;
        renderReorder();
      });
    });
  }
  function closeReorder() { $('lm-reorder-overlay').classList.remove('show'); }
  async function saveReorder() {
    const ids = _reorderItems.map(it => it.id);
    closeReorder();
    const r = await api('/api/admin/layout/reorder', 'PUT', { ids });
    if (r) { if (window.Shell) Shell.toast('레이아웃 순서가 변경되었습니다.'); await load(); }
  }

  /* ── 이벤트 바인딩 ── */
  function bind() {
    $('lm-create').addEventListener('click', createLayout);
    $('lm-reorder').addEventListener('click', openReorder);

    // 입력 모달
    $('lm-input-ok').addEventListener('click', () => closeInput($('lm-input-field').value));
    $('lm-input-cancel').addEventListener('click', () => closeInput(null));
    $('lm-input-field').addEventListener('keydown', (e) => {
      if (e.key === 'Enter') closeInput($('lm-input-field').value);
      else if (e.key === 'Escape') closeInput(null);
    });
    $('lm-input-overlay').addEventListener('click', (e) => { if (e.target === $('lm-input-overlay')) closeInput(null); });

    // 확인 모달
    $('lm-confirm-ok').addEventListener('click', () => closeConfirm(true));
    $('lm-confirm-cancel').addEventListener('click', () => closeConfirm(false));
    $('lm-confirm-overlay').addEventListener('click', (e) => { if (e.target === $('lm-confirm-overlay')) closeConfirm(false); });

    // 순서 변경 모달
    $('lm-reorder-ok').addEventListener('click', saveReorder);
    $('lm-reorder-cancel').addEventListener('click', closeReorder);
    $('lm-reorder-overlay').addEventListener('click', (e) => { if (e.target === $('lm-reorder-overlay')) closeReorder(); });
  }

  document.addEventListener('DOMContentLoaded', async () => {
    if (window.Shell) await Shell.init({ active: 'layout' });
    bind();
    await load();
    setInterval(load, 30000);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) load(); });
  });
})();
