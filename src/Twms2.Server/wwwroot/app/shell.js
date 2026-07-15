/* ============================================================================
 * TWMS2.0 정적 페이지 공통 셸 — 사이드바(네비 + 실시간 자산트리 + 미니KPI) + 상단바.
 * DSPilot shell.js 패턴 이식. 각 정적 페이지는 <body> 첫머리에서
 *   Shell.init({ active: 'overview' })  를 호출한다.
 * 데이터: GET /api/nav  → 네비/트리/KPI/로고/admin여부.
 * 테마: localStorage 'twms-theme' (head 인라인 스니펫이 선적용, 여기선 토글만).
 * ==========================================================================*/
(function () {
  'use strict';

  const NAV_ITEMS = [
    { key: 'overview', label: '대시보드', icon: 'dashboard', href: '/', match: ['/', '/overview'] },
    { key: 'statistics', label: '통계', icon: 'monitoring', href: '/statistics', match: ['/statistics'] },
    { key: 'history', label: '자산 통합조회', icon: 'inventory_2', href: '/history', match: ['/history'] },
  ];

  const HEALTH_COLOR = {
    backedup: 'var(--health-backedup)', unchanged: 'var(--health-unchanged)',
    failed: 'var(--health-failed)', inprogress: 'var(--health-inprogress)', unknown: 'var(--health-unknown)',
  };

  let _treeState = {}; // lineName/plcId → expanded 유지

  const Shell = {
    activeKey: 'overview',
    pollTimer: null,

    async init(opts) {
      // 멱등 — settings 에 직접 이식된 관리 모듈들이 각자 Shell.init 을 호출해도
      // 셸은 한 번만 구성한다(2번째+ 호출은 무시). 모듈은 init 이후 bind()/load() 만 진행.
      if (this._inited) return;
      this._inited = true;
      this.activeKey = (opts && opts.active) || 'overview';
      // 임베드(iframe) 모드: 다른 페이지 안에 iframe 으로 끼워진 경우(현재 미사용 — 직접 이식으로 전환)
      // 사이드바/헤더/폴링/인증표시를 생략하고 페이지 본문(.dsp-page)만 노출한다.
      if (window.self !== window.top) { this.embedded = true; this._initEmbedded(); return; }
      this._applyNavCollapsed(); // 저장된 접힘 상태를 골격 생성 전 선적용 (로드 시 애니메이션 깜빡임 방지)
      this._render(); // 데이터 없이 즉시 골격 렌더 (빠른 첫 페인트)
      this._bindTheme();
      this._bindNavToggle();
      this._bindSearch();
      this._bindSysStatus();
      await this._renderAuth();
      await this.refresh();
      // 30초 폴링 (Blazor 사이드바의 30s 타이머와 동일 주기)
      this.pollTimer = setInterval(() => { if (!document.hidden) this.refresh(); }, 30000);
      document.addEventListener('visibilitychange', () => { if (!document.hidden) this.refresh(); });
    },

    /* ── 임베드 모드: 사이드바/헤더 없이 .dsp-page 본문만 (설정 페이지의 관리 탭 iframe 용) ── */
    _initEmbedded() {
      document.body.classList.add('dsp-embedded');
      if (!document.getElementById('dsp-embed-style')) {
        const s = document.createElement('style');
        s.id = 'dsp-embed-style';
        s.textContent = 'body.dsp-embedded{margin:0;background:var(--c-surface);}'
          + '.dsp-embedded .dsp-page{padding:18px 20px;max-width:none;}';
        document.head.appendChild(s);
      }
    },

    async refresh() {
      try {
        const res = await fetch('/api/nav', { headers: { 'Accept': 'application/json' } });
        if (!res.ok) return;
        const data = await res.json();
        this._renderData(data);
      } catch (e) { /* 사이드바가 깨지지 않도록 무시 */ }
    },

    /* ── 골격(셸 DOM) 생성 — 페이지 본문(.dsp-page)을 main 안으로 이동 ── */
    _render() {
      if (document.querySelector('.dsp-sidebar')) return;
      const page = document.querySelector('.dsp-page');
      const path = location.pathname.replace(/\/$/, '') || '/';
      const settingsActive = path.startsWith('/settings') || this.activeKey === 'settings';

      const aside = document.createElement('aside');
      aside.className = 'dsp-sidebar';
      aside.innerHTML = `
        <a href="/overview" class="dsp-logo-area" title="TWMS — Total Web Management System">
          <img id="dsp-logo" class="dsp-logo-img" alt="TWMS" src="${this._logoSrc()}" />
          <span id="dsp-logo-text" class="dsp-logo-text">
            <span class="dsp-logo-title">TWMS</span>
            <span class="dsp-logo-sub">Total Web Management System</span>
          </span>
        </a>
        <nav id="dsp-nav" style="display:flex;flex-direction:column;gap:6px;"></nav>
        <div style="height:1px;background:var(--c-outline-variant);opacity:0.4;margin:6px 0;"></div>
        <div id="dsp-tree" class="dsp-tree"></div>
        <div style="height:1px;background:var(--c-outline-variant);opacity:0.4;margin:6px 0;"></div>
        <a href="/settings" class="dsp-nav-link${settingsActive ? ' active' : ''}">
          <span class="material-symbols-outlined">settings</span><span>설정</span></a>`;

      const header = document.createElement('header');
      header.className = 'dsp-header';
      header.innerHTML = `
        <div id="dsp-hdr-left" style="display:flex;align-items:center;gap:12px;">
        <button id="dsp-nav-toggle" class="dsp-iconbtn" title="메뉴 접기/펼치기"><span class="material-symbols-outlined">menu_open</span></button>
        <div id="dsp-search-box" class="nm-inset" style="position:relative;display:flex;align-items:center;gap:10px;padding:8px 16px;border-radius:9999px;width:min(420px,40vw);">
          <span class="material-symbols-outlined" style="color:var(--c-outline);">search</span>
          <input id="dsp-search" type="text" placeholder="자산 검색..." autocomplete="off" style="background:transparent;border:none;outline:none;width:100%;color:var(--c-on-surface);font-size:14px;" />
          <div id="dsp-search-results" class="dsp-search-results" style="display:none;"></div>
        </div>
        </div>
        <div id="dsp-hdr-right" style="display:flex;align-items:center;gap:12px;">
          <button id="dsp-sys-status" type="button" class="nm-flat-sm" title="DEXA 서버 · 에이전트 상태 보기" style="padding:8px 16px;border-radius:12px;display:flex;align-items:center;gap:8px;background:var(--c-surface);border:none;cursor:pointer;">
            <span id="dsp-sys-led" class="w-2 h-2 rounded-full status-led" style="background:var(--health-unknown);"></span>
            <span id="dsp-sys-label" class="font-label-mono text-on-surface" style="font-size:12px;">DEXA …</span>
            <span class="material-symbols-outlined" style="font-size:16px;color:var(--c-on-surface-variant);">expand_more</span>
          </button>
          <button id="dsp-theme-toggle" class="dsp-iconbtn" title="테마 전환"><span class="material-symbols-outlined">dark_mode</span></button>
          <div id="dsp-auth" style="display:flex;align-items:center;gap:8px;"></div>
        </div>`;

      const main = document.createElement('main');
      main.className = 'dsp-main';
      const content = document.createElement('div');
      content.className = 'dsp-content';
      if (page) { page.parentNode.removeChild(page); content.appendChild(page); }
      main.appendChild(header);
      main.appendChild(content);

      document.body.insertBefore(aside, document.body.firstChild);
      document.body.appendChild(main);

      // 모바일 드로어 백드롭 — 좁은 화면에서 드로어가 열렸을 때만 CSS 로 노출. 클릭 시 닫힘.
      if (!document.getElementById('dsp-nav-backdrop')) {
        const backdrop = document.createElement('div');
        backdrop.id = 'dsp-nav-backdrop';
        backdrop.addEventListener('click', () => this._closeNav());
        document.body.appendChild(backdrop);
      }

      this._renderNav(false);
      this._syncThemeIcon();
    },

    /* ── 사이드바 브랜드(로고 마크 + 제목/부제) ──
     * 기본: 배경 제거한 TWMS 심볼 마크(좌) + 제목 "TWMS" / 부제 텍스트(우). 다크 테마에선 밝은 마크로 자동 교체.
     * 일반설정으로 마크(logoUrl)·제목(navTitle)·부제(navSubtitle)를 각각 변경 가능. 미설정·삭제 시 기본값으로 복원. */
    _logoSrc() {
      if (this._customLogo) return this._customLogo;
      return document.documentElement.classList.contains('dark')
        ? '/app/twms-mark-dark.png' : '/app/twms-mark.png';
    },
    _syncLogo() {
      const img = document.getElementById('dsp-logo');
      if (img) img.src = this._logoSrc();
    },
    _syncBrand(title, sub) {
      const t = document.querySelector('.dsp-logo-title');
      const s = document.querySelector('.dsp-logo-sub');
      if (t) t.textContent = (title != null && String(title).trim()) ? title : 'TWMS';
      if (s) {
        const v = (sub != null ? String(sub) : 'Total Web Management System');
        s.textContent = v;
        s.style.display = v ? '' : 'none';
      }
    },

    _renderNav(isAdmin) {
      const nav = document.getElementById('dsp-nav');
      if (!nav) return;
      const path = location.pathname.replace(/\/$/, '') || '/';
      nav.innerHTML = NAV_ITEMS
        .filter(it => !it.adminOnly || isAdmin)
        .map(it => {
          const active = it.match.some(m => (m === '/' ? path === '/' : path.startsWith(m))) || it.key === this.activeKey;
          return `<a href="${it.href}" class="dsp-nav-link${active ? ' active' : ''}">
            <span class="material-symbols-outlined">${it.icon}</span><span>${it.label}</span></a>`;
        }).join('');
    },

    _renderData(data) {
      // 로고 마크 + 제목/부제 — 일반설정 값으로 헤더 갱신. 미설정(또는 삭제) 시 기본값으로 복원.
      this._customLogo = data.logoUrl || null;
      this._syncLogo();
      this._syncBrand(data.navTitle, data.navSubtitle);
      // 네비 (admin 반영)
      this.isAdmin = !!data.isAdmin;
      this._renderNav(this.isAdmin);
      document.dispatchEvent(new CustomEvent('shell:auth', { detail: { isAdmin: this.isAdmin, authenticated: !!this.isAuthenticated } }));
      // DEXA 연결 상태
      const led = document.getElementById('dsp-sys-led');
      const label = document.getElementById('dsp-sys-label');
      if (led && label) {
        const online = !!data.dexaOnline;
        led.style.background = online ? 'var(--health-backedup)' : 'var(--health-failed)';
        label.textContent = online ? 'DEXA 정상' : 'DEXA 연결 끊김';
      }
      // 자산 트리
      this._renderTree(data.tree || []);
      // 헤더 검색 자동완성용 자산 인덱스 (트리 평탄화)
      this._buildAssetIndex(data.tree || []);
    },

    /* ── 트리 → 검색용 평탄 자산 목록 ── */
    _buildAssetIndex(lines) {
      const out = [];
      const push = (a, lineName) => {
        if (!a || a.assetId == null) return;
        out.push({ assetId: a.assetId, name: a.displayName || '', icon: a.icon || '', statusColor: a.statusColor, health: a.health, healthLabel: a.healthLabel, offline: !!a.offline, lineName: lineName || '' });
      };
      (lines || []).forEach(line => {
        const ln = line.lineName || '';
        (line.plcNodes || []).forEach(p => {
          push(p.plc, ln);
          (p.children || []).forEach(c => push(c, ln));
        });
        (line.standalone || []).forEach(s => push(s, ln));
      });
      this._assets = out;
    },

    _renderTree(lines) {
      const host = document.getElementById('dsp-tree');
      if (!host) return;
      const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
      // 현재 보고 있는 자산(/assets/{id}) — 트리에서 강조
      const curId = (location.pathname.match(/^\/assets\/(\d+)/) || [])[1] || null;

      // 현재 자산의 조상(라인·PLC)을 최초 1회 자동 펼침 — 이후 사용자가 직접 접으면 그 상태 유지
      if (curId && this._treeRevealedFor !== curId) {
        this._treeRevealedFor = curId;
        lines.forEach(line => {
          const inPlc = (line.plcNodes || []).some(p =>
            String(p.plc.assetId) === curId || (p.children || []).some(c => String(c.assetId) === curId));
          const inStandalone = (line.standalone || []).some(a => String(a.assetId) === curId);
          if (!inPlc && !inStandalone) return;
          _treeState['L:' + line.lineName] = true;
          (line.plcNodes || []).forEach(p => {
            if ((p.children || []).some(c => String(c.assetId) === curId)) _treeState['P:' + p.plc.assetId] = true;
          });
        });
      }

      // 상태 LED 점: 색상 글로우 + 표면 링. 실패/작업중은 펄스, 오프라인은 속 빈 링.
      const dotHtml = (a) => {
        const color = a.statusColor || HEALTH_COLOR[a.health] || 'var(--health-unknown)';
        const off = !!a.offline;
        const pulse = !off && (a.health === 'failed' || a.health === 'inprogress');
        const cls = 'dsp-status-dot' + (off ? ' is-offline' : '') + (pulse ? ' pulse' : '');
        return `<span class="${cls}" style="color:${color};background:${color};"></span>`;
      };
      // 행 툴팁: 자산명 · 상태(· 오프라인)
      const rowTitle = (a) => esc(a.displayName) + (a.healthLabel ? ' · ' + esc(a.healthLabel) : '') + (a.offline ? ' · 오프라인' : '');
      // 현재 자산 강조 + 오프라인 표시용 행 클래스
      const stateCls = (a) => (curId && String(a.assetId) === curId ? ' is-active' : '') + (a.offline ? ' is-offline' : '');

      const leaf = (a) => `<a href="/assets/${a.assetId}" class="dsp-tree-row leaf${stateCls(a)}" title="${rowTitle(a)}">
        <span class="dsp-tree-toggle"></span>
        <img class="dsp-tree-ico" src="${esc(a.icon)}" onerror="this.style.visibility='hidden'"/>
        <span class="dsp-tree-label">${esc(a.displayName)}</span>${dotHtml(a)}</a>`;

      const html = lines.map(line => {
        const lineId = 'L:' + line.lineName;
        const open = _treeState[lineId] !== undefined ? _treeState[lineId] : !!line.expanded;
        const aggColor = line.aggColor || 'var(--health-unknown)';
        // 라인에 실패/작업중이 있으면 집계 점도 펄스
        const aggPulse = (line.failed > 0 || line.inProgress > 0) ? ' pulse' : '';
        // 접힘 여부와 무관하게 라인 단위 문제 수를 배지로 노출 (문제 없으면 표시 안 함)
        const badges = [];
        if (line.failed > 0) badges.push(`<span class="dsp-tree-badge fail" title="작업 실패 ${line.failed}건">${line.failed}</span>`);
        if (line.inProgress > 0) badges.push(`<span class="dsp-tree-badge prog" title="작업중 ${line.inProgress}건">${line.inProgress}</span>`);
        if (line.offline > 0) badges.push(`<span class="dsp-tree-badge off" title="오프라인 ${line.offline}대">${line.offline}</span>`);
        const plcs = (line.plcNodes || []).map(p => {
          const pid = 'P:' + p.plc.assetId;
          const popen = _treeState[pid] !== undefined ? _treeState[pid] : !!p.expanded;
          const hasKids = (p.children || []).length > 0;
          const kids = hasKids ? `<div class="dsp-tree-children" data-children="${pid}" style="display:${popen ? 'block' : 'none'};">${p.children.map(leaf).join('')}</div>` : '';
          const toggle = hasKids
            ? `<span class="dsp-tree-toggle material-symbols-outlined" data-toggle="${pid}">${popen ? 'arrow_drop_up' : 'arrow_drop_down'}</span>`
            : `<span class="dsp-tree-toggle"></span>`;
          return `<div class="dsp-tree-node">
            <div class="dsp-tree-row${stateCls(p.plc)}" title="${rowTitle(p.plc)}">${toggle}
              <a href="/assets/${p.plc.assetId}" class="dsp-tree-label" style="display:flex;align-items:center;gap:4px;text-decoration:none;color:inherit;overflow:hidden;">
                <img class="dsp-tree-ico" src="${esc(p.plc.icon)}" onerror="this.style.visibility='hidden'"/>
                <span class="dsp-tree-label">${esc(p.plc.displayName)}</span></a>${dotHtml(p.plc)}</div>${kids}</div>`;
        }).join('');
        const standalone = (line.standalone || []).map(leaf).join('');
        return `<div class="dsp-tree-line">
          <div class="dsp-tree-row line" data-toggle="${lineId}">
            <span class="dsp-tree-toggle material-symbols-outlined">${open ? 'arrow_drop_up' : 'arrow_drop_down'}</span>
            <span class="dsp-tree-aggdot${aggPulse}" style="color:${aggColor};background:${aggColor};"></span>
            <span class="dsp-tree-label">${esc(line.lineName)}</span>${badges.join('')}</div>
          <div class="dsp-tree-children" data-children="${lineId}" style="display:${open ? 'block' : 'none'};">${plcs}${standalone}</div>
        </div>`;
      }).join('');
      host.innerHTML = html;

      // 최초 렌더 시 현재 자산 행이 사이드바 스크롤 밖이면 보이게 이동 (30s 폴링 재렌더에선 건드리지 않음)
      if (curId && !this._treeScrolledFor) {
        const cur = host.querySelector('.dsp-tree-row.is-active');
        if (cur) { this._treeScrolledFor = curId; cur.scrollIntoView({ block: 'nearest' }); }
      }

      host.querySelectorAll('[data-toggle]').forEach(el => {
        el.addEventListener('click', (ev) => {
          if (ev.target.closest('a')) return; // 링크 클릭은 통과
          const id = el.getAttribute('data-toggle');
          // 자식 컨테이너는 DOM 관계로 탐색 (속성 선택자는 한글/':' 키에서 깨짐)
          const container = el.closest('.dsp-tree-node') || el.closest('.dsp-tree-line');
          const body = container && container.querySelector(':scope > .dsp-tree-children');
          if (!body) return;
          const nowOpen = body.style.display === 'none';
          body.style.display = nowOpen ? 'block' : 'none';
          _treeState[id] = nowOpen;
          const ico = el.classList.contains('dsp-tree-toggle') ? el : el.querySelector('.dsp-tree-toggle');
          if (ico) ico.textContent = nowOpen ? 'arrow_drop_up' : 'arrow_drop_down';
        });
      });
    },

    /* ── 로그인/로그아웃 토글 ── */
    // 헤더: 로그인 안 했으면 "로그인" 아이콘(클릭 시 /login), 했으면 사용자 칩 + 로그아웃 버튼.
    // 사이드바 '설정' 링크는 항상 /settings 로 고정 — 비로그인 접근 시 서버 게이트가 /login 으로 보냄.
    async _renderAuth() {
      const host = document.getElementById('dsp-auth');
      if (!host) return;
      const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
      const ret = encodeURIComponent(location.pathname + location.search);
      let me = { authenticated: false };
      try { me = await fetch('/api/auth/me', { headers: { Accept: 'application/json' } }).then(r => r.json()); } catch (e) {}
      this.isAuthenticated = !!(me && me.authenticated);

      if (me && me.authenticated) {
        // 헤더: 아이콘 + 아이디를 하나의 칩으로 묶어 표시 (설정 기어 제거)
        host.innerHTML = `<div class="nm-inset dsp-user-chip" title="${esc(me.name || '')}" style="display:flex;align-items:center;gap:8px;padding:6px 14px;border-radius:9999px;">
            <span class="material-symbols-outlined" style="font-size:20px;color:var(--c-primary);">account_circle</span>
            <span style="font-size:13px;font-weight:600;color:var(--c-on-surface);max-width:140px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${esc(me.name || '')}</span>
          </div>
          <button id="dsp-logout" class="dsp-iconbtn" title="로그아웃" style="color:var(--c-error);"><span class="material-symbols-outlined">logout</span></button>`;
        const out = document.getElementById('dsp-logout');
        if (out) out.addEventListener('click', async () => {
          try { await fetch('/api/auth/logout', { method: 'POST' }); } catch (e) {}
          location.href = '/';
        });
      } else {
        // 헤더: 로그인 아이콘
        host.innerHTML = `<a href="/login?returnUrl=${ret}" class="dsp-iconbtn" title="로그인" style="color:var(--c-primary);text-decoration:none;"><span class="material-symbols-outlined">login</span></a>`;
      }
    },

    /* ── 헤더 자산 검색: 입력 중 실시간 자동완성 드롭다운 ──
     * - 자산 클릭 → 자산 상세(/assets/{id})
     * - "전체 결과 보기" 또는 Enter → 자산 통합조회(/history?tab=0&q=)
     * - ↑/↓ 로 항목 이동, Esc 로 닫기 */
    _bindSearch() {
      const input = document.getElementById('dsp-search');
      const box = document.getElementById('dsp-search-results');
      if (!input || !box) return;
      this._assets = this._assets || [];
      let activeIdx = -1;   // -1 = "전체 결과 보기" 행
      let matches = [];

      const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
      const goAll = () => {
        const term = input.value.trim();
        if (!term) return;
        location.href = '/history?tab=0&q=' + encodeURIComponent(term);
      };
      const close = () => { box.style.display = 'none'; box.innerHTML = ''; activeIdx = -1; };
      // 검색 결과에도 상태 LED 점 (트리와 동일 규칙)
      const srDot = (a) => {
        const color = a.statusColor || 'var(--health-unknown)';
        const off = !!a.offline;
        const pulse = !off && (a.health === 'failed' || a.health === 'inprogress');
        const t = (a.healthLabel || '') + (off ? ' · 오프라인' : '');
        return `<span class="dsp-status-dot${off ? ' is-offline' : ''}${pulse ? ' pulse' : ''}" style="color:${color};background:${color};" title="${esc(t)}"></span>`;
      };

      const render = () => {
        const term = input.value.trim().toLowerCase();
        if (!term) { close(); return; }
        matches = this._assets
          .filter(a => a.name.toLowerCase().includes(term))
          .slice(0, 8);
        const rows = matches.map((a, i) => `
          <a href="/assets/${a.assetId}" class="dsp-sr-item${i === activeIdx ? ' active' : ''}" data-idx="${i}">
            <img class="dsp-sr-ico" src="${esc(a.icon)}" onerror="this.style.visibility='hidden'"/>
            <span class="dsp-sr-name">${esc(a.name)}</span>
            ${a.lineName ? `<span class="dsp-sr-line">${esc(a.lineName)}</span>` : ''}${srDot(a)}
          </a>`).join('');
        const empty = matches.length ? '' : `<div class="dsp-sr-empty">일치하는 자산 없음</div>`;
        box.innerHTML = rows + empty +
          `<div class="dsp-sr-all${activeIdx === -1 ? ' active' : ''}" data-all="1">
            <span class="material-symbols-outlined">manage_search</span>'${esc(input.value.trim())}' 전체 결과 보기</div>`;
        box.style.display = 'block';
        // 마우스 호버 시 활성 인덱스 동기화
        box.querySelectorAll('.dsp-sr-item').forEach(el =>
          el.addEventListener('mousemove', () => { activeIdx = +el.getAttribute('data-idx'); paint(); }));
        const all = box.querySelector('.dsp-sr-all');
        if (all) {
          all.addEventListener('mousemove', () => { activeIdx = -1; paint(); });
          all.addEventListener('click', goAll);
        }
      };
      // 활성 표시만 갱신 (재렌더 없이)
      const paint = () => {
        box.querySelectorAll('.dsp-sr-item').forEach(el =>
          el.classList.toggle('active', +el.getAttribute('data-idx') === activeIdx));
        const all = box.querySelector('.dsp-sr-all');
        if (all) all.classList.toggle('active', activeIdx === -1);
      };

      input.addEventListener('input', () => { activeIdx = -1; render(); });
      input.addEventListener('focus', () => { if (input.value.trim()) render(); });
      input.addEventListener('keydown', (ev) => {
        if (ev.key === 'ArrowDown') { ev.preventDefault(); activeIdx = Math.min(activeIdx + 1, matches.length - 1); paint(); }
        else if (ev.key === 'ArrowUp') { ev.preventDefault(); activeIdx = Math.max(activeIdx - 1, -1); paint(); }
        else if (ev.key === 'Enter') {
          ev.preventDefault();
          if (activeIdx >= 0 && matches[activeIdx]) location.href = '/assets/' + matches[activeIdx].assetId;
          else goAll();
        } else if (ev.key === 'Escape') { close(); }
      });
      // 바깥 클릭 시 닫기
      document.addEventListener('click', (ev) => { if (!ev.target.closest('#dsp-search-box')) close(); });
    },

    /* ── DEXA 상태 배지: 클릭 시 서버/에이전트 상태 팝오버 ── */
    _bindSysStatus() {
      const btn = document.getElementById('dsp-sys-status');
      if (!btn) return;
      btn.addEventListener('click', (ev) => {
        ev.stopPropagation();
        if (document.getElementById('dsp-sys-pop')) { this._closeSysPop(); return; }
        this._openSysPop(btn);
      });
    },

    _closeSysPop() {
      const pop = document.getElementById('dsp-sys-pop');
      if (pop) pop.remove();
      if (this._sysPopOutside) { document.removeEventListener('click', this._sysPopOutside); this._sysPopOutside = null; }
    },

    async _openSysPop(anchor) {
      const pop = document.createElement('div');
      pop.id = 'dsp-sys-pop';
      pop.className = 'nm-flat';
      const r = anchor.getBoundingClientRect();
      pop.style.cssText = `position:fixed;top:${Math.round(r.bottom + 8)}px;right:${Math.round(window.innerWidth - r.right)}px;`
        + `z-index:200;width:min(340px,92vw);max-height:70vh;overflow:auto;background:var(--c-surface-container);`
        + `border-radius:16px;padding:16px;`;
      pop.innerHTML = `<div style="display:flex;align-items:center;gap:8px;color:var(--c-on-surface-variant);font-size:13px;">
        <span class="material-symbols-outlined" style="font-size:18px;">sync</span> 상태 확인 중…</div>`;
      document.body.appendChild(pop);

      // 바깥 클릭 시 닫기 (배지 자체 클릭은 토글 핸들러가 처리)
      this._sysPopOutside = (e) => { if (!e.target.closest('#dsp-sys-pop') && !e.target.closest('#dsp-sys-status')) this._closeSysPop(); };
      document.addEventListener('click', this._sysPopOutside);

      try {
        const data = await fetch('/api/nav/dexa-status', { headers: { Accept: 'application/json' } }).then(r => r.json());
        if (document.getElementById('dsp-sys-pop')) pop.innerHTML = this._sysPopHtml(data);
      } catch (e) {
        pop.innerHTML = `<div style="color:var(--c-error);font-size:13px;">상태 정보를 불러오지 못했습니다.</div>`;
      }
    },

    _sysPopHtml(d) {
      const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
      // online = DEXA 서버 프로세스와의 실시간 연결, dbOnline = DEXA DB 읽기 가능 (별개로 표시)
      const online = !!d.online;
      const dbOnline = !!d.dbOnline;
      const dotC = online ? 'var(--health-backedup)' : 'var(--health-failed)';
      const fmt = (t) => {
        if (!t) return '—';
        const dt = new Date(t);
        if (isNaN(dt)) return '—';
        const p = (n) => String(n).padStart(2, '0');
        return `${dt.getFullYear()}-${p(dt.getMonth() + 1)}-${p(dt.getDate())} ${p(dt.getHours())}:${p(dt.getMinutes())}`;
      };

      // ── 서버 상태 ──
      const server = `
        <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:12px;">
          <div style="display:flex;align-items:center;gap:8px;">
            <span class="status-led" style="width:10px;height:10px;border-radius:9999px;display:inline-block;background:${dotC};"></span>
            <span class="font-label-mono" style="font-size:14px;color:var(--c-on-surface);">DEXA 서버 ${online ? '정상' : '연결 끊김'}</span>
          </div>
        </div>
        <div style="display:grid;grid-template-columns:auto 1fr;gap:4px 12px;font-size:12px;color:var(--c-on-surface-variant);margin-bottom:14px;">
          <span>DB (${esc(d.provider)})</span><span style="color:${dbOnline ? 'var(--c-on-surface)' : 'var(--c-error)'};text-align:right;">${dbOnline ? '연결됨' : '연결 실패'}</span>
        </div>`;

      // ── 연결된 에이전트 목록 ──
      const agents = d.agents || [];
      let agentBody;
      if (!online) {
        agentBody = `<div style="font-size:12px;color:var(--c-on-surface-variant);padding:8px 0;">DEXA 서버에 연결할 수 없습니다.</div>`;
      } else if (agents.length === 0) {
        agentBody = `<div style="font-size:12px;color:var(--c-on-surface-variant);padding:8px 0;">연결된 에이전트가 없습니다.</div>`;
      } else {
        agentBody = agents.map(a => `<div style="display:flex;align-items:center;gap:8px;padding:6px 0;border-top:1px solid var(--c-outline-variant);">
            <span class="status-led" style="width:8px;height:8px;border-radius:9999px;flex:none;background:var(--health-backedup);"></span>
            <div style="min-width:0;flex:1;">
              <div style="font-size:13px;color:var(--c-on-surface);overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${esc(a.name) || '(이름없음)'}</div>
              <div style="font-size:11px;color:var(--c-on-surface-variant);">${esc(a.ip) || '—'}${a.swVersion ? ' · v' + esc(a.swVersion) : ''}</div>
            </div>
            <div style="font-size:11px;color:var(--c-on-surface-variant);text-align:right;flex:none;">접속 ${fmt(a.connected)}</div>
          </div>`).join('');
      }

      const head = `<div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:4px;">
          <span style="font-size:12px;font-weight:600;color:var(--c-on-surface);">연결된 에이전트</span>
          <span style="font-size:11px;color:var(--c-on-surface-variant);">${online ? `${d.agentCount}개` : ''}</span>
        </div>`;

      return server + head + agentBody;
    },

    /* ── 네비 사이드바 접기/펼치기 ── */
    // 저장된 상태를 body 클래스로 선반영. _render() 가 골격을 만들기 전에 호출되어야
    // 사이드바가 처음부터 접힌 상태로 그려져 로드 시 슬라이드 애니메이션이 튀지 않는다.
    // 좁은 화면(≤1024px) = 드로어 모드. 이 폭 이하에선 사이드바가 본문을 밀지 않고 위에 겹친다.
    _isNarrow() {
      try { return window.matchMedia('(max-width: 1024px)').matches; } catch (e) { return false; }
    },
    _applyNavCollapsed() {
      let collapsed = false;
      try { collapsed = localStorage.getItem('twms-nav-collapsed') === '1'; } catch (e) {}
      // 드로어 모드에선 저장값과 무관하게 항상 닫힌 상태로 시작(첫 화면이 사이드바로 가려지지 않게).
      if (this._isNarrow()) collapsed = true;
      document.body.classList.toggle('nav-collapsed', collapsed);
    },
    _syncNavToggleIcon() {
      const btn = document.getElementById('dsp-nav-toggle');
      if (!btn) return;
      const collapsed = document.body.classList.contains('nav-collapsed');
      const ico = btn.querySelector('.material-symbols-outlined');
      if (ico) ico.textContent = collapsed ? 'menu' : 'menu_open';
    },
    // 드로어 닫기(백드롭/링크 클릭). 드로어 모드에선 데스크톱 접힘 설정을 덮어쓰지 않는다.
    _closeNav() {
      document.body.classList.add('nav-collapsed');
      this._syncNavToggleIcon();
    },
    _bindNavToggle() {
      const btn = document.getElementById('dsp-nav-toggle');
      if (!btn) return;
      this._syncNavToggleIcon();
      btn.addEventListener('click', () => {
        const collapsed = document.body.classList.toggle('nav-collapsed');
        // 드로어 모드의 임시 열림/닫힘은 저장하지 않음(데스크톱 접힘 설정 보존).
        if (!this._isNarrow()) {
          try { localStorage.setItem('twms-nav-collapsed', collapsed ? '1' : '0'); } catch (e) {}
        }
        this._syncNavToggleIcon();
      });
      // 좁은 화면 진입 시 사이드바 링크를 누르면 페이지 전환되며 드로어는 새 페이지에서 닫힌 채 시작.
      // 데스크톱↔드로어 폭 전환 시 상태 정리.
      try {
        const mq = window.matchMedia('(max-width: 1024px)');
        const onChange = (e) => {
          if (e.matches) {
            document.body.classList.add('nav-collapsed'); // 드로어로 전환 → 닫고 시작
          } else {
            let collapsed = false;
            try { collapsed = localStorage.getItem('twms-nav-collapsed') === '1'; } catch (_) {}
            document.body.classList.toggle('nav-collapsed', collapsed); // 데스크톱 설정 복원
          }
          this._syncNavToggleIcon();
        };
        if (mq.addEventListener) mq.addEventListener('change', onChange);
        else if (mq.addListener) mq.addListener(onChange);
      } catch (e) {}
    },

    /* ── 테마 토글 ── */
    _bindTheme() {
      const btn = document.getElementById('dsp-theme-toggle');
      if (btn) btn.addEventListener('click', () => {
        const dark = document.documentElement.classList.toggle('dark');
        try { localStorage.setItem('twms-theme', dark ? 'dark' : 'light'); } catch (e) {}
        this._syncThemeIcon();
        this._syncLogo();
        window.dispatchEvent(new CustomEvent('twms-theme-changed', { detail: { dark } }));
      });
    },
    _syncThemeIcon() {
      const btn = document.getElementById('dsp-theme-toggle');
      if (!btn) return;
      const dark = document.documentElement.classList.contains('dark');
      const ico = btn.querySelector('.material-symbols-outlined');
      if (ico) ico.textContent = dark ? 'light_mode' : 'dark_mode';
    },

    toast(msg) {
      let t = document.querySelector('.dsp-toast');
      if (!t) { t = document.createElement('div'); t.className = 'dsp-toast'; document.body.appendChild(t); }
      t.textContent = msg; t.classList.add('show');
      clearTimeout(t._timer); t._timer = setTimeout(() => t.classList.remove('show'), 2500);
    },
  };

  window.Shell = Shell;
})();
