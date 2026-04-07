// ── 공통 유틸 ──
const _font = 'Pretendard, -apple-system, BlinkMacSystemFont, sans-serif';

function _palette(isDark) {
    return {
        text:      isDark ? '#e2e8f0' : '#2d3748',
        textSub:   isDark ? '#a0aec0' : '#718096',
        primary:   '#8E8CD8',
        success:   isDark ? '#7DCBA4' : '#65B991',
        error:     isDark ? '#F09595' : '#E67E7E',
        info:      isDark ? '#85B5E8' : '#6BA0DE',
        warning:   isDark ? '#fbbf24' : '#f59e0b',
        muted:     isDark ? '#4a5568' : '#94a3b8',
        surface:   isDark ? '#26292e' : '#e0e5ec',
        tooltipBg: isDark ? 'rgba(30,32,38,0.95)' : 'rgba(255,255,255,0.95)',
        tooltipBd: isDark ? 'rgba(50,55,62,0.6)' : 'rgba(163,177,198,0.4)',
    };
}

function _tooltip(p) {
    return {
        trigger: 'item',
        triggerOn: 'mousemove',
        backgroundColor: p.tooltipBg,
        borderColor: p.tooltipBd,
        borderWidth: 1,
        textStyle: { color: p.text, fontFamily: _font, fontSize: 12 },
        padding: [8, 12],
    };
}

function _initChart(instances, elementId, data, isDark, setOptionFn) {
    _disposeChart(instances, elementId);
    const el = document.getElementById(elementId);
    if (!el) return;
    const chart = echarts.init(el, null, { renderer: 'svg' });
    instances[elementId] = chart;
    setOptionFn(chart, data, isDark);
    const ro = new ResizeObserver(() => chart.resize());
    ro.observe(el);
    chart._ro = ro;
}

function _updateChart(instances, elementId, data, isDark, setOptionFn) {
    const chart = instances[elementId];
    if (!chart) {
        _initChart(instances, elementId, data, isDark, setOptionFn);
        return;
    }
    setOptionFn(chart, data, isDark);
}

function _disposeChart(instances, elementId) {
    const chart = instances[elementId];
    if (chart) {
        if (chart._ro) chart._ro.disconnect();
        chart.dispose();
        delete instances[elementId];
    }
}

// ── Gauge (반원 도넛) ──
window.gaugeChart = {
    _i: {},

    init(id, data, isDark) { _initChart(this._i, id, data, isDark, this._set.bind(this)); },
    update(id, data, isDark) { _updateChart(this._i, id, data, isDark, this._set.bind(this)); },
    dispose(id) { _disposeChart(this._i, id); },

    _colors(isDark) {
        return {
            backedUp:  isDark ? '#60a5fa' : '#3b82f6',
            unchanged: isDark ? '#64748b' : '#94a3b8',
            failed:    isDark ? '#f87171' : '#ef4444',
            unknown:   isDark ? '#fb923c' : '#f97316',
        };
    },

    _set(chart, data, isDark) {
        const p = _palette(isDark);
        const c = this._colors(isDark);
        const t = data.total || 1;
        const borderClr = isDark ? '#1e2025' : '#e0e5ec';

        const cats = [
            { name: '백업 갱신', value: data.backedUp,  color: c.backedUp },
            { name: '변경 없음', value: data.unchanged, color: c.unchanged },
            { name: '작업 실패', value: data.failed,    color: c.failed },
            { name: '내역 없음', value: data.unknown,   color: c.unknown },
        ];

        // 보이는 세그먼트 (0인 항목 제외)
        const visibleData = cats.filter(s => s.value > 0).map(s => ({
            value: s.value,
            name: s.name,
            itemStyle: { color: s.color },
        }));

        // 하반원 투명 세그먼트 (반원 형태 만들기)
        visibleData.push({
            value: t,
            name: '',
            itemStyle: { color: 'none', borderWidth: 0, borderColor: 'transparent' },
            label: { show: false },
            labelLine: { show: false },
            emphasis: { disabled: true },
        });

        chart.setOption({
            backgroundColor: 'transparent',
            tooltip: {
                ..._tooltip(p),
                formatter: (params) => {
                    if (!params.name) return '';
                    const pct = t > 0 ? (params.value / t * 100).toFixed(1) : 0;
                    return `<b>${params.name}</b><br/>${params.value}대 (${pct}%)`;
                }
            },
            series: [{
                type: 'pie',
                radius: ['46%', '76%'],
                center: ['50%', '70%'],
                startAngle: 180,
                clockwise: true,
                avoidLabelOverlap: true,
                label: {
                    show: true,
                    position: 'outside',
                    formatter: (params) => {
                        if (!params.name) return '';
                        const pct = t > 0 ? Math.round(params.value / t * 100) : 0;
                        if (pct < 3) return '';
                        return `{bold|${pct}%}`;
                    },
                    rich: {
                        bold: { fontSize: 13, fontWeight: 700, color: p.text, fontFamily: _font }
                    }
                },
                labelLine: {
                    show: true,
                    length: 12,
                    length2: 8,
                    lineStyle: { color: p.textSub, width: 1 }
                },
                itemStyle: {
                    borderWidth: 3,
                    borderColor: borderClr,
                    borderRadius: 4,
                },
                emphasis: {
                    itemStyle: { shadowBlur: 12, shadowColor: 'rgba(0,0,0,0.25)' },
                    scale: true,
                    scaleSize: 4,
                },
                data: visibleData,
                animationType: 'scale',
                animationEasing: 'cubicOut',
            }],
            graphic: [
                {
                    type: 'text',
                    left: 'center', top: '48%',
                    style: {
                        text: t.toLocaleString(),
                        textAlign: 'center',
                        fill: p.text,
                        fontSize: 28, fontWeight: 700, fontFamily: _font,
                    },
                },
                {
                    type: 'text',
                    left: 'center', top: '60%',
                    style: {
                        text: '전체 자산',
                        textAlign: 'center',
                        fill: p.textSub,
                        fontSize: 11, fontFamily: _font,
                    },
                },
            ],
        }, true);
    }
};

// ── Donut ──
window.donutChart = {
    _i: {},

    init(id, data, isDark) { _initChart(this._i, id, data, isDark, this._set.bind(this)); },
    update(id, data, isDark) { _updateChart(this._i, id, data, isDark, this._set.bind(this)); },
    dispose(id) { _disposeChart(this._i, id); },

    _set(chart, data, isDark) {
        const p = _palette(isDark);
        const glassItems = data.items || [];
        chart.setOption({
            backgroundColor: 'transparent',
            tooltip: {
                ..._tooltip(p),
                formatter: '{b}: {c}대 ({d}%)'
            },
            legend: {
                orient: 'horizontal',
                bottom: 0,
                textStyle: { color: p.textSub, fontSize: 11, fontFamily: _font },
                itemWidth: 10, itemHeight: 10, itemGap: 12
            },
            series: [{
                type: 'pie',
                radius: ['45%', '72%'],
                center: ['50%', '42%'],
                avoidLabelOverlap: false,
                label: {
                    show: true,
                    position: 'center',
                    fontFamily: _font,
                    rich: {
                        total: { fontSize: 24, fontWeight: 700, color: p.text, lineHeight: 32 },
                        caption: { fontSize: 11, color: p.textSub, lineHeight: 20 }
                    },
                    formatter: `{total|${data.total}}\n{caption|자산 연결}`
                },
                emphasis: {
                    label: { show: true, fontFamily: _font, fontSize: 13, fontWeight: 600, color: p.text },
                    itemStyle: { shadowBlur: 10, shadowColor: 'rgba(0,0,0,0.2)' }
                },
                labelLine: { show: false },
                itemStyle: { borderWidth: 2, borderColor: p.surface, borderRadius: 4 },
                data: glassItems
            }]
        }, true);
    }
};

// ── Bar Tooltip (막대그래프 세그먼트 툴팁) ──
window.barTooltip = {
    _el: null,
    _bound: false,

    init(isDark) {
        if (!this._el) {
            this._el = document.createElement('div');
            this._el.className = 'bar-chart-tooltip';
            document.body.appendChild(this._el);
        }
        this._applyTheme(isDark);
        this._bind();
    },

    updateTheme(isDark) {
        if (this._el) this._applyTheme(isDark);
    },

    _applyTheme(isDark) {
        const p = _palette(isDark);
        const el = this._el;
        Object.assign(el.style, {
            position: 'fixed',
            zIndex: '10000',
            pointerEvents: 'none',
            opacity: '0',
            transition: 'opacity 0.15s ease',
            background: p.tooltipBg,
            border: `1px solid ${p.tooltipBd}`,
            color: p.text,
            fontFamily: _font,
            fontSize: '12px',
            padding: '8px 12px',
            borderRadius: '4px',
            boxShadow: '0 2px 8px rgba(0,0,0,0.15)',
            maxWidth: '260px',
            lineHeight: '1.5',
        });
    },

    _bind() {
        if (this._bound) return;
        this._bound = true;
        const self = this;

        document.addEventListener('mouseover', (e) => {
            const seg = e.target.closest('.type-bar-seg[data-name]');
            if (!seg || !self._el) return;
            const name = seg.dataset.name;
            const status = seg.dataset.status;
            const value = seg.dataset.value;
            const total = parseInt(seg.dataset.total) || 1;
            const pct = (parseInt(value) / total * 100).toFixed(1);
            self._el.innerHTML = `<b>${name}</b><br/>${status}: ${value}대 (${pct}%)`;
            self._el.style.opacity = '1';
        }, false);

        document.addEventListener('mousemove', (e) => {
            if (!self._el || self._el.style.opacity !== '1') return;
            self._el.style.left = (e.clientX + 14) + 'px';
            self._el.style.top = (e.clientY - 10) + 'px';
        }, false);

        document.addEventListener('mouseout', (e) => {
            const seg = e.target.closest('.type-bar-seg[data-name]');
            if (!seg || !self._el) return;
            if (seg.contains(e.relatedTarget)) return;
            self._el.style.opacity = '0';
        }, false);
    },

    dispose() {
        if (this._el) {
            this._el.remove();
            this._el = null;
        }
        this._bound = false;
    }
};

// ── Stat Donut (타일형 다중 도넛) ──
window.statDonutChart = {
    _i: {},

    init(id, dataArray, isDark, navParam) {
        this.dispose(id);
        const el = document.getElementById(id);
        if (!el) return;
        const chart = echarts.init(el, null, { renderer: 'svg' });
        this._i[id] = chart;
        // 데이터/테마 보관 (리사이즈 시 재계산용)
        chart._statData = dataArray;
        chart._statDark = isDark;
        chart._navParam = navParam || '';
        this._set(chart, dataArray, isDark);
        const self = this;

        // 클릭 → 필터 페이지 네비게이션 (서버 왕복 없음)
        chart.on('click', (params) => {
            if (chart.getDom().closest('.grid-stack.edit-mode')) return;
            const item = dataArray[params.seriesIndex];
            if (!item) return;
            const name = encodeURIComponent(item.name);
            const health = self._HEALTH_MAP[params.name] || '';
            let url = '/history?tab=1';
            if (chart._navParam) url += '&' + chart._navParam + '=' + name;
            if (health) url += '&health=' + health;
            if (window.Blazor?.navigateTo) { Blazor.navigateTo(url); }
            else { window.location.href = url; }
        });

        // 이전 크기 기록 (실제 변경 시에만 재계산)
        chart._lastW = el.clientWidth;
        chart._lastH = el.clientHeight;
        let resizeTimer = null;
        const ro = new ResizeObserver(() => {
            const w = el.clientWidth;
            const h = el.clientHeight;
            // 크기가 실제로 변한 경우에만 재계산 (툴팁 등에 의한 미세 변동 무시)
            if (w === chart._lastW && h === chart._lastH) return;
            chart._lastW = w;
            chart._lastH = h;
            if (resizeTimer) clearTimeout(resizeTimer);
            resizeTimer = setTimeout(() => {
                chart.resize();
                if (chart._statData) self._set(chart, chart._statData, chart._statDark);
            }, 100);
        });
        ro.observe(el);
        chart._ro = ro;
    },
    update(id, dataArray, isDark, navParam) {
        const chart = this._i[id];
        if (!chart) { this.init(id, dataArray, isDark, navParam); return; }
        chart._statData = dataArray;
        chart._statDark = isDark;
        if (navParam) chart._navParam = navParam;
        this._set(chart, dataArray, isDark);
    },
    dispose(id) { _disposeChart(this._i, id); },

    // 한국어 상태명 → URL 파라미터 매핑
    _HEALTH_MAP: {
        '백업갱신': 'backedup',
        '변경없음': 'unchanged',
        '실패':     'failed',
        '작업중':   'inprogress',
        '내역없음': 'unknown',
    },

    _COLORS: {
        backedUp:   '#65B991',
        unchanged:  '#6BA0DE',
        failed:     '#E67E7E',
        inProgress: '#d4a64e',
        unknown:    '#cca84e',
    },

    _NAMES: {
        backedUp:   '백업갱신',
        unchanged:  '변경없음',
        failed:     '실패',
        inProgress: '작업중',
        unknown:    '내역없음',
    },

    // 셀 폭(px)과 폰트 크기(px)로 글자 잘림 없이 들어갈 최대 길이 추정
    _truncate(text, cellW, fontSize) {
        const avgCharW = fontSize * 0.58;  // 한글/영문 혼합 평균 폭
        const maxChars = Math.max(2, Math.floor((cellW - 8) / avgCharW));
        if (text.length <= maxChars) return text;
        return text.slice(0, maxChars - 1) + '…';
    },

    _set(chart, dataArray, isDark) {
        const p = _palette(isDark);
        const C = this._COLORS;
        const N = this._NAMES;
        const n = dataArray.length;
        if (n === 0) return;

        // 컨테이너 실제 크기(px) 기반 계산
        const dom = chart.getDom();
        const W = dom.clientWidth || 300;
        const H = dom.clientHeight || 200;

        // 그리드 레이아웃: 컨테이너 비율에 맞춰 cols/rows 결정
        let bestCols = 1, bestRows = n;
        let bestScore = Infinity;
        for (let c = 1; c <= n; c++) {
            const r = Math.ceil(n / c);
            const cellW = W / c;
            const cellH = H / r;
            const ratio = Math.max(cellW / cellH, cellH / cellW);
            if (ratio < bestScore) {
                bestScore = ratio;
                bestCols = c;
                bestRows = r;
            }
        }
        const cols = bestCols;
        const rows = bestRows;

        // 셀 크기 (px)
        const cellW = W / cols;
        const cellH = H / rows;

        // 이름 레이블 폰트 및 공간
        const nameFont = Math.max(Math.min(Math.round(cellH * 0.13), 13), 8);
        const labelSpace = nameFont + 8;                  // 레이블 높이 + 간격
        const pad = Math.max(Math.round(cellW * 0.06), 3); // 좌우 여백

        // 도넛 반지름: 셀 안에서 레이블 공간을 뺀 영역에 맞춤
        const availH = cellH - labelSpace;
        const maxR = Math.floor(Math.min((cellW - pad * 2) / 2, availH / 2) - 2);
        const outerR = Math.max(maxR, 8);
        const innerR = Math.max(Math.round(outerR * 0.55), 3);

        // 중앙 숫자 폰트
        const valFont = Math.max(Math.min(Math.round(innerR * 0.72), 20), 8);

        const series = [];
        const graphics = [];

        dataArray.forEach((item, i) => {
            const col = i % cols;
            const row = Math.floor(i / cols);

            // 셀 중심 좌표 (px) — 도넛은 레이블 공간만큼 위로
            const cxPx = Math.round(cellW * (col + 0.5));
            const cyPx = Math.round(cellH * row + availH / 2);

            const total = (item.backedUp || 0) + (item.unchanged || 0) +
                          (item.failed || 0) + (item.inProgress || 0) + (item.unknown || 0);

            const pieData = [
                { value: item.backedUp || 0,   name: N.backedUp,   _base: C.backedUp },
                { value: item.unchanged || 0,  name: N.unchanged,  _base: C.unchanged },
                { value: item.failed || 0,     name: N.failed,     _base: C.failed },
                { value: item.inProgress || 0, name: N.inProgress, _base: C.inProgress },
                { value: item.unknown || 0,    name: N.unknown,    _base: C.unknown },
            ].filter(d => d.value > 0).map(d => ({
                value: d.value,
                name: d.name,
                itemStyle: { color: d._base },
            }));

            // ── 인셋 배경 링 (도넛 트랙) ──
            graphics.push({
                type: 'ring',
                shape: { cx: cxPx, cy: cyPx, r: outerR, r0: innerR },
                style: {
                    fill: isDark ? 'rgba(0,0,0,0.18)' : 'rgba(0,0,0,0.06)',
                    shadowBlur: 6,
                    shadowOffsetX: 2,
                    shadowOffsetY: 2,
                    shadowColor: isDark ? 'rgba(0,0,0,0.5)' : 'rgba(0,0,0,0.2)',
                },
                z: 0,
            });
            // ── 인셋 하이라이트 링 (좌상단 밝은 반사) ──
            graphics.push({
                type: 'ring',
                shape: { cx: cxPx, cy: cyPx, r: outerR, r0: innerR },
                style: {
                    fill: isDark ? 'rgba(255,255,255,0.03)' : 'rgba(255,255,255,0.5)',
                    shadowBlur: 4,
                    shadowOffsetX: -2,
                    shadowOffsetY: -2,
                    shadowColor: isDark ? 'rgba(255,255,255,0.08)' : 'rgba(255,255,255,0.6)',
                },
                z: 0,
            });

            series.push({
                type: 'pie',
                radius: [innerR, outerR],
                center: [cxPx, cyPx],
                avoidLabelOverlap: false,
                label: {
                    show: true,
                    position: 'center',
                    fontFamily: _font,
                    rich: {
                        val: { fontSize: valFont, fontWeight: 700, color: p.text, lineHeight: valFont + 4 },
                    },
                    formatter: `{val|${total}}`
                },
                labelLine: { show: false },
                emphasis: {
                    label: { show: true, fontFamily: _font, fontSize: Math.max(valFont - 2, 8), fontWeight: 600, color: p.text },
                    itemStyle: { shadowBlur: 8, shadowColor: 'rgba(0,0,0,0.25)' },
                    scale: true, scaleSize: Math.min(3, outerR * 0.06),
                },
                itemStyle: {
                    borderWidth: outerR > 20 ? 1.5 : 1,
                    borderColor: p.surface,
                    borderRadius: 3,
                    shadowBlur: 3,
                    shadowOffsetX: 1,
                    shadowOffsetY: 2,
                    shadowColor: isDark ? 'rgba(0,0,0,0.4)' : 'rgba(0,0,0,0.15)',
                },
                data: pieData,
                animationType: 'scale',
                animationEasing: 'cubicOut',
            });

            // 이름 레이블 (도넛 하단, x 좌표 기준 중앙 정렬)
            const labelY = cellH * row + availH + 2;
            const labelText = this._truncate(item.name, cellW, nameFont);
            graphics.push({
                type: 'text',
                x: cxPx,
                y: labelY,
                style: {
                    text: labelText,
                    textAlign: 'center',
                    fill: p.text,
                    fontSize: nameFont,
                    fontWeight: 600,
                    fontFamily: _font,
                },
                z: 100,
                cursor: 'pointer',
                onclick: (() => {
                    const itemName = item.name;
                    return () => {
                        if (chart.getDom().closest('.grid-stack.edit-mode')) return;
                        const name = encodeURIComponent(itemName);
                        let url = '/history?tab=1';
                        if (chart._navParam) url += '&' + chart._navParam + '=' + name;
                        if (window.Blazor?.navigateTo) { Blazor.navigateTo(url); }
                        else { window.location.href = url; }
                    };
                })(),
            });
        });

        chart.setOption({
            backgroundColor: 'transparent',
            tooltip: {
                ..._tooltip(p),
                appendToBody: true,
                confine: true,
                formatter: (params) => {
                    const sIdx = params.seriesIndex;
                    const item = dataArray[sIdx];
                    if (!item) return '';
                    const total = (item.backedUp || 0) + (item.unchanged || 0) +
                                  (item.failed || 0) + (item.inProgress || 0) + (item.unknown || 0);
                    const pct = total > 0 ? (params.value / total * 100).toFixed(1) : '0';
                    return `<b>${item.name}</b><br/>${params.name}: ${params.value}대 (${pct}%)`;
                }
            },
            series,
            graphic: graphics,
        }, true);
    }
};
