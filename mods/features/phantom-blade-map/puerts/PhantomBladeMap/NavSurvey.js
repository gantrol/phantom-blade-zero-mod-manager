"use strict";
const SurveyStore = require("./SurveyStore");
const Diagnostics = require("./Diagnostics");
const spacing = SurveyStore.spacing, radius = 6000, limit = 3072;
const directions = [[1, 0], [-1, 0], [0, 1], [0, -1]];
function open(page, UE, puerts, region, mapID, player, profile, isExplored, canSample) {
    const store = SurveyStore.open(UE, puerts, profile, region, player), queue = [], seen = new Set();
    let cursor = 0, attempts = 0, calls = 0, maxStepMs = 0, disposed = false, state = canSample && player ? "surveying" : "surveyRemote";
    let reportedGeneration = -1, lastFlush = 0, attemptLimit = limit, corridor = [];
    function enqueue(point, from, direction) {
        const gx = Math.round(point.x / spacing), gy = Math.round(point.y / spacing);
        if (Math.hypot(gx * spacing - player.x, gy * spacing - player.y) > radius &&
            !corridor.some(p => Math.hypot(gx * spacing - p.x, gy * spacing - p.y) <= 1200)) return;
        // Unknown coverage and the query budget never imply a blocked edge.
        if (!isExplored({ x: gx * spacing, y: gy * spacing, z: point.z, mapID })) return;
        const key = gx + ":" + gy + ":" + Math.round(point.z / 180) + ":" + (direction ?? "seed");
        if (seen.has(key)) return;
        seen.add(key); queue.push({ gx, gy, z: point.z, from, direction });
    }
    if (state === "surveying") enqueue(player);
    function finish(reason) {
        state = store.count ? "surveyReady" : "surveyUnavailable";
        queue.length = 0; cursor = 0;
        Diagnostics.record("survey-complete", { region, samples: store.count, calls, attempts, maxStepMs, reason, format: "spatial-tiles-v2" });
    }
    return {
        get state() { return store.storageError ? "surveyStorageUnavailable" : store.loading ? "surveyLoading" : state; },
        get count() { return store.count; },
        resources() { return { queuedQueries: Math.max(0, queue.length - cursor), calls, attempts }; },
        values() { return store.values(); },
        follow(points) {
            if (!canSample || !points.length || disposed) return;
            corridor = points.filter((p, i) => i % 5 === 0 || i === points.length - 1).map(p => ({ ...p }));
            const before = queue.length;
            for (const point of corridor) enqueue(point);
            if (queue.length > before) { attemptLimit = attempts + limit; state = "surveying"; }
        },
        step(allowSampling = true) {
            if (disposed) return false;
            const now = Date.now(), flush = state !== "surveying" || now - lastFlush >= 750;
            const loaded = store.stepIO(flush), started = Date.now();
            if (flush) lastFlush = now;
            if (state === "surveying" && allowSampling && !store.loading) try {
                for (let n = 0, queries = 0; cursor < queue.length && n < 48 && queries < 4 && Date.now() - started < 2 && attempts < attemptLimit; n++) {
                    const query = queue[cursor++];
                    const x = query.gx * spacing, y = query.gy * spacing;
                    const cached = store.find(mapID, query.gx, query.gy, query.z);
                    if (cached?.px != null && query.from && query.from.cell.links & (1 << query.direction)) {
                        const from = { x: cached.px, y: cached.py, z: cached.z, cell: cached };
                        for (let d = 0; d < directions.length; d++) if (!(cached.closed & (1 << d))) {
                            const [dx, dy] = directions[d]; enqueue({ x: x + dx * spacing, y: y + dy * spacing, z: cached.z }, from, d);
                        }
                        continue;
                    }
                    const out = puerts.$ref(); calls++; queries++; attempts++;
                    const found = UE.NavigationSystemV1.K2_ProjectPointToNavigation(page, new UE.Vector(x, y, query.z), out, undefined, undefined, new UE.Vector(45, 45, 260));
                    const p = found ? puerts.$unref(out) : undefined;
                    let blocked = !p || Math.abs(p.X - x) > 45 || Math.abs(p.Y - y) > 45;
                    if (!blocked && query.from) {
                        const hit = puerts.$ref(); calls++;
                        blocked = UE.NavigationSystemV1.NavigationRaycast(page, new UE.Vector(query.from.x, query.from.y, query.from.z), new UE.Vector(p.X, p.Y, p.Z), hit, undefined, undefined);
                    }
                    if (query.from) store.edge(query.from.cell, query.direction, blocked);
                    if (blocked) continue;
                    // Store the queried grid cell, not the grid nearest to a snapped point.
                    const cell = store.put(mapID, query.gx, query.gy, Math.round(p.Z), -1, 0, 0, true, p.X, p.Y);
                    const from = { x: p.X, y: p.Y, z: p.Z, cell };
                    if (query.from) store.edge(cell, query.direction ^ 1, false);
                    for (let d = 0; d < directions.length; d++) {
                        const [dx, dy] = directions[d]; enqueue({ x: x + dx * spacing, y: y + dy * spacing, z: p.Z }, from, d);
                    }
                }
                maxStepMs = Math.max(maxStepMs, Date.now() - started);
                if (cursor >= queue.length || attempts >= attemptLimit) finish(attempts >= attemptLimit ? "page-budget" : "frontier-exhausted");
            } catch (error) {
                state = "surveyUnavailable"; queue.length = 0;
                Diagnostics.record("survey-error", { region, error: String(error) });
            }
            if (loaded || store.generation !== reportedGeneration && (state !== "surveying" || attempts % 32 === 0)) {
                reportedGeneration = store.generation; return true;
            }
            return false;
        },
        dispose() { if (disposed) return; disposed = true; queue.length = 0; corridor = []; seen.clear(); store.dispose(); page = undefined; }
    };
}
function outline(cells) {
    const lines = [], half = spacing / 2;
    for (const p of cells) for (let d = 0; d < directions.length; d++) {
        if (!(p.closed & (1 << d))) continue;
        const [dx, dy] = directions[d], x = p.gx * spacing, y = p.gy * spacing;
        lines.push(dx ? [p.layer, x + dx * half, y - half, p.z, x + dx * half, y + half, p.z, 0, p.mapID]
            : [p.layer, x - half, y + dy * half, p.z, x + half, y + dy * half, p.z, 0, p.mapID]);
    }
    const groups = new Map();
    for (const s of lines) {
        const vertical = s[1] === s[4], fixed = vertical ? s[1] : s[2], start = vertical ? s[2] : s[1], end = vertical ? s[5] : s[4];
        const key = [s[8], s[0], vertical, fixed, s[3]].join(":");
        if (!groups.has(key)) groups.set(key, { s, vertical, fixed, intervals: [] });
        groups.get(key).intervals.push([Math.min(start, end), Math.max(start, end)]);
    }
    const merged = [];
    for (const { s, vertical, fixed, intervals } of groups.values()) {
        intervals.sort((a, b) => a[0] - b[0]); let [start, end] = intervals[0];
        const push = () => merged.push(vertical ? [s[0], fixed, start, s[3], fixed, end, s[6], s[7], s[8]] : [s[0], start, fixed, s[3], end, fixed, s[6], s[7], s[8]]);
        for (const interval of intervals.slice(1)) { if (interval[0] > end) { push(); start = interval[0]; } end = Math.max(end, interval[1]); } push();
    }
    return merged;
}
module.exports = { open, outline };
