"use strict";

const TWO_PI = Math.PI * 2;

function hashAngle(s) {
    let h = 2166136261;
    const str = String(s || "");
    for (let i = 0; i < str.length; i++) h = Math.imul(h ^ str.charCodeAt(i), 16777619);
    return ((h >>> 0) % 100000) / 100000 * TWO_PI;
}

// Every eligible point contributes to a pin or a count. Bells, tracked targets,
// and the selected destination stay directly actionable; dense groups expand on click.
function groupPins(candidates, camera, width, height, targetID, expanded = new Set(), budget = 140, trackedID = "") {
    const cell = Math.max(44, Math.sqrt(width * height / budget)) * camera.cm;
    const groups = new Map(), pins = [];
    for (const candidate of candidates) {
        const point = candidate.point;
        if (
            point.nativeLock ||
            point.category === "bell" ||
            point.id === targetID ||
            point.id === trackedID ||
            expanded.has(point.id)
        ) {
            pins.push(candidate);
            continue;
        }
        const key = point.mapID + ":" + point.layer + ":" + (point.questKey || "") + ":" + Math.floor(point.x / cell) + ":" + Math.floor(point.y / cell);
        if (!groups.has(key)) groups.set(key, []);
        groups.get(key).push(candidate);
    }
    for (const [key, group] of groups) {
        if (group.length === 1) {
            pins.push(group[0]);
            continue;
        }
        const members = group.map(c => c.point), representative = group[0];
        const point = { ...representative.point, id: "cluster:" + key, pointKind: "cluster", members };
        pins.push({ point, screen: representative.screen, priority: representative.priority ?? 2 });
    }
    pins.sort((a, b) => (a.priority ?? 2) - (b.priority ?? 2) || (a.point.id < b.point.id ? -1 : 1));

    const placed = [];
    const margin = 18;
    for (const item of pins) {
        const rawX = item.screen.x, rawY = item.screen.y;
        let anchor;
        for (const p of placed) {
            if (Math.hypot(p.rawX - rawX, p.rawY - rawY) < 26) {
                anchor = p;
                break;
            }
        }
        if (!anchor) {
            placed.push({ rawX, rawY, px: rawX, py: rawY, count: 0 });
            item.screen = { x: rawX, y: rawY, rawX, rawY };
        } else {
            const slot = ++anchor.count;
            const dx = rawX - anchor.rawX, dy = rawY - anchor.rawY;
            const base = Math.abs(dx) < 0.5 && Math.abs(dy) < 0.5 ? hashAngle(item.point.id) : Math.atan2(dy, dx);
            const angle = base + ((slot - 1) % 6) * 1.04719755;
            const radius = 26 + Math.floor((slot - 1) / 6) * 20;
            const px = Math.max(margin, Math.min(width - margin, anchor.rawX + Math.cos(angle) * radius));
            const py = Math.max(margin, Math.min(height - margin, anchor.rawY + Math.sin(angle) * radius));
            placed.push({ rawX, rawY, px, py, count: 0 });
            item.screen = { x: px, y: py, rawX, rawY };
        }
    }
    return pins;
}

module.exports = { groupPins };
