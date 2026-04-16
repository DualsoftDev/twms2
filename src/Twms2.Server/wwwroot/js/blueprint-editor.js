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

        const inst = { container, svg, dotNetRef, handlers: [], snap: { enabled: true, gridSize: 20 }, dragging: false };
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

            if (fillRect) bindDrag(inst, g, fillRect, lineId, 'move');
            if (borderRect) bindDrag(inst, g, borderRect, lineId, 'move');
            if (label) bindDrag(inst, g, label, lineId, 'move');

            if (resizeHandle) {
                bindDrag(inst, g, resizeHandle, lineId, 'resize');
            }

            if (deleteBtn) {
                const handler = (e) => {
                    e.preventDefault();
                    e.stopPropagation();
                    // 드래그 중에는 삭제 무시
                    if (inst.dragging) return;
                    const lineName = g.querySelector('.bp-rect-label')?.textContent?.trim() || `Line ${lineId}`;
                    if (!confirm(`'${lineName}' 라인 영역을 삭제하시겠습니까?`)) return;
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
            if (e.button !== 0) return; // 좌클릭만 허용
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

            // 핸들 전용 <g>에는 fill이 없으므로 같은 lineId의 fill을 SVG에서 탐색
            let fill = group.querySelector('.bp-rect-fill');
            if (!fill) {
                const lid = group.dataset.lineId;
                const svg = group.closest('svg');
                if (lid && svg) {
                    for (const g of svg.querySelectorAll(`.bp-edit-rect[data-line-id="${lid}"]`)) {
                        fill = g.querySelector('.bp-rect-fill');
                        if (fill) break;
                    }
                }
            }
            if (!fill) return;
            startRect = {
                x: parseFloat(fill.getAttribute('x')),
                y: parseFloat(fill.getAttribute('y')),
                w: parseFloat(fill.getAttribute('width')),
                h: parseFloat(fill.getAttribute('height')),
            };

            document.addEventListener('pointermove', onMove);
            document.addEventListener('pointerup', onUp);
            group.classList.add('dragging');
            inst.dragging = true;
            target.setPointerCapture(e.pointerId);
        };

        const onMove = (e) => {
            if (!startPt || !startRect) return;

            // viewBox 기준 좌표 변환 (줌 반영)
            const dx = ((e.clientX - startPt.clientX) / startPt.svgW) * startPt.vbW;
            const dy = ((e.clientY - startPt.clientY) / startPt.svgH) * startPt.vbH;

            let x, y, w, h;
            const snap = v => inst.snap.enabled ? Math.round(v / inst.snap.gridSize) * inst.snap.gridSize : v;

            if (mode === 'resize') {
                w = Math.max(30, snap(startRect.w + dx));
                h = Math.max(20, snap(startRect.h + dy));
                x = startRect.x;
                y = startRect.y;
            } else {
                x = snap(startRect.x + dx);
                y = snap(startRect.y + dy);
                w = startRect.w;
                h = startRect.h;
            }

            updateGroupPosition(group, x, y, w, h);
        };

        const onUp = (e) => {
            document.removeEventListener('pointermove', onMove);
            document.removeEventListener('pointerup', onUp);
            group.classList.remove('dragging');
            // 약간의 지연 후 dragging 해제 — pointerup 직후 삭제 버튼 오발동 방지
            setTimeout(() => { inst.dragging = false; }, 100);

            if (startPt && startRect && inst.dotNetRef) {
                let fill = group.querySelector('.bp-rect-fill');
                if (!fill) {
                    const lid = group.dataset.lineId;
                    const svg = group.closest('svg');
                    if (lid && svg) {
                        for (const g of svg.querySelectorAll(`.bp-edit-rect[data-line-id="${lid}"]`)) {
                            fill = g.querySelector('.bp-rect-fill');
                            if (fill) break;
                        }
                    }
                }
                if (!fill) { startPt = null; startRect = null; return; }
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
        // 같은 lineId의 모든 <g> 요소에서 자식 탐색 (핸들이 별도 레이어)
        const lineId = group.dataset.lineId;
        const svg = group.closest('svg');
        const allGroups = lineId && svg
            ? svg.querySelectorAll(`.bp-edit-rect[data-line-id="${lineId}"]`)
            : [group];
        let fill, border, label, handle, delBtn;
        allGroups.forEach(g => {
            fill   = g.querySelector('.bp-rect-fill')   || fill;
            border = g.querySelector('.bp-rect-border') || border;
            label  = g.querySelector('.bp-rect-label')  || label;
            handle = g.querySelector('.bp-resize-handle') || handle;
            delBtn = g.querySelector('.bp-delete-btn')  || delBtn;
        });

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

    function setSnapConfig(containerId, enabled, gridSize) {
        const inst = _inst[containerId];
        if (inst) {
            inst.snap.enabled = !!enabled;
            inst.snap.gridSize = gridSize > 0 ? gridSize : 20;
        }
    }

    return { init, getPositions, setSnapConfig, dispose };
})();
