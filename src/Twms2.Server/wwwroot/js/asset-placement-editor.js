/**
 * Asset Placement Editor v2 — 전면 재작성
 * 파워포인트형 SVG 에디터: 줌/팬, 올가미, 드래그, 스냅, 그룹, 격자 배치
 * C#이 상태의 단일 진실 원천, JS는 인터랙션 레이어.
 */
window.assetPlacementEditor = (() => {
    const _inst = {};
    const SVG_NS = 'http://www.w3.org/2000/svg';
    const ORIG_W = 1000, ORIG_H = 600;
    const MIN_ZOOM = 0.3, MAX_ZOOM = 8;
    const r2 = v => Math.round(v * 100) / 100;

    // ════════════════ Init / Dispose ════════════════

    function init(containerId, dotNetRef) {
        dispose(containerId);
        const container = document.getElementById(containerId);
        if (!container) return;
        const svg = container.querySelector('svg');
        if (!svg) return;

        const inst = {
            container, svg, dotNetRef,
            viewBox: { x: 0, y: 0, w: ORIG_W, h: ORIG_H },
            zoom: 1,
            tool: 'select',        // 'select' | 'pan'
            snap: { enabled: true, gridSize: 20, neighborThreshold: 8 },
            // Transient state
            _handlers: [],
            _observer: null,
            _gridPreview: null,
            _guideLines: [],
        };
        _inst[containerId] = inst;

        // 이벤트 바인딩
        bindZoomPan(inst);
        bindSvgPointer(inst);
        bindExistingElements(inst);
        bindKeyboard(inst);
        startObserver(inst);
    }

    function dispose(containerId) {
        const inst = _inst[containerId];
        if (!inst) return;
        if (inst._observer) inst._observer.disconnect();
        inst._handlers.forEach(([el, type, fn, opts]) => el.removeEventListener(type, fn, opts));
        clearGuideLines(inst);
        hideGridPreview(containerId);
        delete _inst[containerId];
    }

    function on(inst, el, type, fn, opts) {
        el.addEventListener(type, fn, opts);
        inst._handlers.push([el, type, fn, opts]);
    }

    // ════════════════ Coordinate Transform ════════════════

    function clientToSvg(inst, cx, cy) {
        const ctm = inst.svg.getScreenCTM();
        if (ctm) {
            const inv = ctm.inverse();
            return {
                x: inv.a * cx + inv.c * cy + inv.e,
                y: inv.b * cx + inv.d * cy + inv.f
            };
        }
        // fallback
        const r = inst.svg.getBoundingClientRect();
        const vb = inst.viewBox;
        return {
            x: vb.x + ((cx - r.left) / r.width) * vb.w,
            y: vb.y + ((cy - r.top) / r.height) * vb.h
        };
    }

    function clientDelta(inst, dx, dy) {
        const ctm = inst.svg.getScreenCTM();
        if (ctm) {
            const inv = ctm.inverse();
            return { dx: inv.a * dx + inv.c * dy, dy: inv.b * dx + inv.d * dy };
        }
        // fallback
        const r = inst.svg.getBoundingClientRect();
        const vb = inst.viewBox;
        return { dx: (dx / r.width) * vb.w, dy: (dy / r.height) * vb.h };
    }

    // ════════════════ ViewBox Zoom / Pan ════════════════

    function bindZoomPan(inst) {
        // Wheel zoom
        on(inst, inst.svg, 'wheel', (e) => {
            e.preventDefault();
            const factor = Math.pow(1.002, -e.deltaY);
            zoomByFactor(inst, factor, e.clientX, e.clientY);
        }, { passive: false });
    }

    function zoomByFactor(inst, factor, cx, cy) {
        const newZoom = Math.max(MIN_ZOOM, Math.min(MAX_ZOOM, inst.zoom * factor));
        if (Math.abs(newZoom - inst.zoom) < 0.001) return;
        const svgPt = clientToSvg(inst, cx, cy);
        const newW = ORIG_W / newZoom, newH = ORIG_H / newZoom;
        // Ratio of cursor position within the rendered SVG area
        const ctm = inst.svg.getScreenCTM();
        let ratioX = 0.5, ratioY = 0.5;
        if (ctm) {
            // Rendered content origin in screen coords
            const vb = inst.viewBox;
            const ox = ctm.a * vb.x + ctm.e;
            const oy = ctm.d * vb.y + ctm.f;
            const rw = ctm.a * vb.w;
            const rh = ctm.d * vb.h;
            ratioX = rw !== 0 ? (cx - ox) / rw : 0.5;
            ratioY = rh !== 0 ? (cy - oy) / rh : 0.5;
        }
        inst.viewBox = {
            x: clampVBX(svgPt.x - newW * ratioX, newW),
            y: clampVBY(svgPt.y - newH * ratioY, newH),
            w: newW, h: newH
        };
        inst.zoom = newZoom;
        applyViewBox(inst);
    }

    function clampVBX(x, w) { const m = w * 0.3; return Math.max(-m, Math.min(ORIG_W - w + m, x)); }
    function clampVBY(y, h) { const m = h * 0.3; return Math.max(-m, Math.min(ORIG_H - h + m, y)); }

    function applyViewBox(inst) {
        const vb = inst.viewBox;
        inst.svg.setAttribute('viewBox', `${vb.x.toFixed(2)} ${vb.y.toFixed(2)} ${vb.w.toFixed(2)} ${vb.h.toFixed(2)}`);
    }

    function getViewCenter(inst) {
        return { x: inst.viewBox.x + inst.viewBox.w / 2, y: inst.viewBox.y + inst.viewBox.h / 2 };
    }

    // ════════════════ SVG Pointer Dispatch ════════════════

    function bindSvgPointer(inst) {
        let action = null; // { type: 'pan'|'lasso'|'move'|'groupMove'|'groupResize', ... }

        on(inst, inst.svg, 'pointerdown', (e) => {
            if (e.button === 1) { // Middle button = always pan
                e.preventDefault();
                action = startPan(inst, e);
                return;
            }
            if (e.button !== 0) return;

            // Resize handle?
            const resizeHandle = e.target.closest('.ap-group-resize');
            if (resizeHandle) {
                e.preventDefault(); e.stopPropagation();
                const groupEl = resizeHandle.closest('.ap-group-container');
                action = startGroupResize(inst, e, groupEl);
                return;
            }

            // Group container (rect background)?
            const groupEl = e.target.closest('.ap-group-container');
            const assetEl = e.target.closest('.ap-asset-icon');

            if (inst.tool === 'pan') {
                e.preventDefault();
                action = startPan(inst, e);
            } else if (assetEl) {
                e.preventDefault(); e.stopPropagation();
                action = startAssetInteraction(inst, e, assetEl);
            } else if (groupEl && !assetEl) {
                e.preventDefault(); e.stopPropagation();
                action = startGroupInteraction(inst, e, groupEl);
            } else {
                // Empty area → lasso
                e.preventDefault();
                action = startLasso(inst, e);
            }
        });

        on(inst, document, 'pointermove', (e) => {
            if (!action) return;
            action.onMove(e);
        });

        on(inst, document, 'pointerup', (e) => {
            if (!action) return;
            action.onUp(e);
            action = null;
        });
    }

    // ════════════════ Pan ════════════════

    function startPan(inst, e) {
        const startPt = { x: e.clientX, y: e.clientY };
        const startVB = { ...inst.viewBox };
        inst.svg.style.cursor = 'grabbing';
        return {
            onMove(e) {
                const r = inst.svg.getBoundingClientRect();
                const dx = ((e.clientX - startPt.x) / r.width) * inst.viewBox.w;
                const dy = ((e.clientY - startPt.y) / r.height) * inst.viewBox.h;
                inst.viewBox.x = clampVBX(startVB.x - dx, inst.viewBox.w);
                inst.viewBox.y = clampVBY(startVB.y - dy, inst.viewBox.h);
                applyViewBox(inst);
            },
            onUp() { inst.svg.style.cursor = ''; }
        };
    }

    // ════════════════ Lasso Selection ════════════════

    function startLasso(inst, e) {
        const start = clientToSvg(inst, e.clientX, e.clientY);
        let lassoRect = null;
        let moved = false;

        return {
            onMove(ev) {
                const cur = clientToSvg(inst, ev.clientX, ev.clientY);
                if (!moved && Math.abs(cur.x - start.x) + Math.abs(cur.y - start.y) < 3) return;
                moved = true;
                if (!lassoRect) {
                    lassoRect = document.createElementNS(SVG_NS, 'rect');
                    lassoRect.classList.add('ap-lasso-rect');
                    inst.svg.appendChild(lassoRect);
                }
                const x = Math.min(start.x, cur.x), y = Math.min(start.y, cur.y);
                const w = Math.abs(cur.x - start.x), h = Math.abs(cur.y - start.y);
                lassoRect.setAttribute('x', x); lassoRect.setAttribute('y', y);
                lassoRect.setAttribute('width', w); lassoRect.setAttribute('height', h);
            },
            onUp(ev) {
                if (lassoRect) {
                    const cur = clientToSvg(inst, ev.clientX, ev.clientY);
                    const lx1 = Math.min(start.x, cur.x), ly1 = Math.min(start.y, cur.y);
                    const lx2 = Math.max(start.x, cur.x), ly2 = Math.max(start.y, cur.y);

                    if (lx2 - lx1 > 3 || ly2 - ly1 > 3) {
                        const assetIds = [], groupIds = [];
                        inst.svg.querySelectorAll('.ap-asset-icon').forEach(g => {
                            const ax = parseFloat(g.dataset.x) || 0;
                            const ay = parseFloat(g.dataset.y) || 0;
                            const sz = getIconSize(g);
                            const cx = ax + sz / 2, cy = ay + sz / 2;
                            if (cx >= lx1 && cx <= lx2 && cy >= ly1 && cy <= ly2)
                                assetIds.push(parseInt(g.dataset.assetId));
                        });
                        inst.svg.querySelectorAll('.ap-group-container').forEach(g => {
                            const gx = parseFloat(g.dataset.x) || 0, gy = parseFloat(g.dataset.y) || 0;
                            const gw = parseFloat(g.dataset.w) || 0, gh = parseFloat(g.dataset.h) || 0;
                            const cx = gx + gw / 2, cy = gy + gh / 2;
                            if (cx >= lx1 && cx <= lx2 && cy >= ly1 && cy <= ly2)
                                groupIds.push(parseInt(g.dataset.groupId));
                        });
                        notifySelection(inst, assetIds, groupIds);
                    }
                    lassoRect.remove();
                } else if (!moved) {
                    // Click on empty → deselect all
                    notifySelection(inst, [], []);
                }
            }
        };
    }

    // ════════════════ Unified Bulk State ════════════════

    function collectAllSelectedStarts(inst) {
        const assets = new Map();  // assetId → { x, y }
        const groups = new Map();  // groupId → { x, y, w, h, el, members: Map }
        const movedAssetIds = new Set(); // track assets moved as part of groups

        // Selected groups + their members
        inst.svg.querySelectorAll('.ap-group-container.ap-group-selected').forEach(gel => {
            const gid = parseInt(gel.dataset.groupId);
            const memberStarts = collectGroupMemberStarts(inst, gel);
            groups.set(gid, {
                x: parseFloat(gel.dataset.x) || 0, y: parseFloat(gel.dataset.y) || 0,
                w: parseFloat(gel.dataset.w) || 0, h: parseFloat(gel.dataset.h) || 0,
                el: gel, members: memberStarts
            });
            for (const aid of memberStarts.keys()) movedAssetIds.add(aid);
        });

        // Selected assets (not already in a selected group)
        inst.svg.querySelectorAll('.ap-asset-icon.ap-selected').forEach(g => {
            const aid = parseInt(g.dataset.assetId);
            if (!movedAssetIds.has(aid)) {
                assets.set(aid, { x: parseFloat(g.dataset.x) || 0, y: parseFloat(g.dataset.y) || 0 });
            }
        });

        return { assets, groups, totalCount: assets.size + groups.size };
    }

    // ════════════════ Asset Interaction ════════════════

    function startAssetInteraction(inst, e, assetEl) {
        const assetId = parseInt(assetEl.dataset.assetId);
        const startClient = { x: e.clientX, y: e.clientY };
        const startPos = { x: parseFloat(assetEl.dataset.x) || 0, y: parseFloat(assetEl.dataset.y) || 0 };
        let moved = false;
        const isSelected = assetEl.classList.contains('ap-selected');

        // Ctrl+Click = toggle selection
        if (e.ctrlKey || e.metaKey) {
            inst.dotNetRef.invokeMethodAsync('OnToggleAssetSelection', assetId);
            return { onMove() {}, onUp() {} };
        }

        // Collect all selected items for bulk move (if this asset is part of selection)
        let bulk = null;
        if (isSelected) {
            bulk = collectAllSelectedStarts(inst);
            // Also include this asset if not already there
            if (!bulk.assets.has(assetId)) {
                let inGroup = false;
                for (const [, gs] of bulk.groups) { if (gs.members.has(assetId)) { inGroup = true; break; } }
                if (!inGroup) bulk.assets.set(assetId, { ...startPos });
            }
        }

        const isBulk = bulk && (bulk.assets.size + bulk.groups.size > 1 ||
            (bulk.groups.size === 1 && bulk.assets.size >= 0) ||
            bulk.assets.size > 1);

        return {
            onMove(ev) {
                const d = clientDelta(inst, ev.clientX - startClient.x, ev.clientY - startClient.y);
                if (!moved && Math.abs(d.dx) + Math.abs(d.dy) < 2) return;
                moved = true;

                // Snap based on reference point
                let snapDx = 0, snapDy = 0;
                if (inst.snap.enabled) {
                    const rawX = startPos.x + d.dx, rawY = startPos.y + d.dy;
                    const sz = getIconSize(assetEl);
                    const allMovingIds = new Set(isBulk ? bulk.assets.keys() : [assetId]);
                    if (isBulk) { for (const [,gs] of bulk.groups) { for (const aid of gs.members.keys()) allMovingIds.add(aid); } }
                    const snapped = applySnap(inst, rawX, rawY, sz, allMovingIds);
                    renderGuideLines(inst, snapped.guides);
                    snapDx = snapped.x - rawX;
                    snapDy = snapped.y - rawY;
                }

                if (isBulk) {
                    // Move all selected assets
                    for (const [aid, sp] of bulk.assets) {
                        const el = inst.svg.querySelector(`.ap-asset-icon[data-asset-id="${aid}"]`);
                        if (!el) continue;
                        const nx = r2(sp.x + d.dx + snapDx), ny = r2(sp.y + d.dy + snapDy);
                        el.dataset.x = nx; el.dataset.y = ny;
                        applyAssetTranslate(el);
                    }
                    // Move all selected groups + their members
                    for (const [, gs] of bulk.groups) {
                        const gnx = r2(gs.x + d.dx + snapDx), gny = r2(gs.y + d.dy + snapDy);
                        updateGroupPos(gs.el, gnx, gny);
                        for (const [aid, sp] of gs.members) {
                            const el = inst.svg.querySelector(`.ap-asset-icon[data-asset-id="${aid}"]`);
                            if (!el) continue;
                            const ax = r2(sp.x + d.dx + snapDx), ay = r2(sp.y + d.dy + snapDy);
                            el.setAttribute('transform', `translate(${ax},${ay})`);
                            el.dataset.x = ax; el.dataset.y = ay;
                        }
                    }
                } else {
                    // Single asset move
                    const sz = getIconSize(assetEl);
                    const snapped = applySnap(inst, startPos.x + d.dx, startPos.y + d.dy, sz, new Set([assetId]));
                    renderGuideLines(inst, snapped.guides);
                    assetEl.dataset.x = snapped.x; assetEl.dataset.y = snapped.y;
                    applyAssetTranslate(assetEl);
                    highlightGroupUnderPoint(inst, snapped.x, snapped.y);
                }
            },
            onUp() {
                clearGuideLines(inst);
                clearGroupHighlight(inst);

                if (!moved) {
                    // Click without move → select only this (unless already in multi-select)
                    if (!isSelected) notifySelection(inst, [assetId], []);
                    return;
                }

                // Report moved positions
                const positions = [];
                const groups = [];
                if (isBulk) {
                    for (const [aid] of bulk.assets) {
                        const el = inst.svg.querySelector(`.ap-asset-icon[data-asset-id="${aid}"]`);
                        if (el) positions.push({ assetId: aid, x: r2(parseFloat(el.dataset.x)), y: r2(parseFloat(el.dataset.y)) });
                    }
                    for (const [gid, gs] of bulk.groups) {
                        groups.push({ groupId: gid, x: r2(parseFloat(gs.el.dataset.x)), y: r2(parseFloat(gs.el.dataset.y)), w: r2(parseFloat(gs.el.dataset.w)), h: r2(parseFloat(gs.el.dataset.h)) });
                        for (const [aid] of gs.members) {
                            const el = inst.svg.querySelector(`.ap-asset-icon[data-asset-id="${aid}"]`);
                            if (el) positions.push({ assetId: aid, x: r2(parseFloat(el.dataset.x)), y: r2(parseFloat(el.dataset.y)) });
                        }
                    }
                    inst.dotNetRef.invokeMethodAsync('OnBulkMoved', positions, groups);
                } else {
                    positions.push({ assetId, x: r2(parseFloat(assetEl.dataset.x)), y: r2(parseFloat(assetEl.dataset.y)) });
                    handleGroupDrop(inst, assetId, parseFloat(assetEl.dataset.x), parseFloat(assetEl.dataset.y));
                    if (positions.length > 0) inst.dotNetRef.invokeMethodAsync('OnItemsMoved', positions);
                }
                if (isBulk) setTimeout(() => updateSelectionBBox(inst), 50);
            }
        };
    }

    // ════════════════ Group Interaction ════════════════

    function startGroupInteraction(inst, e, groupEl) {
        const groupId = parseInt(groupEl.dataset.groupId);
        const startClient = { x: e.clientX, y: e.clientY };
        const startGrp = { x: parseFloat(groupEl.dataset.x) || 0, y: parseFloat(groupEl.dataset.y) || 0 };
        let moved = false;
        const isSelected = groupEl.classList.contains('ap-group-selected');

        if (e.ctrlKey || e.metaKey) {
            inst.dotNetRef.invokeMethodAsync('OnToggleGroupSelection', groupId);
            return { onMove() {}, onUp() {} };
        }

        // Collect all selected items for bulk move
        let bulk = null;
        if (isSelected) {
            bulk = collectAllSelectedStarts(inst);
        }
        // If not part of selection, just move this group + members
        if (!bulk || bulk.groups.size === 0) {
            const memberStarts = collectGroupMemberStarts(inst, groupEl);
            bulk = {
                assets: new Map(),
                groups: new Map([[groupId, { x: startGrp.x, y: startGrp.y,
                    w: parseFloat(groupEl.dataset.w) || 0, h: parseFloat(groupEl.dataset.h) || 0,
                    el: groupEl, members: memberStarts }]]),
                totalCount: 1
            };
        }

        return {
            onMove(ev) {
                const d = clientDelta(inst, ev.clientX - startClient.x, ev.clientY - startClient.y);
                if (!moved && Math.abs(d.dx) + Math.abs(d.dy) < 2) return;
                moved = true;

                let snapDx = 0, snapDy = 0;
                if (inst.snap.enabled) {
                    const nx = snapToGrid(startGrp.x + d.dx, inst.snap.gridSize);
                    const ny = snapToGrid(startGrp.y + d.dy, inst.snap.gridSize);
                    snapDx = nx - (startGrp.x + d.dx);
                    snapDy = ny - (startGrp.y + d.dy);
                }

                // Move all selected assets
                for (const [aid, sp] of bulk.assets) {
                    const el = inst.svg.querySelector(`.ap-asset-icon[data-asset-id="${aid}"]`);
                    if (!el) continue;
                    const ax = r2(sp.x + d.dx + snapDx), ay = r2(sp.y + d.dy + snapDy);
                    el.setAttribute('transform', `translate(${ax},${ay})`);
                    el.dataset.x = ax; el.dataset.y = ay;
                }
                // Move all selected groups + members
                for (const [, gs] of bulk.groups) {
                    const gnx = r2(gs.x + d.dx + snapDx), gny = r2(gs.y + d.dy + snapDy);
                    updateGroupPos(gs.el, gnx, gny);
                    for (const [aid, sp] of gs.members) {
                        const el = inst.svg.querySelector(`.ap-asset-icon[data-asset-id="${aid}"]`);
                        if (!el) continue;
                        const ax = r2(sp.x + d.dx + snapDx), ay = r2(sp.y + d.dy + snapDy);
                        el.setAttribute('transform', `translate(${ax},${ay})`);
                        el.dataset.x = ax; el.dataset.y = ay;
                    }
                }
            },
            onUp() {
                if (!moved) {
                    if (!isSelected) notifySelection(inst, [], [groupId]);
                    return;
                }
                // Report all moved positions
                const positions = [];
                const groups = [];
                for (const [aid] of bulk.assets) {
                    const el = inst.svg.querySelector(`.ap-asset-icon[data-asset-id="${aid}"]`);
                    if (el) positions.push({ assetId: aid, x: r2(parseFloat(el.dataset.x)), y: r2(parseFloat(el.dataset.y)) });
                }
                for (const [gid, gs] of bulk.groups) {
                    groups.push({ groupId: gid, x: r2(parseFloat(gs.el.dataset.x)), y: r2(parseFloat(gs.el.dataset.y)), w: r2(parseFloat(gs.el.dataset.w)), h: r2(parseFloat(gs.el.dataset.h)) });
                    for (const [aid] of gs.members) {
                        const el = inst.svg.querySelector(`.ap-asset-icon[data-asset-id="${aid}"]`);
                        if (el) positions.push({ assetId: aid, x: r2(parseFloat(el.dataset.x)), y: r2(parseFloat(el.dataset.y)) });
                    }
                }
                inst.dotNetRef.invokeMethodAsync('OnBulkMoved', positions, groups);
                setTimeout(() => updateSelectionBBox(inst), 50);
            }
        };
    }

    // ════════════════ Group Resize ════════════════

    function startGroupResize(inst, e, groupEl) {
        const groupId = parseInt(groupEl.dataset.groupId);
        const startClient = { x: e.clientX, y: e.clientY };
        const startSize = { w: parseFloat(groupEl.dataset.w) || 150, h: parseFloat(groupEl.dataset.h) || 100 };
        let rafId = 0;

        return {
            onMove(ev) {
                if (rafId) return;
                rafId = requestAnimationFrame(() => {
                    rafId = 0;
                    const d = clientDelta(inst, ev.clientX - startClient.x, ev.clientY - startClient.y);
                    let nw = Math.max(4, startSize.w + d.dx);
                    let nh = Math.max(4, startSize.h + d.dy);
                    if (inst.snap.enabled) {
                        nw = snapToGrid(nw, inst.snap.gridSize);
                        nh = snapToGrid(nh, inst.snap.gridSize);
                    }
                    updateGroupSize(groupEl, nw, nh);
                });
            },
            onUp() {
                if (rafId) { cancelAnimationFrame(rafId); rafId = 0; }
                const gx = r2(parseFloat(groupEl.dataset.x));
                const gy = r2(parseFloat(groupEl.dataset.y));
                const gw = r2(parseFloat(groupEl.dataset.w));
                const gh = r2(parseFloat(groupEl.dataset.h));
                inst.dotNetRef.invokeMethodAsync('OnGroupMoved', groupId, gx, gy, gw, gh);
            }
        };
    }

    // ════════════════ Group helpers ════════════════

    function updateGroupPos(el, x, y) {
        el.dataset.x = x; el.dataset.y = y;
        const rect = el.querySelector('rect:first-of-type');
        if (rect) { rect.setAttribute('x', x); rect.setAttribute('y', y); }
        const label = el.querySelector('.ap-group-label');
        if (label) { label.setAttribute('x', x + 4); label.setAttribute('y', y - 4); }
        const resize = el.querySelector('.ap-group-resize');
        if (resize) {
            const w = parseFloat(el.dataset.w) || 150, h = parseFloat(el.dataset.h) || 100;
            const sz = Math.min(Math.max(Math.min(w, h) * 0.15, 6), 16);
            const off = sz / 2;
            resize.setAttribute('x', x + w - off); resize.setAttribute('y', y + h - off);
        }
        // Update clipPath rect position
        const gid = el.dataset.groupId;
        if (gid) {
            const svg = el.closest('svg');
            const clip = svg && svg.querySelector(`#ap-grp-clip-${gid} rect`);
            if (clip) { clip.setAttribute('x', x); clip.setAttribute('y', y); }
        }
    }

    function updateGroupSize(el, w, h) {
        el.dataset.w = w; el.dataset.h = h;
        const rect = el.querySelector('rect:first-of-type');
        if (rect) { rect.setAttribute('width', w); rect.setAttribute('height', h); }
        const resize = el.querySelector('.ap-group-resize');
        if (resize) {
            const x = parseFloat(el.dataset.x) || 0, y = parseFloat(el.dataset.y) || 0;
            const sz = Math.min(Math.max(Math.min(w, h) * 0.15, 6), 16);
            const off = sz / 2;
            resize.setAttribute('x', x + w - off); resize.setAttribute('y', y + h - off);
            resize.setAttribute('width', sz); resize.setAttribute('height', sz);
        }
        // Update clipPath rect to match group bounds
        const gid = el.dataset.groupId;
        if (gid) {
            const svg = el.closest('svg');
            const clip = svg && svg.querySelector(`#ap-grp-clip-${gid} rect`);
            if (clip) { clip.setAttribute('width', w); clip.setAttribute('height', h); }
        }
    }

    function collectGroupMemberStarts(inst, groupEl) {
        const map = new Map();
        const members = groupEl.dataset.members || '';
        members.split(',').forEach(s => {
            const aid = parseInt(s);
            if (isNaN(aid)) return;
            const el = inst.svg.querySelector(`.ap-asset-icon[data-asset-id="${aid}"]`);
            if (el) map.set(aid, { x: parseFloat(el.dataset.x) || 0, y: parseFloat(el.dataset.y) || 0 });
        });
        return map;
    }

    // ════════════════ Group drop detection ════════════════

    function highlightGroupUnderPoint(inst, px, py) {
        clearGroupHighlight(inst);
        inst.svg.querySelectorAll('.ap-group-container').forEach(g => {
            const gx = parseFloat(g.dataset.x) || 0, gy = parseFloat(g.dataset.y) || 0;
            const gw = parseFloat(g.dataset.w) || 0, gh = parseFloat(g.dataset.h) || 0;
            if (px >= gx && px <= gx + gw && py >= gy && py <= gy + gh)
                g.classList.add('ap-group-drop-target');
        });
    }

    function clearGroupHighlight(inst) {
        inst.svg.querySelectorAll('.ap-group-drop-target').forEach(g => g.classList.remove('ap-group-drop-target'));
    }

    function handleGroupDrop(inst, assetId, cx, cy) {
        // Find current group
        let prevGroupId = null;
        inst.svg.querySelectorAll('.ap-group-container').forEach(g => {
            const members = (g.dataset.members || '').split(',').map(Number);
            if (members.includes(assetId)) prevGroupId = parseInt(g.dataset.groupId);
        });

        let droppedGroupId = null;
        inst.svg.querySelectorAll('.ap-group-container').forEach(g => {
            const gx = parseFloat(g.dataset.x) || 0, gy = parseFloat(g.dataset.y) || 0;
            const gw = parseFloat(g.dataset.w) || 0, gh = parseFloat(g.dataset.h) || 0;
            if (cx >= gx && cx <= gx + gw && cy >= gy && cy <= gy + gh)
                droppedGroupId = parseInt(g.dataset.groupId);
        });

        if (droppedGroupId !== null && droppedGroupId !== prevGroupId)
            inst.dotNetRef.invokeMethodAsync('OnAssetDroppedOnGroup', assetId, droppedGroupId);
        else if (droppedGroupId === null && prevGroupId !== null)
            inst.dotNetRef.invokeMethodAsync('OnAssetRemovedFromGroup', assetId, prevGroupId);
    }

    // ════════════════ Selection ════════════════

    function notifySelection(inst, assetIds, groupIds) {
        inst.dotNetRef.invokeMethodAsync('OnSelectionChanged', assetIds, groupIds);
        // Update bounding box after Blazor applies ap-selected classes
        setTimeout(() => updateSelectionBBox(inst), 80);
    }

    function collectBulkStarts(inst) {
        const map = new Map();
        inst.svg.querySelectorAll('.ap-asset-icon.ap-selected').forEach(g => {
            const aid = parseInt(g.dataset.assetId);
            map.set(aid, { x: parseFloat(g.dataset.x) || 0, y: parseFloat(g.dataset.y) || 0 });
        });
        return map;
    }

    // ════════════════ Selection Bounding Box + Resize ════════════════

    function computeSelectionBBox(inst) {
        let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
        let count = 0;
        inst.svg.querySelectorAll('.ap-asset-icon.ap-selected').forEach(g => {
            const cx = parseFloat(g.dataset.x) || 0, cy = parseFloat(g.dataset.y) || 0;
            const sz = getIconSize(g), half = sz / 2;
            minX = Math.min(minX, cx - half); minY = Math.min(minY, cy - half);
            maxX = Math.max(maxX, cx + half); maxY = Math.max(maxY, cy + half + 14);
            count++;
        });
        inst.svg.querySelectorAll('.ap-group-container.ap-group-selected').forEach(g => {
            const x = parseFloat(g.dataset.x) || 0, y = parseFloat(g.dataset.y) || 0;
            const w = parseFloat(g.dataset.w) || 0, h = parseFloat(g.dataset.h) || 0;
            minX = Math.min(minX, x); minY = Math.min(minY, y);
            maxX = Math.max(maxX, x + w); maxY = Math.max(maxY, y + h);
            count++;
        });
        if (count < 2) return null;
        return { x: minX, y: minY, w: maxX - minX, h: maxY - minY };
    }

    function updateSelectionBBox(inst) {
        removeSelectionBBox(inst);
        const bbox = computeSelectionBBox(inst);
        if (!bbox) return;

        const g = document.createElementNS(SVG_NS, 'g');
        g.classList.add('ap-sel-bbox');
        g.setAttribute('data-ox', bbox.x); g.setAttribute('data-oy', bbox.y);
        g.setAttribute('data-ow', bbox.w); g.setAttribute('data-oh', bbox.h);

        // Dashed outline
        const outline = document.createElementNS(SVG_NS, 'rect');
        outline.setAttribute('x', bbox.x - 4); outline.setAttribute('y', bbox.y - 4);
        outline.setAttribute('width', bbox.w + 8); outline.setAttribute('height', bbox.h + 8);
        outline.setAttribute('fill', 'none'); outline.setAttribute('stroke', '#2196F3');
        outline.setAttribute('stroke-width', 1); outline.setAttribute('stroke-dasharray', '4 2');
        outline.setAttribute('rx', 3);
        outline.classList.add('ap-sel-outline');
        g.appendChild(outline);

        // 8 resize handles: N, S, E, W, NE, NW, SE, SW
        const pad = 4, hsz = 7;
        const cx = bbox.x + bbox.w / 2 - pad, cy = bbox.y + bbox.h / 2 - pad;
        const handles = [
            { id: 'nw', x: bbox.x - pad - hsz, y: bbox.y - pad - hsz, cursor: 'nwse-resize' },
            { id: 'n',  x: cx - hsz/2,          y: bbox.y - pad - hsz, cursor: 'ns-resize' },
            { id: 'ne', x: bbox.x + bbox.w + pad, y: bbox.y - pad - hsz, cursor: 'nesw-resize' },
            { id: 'w',  x: bbox.x - pad - hsz, y: cy - hsz/2,          cursor: 'ew-resize' },
            { id: 'e',  x: bbox.x + bbox.w + pad, y: cy - hsz/2,       cursor: 'ew-resize' },
            { id: 'sw', x: bbox.x - pad - hsz, y: bbox.y + bbox.h + pad, cursor: 'nesw-resize' },
            { id: 's',  x: cx - hsz/2,          y: bbox.y + bbox.h + pad, cursor: 'ns-resize' },
            { id: 'se', x: bbox.x + bbox.w + pad, y: bbox.y + bbox.h + pad, cursor: 'nwse-resize' },
        ];
        for (const h of handles) {
            const rect = document.createElementNS(SVG_NS, 'rect');
            rect.setAttribute('x', h.x); rect.setAttribute('y', h.y);
            rect.setAttribute('width', hsz); rect.setAttribute('height', hsz);
            rect.setAttribute('rx', 1.5);
            rect.setAttribute('fill', 'white'); rect.setAttribute('stroke', '#2196F3');
            rect.setAttribute('stroke-width', 1.5);
            rect.style.cursor = h.cursor;
            rect.classList.add('ap-sel-handle');
            rect.dataset.handle = h.id;
            g.appendChild(rect);
        }

        inst.svg.appendChild(g);
        inst._selBBox = { g, bbox };

        // Bind resize on handles
        g.querySelectorAll('.ap-sel-handle').forEach(handle => {
            handle.addEventListener('pointerdown', (e) => {
                e.preventDefault(); e.stopPropagation();
                startBBoxResize(inst, handle.dataset.handle, e);
            });
        });
    }

    function removeSelectionBBox(inst) {
        if (inst._selBBox) {
            inst._selBBox.g.remove();
            inst._selBBox = null;
        }
    }

    function startBBoxResize(inst, handleId, e) {
        const origBBox = { ...inst._selBBox.bbox };
        const startClient = { x: e.clientX, y: e.clientY };
        const minW = 20, minH = 20;

        // Snapshot all selected items' positions
        const assetSnaps = [];
        inst.svg.querySelectorAll('.ap-asset-icon.ap-selected').forEach(g => {
            assetSnaps.push({
                el: g, aid: parseInt(g.dataset.assetId),
                x: parseFloat(g.dataset.x) || 0, y: parseFloat(g.dataset.y) || 0
            });
        });
        const groupSnaps = [];
        inst.svg.querySelectorAll('.ap-group-container.ap-group-selected').forEach(g => {
            groupSnaps.push({
                el: g, gid: parseInt(g.dataset.groupId),
                x: parseFloat(g.dataset.x) || 0, y: parseFloat(g.dataset.y) || 0,
                w: parseFloat(g.dataset.w) || 0, h: parseFloat(g.dataset.h) || 0,
                members: collectGroupMemberStarts(inst, g)
            });
        });

        removeSelectionBBox(inst);

        let rafId = 0;
        const doResize = (ev) => {
            const d = clientDelta(inst, ev.clientX - startClient.x, ev.clientY - startClient.y);
            let newBBox = { ...origBBox };

            // Adjust bbox based on which handle
            const affectsLeft = handleId.includes('w');
            const affectsRight = handleId.includes('e');
            const affectsTop = handleId.includes('n');
            const affectsBottom = handleId.includes('s');

            if (affectsRight) newBBox.w = Math.max(minW, origBBox.w + d.dx);
            if (affectsLeft) { newBBox.x = origBBox.x + d.dx; newBBox.w = Math.max(minW, origBBox.w - d.dx); }
            if (affectsBottom) newBBox.h = Math.max(minH, origBBox.h + d.dy);
            if (affectsTop) { newBBox.y = origBBox.y + d.dy; newBBox.h = Math.max(minH, origBBox.h - d.dy); }

            // Proportional rescale
            const scaleX = origBBox.w > 1 ? newBBox.w / origBBox.w : 1;
            const scaleY = origBBox.h > 1 ? newBBox.h / origBBox.h : 1;

            for (const a of assetSnaps) {
                const nx = r2(newBBox.x + (a.x - origBBox.x) * scaleX);
                const ny = r2(newBBox.y + (a.y - origBBox.y) * scaleY);
                a.el.dataset.x = nx; a.el.dataset.y = ny;
                applyAssetTranslate(a.el);
            }
            for (const gs of groupSnaps) {
                const gnx = r2(newBBox.x + (gs.x - origBBox.x) * scaleX);
                const gny = r2(newBBox.y + (gs.y - origBBox.y) * scaleY);
                const gnw = r2(gs.w * scaleX);
                const gnh = r2(gs.h * scaleY);
                updateGroupPos(gs.el, gnx, gny);
                updateGroupSize(gs.el, Math.max(4, gnw), Math.max(4, gnh));
                for (const [aid, sp] of gs.members) {
                    const el = inst.svg.querySelector(`.ap-asset-icon[data-asset-id="${aid}"]`);
                    if (!el) continue;
                    const ax = r2(newBBox.x + (sp.x - origBBox.x) * scaleX);
                    const ay = r2(newBBox.y + (sp.y - origBBox.y) * scaleY);
                    el.setAttribute('transform', `translate(${ax},${ay})`);
                    el.dataset.x = ax; el.dataset.y = ay;
                }
            }
        };

        const onMove = (ev) => {
            if (rafId) return;
            rafId = requestAnimationFrame(() => { rafId = 0; doResize(ev); });
        };

        const onUp = () => {
            if (rafId) { cancelAnimationFrame(rafId); rafId = 0; }
            document.removeEventListener('pointermove', onMove);
            document.removeEventListener('pointerup', onUp);

            // Report all moved items to C#
            const positions = [];
            const groups = [];
            for (const a of assetSnaps) {
                positions.push({ assetId: a.aid, x: r2(parseFloat(a.el.dataset.x)), y: r2(parseFloat(a.el.dataset.y)) });
            }
            for (const gs of groupSnaps) {
                groups.push({ groupId: gs.gid, x: r2(parseFloat(gs.el.dataset.x)), y: r2(parseFloat(gs.el.dataset.y)), w: r2(parseFloat(gs.el.dataset.w)), h: r2(parseFloat(gs.el.dataset.h)) });
                for (const [aid] of gs.members) {
                    const el = inst.svg.querySelector(`.ap-asset-icon[data-asset-id="${aid}"]`);
                    if (el) positions.push({ assetId: aid, x: r2(parseFloat(el.dataset.x)), y: r2(parseFloat(el.dataset.y)) });
                }
            }
            inst.dotNetRef.invokeMethodAsync('OnBulkMoved', positions, groups);

            // Rebuild bbox
            setTimeout(() => updateSelectionBBox(inst), 50);
        };

        document.addEventListener('pointermove', onMove);
        document.addEventListener('pointerup', onUp);
    }

    // ════════════════ Snap ════════════════

    function getIconSize(el) { return 32 * (parseFloat(el.dataset.scale) || 1.0); }

    /** dataset.x/y = 중심 좌표. translate에는 sz/2를 빼서 왼쪽 위로 렌더링 */
    function applyAssetTranslate(el) {
        const cx = parseFloat(el.dataset.x) || 0;
        const cy = parseFloat(el.dataset.y) || 0;
        const sz = getIconSize(el);
        el.setAttribute('transform', `translate(${r2(cx - sz / 2)},${r2(cy - sz / 2)})`);
    }

    function snapToGrid(v, gs) { return Math.round(v / gs) * gs; }

    function applySnap(inst, x, y, sz, excludeIds) {
        if (!inst.snap.enabled) return { x, y, guides: [] };
        const gs = inst.snap.gridSize, thr = inst.snap.neighborThreshold;
        let sx = snapToGrid(x, gs), sy = snapToGrid(y, gs);
        const guides = [];

        // Neighbor snap
        const neighbors = [];
        inst.svg.querySelectorAll('.ap-asset-icon').forEach(g => {
            const aid = parseInt(g.dataset.assetId);
            if (excludeIds instanceof Set ? excludeIds.has(aid) : excludeIds.has?.(aid)) return;
            const nx = parseFloat(g.dataset.x) || 0, ny = parseFloat(g.dataset.y) || 0;
            const nsz = getIconSize(g);
            // 중심 좌표를 바운딩박스로 변환
            neighbors.push({ x: nx - nsz / 2, y: ny - nsz / 2, w: nsz, h: nsz });
        });
        inst.svg.querySelectorAll('.ap-group-container').forEach(g => {
            neighbors.push({
                x: parseFloat(g.dataset.x) || 0, y: parseFloat(g.dataset.y) || 0,
                w: parseFloat(g.dataset.w) || 0, h: parseFloat(g.dataset.h) || 0
            });
        });

        let bestDx = thr + 1, bestDy = thr + 1;
        let snapX = null, snapY = null, guideX = null, guideY = null;
        // x,y = 중심 좌표, 바운딩박스는 중심에서 sz/2 떨어짐
        const half = sz / 2;
        const myEdges = { left: x - half, right: x + half, cx: x, top: y - half, bottom: y + half, cy: y };

        for (const n of neighbors) {
            for (const ne of [n.x, n.x + n.w, n.x + n.w / 2]) {
                for (const me of [myEdges.left, myEdges.right, myEdges.cx]) {
                    const diff = Math.abs(me - ne);
                    if (diff < bestDx) { bestDx = diff; snapX = x + (ne - me); guideX = ne; }
                }
            }
            for (const ne of [n.y, n.y + n.h, n.y + n.h / 2]) {
                for (const me of [myEdges.top, myEdges.bottom, myEdges.cy]) {
                    const diff = Math.abs(me - ne);
                    if (diff < bestDy) { bestDy = diff; snapY = y + (ne - me); guideY = ne; }
                }
            }
        }

        if (bestDx <= thr && snapX !== null) {
            sx = snapX;
            guides.push({ x1: guideX, y1: 0, x2: guideX, y2: ORIG_H });
        }
        if (bestDy <= thr && snapY !== null) {
            sy = snapY;
            guides.push({ x1: 0, y1: guideY, x2: ORIG_W, y2: guideY });
        }

        return { x: r2(sx), y: r2(sy), guides };
    }

    function renderGuideLines(inst, guides) {
        clearGuideLines(inst);
        for (const g of guides) {
            const line = document.createElementNS(SVG_NS, 'line');
            line.classList.add('ap-snap-guide');
            line.setAttribute('x1', g.x1); line.setAttribute('y1', g.y1);
            line.setAttribute('x2', g.x2); line.setAttribute('y2', g.y2);
            inst.svg.appendChild(line);
            inst._guideLines.push(line);
        }
    }

    function clearGuideLines(inst) {
        inst._guideLines.forEach(l => l.remove());
        inst._guideLines.length = 0;
    }

    // ════════════════ MutationObserver ════════════════

    function bindExistingElements(inst) {
        // No per-element binding needed — all handled via delegation in bindSvgPointer
    }

    function startObserver(inst) {
        inst._observer = new MutationObserver(() => {});
        inst._observer.observe(inst.svg, { childList: true, subtree: false });
    }

    // ════════════════ Keyboard ════════════════

    function bindKeyboard(inst) {
        let spaceHeld = false;
        let prevTool = null;

        on(inst, document, 'keydown', (e) => {
            // Ignore if focus is in an input/textarea
            const tag = e.target.tagName;
            if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || e.target.isContentEditable) return;

            const ctrl = e.ctrlKey || e.metaKey;

            // Space hold → temporary pan
            if (e.code === 'Space' && !spaceHeld && !ctrl) {
                spaceHeld = true;
                prevTool = inst.tool;
                inst.tool = 'pan';
                inst.svg.style.cursor = 'grab';
                e.preventDefault();
                return;
            }

            // Escape
            if (e.key === 'Escape') {
                inst.dotNetRef.invokeMethodAsync('OnKeyAction', 'escape');
                removeSelectionBBox(inst);
                e.preventDefault();
                return;
            }

            // Delete / Backspace
            if (e.key === 'Delete' || e.key === 'Backspace') {
                inst.dotNetRef.invokeMethodAsync('OnKeyAction', 'delete');
                removeSelectionBBox(inst);
                e.preventDefault();
                return;
            }

            // Ctrl+Z / Ctrl+Y
            if (ctrl && e.key === 'z') { inst.dotNetRef.invokeMethodAsync('OnKeyAction', 'undo'); e.preventDefault(); return; }
            if (ctrl && e.key === 'y') { inst.dotNetRef.invokeMethodAsync('OnKeyAction', 'redo'); e.preventDefault(); return; }

            // Ctrl+A
            if (ctrl && e.key === 'a') { inst.dotNetRef.invokeMethodAsync('OnKeyAction', 'selectAll'); e.preventDefault(); return; }

            // Ctrl+G
            if (ctrl && e.key === 'g') { inst.dotNetRef.invokeMethodAsync('OnKeyAction', 'group'); e.preventDefault(); return; }

            // Arrow keys
            if (['ArrowUp','ArrowDown','ArrowLeft','ArrowRight'].includes(e.key)) {
                const step = e.shiftKey ? (inst.snap.gridSize || 20) : 1;
                let dx = 0, dy = 0;
                if (e.key === 'ArrowLeft') dx = -step;
                if (e.key === 'ArrowRight') dx = step;
                if (e.key === 'ArrowUp') dy = -step;
                if (e.key === 'ArrowDown') dy = step;
                nudgeSelected(inst, dx, dy);
                e.preventDefault();
                return;
            }
        });

        on(inst, document, 'keyup', (e) => {
            if (e.code === 'Space' && spaceHeld) {
                spaceHeld = false;
                inst.tool = prevTool || 'select';
                inst.svg.style.cursor = '';
                e.preventDefault();
            }
        });
    }

    function nudgeSelected(inst, dx, dy) {
        const moved = [];
        const groups = [];
        inst.svg.querySelectorAll('.ap-asset-icon.ap-selected').forEach(g => {
            const nx = r2((parseFloat(g.dataset.x) || 0) + dx);
            const ny = r2((parseFloat(g.dataset.y) || 0) + dy);
            g.dataset.x = nx; g.dataset.y = ny;
            applyAssetTranslate(g);
            moved.push({ assetId: parseInt(g.dataset.assetId), x: nx, y: ny });
        });
        inst.svg.querySelectorAll('.ap-group-container.ap-group-selected').forEach(g => {
            const gid = parseInt(g.dataset.groupId);
            const nx = r2((parseFloat(g.dataset.x) || 0) + dx);
            const ny = r2((parseFloat(g.dataset.y) || 0) + dy);
            updateGroupPos(g, nx, ny);
            groups.push({ groupId: gid, x: nx, y: ny, w: r2(parseFloat(g.dataset.w)), h: r2(parseFloat(g.dataset.h)) });
            collectGroupMemberStarts(inst, g).forEach((sp, aid) => {
                const el = inst.svg.querySelector(`.ap-asset-icon[data-asset-id="${aid}"]`);
                if (!el) return;
                const ax = r2(sp.x + dx), ay = r2(sp.y + dy);
                el.dataset.x = ax; el.dataset.y = ay;
                applyAssetTranslate(el);
                moved.push({ assetId: aid, x: ax, y: ay });
            });
        });
        if (moved.length > 0 || groups.length > 0) {
            inst.dotNetRef.invokeMethodAsync('OnBulkMoved', moved, groups);
            setTimeout(() => updateSelectionBBox(inst), 50);
        }
    }

    // ════════════════ Grid Preview ════════════════

    function showGridPreview(containerId, cols, rows, cellW, cellH, names) {
        const inst = _inst[containerId];
        if (!inst) return;
        hideGridPreview(containerId);

        const center = getViewCenter(inst);
        const totalW = cols * cellW, totalH = rows * cellH;
        let ox = r2(center.x - totalW / 2), oy = r2(center.y - totalH / 2);

        const g = document.createElementNS(SVG_NS, 'g');
        g.classList.add('ap-grid-preview');

        // Background
        const bg = document.createElementNS(SVG_NS, 'rect');
        bg.setAttribute('x', ox); bg.setAttribute('y', oy);
        bg.setAttribute('width', totalW); bg.setAttribute('height', totalH);
        bg.setAttribute('rx', 6);
        bg.setAttribute('fill', 'rgba(76,175,80,0.08)');
        bg.setAttribute('stroke', '#4caf50');
        bg.setAttribute('stroke-width', 1.5);
        bg.setAttribute('stroke-dasharray', '6 3');
        g.appendChild(bg);

        // Cells
        const count = names.length;
        for (let i = 0; i < count; i++) {
            const col = i % cols, row = Math.floor(i / cols);
            const cx = ox + col * cellW, cy = oy + row * cellH;
            const cell = document.createElementNS(SVG_NS, 'rect');
            cell.setAttribute('x', cx + 2); cell.setAttribute('y', cy + 2);
            cell.setAttribute('width', cellW - 4); cell.setAttribute('height', cellH - 4);
            cell.setAttribute('rx', 3); cell.setAttribute('fill', 'rgba(255,255,255,0.6)');
            cell.setAttribute('stroke', '#ccc'); cell.setAttribute('stroke-width', 0.5);
            g.appendChild(cell);
            const label = document.createElementNS(SVG_NS, 'text');
            label.setAttribute('x', cx + cellW / 2); label.setAttribute('y', cy + cellH / 2 + 3);
            label.setAttribute('text-anchor', 'middle'); label.setAttribute('font-size', 7);
            label.setAttribute('fill', '#666');
            label.textContent = names[i] || '';
            g.appendChild(label);
        }

        inst.svg.appendChild(g);
        inst._gridPreview = { g, ox, oy, totalW, totalH };

        // Drag the preview
        let dragStart = null, dragOx, dragOy;
        const onDown = (e) => {
            if (e.button !== 0) return;
            e.preventDefault(); e.stopPropagation();
            dragStart = clientToSvg(inst, e.clientX, e.clientY);
            dragOx = inst._gridPreview.ox; dragOy = inst._gridPreview.oy;
            document.addEventListener('pointermove', onDragMove);
            document.addEventListener('pointerup', onDragUp);
        };
        const onDragMove = (e) => {
            if (!dragStart) return;
            const cur = clientToSvg(inst, e.clientX, e.clientY);
            let nx = r2(dragOx + cur.x - dragStart.x);
            let ny = r2(dragOy + cur.y - dragStart.y);
            if (inst.snap.enabled) { nx = snapToGrid(nx, inst.snap.gridSize); ny = snapToGrid(ny, inst.snap.gridSize); }
            inst._gridPreview.ox = nx; inst._gridPreview.oy = ny;
            repositionGridPreview(inst);
        };
        const onDragUp = () => {
            dragStart = null;
            document.removeEventListener('pointermove', onDragMove);
            document.removeEventListener('pointerup', onDragUp);
        };
        g.addEventListener('pointerdown', onDown);

        // Double-click or Enter to confirm
        const onDblClick = (e) => {
            e.preventDefault(); e.stopPropagation();
            confirmGridPreview(inst, containerId);
        };
        g.addEventListener('dblclick', onDblClick);

        inst._gridKeyHandler = (e) => {
            if (e.key === 'Enter') confirmGridPreview(inst, containerId);
            else if (e.key === 'Escape') hideGridPreview(containerId);
        };
        document.addEventListener('keydown', inst._gridKeyHandler);
    }

    function repositionGridPreview(inst) {
        if (!inst._gridPreview) return;
        const { g, ox, oy, totalW, totalH } = inst._gridPreview;
        const bg = g.querySelector('rect');
        if (bg) { bg.setAttribute('x', ox); bg.setAttribute('y', oy); }

        const rects = g.querySelectorAll('rect');
        const texts = g.querySelectorAll('text');
        // Recompute from ox/oy — count cells from child elements
        const cellCount = texts.length;
        if (cellCount === 0) return;
        const cols = Math.round(totalW / ((rects.length > 1 ? parseFloat(rects[1].getAttribute('width')) + 4 : totalW)));
        // Simpler: just reposition all children relative to ox/oy
        let idx = 0;
        for (let i = 1; i < rects.length; i++) { // skip background rect
            const cellW = parseFloat(rects[i].getAttribute('width')) + 4;
            const cellH = parseFloat(rects[i].getAttribute('height')) + 4;
            const realCols = Math.round(totalW / cellW);
            const col = (i - 1) % realCols, row = Math.floor((i - 1) / realCols);
            rects[i].setAttribute('x', ox + col * cellW + 2);
            rects[i].setAttribute('y', oy + row * cellH + 2);
            if (texts[i - 1]) {
                texts[i - 1].setAttribute('x', ox + col * cellW + cellW / 2);
                texts[i - 1].setAttribute('y', oy + row * cellH + cellH / 2 + 3);
            }
        }
    }

    function confirmGridPreview(inst, containerId) {
        if (!inst._gridPreview) return;
        const { ox, oy } = inst._gridPreview;
        inst.dotNetRef.invokeMethodAsync('OnGridPlaceConfirmed', r2(ox), r2(oy));
        hideGridPreview(containerId);
    }

    function hideGridPreview(containerId) {
        const inst = _inst[containerId];
        if (!inst) return;
        if (inst._gridPreview) {
            inst._gridPreview.g.remove();
            inst._gridPreview = null;
        }
        if (inst._gridKeyHandler) {
            document.removeEventListener('keydown', inst._gridKeyHandler);
            inst._gridKeyHandler = null;
        }
    }

    // ════════════════ Public API ════════════════

    function setTool(containerId, tool) {
        const inst = _inst[containerId];
        if (inst) {
            inst.tool = tool;
            inst.svg.style.cursor = tool === 'pan' ? 'grab' : '';
        }
    }

    function setSnapConfig(containerId, enabled, gridSize) {
        const inst = _inst[containerId];
        if (inst) {
            inst.snap.enabled = !!enabled;
            if (gridSize > 0) inst.snap.gridSize = gridSize;
        }
    }

    function zoomIn(containerId) {
        const inst = _inst[containerId];
        if (!inst) return;
        const r = inst.svg.getBoundingClientRect();
        zoomByFactor(inst, 1.2, r.left + r.width / 2, r.top + r.height / 2);
    }

    function zoomOut(containerId) {
        const inst = _inst[containerId];
        if (!inst) return;
        const r = inst.svg.getBoundingClientRect();
        zoomByFactor(inst, 1 / 1.2, r.left + r.width / 2, r.top + r.height / 2);
    }

    function resetZoom(containerId) {
        const inst = _inst[containerId];
        if (!inst) return;
        inst.viewBox = { x: 0, y: 0, w: ORIG_W, h: ORIG_H };
        inst.zoom = 1;
        applyViewBox(inst);
    }

    function fitAll(containerId) {
        const inst = _inst[containerId];
        if (!inst) return;
        // Compute bounding box of all elements
        let minX = ORIG_W, minY = ORIG_H, maxX = 0, maxY = 0;
        inst.svg.querySelectorAll('.ap-asset-icon').forEach(g => {
            const x = parseFloat(g.dataset.x) || 0, y = parseFloat(g.dataset.y) || 0;
            const sz = getIconSize(g);
            minX = Math.min(minX, x); minY = Math.min(minY, y);
            maxX = Math.max(maxX, x + sz); maxY = Math.max(maxY, y + sz);
        });
        inst.svg.querySelectorAll('.ap-group-container').forEach(g => {
            const x = parseFloat(g.dataset.x) || 0, y = parseFloat(g.dataset.y) || 0;
            const w = parseFloat(g.dataset.w) || 0, h = parseFloat(g.dataset.h) || 0;
            minX = Math.min(minX, x); minY = Math.min(minY, y);
            maxX = Math.max(maxX, x + w); maxY = Math.max(maxY, y + h);
        });
        if (maxX <= minX || maxY <= minY) { resetZoom(containerId); return; }
        const pad = 40;
        inst.viewBox = { x: minX - pad, y: minY - pad, w: maxX - minX + pad * 2, h: maxY - minY + pad * 2 };
        inst.zoom = ORIG_W / inst.viewBox.w;
        applyViewBox(inst);
    }

    function getZoomLevel(containerId) {
        const inst = _inst[containerId];
        return inst ? Math.round(inst.zoom * 100) : 100;
    }

    function scrollToAsset(containerId, assetId) {
        const inst = _inst[containerId];
        if (!inst) return;
        const el = inst.svg.querySelector(`.ap-asset-icon[data-asset-id="${assetId}"]`);
        if (!el) return;
        const x = parseFloat(el.dataset.x) || 0, y = parseFloat(el.dataset.y) || 0;
        const sz = getIconSize(el);
        // Center view on asset
        const zoom = Math.max(inst.zoom, 1.5);
        const vbW = ORIG_W / zoom, vbH = ORIG_H / zoom;
        inst.viewBox = { x: x + sz / 2 - vbW / 2, y: y + sz / 2 - vbH / 2, w: vbW, h: vbH };
        inst.zoom = zoom;
        applyViewBox(inst);
        // Flash highlight
        el.classList.add('ap-flash');
        setTimeout(() => el.classList.remove('ap-flash'), 1200);
    }

    function scrollListGroupIntoView(groupId) {
        const el = document.querySelector(`[data-list-group-id="${groupId}"]`);
        if (!el) return;
        el.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    }

    return {
        init, dispose,
        setTool, setSnapConfig,
        zoomIn, zoomOut, resetZoom, fitAll, getZoomLevel,
        scrollToAsset,
        scrollListGroupIntoView,
    };
})();
