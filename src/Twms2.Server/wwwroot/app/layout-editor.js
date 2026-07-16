/* ============================================================================
 * 레이아웃 편집기 — /admin/layout/{id}/edit 정적 페이지.
 * Blazor LayoutEditor / BlueprintEditor / PlacementEditor 를 정적 HTML/JS 로 이식.
 *   탭1 도면 설정  : 이미지 업로드·삭제 + 배경색 + 그리드(색/크기/사용)
 *   탭2 라인 영역  : 라인 블럭(영역) 드래그/리사이즈 배치  (blueprint-editor.js 재사용)
 *   탭3 자산 배치  : 자산·그룹 드래그/올가미/스냅/그룹화    (asset-placement-editor.js 재사용)
 * 데이터: GET /api/admin/layout/{id}/edit-data
 * 저장:   PUT config · POST/DELETE image · PUT rects · PUT placement
 *
 * 상호작용 레이어(드래그/줌)는 기존 JS 모듈을 그대로 쓴다. 그 모듈들은 Blazor 의
 * DotNetObjectReference.invokeMethodAsync(...) 를 호출하므로, 같은 모양의 shim 객체를
 * 넘겨 콜백을 순수 JS 핸들러로 라우팅한다.
 * ==========================================================================*/
(function () {
  'use strict';

  // ── 공통 유틸 ───────────────────────────────────────────────────────────
  const $ = (id) => document.getElementById(id);
  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const F = (v) => String(Math.round((Number(v) || 0) * 1000) / 1000);
  const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));
  const iconHref = (p) => '/' + String(p || 'images/icons/plc.png').replace(/^\//, '');
  const toast = (m) => { if (window.Shell && Shell.toast) Shell.toast(m); };

  // ── 공용 우클릭 컨텍스트 메뉴 ──────────────────────────────────────────────
  // items: [{icon,label,danger?,onClick} | {sep:true}]. 도면 위 우클릭 시 브라우저
  // 기본 메뉴 대신 표시. 메뉴 밖 클릭/ESC/스크롤/리사이즈 시 닫힌다.
  const CtxMenu = (() => {
    let el = null;
    function ensure() {
      if (el) return el;
      el = document.createElement('div');
      el.className = 'le-ctxmenu';
      el.hidden = true;
      document.body.appendChild(el);
      return el;
    }
    function hide() { if (el && !el.hidden) { el.hidden = true; el.innerHTML = ''; } }
    function show(x, y, items) {
      const m = ensure();
      m.innerHTML = '';
      items.forEach(it => {
        if (it.sep) { const s = document.createElement('div'); s.className = 'le-ctxmenu-sep'; m.appendChild(s); return; }
        const b = document.createElement('button');
        b.type = 'button';
        b.className = 'le-ctxmenu-item' + (it.danger ? ' danger' : '');
        b.innerHTML = `<span class="material-symbols-outlined">${it.icon || 'chevron_right'}</span><span>${esc(it.label)}</span>`;
        b.addEventListener('click', () => { hide(); try { it.onClick(); } catch (err) { console.error(err); } });
        m.appendChild(b);
      });
      m.style.left = x + 'px'; m.style.top = y + 'px';
      m.hidden = false;
      // 화면 밖으로 넘치지 않게 보정
      const mw = m.offsetWidth, mh = m.offsetHeight;
      m.style.left = Math.max(4, Math.min(x, window.innerWidth - mw - 8)) + 'px';
      m.style.top = Math.max(4, Math.min(y, window.innerHeight - mh - 8)) + 'px';
    }
    document.addEventListener('pointerdown', (e) => { if (el && !el.hidden && !el.contains(e.target)) hide(); });
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape') hide(); });
    window.addEventListener('blur', hide);
    window.addEventListener('resize', hide);
    document.addEventListener('scroll', hide, true);
    return { show, hide };
  })();

  // ── 공용 선택 다이얼로그 ──────────────────────────────────────────────────
  // open({title,icon,msg,items:[{label,sub?,icon?,value}],emptyText,onPick}) → 항목 클릭 시 onPick(value)
  const PickerDialog = (() => {
    let overlay = null;
    function ensure() {
      if (overlay) return overlay;
      overlay = document.createElement('div');
      overlay.className = 'le-overlay';
      overlay.innerHTML = `<div class="le-modal"><div class="le-modal-title" id="le-pick-title"></div>`
        + `<div class="le-modal-msg" id="le-pick-msg"></div><div class="le-pick-list" id="le-pick-list"></div>`
        + `<div class="le-modal-actions"><button type="button" class="le-btn" id="le-pick-cancel">취소</button></div></div>`;
      document.body.appendChild(overlay);
      overlay.addEventListener('click', e => { if (e.target === overlay) close(); });
      overlay.querySelector('#le-pick-cancel').addEventListener('click', close);
      return overlay;
    }
    function close() { if (overlay) overlay.classList.remove('show'); }
    function open(opts) {
      const o = ensure();
      o.querySelector('#le-pick-title').innerHTML = (opts.icon ? `<span class="material-symbols-outlined">${opts.icon}</span>` : '') + `<span>${esc(opts.title || '')}</span>`;
      o.querySelector('#le-pick-msg').textContent = opts.msg || '';
      const list = o.querySelector('#le-pick-list');
      list.innerHTML = '';
      const items = opts.items || [];
      if (!items.length) { list.innerHTML = `<div class="le-pick-empty">${esc(opts.emptyText || '항목이 없습니다.')}</div>`; }
      items.forEach(it => {
        const b = document.createElement('button');
        b.type = 'button'; b.className = 'le-pick-item';
        b.innerHTML = `<span class="material-symbols-outlined">${it.icon || 'chevron_right'}</span>`
          + `<span class="flex-grow-1" style="min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${esc(it.label)}</span>`
          + (it.sub ? `<span style="font-size:12px;color:var(--c-on-surface-variant);">${esc(it.sub)}</span>` : '');
        b.addEventListener('click', () => { close(); try { opts.onPick(it.value); } catch (err) { console.error(err); } });
        list.appendChild(b);
      });
      o.classList.add('show');
    }
    document.addEventListener('keydown', e => { if (e.key === 'Escape') close(); });
    return { open, close };
  })();

  let LID = 0;
  let config = {};        // 도면 설정 (3개 탭 공유; 탭1 저장 시 갱신)
  let allAssets = [];     // 자산 스냅샷
  let assetMap = new Map();
  let linesList = [];     // [{id,name}]
  let lineMap = {};       // id → name

  async function api(url, method, body) {
    const opts = { method, headers: { Accept: 'application/json' } };
    if (body !== undefined) { opts.headers['Content-Type'] = 'application/json'; opts.body = JSON.stringify(body); }
    let res;
    try { res = await fetch(url, opts); } catch (e) { toast('요청 실패: ' + e.message); return null; }
    if (!res.ok) {
      let m = '요청에 실패했습니다.';
      try { const e = await res.json(); if (e && e.error) m = e.error; } catch (_) {}
      toast(m); return null;
    }
    return await res.json().catch(() => ({}));
  }

  // 도면 이미지의 xMidYMid meet 렌더 영역 (LayoutHelpers.CalcImageRect 이식)
  function calcImageRect(cfg, vbW = 1000, vbH = 600) {
    if (!cfg || !(cfg.imageWidth > 0) || !(cfg.imageHeight > 0)) return { x: 0, y: 0, w: vbW, h: vbH };
    const ir = cfg.imageWidth / cfg.imageHeight, vr = vbW / vbH;
    if (ir > vr) { const h = vbW / ir; return { x: 0, y: (vbH - h) / 2, w: vbW, h }; }
    const w = vbH * ir; return { x: (vbW - w) / 2, y: 0, w, h: vbH };
  }

  /* ═══════════════════════════════════════════════════════════════════════
   * 탭 1 — 도면 설정 (배경색 / 그리드 / 이미지)
   * ═════════════════════════════════════════════════════════════════════*/
  const Cfg = (() => {
    // 라인 영역 카드 기본색 = 차콜 65% (layout-render.js LINE_BG_DEFAULT '#1f2937a6' 과 동일)
    const LINE_DEFAULT = '#1f2937', LINE_DEFAULT_ALPHA = 65;
    const aHex = (p) => Math.round(p / 100 * 255).toString(16).padStart(2, '0');
    let bgColor = '#ffffff', gridColor = '#e0e0e0', gridEnabled = true, gridSize = 20;
    let lineHex = null, lineAlpha = LINE_DEFAULT_ALPHA; // 라인 영역 색: hex(null=기본) + 불투명도(%) → 저장 시 #rrggbb[aa] 합성
    let selectedFile = null, previewObjUrl = null;

    // 저장용 라인 영역 색 문자열: 기본(null) 유지, 100% 미만이면 #rrggbbaa
    function composedLineColor() {
      if (!lineHex) return null;
      if (lineAlpha >= 100) return lineHex;
      return lineHex + aHex(lineAlpha);
    }

    function load() {
      bgColor = config.bgColor || '#ffffff';
      gridColor = config.gridColor || '#e0e0e0';
      const lm = /^#?([0-9a-f]{6})([0-9a-f]{2})?$/i.exec(config.lineColor || '');
      lineHex = lm ? '#' + lm[1] : null; // null=기본값(렌더러가 결정)
      lineAlpha = lm ? (lm[2] ? Math.round(parseInt(lm[2], 16) / 255 * 100) : 100) : LINE_DEFAULT_ALPHA;
      gridEnabled = config.gridEnabled !== false;
      gridSize = config.gridSize > 0 ? config.gridSize : 20;
      selectedFile = null;
      $('cfg-bg-color').value = bgColor;
      $('cfg-grid-color').value = gridColor;
      $('cfg-line-color').value = lineHex || LINE_DEFAULT;
      $('cfg-line-alpha').value = lineAlpha;
      $('cfg-grid-enabled').checked = gridEnabled;
      $('cfg-grid-size').value = gridSize;
      syncControls();
      renderPreview();
    }

    function syncControls() {
      $('cfg-bg-swatch').style.background = bgColor;
      $('cfg-bg-hex').textContent = bgColor;
      $('cfg-line-swatch').style.background = lineHex || LINE_DEFAULT;
      $('cfg-line-hex').textContent = lineHex ? composedLineColor() : `${LINE_DEFAULT}${aHex(LINE_DEFAULT_ALPHA)} (기본)`;
      $('cfg-line-alpha-val').textContent = lineAlpha + ' %';
      $('cfg-grid-swatch').style.background = gridColor;
      $('cfg-grid-hex').textContent = gridColor;
      $('cfg-grid-size-val').textContent = gridSize + ' px';
      $('cfg-grid-opts').style.display = gridEnabled ? '' : 'none';
      const hasImg = !!config.imagePath || !!selectedFile;
      $('cfg-img-del').hidden = !config.imagePath;
      $('cfg-img-none').hidden = hasImg;
      const chip = $('cfg-file-chip');
      if (selectedFile) { chip.hidden = false; chip.textContent = selectedFile.name; }
      else chip.hidden = true;
    }

    function previewSrc() {
      if (previewObjUrl) return previewObjUrl;
      if (config.imagePath) return iconHref(config.imagePath);
      return null;
    }

    function renderPreview() {
      const gl = gridSize * 5;
      let defs = `<pattern id="cfgp-sm" width="${gridSize}" height="${gridSize}" patternUnits="userSpaceOnUse">`
        + `<path d="M ${gridSize} 0 L 0 0 0 ${gridSize}" fill="none" stroke="${gridColor}" stroke-width="0.5" opacity="0.2"/></pattern>`
        + `<pattern id="cfgp-lg" width="${gl}" height="${gl}" patternUnits="userSpaceOnUse">`
        + `<rect width="${gl}" height="${gl}" fill="url(#cfgp-sm)"/>`
        + `<path d="M ${gl} 0 L 0 0 0 ${gl}" fill="none" stroke="${gridColor}" stroke-width="0.8" opacity="0.3"/></pattern>`;
      let body = `<rect width="1000" height="600" fill="${bgColor}"/>`;
      if (gridEnabled) body += `<rect width="1000" height="600" fill="url(#cfgp-lg)"/>`;
      // 라인 영역 색 샘플 (실제 위치와 무관한 미리보기용 카드 2개) — 불투명도 반영
      const lc = lineHex || LINE_DEFAULT, la = lineAlpha / 100;
      body += `<rect x="60" y="70" width="360" height="200" rx="10" fill="${lc}" fill-opacity="${la}" stroke="rgba(128,140,170,0.35)" stroke-width="1.5"/>`
        + `<rect x="460" y="70" width="220" height="200" rx="10" fill="${lc}" fill-opacity="${la}" stroke="rgba(128,140,170,0.35)" stroke-width="1.5"/>`;
      const src = previewSrc();
      if (src) { const ir = calcImageRect(config); body += `<image href="${src}" x="${F(ir.x)}" y="${F(ir.y)}" width="${F(ir.w)}" height="${F(ir.h)}" preserveAspectRatio="none" opacity="0.85"/>`; }
      $('cfg-preview').innerHTML = `<defs>${defs}</defs>${body}`;
    }

    function bind() {
      $('cfg-bg-color').addEventListener('input', e => { bgColor = e.target.value; syncControls(); renderPreview(); });
      $('cfg-grid-color').addEventListener('input', e => { gridColor = e.target.value; syncControls(); renderPreview(); });
      $('cfg-line-color').addEventListener('input', e => { lineHex = e.target.value; syncControls(); renderPreview(); });
      $('cfg-line-alpha').addEventListener('input', e => {
        lineAlpha = parseInt(e.target.value, 10) || 100;
        if (!lineHex) { lineHex = LINE_DEFAULT; $('cfg-line-color').value = lineHex; } // 기본색에서 투명도 조정 → 색 구체화
        syncControls(); renderPreview();
      });
      $('cfg-grid-enabled').addEventListener('change', e => { gridEnabled = e.target.checked; syncControls(); renderPreview(); });
      $('cfg-grid-size').addEventListener('input', e => { gridSize = parseInt(e.target.value, 10) || 20; syncControls(); renderPreview(); });
      document.querySelectorAll('.le-preset[data-bg]').forEach(b => b.addEventListener('click', () => { bgColor = b.dataset.bg; $('cfg-bg-color').value = bgColor; syncControls(); renderPreview(); }));
      document.querySelectorAll('.le-preset[data-grid]').forEach(b => b.addEventListener('click', () => { gridColor = b.dataset.grid; $('cfg-grid-color').value = gridColor; syncControls(); renderPreview(); }));
      document.querySelectorAll('.le-preset[data-line]').forEach(b => b.addEventListener('click', () => {
        lineHex = b.dataset.line || null;
        if (!lineHex) { lineAlpha = LINE_DEFAULT_ALPHA; $('cfg-line-alpha').value = lineAlpha; } // 기본 프리셋 = 차콜 65%
        $('cfg-line-color').value = lineHex || LINE_DEFAULT;
        syncControls(); renderPreview();
      }));

      $('cfg-pick').addEventListener('click', () => $('cfg-file').click());
      $('cfg-file').addEventListener('change', e => {
        const f = e.target.files && e.target.files[0];
        if (!f) return;
        selectedFile = f;
        if (previewObjUrl) URL.revokeObjectURL(previewObjUrl);
        previewObjUrl = URL.createObjectURL(f);
        // 임시 미리보기는 원본 비율 유지를 위해 전체 채움(저장 후 실제 크기 반영)
        syncControls(); renderPreview();
      });
      $('cfg-img-del').addEventListener('click', removeImage);
      $('cfg-save').addEventListener('click', save);
    }

    async function removeImage() {
      if (!confirm('도면 이미지를 삭제하시겠습니까?')) return;
      const r = await api(`/api/admin/layout/${LID}/image`, 'DELETE');
      if (!r) return;
      config.imagePath = null; config.imageWidth = null; config.imageHeight = null;
      if (previewObjUrl) { URL.revokeObjectURL(previewObjUrl); previewObjUrl = null; }
      selectedFile = null;
      syncControls(); renderPreview();
      toast('도면 이미지가 삭제되었습니다.');
    }

    async function save() {
      const btn = $('cfg-save'); btn.disabled = true;
      try {
        const lineColor = composedLineColor();
        const r = await api(`/api/admin/layout/${LID}/config`, 'PUT', { bgColor, gridColor, lineColor, gridEnabled, gridSize });
        if (!r) return;
        config.bgColor = bgColor; config.gridColor = gridColor; config.lineColor = lineColor; config.gridEnabled = gridEnabled; config.gridSize = gridSize;

        if (selectedFile) {
          const fd = new FormData(); fd.append('file', selectedFile);
          let res;
          try { res = await fetch(`/api/admin/layout/${LID}/image`, { method: 'POST', body: fd }); }
          catch (e) { toast('이미지 업로드 실패: ' + e.message); return; }
          if (!res.ok) { let m = '이미지 업로드 실패'; try { const e = await res.json(); if (e && e.error) m = e.error; } catch (_) {} toast(m); return; }
          const img = await res.json();
          config.imagePath = img.imagePath; config.imageWidth = img.imageWidth; config.imageHeight = img.imageHeight;
          selectedFile = null;
          if (previewObjUrl) { URL.revokeObjectURL(previewObjUrl); previewObjUrl = null; }
        }
        syncControls(); renderPreview();
        toast('도면 설정이 저장되었습니다.');
      } finally { btn.disabled = false; }
    }

    return { load, bind };
  })();

  /* ═══════════════════════════════════════════════════════════════════════
   * 탭 2 — 라인 영역 (BlueprintEditor 이식, blueprint-editor.js 재사용)
   * ═════════════════════════════════════════════════════════════════════*/
  const Bp = (() => {
    let rects = [];          // [{lineId,x,y,width,height}]
    // 개별 자산 배치와 동일: 주변 영역 스냅 기본 ON, 격자 스냅 기본 OFF
    let neighborSnap = true, gridSnap = false, snapGridSize = 20;
    let hasChanges = false, inited = false;
    let undo = [], redo = [];
    const MAX_UNDO = 30;

    const shim = { invokeMethodAsync(name, ...a) { if (name === 'OnRectMoved') onRectMoved(...a); else if (name === 'OnRectDeleted') onRectDeleted(...a); else if (name === 'OnKeyAction') onKeyAction(...a); return Promise.resolve(); } };

    function load() { rects = (window.__rectsSeed || []).map(r => ({ ...r })); undo = []; redo = []; hasChanges = false; }

    function snapshot() { return rects.map(r => ({ ...r })); }
    function pushUndo() { undo.push(snapshot()); if (undo.length > MAX_UNDO) undo.shift(); redo = []; }
    function restore(snap) { rects = snap.map(r => ({ ...r })); }

    function unplaced() { const placed = new Set(rects.map(r => r.lineId)); return linesList.filter(l => !placed.has(l.id)); }

    function renderSvg() {
      const svg = $('bp-editor-svg');
      // 편집용 격자는 스냅 격자 크기를 그대로 표시 (격자 스냅 ON 일 때만) — 자산 배치 탭과 동일
      const gs = snapGridSize, gl = gs * 5;
      const gColor = config.gridColor || '#e0e0e0', bg = config.bgColor || '#ffffff';
      let defs = `<pattern id="bp-grid-small" width="${gs}" height="${gs}" patternUnits="userSpaceOnUse">`
        + `<path d="M ${gs} 0 L 0 0 0 ${gs}" fill="none" stroke="${gColor}" stroke-width="0.5" opacity="0.2"/></pattern>`
        + `<pattern id="bp-grid-large" width="${gl}" height="${gl}" patternUnits="userSpaceOnUse">`
        + `<rect width="${gl}" height="${gl}" fill="url(#bp-grid-small)"/>`
        + `<path d="M ${gl} 0 L 0 0 0 ${gl}" fill="none" stroke="${gColor}" stroke-width="0.8" opacity="0.3"/></pattern>`;
      let body = `<rect x="-2000" y="-2000" width="5000" height="4000" fill="#111122"/>`
        + `<rect width="1000" height="600" fill="${bg}"/>`
        + (gridSnap ? `<rect width="1000" height="600" fill="url(#bp-grid-large)"/>` : '')
        + `<rect width="1000" height="600" fill="none" stroke="#ffffff" stroke-width="2" stroke-dasharray="8 4" opacity="0.4"/>`;
      if (config.imagePath) { const ir = calcImageRect(config); body += `<image href="${iconHref(config.imagePath)}" x="${F(ir.x)}" y="${F(ir.y)}" width="${F(ir.w)}" height="${F(ir.h)}" preserveAspectRatio="none" opacity="0.5"/>`; }
      for (const r of rects) {
        const label = lineMap[r.lineId] || ('Line ' + r.lineId);
        body += `<g class="bp-edit-rect" data-line-id="${r.lineId}">`
          + `<rect class="bp-rect-fill" x="${F(r.x)}" y="${F(r.y)}" width="${F(r.width)}" height="${F(r.height)}" rx="3" ry="3"/>`
          + `<rect class="bp-rect-border" x="${F(r.x)}" y="${F(r.y)}" width="${F(r.width)}" height="${F(r.height)}" rx="3" ry="3"/>`
          + `<text class="bp-rect-label" x="${F(r.x + r.width / 2)}" y="${F(r.y + r.height / 2)}" text-anchor="middle" dominant-baseline="central">${esc(label)}</text>`
          + `</g>`;
      }
      for (const r of rects) {
        body += `<g class="bp-edit-rect" data-line-id="${r.lineId}">`
          + `<rect class="bp-resize-handle" x="${F(r.x + r.width - 15)}" y="${F(r.y + r.height - 15)}" width="30" height="30" rx="3"/>`
          + `<g class="bp-delete-btn" data-delete="${r.lineId}">`
          + `<circle cx="${F(r.x + r.width)}" cy="${F(r.y)}" r="15"/>`
          + `<text class="bp-delete-text" x="${F(r.x + r.width)}" y="${F(r.y)}" text-anchor="middle" dominant-baseline="central">×</text>`
          + `</g></g>`;
      }
      svg.innerHTML = `<defs>${defs}</defs>${body}`;
      window.blueprintEditor.init('bp-editor-container', shim);
      window.blueprintEditor.setSnapConfig('bp-editor-container', neighborSnap, snapGridSize, gridSnap);
    }

    function renderPanel() {
      const placedRows = rects.map(r => {
        const name = lineMap[r.lineId] || ('Line ' + r.lineId);
        return `<div class="ap-list-item" data-locate-line="${r.lineId}" title="${esc(name)} — 도면에서 위치 보기"><span class="material-symbols-outlined" style="font-size:18px;color:var(--c-primary);">map</span>`
          + `<div class="flex-grow-1" style="min-width:0;"><div class="ap-list-name">${esc(name)}</div>`
          + `<div class="ap-list-sub">${Math.round(r.width)} × ${Math.round(r.height)}</div></div></div>`;
      }).join('') || `<div class="ap-empty">배치된 라인이 없습니다.</div>`;
      const un = unplaced();
      const unRows = un.map(l => `<div class="ap-list-item"><span class="material-symbols-outlined" style="font-size:18px;color:var(--c-on-surface-variant);">add_box</span>`
        + `<span class="ap-list-name flex-grow-1">${esc(l.name)}</span>`
        + `<button class="le-mini" data-add-line="${l.id}" title="배치"><span class="material-symbols-outlined">add</span></button></div>`).join('')
        || `<div class="ap-empty">모든 라인이 배치되었습니다.</div>`;
      $('bp-panel').innerHTML =
        `<div class="le-panel-head"><span class="material-symbols-outlined">map</span>배치된 라인 (${rects.length})</div>`
        + `<div class="ap-asset-list">${placedRows}</div>`
        + `<div class="le-panel-head"><span class="material-symbols-outlined">add_box</span>미배치 라인 (${un.length})</div>`
        + `<div class="ap-asset-list">${unRows}</div>`
        + `<button class="le-pillbtn" id="bp-add" style="margin-top:10px;width:100%;justify-content:center;" ${linesList.length === 0 ? 'disabled' : ''}><span class="material-symbols-outlined">add</span>라인 추가</button>`;
      $('bp-panel').querySelectorAll('[data-locate-line]').forEach(el => el.addEventListener('click', () => focusRect(parseInt(el.dataset.locateLine, 10))));
      $('bp-panel').querySelectorAll('[data-add-line]').forEach(b => b.addEventListener('click', () => addRect(parseInt(b.dataset.addLine, 10))));
      const addAll = $('bp-add'); if (addAll) addAll.addEventListener('click', promptAddLine);
    }

    // '라인 추가' → 다이얼로그로 라인 선택 후 배치 + 해당 영역으로 화면 이동
    function promptAddLine() {
      const un = unplaced();
      if (!un.length) { toast('모든 라인이 이미 배치되었습니다.'); return; }
      PickerDialog.open({
        title: '라인 추가', icon: 'add_road', msg: '도면에 배치할 라인을 선택하세요.',
        items: un.map(l => ({ label: l.name, value: l.id, icon: 'map' })),
        emptyText: '추가할 라인이 없습니다.',
        onPick: lineId => addRect(lineId),   // addRect 가 배치 후 focusRect 로 화면 이동
      });
    }

    // 좌측 라인 클릭 → 도면 위 해당 영역으로 뷰 이동 + 플래시 하이라이트
    function focusRect(lineId) {
      const r = rects.find(x => x.lineId === lineId);
      if (!r) return;
      if (window.blueprintZoom && inited) {
        const cx = r.x + r.width / 2, cy = r.y + r.height / 2;
        const margin = 1.8;
        const vbW = clamp(Math.max(r.width * margin, r.height * margin * (1000 / 600)), 200, 2000);
        const vbH = vbW * (600 / 1000);
        window.blueprintZoom.setViewBox('bp-editor-container', { x: cx - vbW / 2, y: cy - vbH / 2, w: vbW, h: vbH });
      }
      flashRect(lineId);
    }
    function flashRect(lineId) {
      const g = $('bp-editor-svg').querySelector(`.bp-edit-rect[data-line-id="${lineId}"]`);
      if (!g) return;
      g.classList.remove('bp-flash');
      void g.getBoundingClientRect();   // 애니메이션 재시작용 강제 리플로우
      g.classList.add('bp-flash');
      setTimeout(() => g.classList.remove('bp-flash'), 1300);
    }

    function updateToolbar() {
      $('bp-snap').classList.toggle('is-active', neighborSnap);
      $('bp-gridsnap').classList.toggle('is-active', gridSnap);
      $('bp-undo').disabled = undo.length === 0;
      $('bp-redo').disabled = redo.length === 0;
      $('bp-changed').hidden = !hasChanges;
    }

    function markChanged() { hasChanges = true; updateToolbar(); }

    function onRectMoved(lineId, x, y, w, h) {
      const r = rects.find(r => r.lineId === lineId);
      if (!r) return;
      pushUndo();
      r.x = Math.round(x * 100) / 100; r.y = Math.round(y * 100) / 100;
      r.width = Math.round(Math.max(30, w) * 100) / 100; r.height = Math.round(Math.max(20, h) * 100) / 100;
      markChanged();
    }
    function onRectDeleted(lineId) {
      pushUndo();
      rects = rects.filter(r => r.lineId !== lineId);
      markChanged(); renderSvg(); renderPanel();
    }
    function addRect(lineId) {
      if (rects.some(r => r.lineId === lineId)) { focusRect(lineId); return; }
      pushUndo();
      const idx = rects.length;
      rects.push({ lineId, x: 50 + (idx % 4) * 220, y: 50 + Math.floor(idx / 4) * 150, width: 180, height: 120 });
      markChanged(); renderSvg(); renderPanel();
      focusRect(lineId);   // 배치된 라인으로 화면 이동 + 플래시
    }
    function doUndo() { if (!undo.length) return; redo.push(snapshot()); restore(undo.pop()); markChanged(); renderSvg(); renderPanel(); }
    function doRedo() { if (!redo.length) return; undo.push(snapshot()); restore(redo.pop()); markChanged(); renderSvg(); renderPanel(); }
    function onKeyAction(action) { if (action === 'undo') doUndo(); else if (action === 'redo') doRedo(); }

    async function save() {
      // 캔버스 DOM 의 최신 좌표를 동기화 후 저장
      const live = window.blueprintEditor.getPositions('bp-editor-container') || [];
      for (const p of live) { const r = rects.find(r => r.lineId === p.lineId); if (r) { r.x = p.x; r.y = p.y; r.width = p.w; r.height = p.h; } }
      const btn = $('bp-save'); btn.disabled = true;
      try {
        const r = await api(`/api/admin/layout/${LID}/rects`, 'PUT', { rects });
        if (!r) return;
        hasChanges = false; updateToolbar();
        toast(`${rects.length}개 라인 영역이 저장되었습니다.`);
      } finally { btn.disabled = false; }
    }

    // 배치된 라인 영역 전체가 보이도록 뷰 맞춤 (자산 배치 탭의 '전체 보기'와 동일 UX)
    function fitAll() {
      if (!inited) return;
      if (!rects.length) { window.blueprintZoom.reset('bp-editor-container'); return; }
      let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
      for (const r of rects) {
        minX = Math.min(minX, r.x); minY = Math.min(minY, r.y);
        maxX = Math.max(maxX, r.x + r.width); maxY = Math.max(maxY, r.y + r.height);
      }
      const pad = 40;
      minX -= pad; minY -= pad; maxX += pad; maxY += pad;
      let vbW = Math.max(maxX - minX, (maxY - minY) * (1000 / 600));
      vbW = clamp(vbW, 200, 2000);
      const vbH = vbW * (600 / 1000);
      const cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
      window.blueprintZoom.setViewBox('bp-editor-container', { x: cx - vbW / 2, y: cy - vbH / 2, w: vbW, h: vbH });
    }

    function activate() {
      $('bp-editor-svg').setAttribute('viewBox', '0 0 1000 600');
      renderSvg(); renderPanel(); updateToolbar();
      if (!inited) {
        window.blueprintZoom.init('bp-editor-container', {
          clampMargin: 1.0,
          onZoom: z => { const el = $('bp-zoom'); if (el) el.textContent = Math.round(z * 100) + '%'; },
        });
        inited = true;
      }
    }
    function deactivate() {
      if (!inited) return;
      window.blueprintEditor.dispose('bp-editor-container');
      window.blueprintZoom.dispose('bp-editor-container');
      inited = false;
    }
    function hasUnsaved() { return hasChanges; }

    function bind() {
      $('bp-snap').addEventListener('click', () => { neighborSnap = !neighborSnap; window.blueprintEditor.setSnapConfig('bp-editor-container', neighborSnap, snapGridSize, gridSnap); updateToolbar(); });
      $('bp-gridsnap').addEventListener('click', () => { gridSnap = !gridSnap; window.blueprintEditor.setSnapConfig('bp-editor-container', neighborSnap, snapGridSize, gridSnap); renderSvg(); updateToolbar(); });
      $('bp-grid').addEventListener('change', e => { snapGridSize = clamp(parseInt(e.target.value, 10) || 20, 5, 100); e.target.value = snapGridSize; window.blueprintEditor.setSnapConfig('bp-editor-container', neighborSnap, snapGridSize, gridSnap); renderSvg(); });
      $('bp-zoom-in').addEventListener('click', () => window.blueprintZoom.zoomIn('bp-editor-container'));
      $('bp-zoom-out').addEventListener('click', () => window.blueprintZoom.zoomOut('bp-editor-container'));
      $('bp-zoom-reset').addEventListener('click', () => window.blueprintZoom.reset('bp-editor-container'));
      $('bp-zoom-fit').addEventListener('click', fitAll);
      $('bp-undo').addEventListener('click', doUndo);
      $('bp-redo').addEventListener('click', doRedo);
      $('bp-save').addEventListener('click', save);

      // 도면 위 우클릭 → 커스텀 메뉴
      $('bp-editor-container').addEventListener('contextmenu', e => {
        e.preventDefault();
        const g = e.target.closest('.bp-edit-rect');
        const items = [];
        if (g) {
          const lineId = parseInt(g.dataset.lineId, 10);
          const name = lineMap[lineId] || ('Line ' + lineId);
          items.push({ icon: 'center_focus_strong', label: '이 영역으로 확대', onClick: () => focusRect(lineId) });
          items.push({ icon: 'delete', label: `'${name}' 영역 삭제`, danger: true, onClick: () => { if (confirm(`'${name}' 라인 영역을 삭제하시겠습니까?`)) onRectDeleted(lineId); } });
          items.push({ sep: true });
        }
        items.push({ icon: 'add', label: '라인 추가', onClick: promptAddLine });
        items.push({ icon: 'center_focus_weak', label: '100% 보기', onClick: () => window.blueprintZoom.reset('bp-editor-container') });
        CtxMenu.show(e.clientX, e.clientY, items);
      });
    }

    return { load, bind, activate, deactivate, hasUnsaved };
  })();

  /* ═══════════════════════════════════════════════════════════════════════
   * 탭 3 — 자산 배치 (PlacementEditor 이식, asset-placement-editor.js 재사용)
   * ═════════════════════════════════════════════════════════════════════*/
  const Ap = (() => {
    let positions = new Map();      // assetId → {x,y,scale,visible}
    let dbPositionIds = new Set();  // 서버에 위치 행이 있던 자산(미배치 전환 영속화용)
    let groups = [];                // {id,name,x,y,width,height,color,floor}
    let groupMembers = new Map();   // groupId → [assetId]
    let groupedIds = new Set();
    let placedOnOther = new Set();
    let selAssets = new Set(), selGroups = new Set(), checked = new Set(), expanded = new Set();
    let searchText = '', selLineId = null, displayFilter = null, includeOther = false;
    // 개별 자산 배치: 주변 자산/그룹 스냅은 기본 ON, 격자 스냅은 기본 OFF
    let globalScale = 1, neighborSnap = true, gridSnap = false, snapGridSize = 20, activeTool = 'select';
    let hasChanges = false, nextTempGroupId = -1, inited = false, shellBuilt = false;
    let undo = [], redo = [];
    const MAX_UNDO = 30;

    const HANDLERS = {
      OnSelectionChanged: onSelectionChanged,
      OnToggleAssetSelection: onToggleAssetSelection,
      OnToggleGroupSelection: onToggleGroupSelection,
      OnItemsMoved: onItemsMoved,
      OnGroupMoved: onGroupMoved,
      OnBulkMoved: onBulkMoved,
      OnAssetDroppedOnGroup: onAssetDroppedOnGroup,
      OnAssetRemovedFromGroup: onAssetRemovedFromGroup,
      OnKeyAction: onKeyAction,
      OnGridPlaceConfirmed: () => {},
    };
    const shim = { invokeMethodAsync(name, ...a) { const fn = HANDLERS[name]; if (fn) fn(...a); return Promise.resolve(); } };

    // ── 상태 적재 ──
    function load(d) {
      positions = new Map();
      dbPositionIds = new Set();
      (d.positions || []).forEach(p => { positions.set(p.assetId, { x: p.x, y: p.y, scale: p.scale > 0 ? p.scale : 1, visible: !!p.visible }); dbPositionIds.add(p.assetId); });
      groups = (d.groups || []).map(g => ({ ...g }));
      groupMembers = new Map();
      (d.groupMembers || []).forEach(m => { if (!groupMembers.has(m.groupId)) groupMembers.set(m.groupId, []); groupMembers.get(m.groupId).push(m.assetId); });
      placedOnOther = new Set(d.placedOnOtherLayouts || []);
      rebuildIndexes();
      undo = []; redo = []; hasChanges = false; nextTempGroupId = -1;
      selAssets.clear(); selGroups.clear(); checked.clear();
      const scales = [...positions.values()].filter(p => p.scale > 0).map(p => p.scale);
      globalScale = scales.length ? Math.round((scales.reduce((a, b) => a + b, 0) / scales.length) * 10) / 10 : 1;
    }

    function rebuildIndexes() { groupedIds = new Set([...groupMembers.values()].flat()); }
    function getPosition(id) { let p = positions.get(id); if (!p) { p = { x: 100, y: 100, scale: 1, visible: false }; positions.set(id, p); } return p; }
    function posOf(id) { return positions.get(id) || { x: 100, y: 100, scale: 1, visible: false }; }
    function memberIdsOf(gid) { return groupMembers.get(gid) || []; }

    // 그룹 내 동적 그리드 배치 (PlacementEditor.LayoutGroupAssets 이식)
    function layoutGroupAssets(grp, members) {
      const out = [];
      if (!members.length) return out;
      const pad = 2.0, availW = grp.width - pad * 2, availH = grp.height - pad * 2;
      if (availW < 4 || availH < 4) return out;
      const count = members.length, aspect = availW / availH;
      const bestCols = Math.max(1, Math.round(Math.sqrt(count * aspect)));
      const cols = Math.min(bestCols, count), rows = Math.ceil(count / cols);
      const rawSz = Math.min(availW / cols, availH / rows);
      const gap = Math.max(1.0, Math.min(rawSz * 0.1, 2.0));
      const cellW = (availW - gap * (cols - 1)) / cols, cellH = (availH - gap * (rows - 1)) / rows;
      // 개별 자산 크기(32*scale)를 넘지 않도록 제한 → 그룹 안에서도 지정한 크기로 표시(박스를 채우려 키우지 않음)
      const avgScale = members.reduce((s, m) => s + (posOf(m.assetId).scale > 0 ? posOf(m.assetId).scale : 1), 0) / count;
      const sz = Math.max(4, Math.min(cellW, cellH, 32 * avgScale));
      const totalW = cols * sz + (cols - 1) * gap, totalH = rows * sz + (rows - 1) * gap;
      const startX = grp.x + pad + (availW - totalW) / 2, startY = grp.y + pad + (availH - totalH) / 2;
      for (let i = 0; i < count; i++) {
        const c = i % cols, r = Math.floor(i / cols);
        out.push({ x: Math.round((startX + c * (sz + gap)) * 100) / 100, y: Math.round((startY + r * (sz + gap)) * 100) / 100, size: Math.round(sz * 10) / 10, asset: members[i] });
      }
      return out;
    }

    // ── 계산 목록 ──
    function unplacedAssets() {
      let r = allAssets.filter(a => !posOf(a.assetId).visible && !groupedIds.has(a.assetId));
      if (!includeOther) r = r.filter(a => !placedOnOther.has(a.assetId));
      if (selLineId !== null) r = selLineId === -1 ? r.filter(a => a.lineId == null) : r.filter(a => a.lineId === selLineId);
      if (searchText.trim()) { const q = searchText.toLowerCase(); r = r.filter(a => (a.name || '').toLowerCase().includes(q) || (a.ip || '').toLowerCase().includes(q)); }
      return r;
    }
    function placedUngrouped() { return allAssets.filter(a => posOf(a.assetId).visible && !groupedIds.has(a.assetId)); }
    function groupLabel(gid) {
      const ids = memberIdsOf(gid);
      if (!ids.length) return '그룹 #' + gid;
      const names = ids.map(id => assetMap.get(id)).filter(Boolean).slice(0, 5).map(a => a.name);
      let l = names.join(', ');
      if (ids.length > 5) l += ` 외 ${ids.length - 5}`;
      return l;
    }

    // ── SVG 렌더 ──
    function assetNode(asset, cx, cy, sz, scale) {
      const sel = selAssets.has(asset.assetId) ? ' ap-selected' : '';
      const imgSz = sz * 0.75, imgOff = sz * 0.125;
      return `<g class="ap-asset-icon${sel}" data-asset-id="${asset.assetId}" data-x="${F(cx)}" data-y="${F(cy)}" data-scale="${F(scale)}" transform="translate(${F(cx - sz / 2)},${F(cy - sz / 2)})" style="cursor:grab;">`
        + `<title>${esc(asset.name)}</title>`
        + `<rect width="${F(sz)}" height="${F(sz)}" rx="4" fill="white" stroke="#ccc" stroke-width="1" opacity="0.9"/>`
        + `<image href="${iconHref(asset.icon)}" width="${F(imgSz)}" height="${F(imgSz)}" x="${F(imgOff)}" y="${F(imgOff)}"/></g>`;
    }
    function memberNode(asset, it) {
      const sel = selAssets.has(asset.assetId) ? ' ap-selected' : '';
      const imgSz = it.size * 0.75, imgOff = it.size * 0.125;   // 낱개 자산(assetNode)과 동일한 비율로 그려 크기 일치
      return `<g class="ap-asset-icon${sel}" data-asset-id="${asset.assetId}" data-x="${F(it.x + it.size / 2)}" data-y="${F(it.y + it.size / 2)}" data-scale="${F(it.size / 32)}" transform="translate(${F(it.x)},${F(it.y)})" style="cursor:grab;">`
        + `<title>${esc(asset.name)}</title>`
        + `<rect width="${F(it.size)}" height="${F(it.size)}" rx="4" fill="white" stroke="#ccc" stroke-width="1" opacity="0.9"/>`
        + `<image href="${iconHref(asset.icon)}" width="${F(imgSz)}" height="${F(imgSz)}" x="${F(imgOff)}" y="${F(imgOff)}"/></g>`;
    }
    function groupNode(grp) {
      const ids = memberIdsOf(grp.id);
      const members = ids.map(id => assetMap.get(id)).filter(Boolean);
      const color = grp.color || '#8a93a6'; // 뷰어(layout-render.js GROUP_COLOR_DEFAULT)와 동일한 중립 슬레이트 기본색
      const selCls = selGroups.has(grp.id) ? ' ap-group-selected' : '';
      const rSz = clamp(Math.min(grp.width, grp.height) * 0.15, 6, 16), rOff = rSz / 2;
      let inner = '';
      for (const it of layoutGroupAssets(grp, members)) inner += memberNode(it.asset, it);
      return `<g class="ap-group-container${selCls}" data-group-id="${grp.id}" data-x="${F(grp.x)}" data-y="${F(grp.y)}" data-w="${F(grp.width)}" data-h="${F(grp.height)}" data-members="${ids.join(',')}">`
        + `<rect x="${F(grp.x)}" y="${F(grp.y)}" width="${F(grp.width)}" height="${F(grp.height)}" rx="6" fill="${color}10" stroke="${color}" stroke-width="1.5" stroke-dasharray="6 3"/>`
        + `<g clip-path="url(#ap-grp-clip-${grp.id})">${inner}</g>`
        + `<rect class="ap-group-resize" x="${F(grp.x + grp.width - rOff)}" y="${F(grp.y + grp.height - rOff)}" width="${F(rSz)}" height="${F(rSz)}" rx="2" fill="${color}" fill-opacity="0.4"/></g>`;
    }
    function renderSvg() {
      const svg = $('ap-editor-svg');
      const gs = snapGridSize, gl = gs * 5, bg = config.bgColor || '#ffffff';
      let defs = `<pattern id="ap-grid-sm" width="${gs}" height="${gs}" patternUnits="userSpaceOnUse"><path d="M ${gs} 0 L 0 0 0 ${gs}" fill="none" stroke="#e0e0e0" stroke-width="0.3" opacity="0.3"/></pattern>`
        + `<pattern id="ap-grid-lg" width="${gl}" height="${gl}" patternUnits="userSpaceOnUse"><rect width="${gl}" height="${gl}" fill="url(#ap-grid-sm)"/><path d="M ${gl} 0 L 0 0 0 ${gl}" fill="none" stroke="#ccc" stroke-width="0.5" opacity="0.3"/></pattern>`;
      groups.forEach(g => defs += `<clipPath id="ap-grp-clip-${g.id}"><rect x="${F(g.x)}" y="${F(g.y)}" width="${F(g.width)}" height="${F(g.height)}" rx="6"/></clipPath>`);
      let body = `<rect width="1000" height="600" fill="${bg}"/>`;
      if (gridSnap) body += `<rect width="1000" height="600" fill="url(#ap-grid-lg)"/>`;
      if (config.imagePath) { const ir = calcImageRect(config); body += `<image href="${iconHref(config.imagePath)}" x="${F(ir.x)}" y="${F(ir.y)}" width="${F(ir.w)}" height="${F(ir.h)}" preserveAspectRatio="none" opacity="0.5"/>`; }
      groups.forEach(g => body += groupNode(g));
      for (const a of allAssets) {
        if (groupedIds.has(a.assetId)) continue;
        const p = posOf(a.assetId);
        if (!p.visible) continue;
        if (displayFilter && a.typeName !== displayFilter) continue;
        body += assetNode(a, p.x, p.y, 32 * p.scale, p.scale);
      }
      svg.innerHTML = `<defs>${defs}</defs>${body}`;
    }

    // ── 좌측 패널 ──
    function buildShell() {
      const lineOpts = ['<option value="">전체</option>']
        .concat(linesList.map(l => `<option value="${l.id}">${esc(l.name)}</option>`))
        .concat('<option value="-1">미지정</option>').join('');
      const types = [...new Set(allAssets.map(a => a.typeName).filter(Boolean))].sort();
      const typeOpts = ['<option value="">전체 표시</option>'].concat(types.map(t => `<option value="${esc(t)}">${esc(t)} 만</option>`)).join('');
      $('ap-panel').innerHTML =
        `<input type="text" id="ap-search" class="le-panel-input" placeholder="자산 검색..." />`
        + `<select id="ap-line" class="le-panel-input">${lineOpts}</select>`
        + `<select id="ap-display" class="le-panel-input">${typeOpts}</select>`
        + `<div class="ap-panel-section"><div class="le-panel-head" style="justify-content:space-between;">`
        + `<span id="ap-unplaced-title">미배치 (0)</span>`
        + `<span style="display:flex;gap:8px;align-items:center;font-weight:500;font-size:11px;">`
        + `<label style="display:flex;align-items:center;gap:3px;cursor:pointer;"><input type="checkbox" id="ap-include-other" style="accent-color:var(--c-primary);"/>다른도면포함</label>`
        + `<label style="display:flex;align-items:center;gap:3px;cursor:pointer;"><input type="checkbox" id="ap-check-all" style="accent-color:#4caf50;"/>전체</label>`
        + `</span></div><div class="ap-asset-list" id="ap-unplaced-list"></div></div>`
        + `<div class="ap-panel-section" id="ap-groups-section" hidden><div class="le-panel-head" id="ap-groups-title">그룹 (0)</div><div class="ap-group-list" id="ap-groups-list"></div></div>`
        + `<div class="ap-panel-section"><div class="le-panel-head" id="ap-placed-title">배치됨 (0)</div><div class="ap-asset-list" id="ap-placed-list"></div></div>`;

      $('ap-search').addEventListener('input', e => { searchText = e.target.value; renderUnplaced(); });
      $('ap-line').addEventListener('change', e => { selLineId = e.target.value === '' ? null : parseInt(e.target.value, 10); renderUnplaced(); });
      $('ap-display').addEventListener('change', e => { displayFilter = e.target.value || null; renderSvg(); });
      $('ap-include-other').addEventListener('change', e => { includeOther = e.target.checked; checked.clear(); renderUnplaced(); });
      $('ap-check-all').addEventListener('change', e => toggleCheckAll(e.target.checked));

      // 리스트 클릭 위임 — 미배치는 체크박스를 눌러야 배치(단순 행 클릭으로는 배치하지 않음)
      $('ap-unplaced-list').addEventListener('click', e => {
        if (e.target.tagName !== 'INPUT') return;
        const item = e.target.closest('[data-uid]'); if (!item) return;
        toggleCheck(parseInt(item.dataset.uid, 10));
      });
      $('ap-placed-list').addEventListener('click', e => {
        const hide = e.target.closest('[data-hide]');
        if (hide) { unplaceAsset(parseInt(hide.dataset.hide, 10)); return; }
        const item = e.target.closest('[data-pid]'); if (item) focusAsset(parseInt(item.dataset.pid, 10));
      });
      $('ap-groups-list').addEventListener('click', e => {
        const ung = e.target.closest('[data-grp-ungroup]'); if (ung) { ungroupToCanvas(parseInt(ung.dataset.grpUngroup, 10)); return; }
        const rem = e.target.closest('[data-grp-rem]'); if (rem) { removeFromGroup(parseInt(rem.dataset.grp, 10), parseInt(rem.dataset.grpRem, 10)); return; }
        const mem = e.target.closest('[data-locate-asset]'); if (mem) { focusAsset(parseInt(mem.dataset.locateAsset, 10)); return; }
        const exp = e.target.closest('[data-grp-exp]'); if (exp) { toggleGroupExpand(parseInt(exp.dataset.grpExp, 10)); return; }  // 화살표만 펼침/접기
        const head = e.target.closest('[data-grp-locate]'); if (head) locateGroup(parseInt(head.dataset.grpLocate, 10)); // 행 클릭은 도면 위치만
      });
      $('ap-groups-list').addEventListener('change', e => {
        const f = e.target.closest('[data-grp-floor]');
        if (f) onGroupFloorChanged(parseInt(f.dataset.grpFloor, 10), parseInt(f.value, 10));
      });
      shellBuilt = true;
    }

    function renderUnplaced() {
      const list = unplacedAssets();
      $('ap-unplaced-title').textContent = `미배치 (${list.length})`;
      const all = $('ap-check-all'); if (all) all.checked = checked.size > 0 && checked.size >= list.length;
      $('ap-unplaced-list').innerHTML = list.map(a => {
        const ck = checked.has(a.assetId);
        const tag = a.lineName ? `<span class="ap-line-tag">${esc(a.lineName)}</span>` : '';
        return `<div class="ap-list-item ${ck ? 'checked' : ''}" data-uid="${a.assetId}" style="cursor:default;">`
          + `<input type="checkbox" ${ck ? 'checked' : ''} style="accent-color:#4caf50;cursor:pointer;"/>`
          + `<img src="${iconHref(a.icon)}" class="asset-type-icon-sm"/>`
          + `<div class="flex-grow-1" style="min-width:0;"><div class="ap-list-name">${esc(a.name)}</div>`
          + `<div class="ap-list-sub">${esc(a.ip || '')}${tag}</div></div></div>`;
      }).join('') || `<div class="ap-empty">미배치 자산이 없습니다.</div>`;
    }
    function renderGroups() {
      const sec = $('ap-groups-section');
      sec.hidden = groups.length === 0;
      $('ap-groups-title').textContent = `그룹 (${groups.length})`;
      $('ap-groups-list').innerHTML = groups.map(g => {
        const ids = memberIdsOf(g.id), isExp = expanded.has(g.id), selCls = selGroups.has(g.id) ? 'selected' : '';
        let members = '';
        if (isExp) members = `<div class="ap-group-members">` + ids.map(aid => {
          const a = assetMap.get(aid); if (!a) return '';
          return `<div class="ap-list-item small" data-locate-asset="${aid}" title="${esc(a.name)} — 도면에서 위치 보기"><img src="${iconHref(a.icon)}" class="asset-type-icon-sm"/>`
            + `<span class="flex-grow-1 ap-list-name">${esc(a.name)}</span>`
            + `<button class="le-mini danger" data-grp="${g.id}" data-grp-rem="${aid}" title="그룹에서 제거"><span class="material-symbols-outlined">remove_circle_outline</span></button></div>`;
        }).join('') + `</div>`;
        return `<div class="ap-group-item ${selCls}">`
          + `<div class="ap-group-header" data-grp-locate="${g.id}" title="도면에서 위치 보기">`
          + `<span class="material-symbols-outlined" data-grp-exp="${g.id}" style="font-size:18px;cursor:pointer;border-radius:6px;" title="${isExp ? '접기' : '펼치기'}">${isExp ? 'expand_more' : 'chevron_right'}</span>`
          + `<span class="ap-group-color" style="background:${g.color || '#8a93a6'}"></span>`
          + `<span class="flex-grow-1 ap-list-name" style="font-size:.72rem;">${esc(groupLabel(g.id))}</span>`
          + `<span class="le-floor-wrap" title="그룹이 표시될 층" onclick="event.stopPropagation()"><input type="number" class="le-floor-input" data-grp-floor="${g.id}" value="${g.floor}" min="-20" max="100"/><span class="le-floor-suffix">층</span></span>`
          + `<span class="ap-group-count">${ids.length}</span>`
          + `<button class="le-mini" data-grp-ungroup="${g.id}" title="그룹 해제 (도면에 개별 배치)"><span class="material-symbols-outlined">grid_view</span></button>`
          + `</div>${members}</div>`;
      }).join('');
    }
    function renderPlaced() {
      const list = placedUngrouped();
      $('ap-placed-title').textContent = `배치됨 (${list.length})`;
      $('ap-placed-list').innerHTML = list.map(a => {
        const sel = selAssets.has(a.assetId) ? 'selected' : '';
        return `<div class="ap-list-item ${sel}" data-pid="${a.assetId}"><img src="${iconHref(a.icon)}" class="asset-type-icon-sm"/>`
          + `<div class="flex-grow-1" style="min-width:0;"><div class="ap-list-name">${esc(a.name)}</div><div class="ap-list-sub">${esc(a.ip || '')}</div></div>`
          + `<button class="le-mini" data-hide="${a.assetId}" title="미배치"><span class="material-symbols-outlined">visibility_off</span></button></div>`;
      }).join('') || `<div class="ap-empty">배치된 자산이 없습니다.</div>`;
    }
    function renderPanel() { if (!shellBuilt) return; renderUnplaced(); renderGroups(); renderPlaced(); }

    // ── 선택 액션 툴바 ──
    function selectedSharedFloor() {
      const fl = [...new Set(groups.filter(g => selGroups.has(g.id)).map(g => g.floor))];
      return fl.length === 1 ? fl[0] : '';
    }
    function updateSelActions() {
      const host = $('ap-sel-actions');
      const total = selAssets.size + selGroups.size;
      if (total === 0) { host.innerHTML = ''; return; }
      const names = [...selAssets].map(id => (assetMap.get(id) || {}).name || ('#' + id))
        .concat([...selGroups].map(id => { const g = groups.find(g => g.id === id); return g ? (g.name || '그룹 #' + id) : ''; }));
      let html = `<span class="le-sel-label" title="${esc(names.join(', '))}">${esc(names.join(', '))}</span>`;
      if (selAssets.size > 1) {
        html += `<button class="le-pillbtn" id="ap-group-btn"><span class="material-symbols-outlined">workspaces</span>그룹화</button>`
          + `<select class="le-panel-input" id="ap-align" style="width:auto;margin:0;padding:6px 8px;"><option value="">정렬/분배…</option>`
          + `<option value="grid">그리드 정렬</option><option value="row">가로 정렬</option><option value="col">세로 정렬</option>`
          + `<option value="h">가로 균등 분배</option><option value="v">세로 균등 분배</option></select>`;
      }
      if (selAssets.size >= 1) {
        const sizeScales = [...selAssets].map(id => { const s = posOf(id).scale; return s > 0 ? s : 1; });
        const initSize = Math.round((sizeScales.reduce((a, b) => a + b, 0) / sizeScales.length) * 100) / 100;
        html += `<span class="ap-sel-size-wrap" title="선택 자산 크기 (${selAssets.size}개)">`
          + `<span class="material-symbols-outlined" style="font-size:18px;color:var(--c-on-surface-variant);">photo_size_select_large</span>`
          + `<input type="range" id="ap-sel-size" min="0.1" max="3" step="0.05" value="${clamp(initSize, 0.1, 3)}" style="width:104px;accent-color:var(--c-primary);"/>`
          + `<span id="ap-sel-size-val" style="min-width:36px;font-size:12px;font-family:var(--font-mono);font-variant-numeric:tabular-nums;color:var(--c-on-surface-variant);">${initSize.toFixed(2)}×</span></span>`;
      }
      if (selGroups.size > 0) {
        html += `<span class="le-floor-wrap" title="선택 그룹이 표시될 층"><input type="number" class="le-floor-input" id="ap-sel-floor" style="width:36px;" min="-20" max="100" placeholder="혼합" value="${selectedSharedFloor()}"/><span class="le-floor-suffix">층</span></span>`;
      }
      html += `<button class="le-pillbtn danger" id="ap-del-sel"><span class="material-symbols-outlined">delete</span>삭제</button>`;
      host.innerHTML = html;
      const gb = $('ap-group-btn'); if (gb) gb.addEventListener('click', createGroupFromSelection);
      const al = $('ap-align'); if (al) al.addEventListener('change', e => { const v = e.target.value; e.target.value = ''; if (v === 'h' || v === 'v') distribute(v); else if (v) align(v); });
      const sf = $('ap-sel-floor'); if (sf) sf.addEventListener('change', e => onSelectedGroupsFloor(parseInt(e.target.value, 10)));
      const ss = $('ap-sel-size');
      if (ss) {
        let undoPushed = false;
        ss.addEventListener('pointerdown', () => { undoPushed = false; });
        ss.addEventListener('input', e => { if (!undoPushed) { pushUndo(); undoPushed = true; } setSelectedScale(parseFloat(e.target.value)); });
      }
      const ds = $('ap-del-sel'); if (ds) ds.addEventListener('click', () => { pushUndo(); unplaceSelected(); refresh(); });
    }

    function updateToolbar() {
      $('ap-snap').classList.toggle('is-active', neighborSnap);
      $('ap-gridsnap').classList.toggle('is-active', gridSnap);
      $('ap-tool-pan').classList.toggle('is-active', activeTool === 'pan');
      $('ap-undo').disabled = undo.length === 0;
      $('ap-redo').disabled = redo.length === 0;
      $('ap-changed').hidden = !hasChanges;
      $('ap-save').disabled = !hasChanges;
      if (inited) $('ap-zoom').textContent = window.assetPlacementEditor.getZoomLevel('ap-editor-container') + '%';
    }
    function markChanged() { hasChanges = true; }
    function refresh() { renderSvg(); renderPanel(); updateSelActions(); updateToolbar(); }

    // ── Undo / Redo ──
    function snap() {
      return {
        positions: new Map([...positions].map(([k, v]) => [k, { ...v }])),
        groups: groups.map(g => ({ ...g })),
        members: new Map([...groupMembers].map(([k, v]) => [k, v.slice()])),
      };
    }
    function pushUndo() { undo.push(snap()); if (undo.length > MAX_UNDO) undo.shift(); redo = []; }
    function restore(s) {
      positions = new Map([...s.positions].map(([k, v]) => [k, { ...v }]));
      groups = s.groups.map(g => ({ ...g }));
      groupMembers = new Map([...s.members].map(([k, v]) => [k, v.slice()]));
      rebuildIndexes(); selAssets.clear(); selGroups.clear(); markChanged();
    }
    function doUndo() { if (!undo.length) return; redo.push(snap()); restore(undo.pop()); refresh(); }
    function doRedo() { if (!redo.length) return; undo.push(snap()); restore(redo.pop()); refresh(); }

    // ── JS 콜백 핸들러 ──
    function onSelectionChanged(assetIds, groupIds) {
      selAssets = new Set(assetIds || []); selGroups = new Set(groupIds || []);
      const last = (assetIds && assetIds.length) ? assetIds[assetIds.length - 1] : null;
      if (last != null) { for (const [gid, mems] of groupMembers) if (mems.includes(last)) { expanded.add(gid); break; } }
      refresh();
    }
    function onToggleAssetSelection(id) { selGroups.clear(); if (!selAssets.delete(id)) selAssets.add(id); refresh(); }
    function onToggleGroupSelection(id) { selAssets.clear(); if (!selGroups.delete(id)) selGroups.add(id); refresh(); }
    function onItemsMoved(list) { pushUndo(); (list || []).forEach(p => { const pos = getPosition(p.assetId); pos.x = p.x; pos.y = p.y; }); markChanged(); refresh(); }
    function onGroupMoved(gid, x, y, w, h) { pushUndo(); const g = groups.find(g => g.id === gid); if (g) { g.x = x; g.y = y; g.width = w; g.height = h; } markChanged(); refresh(); }
    function onBulkMoved(list, grps) {
      pushUndo();
      (grps || []).forEach(gp => { const g = groups.find(g => g.id === gp.groupId); if (g) { g.x = gp.x; g.y = gp.y; g.width = gp.w; g.height = gp.h; } });
      (list || []).forEach(p => { const pos = getPosition(p.assetId); pos.x = p.x; pos.y = p.y; });
      markChanged(); refresh();
    }
    function onAssetDroppedOnGroup(assetId, groupId) {
      const cur = groupMembers.get(groupId);
      if (cur && cur.includes(assetId)) return;
      for (const [gid, mems] of groupMembers) { const i = mems.indexOf(assetId); if (i >= 0) { mems.splice(i, 1); if (!mems.length) { groups = groups.filter(g => g.id !== gid); groupMembers.delete(gid); } break; } }
      let mems = groupMembers.get(groupId); if (!mems) { mems = []; groupMembers.set(groupId, mems); }
      mems.push(assetId); rebuildIndexes(); markChanged(); refresh();
    }
    function onAssetRemovedFromGroup(assetId, groupId) {
      const mems = groupMembers.get(groupId); if (!mems) return;
      const i = mems.indexOf(assetId); if (i < 0) return; mems.splice(i, 1);
      if (!mems.length) { groups = groups.filter(g => g.id !== groupId); groupMembers.delete(groupId); }
      rebuildIndexes(); markChanged(); refresh();
    }
    function onKeyAction(action) {
      switch (action) {
        case 'delete': pushUndo(); unplaceSelected(); break;
        case 'undo': doUndo(); return;
        case 'redo': doRedo(); return;
        case 'escape': selAssets.clear(); selGroups.clear(); break;
        case 'selectAll': selAssets = new Set(allAssets.filter(a => posOf(a.assetId).visible && !groupedIds.has(a.assetId)).map(a => a.assetId)); break;
        case 'group': if (selAssets.size > 1) { createGroupFromSelection(); return; } break;
      }
      refresh();
    }

    // ── 배치/미배치 ──
    function toggleCheck(assetId) {
      if (!checked.delete(assetId)) { checked.add(assetId); placeIndividually(assetId); refresh(); }
      else renderUnplaced();
    }
    function placeIndividually(assetId) {
      const pos = getPosition(assetId); if (pos.visible) return;
      pushUndo();
      const idx = checked.size - 1, cols = 5, spacing = 32 * globalScale + 12;
      pos.visible = true; pos.scale = globalScale;
      pos.x = Math.round((40 + (idx % cols) * spacing) * 100) / 100;
      pos.y = Math.round((40 + Math.floor(idx / cols) * spacing) * 100) / 100;
      markChanged();
    }
    function unplaceAsset(assetId) {
      pushUndo();
      const pos = getPosition(assetId); pos.visible = false;
      for (const [gid, mems] of groupMembers) { const i = mems.indexOf(assetId); if (i >= 0) { mems.splice(i, 1); if (!mems.length) { groups = groups.filter(g => g.id !== gid); groupMembers.delete(gid); } break; } }
      checked.delete(assetId);
      rebuildIndexes(); markChanged(); refresh();
    }
    function toggleCheckAll(check) {
      if (check) {
        pushUndo();
        const list = unplacedAssets(), spacing = 32 * globalScale + 12, cols = 5;
        list.forEach((a, i) => {
          checked.add(a.assetId);
          const pos = getPosition(a.assetId);
          if (!pos.visible) { pos.visible = true; pos.scale = globalScale; pos.x = Math.round((40 + (i % cols) * spacing) * 100) / 100; pos.y = Math.round((40 + Math.floor(i / cols) * spacing) * 100) / 100; }
        });
        markChanged();
      } else checked.clear();
      refresh();
    }
    function focusAsset(assetId) {
      selAssets = new Set([assetId]); selGroups.clear();
      refresh();   // 먼저 SVG 재생성(선택 표시) → 새 노드에 스크롤/플래시 적용
      if (inited) { try { window.assetPlacementEditor.scrollToAsset('ap-editor-container', assetId); } catch (_) {} }
    }
    // 좌측 그룹 클릭 → 도면 위 해당 그룹으로 뷰 이동 + 플래시 (그룹 펼침은 SVG를 재생성하지 않으므로 노드 유지됨)
    function locateGroup(gid) {
      if (inited) { try { window.assetPlacementEditor.scrollToGroup('ap-editor-container', gid); } catch (_) {} }
    }

    // ── 그룹 ──
    function toggleGroupExpand(gid) { if (!expanded.delete(gid)) expanded.add(gid); renderGroups(); }
    function createGroupFromSelection() {
      if (selAssets.size < 2) return;
      pushUndo();
      const ids = [...selAssets];
      const count = ids.length, tempId = nextTempGroupId--;

      // 선택 자산이 기존 그룹에 속해 있으면 거기서 빼고(중복 멤버 방지), 비워진 그룹은 자동 삭제
      const idSet = new Set(ids);
      for (const [gid, mems] of [...groupMembers]) {
        const kept = mems.filter(m => !idSet.has(m));
        if (kept.length === mems.length) continue;
        if (kept.length === 0) { groups = groups.filter(g => g.id !== gid); groupMembers.delete(gid); expanded.delete(gid); }
        else groupMembers.set(gid, kept);
      }

      // 그룹 크기 = 멤버 개수 × 개별 크기(scale)에 맞춰 산정 → 그룹 안에서도 지정한 크기로 보이도록.
      // (layoutGroupAssets 의 pad=2 / gap=2 와 동일하게 맞추면 셀 크기가 정확히 32*scale 이 된다)
      const scales = ids.map(id => { const s = getPosition(id).scale; return s > 0 ? s : globalScale; });
      const tScale = scales.reduce((a, b) => a + b, 0) / scales.length;
      const cell = 32 * (tScale > 0 ? tScale : 1), gap = 2, edge = 2;
      const cols = Math.max(1, Math.ceil(Math.sqrt(count))), rows = Math.ceil(count / cols);
      const bw = Math.round((cell * cols + gap * (cols - 1) + edge * 2) * 100) / 100;
      const bh = Math.round((cell * rows + gap * (rows - 1) + edge * 2) * 100) / 100;

      // 현재 보이는 화면(viewBox) 중앙에 배치 → 사용자 화면에 보이도록 (없으면 선택 자산 중심 근처)
      let gx, gy;
      const vb = inited && window.assetPlacementEditor.getViewBox ? window.assetPlacementEditor.getViewBox('ap-editor-container') : null;
      if (vb) {
        gx = Math.round((vb.x + (vb.w - bw) / 2) * 100) / 100;
        gy = Math.round((vb.y + (vb.h - bh) / 2) * 100) / 100;
      } else {
        let cx = 0, cy = 0; for (const id of ids) { const p = getPosition(id); cx += p.x; cy += p.y; } cx /= count; cy /= count;
        gx = Math.round((cx - bw / 2) * 100) / 100; gy = Math.round((cy - bh / 2) * 100) / 100;
      }

      groups.push({ id: tempId, name: '그룹', x: gx, y: gy, width: bw, height: bh, color: null, floor: 1 });
      groupMembers.set(tempId, ids);
      rebuildIndexes(); selAssets.clear(); markChanged(); refresh();
      toast('그룹이 생성되었습니다.');
    }
    // 그룹 해제 → 멤버를 숨기지 않고 그룹 근처 빈 공간에 그리드로 펼쳐 개별 배치
    function ungroupToCanvas(gid) {
      const g = groups.find(x => x.id === gid);
      if (!g) return;
      const ids = memberIdsOf(gid).slice();
      pushUndo();
      if (ids.length) {
        const layout = scatterNearGroup(g, ids);
        const sc = globalScale > 0 ? globalScale : 1;
        ids.forEach(aid => {
          const pos = getPosition(aid);
          pos.visible = true;
          pos.scale = sc;   // 그룹 해제된 자산은 도면 기본 크기(globalScale)로 맞춤 — 그리드 간격과 일치
          const t = layout.get(aid);
          if (t) { pos.x = t.x; pos.y = t.y; }
        });
      }
      groups = groups.filter(x => x.id !== gid);
      groupMembers.delete(gid);
      expanded.delete(gid);
      selGroups.clear(); selAssets = new Set(ids);
      rebuildIndexes(); markChanged(); refresh();
      toast(ids.length ? `그룹 해제 — ${ids.length}개 자산을 도면에 배치했습니다.` : '그룹을 해제했습니다.');
    }
    // 그룹 멤버용 그리드 블록을 그룹 중심 부근의 빈 공간에서 찾아 각 멤버의 중심 좌표를 반환
    function scatterNearGroup(g, ids) {
      const out = new Map();
      const n = ids.length;
      const scale = globalScale > 0 ? globalScale : 1;
      const sz = 32 * scale, gap = 14, step = sz + gap;
      const cols = Math.max(1, Math.ceil(Math.sqrt(n))), rows = Math.ceil(n / cols);
      const blockW = cols * step, blockH = rows * step;
      const maxX = Math.max(4, 1000 - blockW - 4), maxY = Math.max(4, 600 - blockH - 4);

      // 장애물: 해제 대상이 아닌 '보이는' 자산 + 다른 그룹의 바운딩박스
      const memberSet = new Set(ids), obstacles = [];
      for (const a of allAssets) {
        if (memberSet.has(a.assetId) || groupedIds.has(a.assetId)) continue;
        const p = posOf(a.assetId); if (!p.visible) continue;
        const s = 32 * (p.scale > 0 ? p.scale : 1);
        obstacles.push({ x: p.x - s / 2, y: p.y - s / 2, w: s, h: s });
      }
      groups.forEach(gr => { if (gr.id !== g.id) obstacles.push({ x: gr.x, y: gr.y, w: gr.width, h: gr.height }); });
      const hit = (x, y) => obstacles.some(o => x < o.x + o.w && x + blockW > o.x && y < o.y + o.h && y + blockH > o.y);

      const cx = g.x + g.width / 2, cy = g.y + g.height / 2;
      let bx = clamp(cx - blockW / 2, 4, maxX), by = clamp(cy - blockH / 2, 4, maxY);
      if (hit(bx, by)) {
        // 그룹 중심에서 바깥 링으로 한 칸씩 넓혀 가며 빈 공간 탐색
        outer:
        for (let r = 1; r <= 14; r++) {
          for (let dy = -r; dy <= r; dy++) for (let dx = -r; dx <= r; dx++) {
            if (Math.abs(dx) !== r && Math.abs(dy) !== r) continue; // 링 둘레만
            const tx = clamp(bx + dx * step, 4, maxX), ty = clamp(by + dy * step, 4, maxY);
            if (!hit(tx, ty)) { bx = tx; by = ty; break outer; }
          }
        }
        // 못 찾으면 원래(그룹 중심) 위치 그대로 사용 — 그래도 보이게는 둔다
      }
      ids.forEach((aid, i) => {
        const c = i % cols, r = Math.floor(i / cols);
        out.set(aid, { x: Math.round((bx + c * step + sz / 2) * 100) / 100, y: Math.round((by + r * step + sz / 2) * 100) / 100 });
      });
      return out;
    }
    function removeFromGroup(gid, aid) {
      const mems = groupMembers.get(gid); if (!mems) return;
      const i = mems.indexOf(aid); if (i < 0) return;
      pushUndo();
      mems.splice(i, 1);
      if (!mems.length) { groups = groups.filter(g => g.id !== gid); groupMembers.delete(gid); expanded.delete(gid); }
      rebuildIndexes(); markChanged(); refresh();
    }
    function onGroupFloorChanged(gid, floor) { if (!Number.isFinite(floor)) return; if (floor === 0) floor = 1; const g = groups.find(g => g.id === gid); if (!g || g.floor === floor) return; pushUndo(); g.floor = floor; markChanged(); refresh(); }
    function onSelectedGroupsFloor(floor) { if (!Number.isFinite(floor)) return; const f = floor === 0 ? 1 : floor; const targets = groups.filter(g => selGroups.has(g.id) && g.floor !== f); if (!targets.length) return; pushUndo(); targets.forEach(g => g.floor = f); markChanged(); refresh(); }

    // ── 정렬 / 분배 ──
    function align(mode) {
      if (selAssets.size < 2) return;
      pushUndo();
      const ids = [...selAssets].sort((a, b) => ((assetMap.get(a) || {}).name || '').localeCompare((assetMap.get(b) || {}).name || ''));
      const n = ids.length; let cx = 0, cy = 0;
      ids.forEach(id => { const p = getPosition(id); cx += p.x; cy += p.y; }); cx /= n; cy /= n;
      const cellW = 32 * globalScale + 12, cellH = 32 * globalScale + 24;
      if (mode === 'grid') {
        const cols = Math.ceil(Math.sqrt(n)), rows = Math.ceil(n / cols);
        const baseX = Math.max(0, cx - cols * cellW / 2), baseY = Math.max(0, cy - rows * cellH / 2);
        ids.forEach((id, i) => { const p = getPosition(id); p.x = Math.round((baseX + (i % cols) * cellW + cellW / 2) * 100) / 100; p.y = Math.round((baseY + Math.floor(i / cols) * cellH + cellH / 2) * 100) / 100; });
      } else if (mode === 'row') {
        const bx = Math.max(0, cx - n * cellW / 2);
        ids.forEach((id, i) => { const p = getPosition(id); p.x = Math.round((bx + i * cellW + cellW / 2) * 100) / 100; p.y = Math.round(cy * 100) / 100; });
      } else if (mode === 'col') {
        const by = Math.max(0, cy - n * cellH / 2);
        ids.forEach((id, i) => { const p = getPosition(id); p.x = Math.round(cx * 100) / 100; p.y = Math.round((by + i * cellH + cellH / 2) * 100) / 100; });
      }
      markChanged(); refresh(); toast(`${n}개 정렬됨`);
    }
    function distribute(axis) {
      if (selAssets.size < 3) return;
      pushUndo();
      let ids = [...selAssets];
      if (axis === 'h') { ids.sort((a, b) => getPosition(a).x - getPosition(b).x); const first = getPosition(ids[0]).x, last = getPosition(ids[ids.length - 1]).x, step = (last - first) / (ids.length - 1); for (let i = 1; i < ids.length - 1; i++) getPosition(ids[i]).x = Math.round((first + i * step) * 100) / 100; }
      else { ids.sort((a, b) => getPosition(a).y - getPosition(b).y); const first = getPosition(ids[0]).y, last = getPosition(ids[ids.length - 1]).y, step = (last - first) / (ids.length - 1); for (let i = 1; i < ids.length - 1; i++) getPosition(ids[i]).y = Math.round((first + i * step) * 100) / 100; }
      markChanged(); refresh(); toast(`${ids.length}개 균등 분배됨`);
    }
    function unplaceSelected() {
      let count = 0;
      for (const aid of selAssets) {
        const pos = getPosition(aid); if (pos.visible) { pos.visible = false; count++; }
        for (const [gid, mems] of groupMembers) { const i = mems.indexOf(aid); if (i >= 0) { mems.splice(i, 1); if (!mems.length) { groups = groups.filter(g => g.id !== gid); groupMembers.delete(gid); } } }
      }
      for (const gid of selGroups) { groups = groups.filter(g => g.id !== gid); groupMembers.delete(gid); count++; }
      selAssets.clear(); selGroups.clear(); rebuildIndexes(); markChanged();
      if (count > 0) toast(`${count}개 제거됨`);
    }

    function setGlobalScale(v) {
      v = Math.round(clamp(v, 0.1, 5) * 100) / 100; globalScale = v;
      for (const p of positions.values()) p.scale = v;
      const lbl = $('ap-scale-val'); if (lbl) lbl.textContent = v.toFixed(2) + '×';
      markChanged(); renderSvg(); updateToolbar();   // 슬라이더 드래그 중 가벼운 라이브 반영
    }
    // 선택한 개별 자산만 크기(스케일) 조절 — 슬라이더 드래그 중 라이브 반영
    function setSelectedScale(v) {
      v = Math.round(clamp(v, 0.1, 5) * 100) / 100;
      for (const aid of selAssets) getPosition(aid).scale = v;
      const lbl = $('ap-sel-size-val'); if (lbl) lbl.textContent = v.toFixed(2) + '×';
      markChanged(); renderSvg(); updateToolbar();
    }

    // ── 저장 (PlacementEditor.ApplyChangesAsync 이식) ──
    async function save() {
      const btn = $('ap-save'); btn.disabled = true;
      try {
        const posList = [];
        for (const [aid, p] of positions) if (p.visible || dbPositionIds.has(aid)) posList.push({ assetId: aid, x: p.x, y: p.y, scale: p.scale > 0 ? p.scale : 1, visible: p.visible });
        const grpList = groups.map(g => ({ name: g.name || '그룹', x: g.x, y: g.y, width: g.width, height: g.height, color: g.color, floor: g.floor, memberIds: memberIdsOf(g.id) }));
        const r = await api(`/api/admin/layout/${LID}/placement`, 'PUT', { positions: posList, groups: grpList });
        if (!r) return;
        dbPositionIds = new Set(posList.filter(p => p.visible).map(p => p.assetId));
        hasChanges = false; updateToolbar();
        toast(`${posList.filter(p => p.visible).length}개 자산, ${grpList.length}개 그룹 적용됨`);
      } finally { btn.disabled = false; }
    }

    function activate() {
      $('ap-editor-svg').setAttribute('viewBox', '0 0 1000 600');
      if (!shellBuilt) buildShell();
      $('ap-scale').value = globalScale.toFixed(2);
      const gsv = $('ap-scale-val'); if (gsv) gsv.textContent = globalScale.toFixed(2) + '×';
      renderSvg(); renderPanel(); updateSelActions(); updateToolbar();
      window.assetPlacementEditor.init('ap-editor-container', shim);
      window.assetPlacementEditor.setSnapConfig('ap-editor-container', neighborSnap, snapGridSize, gridSnap);
      window.assetPlacementEditor.setTool('ap-editor-container', activeTool);
      inited = true;
      updateToolbar();
    }
    function deactivate() { if (inited) { window.assetPlacementEditor.dispose('ap-editor-container'); inited = false; } }
    function hasUnsaved() { return hasChanges; }

    function bind() {
      $('ap-tool-pan').addEventListener('click', () => { activeTool = activeTool === 'pan' ? 'select' : 'pan'; window.assetPlacementEditor.setTool('ap-editor-container', activeTool); updateToolbar(); });
      $('ap-snap').addEventListener('click', () => { neighborSnap = !neighborSnap; window.assetPlacementEditor.setSnapConfig('ap-editor-container', neighborSnap, snapGridSize, gridSnap); updateToolbar(); });
      $('ap-gridsnap').addEventListener('click', () => { gridSnap = !gridSnap; window.assetPlacementEditor.setSnapConfig('ap-editor-container', neighborSnap, snapGridSize, gridSnap); renderSvg(); updateToolbar(); });
      $('ap-grid').addEventListener('change', e => { snapGridSize = clamp(parseInt(e.target.value, 10) || 20, 5, 100); e.target.value = snapGridSize; window.assetPlacementEditor.setSnapConfig('ap-editor-container', neighborSnap, snapGridSize, gridSnap); renderSvg(); });
      $('ap-zoom-in').addEventListener('click', () => { window.assetPlacementEditor.zoomIn('ap-editor-container'); updateToolbar(); });
      $('ap-zoom-out').addEventListener('click', () => { window.assetPlacementEditor.zoomOut('ap-editor-container'); updateToolbar(); });
      $('ap-zoom-reset').addEventListener('click', () => { window.assetPlacementEditor.resetZoom('ap-editor-container'); updateToolbar(); });
      $('ap-zoom-fit').addEventListener('click', () => { window.assetPlacementEditor.fitAll('ap-editor-container'); updateToolbar(); });
      $('ap-undo').addEventListener('click', doUndo);
      $('ap-redo').addEventListener('click', doRedo);
      const gscale = $('ap-scale');
      if (gscale) {
        let gUndo = false;
        gscale.addEventListener('pointerdown', () => { gUndo = false; });
        gscale.addEventListener('input', e => { if (!gUndo) { pushUndo(); gUndo = true; } setGlobalScale(parseFloat(e.target.value) || 1); });
      }
      $('ap-save').addEventListener('click', save);

      // 도면 위 우클릭 → 커스텀 메뉴 (자산/그룹/빈 영역에 따라 항목 구성)
      $('ap-editor-container').addEventListener('contextmenu', e => {
        e.preventDefault();
        const assetEl = e.target.closest('.ap-asset-icon');
        const groupEl = e.target.closest('.ap-group-container');
        const items = [];
        if (assetEl) {
          const aid = parseInt(assetEl.dataset.assetId, 10);
          const selCount = selAssets.size + selGroups.size;
          const many = selAssets.has(aid) && selCount > 1;
          items.push({ icon: 'visibility_off', label: many ? `미배치로 제거 (${selCount}개)` : '미배치로 제거',
            onClick: many ? () => { pushUndo(); unplaceSelected(); refresh(); } : () => unplaceAsset(aid) });
          if (selAssets.size >= 2) items.push({ icon: 'workspaces', label: `선택 자산 그룹화 (${selAssets.size}개)`, onClick: createGroupFromSelection });
          items.push({ sep: true });
        } else if (groupEl) {
          const gid = parseInt(groupEl.dataset.groupId, 10);
          items.push({ icon: 'grid_view', label: '그룹 해제 (개별 배치)', onClick: () => ungroupToCanvas(gid) });
          items.push({ sep: true });
        }
        items.push({ icon: 'fit_screen', label: '전체 보기', onClick: () => { window.assetPlacementEditor.fitAll('ap-editor-container'); updateToolbar(); } });
        items.push({ icon: 'center_focus_weak', label: '100% 보기', onClick: () => { window.assetPlacementEditor.resetZoom('ap-editor-container'); updateToolbar(); } });
        items.push({ icon: 'select_all', label: '전체 선택', onClick: () => onKeyAction('selectAll') });
        CtxMenu.show(e.clientX, e.clientY, items);
      });
    }

    return { load, bind, activate, deactivate, hasUnsaved };
  })();

  /* ═══════════════════════════════════════════════════════════════════════
   * 탭 전환 + 부트스트랩
   * ═════════════════════════════════════════════════════════════════════*/
  let activeTab = 'config';
  const EDITOR_TABS = { lines: Bp, assets: Ap };

  function switchTab(tab) {
    if (tab === activeTab) return;
    // 떠나는 탭에 미저장 변경이 있으면 확인
    const leaving = EDITOR_TABS[activeTab];
    if (leaving && leaving.hasUnsaved() && !confirm('저장하지 않은 변경사항이 있습니다. 탭을 전환하시겠습니까?')) {
      document.querySelectorAll('#le-tabs .lv-seg-btn').forEach(b => b.classList.toggle('active', b.dataset.tab === activeTab));
      return;
    }
    if (leaving) leaving.deactivate();

    activeTab = tab;
    document.querySelectorAll('#le-tabs .lv-seg-btn').forEach(b => b.classList.toggle('active', b.dataset.tab === tab));
    ['config', 'lines', 'assets'].forEach(t => { $('tab-' + t).hidden = (t !== tab); });

    if (tab === 'config') Cfg.load();
    else EDITOR_TABS[tab].activate();
  }

  async function boot() {
    if (window.Shell) await Shell.init({ active: 'layout' });
    const m = location.pathname.match(/\/admin\/layout\/(\d+)\/edit/);
    LID = m ? parseInt(m[1], 10) : 0;
    if (!LID) { $('le-loading').textContent = '잘못된 레이아웃입니다.'; return; }

    const d = await api(`/api/admin/layout/${LID}/edit-data`, 'GET');
    if (!d) { $('le-loading').textContent = '레이아웃 데이터를 불러올 수 없습니다.'; return; }

    config = d.config || { bgColor: '#ffffff', gridColor: '#e0e0e0', gridEnabled: true, gridSize: 20 };
    allAssets = d.assets || [];
    assetMap = new Map(allAssets.map(a => [a.assetId, a]));
    linesList = d.lines || [];
    lineMap = {}; linesList.forEach(l => lineMap[l.id] = l.name);
    window.__rectsSeed = d.rects || [];

    $('le-title').textContent = (d.layout && d.layout.name) ? d.layout.name : '레이아웃 편집';
    document.title = `${(d.layout && d.layout.name) || '레이아웃'} 편집 — TWMS`;

    Cfg.bind(); Bp.bind(); Bp.load(); Ap.bind(); Ap.load(d);

    // 탭 버튼 + 초기 탭(도면 설정) 표시
    document.querySelectorAll('#le-tabs .lv-seg-btn').forEach(b => b.addEventListener('click', () => switchTab(b.dataset.tab)));
    $('le-loading').hidden = true;
    $('tab-config').hidden = false;
    Cfg.load();

    window.addEventListener('beforeunload', (e) => { if (Bp.hasUnsaved() || Ap.hasUnsaved()) { e.preventDefault(); e.returnValue = ''; } });
  }

  document.addEventListener('DOMContentLoaded', boot);
})();
