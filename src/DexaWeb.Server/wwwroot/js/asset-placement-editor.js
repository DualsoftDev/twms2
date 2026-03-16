/**
 * Asset Placement Editor - 자산 아이콘 드래그 배치 모듈
 * SVG viewBox 0 0 1000 600 좌표계
 * 기능: 단일/다중 선택, 올가미, 일괄 이동, 그룹 드래그/리사이즈
 */
window.assetPlacementEditor = (() => {
    const _inst = {};
    const VB_W = 1000, VB_H = 600;
    const ICON_SIZE = 32;

    // ── 유틸 ──
    const r2 = v => Math.round(v * 100) / 100;
    function getIconSize(el) {
        return ICON_SIZE * (parseFloat(el.dataset.scale) || 1.0);
    }
    function clientToSvg(inst, clientX, clientY) {
        const r = inst.svg.getBoundingClientRect();
        return { x: ((clientX - r.left) / r.width) * VB_W, y: ((clientY - r.top) / r.height) * VB_H };
    }
    function clientDelta(inst, dx, dy) {
        const r = inst.svg.getBoundingClientRect();
        return { dx: (dx / r.width) * VB_W, dy: (dy / r.height) * VB_H };
    }

    // ── Init ──
    function init(containerId, dotNetRef) {
        dispose(containerId);

        const container = document.getElementById(containerId);
        if (!container) return;
        const svg = container.querySelector('svg');
        if (!svg) return;

        const inst = {
            container, svg, dotNetRef, handlers: [],
            selectedIds: new Set(),
            mode: 'single', // 'single' | 'multi'
        };
        _inst[containerId] = inst;

        // 자산 아이콘 드래그 바인딩
        svg.querySelectorAll('.ap-asset-icon').forEach(g => {
            const assetId = parseInt(g.dataset.assetId);
            if (!isNaN(assetId)) bindAssetDrag(inst, g, assetId);
        });

        // 그룹 컨테이너 드래그/리사이즈 바인딩
        svg.querySelectorAll('.ap-group-container').forEach(g => {
            const groupId = parseInt(g.dataset.groupId);
            if (!isNaN(groupId)) {
                bindGroupDrag(inst, g, groupId);
                const resizeHandle = g.querySelector('.ap-group-resize');
                if (resizeHandle) bindGroupResize(inst, g, resizeHandle, groupId);
            }
        });

        // 올가미 선택 (SVG 빈 영역 클릭/드래그)
        bindLasso(inst);
    }

    // ── 자산 드래그 (단일 + 다중 이동) ──
    function bindAssetDrag(inst, group, assetId) {
        let startPt = null;
        let startPos = null;
        let moved = false;
        let bulkStarts = null; // 다중 이동 시 각 자산의 시작 위치

        const onDown = (e) => {
            e.preventDefault();
            e.stopPropagation();

            // Ctrl+클릭: 선택 토글
            if (inst.mode === 'multi' && (e.ctrlKey || e.metaKey)) {
                toggleSelect(inst, assetId, group);
                return;
            }

            // 다중 모드에서 선택된 자산 클릭 → 일괄 이동 준비
            const isSelected = inst.selectedIds.has(assetId);
            if (inst.mode === 'multi' && isSelected && inst.selectedIds.size > 1) {
                bulkStarts = collectBulkStarts(inst);
            } else {
                bulkStarts = null;
            }

            startPt = { clientX: e.clientX, clientY: e.clientY };
            startPos = { x: parseFloat(group.dataset.x) || 0, y: parseFloat(group.dataset.y) || 0 };
            moved = false;

            document.addEventListener('pointermove', onMove);
            document.addEventListener('pointerup', onUp);
            group.setPointerCapture(e.pointerId);
        };

        const onMove = (e) => {
            if (!startPt) return;
            moved = true;
            const d = clientDelta(inst, e.clientX - startPt.clientX, e.clientY - startPt.clientY);

            if (bulkStarts) {
                // 일괄 이동
                for (const [aid, sp] of bulkStarts) {
                    const el = inst.svg.querySelector(`.ap-asset-icon[data-asset-id="${aid}"]`);
                    if (!el) continue;
                    const sz = getIconSize(el);
                    const nx = clampX(sp.x + d.dx, sz);
                    const ny = clampY(sp.y + d.dy, sz);
                    el.setAttribute('transform', `translate(${nx}, ${ny})`);
                    el.dataset.x = nx;
                    el.dataset.y = ny;
                }
            } else {
                // 단일 이동
                const sz = getIconSize(group);
                const nx = clampX(startPos.x + d.dx, sz);
                const ny = clampY(startPos.y + d.dy, sz);
                group.setAttribute('transform', `translate(${nx}, ${ny})`);
                group.dataset.x = nx;
                group.dataset.y = ny;
            }
        };

        const onUp = (e) => {
            document.removeEventListener('pointermove', onMove);
            document.removeEventListener('pointerup', onUp);

            if (!moved && inst.mode === 'multi' && !e.ctrlKey && !e.metaKey) {
                // 클릭만 한 경우: 단독 선택
                selectOnly(inst, assetId);
            }

            if (moved && inst.dotNetRef) {
                if (bulkStarts) {
                    const positions = [];
                    for (const [aid] of bulkStarts) {
                        const el = inst.svg.querySelector(`.ap-asset-icon[data-asset-id="${aid}"]`);
                        if (!el) continue;
                        positions.push({ assetId: aid, x: r2(parseFloat(el.dataset.x)), y: r2(parseFloat(el.dataset.y)) });
                    }
                    inst.dotNetRef.invokeMethodAsync('OnBulkMoved', positions);
                } else {
                    inst.dotNetRef.invokeMethodAsync('OnAssetMoved', assetId,
                        r2(parseFloat(group.dataset.x)), r2(parseFloat(group.dataset.y)));
                }
            }

            startPt = null;
            startPos = null;
            bulkStarts = null;
        };

        group.addEventListener('pointerdown', onDown);
        inst.handlers.push({ el: group, type: 'pointerdown', fn: onDown });
    }

    // ── 그룹 드래그 ──
    function bindGroupDrag(inst, groupEl, groupId) {
        const rect = groupEl.querySelector('rect:first-of-type');
        if (!rect) return;

        let startPt = null;
        let startGrp = null;
        let memberStarts = null;

        const onDown = (e) => {
            if (e.target.classList.contains('ap-group-resize')) return;
            e.preventDefault();
            e.stopPropagation();

            startPt = { clientX: e.clientX, clientY: e.clientY };
            startGrp = {
                x: parseFloat(groupEl.dataset.x) || 0,
                y: parseFloat(groupEl.dataset.y) || 0,
            };

            // 멤버 자산 시작 위치 수집
            memberStarts = collectGroupMemberStarts(inst, groupEl);

            document.addEventListener('pointermove', onMove);
            document.addEventListener('pointerup', onUp);
            groupEl.classList.add('dragging');
            groupEl.setPointerCapture(e.pointerId);
        };

        const onMove = (e) => {
            if (!startPt) return;
            const d = clientDelta(inst, e.clientX - startPt.clientX, e.clientY - startPt.clientY);
            const nx = Math.max(0, startGrp.x + d.dx);
            const ny = Math.max(0, startGrp.y + d.dy);

            updateGroupPosition(groupEl, nx, ny);

            // 멤버 자산도 동일 delta 이동
            if (memberStarts) {
                for (const [aid, sp] of memberStarts) {
                    const el = inst.svg.querySelector(`.ap-asset-icon[data-asset-id="${aid}"]`);
                    if (!el) continue;
                    const sz = getIconSize(el);
                    const ax = clampX(sp.x + d.dx, sz);
                    const ay = clampY(sp.y + d.dy, sz);
                    el.setAttribute('transform', `translate(${ax}, ${ay})`);
                    el.dataset.x = ax;
                    el.dataset.y = ay;
                }
            }
        };

        const onUp = () => {
            document.removeEventListener('pointermove', onMove);
            document.removeEventListener('pointerup', onUp);
            groupEl.classList.remove('dragging');

            if (startPt && inst.dotNetRef) {
                const gx = r2(parseFloat(groupEl.dataset.x));
                const gy = r2(parseFloat(groupEl.dataset.y));
                const gw = r2(parseFloat(groupEl.dataset.w));
                const gh = r2(parseFloat(groupEl.dataset.h));
                inst.dotNetRef.invokeMethodAsync('OnGroupMoved', groupId, gx, gy, gw, gh);

                // 멤버 자산 위치도 콜백
                if (memberStarts) {
                    const positions = [];
                    for (const [aid] of memberStarts) {
                        const el = inst.svg.querySelector(`.ap-asset-icon[data-asset-id="${aid}"]`);
                        if (!el) continue;
                        positions.push({ assetId: aid, x: r2(parseFloat(el.dataset.x)), y: r2(parseFloat(el.dataset.y)) });
                    }
                    if (positions.length > 0) inst.dotNetRef.invokeMethodAsync('OnBulkMoved', positions);
                }
            }
            startPt = null;
            memberStarts = null;
        };

        rect.addEventListener('pointerdown', onDown);
        inst.handlers.push({ el: rect, type: 'pointerdown', fn: onDown });
    }

    // ── 그룹 리사이즈 ──
    function bindGroupResize(inst, groupEl, handle, groupId) {
        let startPt = null;
        let startSize = null;

        const onDown = (e) => {
            e.preventDefault();
            e.stopPropagation();
            startPt = { clientX: e.clientX, clientY: e.clientY };
            startSize = {
                w: parseFloat(groupEl.dataset.w) || 150,
                h: parseFloat(groupEl.dataset.h) || 100,
            };
            document.addEventListener('pointermove', onMove);
            document.addEventListener('pointerup', onUp);
            handle.setPointerCapture(e.pointerId);
        };

        const onMove = (e) => {
            if (!startPt) return;
            const d = clientDelta(inst, e.clientX - startPt.clientX, e.clientY - startPt.clientY);
            const nw = Math.max(30, startSize.w + d.dx);
            const nh = Math.max(20, startSize.h + d.dy);
            updateGroupSize(groupEl, nw, nh);
        };

        const onUp = () => {
            document.removeEventListener('pointermove', onMove);
            document.removeEventListener('pointerup', onUp);
            if (startPt && inst.dotNetRef) {
                inst.dotNetRef.invokeMethodAsync('OnGroupMoved', groupId,
                    r2(parseFloat(groupEl.dataset.x)),
                    r2(parseFloat(groupEl.dataset.y)),
                    r2(parseFloat(groupEl.dataset.w)),
                    r2(parseFloat(groupEl.dataset.h)));
            }
            startPt = null;
        };

        handle.addEventListener('pointerdown', onDown);
        inst.handlers.push({ el: handle, type: 'pointerdown', fn: onDown });
    }

    // ── 올가미 선택 ──
    function bindLasso(inst) {
        let startSvg = null;
        let lassoRect = null;

        const onDown = (e) => {
            // 자산이나 그룹 위에서 시작하면 무시
            if (inst.mode !== 'multi') return;
            if (e.target.closest('.ap-asset-icon') || e.target.closest('.ap-group-container')) return;

            e.preventDefault();
            startSvg = clientToSvg(inst, e.clientX, e.clientY);

            // 올가미 rect 생성
            lassoRect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
            lassoRect.classList.add('ap-lasso-rect');
            lassoRect.setAttribute('x', startSvg.x);
            lassoRect.setAttribute('y', startSvg.y);
            lassoRect.setAttribute('width', 0);
            lassoRect.setAttribute('height', 0);
            inst.svg.appendChild(lassoRect);

            document.addEventListener('pointermove', onMove);
            document.addEventListener('pointerup', onUp);
        };

        const onMove = (e) => {
            if (!startSvg || !lassoRect) return;
            const cur = clientToSvg(inst, e.clientX, e.clientY);
            const x = Math.min(startSvg.x, cur.x);
            const y = Math.min(startSvg.y, cur.y);
            const w = Math.abs(cur.x - startSvg.x);
            const h = Math.abs(cur.y - startSvg.y);
            lassoRect.setAttribute('x', x);
            lassoRect.setAttribute('y', y);
            lassoRect.setAttribute('width', w);
            lassoRect.setAttribute('height', h);
        };

        const onUp = (e) => {
            document.removeEventListener('pointermove', onMove);
            document.removeEventListener('pointerup', onUp);

            if (startSvg && lassoRect) {
                const cur = clientToSvg(inst, e.clientX, e.clientY);
                const lx1 = Math.min(startSvg.x, cur.x);
                const ly1 = Math.min(startSvg.y, cur.y);
                const lx2 = Math.max(startSvg.x, cur.x);
                const ly2 = Math.max(startSvg.y, cur.y);

                // 올가미 범위에 중심이 포함된 자산 선택
                if (lx2 - lx1 > 5 || ly2 - ly1 > 5) {
                    inst.selectedIds.clear();
                    inst.svg.querySelectorAll('.ap-asset-icon').forEach(g => {
                        const sz = getIconSize(g);
                        const ax = (parseFloat(g.dataset.x) || 0) + sz / 2;
                        const ay = (parseFloat(g.dataset.y) || 0) + sz / 2;
                        if (ax >= lx1 && ax <= lx2 && ay >= ly1 && ay <= ly2) {
                            inst.selectedIds.add(parseInt(g.dataset.assetId));
                        }
                    });
                    updateSelectionVisual(inst);
                    if (inst.dotNetRef) {
                        inst.dotNetRef.invokeMethodAsync('OnSelectionChanged', [...inst.selectedIds]);
                    }
                }

                lassoRect.remove();
            }
            startSvg = null;
            lassoRect = null;
        };

        inst.svg.addEventListener('pointerdown', onDown);
        inst.handlers.push({ el: inst.svg, type: 'pointerdown', fn: onDown });
    }

    // ── 선택 헬퍼 ──
    function toggleSelect(inst, assetId, el) {
        if (inst.selectedIds.has(assetId)) {
            inst.selectedIds.delete(assetId);
            el.classList.remove('ap-selected');
        } else {
            inst.selectedIds.add(assetId);
            el.classList.add('ap-selected');
        }
        if (inst.dotNetRef) inst.dotNetRef.invokeMethodAsync('OnSelectionChanged', [...inst.selectedIds]);
    }

    function selectOnly(inst, assetId) {
        inst.selectedIds.clear();
        inst.selectedIds.add(assetId);
        updateSelectionVisual(inst);
        if (inst.dotNetRef) inst.dotNetRef.invokeMethodAsync('OnSelectionChanged', [...inst.selectedIds]);
    }

    function updateSelectionVisual(inst) {
        inst.svg.querySelectorAll('.ap-asset-icon').forEach(g => {
            const aid = parseInt(g.dataset.assetId);
            g.classList.toggle('ap-selected', inst.selectedIds.has(aid));
        });
    }

    function collectBulkStarts(inst) {
        const map = new Map();
        for (const aid of inst.selectedIds) {
            const el = inst.svg.querySelector(`.ap-asset-icon[data-asset-id="${aid}"]`);
            if (el) map.set(aid, { x: parseFloat(el.dataset.x) || 0, y: parseFloat(el.dataset.y) || 0 });
        }
        return map;
    }

    function collectGroupMemberStarts(inst, groupEl) {
        const map = new Map();
        const members = groupEl.dataset.members;
        if (!members) return map;
        members.split(',').forEach(s => {
            const aid = parseInt(s);
            if (isNaN(aid)) return;
            const el = inst.svg.querySelector(`.ap-asset-icon[data-asset-id="${aid}"]`);
            if (el) map.set(aid, { x: parseFloat(el.dataset.x) || 0, y: parseFloat(el.dataset.y) || 0 });
        });
        return map;
    }

    // ── 그룹 위치/크기 업데이트 ──
    function updateGroupPosition(groupEl, x, y) {
        groupEl.dataset.x = x;
        groupEl.dataset.y = y;
        const rect = groupEl.querySelector('rect:first-of-type');
        if (rect) { rect.setAttribute('x', x); rect.setAttribute('y', y); }
        const label = groupEl.querySelector('.ap-group-label');
        if (label) { label.setAttribute('x', x + 4); label.setAttribute('y', y - 4); }
        const resize = groupEl.querySelector('.ap-group-resize');
        if (resize) {
            const w = parseFloat(groupEl.dataset.w) || 150;
            const h = parseFloat(groupEl.dataset.h) || 100;
            resize.setAttribute('x', x + w - 8);
            resize.setAttribute('y', y + h - 8);
        }
    }

    function updateGroupSize(groupEl, w, h) {
        groupEl.dataset.w = w;
        groupEl.dataset.h = h;
        const rect = groupEl.querySelector('rect:first-of-type');
        if (rect) { rect.setAttribute('width', w); rect.setAttribute('height', h); }
        const resize = groupEl.querySelector('.ap-group-resize');
        if (resize) {
            const x = parseFloat(groupEl.dataset.x) || 0;
            const y = parseFloat(groupEl.dataset.y) || 0;
            resize.setAttribute('x', x + w - 8);
            resize.setAttribute('y', y + h - 8);
        }
    }

    function clampX(v, sz) { return Math.max(0, Math.min(VB_W - (sz || ICON_SIZE), v)); }
    function clampY(v, sz) { return Math.max(0, Math.min(VB_H - (sz || ICON_SIZE), v)); }

    // ── 외부 API ──
    function setMode(containerId, mode) {
        const inst = _inst[containerId];
        if (inst) {
            inst.mode = mode;
            if (mode === 'single') {
                inst.selectedIds.clear();
                updateSelectionVisual(inst);
            }
        }
    }

    function clearSelection(containerId) {
        const inst = _inst[containerId];
        if (inst) {
            inst.selectedIds.clear();
            updateSelectionVisual(inst);
        }
    }

    function getPositions(containerId) {
        const container = document.getElementById(containerId);
        if (!container) return [];
        const svg = container.querySelector('svg');
        if (!svg) return [];

        const result = [];
        svg.querySelectorAll('.ap-asset-icon').forEach(g => {
            const assetId = parseInt(g.dataset.assetId);
            if (isNaN(assetId)) return;
            result.push({ assetId, x: r2(parseFloat(g.dataset.x || 0)), y: r2(parseFloat(g.dataset.y || 0)) });
        });
        return result;
    }

    function getGroupPositions(containerId) {
        const container = document.getElementById(containerId);
        if (!container) return [];
        const svg = container.querySelector('svg');
        if (!svg) return [];

        const result = [];
        svg.querySelectorAll('.ap-group-container').forEach(g => {
            const groupId = parseInt(g.dataset.groupId);
            if (isNaN(groupId)) return;
            result.push({
                groupId,
                x: r2(parseFloat(g.dataset.x || 0)),
                y: r2(parseFloat(g.dataset.y || 0)),
                w: r2(parseFloat(g.dataset.w || 150)),
                h: r2(parseFloat(g.dataset.h || 100)),
            });
        });
        return result;
    }

    function dispose(containerId) {
        const inst = _inst[containerId];
        if (!inst) return;
        inst.handlers.forEach(({ el, type, fn }) => el.removeEventListener(type, fn));
        inst.handlers.length = 0;
        delete _inst[containerId];
    }

    return { init, getPositions, getGroupPositions, dispose, setMode, clearSelection };
})();
