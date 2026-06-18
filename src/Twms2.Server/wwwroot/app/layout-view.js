/* ============================================================================
 * 레이아웃(도면 보기) — /layout 정적 페이지.
 * LayoutView.razor + 자식 렌더러(BlueprintView / AssetPlacementView)의
 * 읽기 전용 SVG 시각화를 형태 유지하여 재현.
 *   - viewMode 0(라인별): 도면 이미지 + 라인 영역 사각형 + 영역 내 자산 아이콘.
 *   - viewMode 1(개별):   도면 이미지 + 그룹 박스(층별 3D 톤) + 그룹/단독 자산 마커.
 * 자산 마커는 health 색으로 칠하고 <title> 툴팁, 클릭 시 /assets/{id} 로 이동.
 * 편집(드래그/배치/그룹 생성)은 Blazor 편집기에 유지(아래 deviation).
 * GET /api/layout?layoutId= → 스냅샷. 30초 폴링 + 탭 복귀 시 갱신.
 * ==========================================================================*/
(function () {
  'use strict';

  const SVGNS = 'http://www.w3.org/2000/svg';
  const VB_W = 1000, VB_H = 600;
  const HEALTH_COLOR = {
    backedup: '#65B991', unchanged: '#6BA0DE', failed: '#E67E7E',
    inprogress: '#f59e0b', unknown: '#999',
  };
  const HEALTH_LABEL = {
    backedup: '백업 갱신', unchanged: '변경 없음', failed: '작업 실패',
    inprogress: '작업중', unknown: '내역 없음',
  };

  const $ = (id) => document.getElementById(id);

  let DATA = null;
  let SELECTED_LAYOUT = null;   // layoutId
  let VIEW_MODE = 0;            // 0=라인별, 1=개별
  let HIDDEN_FLOORS = null;     // 개별 뷰: 숨김 층 집합(기본 1층만 표시). null=미초기화

  // ── viewMode localStorage 복원 (LayoutView OnInitializedAsync 이식) ──
  try {
    const saved = localStorage.getItem('twms-layout-viewMode');
    const m = parseInt(saved, 10);
    if (m === 0 || m === 1) VIEW_MODE = m;
  } catch (e) { /* ignore */ }

  // ── 유틸 ──────────────────────────────────────────────────────────────
  function svgEl(name, attrs) {
    const el = document.createElementNS(SVGNS, name);
    if (attrs) for (const k in attrs) { if (attrs[k] != null) el.setAttribute(k, attrs[k]); }
    return el;
  }
  function colorFor(asset) { return HEALTH_COLOR[asset.health] || HEALTH_COLOR.unknown; }
  function fmtMinute(s) {
    if (!s) return '없음';
    const d = new Date(s); if (isNaN(d)) return s;
    const p = (n) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
  }
  function floorLabel(f) { return f == null ? '' : (f < 0 ? `B${-f}` : `${f}F`); }

  // SVG <title> 툴팁 (LayoutHelpers.RenderSvgTitle 이식)
  function titleFor(asset, floor) {
    const online = asset.pingReachable == null ? 'Ping 내역없음'
      : (asset.pingReachable ? '온라인' : '오프라인');
    const health = HEALTH_LABEL[asset.health] || HEALTH_LABEL.unknown;
    const via = asset.ipVia ? `\n경유IP: ${asset.ipVia}` : '';
    const fl = (floor != null) ? `\n층: ${floorLabel(floor)}` : '';
    return `${asset.name}\nIP: ${asset.ip || '-'}${via}${fl}\n상태: ${health}\n${online}\n최근 백업: ${fmtMinute(asset.lastBackupTime)}`;
  }

  // 도면 이미지 렌더 영역 계산 (LayoutHelpers.CalcImageRect 이식 — xMidYMid meet)
  function calcImageRect(config) {
    if (!config || !(config.imageWidth > 0) || !(config.imageHeight > 0))
      return { x: 0, y: 0, w: VB_W, h: VB_H };
    const imgRatio = config.imageWidth / config.imageHeight;
    const vbRatio = VB_W / VB_H;
    if (imgRatio > vbRatio) { const h = VB_W / imgRatio; return { x: 0, y: (VB_H - h) / 2, w: VB_W, h }; }
    const w = VB_H * imgRatio; return { x: (VB_W - w) / 2, y: 0, w, h: VB_H };
  }

  function assetById(id) {
    const m = DATA._assetMap;
    return m ? m.get(id) : null;
  }

  // 자산 마커 <g> (사각 배경 + 아이콘 + 툴팁), 클릭 → /assets/{id}
  function assetMarker(asset, x, y, size, floor) {
    const g = svgEl('g', {
      transform: `translate(${x}, ${y})`,
      class: 'lv-asset-icon',
      style: 'cursor:pointer',
      opacity: '0.92',
    });
    g.appendChild(makeTitle(asset, floor));
    g.appendChild(svgEl('rect', {
      width: size, height: size, rx: Math.max(1, size * 0.12),
      fill: asset.iconBgColor || '#e0e0e0', stroke: colorFor(asset),
      'stroke-width': Math.max(0.5, size * 0.05),
    }));
    g.appendChild(svgEl('image', {
      href: '/' + String(asset.icon || 'images/icons/plc.png').replace(/^\//, ''),
      x: size * 0.08, y: size * 0.08, width: size * 0.84, height: size * 0.84,
    }));
    g.addEventListener('click', (e) => { e.stopPropagation(); location.href = `/assets/${asset.assetId}`; });
    return g;
  }
  function makeTitle(asset, floor) {
    const t = svgEl('title');
    t.textContent = titleFor(asset, floor);
    return t;
  }

  // 자식(2단 경유) 자산 id 집합: ipVia 가 다른 자산의 ip 와 일치
  function childAssetIds() {
    const ipMap = new Map();
    for (const a of DATA.assets) { if (a.ip && !ipMap.has(a.ip)) ipMap.set(a.ip, a); }
    const children = new Set();
    for (const a of DATA.assets) {
      if (!a.ipVia) continue;
      const plc = ipMap.get(a.ipVia);
      if (plc && plc.assetId !== a.assetId) children.add(a.assetId);
    }
    return children;
  }

  // 영역/그룹 내 그리드 배치 (LayoutGroupAssets 이식: 비율 기반 cols/rows, 중앙정렬)
  function gridLayout(x, y, w, h, count, pad) {
    pad = pad == null ? 2.0 : pad;
    const availW = w - pad * 2, availH = h - pad * 2;
    if (availW < 4 || availH < 4 || count === 0) return [];
    const aspect = availW / availH;
    const bestCols = Math.max(1, Math.round(Math.sqrt(count * aspect)));
    const cols = Math.min(bestCols, count);
    const rows = Math.ceil(count / cols);
    const rawSz = Math.min(availW / cols, availH / rows);
    const gap = Math.max(1.0, Math.min(rawSz * 0.1, 2.0));
    const cellW = (availW - gap * (cols - 1)) / cols;
    const cellH = (availH - gap * (rows - 1)) / rows;
    const sz = Math.max(4, Math.min(cellW, cellH));
    const totalW = cols * sz + (cols - 1) * gap;
    const totalH = rows * sz + (rows - 1) * gap;
    const startX = x + pad + (availW - totalW) / 2;
    const startY = y + pad + (availH - totalH) / 2;
    const out = [];
    for (let i = 0; i < count; i++) {
      const col = i % cols, row = Math.floor(i / cols);
      out.push({ x: startX + col * (sz + gap), y: startY + row * (sz + gap), size: sz });
    }
    return out;
  }

  // ── 라인별(도면) 뷰 SVG ────────────────────────────────────────────────
  function renderLineView(svg) {
    const lineMap = new Map(DATA.lines.map(l => [l.id, l]));
    const child = childAssetIds();

    for (const rect of DATA.blueprintRects) {
      const line = lineMap.get(rect.lineId);
      if (!line) continue;
      const lineAssets = DATA.assets.filter(a => a.lineId === rect.lineId);
      const topLevel = lineAssets.filter(a => !child.has(a.assetId));
      const healthColor = aggregateColor(lineAssets);

      const g = svgEl('g');
      // 영역 배경
      g.appendChild(svgEl('rect', {
        x: rect.x, y: rect.y, width: rect.width, height: rect.height, rx: 6, ry: 6,
        fill: healthColor + '10', stroke: healthColor, 'stroke-width': 0.7, 'stroke-opacity': 0.6,
      }));
      // 라인 이름 (상단 가운데)
      const label = svgEl('text', {
        x: rect.x + rect.width / 2, y: rect.y + 15, 'text-anchor': 'middle',
        fill: 'white', 'font-size': 12, 'font-weight': 'bold',
        stroke: healthColor, 'stroke-width': 0.3, 'paint-order': 'stroke',
      });
      label.textContent = line.name;
      g.appendChild(label);
      // 자산 수 배지
      if (lineAssets.length > 0) {
        const estW = [...line.name].reduce((s, c) => s + (c.charCodeAt(0) > 127 ? 12 : 7), 0);
        const bcx = rect.x + rect.width / 2 + estW / 2 + 8, bcy = rect.y + 11;
        g.appendChild(svgEl('circle', { cx: bcx, cy: bcy, r: 5.5, fill: healthColor, stroke: 'white', 'stroke-width': 0.4 }));
        const bt = svgEl('text', { x: bcx, y: bcy + 2.5, 'text-anchor': 'middle', 'font-size': 6, fill: 'white', 'font-weight': 'bold' });
        bt.textContent = lineAssets.length;
        g.appendChild(bt);
      }

      // 영역 내부 자산 아이콘 (clipPath 로 영역 내 제한) — 1단(topLevel) 그리드 배치
      const clipId = `lv-line-clip-${rect.lineId}`;
      const clip = svgEl('clipPath', { id: clipId });
      clip.appendChild(svgEl('rect', { x: rect.x, y: rect.y, width: rect.width, height: rect.height, rx: 6 }));
      g.appendChild(clip);

      const inner = svgEl('g', { 'clip-path': `url(#${clipId})` });
      const headerH = 20, padBottom = 4, padSide = 4;
      const cells = gridLayout(rect.x + padSide, rect.y + headerH,
        rect.width - padSide * 2, rect.height - headerH - padBottom, topLevel.length, 0);
      topLevel.forEach((a, i) => {
        const c = cells[i]; if (!c) return;
        inner.appendChild(assetMarker(a, c.x, c.y, c.size, a.floor));
      });
      g.appendChild(inner);
      svg.appendChild(g);
    }
  }

  function aggregateColor(assets) {
    if (assets.length === 0) return '#999';
    if (assets.some(a => a.health === 'failed')) return '#E67E7E';
    if (assets.some(a => a.health === 'inprogress')) return '#f59e0b';
    if (assets.some(a => a.health === 'backedup')) return '#65B991';
    if (assets.some(a => a.health === 'unchanged')) return '#6BA0DE';
    return '#999';
  }

  // ── 개별(배치도) 뷰 SVG ───────────────────────────────────────────────
  function renderPlacementView(svg) {
    // 그룹 멤버 자산 id → 그룹 소속 여부
    const groupedIds = new Set(DATA.groupMembers.map(m => m.assetId));
    const membersByGroup = new Map();
    for (const m of DATA.groupMembers) {
      if (!membersByGroup.has(m.groupId)) membersByGroup.set(m.groupId, []);
      membersByGroup.get(m.groupId).push(m.assetId);
    }

    // 그룹 박스 + 멤버 (층 필터 적용)
    for (const grp of DATA.groups) {
      const memberIds = membersByGroup.get(grp.id) || [];
      let members = memberIds.map(id => assetById(id)).filter(Boolean)
        .filter(a => !(HIDDEN_FLOORS && a.floor != null && HIDDEN_FLOORS.has(a.floor)));
      if (members.length === 0) continue;

      const grpColor = grp.color || '#4a90d9';
      const g = svgEl('g');
      const rRx = Math.min(6.0, Math.min(grp.width, grp.height) / 2);
      // 층 기반 3D 톤 (AssetPlacementView 이식: >=2 튀어나옴, <0 들어감, 그 외 점선)
      if (grp.floor >= 2) {
        g.appendChild(svgEl('rect', {
          x: grp.x, y: grp.y, width: grp.width, height: grp.height, rx: rRx,
          fill: grpColor, 'fill-opacity': 0.5, stroke: grpColor, 'stroke-width': 0.8, 'stroke-opacity': 0.75,
          style: 'filter: drop-shadow(4px 6px 3px rgba(0,0,0,0.4));',
        }));
      } else if (grp.floor < 0) {
        g.appendChild(svgEl('rect', {
          x: grp.x, y: grp.y, width: grp.width, height: grp.height, rx: rRx,
          fill: '#000', 'fill-opacity': 0.38, stroke: grpColor, 'stroke-width': 0.8, 'stroke-opacity': 0.7,
        }));
      } else {
        g.appendChild(svgEl('rect', {
          x: grp.x, y: grp.y, width: grp.width, height: grp.height, rx: rRx,
          fill: grpColor + '10', stroke: grpColor, 'stroke-width': 0.7, 'stroke-dasharray': '6 3',
        }));
      }

      // 멤버 아이콘 (clipPath 로 영역 내 제한)
      const clipId = `lv-grp-clip-${grp.id}`;
      const clip = svgEl('clipPath', { id: clipId });
      clip.appendChild(svgEl('rect', { x: grp.x, y: grp.y, width: grp.width, height: grp.height, rx: rRx }));
      g.appendChild(clip);
      const inner = svgEl('g', { 'clip-path': `url(#${clipId})` });
      const cells = gridLayout(grp.x, grp.y, grp.width, grp.height, members.length, 2.0);
      members.forEach((a, i) => {
        const c = cells[i]; if (!c) return;
        inner.appendChild(assetMarker(a, c.x, c.y, c.size, grp.floor));
      });
      g.appendChild(inner);
      svg.appendChild(g);
    }

    // 그룹 미소속 + 위치 지정 자산 마커 (TwmsAssetPosition: viewBox 좌표, 중심 기준)
    for (const pos of DATA.positions) {
      if (groupedIds.has(pos.assetId)) continue;
      const a = assetById(pos.assetId);
      if (!a) continue;
      const s = pos.scale || 1;
      const sz = 36 * s;
      svg.appendChild(assetMarker(a, pos.x - sz / 2, pos.y - sz / 2, sz, a.floor));
    }
  }

  // ── 전체 렌더 ─────────────────────────────────────────────────────────
  function renderTabs() {
    const tabs = $('lv-layout-tabs');
    if (!DATA || DATA.layouts.length <= 1) { tabs.style.display = 'none'; tabs.innerHTML = ''; return; }
    tabs.style.display = 'flex';
    tabs.innerHTML = DATA.layouts.map(l =>
      `<button class="lv-tab${l.id === SELECTED_LAYOUT ? ' active' : ''}" data-layout="${l.id}">${escapeHtml(l.name)}</button>`
    ).join('');
    tabs.querySelectorAll('.lv-tab').forEach(btn => {
      btn.addEventListener('click', () => {
        const id = parseInt(btn.dataset.layout, 10);
        if (id !== SELECTED_LAYOUT) { SELECTED_LAYOUT = id; HIDDEN_FLOORS = null; load(); }
      });
    });
  }

  function escapeHtml(s) {
    return String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  }

  function initFloorFilter() {
    // AssetPlacementView 기본값: 1층만 표시, 나머지 숨김 (그룹 floor 기준)
    if (HIDDEN_FLOORS != null) return;
    const floors = [...new Set(DATA.groups.map(g => g.floor).filter(f => f != null))];
    HIDDEN_FLOORS = new Set(floors.filter(f => f !== 1));
  }

  function render() {
    const vp = $('lv-viewport');
    if (!DATA) { vp.innerHTML = '<div class="lv-loading">불러오는 중…</div>'; return; }

    const hasContent = DATA.blueprintRects.length > 0 || DATA.positions.length > 0 || DATA.groups.length > 0;
    if (DATA.layouts.length === 0 || !hasContent) {
      vp.innerHTML = '<div class="lv-empty">배치된 자산이 없습니다. 관리 &gt; 레이아웃 관리에서 자산을 배치해 주세요.</div>';
      $('lv-count').textContent = '';
      return;
    }

    initFloorFilter();

    const svg = svgEl('svg', { viewBox: `0 0 ${VB_W} ${VB_H}`, preserveAspectRatio: 'xMidYMid meet' });
    svg.style.backgroundColor = (DATA.config && DATA.config.bgColor) || '#1a1a2e';

    // 도면 이미지 배경
    if (DATA.config && DATA.config.imagePath) {
      const ir = calcImageRect(DATA.config);
      svg.appendChild(svgEl('image', {
        href: '/' + String(DATA.config.imagePath).replace(/^\//, ''),
        x: ir.x, y: ir.y, width: ir.w, height: ir.h,
        preserveAspectRatio: 'none', opacity: 0.85,
      }));
    }

    if (VIEW_MODE === 0) renderLineView(svg);
    else renderPlacementView(svg);

    vp.innerHTML = '';
    vp.appendChild(svg);

    // 표시 카운트
    const shown = svg.querySelectorAll('.lv-asset-icon').length;
    $('lv-count').textContent = `${shown}개 표시 · 자산 클릭 시 상세 보기`;
  }

  function setViewMode(m) {
    VIEW_MODE = m;
    try { localStorage.setItem('twms-layout-viewMode', String(m)); } catch (e) { /* ignore */ }
    document.querySelectorAll('#lv-viewmode .lv-seg-btn').forEach(b =>
      b.classList.toggle('active', parseInt(b.dataset.view, 10) === m));
    render();
  }

  // ── 데이터 로드 ───────────────────────────────────────────────────────
  async function load() {
    try {
      const url = SELECTED_LAYOUT ? `/api/layout?layoutId=${SELECTED_LAYOUT}` : '/api/layout';
      const res = await fetch(url, { headers: { 'Accept': 'application/json' } });
      if (!res.ok) return;
      const d = await res.json();
      DATA = d;
      DATA._assetMap = new Map(d.assets.map(a => [a.assetId, a]));
      SELECTED_LAYOUT = d.selectedLayoutId || null;
      renderTabs();
      render();
    } catch (e) { /* 무시 */ }
  }

  document.addEventListener('DOMContentLoaded', async () => {
    if (window.Shell) await Shell.init({ active: 'layout' });

    // 뷰 모드 토글 활성 상태 동기화 + 핸들러
    document.querySelectorAll('#lv-viewmode .lv-seg-btn').forEach(b => {
      b.classList.toggle('active', parseInt(b.dataset.view, 10) === VIEW_MODE);
      b.addEventListener('click', () => setViewMode(parseInt(b.dataset.view, 10)));
    });

    await load();
    // 30초 폴링 (원본 _statusTimer 30초 갱신 이식) + 탭 복귀 시 갱신
    setInterval(load, 30000);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) load(); });
  });
})();
