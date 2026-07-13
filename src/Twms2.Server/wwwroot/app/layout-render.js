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
  // 카드 공통 패딩 + PLC 이름/IP 텍스트 메트릭 (buildCardModel · plcCardGroup 공유)
  const CARD_PAD = 3;
  const NAME_FZ = 7.5, NAME_LH = 9.5, IP_FZ = 6, IP_LH = 8, NAME_MAX = 14;
  const OFFLINE_COLOR = '#8b93b0'; // ping 불가(오프라인) 축 — health 색과 분리
  const LINE_BG_DEFAULT = '#1f2937a6'; // 라인 영역 카드 기본 배경 = 차콜 65% (도면 설정 '라인 영역 색' 미설정 시)

  // ── 무상태 유틸 ───────────────────────────────────────────────────────────
  function svgEl(name, attrs) {
    const el = document.createElementNS(SVGNS, name);
    if (attrs) for (const k in attrs) { if (attrs[k] != null) el.setAttribute(k, attrs[k]); }
    return el;
  }
  function colorFor(asset) { return HEALTH_COLOR[asset.health] || HEALTH_COLOR.unknown; }
  // '#rrggbb' | '#rrggbbaa' → { hex6, r, g, b, a(0~1) }. 그 외 형식은 null.
  function parseHexA(s) {
    const m = /^#?([0-9a-f]{6})([0-9a-f]{2})?$/i.exec(String(s || ''));
    if (!m) return null;
    const v = parseInt(m[1], 16);
    return { hex6: '#' + m[1], r: (v >> 16) & 255, g: (v >> 8) & 255, b: v & 255, a: m[2] ? parseInt(m[2], 16) / 255 : 1 };
  }
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

  // 오프라인(ping 불가) 코너 마크 — 상태색(테두리)과 직교하는 별도 축. 대상 <g> 좌상단에 붙인다.
  function offlineMark(g, x, y, size) {
    const r = Math.min(Math.max(1.4, size * 0.28), 4);   // 작은 아이콘에서 본체를 덮지 않게 비례 보존
    const off = size < 8 ? r * 0.45 : r * 0.75;          // 작을수록 모서리에 더 붙임
    const mx = x + off, my = y + off;
    g.appendChild(svgEl('circle', { cx: mx, cy: my, r, fill: OFFLINE_COLOR, stroke: 'white', 'stroke-width': size < 8 ? 0.4 : 0.6 }));
    if (size >= 8) { // 작은 아이콘에서는 슬래시 생략(점만으로 표시 — 식별 방해 최소화)
      const d = r * 0.5;
      g.appendChild(svgEl('line', { x1: mx - d, y1: my - d, x2: mx + d, y2: my + d, stroke: 'white', 'stroke-width': 0.9, 'stroke-linecap': 'round' }));
    }
  }
  // 글리프 폭 추정(한글≈전각, 라틴≈0.58em) — content-driven 카드 폭 계산용
  function textWidth(s, fz) {
    let w = 0; for (const ch of String(s == null ? '' : s)) w += (ch.charCodeAt(0) > 127 ? fz * 1.02 : fz * 0.58);
    return w;
  }
  function truncName(s) {
    const a = [...String(s == null ? '' : s)];
    return a.length > NAME_MAX ? a.slice(0, NAME_MAX - 1).join('') + '…' : a.join('');
  }

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
  // neutral: 라인 뷰용 중립 배경(경보만 채움색) — 개별(배치도) 뷰는 기존 상태색 배경 유지.
  function assetMarker(asset, x, y, size, floor, neutral) {
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
      fill: neutral ? neutralIconBg(asset.health) : (asset.iconBgColor || '#e0e0e0'), stroke: colorFor(asset),
      'stroke-width': Math.max(0.5, size * 0.05),
    }));
    g.appendChild(svgEl('image', {
      href: '/' + String(asset.icon || 'images/icons/plc.png').replace(/^\//, ''),
      x: size * 0.08, y: size * 0.08, width: size * 0.84, height: size * 0.84,
    }));
    if (asset.pingReachable === false) offlineMark(g, 0, 0, size);
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

  // 라인 뷰 아이콘 배경 — 면은 중립(무채색), 경보(실패/작업중)만 채움색으로 튀게.
  // 정상/내역없음의 상태는 테두리색(colorFor)이 전담한다.
  function neutralIconBg(health) {
    return health === 'failed' ? '#f5d0d0' : health === 'inprogress' ? '#fde8c4' : '#e6e9f2';
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

  function aggregateColor(assets) {
    if (assets.length === 0) return '#999';
    if (assets.some(a => a.health === 'failed')) return '#E67E7E';
    if (assets.some(a => a.health === 'inprogress')) return '#f59e0b';
    if (assets.some(a => a.health === 'backedup')) return '#65B991';
    if (assets.some(a => a.health === 'unchanged')) return '#6BA0DE';
    return '#999';
  }

  // ── 라인 영역 내 1단 자산 배치 ───────────────────────────────────────────────
  // PLC(2단 경유 자식 보유)는 "상태요약 카드"(buildCardModel)로, 나머지는 standalone 아이콘으로.
  function layoutLineAssets(rect, topLevel, plcChildren) {
    const standalones = [], plcCards = [];
    if (topLevel.length === 0) return { standalones, plcCards };

    const padSide = 4.0, padBottom = 4.0, headerH = 20.0;
    const innerGap = 5.0, rowGap = 5.0;

    const availW = rect.width - padSide * 2;
    const availH = rect.height - headerH - padBottom;
    if (availW < 4 || availH < 4) return { standalones, plcCards };

    // PLC 카드 헤더 아이콘 + standalone 아이콘 공통 기준 크기 (영역 비율 기반)
    const aspectRatio = availW / Math.max(1, availH);
    const bestCols = Math.max(1, Math.round(Math.sqrt(topLevel.length * aspectRatio)));
    const initCols = Math.min(bestCols, topLevel.length);
    const initRows = Math.ceil(topLevel.length / initCols);
    const cellH = (availH - rowGap * Math.max(0, initRows - 1)) / Math.max(1, initRows);
    let sz = Math.max(7, Math.min(cellH, 30));
    const plcTotal = topLevel.filter(a => plcChildren.has(a.assetId)).length;
    // 세로형 카드(헤더+상태바+본문)는 높이를 많이 쓰므로, 카드가 섞인 좁고 높은 영역에선 아이콘을 줄인다.
    if (plcTotal > 0 && availH >= availW) sz = Math.max(7, Math.min(sz, cellH * 0.62));

    // 1) 자연 크기 계산 — PLC 는 origin(0,0) 기준 카드 모델로 측정(자식 수에 따라 모드 결정).
    const plans = [];
    for (const asset of topLevel) {
      const children = plcChildren.get(asset.assetId);
      if (children && children.length) {
        const model = buildCardModel(asset, children, sz, availW);
        plans.push({ asset, isPlc: true, model, natW: model.cardW, natH: model.cardH });
      } else {
        plans.push({ asset, isPlc: false, natW: sz, natH: sz });
      }
    }

    // 2) 그룹(PLC 카드)과 개별(standalone)을 분리 배치 — 카드 행(들) 위, 개별 아이콘 그리드 아래.
    //    카드끼리는 영역 종횡비에 맞춰 최적 열 수(cols)를 탐색(행으로 끊어 fit 최대화 + 종횡비 동률 판정),
    //    개별 아이콘은 카드 블록 폭(카드가 없으면 영역 종횡비)에 맞춘 그리드로 그 아래에 깐다.
    const margin = 2.5, MAX_FIT = 2.0;
    const regionAspect = availW / availH;
    const cardPlans = plans.filter(p => p.isPlc);
    const stdPlans = plans.filter(p => !p.isPlc);

    let best = { rows: [], rh: [], rw: [], pW: 0, pH: 0 };
    if (cardPlans.length) {
      let bestScore = null;
      for (let cols = 1; cols <= cardPlans.length; cols++) {
        const rows = [];
        for (let i = 0; i < cardPlans.length; i += cols) rows.push(cardPlans.slice(i, i + cols));
        const rh = rows.map(r => Math.max(...r.map(it => it.natH)));
        const rw = rows.map(r => r.reduce((s, it) => s + it.natW, 0) + innerGap * (r.length - 1));
        const pW = Math.max(...rw);
        const pH = rh.reduce((s, h) => s + h, 0) + rowGap * (rows.length - 1);
        const fit = Math.min(MAX_FIT, availW / (pW + margin), availH / (pH + margin));
        const aspectMatch = -Math.abs(Math.log(pW / pH) - Math.log(regionAspect)); // 0 에 가까울수록 영역 종횡비와 일치
        if (!bestScore || fit > bestScore.fit + 1e-6 ||
            (Math.abs(fit - bestScore.fit) <= 1e-6 && aspectMatch > bestScore.aspectMatch + 1e-9)) {
          bestScore = { rows, rh, rw, pW, pH, fit, aspectMatch };
        }
      }
      best = bestScore;
    }

    // 개별 아이콘 그리드 열 수: 카드 블록(위 고정) 아래에 붙였을 때 최종 블록 fit 이
    // 최대가 되는 열 수를 탐색 — 넓은 영역은 넓게, 좁은 영역은 여러 행으로 자연 선택.
    const stdRows = [];
    if (stdPlans.length) {
      let bestStd = null;
      for (let c = 1; c <= stdPlans.length; c++) {
        const rows = Math.ceil(stdPlans.length / c);
        const w = c * sz + (c - 1) * innerGap;
        const h = rows * sz + (rows - 1) * innerGap;
        const bw = Math.max(best.pW, w);
        const bh = best.pH + (best.pH > 0 ? rowGap + 1.5 : 0) + h;
        const fit = Math.min(MAX_FIT, availW / (bw + margin), availH / (bh + margin));
        const aspectMatch = -Math.abs(Math.log(bw / bh) - Math.log(regionAspect));
        if (!bestStd || fit > bestStd.fit + 1e-6 ||
            (Math.abs(fit - bestStd.fit) <= 1e-6 && aspectMatch > bestStd.aspectMatch + 1e-9)) {
          bestStd = { c, fit, aspectMatch };
        }
      }
      for (let i = 0; i < stdPlans.length; i += bestStd.c) stdRows.push(stdPlans.slice(i, i + bestStd.c));
    }
    const stdRowW = stdRows.map(r => r.length * sz + (r.length - 1) * innerGap);
    const stdW = stdRowW.length ? Math.max(...stdRowW) : 0;
    const stdH = stdRows.length ? stdRows.length * sz + (stdRows.length - 1) * innerGap : 0;
    const blockW = Math.max(best.pW, stdW);
    const sectionGap = best.pH > 0 && stdH > 0 ? rowGap + 1.5 : 0; // 카드↔개별 사이 살짝 넓은 간격
    const blockH = best.pH + sectionGap + stdH;

    // 3) packed-local 좌표(원점 0,0)로 배치 — 각 행을 블록 폭 기준 가운데 정렬.
    //    같은 행 카드는 박스 높이를 행 최대 높이(rowH)로 통일 + 상단 정렬 → 위아래/헤더 라인이 가지런해진다.
    let py = 0;
    for (let ri = 0; ri < best.rows.length; ri++) {
      const row = best.rows[ri], rowH = best.rh[ri];
      let px = (blockW - best.rw[ri]) / 2;
      for (const it of row) {
        const card = it.model; card.x = px; card.y = py; card.renderH = rowH;
        plcCards.push(card);
        px += it.natW + innerGap;
      }
      py += rowH + rowGap;
    }
    py += sectionGap ? sectionGap - rowGap : 0; // 카드 마지막 행 뒤 rowGap 은 이미 더해짐
    for (const row of stdRows) {
      const rw = row.length * sz + (row.length - 1) * innerGap;
      let px = (blockW - rw) / 2;
      for (const it of row) {
        standalones.push({ x: px, y: py, size: sz, asset: it.asset });
        px += sz + innerGap;
      }
      py += sz + innerGap;
    }

    // 4) 블록 전체를 콘텐츠 영역(availW×availH)에 한 번에 배치(가운데 + 단일 fit).
    //    renderLineView 가 이 transform 을 clip 되는 scaler <g> 에 적용한다.
    const fit = Math.min(MAX_FIT, availW / (blockW + margin), availH / (blockH + margin));
    const offsetX = rect.x + padSide + (availW - (blockW + margin) * fit) / 2;
    const offsetY = rect.y + headerH + (availH - (blockH + margin) * fit) / 2;
    const transform = `translate(${offsetX} ${offsetY}) scale(${fit})`;

    return { standalones, plcCards, transform };
  }

  // ── 라인 카드(상태요약형 + 하이브리드) ──────────────────────────────────────
  //  [헤더]  PLC 아이콘(자체 상태색) · 이름(hero) · IP(보조) · "N대"+집계 점
  //  [상태바] 자식 health 비율 누적 막대 → 라인 전반 상태를 한눈에
  //  [본문]  자식 수에 따라 형태 전환(하이브리드):
  //          n≤6   행 리스트(상태점·이름·IP)         — 개별 정보 최대 노출
  //          n≤16  상태색 아이콘 타일 격자            — 종류·상태 한눈에
  //          그 외 점 격자(자식 1개=점) + 비정상 카운트 — 고밀도 요약
  //  자식 행/타일/점은 각자 hover 툴팁 + 클릭(상세)을 가져, 개별 이름은 hover/클릭으로 확인.
  const CHILD_LIST_MAX = 6, CHILD_TILE_MAX = 16;
  const HDR_GAP = 4, BAR_H = 4.5, BAR_GAP = 3, SEC_GAP = 4;
  const ROW_H = 9.5, ROW_DOT_R = 2.0, ROW_NAME_FZ = 6.6, ROW_IP_FZ = 6;
  const COUNT_FZ = 6, LEG_FZ = 6, LEG_DOT_R = 1.7;
  const CARD_BG = '#262643';           // PLC 카드 본체 — 라인 카드(차콜)보다 한 단계 밝은 다크 서피스
  const HEALTH_BAR_ORDER = ['backedup', 'unchanged', 'unknown', 'inprogress', 'failed'];

  function iconHref(icon) { return '/' + String(icon || 'images/icons/plc.png').replace(/^\//, ''); }

  // 자식 자산 health/온오프 집계. counts=상태별 수, offline=ping 불가 수, total=전체.
  function healthBreakdown(children) {
    const counts = { backedup: 0, unchanged: 0, inprogress: 0, failed: 0, unknown: 0 };
    let offline = 0;
    for (const a of children) {
      counts[HEALTH_COLOR[a.health] ? a.health : 'unknown']++;
      if (a.pingReachable === false) offline++;
    }
    return { counts, offline, total: children.length };
  }

  // 본문 하단 카운트 범례 항목 — 비정상(실패/작업중/오프)을 앞에, 정상은 합산해 뒤에.
  function buildLegend(bd) {
    const out = [];
    if (bd.counts.failed > 0) out.push({ c: HEALTH_COLOR.failed, t: `실패 ${bd.counts.failed}` });
    if (bd.counts.inprogress > 0) out.push({ c: HEALTH_COLOR.inprogress, t: `작업 ${bd.counts.inprogress}` });
    if (bd.offline > 0) out.push({ c: OFFLINE_COLOR, t: `오프 ${bd.offline}` });
    if (bd.counts.unknown > 0) out.push({ c: HEALTH_COLOR.unknown, t: `내역없음 ${bd.counts.unknown}` });
    const normal = bd.counts.backedup + bd.counts.unchanged;
    if (normal > 0) out.push({ c: HEALTH_COLOR.backedup, t: `정상 ${normal}` });
    return out.length ? out : null;
  }

  // 카드 1개의 기하 + 렌더 파라미터(origin 0,0 기준). 자식 수로 본문 모드를 정한다.
  // 반환 객체의 x/y(packed-local 위치)는 layoutLineAssets 가 배치 시 채운다. 축소는 영역 scaler 가 일괄.
  function buildCardModel(plc, children, plcSize, maxW) {
    const PAD = CARD_PAD;
    const n = children.length;
    const bd = healthBreakdown(children);
    const members = [plc]; for (const c of children) members.push(c);
    const aggColor = aggregateColor(members);
    const isAlert = members.some(a => a.health === 'failed' || a.health === 'inprogress');
    const mode = n <= CHILD_LIST_MAX ? 'list' : n <= CHILD_TILE_MAX ? 'tile' : 'dot';

    const nameStr = truncName(plc.name);
    const ipStr = plc.ip || '';
    const countStr = `${n}대`;
    const aggR = 2.2;
    const countW = aggR * 2 + 2 + textWidth(countStr, COUNT_FZ);

    const headerH = Math.max(plcSize, NAME_LH + IP_LH);
    const hdrTextW = Math.max(textWidth(nameStr, NAME_FZ), textWidth(ipStr, IP_FZ));
    const headerInnerW = plcSize + HDR_GAP + hdrTextW + 8 + countW;
    const maxInnerW = Math.max(40, maxW - PAD * 2);

    // 본문 크기 + 셀 파라미터
    let bodyW = 0, bodyH = 0, cols = 1, rows = 1, tileSz = 0, dotR = 0, gap = 0;
    if (mode === 'list') {
      let maxNameW = 0, maxIpW = 0;
      for (const c of children) {
        maxNameW = Math.max(maxNameW, textWidth(truncName(c.name), ROW_NAME_FZ));
        maxIpW = Math.max(maxIpW, textWidth(c.ip || '', ROW_IP_FZ));
      }
      bodyW = ROW_DOT_R * 2 + 4 + maxNameW + 8 + maxIpW;
      bodyH = n * ROW_H; rows = n; cols = 1;
    } else if (mode === 'tile') {
      tileSz = Math.max(8, Math.min(plcSize * 0.62, 16)); gap = 2.0;
      const colsByWidth = Math.max(1, Math.floor((maxInnerW + gap) / (tileSz + gap)));
      cols = Math.min(Math.max(1, Math.round(Math.sqrt(n * 1.4))), colsByWidth);
      rows = Math.ceil(n / cols);
      bodyW = cols * tileSz + (cols - 1) * gap;
      bodyH = rows * tileSz + (rows - 1) * gap;
    } else {
      dotR = Math.max(1.7, Math.min(plcSize * 0.18, 3.2)); gap = 1.6;
      const cell = dotR * 2 + gap;
      const colsByWidth = Math.max(1, Math.floor((maxInnerW + gap) / cell));
      cols = Math.min(Math.max(1, Math.round(Math.sqrt(n * 2.0))), colsByWidth);
      rows = Math.ceil(n / cols);
      bodyW = cols * cell - gap;
      bodyH = rows * cell - gap;
    }

    const barShown = n >= 2;
    const legend = mode !== 'list' ? buildLegend(bd) : null;

    const innerW = Math.max(headerInnerW, bodyW, 50);
    const cardW = innerW + PAD * 2;
    let cy = PAD;
    const headerY = cy; cy += headerH;
    let barY = 0; if (barShown) { cy += BAR_GAP; barY = cy; cy += BAR_H; }
    cy += SEC_GAP; const bodyY = cy; cy += bodyH;
    let legendY = 0; if (legend) { cy += 2.5; legendY = cy; cy += LEG_FZ; }
    const cardH = cy + PAD;

    return {
      plc, children, n, bd, mode, cols, rows, tileSz, dotR, gap,
      aggColor, isAlert, nameStr, ipStr, countStr, aggR,
      cardW, cardH, plcSize, headerY, headerH, barShown, barY, bodyY, bodyW, legend, legendY,
      x: 0, y: 0,
    };
  }

  // 카운트 칩 hover 툴팁(연결 자산 요약). 클릭 시 멤버 전체 필터 목록으로 이동.
  function childrenTip(card) {
    const bd = card.bd;
    const rows = [tipRow('정상', String(bd.counts.backedup + bd.counts.unchanged))];
    if (bd.counts.inprogress) rows.push(tipRow('작업중', `<span style="color:${HEALTH_COLOR.inprogress}">${bd.counts.inprogress}</span>`));
    if (bd.counts.failed) rows.push(tipRow('실패', `<span style="color:${HEALTH_COLOR.failed}">${bd.counts.failed}</span>`));
    if (bd.counts.unknown) rows.push(tipRow('내역없음', String(bd.counts.unknown)));
    if (bd.offline) rows.push(tipRow('오프라인', `<span style="color:${OFFLINE_COLOR}">${bd.offline}</span>`));
    return `<div class="lv-tip-head"><span class="lv-tip-dot" style="background:${card.aggColor}"></span>`
      + `<span class="lv-tip-name">${escapeHtml(card.plc.name)} · 연결 ${card.n}대</span></div>`
      + `<div class="lv-tip-body">${rows.join('')}<div class="lv-tip-hint">클릭 시 연결 자산 목록</div></div>`;
  }

  // 상태바: 자식 health 비율 누적 막대(정상→경보). 트랙 + 세그먼트 + 라운드 프레임.
  function drawStatusBar(g, x, y, w, h, bd) {
    g.appendChild(svgEl('rect', { x, y, width: w, height: h, rx: h / 2, fill: 'rgba(255,255,255,0.07)' }));
    const total = bd.total || 1;
    let cx = x;
    for (const k of HEALTH_BAR_ORDER) {
      const cnt = bd.counts[k]; if (!cnt) continue;
      const segW = w * (cnt / total);
      g.appendChild(svgEl('rect', { x: cx, y, width: segW, height: h, fill: HEALTH_COLOR[k] }));
      cx += segW;
    }
    g.appendChild(svgEl('rect', { x, y, width: w, height: h, rx: h / 2, fill: 'none', stroke: 'rgba(0,0,0,0.3)', 'stroke-width': 0.6 }));
  }

  // 본문(list): 자식 1개 = 행(상태점 · 이름 · IP). 오프라인은 점에 회색 링(직교 신호).
  function drawChildRows(g, card, x, y, w) {
    card.children.forEach((c, i) => {
      const ry = y + i * ROW_H, cyr = ry + ROW_H / 2;
      const row = svgEl('g', { class: 'lv-asset-icon', style: 'cursor:pointer', 'data-asset-id': c.assetId, 'data-tip': titleFor(c, c.floor) });
      row.appendChild(svgEl('rect', { x, y: ry + 0.4, width: w, height: ROW_H - 0.8, rx: 2, fill: 'rgba(255,255,255,0.02)' }));
      const dotX = x + ROW_DOT_R + 1;
      if (c.pingReachable === false)
        row.appendChild(svgEl('circle', { cx: dotX, cy: cyr, r: ROW_DOT_R + 1.1, fill: 'none', stroke: OFFLINE_COLOR, 'stroke-width': 0.8 }));
      row.appendChild(svgEl('circle', { cx: dotX, cy: cyr, r: ROW_DOT_R, fill: colorFor(c) }));
      const nt = svgEl('text', { x: dotX + ROW_DOT_R + 3, y: cyr + ROW_NAME_FZ * 0.36, 'font-size': ROW_NAME_FZ, fill: '#dfe6f5' });
      nt.textContent = truncName(c.name); row.appendChild(nt);
      if (c.ip) {
        const it = svgEl('text', { x: x + w, y: cyr + ROW_IP_FZ * 0.36, 'text-anchor': 'end', 'font-size': ROW_IP_FZ, fill: 'rgba(184,200,230,0.6)', 'font-family': "'Consolas','Monaco',monospace" });
        it.textContent = c.ip; row.appendChild(it);
      }
      g.appendChild(row);
    });
  }

  // 본문(tile): 자식 1개 = 상태색 아이콘 타일. 오프라인은 코너 마크.
  function drawChildTiles(g, card, x, y) {
    const { cols, tileSz, gap } = card;
    card.children.forEach((c, i) => {
      const tx = x + (i % cols) * (tileSz + gap), ty = y + Math.floor(i / cols) * (tileSz + gap);
      const hcc = colorFor(c);
      const t = svgEl('g', { class: 'lv-asset-icon', style: 'cursor:pointer', 'data-asset-id': c.assetId, 'data-tip': titleFor(c, c.floor) });
      t.appendChild(svgEl('rect', { x: tx, y: ty, width: tileSz, height: tileSz, rx: Math.max(2, tileSz * 0.22), fill: neutralIconBg(c.health), stroke: hcc, 'stroke-width': c.health === 'failed' ? 1.5 : 1.1, opacity: 0.95 }));
      t.appendChild(svgEl('image', { href: iconHref(c.icon), x: tx + 1.2, y: ty + 1.2, width: tileSz - 2.4, height: tileSz - 2.4 }));
      if (c.pingReachable === false) offlineMark(t, tx, ty, tileSz);
      g.appendChild(t);
    });
  }

  // 본문(dot): 자식 1개 = 점(상태색). 오프라인은 회색 링. 점마다 hover 툴팁 + 클릭(상세).
  function drawChildDots(g, card, x, y) {
    const { cols, dotR, gap } = card;
    const cell = dotR * 2 + gap;
    card.children.forEach((c, i) => {
      const cxp = x + (i % cols) * cell + dotR, cyp = y + Math.floor(i / cols) * cell + dotR;
      const d = svgEl('g', { class: 'lv-asset-icon', style: 'cursor:pointer', 'data-asset-id': c.assetId, 'data-tip': titleFor(c, c.floor) });
      if (c.pingReachable === false)
        d.appendChild(svgEl('circle', { cx: cxp, cy: cyp, r: dotR + 0.9, fill: 'none', stroke: OFFLINE_COLOR, 'stroke-width': 0.7 }));
      d.appendChild(svgEl('circle', { cx: cxp, cy: cyp, r: dotR, fill: colorFor(c), stroke: 'rgba(0,0,0,0.25)', 'stroke-width': 0.3 }));
      g.appendChild(d);
    });
  }

  // 카운트 범례(tile/dot): 작은 색점 + 라벨을 한 줄로.
  function drawLegend(g, legend, x, y) {
    let lx = x;
    for (const e of legend) {
      g.appendChild(svgEl('circle', { cx: lx + LEG_DOT_R, cy: y + LEG_FZ * 0.42, r: LEG_DOT_R, fill: e.c }));
      const t = svgEl('text', { x: lx + LEG_DOT_R * 2 + 2, y: y + LEG_FZ * 0.82, 'font-size': LEG_FZ, fill: '#b9c3da' });
      t.textContent = e.t; g.appendChild(t);
      lx += LEG_DOT_R * 2 + 2 + textWidth(e.t, LEG_FZ) + 6;
    }
  }

  // PLC 카드 <g>: 그림자 → 본체(집계 상태색 테두리) → [경보 띠/틴트] → 헤더(아이콘·이름·IP·카운트)
  //              → 상태바 → 본문(모드별) → 범례. 배치/축소는 transform 으로 일괄 적용.
  // 카드 테두리/틴트 = 그룹(PLC+자식) 집계 상태색(card.aggColor), PLC 아이콘 테두리 = PLC 자신의 상태색.
  // bg: 라인 실효색에서 파생한 카드 배경(renderLineView 가 전달) — 미전달 시 고정 다크(CARD_BG).
  function plcCardGroup(card, bg) {
    // H = 행 통일 높이(renderH)가 있으면 그것, 없으면 자연 높이. 내용(헤더/바/본문/범례)은 상단 기준이라 그대로,
    // 박스(그림자·본체·경보 띠/틴트)만 H 까지 늘려 같은 행 카드들이 같은 높이가 된다.
    const plc = card.plc, PAD = CARD_PAD, W = card.cardW, H = card.renderH || card.cardH;
    const g = svgEl('g', { style: 'cursor:pointer', 'data-asset-id': plc.assetId });

    // 그림자(캔버스/영역/카드 3층 명도계단)
    g.appendChild(svgEl('rect', { x: 1.2, y: 1.8, width: W, height: H, rx: 6, fill: 'rgba(0,0,0,0.42)' }));
    // 본체 — 면은 중립: 경보(실패/작업중) 카드만 상태색 테두리 + 은은한 틴트/색띠, 정상 카드는 무채 테두리.
    g.appendChild(svgEl('rect', {
      x: 0, y: 0, width: W, height: H, rx: 6,
      fill: bg || CARD_BG, stroke: card.isAlert ? card.aggColor : 'rgba(150,165,205,0.40)',
      'stroke-width': card.isAlert ? 1.4 : 1.0, 'stroke-opacity': card.isAlert ? 0.95 : 1,
    }));
    if (card.isAlert) {
      g.appendChild(svgEl('rect', { x: 0, y: 0, width: W, height: H, rx: 6, fill: card.aggColor, 'fill-opacity': 0.06 }));
      g.appendChild(svgEl('rect', { x: 1, y: 4, width: 2, height: H - 8, rx: 1, fill: card.aggColor }));
    }

    // ── 헤더: PLC 아이콘(자체 상태색) ──
    const icoSize = card.plcSize;
    const icoX = PAD, icoY = card.headerY + (card.headerH - icoSize) / 2;
    const hc = plc.healthColor || colorFor(plc);
    const ico = svgEl('g', { class: 'lv-asset-icon', 'data-tip': titleFor(plc, plc.floor) });
    ico.appendChild(svgEl('rect', { x: icoX, y: icoY, width: icoSize, height: icoSize, rx: 4, fill: neutralIconBg(plc.health), stroke: hc, 'stroke-width': 0.9, opacity: 0.97 }));
    ico.appendChild(svgEl('image', { href: iconHref(plc.icon), x: icoX + 2, y: icoY + 2, width: icoSize - 4, height: icoSize - 4 }));
    g.appendChild(ico);
    if (plc.pingReachable === false) offlineMark(g, icoX, icoY, icoSize);

    // 이름(hero) + IP(보조)
    const tx = icoX + icoSize + HDR_GAP;
    const blockY = card.headerY + (card.headerH - (NAME_LH + IP_LH)) / 2;
    if (card.nameStr) {
      const nt = svgEl('text', { x: tx, y: blockY + NAME_FZ * 0.92, 'font-size': NAME_FZ, fill: '#eef1fb', 'font-weight': 'bold' });
      nt.textContent = card.nameStr; g.appendChild(nt);
    }
    if (card.ipStr) {
      const it = svgEl('text', { x: tx, y: blockY + NAME_LH + IP_FZ * 0.9, 'font-size': IP_FZ, fill: 'rgba(184,200,230,0.72)', 'font-family': "'Consolas','Monaco',monospace", 'letter-spacing': 0.2 });
      it.textContent = card.ipStr; g.appendChild(it);
    }
    // 카운트 칩(우측) — 집계 점 + "N대", 클릭 시 연결 자산 전체 필터 목록.
    const ids = card.children.map(c => c.assetId).join(',');
    const cg = svgEl('g', { class: 'lv-asset-icon', style: 'cursor:pointer', 'data-asset-ids': ids, 'data-tip': childrenTip(card) });
    const dotCy = card.headerY + card.headerH / 2;
    const countCx = W - PAD - textWidth(card.countStr, COUNT_FZ) - card.aggR - 1.5;
    cg.appendChild(svgEl('circle', { cx: countCx, cy: dotCy, r: card.aggR, fill: card.aggColor, stroke: 'white', 'stroke-width': 0.4 }));
    const ct = svgEl('text', { x: W - PAD, y: dotCy + COUNT_FZ * 0.36, 'text-anchor': 'end', 'font-size': COUNT_FZ, fill: '#c7d0e8', 'font-weight': 'bold' });
    ct.textContent = card.countStr; cg.appendChild(ct);
    g.appendChild(cg);

    // ── 상태바 ──
    if (card.barShown) drawStatusBar(g, PAD, card.barY, W - PAD * 2, BAR_H, card.bd);

    // ── 본문(모드별) ──
    if (card.mode === 'list') drawChildRows(g, card, PAD, card.bodyY, W - PAD * 2);
    else if (card.mode === 'tile') drawChildTiles(g, card, PAD, card.bodyY);
    else drawChildDots(g, card, PAD, card.bodyY);

    // ── 범례(tile/dot) ──
    if (card.legend) drawLegend(g, card.legend, PAD, card.legendY);

    // 배치: 내용은 origin(0,0) 기준이므로 packed-local 위치로 이동만. 축소는 renderLineView 의 scaler 가 일괄 처리.
    g.setAttribute('transform', `translate(${card.x} ${card.y})`);
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
    const panEnabled = opts.pan !== false; // 드래그 팬 (기본 on; 대시보드 위젯은 pan:false 로 끔)
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
        // 클릭 팝오버가 떠 있는 동안엔 hover 툴팁 억제 — 같은 자리에 겹쳐 글자가 뒤섞여 보임
        if (popEl && popEl.style.display !== 'none') { hideTip(); return; }
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
      hideTip(); // hover 툴팁과 같은 좌표에 뜨므로 먼저 치움
      const p = ensurePop();
      p.innerHTML = `<button class="lv-pop-close" title="닫기">&times;</button>`
        + titleFor(a, a.floor)
        + `<a class="lv-pop-link" href="/assets/${a.assetId}"><span class="material-symbols-outlined">open_in_new</span>상세 보기</a>`;
      p.querySelector('.lv-pop-close').addEventListener('click', (e) => { e.stopPropagation(); closePop(); });
      positionPop(evt);
    }

    function openLinePopover(lineId, evt) {
      hideTip();
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

      // 라인 카드 서피스 = 사용자 설정(도면 설정 '라인 영역 색', #rrggbb 또는 #rrggbbaa) 또는 기본값.
      // 면은 중립으로 두고 상태색은 경보(실패/작업중)의 테두리·스트립과 헤더 요약에만 쓴다.
      const lc = parseHexA(inst.data.config && inst.data.config.lineColor) || parseHexA(LINE_BG_DEFAULT);
      const lineBg = lc.hex6, lineAlpha = lc.a;
      // 텍스트 대비는 실효색(반투명이면 캔버스 배경과 혼합한 색)의 밝기로 판정
      const cv = parseHexA(inst.data.config && inst.data.config.bgColor) || { r: 255, g: 255, b: 255 };
      const mix = (c1, c2) => c1 * lineAlpha + c2 * (1 - lineAlpha);
      const effR = mix(lc.r, cv.r), effG = mix(lc.g, cv.g), effB = mix(lc.b, cv.b);
      const lum = (0.2126 * effR + 0.7152 * effG + 0.0722 * effB) / 255;
      const lightBg = lum > 0.55;

      // PLC 카드 배경 파생 — 라인 실효색 기준 명도 계단 자동 유지:
      // 밝은 라인 = 고정 다크 카드(검증된 대비), 깊은 다크 = 살짝 밝게, 중간톤 = 어둡게.
      // 두 규칙은 lum 0.15~0.25 구간에서 선형 블렌드(투명도 슬라이더 연속 변화 시 점프 방지).
      let cardBg = CARD_BG;
      if (!lightBg) {
        const t = Math.max(0, Math.min(1, (lum - 0.15) / 0.10));
        const ch = (v) => Math.round((v + (255 - v) * 0.10) * (1 - t) + v * 0.52 * t);
        cardBg = '#' + [effR, effG, effB].map(v => ch(v).toString(16).padStart(2, '0')).join('');
      }
      const ink = lightBg ? '#1d2433' : '#e8edf9';
      const dim = lightBg ? 'rgba(29,36,51,0.60)' : 'rgba(184,200,230,0.65)';
      const edge = lightBg ? 'rgba(20,28,45,0.25)' : 'rgba(150,165,205,0.28)';

      for (const rect of inst.data.blueprintRects) {
        const line = lineMap.get(rect.lineId);
        if (!line) continue;
        const lineAssets = inst.data.assets.filter(a => a.lineId === rect.lineId);
        const topLevel = lineAssets.filter(a => !hier.childIds.has(a.assetId));
        const bd = healthBreakdown(lineAssets);
        const alertColor = bd.counts.failed > 0 ? HEALTH_COLOR.failed
          : bd.counts.inprogress > 0 ? HEALTH_COLOR.inprogress : null;

        const g = svgEl('g', { 'data-line-id': rect.lineId, style: 'cursor:pointer' });
        // 그림자 + 본체(중립 서피스) — 캔버스/영역/카드 3층 명도계단의 가운데 층.
        // 반투명 카드면 그림자가 면 전체에 비쳐 탁해지므로 생략.
        if (lineAlpha > 0.9)
          g.appendChild(svgEl('rect', { x: rect.x + 1, y: rect.y + 1.5, width: rect.width, height: rect.height, rx: 6, fill: 'rgba(0,0,0,0.35)' }));
        g.appendChild(svgEl('rect', {
          x: rect.x, y: rect.y, width: rect.width, height: rect.height, rx: 6, ry: 6,
          fill: lineBg, 'fill-opacity': lineAlpha < 1 ? lineAlpha : null,
          stroke: alertColor || edge, 'stroke-width': alertColor ? 1.1 : 0.7,
          'stroke-opacity': alertColor ? 0.75 : 1,
        }));
        // 상단 경보 스트립 (실패=빨강 / 작업중=주황) — 축소 상태에서도 아픈 라인이 먼저 보이게
        if (alertColor)
          g.appendChild(svgEl('rect', { x: rect.x + 8, y: rect.y + 0.6, width: Math.max(10, rect.width - 16), height: 2.2, rx: 1.1, fill: alertColor }));

        // ── 헤더: 라인명·N대(좌) + 상태 세그먼트바·실패칩(우) — 중앙 워터마크 제거.
        //    이름이 우선: 칩/바는 이름 최소폭을 확보하고도 공간이 남을 때만 그린다("WB…" 방지).
        const padL = rect.x + 7;
        const cntStr = lineAssets.length > 0 ? `${lineAssets.length}대` : '';
        const nameW = (s) => textWidth(s, 10) * 1.12; // 볼드 보정 — textWidth 는 일반 굵기 기준
        const cntW = cntStr ? textWidth(cntStr, 6.5) + 5 : 0;
        const minNameW = Math.min(nameW(String(line.name || '')), (rect.width - 14) * 0.55);
        let rightX = rect.x + rect.width - 7; // 우측 클러스터 커서 (칩 → 바 순서로 왼쪽으로 채움)
        if (bd.counts.failed > 0) {
          const ftxt = `실패 ${bd.counts.failed}`;
          const chipW = textWidth(ftxt, 6.2) + 9, chipH = 10.5, chipY = rect.y + 4.6;
          if (rightX - chipW - 5 - padL - cntW >= minNameW) {
            g.appendChild(svgEl('rect', {
              x: rightX - chipW, y: chipY, width: chipW, height: chipH, rx: chipH / 2,
              fill: HEALTH_COLOR.failed, 'fill-opacity': 0.2, stroke: HEALTH_COLOR.failed, 'stroke-width': 0.7,
            }));
            const ft = svgEl('text', {
              x: rightX - chipW / 2, y: chipY + chipH / 2 + 6.2 * 0.36, 'text-anchor': 'middle',
              'font-size': 6.2, 'font-weight': 'bold', fill: lightBg ? '#a33c36' : '#ffd9d6',
            });
            ft.textContent = ftxt; g.appendChild(ft);
            rightX -= chipW + 5;
          }
        }
        if (lineAssets.length >= 2) {
          const barW = Math.min(52, rect.width * 0.2);
          if (barW >= 18 && rightX - barW - 6 - padL - cntW >= minNameW) {
            drawStatusBar(g, rightX - barW, rect.y + 7.6, barW, 4.2, bd);
            rightX -= barW + 6;
          }
        }
        const availName = rightX - padL - cntW - 3;
        let name = String(line.name || '');
        if (nameW(name) > availName) {
          while (name.length > 1 && nameW(name + '…') > availName) name = name.slice(0, -1);
          name += '…';
        }
        const label = svgEl('text', { x: padL, y: rect.y + 13.2, 'font-size': 10, 'font-weight': 'bold', fill: ink });
        label.textContent = name;
        g.appendChild(label);
        if (cntStr) {
          const ct = svgEl('text', { x: padL + nameW(name) + 5, y: rect.y + 13.2, 'font-size': 6.5, 'font-weight': 'bold', fill: dim });
          ct.textContent = cntStr; g.appendChild(ct);
        }

        // 영역 내부 자산 아이콘 (clipPath 로 영역 내 제한) — 1단(topLevel) 그리드 배치
        const clipId = `lv-line-clip-${inst.clipNs}-${rect.lineId}`;
        const clip = svgEl('clipPath', { id: clipId });
        clip.appendChild(svgEl('rect', { x: rect.x, y: rect.y, width: rect.width, height: rect.height, rx: 6 }));
        g.appendChild(clip);

        const inner = svgEl('g', { 'clip-path': `url(#${clipId})` });
        // PLC 카드 + standalone 을 packed-local 좌표로 배치하고, 블록 전체를 영역에 맞춘
        // scaler(<g transform>) 안에 담는다. clip 은 영역 좌표계의 inner 가 담당(잘림 방지 안전망).
        const { standalones, plcCards, transform } = layoutLineAssets(rect, topLevel, hier.plcChildren);
        const scaler = svgEl('g', { transform });
        for (const card of plcCards) scaler.appendChild(plcCardGroup(card, cardBg));
        for (const st of standalones) {
          const m = assetMarker(st.asset, st.x, st.y, st.size, st.asset.floor, true);
          if (st.asset.health === 'unknown') m.setAttribute('opacity', '0.55'); // 내역없음 감쇠 — 실상태가 튀게
          scaler.appendChild(m);
        }
        inner.appendChild(scaler);
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
      svg.style.backgroundColor = (data.config && data.config.bgColor) || '#ffffff';
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
      if (!on) { viewport.style.gridTemplateColumns = ''; viewport.style.gridTemplateRows = ''; }
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
        window.blueprintZoom.init(viewport.id, { clampMargin: 0.3, pan: panEnabled });
        if (savedVb) window.blueprintZoom.setViewBox(viewport.id, savedVb);
        viewport.appendChild(buildControls());
        onFsChange(); // 전체화면 아이콘 동기화
      }
      const shown = svg.querySelectorAll('.lv-asset-icon').length;
      if (countEl) countEl.textContent = `${shown}개 표시 · 자산/라인 클릭 시 상세 보기`;
    }

    // 분할 뷰: 모든 레이아웃을 그리드 셀(미니 화면)로 렌더.
    // CCTV식 모자이크 — 가로로만 늘리지 않고 도면 수에 맞춰 가로·세로를 번갈아 분할한다.
    // rows=round(√n), cols=ceil(n/rows) 로 가로(landscape)에 살짝 치우친 정사각형에 가까운 격자를 만들고,
    // 마지막(덜 찬) 행의 셀들이 남은 칸을 균등 분배해 빈칸 없이 채운다.
    function renderSplit() {
      viewport.innerHTML = '';
      tipEl = null;
      let total = 0;
      const n = inst.splitData.length;
      const rows = Math.max(1, Math.round(Math.sqrt(n)));
      const cols = Math.ceil(n / rows);
      viewport.style.gridTemplateColumns = `repeat(${cols}, minmax(0, 1fr))`;
      viewport.style.gridTemplateRows = `repeat(${rows}, minmax(0, 1fr))`;
      const lastRowStart = cols * (rows - 1);     // 마지막 행 첫 셀 인덱스
      const lastRowCount = n - lastRowStart;       // 마지막 행 셀 수
      const spanBase = Math.floor(cols / lastRowCount);
      const spanExtra = cols % lastRowCount;
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
        // 마지막 행이 덜 찼으면 그 행 셀들을 늘려 남은 칸을 채운다(빈칸 방지).
        if (i >= lastRowStart && lastRowCount < cols) {
          const j = i - lastRowStart;
          cell.style.gridColumn = `span ${spanBase + (j < spanExtra ? 1 : 0)}`;
        }
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
