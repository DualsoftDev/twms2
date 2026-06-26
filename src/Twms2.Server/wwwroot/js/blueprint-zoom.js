/**
 * SVG viewBox 기반 줌/팬 모듈.
 * 여러 컨테이너에 독립 인스턴스로 동작.
 */
window.blueprintZoom = (() => {
    const instances = {};
    const ORIG_W = 1000, ORIG_H = 600;
    const MIN_ZOOM = 0.5, MAX_ZOOM = 5;
    const ZOOM_STEP = 1.08;

    function getInstance(id) { return instances[id]; }

    function init(containerId, opts) {
        const container = document.getElementById(containerId);
        if (!container) return;
        const svg = container.querySelector('svg');
        if (!svg) return;

        // 기존 인스턴스 정리
        if (instances[containerId]) dispose(containerId);

        const inst = {
            svg,
            container,
            vb: { x: 0, y: 0, w: ORIG_W, h: ORIG_H },
            zoom: 1,
            isPanning: false,
            panStart: null,
            vbStart: null,
            clampMargin: (opts && opts.clampMargin) || 0.3,  // 기본 30%, 에디터용으로 확대 가능
        };
        instances[containerId] = inst;

        applyViewBox(inst);

        // ── wheel zoom ──
        inst._onWheel = (e) => {
            e.preventDefault();
            // deltaY 크기에 비례하여 줌 (1.002^delta → deltaY=100 일 때 약 22% 줌)
            const factor = Math.pow(1.002, -e.deltaY);
            zoomByFactor(inst, factor, e.clientX, e.clientY);
        };
        svg.addEventListener('wheel', inst._onWheel, { passive: false });

        // ── pointer pan (드래그 임계값으로 클릭/팬 구분) ──
        const DRAG_THRESHOLD = 5; // px
        inst._onPointerDown = (e) => {
            inst.isPanning = true;
            inst.hasDragged = false;
            inst._pointerId = e.pointerId;
            inst.panStart = { x: e.clientX, y: e.clientY };
            inst.vbStart = { x: inst.vb.x, y: inst.vb.y };
            // setPointerCapture를 여기서 하지 않음 → 클릭이 자식 요소에 정상 전달
        };
        inst._onPointerMove = (e) => {
            if (!inst.isPanning) return;
            const dxScreen = e.clientX - inst.panStart.x;
            const dyScreen = e.clientY - inst.panStart.y;
            if (!inst.hasDragged) {
                if (Math.abs(dxScreen) + Math.abs(dyScreen) < DRAG_THRESHOLD) return;
                inst.hasDragged = true;
                svg.style.cursor = 'grabbing';
                svg.setPointerCapture(inst._pointerId); // 드래그 확정 시에만 캡처
            }
            const rect = svg.getBoundingClientRect();
            const dx = (dxScreen / rect.width) * inst.vb.w;
            const dy = (dyScreen / rect.height) * inst.vb.h;
            inst.vb.x = clampX(inst, inst.vbStart.x - dx, inst.vb.w);
            inst.vb.y = clampY(inst, inst.vbStart.y - dy, inst.vb.h);
            applyViewBox(inst);
        };
        inst._onPointerUp = (e) => {
            if (!inst.isPanning) return;
            inst.isPanning = false;
            svg.style.cursor = inst.zoom > 1.01 ? 'grab' : '';
            if (inst.hasDragged) svg.releasePointerCapture(e.pointerId);
        };
        // 드래그 후 click 이벤트 억제 (캡처 단계)
        inst._onClick = (e) => {
            if (inst.hasDragged) {
                e.stopPropagation();
                e.preventDefault();
                inst.hasDragged = false;
            }
        };
        svg.addEventListener('pointerdown', inst._onPointerDown);
        svg.addEventListener('pointermove', inst._onPointerMove);
        svg.addEventListener('pointerup', inst._onPointerUp);
        svg.addEventListener('pointercancel', inst._onPointerUp);
        svg.addEventListener('click', inst._onClick, true); // capture phase
    }

    function zoomByFactor(inst, factor, clientX, clientY) {
        const newZoom = Math.max(MIN_ZOOM, Math.min(MAX_ZOOM, inst.zoom * factor));
        if (Math.abs(newZoom - inst.zoom) < 0.001) return;

        // 마우스 위치의 SVG 좌표 계산
        const rect = inst.svg.getBoundingClientRect();
        const mx = ((clientX - rect.left) / rect.width) * inst.vb.w + inst.vb.x;
        const my = ((clientY - rect.top) / rect.height) * inst.vb.h + inst.vb.y;

        const newW = ORIG_W / newZoom;
        const newH = ORIG_H / newZoom;

        // 마우스 위치를 기준으로 줌
        const ratio = ((clientX - rect.left) / rect.width);
        const ratioY = ((clientY - rect.top) / rect.height);
        inst.vb.x = clampX(inst, mx - newW * ratio, newW);
        inst.vb.y = clampY(inst, my - newH * ratioY, newH);
        inst.vb.w = newW;
        inst.vb.h = newH;
        inst.zoom = newZoom;

        applyViewBox(inst);
        inst.svg.style.cursor = newZoom > 1.01 ? 'grab' : '';
    }

    function clampX(inst, x, w) {
        const margin = w * (inst ? inst.clampMargin : 0.3);
        return Math.max(-margin, Math.min(ORIG_W - w + margin, x));
    }
    function clampY(inst, y, h) {
        const margin = h * (inst ? inst.clampMargin : 0.3);
        return Math.max(-margin, Math.min(ORIG_H - h + margin, y));
    }

    function applyViewBox(inst) {
        inst.svg.setAttribute('viewBox',
            `${inst.vb.x.toFixed(2)} ${inst.vb.y.toFixed(2)} ${inst.vb.w.toFixed(2)} ${inst.vb.h.toFixed(2)}`);
    }

    function zoomIn(containerId) {
        const inst = getInstance(containerId);
        if (!inst) return;
        const rect = inst.svg.getBoundingClientRect();
        zoomByFactor(inst, ZOOM_STEP, rect.left + rect.width / 2, rect.top + rect.height / 2);
    }

    function zoomOut(containerId) {
        const inst = getInstance(containerId);
        if (!inst) return;
        const rect = inst.svg.getBoundingClientRect();
        zoomByFactor(inst, 1 / ZOOM_STEP, rect.left + rect.width / 2, rect.top + rect.height / 2);
    }

    function reset(containerId) {
        const inst = getInstance(containerId);
        if (!inst) return;
        inst.vb = { x: 0, y: 0, w: ORIG_W, h: ORIG_H };
        inst.zoom = 1;
        applyViewBox(inst);
        inst.svg.style.cursor = '';
    }

    function toggleFullscreen(containerId, enter) {
        const container = document.getElementById(containerId);
        if (!container) return;
        if (enter) {
            const el = container.closest('.blueprint-container') || container;
            if (el.requestFullscreen) el.requestFullscreen();
            else if (el.webkitRequestFullscreen) el.webkitRequestFullscreen();
        } else {
            if (document.exitFullscreen) document.exitFullscreen();
            else if (document.webkitExitFullscreen) document.webkitExitFullscreen();
        }
    }

    function getViewBox(containerId) {
        const inst = getInstance(containerId);
        if (!inst) return { x: 0, y: 0, w: ORIG_W, h: ORIG_H };
        return { x: inst.vb.x, y: inst.vb.y, w: inst.vb.w, h: inst.vb.h };
    }

    // 외부에서 viewBox(줌/팬 상태)를 복원 — 폴링 재렌더 후 줌 유지용.
    function setViewBox(containerId, vb) {
        const inst = getInstance(containerId);
        if (!inst || !vb || !(vb.w > 0)) return;
        inst.vb = { x: vb.x, y: vb.y, w: vb.w, h: vb.h };
        inst.zoom = Math.max(MIN_ZOOM, Math.min(MAX_ZOOM, ORIG_W / vb.w));
        applyViewBox(inst);
        inst.svg.style.cursor = inst.zoom > 1.01 ? 'grab' : '';
    }

    function dispose(containerId) {
        const inst = instances[containerId];
        if (!inst) return;
        inst.svg.removeEventListener('wheel', inst._onWheel);
        inst.svg.removeEventListener('pointerdown', inst._onPointerDown);
        inst.svg.removeEventListener('pointermove', inst._onPointerMove);
        inst.svg.removeEventListener('pointerup', inst._onPointerUp);
        inst.svg.removeEventListener('pointercancel', inst._onPointerUp);
        inst.svg.removeEventListener('click', inst._onClick, true);
        delete instances[containerId];
    }

    return { init, zoomIn, zoomOut, reset, toggleFullscreen, getViewBox, setViewBox, dispose };
})();
