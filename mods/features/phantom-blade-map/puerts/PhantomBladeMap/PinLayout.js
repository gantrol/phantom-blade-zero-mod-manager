"use strict";

const TWO_PI = Math.PI * 2;

function hashAngle(s) {
    let h = 2166136261;
    const str = String(s || "");
    for (let i = 0; i < str.length; i++) h = Math.imul(h ^ str.charCodeAt(i), 16777619);
    return ((h >>> 0) % 100000) / 100000 * TWO_PI;
}

function spreadAnglesKeepOrder(targetAngles, minStep) {
    const n = targetAngles.length;
    if (n <= 1) return targetAngles.slice();
    if (n * minStep >= TWO_PI) {
        const step = TWO_PI / n, start = targetAngles[0];
        return Array.from({ length: n }, (_, i) => start + i * step);
    }
    const out = targetAngles.slice();
    for (let i = 1; i < n; i++) {
        if (out[i] < out[i - 1] + minStep) out[i] = out[i - 1] + minStep;
    }
    let targetSum = 0, outSum = 0;
    for (let i = 0; i < n; i++) { targetSum += targetAngles[i]; outSum += out[i]; }
    const shift = (targetSum - outSum) / n;
    for (let i = 0; i < n; i++) out[i] += shift;
    return out;
}

function pinMass(point, activeID, trackedID) {
    if (!point) return 1;
    if (point.id === trackedID) return 9;
    if (point.id === activeID) return 8;
    if (point.category === "bell") return 7;
    if (point.category === "quest") return 6;
    if (point.category === "marker") return 5;
    if (point.facility || point.pointKind === "facility") return 2;
    if (point.state === "collected") return 1;
    return 4;
}

function collectNativeAnchors(page, UE) {
    const anchors = [];
    if (!page?.focusItemMap) return anchors;
    try {
        for (const [key, data] of page.focusItemMap) {
            if (!data?.widget || data.itemType === 4) continue;
            if (!UE.KismetSystemLibrary.IsValid(data.widget) || !data.widget.IsVisible()) continue;
            const slot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(data.widget);
            if (!slot) continue;
            const pos = slot.GetPosition(), size = slot.GetSize();
            if (!pos) continue;
            const halfW = Math.max(15, (size?.X || 36) * 0.45);
            const halfH = Math.max(15, (size?.Y || 36) * 0.45);
            anchors.push({
                key: "native:" + String(key).toLowerCase(),
                nativeKey: String(key).toLowerCase(),
                cx: pos.X, cy: pos.Y, rawX: pos.X, rawY: pos.Y,
                halfW, halfH, locked: true, mass: 100, itemType: data.itemType
            });
        }
    } catch {}
    return anchors;
}

function resolveNativeMapOverlaps(modItems, nativeAnchors, zoomScale = 1) {
    const result = new Map();
    if (!modItems.length) return result;
    const half = Math.max(11, 14.5 * zoomScale), margin = 4 * zoomScale;
    const layouts = nativeAnchors.map(a => ({ ...a }));
    for (const item of modItems) {
        layouts.push({
            key: item.id, id: item.id,
            cx: item.rawX, cy: item.rawY, rawX: item.rawX, rawY: item.rawY,
            halfW: half, halfH: half, locked: false, mass: item.mass
        });
    }
    const n = layouts.length;
    if (n < 2) {
        for (const item of modItems) result.set(item.id, { x: item.rawX, y: item.rawY, px: item.rawX, py: item.rawY });
        return result;
    }

    const parent = Array.from({ length: n }, (_, i) => i);
    const find = x => { while (parent[x] !== x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; };
    const cell = 64, grid = new Map();
    for (let i = 0; i < n; i++) {
        const gx = Math.floor(layouts[i].cx / cell), gy = Math.floor(layouts[i].cy / cell);
        for (let dx = -1; dx <= 1; dx++) {
            for (let dy = -1; dy <= 1; dy++) {
                const bucket = grid.get((gx + dx) + ":" + (gy + dy));
                if (!bucket) continue;
                for (const j of bucket) {
                    const a = layouts[i], b = layouts[j];
                    const ox = (a.halfW + b.halfW + margin) - Math.abs(b.cx - a.cx);
                    const oy = (a.halfH + b.halfH + margin) - Math.abs(b.cy - a.cy);
                    if (ox > 0 && oy > 0) {
                        const ri = find(i), rj = find(j);
                        if (ri !== rj) parent[ri] = rj;
                    }
                }
            }
        }
        const key = gx + ":" + gy;
        let list = grid.get(key);
        if (!list) { list = []; grid.set(key, list); }
        list.push(i);
    }

    const clusters = new Map();
    for (let i = 0; i < n; i++) {
        const r = find(i);
        let list = clusters.get(r);
        if (!list) { list = []; clusters.set(r, list); }
        list.push(i);
    }

    for (const members of clusters.values()) {
        if (members.length < 2) continue;
        let anchorIdx = -1;
        for (const idx of members) {
            if (layouts[idx].locked) { anchorIdx = idx; break; }
        }
        if (anchorIdx < 0) {
            anchorIdx = members[0];
            for (const idx of members) {
                const c = layouts[idx], cur = layouts[anchorIdx];
                if (c.mass > cur.mass || (c.mass === cur.mass && c.key < cur.key)) anchorIdx = idx;
            }
        }
        const anchor = layouts[anchorIdx];
        const scatter = members.filter(idx => idx !== anchorIdx && !layouts[idx].locked);
        if (!scatter.length) continue;

        const spacing = 2 * half + margin;
        const firstRadius = Math.max(anchor.halfW, anchor.halfH) + half + margin;
        let maxPen = 0;
        for (let i = 0; i < members.length; i++) {
            for (let j = i + 1; j < members.length; j++) {
                const a = layouts[members[i]], b = layouts[members[j]];
                if (a.locked && b.locked) continue;
                const ox = (a.halfW + b.halfW + margin) - Math.abs(b.cx - a.cx);
                const oy = (a.halfH + b.halfH + margin) - Math.abs(b.cy - a.cy);
                if (ox > 0 && oy > 0) maxPen = Math.max(maxPen, Math.min(ox, oy));
            }
        }
        const norm = spacing > 0 ? Math.min(maxPen / spacing, 1) : 0;
        const t = norm * norm * (3 - 2 * norm);
        if (t <= 0.001) continue;

        const memberAngles = scatter.map(idx => {
            const m = layouts[idx], dx = m.cx - anchor.cx, dy = m.cy - anchor.cy;
            const angle = Math.abs(dx) < 0.5 && Math.abs(dy) < 0.5 ? hashAngle(m.key) : Math.atan2(dy, dx);
            return { idx, angle };
        }).sort((a, b) => a.angle - b.angle || (layouts[a.idx].key < layouts[b.idx].key ? -1 : 1));

        let placed = 0, ring = 0;
        while (placed < memberAngles.length) {
            const radius = firstRadius + ring * spacing;
            const minStep = Math.min(spacing / radius, Math.PI);
            const cap = Math.max(1, Math.floor(TWO_PI / minStep));
            const slice = memberAngles.slice(placed, placed + cap);
            const angles = spreadAnglesKeepOrder(slice.map(m => m.angle), minStep);
            for (let k = 0; k < slice.length; k++) {
                const m = layouts[slice[k].idx], ang = angles[k];
                const rx = anchor.cx + radius * Math.cos(ang), ry = anchor.cy + radius * Math.sin(ang);
                m.cx = m.rawX + (rx - m.rawX) * t;
                m.cy = m.rawY + (ry - m.rawY) * t;
            }
            placed += slice.length;
            ring++;
        }

        // Secondary clearance against any other locked native icon in the same cluster
        const lockedOthers = members.filter(idx => idx !== anchorIdx && layouts[idx].locked);
        if (lockedOthers.length) {
            for (const idx of scatter) {
                const m = layouts[idx];
                for (const li of lockedOthers) {
                    const lk = layouts[li], minSep = Math.max(lk.halfW, lk.halfH) + half + margin;
                    const dx = m.cx - lk.cx, dy = m.cy - lk.cy, d = Math.hypot(dx, dy);
                    if (d < minSep) {
                        const ang = d < 0.5 ? hashAngle(m.key) : Math.atan2(dy, dx);
                        m.cx = lk.cx + Math.cos(ang) * minSep;
                        m.cy = lk.cy + Math.sin(ang) * minSep;
                    }
                }
            }
        }
    }

    for (const l of layouts) {
        if (!l.locked && l.id) result.set(l.id, { x: l.rawX, y: l.rawY, px: l.cx, py: l.cy });
    }
    return result;
}

function arbitrateCanvasPin(position, pinByID, pinPositions, page, UE, prevID = "", zoomScale = 1, activeID = "") {
    if (!position || !pinByID?.size) return { node: undefined, peers: [] };
    const enterR = Math.max(15, 21 * zoomScale);
    const exitR = Math.max(20, 28 * zoomScale);
    const hits = [];

    for (const node of pinByID.values()) {
        if (!node.shown || !node.pointID) continue;
        const p = pinPositions.get(node.pointID);
        if (!p) continue;
        const d = Math.hypot(position.X - p.px, position.Y - p.py);
        const radius = node.pointID === prevID ? exitR : enterR;
        if (d > radius) continue;
        const bias = (node.pointID === prevID ? -2.5 : 0) + (node.pointID === activeID ? -1.5 : 0);
        hits.push({ node, p, d, score: d + bias });
    }
    if (!hits.length) return { node: undefined, peers: [] };

    hits.sort((a, b) => {
        if (Math.abs(a.score - b.score) > 3.5) return a.score - b.score;
        if (a.node.zOrder !== b.node.zOrder) return b.node.zOrder - a.node.zOrder;
        return a.d - b.d;
    });
    const best = hits[0];

    // Yield to a native Bell / Quest / Lock / Portal if its center is closer to the cursor
    if (page?.focusItemMap) {
        const nativeR = Math.max(20, 28 * zoomScale);
        for (const [, data] of page.focusItemMap) {
            if (!data?.widget || data.itemType === 4) continue;
            if (!UE.KismetSystemLibrary.IsValid(data.widget) || !data.widget.IsVisible()) continue;
            const slot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(data.widget);
            const c = slot?.GetPosition();
            if (!c) continue;
            const nd = Math.hypot(position.X - c.X, position.Y - c.Y);
            if (nd <= nativeR && nd + 2 < best.d) return { node: undefined, peers: [] };
        }
    }

    // Gather residual overlapping peers around the winning pin so stacked points can be cycled
    const peerR = Math.max(18, 25 * zoomScale);
    const peers = [];
    for (const node of pinByID.values()) {
        if (!node.shown || !node.point) continue;
        const p = pinPositions.get(node.pointID);
        if (!p) continue;
        if (Math.hypot(p.px - best.p.px, p.py - best.p.py) <= peerR) peers.push(node);
    }
    peers.sort((a, b) => (b.baseZOrder || 0) - (a.baseZOrder || 0) || (a.pointID < b.pointID ? -1 : 1));
    return { node: best.node, peers };
}

module.exports = { pinMass, collectNativeAnchors, resolveNativeMapOverlaps, arbitrateCanvasPin };
