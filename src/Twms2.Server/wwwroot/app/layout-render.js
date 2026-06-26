/* ============================================================================
 * layout-render.js — 도면(레이아웃) 읽기전용 SVG 렌더러 (인스턴스 기반 공용 모듈).
 * /layout 페이지(layout-view.js)와 대시보드 히어로 위젯이 공유한다.
 *
 * 사용:
 *   const r = LayoutRenderer.mount({
 *     viewport:      'lv-viewport',    // 필수: SVG 호스트 (id 문자열 또는 element)
 *     viewmode:      'lv-viewmode',    // 선택: 라인별/개별 토글 컨테이너 ([data-view] 버튼)
 *     tabs:          'lv-layout-tabs', // 선택: 레이아웃 선택 탭 호스트
 *     count:         'lv-count',       // 선택: "N개 표시" 카운트 라벨
 *     fullscreenBtn: 'lv-fs',          // 선택: 전체화면 버튼 (Phase 4)
 *     layoutId:      null,             // 선택: 초기 레이아웃 (미지정 시 서버 기본=SortOrder 최상위)
 *     defaultMode:   0,                // 선택: 0=라인별, 1=개별 (localStorage 값 우선)
 *     storeKey:      'twms-...',       // 선택: viewMode 저장 키 (페이지별 독립 기억; 미지정 시 공용)
 *     splitBtn:      'lv-split',       // 선택: 분할(도면 여러개 동시) 토글 버튼 (도면 2개 이상에서만 노출)
 *     splitStoreKey: 'twms-...',       // 선택: 분할 on/off 저장 키
 *     poll:          30000,            // 선택: 자동 갱신 주기(ms). 미지정 시 1회만 로드.
 *   });
 *   r.refresh() / r.setMode(m) / r.setLayout(id) / r.destroy()
 *
 * 데이터: GET /api/layout?layoutId=  (LayoutViewController). 읽기전용.
 * 원본: layout-view.js(BlueprintView / AssetPlacementView 이식)를 모듈화 — 렌더 로직 동일.
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
  let _vpSeq = 0; // viewport 자동 id 시퀀스 (blueprintZoom 은 getElementById 필요)

  // ── 무상태 유틸 ───────────────────────────────────────────────────────────
  function svgEl(name, attrs) {
    const el = document.createElementNS(SVGNS, name);
    if (attrs) for (const k in attrs) { if (attrs[k] != null) el.setAttribute(k, attrs[k]); }
    return el;
  }
  function colorFor(asset) { return HEALTH_COLOR[asset.health] || HEALTH_COLOR.unknown; }
  function escapeHtml(s) {
    return String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  }
  function fmtMinute(s) {
    if (!s) return '없음';
    const d = new Date(s); if (isNaN(d)) return s;
    const p = (n) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
  }
  function floorLabel(f) { return f == null ? '' : (f < 0 ? `B${-f}` : `${f}F`); }

  // 툴팁 키/값 한 줄 (lv-tip-body 의 2열 그리드 한 행). vHtml 는 이미 안전한 HTML.
  function tipRow(k, vHtml) {
    return `<div class="lv-tip-row"><span class="lv-tip-k">${escapeHtml(k)}</span><span class="lv-tip-v">${vHtml}</span></div>`;
  }
  // 자산 hover 툴팁 HTML — 머리글(상태색 점 + 이름) + 키/값 표, 상태/연결은 색상 구분.
  function titleFor(asset, floor) {
    const hc = colorFor(asset);
    const health = HEALTH_LABEL[asset.health] || HEALTH_LABEL.unknown;
    let conn, connColor;
    if (asset.pingReachable == null) { conn = 'Ping 내역없음'; connColor = 'var(--c-on-surface-variant)'; }
    else if (asset.pingReachable) { conn = '온라인'; connColor = HEALTH_COLOR.backedup; }
    else { conn = '오프라인'; connColor = HEALTH_COLOR.failed; }
    const rows = [tipRow('IP', escapeHtml(asset.ip || '-'))];
    if (asset.ipVia) rows.push(tipRow('경유IP', escapeHtml(asset.ipVia)));
    if (floor != null) rows.push(tipRow('층', escapeHtml(floorLabel(floor))));
    rows.push(tipRow('상태', `<span style="color:${hc}">${escapeHtml(health)}</span>`));
    rows.push(tipRow('연결', `<span style="color:${connColor}">${escapeHtml(conn)}</span>`));
    rows.push(tipRow('최근 백업', escapeHtml(fmtMinute(asset.lastBackupTime))));
    return `<div class="lv-tip-head"><span class="lv-tip-dot" style="background:${hc}"></span>`
      + `<span class="lv-tip-name">${escapeHtml(asset.name)}</span></div>`
      + `<div class="lv-tip-body">${rows.join('')}</div>`;
  }
  // 그룹(다수 동종 자산) hover 툴팁 HTML. 다수면 "자산 목록", 단일이면 "상세"로 안내.
  function groupTipHtml(grp) {
    const hint = grp.count > 1 ? '클릭 시 자산 목록 보기' : '클릭 시 자산 상세';
    return `<div class="lv-tip-head"><span class="lv-tip-name">`
      + `${escapeHtml(grp.rep.typeName || '자산')} ${grp.count}개</span></div>`
      + `<div class="lv-tip-body">${tipRow('경유', escapeHtml(grp.rep.ipVia || '-'))}`
      + `<div class="lv-tip-hint">${hint}</div></div>`;
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

  // 자산 마커 <g> (사각 배경 + 아이콘 + 툴팁), 클릭 → /assets/{id}
  // 툴팁 텍스트는 data-tip 에 담아 두고, 뷰포트의 커스텀 HTML 툴팁이 hover 시 표시한다.
  function assetMarker(asset, x, y, size, floor) {
    const g = svgEl('g', {
      transform: `translate(${x}, ${y})`,
      class: 'lv-asset-icon',
      style: 'cursor:pointer',
      opacity: '0.92',
      'data-tip': titleFor(asset, floor),
      'data-asset-id': asset.assetId, // 클릭은 인스턴스 위임 핸들러가 처리(네비/팝오버)
    });
    g.appendChild(svgEl('rect', {
      width: size, height: size, rx: Math.max(1, size * 0.12),
      fill: asset.iconBgColor || '#e0e0e0', stroke: colorFor(asset),
      'stroke-width': Math.max(0.5, size * 0.05),
    }));
    g.appendChild(svgEl('image', {
      href: '/' + String(asset.icon || 'images/icons/plc.png').replace(/^\//, ''),
      x: size * 0.08, y: size * 0.08, width: size * 0.84, height: size * 0.84,
    }));
    return g;
  }

  // PLC 계층 (BlueprintView.BuildPlcHierarchy 이식): ipVia 가 다른 자산의 ip 와 일치하면
  // 그 자산을 2단 경유(자식)로 본다. childIds=자식 id 집합, plcChildren=부모 assetId→자식[].
  function buildPlcHierarchy(assets) {
    const ipMap = new Map();
    for (const a of assets) { if (a.ip && !ipMap.has(a.ip)) ipMap.set(a.ip, a); }
    const childIds = new Set();
    const plcChildren = new Map();
    for (const a of assets) {
      if (!a.ipVia) continue;
      const plc = ipMap.get(a.ipVia);
      if (plc && plc.assetId !== a.assetId) {
        if (!plcChildren.has(plc.assetId)) plcChildren.set(plc.assetId, []);
        plcChildren.get(plc.assetId).push(a);
        childIds.add(a.assetId);
      }
    }
    return { childIds, plcChildren };
  }

  // 자식 자산을 아이콘(종류)별로 묶음 → [{icon, items[]}] (BlueprintView GroupBy(GetAssetIcon) 이식)
  function groupByIcon(children) {
    const map = new Map();
    for (const c of children) {
      const icon = c.icon || 'images/icons/plc.png';
      if (!map.has(icon)) map.set(icon, []);
      map.get(icon).push(c);
    }
    return [...map.entries()].map(([icon, items]) => ({ icon, items }));
  }

  // 건강 색상 hex → 밝은 그룹 아이콘 배경색 (LayoutHelpers.GetIconBgColorFromHex 이식)
  const ICON_BG_FROM_HEX = {
    '#65B991': '#d4f0e0', '#6BA0DE': '#d6e8f7', '#E67E7E': '#f5d0d0', '#f59e0b': '#fde8c4',
  };
  function iconBgFromHex(hex) { return ICON_BG_FROM_HEX[hex] || '#e0e0e0'; }

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

  function aggregateColor(assets) {
    if (assets.length === 0) return '#999';
    if (assets.some(a => a.health === 'failed')) return '#E67E7E';
    if (assets.some(a => a.health === 'inprogress')) return '#f59e0b';
    if (assets.some(a => a.health === 'backedup')) return '#65B991';
    if (assets.some(a => a.health === 'unchanged')) return '#6BA0DE';
    return '#999';
  }

  // ── 라인 영역 내 1단 자산 배치 (BlueprintView.LayoutLineAssets 이식) ───────────
  // PLC(2단 경유 자식 보유)는 IP+자식 그룹을 담는 "카드"로, 나머지는 standalone 아이콘으로.
  function layoutLineAssets(rect, topLevel, plcChildren) {
    const standalones = [], plcCards = [];
    if (topLevel.length === 0) return { standalones, plcCards };

    const padSide = 4.0, padBottom = 4.0, headerH = 20.0;
    const innerGap = 4.0, rowGap = 4.0, ipLineH = 10.0, ipFontSz = 7.0;

    const availW = rect.width - padSide * 2;
    const availH = rect.height - headerH - padBottom;
    if (availW < 4 || availH < 4) return { standalones, plcCards };

    // 아이콘 크기 결정 (영역 비율 기반)
    const aspectRatio = availW / Math.max(1, availH);
    const bestCols = Math.max(1, Math.round(Math.sqrt(topLevel.length * aspectRatio)));
    const initCols = Math.min(bestCols, topLevel.length);
    const initRows = Math.ceil(topLevel.length / initCols);
    const cellH = (availH - rowGap * Math.max(0, initRows - 1)) / Math.max(1, initRows);
    let sz = Math.max(6, Math.min(cellH, 36));

    const plcTotal = topLevel.filter(a => plcChildren.has(a.assetId)).length;
    if (plcTotal > 0) {
      const testHorizW = sz + 80;
      if (testHorizW > availW || availH > availW * 1.5)
        sz = Math.max(6, Math.min(sz, (cellH - 16) / 1.45));
    }
    const childSz = Math.round(Math.min(sz * 0.5, 24) * 10) / 10;

    // 1) 자연 크기 계산
    const plans = [];
    for (const asset of topLevel) {
      const children = plcChildren.get(asset.assetId);
      if (children) {
        const groups = groupByIcon(children);
        const hasIp = !!asset.ip;
        const ipLen = asset.ip ? asset.ip.length : 0;
        const ipTextW = ipLen * ipFontSz * 0.62 + 8;
        const childrenW = groups.length > 0
          ? groups.length * childSz + (groups.length - 1) * innerGap : 0;
        const rightW = Math.max(Math.max(childrenW + 8, ipTextW), 40);
        const horizW = sz + innerGap + rightW + 3;
        const vertW = Math.max(sz, Math.max(ipTextW, childrenW));
        const vertH = sz + (hasIp ? ipLineH : 0) + (groups.length > 0 ? childSz + 2 : 0) + 4;
        const useVert = horizW > availW || availH > availW * 1.5;
        plans.push({
          asset, isPlc: true, plcGroups: groups, vertical: useVert, hasIp,
          natW: useVert ? vertW : horizW, natH: useVert ? vertH : sz,
        });
      } else {
        plans.push({ asset, isPlc: false, plcGroups: [], natW: sz, natH: sz });
      }
    }

    // 2) 행으로 포장 (greedy)
    const rowsPacked = [];
    let cur = [], curW = 0;
    for (const it of plans) {
      let tryW = cur.length === 0 ? it.natW : curW + innerGap + it.natW;
      if (tryW > availW && cur.length > 0) { rowsPacked.push(cur); cur = []; tryW = it.natW; }
      cur.push(it); curW = tryW;
    }
    if (cur.length > 0) rowsPacked.push(cur);

    // 3) 수직 중앙 정렬
    const rowHeights = rowsPacked.map(r => Math.max(...r.map(i => i.natH)));
    const totalH = rowHeights.reduce((s, h) => s + h, 0) + rowGap * Math.max(0, rowsPacked.length - 1);
    const yStart = rect.y + headerH + Math.max(padBottom, (availH - totalH) / 2);

    // 4) 행별 수평 스트레치 + 배치
    for (let ri = 0; ri < rowsPacked.length; ri++) {
      const row = rowsPacked[ri];
      const natW = row.reduce((s, i) => s + i.natW, 0) + innerGap * Math.max(0, row.length - 1);
      const extraW = Math.max(0, availW - natW);
      const plcsInRow = row.filter(i => i.isPlc).length;

      let perPlcExtra = 0, edgePad = 0, extraInter = 0;
      if (plcsInRow > 0) { perPlcExtra = extraW / plcsInRow; }
      else if (row.length === 1) { edgePad = extraW / 2; }
      else { const slots = row.length + 1; edgePad = extraW / slots; extraInter = extraW / slots; }

      const rowH = rowHeights[ri];
      const y = yStart + rowHeights.slice(0, ri).reduce((s, h) => s + h, 0) + rowGap * ri;
      let x = rect.x + padSide + edgePad;

      for (const it of row) {
        const itemW = it.natW + (it.isPlc ? perPlcExtra : 0);
        const itemY = y + (rowH - it.natH) / 2;
        if (it.isPlc) {
          plcCards.push(buildPlcCard(it, x, itemY, itemW, sz, childSz, innerGap, ipLineH, ipFontSz));
        } else {
          standalones.push({ x: x + (itemW - sz) / 2, y: itemY, size: sz, asset: it.asset });
        }
        x += itemW + innerGap + extraInter;
      }
    }

    return { standalones, plcCards };
  }

  // PLC 카드 1개의 기하 계산 (BlueprintView.BuildPlcCard 이식)
  function buildPlcCard(plan, x, y, cardW, plcSize, childSz, innerGap, ipLineH, ipFontSz) {
    const card = {
      x, y, cardW, plcSize, plc: plan.asset, ip: plan.asset.ip,
      vertical: plan.vertical, hasIp: plan.hasIp, groups: [],
    };
    const ipLen = plan.asset.ip ? plan.asset.ip.length : 0;

    if (plan.vertical) {
      const childRowH = plan.plcGroups.length > 0 ? childSz + 2 : 0;
      card.cardH = plcSize + (plan.hasIp ? ipLineH : 0) + childRowH + 4;
      let belowY = y + plcSize + 2;
      if (plan.hasIp) {
        const ipTextW = ipLen * ipFontSz * 0.62;
        card.ipBgX = x + 1; card.ipBgY = belowY - 1; card.ipBgW = cardW - 2; card.ipBgH = ipLineH;
        card.ipX = x + Math.max(2, (cardW - ipTextW) / 2); card.ipY = belowY + 7;
        card.divX1 = x + 3; card.divX2 = x + cardW - 3; card.divY = belowY + ipLineH - 1;
        belowY += ipLineH;
      }
      const childrenTotalW = plan.plcGroups.length * childSz + Math.max(0, plan.plcGroups.length - 1) * innerGap;
      let childX = x + Math.max(2, (cardW - childrenTotalW) / 2);
      for (const grp of plan.plcGroups) {
        if (childX + childSz > x + cardW - 1) break;
        card.groups.push({
          x: childX, y: belowY, size: childSz, rep: grp.items[0],
          count: grp.items.length, color: aggregateColor(grp.items), icon: grp.icon,
          ids: grp.items.map(it => it.assetId),
        });
        childX += childSz + innerGap;
      }
    } else {
      card.cardH = plcSize;
      const rightAreaX = x + plcSize + innerGap;
      const rightAreaW = Math.max(0, cardW - plcSize - innerGap - 3);
      if (plan.hasIp) {
        const ipTextW = ipLen * ipFontSz * 0.62;
        card.ipBgX = x + plcSize + 2; card.ipBgY = y + 1; card.ipBgW = cardW - plcSize - 4; card.ipBgH = ipLineH + 2;
        card.ipX = rightAreaX + Math.max(0, (rightAreaW - ipTextW) / 2); card.ipY = y + 10;
        card.divX1 = x + plcSize + 4; card.divX2 = x + cardW - 4; card.divY = y + ipLineH + 3;
      }
      const childrenTotalW = plan.plcGroups.length * childSz + Math.max(0, plan.plcGroups.length - 1) * innerGap;
      let childX = rightAreaX + Math.max(0, (rightAreaW - childrenTotalW) / 2);
      const childY = y + card.cardH - childSz - 3;
      const childMaxX = x + cardW - 3;
      for (const grp of plan.plcGroups) {
        if (childX + childSz > childMaxX) break;
        card.groups.push({
          x: childX, y: childY, size: childSz, rep: grp.items[0],
          count: grp.items.length, color: aggregateColor(grp.items), icon: grp.icon,
          ids: grp.items.map(it => it.assetId),
        });
        childX += childSz + innerGap;
      }
    }
    return card;
  }

  // 2단 자산 그룹 아이콘 <g> (아이콘 + 개수 배지).
  // 다수(>1) → 자산표 필터 이동(멤버 전체), 단일 → 그 자산 상세.
  function childGroupNode(grp) {
    const multi = grp.count > 1 && Array.isArray(grp.ids) && grp.ids.length > 1;
    const g = svgEl('g', {
      class: 'lv-asset-icon', style: 'cursor:pointer',
      'data-tip': groupTipHtml(grp),
      'data-asset-id': grp.rep.assetId,                       // 단일 묶음 클릭 시 상세 (위임)
      'data-asset-ids': multi ? grp.ids.join(',') : null,     // 다수 묶음 → 자산표 필터 이동
    });
    g.appendChild(svgEl('rect', {
      x: grp.x, y: grp.y, width: grp.size, height: grp.size, rx: 2,
      fill: iconBgFromHex(grp.color), stroke: grp.color, 'stroke-width': 1, opacity: 0.9,
    }));
    g.appendChild(svgEl('image', {
      href: '/' + String(grp.icon || 'images/icons/plc.png').replace(/^\//, ''),
      x: grp.x + 1, y: grp.y + 1, width: grp.size - 2, height: grp.size - 2,
    }));
    g.appendChild(svgEl('circle', {
      cx: grp.x + grp.size - 2, cy: grp.y + 2, r: 5, fill: '#555', stroke: 'white', 'stroke-width': 0.8,
    }));
    const bt = svgEl('text', {
      x: grp.x + grp.size - 2, y: grp.y + 4.5, 'text-anchor': 'middle',
      'font-size': 6, fill: 'white', 'font-weight': 'bold',
    });
    bt.textContent = grp.count;
    g.appendChild(bt);
    return g;
  }

  // PLC 카드 <g> (카드 배경 + PLC 아이콘 + IP 인셋 + 2단 그룹). 카드 클릭 → PLC 상세.
  function plcCardGroup(card) {
    const plc = card.plc;
    const g = svgEl('g', { style: 'cursor:pointer', 'data-asset-id': plc.assetId });
    g.appendChild(svgEl('rect', {
      x: card.x, y: card.y, width: card.cardW, height: card.cardH, rx: 5,
      fill: '#1e1e38', stroke: 'rgba(255,255,255,0.25)', 'stroke-width': 0.8,
    }));
    const plcIx = card.vertical ? card.x + (card.cardW - card.plcSize) / 2 : card.x + 2;
    const plcIy = card.vertical ? card.y + 2 : card.y + (card.cardH - card.plcSize) / 2;
    const ico = svgEl('g', { class: 'lv-asset-icon', 'data-tip': titleFor(plc, plc.floor) });
    ico.appendChild(svgEl('rect', {
      x: plcIx, y: plcIy, width: card.plcSize, height: card.plcSize, rx: 4,
      fill: plc.iconBgColor || '#e0e0e0', stroke: plc.healthColor || '#999', 'stroke-width': 0.7, opacity: 0.95,
    }));
    ico.appendChild(svgEl('image', {
      href: '/' + String(plc.icon || 'images/icons/plc.png').replace(/^\//, ''),
      x: plcIx + 2, y: plcIy + 2, width: card.plcSize - 4, height: card.plcSize - 4,
    }));
    // 카드의 자식 그룹 노드는 자체 data-asset-id 를 가지므로 위임 핸들러가 우선 처리.
    g.appendChild(ico);
    if (card.hasIp && card.ip) {
      g.appendChild(svgEl('rect', {
        x: card.ipBgX, y: card.ipBgY, width: card.ipBgW, height: card.ipBgH, rx: 2,
        fill: 'rgba(0,0,0,0.3)', stroke: 'rgba(0,0,0,0.2)', 'stroke-width': 0.5,
      }));
      const ipt = svgEl('text', {
        x: card.ipX, y: card.ipY, 'font-size': 6.5, fill: 'rgba(200,220,255,0.9)',
        'font-family': "'Consolas','Monaco',monospace", 'letter-spacing': 0.3,
      });
      ipt.textContent = card.ip;
      g.appendChild(ipt);
      if (card.groups.length > 0) {
        g.appendChild(svgEl('line', {
          x1: card.divX1, y1: card.divY, x2: card.divX2, y2: card.divY,
          stroke: 'rgba(255,255,255,0.15)', 'stroke-width': 0.5,
        }));
      }
    }
    for (const grp of card.groups) g.appendChild(childGroupNode(grp));
    return g;
  }

  // ── 인스턴스 팩토리 ────────────────────────────────────────────────────────
  function mount(opts) {
    opts = opts || {};
    const resolve = (v) => (typeof v === 'string' ? document.getElementById(v) : (v || null));
    const viewport = resolve(opts.viewport);
    const countEl = resolve(opts.count);
    const viewmodeEl = resolve(opts.viewmode);
    const tabsEl = resolve(opts.tabs);
    const fullscreenBtn = resolve(opts.fullscreenBtn);
    const splitBtn = resolve(opts.splitBtn);
    const splitKey = opts.splitStoreKey || null;
    if (!viewport) { console.warn('LayoutRenderer: viewport 요소 없음'); return null; }

    // 줌/팬 + 컨트롤(blueprint-zoom.js 필요) · 클릭 팝오버 — 둘 다 opt-in(/layout 만 사용, 대시보드 위젯은 미사용)
    const zoomEnabled = !!opts.zoom && !!window.blueprintZoom;
    const popoverEnabled = !!opts.popover;
    if ((zoomEnabled || popoverEnabled) && !viewport.id) viewport.id = 'lv-vp-' + (++_vpSeq);

    // forceMode(고정): 한 모드만 표시할 때. localStorage/토글 무시.
    const lockMode = (opts.forceMode === 0 || opts.forceMode === 1);
    // viewMode 저장 키 — 페이지별로 독립 기억(미지정 시 공용 키 사용).
    const storeKey = opts.storeKey || 'twms-layout-viewMode';

    const inst = {
      data: null,
      allLayouts: [],       // 전체 레이아웃 목록 [{id,name,sortOrder}]
      splitData: null,      // 분할 뷰용: 전체 레이아웃 데이터 배열(각 레이아웃 풀 응답)
      split: false,         // 분할(여러 도면 동시 표시) 여부
      selectedLayout: opts.layoutId || null,
      viewMode: lockMode ? opts.forceMode
        : (opts.defaultMode === 0 || opts.defaultMode === 1) ? opts.defaultMode : 0,
      hiddenFloors: null,   // 개별 뷰: 숨김 층 집합(기본 1층만 표시). null=미초기화
      clipNs: '0',          // SVG별 clipPath id 네임스페이스(분할 시 id 충돌 방지)
      pollTimer: null,
    };
    // viewMode localStorage 복원 (LayoutView OnInitializedAsync 이식) — 고정 모드면 생략
    if (!lockMode) {
      try { const m = parseInt(localStorage.getItem(storeKey), 10); if (m === 0 || m === 1) inst.viewMode = m; } catch (e) { /* ignore */ }
    }
    // 분할 on/off 복원
    if (splitKey) { try { inst.split = (localStorage.getItem(splitKey) === '1'); } catch (e) { /* ignore */ } }

    function assetById(id) { const m = inst.data && inst.data._assetMap; return m ? m.get(id) : null; }

    // ── 커스텀 HTML 툴팁 (자산 아이콘 hover, 마우스 추적) ───────────────────────
    // native SVG <title> 은 <g> 그룹에서 브라우저별로 안 뜨거나 한 줄로만 보여
    // 정보 툴팁을 viewport 안의 절대배치 div 로 직접 그린다. data-tip 텍스트 사용.
    let tipEl = null;
    function ensureTip() {
      if (tipEl && tipEl.parentNode === viewport) return tipEl;
      tipEl = document.createElement('div');
      tipEl.className = 'lv-tip';
      tipEl.style.display = 'none';
      viewport.appendChild(tipEl);
      return tipEl;
    }
    function moveTip(e) {
      if (!tipEl || tipEl.style.display === 'none') return;
      const r = viewport.getBoundingClientRect();
      let x = e.clientX - r.left + 14, y = e.clientY - r.top + 14;
      if (x + tipEl.offsetWidth > r.width) x = e.clientX - r.left - tipEl.offsetWidth - 14;
      if (y + tipEl.offsetHeight > r.height) y = e.clientY - r.top - tipEl.offsetHeight - 14;
      tipEl.style.left = Math.max(0, x) + 'px';
      tipEl.style.top = Math.max(0, y) + 'px';
    }
    function hideTip() { if (tipEl) tipEl.style.display = 'none'; }
    function attachTooltip(svg) {
      svg.addEventListener('mousemove', (e) => {
        const icon = e.target.closest && e.target.closest('.lv-asset-icon');
        const tip = icon && icon.getAttribute('data-tip');
        if (!tip) { hideTip(); return; }
        const t = ensureTip();
        if (t.style.display === 'none' || t._tip !== tip) {
          t.innerHTML = tip; t._tip = tip; t.style.display = 'block';
        }
        moveTip(e);
      });
      svg.addEventListener('mouseleave', hideTip);
    }

    // ── 클릭 팝오버 (자산 / 라인) — BlueprintView 의 클릭 다이얼로그 이식 ──────────
    let popEl = null;
    function ensurePop() {
      if (popEl && popEl.parentNode === viewport) return popEl;
      popEl = document.createElement('div');
      popEl.className = 'lv-pop';
      popEl.style.display = 'none';
      viewport.appendChild(popEl);
      return popEl;
    }
    function positionPop(evt) {
      const r = viewport.getBoundingClientRect();
      popEl.style.left = '0px'; popEl.style.top = '0px'; popEl.style.display = 'block';
      let x = (evt ? evt.clientX - r.left : 20) + 12;
      let y = (evt ? evt.clientY - r.top : 20) + 12;
      if (x + popEl.offsetWidth > r.width) x = Math.max(4, r.width - popEl.offsetWidth - 8);
      if (y + popEl.offsetHeight > r.height) y = Math.max(4, r.height - popEl.offsetHeight - 8);
      popEl.style.left = Math.max(4, x) + 'px';
      popEl.style.top = Math.max(4, y) + 'px';
    }
    function closePop() { if (popEl) popEl.style.display = 'none'; }

    function openAssetPopover(id, evt) {
      const a = assetById(id);
      if (!a) { location.href = `/assets/${id}`; return; }
      const p = ensurePop();
      p.innerHTML = `<button class="lv-pop-close" title="닫기">&times;</button>`
        + titleFor(a, a.floor)
        + `<a class="lv-pop-link" href="/assets/${a.assetId}"><span class="material-symbols-outlined">open_in_new</span>상세 보기</a>`;
      p.querySelector('.lv-pop-close').addEventListener('click', (e) => { e.stopPropagation(); closePop(); });
      positionPop(evt);
    }

    function openLinePopover(lineId, evt) {
      const line = (inst.data.lines || []).find(l => l.id === lineId);
      const assets = (inst.data.assets || []).filter(a => a.lineId === lineId)
        .slice().sort((x, y) => String(x.name || '').localeCompare(String(y.name || '')));
      const rows = assets.map(a => {
        const hc = colorFor(a);
        return `<a class="lv-pop-row" href="/assets/${a.assetId}">`
          + `<span class="lv-pop-dot" style="background:${hc}"></span>`
          + `<span class="lv-pop-row-name">${escapeHtml(a.name || '')}</span>`
          + `<span class="lv-pop-row-ip">${escapeHtml(a.ip || '')}</span></a>`;
      }).join('');
      const p = ensurePop();
      p.innerHTML = `<button class="lv-pop-close" title="닫기">&times;</button>`
        + `<div class="lv-tip-head"><span class="lv-tip-name">${escapeHtml(line ? line.name : '라인')}</span>`
        + `<span class="lv-pop-count">${assets.length}개</span></div>`
        + `<div class="lv-pop-list">${rows || '<div class="lv-pop-empty">자산 없음</div>'}</div>`;
      p.querySelector('.lv-pop-close').addEventListener('click', (e) => { e.stopPropagation(); closePop(); });
      positionPop(evt);
    }

    // 위임 클릭: 다수묶음(data-asset-ids) → 자산표 필터 이동, 자산(data-asset-id) → 팝오버/네비,
    // 라인영역(data-line-id, 라인별 뷰) → 라인 팝오버.
    // blueprintZoom 의 capture 핸들러가 드래그-클릭을 먼저 억제하므로, 여기 도달하는 건 순수 클릭뿐.
    function attachClicks(svg) {
      svg.addEventListener('click', (e) => {
        const usePop = popoverEnabled && !inst.split;
        // 여러 자산 묶음은 대표 1개로 점프하지 않고, 그 멤버만 필터된 자산 목록으로 이동.
        const groupEl = e.target.closest('[data-asset-ids]');
        if (groupEl) {
          e.stopPropagation();
          location.href = `/assets?ids=${encodeURIComponent(groupEl.getAttribute('data-asset-ids'))}`;
          return;
        }
        const assetEl = e.target.closest('[data-asset-id]');
        if (assetEl) {
          const id = parseInt(assetEl.getAttribute('data-asset-id'), 10);
          if (usePop) { e.stopPropagation(); openAssetPopover(id, e); }
          else location.href = `/assets/${id}`;
          return;
        }
        if (usePop && inst.viewMode === 0) {
          const lineEl = e.target.closest('[data-line-id]');
          if (lineEl) { e.stopPropagation(); openLinePopover(parseInt(lineEl.getAttribute('data-line-id'), 10), e); }
        }
      });
    }

    // 팝오버 바깥 클릭 시 닫기 (자산/라인 클릭은 stopPropagation 으로 여기 도달 안 함)
    const _onDocClick = (e) => {
      if (!popEl || popEl.style.display === 'none') return;
      if (e.target.closest && e.target.closest('.lv-pop')) return;
      closePop();
    };
    if (popoverEnabled) document.addEventListener('click', _onDocClick);

    // ── 줌/전체화면 컨트롤 오버레이 (뷰포트 내부 → 전체화면에서도 노출, 재렌더마다 재생성) ──
    function toggleFs() {
      if (document.fullscreenElement === viewport) { if (document.exitFullscreen) document.exitFullscreen(); }
      else if (viewport.requestFullscreen) viewport.requestFullscreen();
    }
    function buildControls() {
      const c = document.createElement('div');
      c.className = 'lv-controls';
      c.innerHTML =
        `<button class="lv-ctrl-btn" data-z="in" title="확대"><span class="material-symbols-outlined">add</span></button>`
        + `<button class="lv-ctrl-btn" data-z="out" title="축소"><span class="material-symbols-outlined">remove</span></button>`
        + `<button class="lv-ctrl-btn" data-z="reset" title="원래대로"><span class="material-symbols-outlined">fit_screen</span></button>`
        + `<button class="lv-ctrl-btn" data-z="fs" title="전체화면"><span class="material-symbols-outlined">fullscreen</span></button>`;
      c.querySelector('[data-z="in"]').addEventListener('click', () => window.blueprintZoom.zoomIn(viewport.id));
      c.querySelector('[data-z="out"]').addEventListener('click', () => window.blueprintZoom.zoomOut(viewport.id));
      c.querySelector('[data-z="reset"]').addEventListener('click', () => window.blueprintZoom.reset(viewport.id));
      c.querySelector('[data-z="fs"]').addEventListener('click', toggleFs);
      return c;
    }

    // ── 라인별(도면) 뷰 SVG ──
    function renderLineView(svg) {
      const lineMap = new Map(inst.data.lines.map(l => [l.id, l]));
      const hier = buildPlcHierarchy(inst.data.assets);

      for (const rect of inst.data.blueprintRects) {
        const line = lineMap.get(rect.lineId);
        if (!line) continue;
        const lineAssets = inst.data.assets.filter(a => a.lineId === rect.lineId);
        const topLevel = lineAssets.filter(a => !hier.childIds.has(a.assetId));
        const healthColor = aggregateColor(lineAssets);

        const g = svgEl('g', { 'data-line-id': rect.lineId, style: 'cursor:pointer' });
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
        const clipId = `lv-line-clip-${inst.clipNs}-${rect.lineId}`;
        const clip = svgEl('clipPath', { id: clipId });
        clip.appendChild(svgEl('rect', { x: rect.x, y: rect.y, width: rect.width, height: rect.height, rx: 6 }));
        g.appendChild(clip);

        const inner = svgEl('g', { 'clip-path': `url(#${clipId})` });
        // PLC 카드(2단 경유 자식 포함) + standalone 아이콘 배치 (BlueprintView 이식)
        const { standalones, plcCards } = layoutLineAssets(rect, topLevel, hier.plcChildren);
        for (const card of plcCards) inner.appendChild(plcCardGroup(card));
        for (const st of standalones) inner.appendChild(assetMarker(st.asset, st.x, st.y, st.size, st.asset.floor));
        g.appendChild(inner);
        svg.appendChild(g);
      }
    }

    // ── 개별(배치도) 뷰 SVG ──
    function renderPlacementView(svg) {
      const groupedIds = new Set(inst.data.groupMembers.map(m => m.assetId));
      const membersByGroup = new Map();
      for (const m of inst.data.groupMembers) {
        if (!membersByGroup.has(m.groupId)) membersByGroup.set(m.groupId, []);
        membersByGroup.get(m.groupId).push(m.assetId);
      }

      // 그룹 박스 + 멤버 (층 필터 적용)
      for (const grp of inst.data.groups) {
        const memberIds = membersByGroup.get(grp.id) || [];
        let members = memberIds.map(id => assetById(id)).filter(Boolean)
          .filter(a => !(inst.hiddenFloors && a.floor != null && inst.hiddenFloors.has(a.floor)));
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
        const clipId = `lv-grp-clip-${inst.clipNs}-${grp.id}`;
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
      for (const pos of inst.data.positions) {
        if (groupedIds.has(pos.assetId)) continue;
        const a = assetById(pos.assetId);
        if (!a) continue;
        const s = pos.scale || 1;
        const sz = 36 * s;
        svg.appendChild(assetMarker(a, pos.x - sz / 2, pos.y - sz / 2, sz, a.floor));
      }
    }

    function initFloorFilter() {
      // AssetPlacementView 기본값: 1층만 표시, 나머지 숨김 (그룹 floor 기준)
      if (inst.hiddenFloors != null) return;
      const floors = [...new Set(inst.data.groups.map(g => g.floor).filter(f => f != null))];
      inst.hiddenFloors = new Set(floors.filter(f => f !== 1));
    }

    function renderTabs() {
      if (!tabsEl) return;
      // 탭은 단일 모드 + 도면 2개 이상일 때만. 분할 모드에선 전부 보이므로 숨김.
      if (inst.allLayouts.length <= 1 || inst.split) { tabsEl.style.display = 'none'; tabsEl.innerHTML = ''; return; }
      tabsEl.style.display = 'flex';
      tabsEl.innerHTML = inst.allLayouts.map(l =>
        `<button class="lv-tab${l.id === inst.selectedLayout ? ' active' : ''}" data-layout="${l.id}">${escapeHtml(l.name)}</button>`
      ).join('');
      tabsEl.querySelectorAll('.lv-tab').forEach(btn => {
        btn.addEventListener('click', () => {
          const id = parseInt(btn.dataset.layout, 10);
          if (id !== inst.selectedLayout) { inst.selectedLayout = id; inst.hiddenFloors = null; inst._resetZoom = true; refresh(); }
        });
      });
    }

    // 도면 컨트롤 동기화: 분할 버튼(2개 이상에서만 노출/활성) + 레이아웃 탭.
    function renderControls() {
      const multi = inst.allLayouts.length > 1;
      if (splitBtn) {
        splitBtn.style.display = multi ? '' : 'none';
        splitBtn.classList.toggle('active', !!inst.split && multi);
      }
      renderTabs();
    }

    // 현재 viewMode 기준 콘텐츠 유무 (라인별=영역, 개별=위치/그룹)
    function hasContentFor(data) {
      return inst.viewMode === 0
        ? data.blueprintRects.length > 0
        : (data.positions.length > 0 || data.groups.length > 0);
    }

    // 도면 1개 → SVG 엘리먼트. 도면 이미지 배경 + 라인별/개별 렌더(현재 viewMode).
    function buildSvg(data) {
      const svg = svgEl('svg', { viewBox: `0 0 ${VB_W} ${VB_H}`, preserveAspectRatio: 'xMidYMid meet' });
      svg.style.backgroundColor = (data.config && data.config.bgColor) || '#1a1a2e';
      if (data.config && data.config.imagePath) {
        const ir = calcImageRect(data.config);
        svg.appendChild(svgEl('image', {
          href: '/' + String(data.config.imagePath).replace(/^\//, ''),
          x: ir.x, y: ir.y, width: ir.w, height: ir.h,
          preserveAspectRatio: 'none', opacity: 0.85,
        }));
      }
      if (inst.viewMode === 0) renderLineView(svg);
      else renderPlacementView(svg);
      return svg;
    }

    // 분할 셀용: inst.data/hiddenFloors/clipNs 를 잠시 교체해 기존 렌더 함수 재사용(동기 처리라 안전).
    function buildSvgFor(data, ns) {
      const prevData = inst.data, prevHidden = inst.hiddenFloors, prevNs = inst.clipNs;
      inst.data = data; inst.hiddenFloors = null; inst.clipNs = ns;
      initFloorFilter();
      const svg = buildSvg(data);
      inst.data = prevData; inst.hiddenFloors = prevHidden; inst.clipNs = prevNs;
      return svg;
    }

    // 분할/단일 클래스 토글 (뷰포트 + 래퍼). 분할이면 뷰포트가 그리드 컨테이너가 된다.
    function applySplit(on) {
      viewport.classList.toggle('lv-split', on);
      const host = viewport.parentElement;
      if (host) host.classList.toggle('lv-split-host', on);
    }

    function render() {
      if (!inst.data) { viewport.innerHTML = '<div class="lv-loading">불러오는 중…</div>'; return; }
      const multi = inst.allLayouts.length > 1;
      const doSplit = !!inst.split && multi && inst.splitData && inst.splitData.length > 1;
      applySplit(doSplit);
      if (doSplit) renderSplit(); else renderSingle();
    }

    // 단일 뷰(기존 동작): 선택된 1개 레이아웃을 뷰포트 전체에 렌더.
    function renderSingle() {
      const data = inst.data;
      if (data.layouts.length === 0 || !hasContentFor(data)) {
        viewport.innerHTML = '<div class="lv-empty">배치된 자산이 없습니다. 관리 &gt; 레이아웃 관리에서 자산을 배치해 주세요.</div>';
        if (countEl) countEl.textContent = '';
        return;
      }
      initFloorFilter();
      inst.clipNs = '0';
      // 줌 viewBox 보존: 폴링 재렌더 시 줌/팬 유지(모드·레이아웃 변경 시엔 _resetZoom 로 초기화).
      let savedVb = null;
      if (zoomEnabled && !inst._resetZoom) {
        const vb = window.blueprintZoom.getViewBox(viewport.id);
        if (vb && vb.w < VB_W - 0.5) savedVb = vb;
      }
      inst._resetZoom = false;
      const svg = buildSvg(data);
      viewport.innerHTML = '';
      tipEl = null; popEl = null;   // viewport 비움 → 툴팁/팝오버 재생성 필요
      viewport.appendChild(svg);
      attachTooltip(svg);
      attachClicks(svg);
      if (zoomEnabled) {
        window.blueprintZoom.init(viewport.id, { clampMargin: 0.3 });
        if (savedVb) window.blueprintZoom.setViewBox(viewport.id, savedVb);
        viewport.appendChild(buildControls());
        onFsChange(); // 전체화면 아이콘 동기화
      }
      const shown = svg.querySelectorAll('.lv-asset-icon').length;
      if (countEl) countEl.textContent = `${shown}개 표시 · 자산/라인 클릭 시 상세 보기`;
    }

    // 분할 뷰: 모든 레이아웃을 그리드 셀(미니 화면)로 나란히 렌더.
    function renderSplit() {
      viewport.innerHTML = '';
      tipEl = null;
      let total = 0;
      inst.splitData.forEach((data, i) => {
        const layout = inst.allLayouts.find(l => l.id === data.selectedLayoutId) || {};
        const cell = document.createElement('div');
        cell.className = 'lv-cell';
        const title = document.createElement('div');
        title.className = 'lv-cell-title';
        const body = document.createElement('div');
        body.className = 'lv-cell-body';
        if (!hasContentFor(data)) {
          title.textContent = layout.name || '도면';
          body.innerHTML = '<div class="lv-empty" style="padding:14px;font-size:13px;">배치된 자산이 없습니다.</div>';
        } else {
          const svg = buildSvgFor(data, String(i));
          body.appendChild(svg);
          attachTooltip(svg);
          attachClicks(svg); // 분할 셀: 팝오버 없이 네비게이션
          const shown = svg.querySelectorAll('.lv-asset-icon').length;
          total += shown;
          title.textContent = `${layout.name || '도면'} · ${shown}개`;
        }
        // 셀 우측 상단: 이 도면만(단일) 보기 — 분할 해제 + 해당 레이아웃 선택
        const only = document.createElement('button');
        only.className = 'lv-cell-only';
        only.title = '이 도면만 보기';
        only.innerHTML = '<span class="material-symbols-outlined">open_in_full</span>';
        only.addEventListener('click', (e) => { e.stopPropagation(); setSplit(false, data.selectedLayoutId); });
        cell.appendChild(title);
        cell.appendChild(only);
        cell.appendChild(body);
        viewport.appendChild(cell);
      });
      if (countEl) countEl.textContent = `${inst.splitData.length}개 도면 · 자산 ${total}개`;
    }

    function setMode(m) {
      if (lockMode) return; // 고정 모드에서는 토글 무시
      inst.viewMode = m;
      inst._resetZoom = true; // 모드 변경 → 줌 초기화
      try { localStorage.setItem(storeKey, String(m)); } catch (e) { /* ignore */ }
      if (viewmodeEl) viewmodeEl.querySelectorAll('[data-view]').forEach(b =>
        b.classList.toggle('active', parseInt(b.dataset.view, 10) === m));
      render();
    }

    function setLayout(id) { inst.selectedLayout = id; inst.hiddenFloors = null; inst._resetZoom = true; refresh(); }

    // 분할 on/off 전환(옵션: 단일로 전환 시 특정 레이아웃 선택). localStorage 기억.
    function setSplit(on, selectId) {
      inst.split = !!on;
      inst._resetZoom = true;
      if (typeof selectId === 'number') { inst.selectedLayout = selectId; inst.hiddenFloors = null; }
      if (splitKey) { try { localStorage.setItem(splitKey, inst.split ? '1' : '0'); } catch (e) { /* ignore */ } }
      renderControls();
      refresh();
    }

    // ── 데이터 로드 ──
    async function fetchLayout(id) {
      const url = id ? `/api/layout?layoutId=${id}` : '/api/layout';
      const res = await fetch(url, { headers: { 'Accept': 'application/json' } });
      if (!res.ok) throw new Error('layout fetch ' + res.status);
      const d = await res.json();
      d._assetMap = new Map(d.assets.map(a => [a.assetId, a]));
      return d;
    }

    async function refresh() {
      try {
        const main = await fetchLayout(inst.selectedLayout);
        inst.data = main;
        inst.allLayouts = main.layouts || [];
        inst.selectedLayout = main.selectedLayoutId || null;
        // 분할 + 도면 2개 이상이면 전체 도면 데이터 로드(선택분은 재사용). 한 도면 실패는 건너뜀.
        if (inst.split && inst.allLayouts.length > 1) {
          const all = await Promise.all(inst.allLayouts.map(l =>
            l.id === inst.selectedLayout ? Promise.resolve(main) : fetchLayout(l.id).catch(() => null)
          ));
          inst.splitData = all.filter(Boolean);
        } else {
          inst.splitData = null;
        }
        renderControls();
        render();
      } catch (e) { /* 무시 */ }
    }

    // 뷰모드 토글 버튼 바인딩 + 활성 동기화
    if (viewmodeEl) viewmodeEl.querySelectorAll('[data-view]').forEach(b => {
      b.classList.toggle('active', parseInt(b.dataset.view, 10) === inst.viewMode);
      b.addEventListener('click', () => setMode(parseInt(b.dataset.view, 10)));
    });

    // 분할(도면 여러 개 동시) 토글 버튼
    if (splitBtn) splitBtn.addEventListener('click', () => setSplit(!inst.split));

    // 전체화면 아이콘 동기화 — 외부 버튼(opts.fullscreenBtn) + 오버레이 버튼(.lv-controls) 둘 다.
    function onFsChange() {
      const inFs = document.fullscreenElement === viewport;
      const setIco = (btn) => { const i = btn && btn.querySelector('.material-symbols-outlined'); if (i) i.textContent = inFs ? 'fullscreen_exit' : 'fullscreen'; };
      setIco(fullscreenBtn);
      setIco(viewport.querySelector('.lv-ctrl-btn[data-z="fs"]'));
    }
    if (fullscreenBtn) {
      fullscreenBtn.addEventListener('click', () => {
        if (document.fullscreenElement === viewport) { if (document.exitFullscreen) document.exitFullscreen(); }
        else if (viewport.requestFullscreen) { viewport.requestFullscreen(); }
      });
    }
    if (fullscreenBtn || zoomEnabled) document.addEventListener('fullscreenchange', onFsChange);

    function onVis() { if (!document.hidden) refresh(); }

    // 초기 로드 + 폴링
    refresh();
    if (opts.poll) {
      inst.pollTimer = setInterval(refresh, opts.poll);
      document.addEventListener('visibilitychange', onVis);
    }

    function destroy() {
      if (inst.pollTimer) clearInterval(inst.pollTimer);
      document.removeEventListener('visibilitychange', onVis);
      document.removeEventListener('fullscreenchange', onFsChange);
      if (popoverEnabled) document.removeEventListener('click', _onDocClick);
      if (zoomEnabled && viewport.id && window.blueprintZoom) window.blueprintZoom.dispose(viewport.id);
    }

    function getLayoutId() { return inst.selectedLayout; }

    return { refresh, setMode, setLayout, destroy, getLayoutId };
  }

  window.LayoutRenderer = { mount };
})();
