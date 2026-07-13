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

        const inst = {
            container, svg, dotNetRef, handlers: [],
            snap: { neighbor: true, grid: false, gridSize: 20, threshold: 8 },
            dragging: false, _guides: [],
        };
        _inst[containerId] = inst;

        bindKeyboard(inst);

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
            const grid = v => Math.round(v / inst.snap.gridSize) * inst.snap.gridSize;

            if (mode === 'resize') {
                x = startRect.x;
                y = startRect.y;
                let rw = startRect.w + dx, rh = startRect.h + dy;
                if (inst.snap.grid) {
                    rw = grid(rw); rh = grid(rh);
                    clearGuides(inst);
                } else if (inst.snap.neighbor) {
                    const s = snapResize(inst, lineId, x, y, rw, rh);
                    rw = s.w; rh = s.h;
                    renderGuides(inst, s.guides);
                }
                w = Math.max(30, rw);
                h = Math.max(20, rh);
            } else {
                w = startRect.w;
                h = startRect.h;
                let rx = startRect.x + dx, ry = startRect.y + dy;
                if (inst.snap.grid) {
                    rx = grid(rx); ry = grid(ry);
                    clearGuides(inst);
                } else if (inst.snap.neighbor) {
                    const s = snapMove(inst, lineId, rx, ry, w, h);
                    rx = s.x; ry = s.y;
                    renderGuides(inst, s.guides);
                }
                x = rx;
                y = ry;
            }

            updateGroupPosition(group, x, y, w, h);
        };

        const onUp = (e) => {
            document.removeEventListener('pointermove', onMove);
            document.removeEventListener('pointerup', onUp);
            group.classList.remove('dragging');
            clearGuides(inst);
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
                // 실제로 위치/크기가 바뀐 경우에만 보고 — 단순 클릭은 '변경됨' 처리하지 않음
                if (fx !== startRect.x || fy !== startRect.y || fw !== startRect.w || fh !== startRect.h) {
                    inst.dotNetRef.invokeMethodAsync('OnRectMoved', lineId,
                        Math.round(fx * 100) / 100,
                        Math.round(fy * 100) / 100,
                        Math.round(fw * 100) / 100,
                        Math.round(fh * 100) / 100);
                }
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

    // ── 주변 영역 스냅 (asset-placement-editor 와 동일 UX: 임계값 내 정렬 + 가이드라인) ──

    /** lineId 제외 나머지 라인 영역들의 현재 좌표 */
    function otherRects(inst, lineId) {
        const result = [];
        inst.svg.querySelectorAll('.bp-edit-rect').forEach(g => {
            const lid = parseInt(g.dataset.lineId);
            const fill = g.querySelector('.bp-rect-fill');
            if (isNaN(lid) || lid === lineId || !fill) return;
            result.push({
                x: parseFloat(fill.getAttribute('x')),
                y: parseFloat(fill.getAttribute('y')),
                w: parseFloat(fill.getAttribute('width')),
                h: parseFloat(fill.getAttribute('height')),
            });
        });
        return result;
    }

    /** 이동 스냅: 좌/중/우, 상/중/하 모서리를 이웃 모서리에 정렬 */
    function snapMove(inst, lineId, x, y, w, h) {
        const thr = inst.snap.threshold;
        const guides = [];
        let bestDx = thr + 1, bestDy = thr + 1, snapX = null, snapY = null, guideX = null, guideY = null;
        for (const n of otherRects(inst, lineId)) {
            const nxs = [n.x, n.x + n.w / 2, n.x + n.w];
            const nys = [n.y, n.y + n.h / 2, n.y + n.h];
            for (const me of [x, x + w / 2, x + w]) {
                for (const ne of nxs) {
                    const diff = Math.abs(me - ne);
                    if (diff < bestDx) { bestDx = diff; snapX = x + (ne - me); guideX = ne; }
                }
            }
            for (const me of [y, y + h / 2, y + h]) {
                for (const ne of nys) {
                    const diff = Math.abs(me - ne);
                    if (diff < bestDy) { bestDy = diff; snapY = y + (ne - me); guideY = ne; }
                }
            }
        }
        if (bestDx <= thr && snapX !== null) { x = snapX; guides.push({ x1: guideX, y1: 0, x2: guideX, y2: 600 }); }
        if (bestDy <= thr && snapY !== null) { y = snapY; guides.push({ x1: 0, y1: guideY, x2: 1000, y2: guideY }); }
        return { x, y, guides };
    }

    /** 리사이즈 스냅: 우/하 모서리를 이웃 모서리에 정렬 */
    function snapResize(inst, lineId, x, y, w, h) {
        const thr = inst.snap.threshold;
        const guides = [];
        let bestDw = thr + 1, bestDh = thr + 1, snapW = null, snapH = null, guideX = null, guideY = null;
        for (const n of otherRects(inst, lineId)) {
            for (const ne of [n.x, n.x + n.w]) {
                const diff = Math.abs((x + w) - ne);
                if (diff < bestDw && ne - x >= 30) { bestDw = diff; snapW = ne - x; guideX = ne; }
            }
            for (const ne of [n.y, n.y + n.h]) {
                const diff = Math.abs((y + h) - ne);
                if (diff < bestDh && ne - y >= 20) { bestDh = diff; snapH = ne - y; guideY = ne; }
            }
        }
        if (bestDw <= thr && snapW !== null) { w = snapW; guides.push({ x1: guideX, y1: 0, x2: guideX, y2: 600 }); }
        if (bestDh <= thr && snapH !== null) { h = snapH; guides.push({ x1: 0, y1: guideY, x2: 1000, y2: guideY }); }
        return { w, h, guides };
    }

    function renderGuides(inst, guides) {
        clearGuides(inst);
        for (const g of guides) {
            const line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
            line.classList.add('ap-snap-guide');
            line.setAttribute('x1', g.x1); line.setAttribute('y1', g.y1);
            line.setAttribute('x2', g.x2); line.setAttribute('y2', g.y2);
            inst.svg.appendChild(line);
            inst._guides.push(line);
        }
    }

    function clearGuides(inst) {
        inst._guides.forEach(l => l.remove());
        inst._guides.length = 0;
    }

    // ── 키보드 (Ctrl+Z/Y — 호스트 shim/컴포넌트의 OnKeyAction 으로 전달) ──
    function bindKeyboard(inst) {
        const handler = (e) => {
            const tag = e.target.tagName;
            if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || e.target.isContentEditable) return;
            const ctrl = e.ctrlKey || e.metaKey;
            if (!ctrl || !inst.dotNetRef) return;
            const key = e.key.toLowerCase();
            let action = null;
            if (key === 'z') action = e.shiftKey ? 'redo' : 'undo';
            else if (key === 'y') action = 'redo';
            if (!action) return;
            e.preventDefault();
            // OnKeyAction 미구현 호스트(레거시 Blazor)는 조용히 무시
            try { Promise.resolve(inst.dotNetRef.invokeMethodAsync('OnKeyAction', action)).catch(() => { }); } catch { }
        };
        document.addEventListener('keydown', handler);
        inst.handlers.push({ el: document, type: 'keydown', fn: handler });
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

        clearGuides(inst);
        inst.handlers.forEach(({ el, type, fn }) => {
            el.removeEventListener(type, fn);
        });
        inst.handlers.length = 0;
        delete _inst[containerId];
    }

    /** (containerId, neighborSnap, gridSize, gridSnap) — 구 호출부(3인자)는 grid=false 로 동작 */
    function setSnapConfig(containerId, neighbor, gridSize, grid) {
        const inst = _inst[containerId];
        if (inst) {
            inst.snap.neighbor = !!neighbor;
            inst.snap.grid = !!grid;
            if (gridSize > 0) inst.snap.gridSize = gridSize;
        }
    }

    return { init, getPositions, setSnapConfig, dispose };
})();
