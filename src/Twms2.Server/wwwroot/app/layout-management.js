/* ============================================================================
 * 레이아웃 관리(Admin/LayoutManagement) — Admin/LayoutManagement.razor 의
 * 레이아웃 목록(테이블) + 생성/복제/이름변경/순서변경/삭제를 정적 페이지로 이식.
 * GET /api/admin/layout (목록 + 도면 썸네일).
 * 쓰기: POST(생성/복제) · PUT(이름/순서) · DELETE(삭제).
 * 편집기(/admin/layout/{id}/edit)는 정적 페이지(layout-editor) — 단순 링크로 진입.
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

  /* ── 행 액션 오버플로 메뉴 (⋯ 더보기) ──
   * 같은 버튼을 다시 누르면 닫히고, 다른 버튼을 누르면 그쪽으로 이동.
   * items: [{icon,label,danger?,disabled?,onClick} | {sep:true}] */
  const RowMenu = (() => {
    let el = null;
    function ensure() {
      if (el) return el;
      el = document.createElement('div');
      el.className = 'lm-menu';
      el.hidden = true;
      document.body.appendChild(el);
      return el;
    }
    function hide() { if (el && !el.hidden) { el.hidden = true; el.innerHTML = ''; el._anchor = null; } }
    function show(anchor, items) {
      const m = ensure();
      if (!m.hidden && m._anchor === anchor) { hide(); return; }   // 같은 버튼 토글 닫기
      m.innerHTML = '';
      items.forEach(it => {
        if (it.sep) { const s = document.createElement('div'); s.className = 'lm-menu-sep'; m.appendChild(s); return; }
        const b = document.createElement('button');
        b.type = 'button';
        b.className = 'lm-menu-item' + (it.danger ? ' danger' : '');
        b.disabled = !!it.disabled;
        b.innerHTML = `<span class="material-symbols-outlined">${it.icon}</span><span>${esc(it.label)}</span>`;
        if (!it.disabled) b.addEventListener('click', () => { hide(); try { it.onClick(); } catch (err) { console.error(err); } });
        m.appendChild(b);
      });
      m._anchor = anchor;
      m.hidden = false;
      const r = anchor.getBoundingClientRect(), mw = m.offsetWidth, mh = m.offsetHeight;
      let top = r.bottom + 6;
      if (top + mh > window.innerHeight - 8) top = r.top - mh - 6;   // 아래 공간 없으면 위로
      m.style.left = Math.max(8, Math.min(r.right - mw, window.innerWidth - mw - 8)) + 'px';
      m.style.top = Math.max(8, top) + 'px';
    }
    document.addEventListener('pointerdown', e => { if (el && !el.hidden && !el.contains(e.target) && !e.target.closest('[data-menu]')) hide(); });
    document.addEventListener('keydown', e => { if (e.key === 'Escape') hide(); });
    window.addEventListener('resize', hide);
    document.addEventListener('scroll', hide, true);
    return { show, hide };
  })();

  function openRowMenu(anchor, id) {
    const l = findLayout(id); if (!l) return;
    const items = [
      { icon: 'content_copy', label: '복제', onClick: () => duplicateLayout(id) },
      { icon: 'drive_file_rename_outline', label: '이름변경', onClick: () => renameLayout(id) },
      { icon: 'download', label: '내보내기', onClick: () => exportLayout(id) },
      { icon: 'upload', label: '가져오기', onClick: () => importLayout(id) },
    ];
    if (l.imagePath) items.push({ icon: 'image', label: '이미지 다운로드', onClick: () => downloadImage(id) });
    items.push({ icon: 'file_upload', label: '이전 TWMS 가져오기', onClick: () => { location.href = `/admin/layout/${id}?tab=dexa`; } });
    items.push({ sep: true });
    items.push({ icon: 'delete', label: '삭제', danger: true, disabled: LAYOUTS.length <= 1, onClick: () => deleteLayout(id) });
    RowMenu.show(anchor, items);
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
    RowMenu.hide();
    const head = `<thead><tr>
      <th style="width:50px;">#</th>
      <th>이름</th>
      <th style="width:90px;">도면</th>
      <th style="width:150px;">수정일</th>
      <th style="width:120px;">액션</th>
    </tr></thead>`;

    if (LAYOUTS.length === 0) {
      $('lm-table').innerHTML = head +
        `<tbody><tr><td colspan="5"><div class="lm-empty">레이아웃이 없습니다. 새 레이아웃을 추가하세요.</div></td></tr></tbody>`;
      return;
    }

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
            <a class="lm-iconbtn lm-iconbtn-edit" href="/admin/layout/${l.id}/edit"><span class="material-symbols-outlined">edit</span>편집</a>
            <button class="lm-iconbtn lm-kebab" data-menu="${l.id}" title="더보기"><span class="material-symbols-outlined">more_horiz</span></button>
          </div>
        </td>
      </tr>`;
    }).join('');

    $('lm-table').innerHTML = head + `<tbody>${body}</tbody>`;

    $('lm-table').querySelectorAll('button[data-menu]').forEach(btn => {
      const id = parseInt(btn.getAttribute('data-menu'), 10);
      btn.addEventListener('click', (e) => { e.stopPropagation(); openRowMenu(btn, id); });
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

  /* ════════════════ 확인 모달 (삭제 / 가져오기 공용) ════════════════ */
  let _confirmResolve = null;
  // opts: { okLabel='삭제', okIcon='delete', danger=true }
  function confirmBox(title, message, opts) {
    const o = opts || {};
    const okLabel = o.okLabel || '삭제';
    const okIcon = o.okIcon || 'delete';
    const danger = o.danger !== false;
    $('lm-confirm-title').textContent = title;
    $('lm-confirm-msg').textContent = message || '';
    const ok = $('lm-confirm-ok');
    ok.innerHTML = `<span class="material-symbols-outlined">${okIcon}</span>${esc(okLabel)}`;
    ok.classList.toggle('lm-btn-danger', danger);
    ok.classList.toggle('lm-btn-primary', !danger);
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

  /* ════════════════ JSON Export / Import / 이미지 다운로드 ════════════════ */
  function toast(msg) { if (window.Shell) Shell.toast(msg); }

  function downloadBlob(blob, fileName) {
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url; a.download = fileName; a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  // Content-Disposition 의 filename* / filename 파싱 (없으면 fallback)
  function filenameFrom(res, fallback) {
    const cd = res.headers.get('Content-Disposition') || '';
    const star = /filename\*=(?:UTF-8'')?([^;]+)/i.exec(cd);
    if (star) { try { return decodeURIComponent(star[1].replace(/["']/g, '')); } catch (_) {} }
    const plain = /filename="?([^";]+)"?/i.exec(cd);
    if (plain) return plain[1];
    return fallback;
  }

  // 인증 필요한 GET 파일 다운로드 (export / image 공용)
  async function downloadAuthedFile(url, fallbackName, okMsg) {
    try {
      const res = await fetch(url, { headers: { 'Accept': 'application/octet-stream' } });
      if (!res.ok) {
        let msg = '다운로드에 실패했습니다.';
        try { const e = await res.json(); if (e && e.error) msg = e.error; } catch (_) {}
        toast(msg); return;
      }
      downloadBlob(await res.blob(), filenameFrom(res, fallbackName));
      if (okMsg) toast(okMsg);
    } catch (e) { toast('다운로드에 실패했습니다: ' + e.message); }
  }

  async function exportLayout(id) {
    const l = findLayout(id); if (!l) return;
    await downloadAuthedFile(`/api/admin/layout/${id}/export`, `layout-${l.name}.json`,
      `'${l.name}' 레이아웃을 내보냈습니다.`);
  }

  async function downloadImage(id) {
    const l = findLayout(id); if (!l) return;
    await downloadAuthedFile(`/api/admin/layout/${id}/image`, `${l.name}.png`, null);
  }

  let _importTargetId = null;
  function importLayout(id) {
    _importTargetId = id;
    const input = $('lm-json-import');
    input.value = '';
    input.click();
  }

  async function onImportFile(e) {
    const file = e.target.files && e.target.files[0];
    e.target.value = '';
    if (!file) return;
    const id = _importTargetId;
    const l = findLayout(id);

    // 클라이언트 선검증 + 미리보기 카운트 (Blazor 확인 다이얼로그 이식)
    let data;
    try { data = JSON.parse(await file.text()); }
    catch (_) { toast('JSON 형식이 올바르지 않습니다. 레이아웃 내보내기 파일인지 확인하세요.'); return; }
    if (!data || (data.version || 0) < 1) { toast('유효하지 않은 JSON 파일입니다.'); return; }
    const pc = (data.positions || []).length, gc = (data.groups || []).length, rc = (data.blueprintRects || []).length;
    if (pc + gc + rc === 0) { toast('가져올 데이터가 없는 JSON 파일입니다.'); return; }

    const ok = await confirmBox('레이아웃 데이터 가져오기',
      `'${l ? l.name : id}'에 다음 데이터를 덮어쓰시겠습니까?\n` +
      `원본: ${data.layoutName || '알 수 없음'}\n` +
      `자산 ${pc}개, 그룹 ${gc}개, 라인 영역 ${rc}개\n\n` +
      `기존 배치 / 그룹 / 라인 영역이 모두 교체됩니다.`,
      { okLabel: '가져오기', okIcon: 'upload', danger: false });
    if (!ok) return;

    try {
      const fd = new FormData();
      fd.append('file', file, file.name);
      const res = await fetch(`/api/admin/layout/${id}/import`, { method: 'POST', body: fd });
      const r = await res.json().catch(() => ({}));
      if (!res.ok) { toast(r.error || '가져오기에 실패했습니다.'); return; }
      let msg = `완료 — 자산 ${r.positions}개, 그룹 ${r.groups}개, 라인 영역 ${r.rects}개 가져옴`;
      if (r.skipped > 0) msg += ` (건너뜀: ${r.skipped})`;
      toast(msg);
      await load();
    } catch (e) { toast('가져오기에 실패했습니다: ' + e.message); }
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
    $('lm-json-import').addEventListener('change', onImportFile);

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

  // settings.html 동거 시 자기 패널(layoutmgmt)이 활성일 때만 폴링 — 독립 페이지에는 패널이 없어 항상 true
  function panelActive() {
    const p = document.querySelector('.set-panel[data-panel="layoutmgmt"]');
    return !p || p.classList.contains('active');
  }

  document.addEventListener('DOMContentLoaded', async () => {
    if (window.Shell) await Shell.init({ active: 'layout' });
    bind();
    await load();
    setInterval(() => { if (!document.hidden && panelActive()) load(); }, 30000);
    document.addEventListener('visibilitychange', () => { if (!document.hidden && panelActive()) load(); });
    document.addEventListener('twms:panel-shown', (e) => { if (e.detail === 'layoutmgmt') load(); });
  });
})();
