/* ============================================================================
 * 설정(Settings) — Settings.razor + 자식 탭(라인/매뉴얼/일반)을 정적 페이지로 이식.
 * GET /api/settings 1회 조회 → 3개 탭 렌더. 30초 폴링 + 탭 복귀 시 갱신.
 * 쓰기: POST/DELETE 엔드포인트가 존재하는 항목만 fetch 로 처리 후 재조회.
 *   - 로고 업로드: PNG/JPG 는 logoConverter(imagetracer) 로 SVG 변환 후, 그 외는 원본 그대로 POST /api/settings/logo.
 * ==========================================================================*/
(function () {
  'use strict';

  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const $ = (id) => document.getElementById(id);

  let _state = { general: {}, lines: [], manuals: [] };
  let _activeTab = 'lines';
  let _pendingFile = null; // 선택된 매뉴얼 PDF
  let _pendingLogo = null; // 선택된 로고 이미지

  async function load() {
    try {
      const res = await fetch('/api/settings', { headers: { 'Accept': 'application/json' } });
      if (!res.ok) return;
      _state = await res.json();
      render();
    } catch (e) { /* 무시 */ }
  }

  function render() {
    renderGeneral(_state.general || {});
    renderLines(_state.lines || []);
    renderManuals(_state.manuals || []);
  }

  // ──────────────── 일반 ────────────────
  function renderGeneral(g) {
    // 제목/부제 — 편집 중인 입력은 덮어쓰지 않음(폴링 중 커서 튐 방지)
    const tEl = $('brand-title'), sEl = $('brand-subtitle');
    if (tEl && document.activeElement !== tEl) tEl.value = (g.navTitle ?? '');
    if (sEl && document.activeElement !== sEl) sEl.value = (g.navSubtitle ?? '');

    const host = $('logo-preview-host');
    if (g.logoUrl) {
      host.innerHTML = `<div class="logo-preview-box">
        <img class="logo-preview-img" src="${esc(g.logoUrl)}" alt="현재 로고" /></div>`;
    } else {
      host.innerHTML = `<p class="text-on-surface-variant" style="font-style:italic;margin:0;">설정된 로고 없음</p>`;
    }

    // 삭제 버튼은 로고가 있을 때만 노출 (SettingsGeneral 와 동일)
    const delBtn = $('logo-delete-btn');
    if (delBtn) delBtn.style.display = g.logoUrl ? 'inline-flex' : 'none';
  }

  // ──────────────── 사이드바 로고 ────────────────
  function onLogoPicked(e) {
    const f = e.target.files && e.target.files[0] ? e.target.files[0] : null;
    _pendingLogo = f;
    $('logo-file-name').textContent = f ? f.name : '선택된 파일 없음';
    $('logo-upload-btn').disabled = !f;
  }

  function fileToBase64(file) {
    return new Promise((resolve, reject) => {
      const r = new FileReader();
      r.onload = () => { const s = String(r.result); resolve(s.slice(s.indexOf(',') + 1)); };
      r.onerror = () => reject(new Error('파일 읽기 실패'));
      r.readAsDataURL(file);
    });
  }

  async function uploadLogo() {
    if (!_pendingLogo) return;
    const f = _pendingLogo;
    const ext = (f.name.split('.').pop() || '').toLowerCase();
    if (!['png', 'jpg', 'jpeg', 'gif', 'svg', 'webp'].includes(ext)) {
      toast('PNG, JPG, SVG, GIF, WEBP 파일만 지원합니다.'); return;
    }
    if (f.size > 2 * 1024 * 1024) { toast('파일 크기가 2MB를 초과합니다.'); return; }

    const btn = $('logo-upload-btn');
    btn.disabled = true;
    $('logo-upload-label').textContent = '저장 중...';
    try {
      let blob, name;
      if (ext === 'png' || ext === 'jpg' || ext === 'jpeg') {
        // Blazor SettingsGeneral 와 동일: imagetracer 로 SVG 변환 후 app-logo.svg 로 저장
        if (!window.logoConverter) { toast('SVG 변환 모듈을 불러오지 못했습니다.'); return; }
        const base64 = await fileToBase64(f);
        const svg = await window.logoConverter.convertToSvg(base64, ext === 'png' ? 'image/png' : 'image/jpeg');
        blob = new Blob([svg], { type: 'image/svg+xml' });
        name = 'app-logo.svg';
      } else {
        blob = f;
        name = 'app-logo.' + ext;
      }
      const fd = new FormData();
      fd.append('file', blob, name);
      const res = await fetch('/api/settings/logo', { method: 'POST', body: fd });
      const data = await res.json().catch(() => ({}));
      if (res.ok) {
        toast('로고가 저장되었습니다.');
        _pendingLogo = null;
        $('logo-file-input').value = '';
        $('logo-file-name').textContent = '선택된 파일 없음';
        await load();
        if (window.Shell && Shell.refresh) Shell.refresh(); // 사이드바 로고 즉시 갱신
      } else toast(data.error || '업로드 실패');
    } catch (e) {
      toast('업로드 중 오류가 발생했습니다.');
    } finally {
      $('logo-upload-label').textContent = '저장';
      $('logo-upload-btn').disabled = !_pendingLogo;
    }
  }

  async function deleteLogo() {
    if (!window.confirm('사이드바 로고를 삭제하시겠습니까?')) return;
    try {
      const res = await fetch('/api/settings/logo', { method: 'DELETE', headers: { 'Accept': 'application/json' } });
      const data = await res.json().catch(() => ({}));
      if (res.ok) {
        toast('로고가 삭제되었습니다.');
        await load();
        if (window.Shell && Shell.refresh) Shell.refresh();
      } else toast(data.error || '삭제 실패');
    } catch (e) { toast('삭제 중 오류가 발생했습니다.'); }
  }

  // ──────────────── 제목 · 부제 ────────────────
  const BRAND_DEFAULT = { title: 'TWMS', subtitle: 'Total Web Management System' };

  async function saveBrand() {
    const title = ($('brand-title').value || '').trim();
    if (!title) { toast('제목을 입력해주세요.'); return; }
    const btn = $('brand-save-btn'); btn.disabled = true;
    try {
      const res = await postJson('/api/settings/brand', {
        navTitle: title,
        navSubtitle: ($('brand-subtitle').value || '').trim(),
      });
      toast(res.ok ? '제목·부제가 저장되었습니다.' : (res.error || '저장 실패'));
      if (res.ok) { await load(); if (window.Shell && Shell.refresh) Shell.refresh(); } // 사이드바 즉시 갱신
    } finally { btn.disabled = false; }
  }

  async function resetBrand() {
    if (!window.confirm('제목·부제를 기본값(TWMS · Total Web Management System)으로 초기화하시겠습니까?')) return;
    const btn = $('brand-reset-btn'); btn.disabled = true;
    try {
      const res = await postJson('/api/settings/brand', { navTitle: BRAND_DEFAULT.title, navSubtitle: BRAND_DEFAULT.subtitle });
      toast(res.ok ? '기본값으로 초기화되었습니다.' : (res.error || '초기화 실패'));
      if (res.ok) { await load(); if (window.Shell && Shell.refresh) Shell.refresh(); }
    } finally { btn.disabled = false; }
  }

  // ──────────────── 라인 ────────────────
  function renderLines(lines) {
    const host = $('lines-host');
    if (!lines.length) {
      host.innerHTML = `<div class="set-alert"><span class="material-symbols-outlined">info</span>
        등록된 라인이 없습니다. "라인 추가" 버튼으로 새 라인을 추가하세요.</div>`;
      return;
    }
    host.innerHTML = `<table class="nm-table"><thead><tr>
      <th style="width:80px;">ID</th><th>이름</th><th style="width:180px;">수정일</th><th style="width:110px;">작업</th></tr></thead><tbody>
      ${lines.map(l => `<tr>
        <td>${l.id}</td>
        <td>${esc(l.name)}</td>
        <td>${esc(fmtTime(l.updatedAt))}</td>
        <td><div style="display:flex;gap:6px;">
          <button class="set-icon-btn" title="편집" data-edit-line="${l.id}"><span class="material-symbols-outlined">edit</span></button>
          <button class="set-icon-btn set-icon-btn-error" title="삭제" data-del-line="${l.id}"><span class="material-symbols-outlined">delete</span></button>
        </div></td></tr>`).join('')}</tbody></table>`;

    host.querySelectorAll('[data-edit-line]').forEach(b => b.addEventListener('click', () => {
      const id = +b.getAttribute('data-edit-line');
      const line = (_state.lines || []).find(x => x.id === id);
      editLine(id, line ? line.name : '');
    }));
    host.querySelectorAll('[data-del-line]').forEach(b => b.addEventListener('click', () => deleteLine(+b.getAttribute('data-del-line'))));
  }

  async function addLine() {
    const nextId = (_state.lines || []).reduce((m, l) => Math.max(m, l.id), 0) + 1;
    const name = window.prompt(`새 라인 이름을 입력하세요. (ID: ${nextId})`, '');
    if (name == null) return;
    if (!name.trim()) { toast('라인 이름을 입력해주세요.'); return; }
    const res = await postJson('/api/settings/lines', { id: 0, name: name.trim() });
    toast(res.ok ? `라인 '${name.trim()}'이(가) 추가되었습니다.` : (res.error || '추가 실패'));
    if (res.ok) await load();
  }

  async function editLine(id, current) {
    const name = window.prompt(`라인 이름을 수정하세요. (ID: ${id})`, current || '');
    if (name == null) return;
    if (!name.trim()) { toast('라인 이름을 입력해주세요.'); return; }
    const res = await postJson('/api/settings/lines', { id, name: name.trim() });
    toast(res.ok ? `라인 이름이 '${name.trim()}'(으)로 변경되었습니다.` : (res.error || '수정 실패'));
    if (res.ok) await load();
  }

  async function deleteLine(id) {
    const line = (_state.lines || []).find(x => x.id === id);
    if (!window.confirm(`라인 '${line ? line.name : id}'을(를) 삭제하시겠습니까?`)) return;
    try {
      const res = await fetch(`/api/settings/lines/${id}`, { method: 'DELETE', headers: { 'Accept': 'application/json' } });
      const data = await res.json().catch(() => ({}));
      if (res.ok) { toast('라인이 삭제되었습니다.'); await load(); }
      else toast(data.error || '삭제할 수 없습니다.');
    } catch (e) { toast('삭제 중 오류가 발생했습니다.'); }
  }

  // ──────────────── 매뉴얼 ────────────────
  function renderManuals(manuals) {
    const host = $('manuals-host');
    if (!manuals.length) {
      host.innerHTML = `<div class="set-alert"><span class="material-symbols-outlined">info</span>등록된 매뉴얼이 없습니다.</div>`;
      return;
    }
    host.innerHTML = `<table class="nm-table"><thead><tr>
      <th>키워드</th><th>파일명</th><th style="width:180px;">업로드일</th><th style="width:120px;">작업</th></tr></thead><tbody>
      ${manuals.map(m => `<tr>
        <td><span class="chip chip-default" style="background:var(--c-primary);">${esc(m.keyword)}</span></td>
        <td>${esc(m.fileName)}</td>
        <td>${esc(fmtTime(m.uploadedAt))}</td>
        <td><div style="display:flex;gap:6px;">
          <a class="set-icon-btn" title="다운로드" href="/manuals/${esc(m.storedFileName)}" target="_blank" rel="noopener"><span class="material-symbols-outlined">download</span></a>
          <button class="set-icon-btn set-icon-btn-error" title="삭제" data-del-manual="${m.id}"><span class="material-symbols-outlined">delete</span></button>
        </div></td></tr>`).join('')}</tbody></table>`;

    host.querySelectorAll('[data-del-manual]').forEach(b => b.addEventListener('click', () => deleteManual(+b.getAttribute('data-del-manual'))));
  }

  function onFilePicked(e) {
    const f = e.target.files && e.target.files[0] ? e.target.files[0] : null;
    _pendingFile = f;
    $('manual-file-name').textContent = f ? f.name : '선택된 파일 없음';
    syncUploadBtn();
  }

  function syncUploadBtn() {
    $('manual-upload-btn').disabled = !(($('manual-keyword').value || '').trim() && _pendingFile);
  }

  async function uploadManual() {
    const keyword = ($('manual-keyword').value || '').trim();
    if (!keyword || !_pendingFile) return;
    if (_pendingFile.size > 50 * 1024 * 1024) { toast('파일 크기가 50MB를 초과합니다.'); return; }

    const btn = $('manual-upload-btn');
    btn.disabled = true;
    const fd = new FormData();
    fd.append('keyword', keyword);
    fd.append('file', _pendingFile);
    try {
      const res = await fetch('/api/settings/manuals', { method: 'POST', body: fd });
      const data = await res.json().catch(() => ({}));
      if (res.ok) {
        toast(`매뉴얼 '${_pendingFile.name}'이(가) 업로드되었습니다.`);
        $('manual-keyword').value = '';
        _pendingFile = null;
        $('manual-file-input').value = '';
        $('manual-file-name').textContent = '선택된 파일 없음';
        await load();
      } else toast(data.error || '업로드 실패');
    } catch (e) { toast('업로드 중 오류가 발생했습니다.'); }
    finally { syncUploadBtn(); }
  }

  async function deleteManual(id) {
    const m = (_state.manuals || []).find(x => x.id === id);
    if (!window.confirm(`매뉴얼 '${m ? m.fileName : id}'${m ? ' (키워드: ' + m.keyword + ')' : ''}을(를) 삭제하시겠습니까?`)) return;
    try {
      const res = await fetch(`/api/settings/manuals/${id}`, { method: 'DELETE', headers: { 'Accept': 'application/json' } });
      const data = await res.json().catch(() => ({}));
      if (res.ok) { toast('매뉴얼이 삭제되었습니다.'); await load(); }
      else toast(data.error || '삭제 실패');
    } catch (e) { toast('삭제 중 오류가 발생했습니다.'); }
  }

  // ──────────────── 탭 전환 ────────────────
  function switchTab(key) {
    _activeTab = key;
    document.querySelectorAll('.set-tab').forEach(t => t.classList.toggle('active', t.getAttribute('data-tab') === key));
    document.querySelectorAll('.set-panel').forEach(p => p.classList.toggle('active', p.getAttribute('data-panel') === key));
    // 관리 탭(iframe): 최초 진입 시에만 로드 (지연 로드 — 모든 관리 페이지를 한꺼번에 띄우지 않음)
    const panel = document.querySelector(`.set-panel[data-panel="${key}"]`);
    const f = panel && panel.querySelector('iframe[data-src]');
    if (f && !f.dataset.loaded) { f.src = f.getAttribute('data-src'); f.dataset.loaded = '1'; }
    // 동거 모듈(admin-config/layout-management/admin-database)에 패널 활성화 알림
    // — 비활성 패널은 폴링을 쉬므로, 전환 시 즉시 갱신하도록.
    document.dispatchEvent(new CustomEvent('twms:panel-shown', { detail: key }));
  }

  // ──────────────── 헬퍼 ────────────────
  async function postJson(url, body) {
    try {
      const res = await fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
        body: JSON.stringify(body),
      });
      const data = await res.json().catch(() => ({}));
      return res.ok ? Object.assign({ ok: true }, data) : { ok: false, error: data.error };
    } catch (e) { return { ok: false, error: '요청 중 오류가 발생했습니다.' }; }
  }

  function toast(msg) { if (window.Shell && Shell.toast) Shell.toast(msg); }

  function fmtTime(s) {
    if (!s) return '';
    const d = new Date(s); if (isNaN(d)) return s;
    const p = (n) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
  }

  function bind() {
    // 탭
    document.querySelectorAll('.set-tab').forEach(t => t.addEventListener('click', () => switchTab(t.getAttribute('data-tab'))));
    // 제목·부제
    $('brand-save-btn').addEventListener('click', saveBrand);
    $('brand-reset-btn').addEventListener('click', resetBrand);
    // 로고 업로드/삭제
    $('logo-pick-btn').addEventListener('click', () => $('logo-file-input').click());
    $('logo-file-input').addEventListener('change', onLogoPicked);
    $('logo-upload-btn').addEventListener('click', uploadLogo);
    $('logo-delete-btn').addEventListener('click', deleteLogo);
    // 라인
    $('line-add-btn').addEventListener('click', addLine);
    // 매뉴얼
    $('manual-pick-btn').addEventListener('click', () => $('manual-file-input').click());
    $('manual-file-input').addEventListener('change', onFilePicked);
    $('manual-keyword').addEventListener('input', syncUploadBtn);
    $('manual-upload-btn').addEventListener('click', uploadManual);
  }

  document.addEventListener('DOMContentLoaded', async () => {
    if (window.Shell) await Shell.init({ active: 'settings' });
    bind();
    // 딥링크: /settings?tab=assets 등으로 특정 탭 직접 진입 (database 는 dexa 안 접이식으로 흡수)
    let urlTab = new URLSearchParams(location.search).get('tab');
    if (urlTab === 'database') urlTab = 'dexa';
    if (urlTab && document.querySelector(`.set-panel[data-panel="${urlTab}"]`)) switchTab(urlTab);
    await load();
    setInterval(() => { if (!document.hidden) load(); }, 30000);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) load(); });
  });
})();
