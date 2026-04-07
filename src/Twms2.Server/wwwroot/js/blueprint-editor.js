/**
 * Blueprint Editor - 도면 위 라인 사각형 드래그/리사이즈 모듈
 * SVG viewBox 좌표계 사용 (줌 시 동적 viewBox 반영)
 */
window.blueprintEditor = (() => {
    const _inst = {};

    /** 현재 SVG viewBox에서 폭/높이를 읽어온다 (줌 상태 반영) */
    function getViewBoxSize(svg) {
        const vb = svg.getAttribute('viewBox');
        if (vb) {
            const parts = vb.split(/\s+/).map(Number);
            if (parts.length === 4) return { w: parts[2], h: parts[3] };
        }
        return { w: 1000, h: 600 };
    }

    function init(containerId, dotNetRef) {
        dispose(containerId);

        const container = document.getElementById(containerId);
        if (!container) return;

        const svg = container.querySelector('svg');
        if (!svg) return;

        const inst = { container, svg, dotNetRef, handlers: [] };
        _inst[containerId] = inst;

        const groups = svg.querySelectorAll('.bp-edit-rect');
        groups.forEach(g => {
            const lineId = parseInt(g.dataset.lineId);
            if (isNaN(lineId)) return;

            const fillRect = g.querySelector('.bp-rect-fill');
            const borderRect = g.querySelector('.bp-rect-border');
            const label = g.querySelector('.bp-rect-label');
            const resizeHandle = g.querySelector('.bp-resize-handle');
            const deleteBtn = g.querySelector('.bp-delete-btn');

            bindDrag(inst, g, fillRect, lineId, 'move');
            bindDrag(inst, g, borderRect, lineId, 'move');
            bindDrag(inst, g, label, lineId, 'move');

            if (resizeHandle) {
                bindDrag(inst, g, resizeHandle, lineId, 'resize');
            }

            if (deleteBtn) {
                const handler = (e) => {
                    e.preventDefault();
                    e.stopPropagation();
                    if (dotNetRef) {
                        dotNetRef.invokeMethodAsync('OnRectDeleted', lineId);
                    }
                };
                deleteBtn.addEventListener('pointerdown', handler);
                inst.handlers.push({ el: deleteBtn, type: 'pointerdown', fn: handler });
            }
        });
    }

    function bindDrag(inst, group, target, lineId, mode) {
        let startPt = null;
        let startRect = null;

        const onDown = (e) => {
            if (e.target.closest('.bp-delete-btn')) return;
            e.preventDefault();
            e.stopPropagation();

            const svgRect = inst.svg.getBoundingClientRect();
            const vbSize = getViewBoxSize(inst.svg);
            startPt = {
                clientX: e.clientX,
                clientY: e.clientY,
                svgW: svgRect.width,
                svgH: svgRect.height,
                vbW: vbSize.w,
                vbH: vbSize.h,
            };

            const fill = group.querySelector('.bp-rect-fill');
            startRect = {
                x: parseFloat(fill.getAttribute('x')),
                y: parseFloat(fill.getAttribute('y')),
                w: parseFloat(fill.getAttribute('width')),
                h: parseFloat(fill.getAttribute('height')),
            };

            document.addEventListener('pointermove', onMove);
            document.addEventListener('pointerup', onUp);
            group.classList.add('dragging');
            target.setPointerCapture(e.pointerId);
        };

        const onMove = (e) => {
            if (!startPt || !startRect) return;

            // viewBox 기준 좌표 변환 (줌 반영)
            const dx = ((e.clientX - startPt.clientX) / startPt.svgW) * startPt.vbW;
            const dy = ((e.clientY - startPt.clientY) / startPt.svgH) * startPt.vbH;

            let x, y, w, h;

            if (mode === 'resize') {
                w = Math.max(30, startRect.w + dx);
                h = Math.max(20, startRect.h + dy);
                x = startRect.x;
                y = startRect.y;
                // 경계 제한 없음 — 도면 밖 배치 허용
            } else {
                x = startRect.x + dx;
                y = startRect.y + dy;
                w = startRect.w;
                h = startRect.h;
                // 경계 제한 없음 — 도면 밖 배치 허용
            }

            updateGroupPosition(group, x, y, w, h);
        };

        const onUp = (e) => {
            document.removeEventListener('pointermove', onMove);
            document.removeEventListener('pointerup', onUp);
            group.classList.remove('dragging');

            if (startPt && startRect && inst.dotNetRef) {
                const fill = group.querySelector('.bp-rect-fill');
                const fx = parseFloat(fill.getAttribute('x'));
                const fy = parseFloat(fill.getAttribute('y'));
                const fw = parseFloat(fill.getAttribute('width'));
                const fh = parseFloat(fill.getAttribute('height'));
                inst.dotNetRef.invokeMethodAsync('OnRectMoved', lineId,
                    Math.round(fx * 100) / 100,
                    Math.round(fy * 100) / 100,
                    Math.round(fw * 100) / 100,
                    Math.round(fh * 100) / 100);
            }

            startPt = null;
            startRect = null;
        };

        target.addEventListener('pointerdown', onDown);
        inst.handlers.push({ el: target, type: 'pointerdown', fn: onDown });
    }

    function updateGroupPosition(group, x, y, w, h) {
        const fill = group.querySelector('.bp-rect-fill');
        const border = group.querySelector('.bp-rect-border');
        const label = group.querySelector('.bp-rect-label');
        const handle = group.querySelector('.bp-resize-handle');
        const delBtn = group.querySelector('.bp-delete-btn');

        if (fill) {
            fill.setAttribute('x', x);
            fill.setAttribute('y', y);
            fill.setAttribute('width', w);
            fill.setAttribute('height', h);
        }
        if (border) {
            border.setAttribute('x', x);
            border.setAttribute('y', y);
            border.setAttribute('width', w);
            border.setAttribute('height', h);
        }
        if (label) {
            label.setAttribute('x', x + w / 2);
            label.setAttribute('y', y + h / 2);
        }
        if (handle) {
            handle.setAttribute('x', x + w - 15);
            handle.setAttribute('y', y + h - 15);
        }
        if (delBtn) {
            const circle = delBtn.querySelector('circle');
            const text = delBtn.querySelector('text');
            if (circle) {
                circle.setAttribute('cx', x + w);
                circle.setAttribute('cy', y);
            }
            if (text) {
                text.setAttribute('x', x + w);
                text.setAttribute('y', y);
            }
        }
    }

    function getPositions(containerId) {
        const container = document.getElementById(containerId);
        if (!container) return [];

        const svg = container.querySelector('svg');
        if (!svg) return [];

        const result = [];
        svg.querySelectorAll('.bp-edit-rect').forEach(g => {
            const lineId = parseInt(g.dataset.lineId);
            const fill = g.querySelector('.bp-rect-fill');
            if (isNaN(lineId) || !fill) return;
            result.push({
                lineId: lineId,
                x: Math.round(parseFloat(fill.getAttribute('x')) * 100) / 100,
                y: Math.round(parseFloat(fill.getAttribute('y')) * 100) / 100,
                w: Math.round(parseFloat(fill.getAttribute('width')) * 100) / 100,
                h: Math.round(parseFloat(fill.getAttribute('height')) * 100) / 100,
            });
        });
        return result;
    }

    function dispose(containerId) {
        const inst = _inst[containerId];
        if (!inst) return;

        inst.handlers.forEach(({ el, type, fn }) => {
            el.removeEventListener(type, fn);
        });
        inst.handlers.length = 0;
        delete _inst[containerId];
    }

    return { init, getPositions, dispose };
})();
