/* ============================================================================
 * 통계(Statistics) — 기간별 백업 추이/성공·실패·변화 + 온·오프라인 전환 통계.
 * GET /api/statistics?start=&end= 로 (자산×일자) 버킷을 1회 조회 → 전체/라인별/타입별/개별
 * 분해·비율·추이는 전부 클라이언트가 합산해 렌더(필터 변경 시 재조회 없음).
 * 차트는 외부 라이브러리 없이 순수 SVG(폐쇄망/CDN 제거 정책 준수, 대시보드 위젯과 동일 기조).
 * ==========================================================================*/
(function () {
  'use strict';

  const $ = (id) => document.getElementById(id);
  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const SVGNS = 'http://www.w3.org/2000/svg';

  // 비교(라인/타입 전체비교) 모드에서 그룹별 라인 색 — 라이트/다크 공통으로 무난한 팔레트.
  const PALETTE = ['#3b82f6', '#ef4444', '#10b981', '#f59e0b', '#8b5cf6', '#ec4899',
    '#14b8a6', '#f97316', '#6366f1', '#84cc16', '#06b6d4', '#d946ef', '#0ea5e9', '#dc2626'];

  // ── 상태 ──
  const state = {
    data: null,            // 서버 응답
    meta: new Map(),       // assetId → { name, line, type }
    dim: 'all',            // all | line | type | asset
    target: '',            // 선택 그룹/자산 ('' = 라인/타입 전체 비교)
    valmode: 'count',      // count | rate  (단일 스코프 추이)
    metric: 'n',           // 비교 모드 지표
    hidden: new Set(),     // 추이 차트에서 숨긴 시리즈 키
    sortKey: 'n',          // 그룹 상세 정렬 컬럼
    sortDir: -1,           // 1 오름 / -1 내림
    bdPage: 0,             // 그룹 상세 테이블 현재 페이지
  };

  const BD_PAGE_SIZE = 15; // 그룹 상세 페이지당 행 수

  // ── 날짜 유틸 ──
  const pad = (n) => String(n).padStart(2, '0');
  const toISO = (d) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  const fromISO = (s) => { const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(String(s || '')); return m ? new Date(+m[1], +m[2] - 1, +m[3]) : null; };
  const mmdd = (s) => { const m = /^\d{4}-(\d{2})-(\d{2})$/.exec(String(s || '')); return m ? `${m[1]}/${m[2]}` : s; };
  const kdate = (s) => { const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(String(s || '')); return m ? `${m[1]}.${m[2]}.${m[3]}` : s; };

  // 테마 변수 → 실제 색 (SVG presentation attribute 는 var() 미지원이라 매 렌더 해석).
  function palette() {
    const cs = getComputedStyle(document.documentElement);
    const v = (n) => cs.getPropertyValue(n).trim();
    return {
      primary: v('--c-primary') || '#3b82f6',
      changed: v('--health-backedup') || '#65b991',   // 변화/갱신 = 초록
      unchanged: v('--health-unchanged') || '#6ba0de', // 변경없음 = 파랑
      failed: v('--health-failed') || '#e67e7e',       // 실패 = 빨강
      inprogress: v('--health-inprogress') || '#f59e0b', // 작업중 = 주황
      muted: v('--c-on-surface-variant') || '#888',
    };
  }

  // ──────────────────────────────────────────────────────────────────────────
  //  데이터 합산 (클라이언트 그룹핑)
  // ──────────────────────────────────────────────────────────────────────────

  // 현재 스코프에 해당하는 자산 id 집합. null = 전체.
  function scopeIds() {
    const d = state.data; if (!d) return null;
    if (state.dim === 'all') return null;
    if (state.dim === 'asset') {
      const id = parseInt(state.target, 10);
      return Number.isFinite(id) ? new Set([id]) : new Set();
    }
    if (!state.target) return null; // 라인/타입 전체 비교 → 전체
    const field = state.dim; // 'line' | 'type'
    const ids = new Set();
    state.meta.forEach((m, id) => { if (m[field] === state.target) ids.add(id); });
    return ids;
  }

  // 백업 일자별 합계: ids(null=전체) → [{n,c,u,f,p}] (days 길이)
  function backupDaily(ids) {
    const d = state.data;
    const out = d.days.map(() => ({ n: 0, c: 0, u: 0, f: 0, p: 0 }));
    for (const b of d.backup) {
      if (ids && !ids.has(b.a)) continue;
      const o = out[b.d]; if (!o) continue;
      o.n += b.n; o.c += b.c; o.u += b.u; o.f += b.f; o.p += b.p;
    }
    return out;
  }

  // 전환 일자별 합계: ids → [{on,off}]
  function transDaily(ids) {
    const d = state.data;
    const out = d.days.map(() => ({ on: 0, off: 0 }));
    for (const t of d.transition) {
      if (ids && !ids.has(t.a)) continue;
      const o = out[t.d]; if (!o) continue;
      o.on += t.on; o.off += t.off;
    }
    return out;
  }

  const sum = (arr, k) => arr.reduce((s, x) => s + (x[k] || 0), 0);
  const rate = (num, den) => den > 0 ? Math.round(1000 * num / den) / 10 : 0;

  // 단일 그룹의 기간 합계 + 비율
  function totals(ids) {
    const bd = backupDaily(ids), td = transDaily(ids);
    const n = sum(bd, 'n'), c = sum(bd, 'c'), u = sum(bd, 'u'), f = sum(bd, 'f'), p = sum(bd, 'p');
    return {
      n, c, u, f, p, s: c + u,
      on: sum(td, 'on'), off: sum(td, 'off'),
      successRate: rate(c + u, n), changeRate: rate(c, n), failRate: rate(f, n),
    };
  }

  // 비교 모드용: 그룹별(라인/타입) id 집합
  function groupIdMap(field) {
    const map = new Map(); // groupName → Set(ids)
    state.meta.forEach((m, id) => {
      const g = m[field];
      if (!map.has(g)) map.set(g, new Set());
      map.get(g).add(id);
    });
    return map;
  }

  // ──────────────────────────────────────────────────────────────────────────
  //  SVG 차트 엔진 (순수 SVG)
  // ──────────────────────────────────────────────────────────────────────────

  function el(tag, attrs, parent) {
    const e = document.createElementNS(SVGNS, tag);
    for (const k in attrs) e.setAttribute(k, attrs[k]);
    if (parent) parent.appendChild(e);
    return e;
  }
  function niceMax(v) {
    if (v <= 0) return 1;
    const pow = Math.pow(10, Math.floor(Math.log10(v)));
    const n = v / pow;
    const step = n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10;
    return step * pow;
  }
  function clearHost(host) {
    // 호스트 안의 svg/tooltip 제거 후 새로 그린다.
    host.querySelectorAll('svg, .st-tip').forEach(e => e.remove());
  }
  function emptyHost(host, msg) {
    clearHost(host);
    host.innerHTML = `<div class="st-chart-empty">${esc(msg || '표시할 데이터가 없습니다.')}</div>`;
  }
  function ensureSvg(host, W, H) {
    host.innerHTML = '';
    const svg = el('svg', { width: W, height: H, viewBox: `0 0 ${W} ${H}` }, host);
    const tip = document.createElement('div');
    tip.className = 'st-tip';
    host.appendChild(tip);
    return { svg, tip };
  }
  function showTip(tip, host, px, py, html) {
    tip.innerHTML = html;
    const W = host.clientWidth;
    let left = Math.max(8, Math.min(W - 8, px));
    tip.style.left = left + 'px';
    tip.style.top = Math.max(8, py) + 'px';
    tip.style.display = 'block';
  }

  // 가로 격자 + Y축 라벨 + X축 라벨 공통 렌더. 반환: 좌표 변환기.
  function axes(svg, W, H, n, yMax, percent) {
    const mL = 46, mR = 14, mT = 12, mB = 26;
    const pw = W - mL - mR, ph = H - mT - mB;
    const xAt = (i) => n <= 1 ? mL + pw / 2 : mL + (i / (n - 1)) * pw;
    const yAt = (v) => mT + ph - (Math.max(0, v) / yMax) * ph;

    // 가로 격자 (5단)
    const ticks = 5;
    for (let t = 0; t <= ticks; t++) {
      const val = yMax * t / ticks;
      const y = mT + ph - (t / ticks) * ph;
      el('line', { x1: mL, y1: y, x2: W - mR, y2: y, class: t === 0 ? 'st-axis-line' : 'st-grid-line' }, svg);
      const lab = percent ? Math.round(val) + '%' : (Number.isInteger(val) ? val : Math.round(val));
      const tx = el('text', { x: mL - 8, y: y + 3, 'text-anchor': 'end', class: 'st-axis-label' }, svg);
      tx.textContent = lab;
    }
    // X축 라벨 (최대 ~12개)
    const step = Math.max(1, Math.ceil(n / 12));
    for (let i = 0; i < n; i++) {
      if (i % step !== 0 && i !== n - 1) continue;
      const tx = el('text', { x: xAt(i), y: H - 8, 'text-anchor': 'middle', class: 'st-axis-label' }, svg);
      tx.textContent = mmdd(state.data.days[i]);
    }
    return { mL, mR, mT, mB, pw, ph, xAt, yAt };
  }

  // 다중 라인 차트. series: [{key,label,color,values:[num|null],width?,dash?}]
  function lineChart(host, { series, yMax, percent, tipTitle }) {
    const days = state.data.days, n = days.length;
    const visible = series.filter(s => !state.hidden.has(s.key));
    let max = yMax;
    if (max == null) {
      max = 0;
      visible.forEach(s => s.values.forEach(v => { if (v != null && v > max) max = v; }));
      max = niceMax(max);
    }
    if (percent) max = 100;
    if (max <= 0) max = 1;

    const W = host.clientWidth || 800, H = 300;
    const { svg, tip } = ensureSvg(host, W, H);
    const ax = axes(svg, W, H, n, max, percent);

    visible.forEach(s => {
      // null 을 끊어 세그먼트로 분할
      let dPath = '', pen = false;
      for (let i = 0; i < n; i++) {
        const v = s.values[i];
        if (v == null) { pen = false; continue; }
        const x = ax.xAt(i), y = ax.yAt(v);
        dPath += (pen ? 'L' : 'M') + x.toFixed(1) + ' ' + y.toFixed(1) + ' ';
        pen = true;
      }
      if (dPath) el('path', { d: dPath.trim(), fill: 'none', stroke: s.color, 'stroke-width': s.width || 2.4, 'stroke-linejoin': 'round', 'stroke-linecap': 'round', 'stroke-dasharray': s.dash || 'none' }, svg);
      // 점 (일수 적을 때만 — 30일까지)
      if (n <= 31) {
        for (let i = 0; i < n; i++) {
          const v = s.values[i]; if (v == null) continue;
          el('circle', { cx: ax.xAt(i), cy: ax.yAt(v), r: n === 1 ? 4 : 2.6, fill: s.color }, svg);
        }
      }
    });

    // 단일 점(하루)일 때 값 라벨
    if (n === 1) {
      visible.forEach((s, k) => {
        const v = s.values[0]; if (v == null) return;
        // 시리즈별로 위로 단차를 줘 값이 겹쳐도 라벨이 포개지지 않게 한다.
        const tx = el('text', { x: ax.xAt(0) + 8, y: ax.yAt(v) - 2 - k * 14, class: 'st-axis-label' }, svg);
        tx.setAttribute('fill', s.color); tx.textContent = percent ? v + '%' : v;
      });
    }

    // 호버 가이드 + 툴팁
    const guide = el('line', { x1: 0, y1: ax.mT, x2: 0, y2: ax.mT + ax.ph, class: 'st-axis-line', opacity: 0, 'stroke-dasharray': '3 3' }, svg);
    const hot = el('rect', { x: ax.mL, y: ax.mT, width: ax.pw, height: ax.ph, fill: 'transparent' }, svg);
    const nearest = (mx) => n <= 1 ? 0 : Math.max(0, Math.min(n - 1, Math.round((mx - ax.mL) / ax.pw * (n - 1))));
    const move = (ev) => {
      const r = host.getBoundingClientRect();
      const mx = ev.clientX - r.left;
      const i = nearest(mx);
      const gx = ax.xAt(i);
      guide.setAttribute('x1', gx); guide.setAttribute('x2', gx); guide.setAttribute('opacity', 0.6);
      const rows = visible.map(s => {
        const v = s.values[i];
        return `<div class="st-tip-row"><span class="st-tip-key"><span class="st-tip-swatch" style="background:${s.color}"></span>${esc(s.label)}</span><span class="st-tip-val">${v == null ? '–' : (percent ? v + '%' : v.toLocaleString())}</span></div>`;
      }).join('');
      showTip(tip, host, gx, ax.mT + 4, `<div class="st-tip-title">${esc(kdate(days[i]))}${tipTitle ? ' · ' + esc(tipTitle) : ''}</div>${rows}`);
    };
    hot.addEventListener('mousemove', move);
    hot.addEventListener('mouseleave', () => { tip.style.display = 'none'; guide.setAttribute('opacity', 0); });
  }

  // 누적 막대 차트. stacks: [{key,label,color,values}]
  function stackedChart(host, { stacks }) {
    const days = state.data.days, n = days.length;
    const totalsPerDay = days.map((_, i) => stacks.reduce((t, s) => t + (s.values[i] || 0), 0));
    let max = niceMax(Math.max(1, ...totalsPerDay));
    const W = host.clientWidth || 800, H = 280;
    const { svg, tip } = ensureSvg(host, W, H);
    const ax = axes(svg, W, H, n, max, false);
    const band = ax.pw / n;
    const bw = Math.min(band * 0.66, 38);

    for (let i = 0; i < n; i++) {
      const cx = n <= 1 ? ax.mL + ax.pw / 2 : ax.mL + band * (i + 0.5);
      let yTop = ax.yAt(0);
      stacks.forEach(s => {
        const v = s.values[i] || 0; if (v <= 0) return;
        const h = (v / max) * ax.ph;
        yTop -= h;
        el('rect', { x: cx - bw / 2, y: yTop, width: bw, height: h, fill: s.color, rx: 1.5 }, svg);
      });
    }
    // 호버
    const hot = el('rect', { x: ax.mL, y: ax.mT, width: ax.pw, height: ax.ph, fill: 'transparent' }, svg);
    const hl = el('rect', { x: 0, y: ax.mT, width: bw, height: ax.ph, fill: 'currentColor', opacity: 0, rx: 2 }, svg);
    hl.style.color = palette().muted;
    const move = (ev) => {
      const r = host.getBoundingClientRect();
      const mx = ev.clientX - r.left;
      const i = n <= 1 ? 0 : Math.max(0, Math.min(n - 1, Math.floor((mx - ax.mL) / band)));
      const cx = n <= 1 ? ax.mL + ax.pw / 2 : ax.mL + band * (i + 0.5);
      hl.setAttribute('x', cx - bw / 2); hl.setAttribute('opacity', 0.08);
      const tot = totalsPerDay[i];
      const rows = stacks.map(s => `<div class="st-tip-row"><span class="st-tip-key"><span class="st-tip-swatch" style="background:${s.color}"></span>${esc(s.label)}</span><span class="st-tip-val">${(s.values[i] || 0).toLocaleString()}</span></div>`).join('');
      showTip(tip, host, cx, ax.mT + 4, `<div class="st-tip-title">${esc(kdate(days[i]))} · 총 ${tot.toLocaleString()}회</div>${rows}`);
    };
    hot.addEventListener('mousemove', move);
    hot.addEventListener('mouseleave', () => { tip.style.display = 'none'; hl.setAttribute('opacity', 0); });
  }

  // 발산형 막대(전환): 온라인 위 / 오프라인 아래.
  function divergingChart(host, { up, down }) {
    const days = state.data.days, n = days.length;
    const maxUp = Math.max(0, ...up.values), maxDown = Math.max(0, ...down.values);
    const max = niceMax(Math.max(1, maxUp, maxDown));
    const W = host.clientWidth || 500, H = 280;
    const mL = 40, mR = 12, mT = 14, mB = 24;
    const pw = W - mL - mR, ph = H - mT - mB;
    const mid = mT + ph / 2;
    const { svg, tip } = ensureSvg(host, W, H);
    const band = pw / n;
    const bw = Math.min(band * 0.5, 16);
    const half = ph / 2;

    // 중앙(0)선 + 위/아래 눈금
    el('line', { x1: mL, y1: mid, x2: W - mR, y2: mid, class: 'st-axis-line' }, svg);
    [1, 2].forEach(t => {
      [-1, 1].forEach(sgn => {
        const y = mid - sgn * (t / 2) * half;
        el('line', { x1: mL, y1: y, x2: W - mR, y2: y, class: 'st-grid-line' }, svg);
        const tx = el('text', { x: mL - 6, y: y + 3, 'text-anchor': 'end', class: 'st-axis-label' }, svg);
        tx.textContent = Math.round(max * t / 2);
      });
    });
    const xAt = (i) => n <= 1 ? mL + pw / 2 : mL + band * (i + 0.5);
    // X 라벨
    const step = Math.max(1, Math.ceil(n / 8));
    for (let i = 0; i < n; i++) {
      if (i % step !== 0 && i !== n - 1) continue;
      const tx = el('text', { x: xAt(i), y: H - 7, 'text-anchor': 'middle', class: 'st-axis-label' }, svg);
      tx.textContent = mmdd(days[i]);
    }
    for (let i = 0; i < n; i++) {
      const cx = xAt(i);
      const uh = (up.values[i] || 0) / max * half;
      const dh = (down.values[i] || 0) / max * half;
      if (uh > 0) el('rect', { x: cx - bw / 2, y: mid - uh, width: bw, height: uh, fill: up.color, rx: 1.5 }, svg);
      if (dh > 0) el('rect', { x: cx - bw / 2, y: mid, width: bw, height: dh, fill: down.color, rx: 1.5 }, svg);
    }
    const hot = el('rect', { x: mL, y: mT, width: pw, height: ph, fill: 'transparent' }, svg);
    const move = (ev) => {
      const r = host.getBoundingClientRect();
      const mx = ev.clientX - r.left;
      const i = n <= 1 ? 0 : Math.max(0, Math.min(n - 1, Math.floor((mx - mL) / band)));
      const cx = xAt(i);
      showTip(tip, host, cx, mT + 4,
        `<div class="st-tip-title">${esc(kdate(days[i]))}</div>` +
        `<div class="st-tip-row"><span class="st-tip-key"><span class="st-tip-swatch" style="background:${up.color}"></span>${esc(up.label)}</span><span class="st-tip-val">${(up.values[i] || 0).toLocaleString()}</span></div>` +
        `<div class="st-tip-row"><span class="st-tip-key"><span class="st-tip-swatch" style="background:${down.color}"></span>${esc(down.label)}</span><span class="st-tip-val">${(down.values[i] || 0).toLocaleString()}</span></div>`);
    };
    hot.addEventListener('mousemove', move);
    hot.addEventListener('mouseleave', () => { tip.style.display = 'none'; });
  }

  // ──────────────────────────────────────────────────────────────────────────
  //  렌더
  // ──────────────────────────────────────────────────────────────────────────

  const isCompare = () => (state.dim === 'line' || state.dim === 'type') && !state.target;

  function renderAll() {
    if (!state.data) return;
    const ids = scopeIds();
    renderScopeLabels(ids);
    renderKpis(ids);
    renderTrend(ids);
    renderStack(ids);
    renderTrans(ids);
    renderBreakdown();
  }

  function scopeName() {
    if (state.dim === 'all') return '전체 자산';
    if (state.dim === 'asset') {
      const m = state.meta.get(parseInt(state.target, 10));
      return m ? m.name : '자산';
    }
    if (!state.target) return state.dim === 'line' ? '라인별 비교' : '타입별 비교';
    return state.target;
  }

  function renderScopeLabels(ids) {
    const cnt = ids ? ids.size : state.meta.size;
    $('st-scope-count').textContent = `· 대상 ${cnt.toLocaleString()}개 자산`;
    $('st-range-label').textContent = `${kdate(state.data.start)} ~ ${kdate(state.data.end)} (${state.data.days.length}일)`;
    $('st-trend-scope').textContent = '· ' + scopeName();
  }

  function renderKpis(ids) {
    const t = totals(ids);
    const dayN = state.data.days.length || 1;
    $('kpi-total').textContent = t.n.toLocaleString();
    $('kpi-total-sub').textContent = `일 평균 ${(Math.round(t.n / dayN * 10) / 10).toLocaleString()}회`;
    $('kpi-success').textContent = t.successRate;
    $('kpi-success-sub').textContent = `성공 ${t.s.toLocaleString()} / 시도 ${t.n.toLocaleString()}회`;
    $('kpi-changed').textContent = t.c.toLocaleString();
    $('kpi-changed-sub').textContent = `변화율 ${t.changeRate}%`;
    $('kpi-failed').textContent = t.f.toLocaleString();
    $('kpi-failed-sub').textContent = `실패율 ${t.failRate}%`;
    $('kpi-online').textContent = t.on.toLocaleString();
    $('kpi-online-sub').textContent = '복구(오프라인→온라인)';
    $('kpi-offline').textContent = t.off.toLocaleString();
    $('kpi-offline-sub').textContent = '단절(온라인→오프라인)';
  }

  function renderTrend(ids) {
    const host = $('st-trend');
    const compare = isCompare();
    // 컨트롤 가시성: 비교 모드 → 지표 셀렉트, 단일 → 횟수/비율 토글
    $('st-metric').style.display = compare ? '' : 'none';
    $('st-valmode').style.display = compare ? 'none' : '';

    const pal = palette();
    let series, percent = false, legendItems;

    if (compare) {
      const field = state.dim; // line|type
      const groupMap = groupIdMap(field);
      const names = (field === 'line' ? state.data.lines : state.data.types);
      const metric = state.metric;
      percent = metric === 'successRate' || metric === 'failRate' || metric === 'changeRate';
      series = names.map((name, k) => {
        const bd = backupDaily(groupMap.get(name) || new Set());
        const values = bd.map(o => {
          if (metric === 'n') return o.n;
          if (metric === 's') return o.c + o.u;
          if (metric === 'c') return o.c;
          if (metric === 'f') return o.f;
          if (o.n === 0) return null; // 비율은 시도 없으면 공백
          if (metric === 'successRate') return rate(o.c + o.u, o.n);
          if (metric === 'failRate') return rate(o.f, o.n);
          if (metric === 'changeRate') return rate(o.c, o.n);
          return o.n;
        });
        // active: 기간 내 작업 시도가 있던 그룹 (성공 횟수처럼 값이 전부 0이어도 표시할 근거)
        return { key: 'g:' + name, label: name, color: PALETTE[k % PALETTE.length], values, active: bd.some(o => o.n > 0) };
      }).filter(s => percent || metric === 's'
        ? s.active                                  // 비율·성공 횟수: 시도가 있던 그룹은 0이어도 표시(최악 성과군 노출)
        : s.values.some(v => v != null && v > 0));  // 그 외 횟수: 활동이 있던 그룹만
      legendItems = series;
    } else {
      const bd = backupDaily(ids);
      if (state.valmode === 'rate') {
        percent = true;
        series = [
          { key: 'successRate', label: '성공률', color: pal.unchanged, values: bd.map(o => o.n ? rate(o.c + o.u, o.n) : null) },
          { key: 'changeRate', label: '변화율', color: pal.changed, values: bd.map(o => o.n ? rate(o.c, o.n) : null) },
          { key: 'failRate', label: '실패율', color: pal.failed, values: bd.map(o => o.n ? rate(o.f, o.n) : null) },
        ];
      } else {
        series = [
          { key: 'n', label: '작업 횟수', color: pal.primary, values: bd.map(o => o.n), width: 3 },
          { key: 's', label: '성공', color: '#8b5cf6', values: bd.map(o => o.c + o.u), dash: '2 3' },
          { key: 'c', label: '변화', color: pal.changed, values: bd.map(o => o.c) },
          { key: 'u', label: '변경없음', color: pal.unchanged, values: bd.map(o => o.u), dash: '5 4' },
          { key: 'f', label: '실패', color: pal.failed, values: bd.map(o => o.f) },
        ];
      }
      legendItems = series;
    }

    renderTrendLegend(legendItems);

    // 비율·성공 횟수는 값이 전부 0(예: 실패만 있던 기간)이어도 시도가 있었다면 데이터로 취급해 0으로 그린다.
    // 비교 모드는 위 필터가 이미 표시 대상 그룹만 남기므로 시리즈 유무로 판단.
    const hasData = compare
      ? series.length > 0
      : series.some(s => s.values.some(v => percent ? v != null : (v != null && v > 0)));
    if (!hasData) { emptyHost(host, '이 기간에 작업 이력이 없습니다.'); return; }
    lineChart(host, { series, percent, tipTitle: compare ? null : scopeName() });
  }

  function renderTrendLegend(items) {
    const host = $('st-trend-legend');
    host.innerHTML = items.map(s =>
      `<span class="st-legend-item ${state.hidden.has(s.key) ? 'off' : ''}" data-key="${esc(s.key)}">
        <span class="st-legend-swatch" style="background:${s.color}"></span>${esc(s.label)}</span>`).join('');
    host.querySelectorAll('.st-legend-item').forEach(elm => {
      elm.addEventListener('click', () => {
        const k = elm.getAttribute('data-key');
        if (state.hidden.has(k)) state.hidden.delete(k); else state.hidden.add(k);
        renderTrend(scopeIds());
      });
    });
  }

  function renderStack(ids) {
    const host = $('st-stack');
    const bd = backupDaily(ids);
    if (!bd.some(o => o.n > 0)) { emptyHost(host, '작업 이력이 없습니다.'); return; }
    const pal = palette();
    stackedChart(host, {
      stacks: [
        { key: 'c', label: '갱신', color: pal.changed, values: bd.map(o => o.c) },
        { key: 'u', label: '변경없음', color: pal.unchanged, values: bd.map(o => o.u) },
        { key: 'f', label: '실패', color: pal.failed, values: bd.map(o => o.f) },
        { key: 'p', label: '작업중', color: pal.inprogress, values: bd.map(o => o.p) },
      ],
    });
  }

  function renderTrans(ids) {
    const host = $('st-trans');
    const td = transDaily(ids);
    if (!td.some(o => o.on > 0 || o.off > 0)) { emptyHost(host, '전환 기록이 없습니다.'); return; }
    const pal = palette();
    divergingChart(host, {
      up: { label: '온라인 전환', color: pal.changed, values: td.map(o => o.on) },
      down: { label: '오프라인 전환', color: pal.failed, values: td.map(o => o.off) },
    });
  }

  // 다음/이전 페이저 (대시보드와 동일 동작). total===0 또는 1페이지면 비운다.
  function renderPager(hostId, page, pages, total, go) {
    const host = $(hostId);
    if (!host) return;
    if (total === 0 || pages <= 1) { host.innerHTML = ''; return; }
    host.innerHTML = `
      <button class="st-iconbtn" ${page <= 0 ? 'disabled' : ''} data-act="prev"><span class="material-symbols-outlined">chevron_left</span></button>
      <span>${page + 1} / ${pages} <span style="opacity:0.6;">(${total.toLocaleString()}개)</span></span>
      <button class="st-iconbtn" ${page >= pages - 1 ? 'disabled' : ''} data-act="next"><span class="material-symbols-outlined">chevron_right</span></button>`;
    const prev = host.querySelector('[data-act="prev"]');
    const next = host.querySelector('[data-act="next"]');
    if (prev) prev.addEventListener('click', () => { if (page > 0) go(page - 1); });
    if (next) next.addEventListener('click', () => { if (page < pages - 1) go(page + 1); });
  }

  // ── 그룹 상세 테이블 ──
  function renderBreakdown() {
    const table = $('st-breakdown');
    const titleEl = $('st-breakdown-title');
    let rows, isAsset = false, isLineType = false, field = null;

    if (state.dim === 'asset') {
      isAsset = true;
      titleEl.textContent = '자산별 상세';
      rows = state.data.assets.map(a => {
        const t = totals(new Set([a.id]));
        return { key: String(a.id), name: a.name, line: a.line, type: a.type, ...t };
      });
    } else {
      field = (state.dim === 'type') ? 'type' : 'line'; // all → 라인별 요약
      isLineType = true;
      titleEl.textContent = field === 'type' ? '타입별 상세' : '라인별 상세' + (state.dim === 'all' ? ' (요약)' : '');
      const gmap = groupIdMap(field);
      const names = field === 'type' ? state.data.types : state.data.lines;
      rows = names.map(name => {
        const ids = gmap.get(name) || new Set();
        const t = totals(ids);
        return { key: name, name, count: ids.size, ...t };
      });
    }

    // 정렬
    const k = state.sortKey;
    rows.sort((a, b) => {
      let av = a[k], bv = b[k];
      if (typeof av === 'string') return state.sortDir * av.localeCompare(bv, 'ko');
      return state.sortDir * ((av || 0) - (bv || 0));
    });

    const pal = palette();
    const ratioBar = (pct, color) =>
      `<span class="st-ratiobar"><span style="width:${Math.max(0, Math.min(100, pct))}%;background:${color}"></span></span> <span style="font-variant-numeric:tabular-nums;">${pct}%</span>`;

    const cols = isAsset
      ? [['name', '자산명', 'l'], ['line', '라인', 'l'], ['type', '타입', 'l'], ['n', '작업', 'r'], ['s', '성공', 'r'], ['c', '변화', 'r'], ['f', '실패', 'r'], ['successRate', '성공률', 'r'], ['on', '온라인전환', 'r'], ['off', '오프라인전환', 'r']]
      : [['name', field === 'type' ? '타입' : '라인', 'l'], ['count', '자산수', 'r'], ['n', '작업', 'r'], ['s', '성공', 'r'], ['c', '변화', 'r'], ['u', '변경없음', 'r'], ['f', '실패', 'r'], ['successRate', '성공률', 'r'], ['on', '온라인전환', 'r'], ['off', '오프라인전환', 'r']];

    const arrow = (key) => state.sortKey === key ? `<span class="sort-arrow material-symbols-outlined">${state.sortDir < 0 ? 'arrow_drop_down' : 'arrow_drop_up'}</span>` : '';
    const thead = `<thead><tr>${cols.map(([key, lbl, al]) =>
      `<th data-sort="${key}" style="text-align:${al === 'r' ? 'right' : 'left'}">${esc(lbl)}${arrow(key)}</th>`).join('')}</tr></thead>`;

    const cell = (r, key, al) => {
      let v;
      if (key === 'successRate') v = ratioBar(r.successRate, pal.unchanged);
      else if (key === 'name' || key === 'line' || key === 'type') v = esc(r[key] || '-');
      else v = (r[key] || 0).toLocaleString();
      return `<td style="text-align:${al === 'r' ? 'right' : 'left'}">${v}</td>`;
    };
    // 페이지네이션 (자산별 상세는 자산 수만큼 행이 많을 수 있음; 그룹 수가 적으면 페이저는 자동 숨김)
    const total = rows.length;
    const pages = Math.max(1, Math.ceil(total / BD_PAGE_SIZE));
    if (state.bdPage >= pages) state.bdPage = pages - 1;
    if (state.bdPage < 0) state.bdPage = 0;
    const pageRows = rows.slice(state.bdPage * BD_PAGE_SIZE, (state.bdPage + 1) * BD_PAGE_SIZE);

    const body = total
      ? `<tbody>${pageRows.map(r =>
        `<tr class="st-table-row-link" data-key="${esc(r.key)}">${cols.map(([key, , al]) => cell(r, key, al)).join('')}</tr>`).join('')}</tbody>`
      : `<tbody><tr><td colspan="${cols.length}" class="st-empty">데이터가 없습니다.</td></tr></tbody>`;

    table.innerHTML = thead + body;
    renderPager('st-breakdown-pager', state.bdPage, pages, total, (p) => { state.bdPage = p; renderBreakdown(); });

    // 헤더 정렬
    table.querySelectorAll('th[data-sort]').forEach(th => th.addEventListener('click', () => {
      const key = th.getAttribute('data-sort');
      if (state.sortKey === key) state.sortDir *= -1;
      else { state.sortKey = key; state.sortDir = (key === 'name' || key === 'line' || key === 'type') ? 1 : -1; }
      state.bdPage = 0; // 정렬 바뀌면 첫 페이지로
      renderBreakdown();
    }));
    // 행 클릭 → 드릴다운
    table.querySelectorAll('tr.st-table-row-link').forEach(tr => tr.addEventListener('click', () => {
      const key = tr.getAttribute('data-key');
      if (isAsset) { setDim('asset'); state.target = key; }
      else { setDim(field); state.target = key; }
      syncControls();
      renderAll();
    }));
  }

  // ──────────────────────────────────────────────────────────────────────────
  //  컨트롤 / 데이터 로드
  // ──────────────────────────────────────────────────────────────────────────

  function populateTarget() {
    const sel = $('st-target');
    const box = $('st-asset-box');
    if (state.dim === 'all') { sel.style.display = 'none'; box.style.display = 'none'; return; }
    if (state.dim === 'asset') {
      // 개별 → 직접 입력해 찾는 검색 콤보박스
      sel.style.display = 'none';
      box.style.display = '';
      $('st-asset-results').style.display = 'none';
      if (!state.meta.has(parseInt(state.target, 10))) state.target = defaultAsset();
      renderAssetCombo();
      return;
    }
    // 라인/타입 → 셀렉트(전체 비교 + 그룹들)
    box.style.display = 'none';
    sel.style.display = '';
    const names = state.dim === 'type' ? state.data.types : state.data.lines;
    sel.innerHTML = `<option value="">(전체 비교)</option>` + names.map(nm => `<option value="${esc(nm)}">${esc(nm)}</option>`).join('');
    sel.value = state.target;
    if (sel.value !== state.target) state.target = sel.value; // 값 보정(없으면 첫 옵션)
  }

  // 선택된 자산명을 콤보 입력칸에 반영
  function renderAssetCombo() {
    const input = $('st-asset-input');
    if (!input) return;
    const m = state.meta.get(parseInt(state.target, 10));
    input.value = m ? m.name : '';
  }

  // 개별 자산 검색 콤보박스: 입력하면 실시간 필터 드롭다운, 클릭/Enter 로 선택. 한 번만 바인딩.
  function bindAssetCombo() {
    const input = $('st-asset-input');
    const box = $('st-asset-results');
    if (!input || !box) return;
    let matches = [], active = -1;

    const paint = () => box.querySelectorAll('.st-combo-item').forEach(elm => elm.classList.toggle('active', +elm.dataset.idx === active));
    const scrollActive = () => { const elm = box.querySelector('.st-combo-item.active'); if (elm) elm.scrollIntoView({ block: 'nearest' }); };
    const pick = (id) => {
      state.target = String(id);
      state.hidden.clear();
      box.style.display = 'none';
      renderAssetCombo();
      renderAll();
    };
    const render = () => {
      const t = input.value.trim().toLowerCase();
      let list = state.data ? state.data.assets : [];
      if (t) list = list.filter(a => a.name.toLowerCase().includes(t)
        || (a.line || '').toLowerCase().includes(t) || (a.type || '').toLowerCase().includes(t));
      matches = list.slice(0, 60);
      active = -1;
      if (!matches.length) { box.innerHTML = `<div class="st-combo-empty">일치하는 자산이 없습니다.</div>`; box.style.display = 'block'; return; }
      box.innerHTML = matches.map((a, i) =>
        `<div class="st-combo-item${String(a.id) === state.target ? ' active' : ''}" data-idx="${i}" data-id="${a.id}">
          <span class="st-combo-name">${esc(a.name)}</span>
          ${a.line && a.line !== '라인없음' ? `<span class="st-combo-meta">${esc(a.line)}</span>` : ''}
          <span class="st-combo-meta">${esc(a.type)}</span></div>`).join('');
      box.style.display = 'block';
      box.querySelectorAll('.st-combo-item').forEach(elm =>
        elm.addEventListener('mousedown', (ev) => { ev.preventDefault(); pick(+elm.dataset.id); }));
    };

    input.addEventListener('focus', () => { input.select(); render(); });
    input.addEventListener('input', render);
    input.addEventListener('keydown', (ev) => {
      if (box.style.display === 'none' && ev.key === 'ArrowDown') { render(); return; }
      if (ev.key === 'ArrowDown') { ev.preventDefault(); active = Math.min(active + 1, matches.length - 1); paint(); scrollActive(); }
      else if (ev.key === 'ArrowUp') { ev.preventDefault(); active = Math.max(active - 1, 0); paint(); scrollActive(); }
      else if (ev.key === 'Enter') { ev.preventDefault(); const m = active >= 0 ? matches[active] : matches[0]; if (m) pick(m.id); }
      else if (ev.key === 'Escape') { box.style.display = 'none'; input.blur(); }
    });
    // 바깥 클릭 / 블러 시 닫고, 입력값을 실제 선택 자산명으로 복원
    document.addEventListener('click', (ev) => { if (!ev.target.closest('#st-asset-box')) { box.style.display = 'none'; renderAssetCombo(); } });
    input.addEventListener('blur', () => setTimeout(() => { box.style.display = 'none'; renderAssetCombo(); }, 150));
  }

  // dim 변경 시 기본 target 설정 + 숨김 시리즈 초기화
  function setDim(dim) {
    state.dim = dim;
    state.hidden.clear();
    state.sortKey = 'n'; state.sortDir = -1; // 차원 전환 시 정렬 초기화(문자열 키 잔존 방지)
    state.bdPage = 0;                         // 상세 페이지도 첫 장으로
    if (dim === 'all') state.target = '';
    else if (dim === 'asset') state.target = defaultAsset();
    else state.target = ''; // 라인/타입 → 전체 비교
  }

  // 활동이 가장 많은 자산을 개별 기본값으로
  function defaultAsset() {
    if (!state.data || !state.data.assets.length) return '';
    const cnt = new Map();
    for (const b of state.data.backup) cnt.set(b.a, (cnt.get(b.a) || 0) + b.n);
    let best = null, bestN = -1;
    for (const a of state.data.assets) {
      const c = cnt.get(a.id) || 0;
      if (c > bestN) { bestN = c; best = a.id; }
    }
    return String(best != null ? best : state.data.assets[0].id);
  }

  // 컨트롤 UI 를 state 에 맞게 동기화
  function syncControls() {
    document.querySelectorAll('#st-dim .st-seg-btn').forEach(b => b.classList.toggle('active', b.dataset.dim === state.dim));
    document.querySelectorAll('#st-valmode .st-seg-btn').forEach(b => b.classList.toggle('active', b.dataset.val === state.valmode));
    populateTarget();
    $('st-metric').value = state.metric;
  }

  function bindControls() {
    // 기간 프리셋
    document.querySelectorAll('#st-period .st-seg-btn').forEach(b => b.addEventListener('click', () => {
      const days = parseInt(b.dataset.days, 10);
      const end = new Date();
      const start = new Date(); start.setDate(start.getDate() - (days - 1));
      $('st-end').value = toISO(end);
      $('st-start').value = toISO(start);
      document.querySelectorAll('#st-period .st-seg-btn').forEach(x => x.classList.toggle('active', x === b));
      load();
    }));
    // 날짜 직접 변경 → 프리셋 해제 후 재조회
    const onDate = () => { document.querySelectorAll('#st-period .st-seg-btn').forEach(x => x.classList.remove('active')); load(); };
    $('st-start').addEventListener('change', onDate);
    $('st-end').addEventListener('change', onDate);

    // 집계 기준
    document.querySelectorAll('#st-dim .st-seg-btn').forEach(b => b.addEventListener('click', () => {
      setDim(b.dataset.dim);
      syncControls();
      renderAll();
    }));
    // 타깃 그룹(라인/타입 셀렉트)
    $('st-target').addEventListener('change', () => { state.target = $('st-target').value; state.hidden.clear(); renderAll(); });
    // 개별 자산 검색 콤보박스
    bindAssetCombo();
    // 비교 지표
    $('st-metric').addEventListener('change', () => { state.metric = $('st-metric').value; renderAll(); });
    // 값 종류
    document.querySelectorAll('#st-valmode .st-seg-btn').forEach(b => b.addEventListener('click', () => {
      state.valmode = b.dataset.val; state.hidden.clear();
      document.querySelectorAll('#st-valmode .st-seg-btn').forEach(x => x.classList.toggle('active', x === b));
      renderTrend(scopeIds());
    }));
    // CSV
    $('st-csv').addEventListener('click', exportCsv);

    // 리사이즈/테마 변경 시 차트 재렌더 (데이터 재조회 없음)
    let rz;
    window.addEventListener('resize', () => { clearTimeout(rz); rz = setTimeout(renderAll, 180); });
    window.addEventListener('twms-theme-changed', () => renderAll());
  }

  async function load() {
    const start = $('st-start').value, end = $('st-end').value;
    if (!start || !end) return;
    try {
      const res = await fetch(`/api/statistics?start=${encodeURIComponent(start)}&end=${encodeURIComponent(end)}`, { headers: { Accept: 'application/json' } });
      if (!res.ok) return;
      state.data = await res.json();
      state.meta = new Map(state.data.assets.map(a => [a.id, { name: a.name, line: a.line, type: a.type }]));
      // 개별 기본 타깃이 비어 있으면 채움(첫 로드 시 dim=asset 진입 대비)
      if (state.dim === 'asset' && !state.target) state.target = defaultAsset();
      state.bdPage = 0; // 기간 재조회 시 상세 첫 페이지로
      syncControls();
      renderAll();
    } catch (e) { /* 무시 */ }
  }

  function exportCsv() {
    if (!state.data) return;
    const ids = scopeIds();
    const bd = backupDaily(ids), td = transDaily(ids);
    const head = ['날짜', '작업횟수', '성공', '변화', '변경없음', '실패', '작업중', '성공률(%)', '온라인전환', '오프라인전환'];
    const lines = [head.join(',')];
    state.data.days.forEach((day, i) => {
      const o = bd[i], t = td[i];
      lines.push([day, o.n, o.c + o.u, o.c, o.u, o.f, o.p, (o.n ? rate(o.c + o.u, o.n) : 0), t.on, t.off].join(','));
    });
    const blob = new Blob(['﻿' + lines.join('\r\n')], { type: 'text/csv;charset=utf-8;' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    // 비교 모드(라인/타입 전체비교)의 CSV 는 그룹별 분해가 아닌 전체 합계이므로 라벨을 명확히 한다.
    const label = isCompare() ? '전체합계' : scopeName();
    a.download = `통계_${label}_${state.data.start}_${state.data.end}.csv`;
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(a.href), 1000);
  }

  document.addEventListener('DOMContentLoaded', async () => {
    if (window.Shell) await Shell.init({ active: 'statistics' });
    // 기본 기간: 최근 7일
    const end = new Date(), start = new Date(); start.setDate(start.getDate() - 6);
    $('st-end').value = toISO(end);
    $('st-start').value = toISO(start);
    bindControls();
    await load();
  });
})();
