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
    { key: 'layout', label: '레이아웃', icon: 'space_dashboard', href: '/layout', match: ['/layout'] },
    { key: 'history', label: '자산 통합조회', icon: 'inventory_2', href: '/history', match: ['/history'] },
    { key: 'admin', label: '관리', icon: 'settings', href: '/admin', match: ['/admin'], adminOnly: true },
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
      this.activeKey = (opts && opts.active) || 'overview';
      this._render(); // 데이터 없이 즉시 골격 렌더 (빠른 첫 페인트)
      this._bindTheme();
      await this._renderAuth();
      await this.refresh();
      // 30초 폴링 (Blazor 사이드바의 30s 타이머와 동일 주기)
      this.pollTimer = setInterval(() => this.refresh(), 30000);
      document.addEventListener('visibilitychange', () => { if (!document.hidden) this.refresh(); });
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

      const aside = document.createElement('aside');
      aside.className = 'dsp-sidebar';
      aside.innerHTML = `
        <a href="/overview" class="dsp-logo-area" style="display:flex;align-items:center;gap:10px;text-decoration:none;margin-bottom:8px;">
          <img id="dsp-logo" alt="logo" style="max-height:40px;max-width:100%;object-fit:contain;display:none;" />
          <div id="dsp-logo-text">
            <h1 class="font-display" style="font-weight:800;font-size:24px;color:var(--c-primary);margin:0;line-height:1;">TWMS</h1>
            <p style="font-size:12px;color:var(--c-on-surface-variant);opacity:0.8;margin:0;">Total Web Management</p>
          </div>
        </a>
        <nav id="dsp-nav" style="display:flex;flex-direction:column;gap:6px;"></nav>
        <div style="height:1px;background:var(--c-outline-variant);opacity:0.4;margin:6px 0;"></div>
        <div id="dsp-tree" class="dsp-tree"></div>
        <div id="dsp-mini-kpi" class="dsp-mini-kpi"></div>`;

      const header = document.createElement('header');
      header.className = 'dsp-header';
      header.innerHTML = `
        <div class="nm-inset" style="display:flex;align-items:center;gap:10px;padding:8px 16px;border-radius:9999px;width:min(420px,40vw);">
          <span class="material-symbols-outlined" style="color:var(--c-outline);">search</span>
          <input id="dsp-search" type="text" placeholder="자산 검색..." style="background:transparent;border:none;outline:none;width:100%;color:var(--c-on-surface);font-size:14px;" />
        </div>
        <div style="display:flex;align-items:center;gap:12px;">
          <button id="dsp-theme-toggle" class="dsp-iconbtn" title="테마 전환"><span class="material-symbols-outlined">dark_mode</span></button>
          <button class="dsp-iconbtn" title="알림"><span class="material-symbols-outlined">notifications</span></button>
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

      this._renderNav(false);
      this._syncThemeIcon();
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
      // 로고
      const logo = document.getElementById('dsp-logo');
      const logoText = document.getElementById('dsp-logo-text');
      if (data.logoUrl && logo) { logo.src = data.logoUrl; logo.style.display = 'block'; if (logoText) logoText.style.display = 'none'; }
      // 네비 (admin 반영)
      this._renderNav(!!data.isAdmin);
      // 미니 KPI
      const kpi = data.kpi || {};
      const mini = document.getElementById('dsp-mini-kpi');
      if (mini) {
        const items = [
          { dot: 'var(--c-primary)', v: kpi.total ?? 0, l: '전체' },
          { dot: 'var(--health-backedup)', v: kpi.backedUp ?? 0, l: '갱신' },
          { dot: 'var(--health-unchanged)', v: kpi.unchanged ?? 0, l: '유지' },
          { dot: 'var(--health-failed)', v: kpi.failed ?? 0, l: '주의' },
        ];
        mini.innerHTML = items.map(i => `<div class="dsp-mini-kpi-item">
          <span class="dsp-mini-kpi-dot" style="background:${i.dot};"></span>
          <span class="dsp-mini-kpi-value">${i.v}</span>
          <span class="dsp-mini-kpi-label">${i.l}</span></div>`).join('');
      }
      // 자산 트리
      this._renderTree(data.tree || []);
    },

    _renderTree(lines) {
      const host = document.getElementById('dsp-tree');
      if (!host) return;
      const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
      const dot = (c) => `<span class="dsp-status-dot" style="background:${HEALTH_COLOR[c] || c || 'var(--health-unknown)'};"></span>`;

      const leaf = (a) => `<a href="/assets/${a.assetId}" class="dsp-tree-row" title="${esc(a.displayName)}">
        <img class="dsp-tree-ico" src="${esc(a.icon)}" onerror="this.style.visibility='hidden'"/>
        <span class="dsp-tree-label">${esc(a.displayName)}</span>${dot(a.statusColor)}</a>`;

      const html = lines.map(line => {
        const lineId = 'L:' + line.lineName;
        const open = _treeState[lineId] !== undefined ? _treeState[lineId] : !!line.expanded;
        const plcs = (line.plcNodes || []).map(p => {
          const pid = 'P:' + p.plc.assetId;
          const popen = _treeState[pid] !== undefined ? _treeState[pid] : !!p.expanded;
          const hasKids = (p.children || []).length > 0;
          const kids = hasKids ? `<div class="dsp-tree-children" data-children="${pid}" style="display:${popen ? 'block' : 'none'};">${p.children.map(leaf).join('')}</div>` : '';
          const toggle = hasKids
            ? `<span class="dsp-tree-toggle material-symbols-outlined" data-toggle="${pid}">${popen ? 'arrow_drop_up' : 'arrow_drop_down'}</span>`
            : `<span class="dsp-tree-toggle"></span>`;
          return `<div class="dsp-tree-node">
            <div class="dsp-tree-row">${toggle}
              <a href="/assets/${p.plc.assetId}" class="dsp-tree-label" style="display:flex;align-items:center;gap:4px;text-decoration:none;color:inherit;overflow:hidden;">
                <img class="dsp-tree-ico" src="${esc(p.plc.icon)}" onerror="this.style.visibility='hidden'"/>
                <span class="dsp-tree-label">${esc(p.plc.displayName)}</span></a>${dot(p.plc.statusColor)}</div>${kids}</div>`;
        }).join('');
        const standalone = (line.standalone || []).map(leaf).join('');
        return `<div class="dsp-tree-line">
          <div class="dsp-tree-row line" data-toggle="${lineId}">
            <span class="dsp-tree-toggle material-symbols-outlined">${open ? 'arrow_drop_up' : 'arrow_drop_down'}</span>
            <span class="dsp-tree-aggdot" style="background:${line.aggColor || 'var(--health-unknown)'};"></span>
            <span class="dsp-tree-label">${esc(line.lineName)}</span></div>
          <div class="dsp-tree-children" data-children="${lineId}" style="display:${open ? 'block' : 'none'};">${plcs}${standalone}</div>
        </div>`;
      }).join('');
      host.innerHTML = html;

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
    // 로그인 안 했으면 "로그인" 버튼(클릭 시 /login), 했으면 사용자명 + 로그아웃 버튼.
    async _renderAuth() {
      const host = document.getElementById('dsp-auth');
      if (!host) return;
      let me = { authenticated: false };
      try { me = await fetch('/api/auth/me', { headers: { Accept: 'application/json' } }).then(r => r.json()); } catch (e) {}
      if (me && me.authenticated) {
        host.innerHTML = `<span style="font-size:13px;color:var(--c-on-surface-variant);max-width:120px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${(me.name || '')}</span>
          <button id="dsp-logout" class="dsp-iconbtn" title="로그아웃" style="color:var(--c-error);"><span class="material-symbols-outlined">logout</span></button>`;
        const out = document.getElementById('dsp-logout');
        if (out) out.addEventListener('click', async () => {
          try { await fetch('/api/auth/logout', { method: 'POST' }); } catch (e) {}
          location.href = '/';
        });
      } else {
        const ret = encodeURIComponent(location.pathname + location.search);
        host.innerHTML = `<a href="/login?returnUrl=${ret}" class="dsp-iconbtn" title="로그인" style="color:var(--c-primary);text-decoration:none;"><span class="material-symbols-outlined">login</span></a>`;
      }
    },

    /* ── 테마 토글 ── */
    _bindTheme() {
      const btn = document.getElementById('dsp-theme-toggle');
      if (btn) btn.addEventListener('click', () => {
        const dark = document.documentElement.classList.toggle('dark');
        try { localStorage.setItem('twms-theme', dark ? 'dark' : 'light'); } catch (e) {}
        this._syncThemeIcon();
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
