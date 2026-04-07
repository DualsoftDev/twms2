window.dashboardLayout = {
    _grid: null,
    _key: null,
    _snapshot: null,
    _prefsKey: null,
    _prefs: {},
    _prefsSnapshot: null,
    _htmlMins: null,
    _htmlDefaults: null,

    init(storageKey, editMode) {
        this._key = storageKey;
        this._prefsKey = storageKey.replace('layout', 'chartprefs');

        // 차트 타입 설정 로드
        try {
            const savedPrefs = localStorage.getItem(this._prefsKey);
            this._prefs = savedPrefs ? JSON.parse(savedPrefs) : {};
        } catch {
            this._prefs = {};
        }

        try {
            if (typeof GridStack === 'undefined') {
                console.error('dashboard-layout: GridStack is not defined. CDN may not have loaded.');
                return;
            }

            const container = document.querySelector('.grid-stack');
            if (!container) {
                console.error('dashboard-layout: .grid-stack element not found in DOM.');
                return;
            }

            this._grid = GridStack.init({
                column: 12,
                columnOpts: {
                    breakpoints: [{ w: 768, c: 1 }],
                    layout: 'compact',
                },
                cellHeight: 80,
                margin: 10,
                animate: false,
                float: false,
                disableDrag: !editMode,
                disableResize: !editMode,
            }, container);

            // Mark as initialized — CSS fallback (position:relative) is removed
            container.classList.add('gs-initialized');

            // init() 직후 HTML 기본 레이아웃 캡처 (load가 덮어쓰기 전)
            this._htmlMins = {};
            this._htmlDefaults = {};
            this._grid.engine.nodes.forEach(n => {
                if (n.id) {
                    this._htmlMins[n.id] = { minW: n.minW, minH: n.minH };
                    this._htmlDefaults[n.id] = { x: n.x, y: n.y, w: n.w, h: n.h, minW: n.minW, minH: n.minH };
                }
            });

            const saved = localStorage.getItem(storageKey);
            if (saved) {
                try {
                    this._grid.load(JSON.parse(saved));
                } catch (e) {
                    console.warn('dashboard-layout: failed to load saved layout, using defaults.', e);
                    localStorage.removeItem(storageKey);
                }
            }

            // load()가 덮어쓴 minW/minH를 HTML 기준으로 복원
            this._reapplyMinConstraints();

            this._grid.on('change', () => this._save());
            console.log('dashboard-layout: initialized with key=' + storageKey);
        } catch (e) {
            console.error('dashboard-layout: init failed:', e);
        }
    },

    // Called on every Blazor render — init if not yet done, otherwise check if positions are still valid
    ensure(storageKey, editMode) {
        if (!this._grid) {
            this.init(storageKey, editMode);
            return;
        }
        // If gridstack items lost their inline styles (e.g. Blazor re-render edge case), re-commit positions
        const firstItem = this._grid.el && this._grid.el.querySelector('.grid-stack-item');
        if (firstItem && !firstItem.style.top) {
            try {
                this._grid.engine.nodes.forEach(n => {
                    if (n.el) this._grid.update(n.el, { x: n.x, y: n.y, w: n.w, h: n.h });
                });
            } catch (e) {
                console.warn('dashboard-layout: reapply failed, re-initializing', e);
                this._grid.destroy(false);
                this._grid = null;
                this.init(storageKey, editMode);
            }
        }
    },

    // load() 후 minW/minH를 _htmlMins 기준으로 복원 (relayout 없이)
    _reapplyMinConstraints() {
        if (!this._grid || !this._htmlMins) return;
        this._grid.engine.nodes.forEach(n => {
            if (!n.id || !this._htmlMins[n.id]) return;
            const m = this._htmlMins[n.id];
            if (m.minW != null) {
                n.minW = m.minW;
                if (n.el) n.el.setAttribute('gs-min-w', m.minW);
            }
            if (m.minH != null) {
                n.minH = m.minH;
                if (n.el) n.el.setAttribute('gs-min-h', m.minH);
            }
        });
    },

    _save() {
        if (!this._grid || !this._key) return;
        // 모바일(768px 미만) 1-컬럼 상태에서는 저장 안 함 — 데스크톱 레이아웃 보호
        if (window.innerWidth < 768) return;
        localStorage.setItem(this._key, JSON.stringify(this._grid.save(false)));
    },

    _savePrefs() {
        if (!this._prefsKey) return;
        localStorage.setItem(this._prefsKey, JSON.stringify(this._prefs));
    },

    setEditMode(enabled) {
        if (!this._grid) return;

        if (enabled) {
            // Save snapshot before editing so we can undo
            this._snapshot = JSON.stringify(this._grid.save(false));
            this._prefsSnapshot = JSON.stringify(this._prefs);
        } else {
            // 적용 시 차트 설정도 저장
            this._savePrefs();
        }

        this._grid.enableMove(enabled);
        this._grid.enableResize(enabled);

        const el = this._grid.el;
        if (enabled) el.classList.add('edit-mode');
        else el.classList.remove('edit-mode');
    },

    // Undo changes made in current edit session (restore pre-edit state)
    undoChanges() {
        if (!this._grid || !this._snapshot) return;
        try {
            this._grid.load(JSON.parse(this._snapshot));
            this._reapplyMinConstraints();
            this._save();
        } catch (e) {
            console.warn('dashboard-layout: undo failed', e);
        }
        // 차트 설정도 되돌리기
        if (this._prefsSnapshot) {
            try {
                this._prefs = JSON.parse(this._prefsSnapshot);
                this._savePrefs();
            } catch {}
        }
    },

    // Restore factory default layout from init-time captured defaults
    resetToDefault() {
        if (!this._grid || !this._key) return;
        localStorage.removeItem(this._key);
        this._grid.engine.nodes.forEach(n => {
            if (n.id && this._htmlDefaults && this._htmlDefaults[n.id]) {
                const d = this._htmlDefaults[n.id];
                const opts = { x: d.x, y: d.y, w: d.w, h: d.h };
                if (d.minW != null) opts.minW = d.minW;
                if (d.minH != null) opts.minH = d.minH;
                if (n.el) this._grid.update(n.el, opts);
            }
        });
        this._grid.compact();
        // 차트 설정도 초기화
        this._prefs = {};
        if (this._prefsKey) localStorage.removeItem(this._prefsKey);
    },

    getPrefs() {
        return { ...this._prefs };
    },

    getChartPref(panelId) {
        return this._prefs[panelId] || 'bar';
    },

    setChartPref(panelId, chartType) {
        this._prefs[panelId] = chartType;
    },

    destroy() {
        if (this._grid) {
            // Hide container and footer immediately to prevent flash during navigation
            if (this._grid.el) this._grid.el.style.display = 'none';
            var footer = document.querySelector('.dashboard-footer');
            if (footer) footer.style.display = 'none';
            this._grid.destroy(false);
            this._grid = null;
        }
        // Clean up heatmap ResizeObserver
        if (this._heatmapRO) {
            this._heatmapRO.disconnect();
            this._heatmapRO = null;
        }
    }
};

/* ── Heatmap flexible tile fitting ── */
window.heatmapFit = {
    _ro: null,

    init() {
        const container = document.querySelector('.heatmap-container');
        if (!container) return;

        // Run once immediately
        this._fit(container);

        // Re-fit on resize
        if (this._ro) this._ro.disconnect();
        this._ro = new ResizeObserver(() => this._fit(container));
        this._ro.observe(container);
    },

    _fit(container) {
        const grid = container.querySelector('.heatmap-grid');
        if (!grid) return;

        const tiles = grid.querySelectorAll('.heatmap-tile');
        const count = tiles.length;
        if (count === 0) return;

        const style = getComputedStyle(container);
        const W = container.clientWidth
            - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
        const H = container.clientHeight
            - parseFloat(style.paddingTop) - parseFloat(style.paddingBottom);

        if (W <= 0 || H <= 0) return;

        const gap = 3;
        const size = this._calcTileSize(W, H, count, gap);

        grid.style.gridTemplateColumns = `repeat(auto-fill, minmax(${size}px, 1fr))`;
    },

    /** Binary-search the largest square tile size that fits all tiles without overflow */
    _calcTileSize(W, H, count, gap) {
        let lo = 6, hi = Math.min(W, H, 48);
        let best = lo;

        while (lo <= hi) {
            const mid = Math.floor((lo + hi) / 2);
            const cols = Math.max(1, Math.floor((W + gap) / (mid + gap)));
            const rows = Math.ceil(count / cols);
            const totalH = rows * (mid + gap) - gap;

            if (totalH <= H) {
                best = mid;
                lo = mid + 1;
            } else {
                hi = mid - 1;
            }
        }

        return Math.max(best, 6);
    },

    dispose() {
        if (this._ro) {
            this._ro.disconnect();
            this._ro = null;
        }
    }
};
